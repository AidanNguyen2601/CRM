using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Cấu hình hộp thư hỗ trợ (AppSettings "inbound")</summary>
public class InboundSettings
{
    public const string Key = "inbound";
    /// <summary>Đọc hộp thư POP3 định kỳ</summary>
    public bool Pop3Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 995;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? PasswordProtected { get; set; }
    /// <summary>Xóa email khỏi hộp thư sau khi đã tạo phiếu</summary>
    public bool DeleteAfterRead { get; set; }
    /// <summary>Mã bí mật của địa chỉ webhook nhận email (dịch vụ chuyển tiếp email: SendGrid, Mailgun, Postmark...)</summary>
    public string WebhookToken { get; set; } = SecurityText.NewToken()[..24];
    /// <summary>Tự gửi email xác nhận đã tiếp nhận kèm số phiếu</summary>
    public bool AutoReply { get; set; } = true;
    public TicketPriority DefaultPriority { get; set; } = TicketPriority.Normal;
    public DateTime? LastCheckAt { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>Một email đã đọc</summary>
public record InboundMail(string Key, string FromEmail, string? FromName, string Subject, string Body);

/// <summary>Tạo phiếu hỗ trợ / ghép trả lời vào phiếu từ email gửi tới hộp thư hỗ trợ</summary>
public class InboundService
{
    private static readonly Regex TicketNo = new(@"\[?(YC-\d{4}-\d{3,})\]?", RegexOptions.IgnoreCase);
    /// <summary>Mã khách hàng tạm cho email từ người gửi chưa xác định</summary>
    public const string UnknownCode = "KH-CHUAXD";

    private static readonly string[] FreeDomains = { "gmail.com", "yahoo.com", "outlook.com", "hotmail.com", "icloud.com", "live.com", "yahoo.com.vn" };

    private readonly CrmDbContext _db;
    private readonly NotificationService _notify;
    private readonly EmailService _mail;
    private readonly SettingsStore _settings;

    public InboundService(CrmDbContext db, NotificationService notify, EmailService mail, SettingsStore settings)
    {
        _db = db; _notify = notify; _mail = mail; _settings = settings;
    }

    public Task<InboundSettings> SettingsAsync() => _settings.GetAsync<InboundSettings>(InboundSettings.Key);

    /// <summary>Xử lý một email: trả về "created" / "replied" / "duplicate" / "ignored"</summary>
    public async Task<(string Result, Ticket? Ticket)> ProcessAsync(InboundMail m)
    {
        var key = Cut(m.Key, 300);
        if (await _db.InboundMessages.AnyAsync(x => x.MessageKey == key)) return ("duplicate", null);
        var from = m.FromEmail.Trim().ToLowerInvariant();
        var log = new InboundMessage { MessageKey = key, From = Cut(from, 200), Subject = Cut(m.Subject, 300), ReceivedAt = DateTime.Now };
        _db.InboundMessages.Add(log);
        var settings = await SettingsAsync();
        var smtp = await _mail.SettingsAsync();
        // Bỏ qua email do chính hệ thống gửi (tránh vòng lặp tự trả lời)
        if (string.IsNullOrEmpty(from) || string.Equals(from, smtp.FromEmail, StringComparison.OrdinalIgnoreCase))
        {
            log.Result = "ignored"; await _db.SaveChangesAsync(); return ("ignored", null);
        }
        var body = CleanBody(m.Body);
        var contact = await _db.Contacts.IgnoreQueryFilters().Where(c => c.Email == from).OrderByDescending(c => c.IsPrimary).FirstOrDefaultAsync();
        var author = !string.IsNullOrWhiteSpace(m.FromName) ? m.FromName!.Trim() : contact?.FullName ?? from;

        // Trả lời phiếu đã có (tiêu đề chứa số phiếu)
        var mt = TicketNo.Match(m.Subject ?? "");
        if (mt.Success)
        {
            var no = mt.Groups[1].Value.ToUpperInvariant();
            var t = await _db.Tickets.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Number == no);
            if (t != null)
            {
                t.Comments.Add(new TicketComment { Body = Cut(body.Length == 0 ? m.Subject ?? "" : body, 4000), FromCustomer = true, AuthorName = Cut($"{author} <{from}>", 150), CreatedAt = DateTime.Now });
                if (t.Status is TicketStatus.WaitingCustomer or TicketStatus.Resolved)
                {
                    t.Comments.Add(new TicketComment { Body = I18n.T("Khách hàng phản hồi qua email – chuyển {0} → {1}.", t.Status.GetDisplayName(), TicketStatus.InProgress.GetDisplayName()), IsSystem = true, IsInternal = true, CreatedAt = DateTime.Now });
                    t.Status = TicketStatus.InProgress; t.ResolvedAt = null;
                }
                t.UpdatedAt = DateTime.Now;
                log.TicketId = t.Id; log.Result = "replied";
                _notify.Add(t.OwnerId, "ticket", I18n.T("Khách hàng trả lời phiếu {0}: {1}", t.Number, t.Subject), $"/Tickets/Details/{t.Id}", null, Cut(body, 200));
                await _db.SaveChangesAsync();
                return ("replied", t);
            }
        }

        // Phiếu mới: khách hàng theo người liên hệ → email khách hàng → tên miền email công ty
        int? customerId = contact?.CustomerId;
        if (customerId == null)
            customerId = await _db.Customers.IgnoreQueryFilters().Where(c => c.Email == from).Select(c => (int?)c.Id).FirstOrDefaultAsync();
        var domain = from.Contains('@') ? from[(from.IndexOf('@') + 1)..] : "";
        if (customerId == null && domain.Length > 3 && !FreeDomains.Contains(domain))
            customerId = await _db.Customers.IgnoreQueryFilters()
                .Where(c => (c.Email != null && c.Email.EndsWith("@" + domain)) || (c.Website != null && c.Website.Contains(domain)))
                .Select(c => (int?)c.Id).FirstOrDefaultAsync();
        customerId ??= await UnknownCustomerAsync();
        var ticket = new Ticket
        {
            Number = await NextNumberAsync(), Subject = Cut(string.IsNullOrWhiteSpace(m.Subject) ? I18n.T("(Không có tiêu đề)") : m.Subject.Trim(), 200),
            Description = Cut(body, 4000), CustomerId = customerId, ContactId = contact?.CustomerId == customerId ? contact?.Id : null,
            ReporterEmail = contact == null ? Cut(from, 150) : null, Channel = TicketChannel.Email, Priority = settings.DefaultPriority,
            Status = TicketStatus.New, CreatedAt = DateTime.Now
        };
        ticket.ApplySla();
        ticket.Comments.Add(new TicketComment { Body = I18n.T("Tiếp nhận yêu cầu qua email từ {0}.", $"{author} <{from}>") + (customerId == null ? " " + I18n.T("Chưa xác định được khách hàng – vui lòng gán khách hàng cho phiếu.") : ""), IsSystem = true, IsInternal = true, CreatedAt = DateTime.Now });
        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync();
        log.TicketId = ticket.Id; log.Result = "created";
        // Báo bộ phận CSKH
        foreach (var uid in await _db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Support || u.Role == AppRoles.Manager)).Select(u => u.Id).ToListAsync())
            _notify.Add(uid, "ticket", I18n.T("Phiếu hỗ trợ mới từ email: {0} – {1}", ticket.Number, ticket.Subject), $"/Tickets/Details/{ticket.Id}", null, from);
        if (settings.AutoReply && smtp.IsReady)
            _mail.Queue(from, $"[{ticket.Number}] {ticket.Subject}",
                EmailService.ToHtml(I18n.T("Kính gửi {0},", author) + "\n\n" + I18n.T("Chúng tôi đã tiếp nhận yêu cầu của Quý khách với mã {0}. Bộ phận chăm sóc khách hàng sẽ phản hồi trong thời gian sớm nhất.", ticket.Number)
                    + "\n\n" + I18n.T("Khi cần bổ sung thông tin, Quý khách vui lòng trả lời email này và giữ nguyên tiêu đề.")),
                nameof(Ticket), ticket.Id, ticket.CustomerId);
        await _db.SaveChangesAsync();
        return ("created", ticket);
    }

    /// <summary>Đọc hộp thư POP3: xử lý các email chưa xử lý. Trả về (số email đọc, số phiếu tạo, số trả lời).</summary>
    public async Task<(int Read, int Created, int Replied)> CheckMailboxAsync(CancellationToken ct = default)
    {
        var s = await SettingsAsync();
        if (!s.Pop3Enabled || string.IsNullOrWhiteSpace(s.Host)) return (0, 0, 0);
        int read = 0, created = 0, replied = 0;
        try
        {
            using var pop = new Pop3Client();
            await pop.ConnectAsync(s.Host, s.Port, s.UseSsl, ct);
            await pop.LoginAsync(s.Username ?? "", _mail.Unprotect(s.PasswordProtected) ?? "", ct);
            var known = (await _db.InboundMessages.Select(x => x.MessageKey).ToListAsync(ct)).ToHashSet();
            foreach (var (n, uid) in (await pop.ListAsync(ct)).Take(50))
            {
                var key = "pop3:" + uid;
                if (known.Contains(key)) { if (s.DeleteAfterRead) await pop.DeleteAsync(n, ct); continue; }
                var raw = await pop.RetrieveAsync(n, ct);
                var msg = MimeLite.Parse(raw, key);
                read++;
                string result;
                try { (result, _) = await ProcessAsync(msg); }
                catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
                {
                    _db.ChangeTracker.Clear();   // bỏ email lỗi, đọc tiếp email khác
                    result = "error";
                    s.LastResult = ex.InnerException?.Message ?? ex.Message;
                }
                if (result == "created") created++; else if (result == "replied") replied++;
                if (s.DeleteAfterRead) await pop.DeleteAsync(n, ct);
            }
            await pop.QuitAsync(ct);
            s.LastResult = I18n.T("Đọc {0} email: {1} phiếu mới, {2} trả lời.", read, created, replied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            s.LastResult = I18n.T("Lỗi: {0}", ex.Message);
        }
        s.LastCheckAt = DateTime.Now;
        await _settings.SaveAsync(InboundSettings.Key, s);
        return (read, created, replied);
    }

    /// <summary>Bỏ phần trích dẫn thư cũ khi khách trả lời</summary>
    public static string CleanBody(string body)
    {
        var text = (body ?? "").Replace("\r\n", "\n");
        var markers = new[] { "\n-----Original Message-----", "\n________________________________", "\nFrom: ", "\nTừ: " };
        foreach (var mk in markers) { var i = text.IndexOf(mk, StringComparison.OrdinalIgnoreCase); if (i > 0) text = text[..i]; }
        var wrote = Regex.Match(text, @"\n(On .{5,200} wrote:|Vào .{5,200} đã viết:)", RegexOptions.Singleline);
        if (wrote.Success && wrote.Index > 0) text = text[..wrote.Index];
        text = string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith(">")));
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Khách hàng tạm gom phiếu từ email chưa xác định được khách hàng (tạo một lần)</summary>
    private async Task<int> UnknownCustomerAsync()
    {
        var id = await _db.Customers.IgnoreQueryFilters().Where(c => c.Code == UnknownCode).Select(c => (int?)c.Id).FirstOrDefaultAsync();
        if (id.HasValue) return id.Value;
        var c = new Customer { Code = UnknownCode, Name = I18n.T("Khách hàng chưa xác định (email)"), Type = CustomerType.Prospect,
            Description = I18n.T("Tự tạo: gom các phiếu hỗ trợ từ email chưa nhận diện được khách hàng. Hãy gán lại khách hàng đúng cho từng phiếu."), CreatedAt = DateTime.Now };
        _db.Customers.Add(c);
        await _db.SaveChangesAsync();
        return c.Id;
    }

    private async Task<string> NextNumberAsync()
    {
        var prefix = $"YC-{DateTime.Today:yyyy}-";
        var n = await _db.Tickets.IgnoreQueryFilters().CountAsync(x => x.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await _db.Tickets.IgnoreQueryFilters().AnyAsync(x => x.Number == no));
        return no;
    }

    private static string Cut(string? s, int max) => s == null ? "" : s.Length > max ? s[..(max - 1)] + "…" : s;
}

/// <summary>POP3 tối giản (RFC 1939): USER/PASS, UIDL, RETR, DELE, QUIT; hỗ trợ SSL (cổng 995)</summary>
public sealed class Pop3Client : IDisposable
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private StreamReader? _reader;

    public async Task ConnectAsync(string host, int port, bool ssl, CancellationToken ct)
    {
        _tcp = new TcpClient { ReceiveTimeout = 30000, SendTimeout = 30000 };
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        if (ssl)
        {
            var s = new SslStream(_stream, false);
            await s.AuthenticateAsClientAsync(host);
            _stream = s;
        }
        _reader = new StreamReader(_stream, Encoding.Latin1);
        Expect(await _reader.ReadLineAsync(ct));
    }

    private static string Expect(string? line)
    {
        if (line == null || !line.StartsWith("+OK")) throw new IOException("POP3: " + (line ?? "mất kết nối"));
        return line;
    }

    private async Task<string> CommandAsync(string cmd, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(cmd + "\r\n");
        await _stream!.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
        return Expect(await _reader!.ReadLineAsync(ct));
    }

    private async Task<List<string>> MultiLineAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        while (true)
        {
            var l = await _reader!.ReadLineAsync(ct) ?? throw new IOException("POP3: mất kết nối");
            if (l == ".") break;
            lines.Add(l.StartsWith("..") ? l[1..] : l);
        }
        return lines;
    }

    public async Task LoginAsync(string user, string pass, CancellationToken ct)
    {
        await CommandAsync("USER " + user, ct);
        await CommandAsync("PASS " + pass, ct);
    }

    public async Task<List<(int N, string Uid)>> ListAsync(CancellationToken ct)
    {
        await CommandAsync("UIDL", ct);
        return (await MultiLineAsync(ct)).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2 && int.TryParse(p[0], out _))
            .Select(p => (int.Parse(p[0]), p[1].Trim())).ToList();
    }

    /// <summary>Nội dung thô của email (byte gốc giữ nguyên qua Latin1)</summary>
    public async Task<byte[]> RetrieveAsync(int n, CancellationToken ct)
    {
        await CommandAsync("RETR " + n, ct);
        return Encoding.Latin1.GetBytes(string.Join("\r\n", await MultiLineAsync(ct)));
    }

    public Task DeleteAsync(int n, CancellationToken ct) => CommandAsync("DELE " + n, ct);
    public async Task QuitAsync(CancellationToken ct) { try { await CommandAsync("QUIT", ct); } catch (IOException) { } }

    public void Dispose() { _reader?.Dispose(); _stream?.Dispose(); _tcp?.Dispose(); }
}

/// <summary>
/// Đọc email MIME tối giản: người gửi, tiêu đề (mã hóa =?charset?B/Q?...?=), phần văn bản (text/plain, nếu không có thì text/html bỏ thẻ),
/// giải mã base64 / quoted-printable và bảng mã.
/// </summary>
public static class MimeLite
{
    static MimeLite() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static InboundMail Parse(byte[] raw, string fallbackKey)
    {
        var text = Encoding.Latin1.GetString(raw);
        var (headers, body) = Split(text);
        var from = Decode(Header(headers, "From") ?? "");
        var (email, name) = Address(from);
        var subject = Decode(Header(headers, "Subject") ?? "");
        var key = Header(headers, "Message-ID")?.Trim() is { Length: > 0 } mid ? "mid:" + mid : fallbackKey;
        return new InboundMail(key, email, name, subject.Trim(), ExtractText(headers, body).Trim());
    }

    private static (Dictionary<string, string> Headers, string Body) Split(string part)
    {
        var idx = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = idx < 0 ? part : part[..idx];
        var body = idx < 0 ? "" : part[(idx + 4)..];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var line in head.Split("\r\n"))
        {
            if ((line.StartsWith(" ") || line.StartsWith("\t")) && last != null) { headers[last] += " " + line.Trim(); continue; }
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            last = line[..c].Trim();
            headers[last] = line[(c + 1)..].Trim();
        }
        return (headers, body);
    }

    private static string? Header(Dictionary<string, string> h, string name) => h.TryGetValue(name, out var v) ? v : null;

    private static string Param(string? header, string name)
    {
        var m = Regex.Match(header ?? "", name + @"\s*=\s*(""([^""]*)""|([^;\s]+))", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : "";
    }

    private static string ExtractText(Dictionary<string, string> headers, string body)
    {
        var ctype = Header(headers, "Content-Type") ?? "text/plain";
        if (ctype.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = Param(ctype, "boundary");
            if (boundary.Length == 0) return "";
            var parts = body.Split("--" + boundary).Skip(1).Where(p => !p.StartsWith("--")).Select(p => p.TrimStart('\r', '\n')).Select(Split).ToList();
            var plain = parts.FirstOrDefault(p => (Header(p.Headers, "Content-Type") ?? "text/plain").StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
                && !(Header(p.Headers, "Content-Disposition") ?? "").StartsWith("attachment", StringComparison.OrdinalIgnoreCase));
            if (plain.Headers != null) return ExtractText(plain.Headers, plain.Body);
            foreach (var p in parts.Where(p => (Header(p.Headers, "Content-Type") ?? "").StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)))
                if (ExtractText(p.Headers, p.Body) is { Length: > 0 } t) return t;
            var html = parts.FirstOrDefault(p => (Header(p.Headers, "Content-Type") ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
            return html.Headers != null ? ExtractText(html.Headers, html.Body) : "";
        }
        var bytes = (Header(headers, "Content-Transfer-Encoding") ?? "").Trim().ToLowerInvariant() switch
        {
            "base64" => SafeBase64(body),
            "quoted-printable" => QuotedPrintable(body),
            _ => Encoding.Latin1.GetBytes(body)
        };
        var text = Charset(Param(ctype, "charset")).GetString(bytes);
        if (ctype.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            text = Regex.Replace(text, @"<(br|/p|/div|/tr|/li)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<(style|script)[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = System.Net.WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", ""));
        }
        return text.Replace("\r\n", "\n");
    }

    private static Encoding Charset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Encoding.UTF8;
        try { return Encoding.GetEncoding(name.Trim()); } catch { return Encoding.UTF8; }
    }

    private static byte[] SafeBase64(string s)
    {
        try { return Convert.FromBase64String(Regex.Replace(s, @"[^A-Za-z0-9+/=]", "")); } catch { return Array.Empty<byte>(); }
    }

    private static byte[] QuotedPrintable(string s)
    {
        var ms = new MemoryStream();
        s = s.Replace("=\r\n", "").Replace("=\n", "");
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { ms.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
            else ms.WriteByte((byte)s[i]);
        }
        return ms.ToArray();
    }

    /// <summary>Giải mã tiêu đề dạng =?utf-8?B?...?= / =?utf-8?Q?...?= (ghép các đoạn liền nhau)</summary>
    public static string Decode(string value)
    {
        value = Regex.Replace(value, @"\?=\s+=\?", "?==?");
        var decoded = Regex.Replace(value, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
        {
            var enc = Charset(m.Groups[1].Value);
            var data = m.Groups[2].Value.ToUpperInvariant() == "B" ? SafeBase64(m.Groups[3].Value) : QuotedPrintable(m.Groups[3].Value.Replace('_', ' '));
            return enc.GetString(data);
        });
        // Giá trị không mã hóa nhưng chứa byte UTF-8 thô
        return decoded.Any(c => c > 127) && decoded.All(c => c < 256) ? Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(decoded)) : decoded;
    }

    public static (string Email, string? Name) Address(string from)
    {
        var m = Regex.Match(from, @"^\s*""?([^""<]*?)""?\s*<([^>]+)>");
        if (m.Success) return (m.Groups[2].Value.Trim(), m.Groups[1].Value.Trim() is { Length: > 0 } n ? n : null);
        var e = Regex.Match(from, @"[\w.+-]+@[\w-]+(\.[\w-]+)+");
        return (e.Success ? e.Value : from.Trim(), null);
    }
}

/// <summary>Tác vụ nền: mỗi 3 phút đọc hộp thư hỗ trợ (nếu bật POP3)</summary>
public class InboundWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InboundWorker> _log;
    public InboundWorker(IServiceScopeFactory scopes, ILogger<InboundWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var (read, created, replied) = await scope.ServiceProvider.GetRequiredService<InboundService>().CheckMailboxAsync(ct);
                if (read > 0) _log.LogInformation("Hộp thư hỗ trợ: {Read} email, {Created} phiếu mới, {Replied} trả lời", read, created, replied);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "Lỗi đọc hộp thư hỗ trợ"); }
            try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
