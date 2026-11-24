using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Hạng thành viên: xét theo tổng điểm tích lũy trọn đời, mỗi hạng có hệ số nhân điểm và ưu đãi</summary>
public class LoyaltyTierDef
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Tổng điểm tích lũy tối thiểu để đạt hạng</summary>
    public int MinPoints { get; set; }
    /// <summary>Hệ số nhân điểm khi tích điểm (1 = chuẩn, 1.25 = thêm 25%)</summary>
    public decimal EarnMultiplier { get; set; } = 1;
    /// <summary>Mức giảm giá ưu đãi gợi ý cho hạng (%) – dùng để báo giá / đàm phán</summary>
    public decimal DiscountPercent { get; set; }
    public string Color { get; set; } = "#6c757d";
    public string? Benefits { get; set; }
    /// <summary>Tên / quyền lợi hiển thị theo ngôn ngữ (các hạng mặc định có bản dịch)</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Label => I18n.T(Name);
    [System.Text.Json.Serialization.JsonIgnore] public string? BenefitsLabel => string.IsNullOrWhiteSpace(Benefits) ? null : I18n.T(Benefits);
}

/// <summary>Cấu hình chương trình khách hàng thân thiết (AppSettings "loyalty.program")</summary>
public class LoyaltySettings
{
    public const string Key = "loyalty.program";
    public bool Enabled { get; set; } = true;
    /// <summary>Số tiền thanh toán để được 1 điểm</summary>
    public decimal SpendPerPoint { get; set; } = 10_000_000;
    /// <summary>Giá trị quy đổi của 1 điểm khi đổi thưởng (đồng)</summary>
    public decimal PointValue { get; set; } = 100_000;
    /// <summary>Điểm hết hạn sau bao nhiêu tháng kể từ ngày tích (0 = không hết hạn)</summary>
    public int ExpiryMonths { get; set; } = 12;
    /// <summary>Số điểm tối thiểu mỗi lần đổi</summary>
    public int MinRedeem { get; set; } = 10;
    public List<LoyaltyTierDef> Tiers { get; set; } = new()
    {
        new() { Id = 1, Name = "Thành viên", MinPoints = 0, EarnMultiplier = 1, DiscountPercent = 0, Color = "#6c757d", Benefits = "Tích điểm trên mọi khoản thanh toán" },
        new() { Id = 2, Name = "Bạc", MinPoints = 50, EarnMultiplier = 1.1m, DiscountPercent = 2, Color = "#8e9aaf", Benefits = "Nhân 1,1 lần điểm; ưu tiên hỗ trợ" },
        new() { Id = 3, Name = "Vàng", MinPoints = 150, EarnMultiplier = 1.25m, DiscountPercent = 5, Color = "#d4a017", Benefits = "Nhân 1,25 lần điểm; chiết khấu gợi ý 5%; người phụ trách riêng" },
        new() { Id = 4, Name = "Kim cương", MinPoints = 400, EarnMultiplier = 1.5m, DiscountPercent = 8, Color = "#2b8cbe", Benefits = "Nhân 1,5 lần điểm; chiết khấu gợi ý 8%; hỗ trợ ưu tiên cao nhất" }
    };

    public IEnumerable<LoyaltyTierDef> Ordered => Tiers.OrderBy(t => t.MinPoints);
    /// <summary>Hạng ứng với tổng điểm tích lũy</summary>
    public LoyaltyTierDef? TierFor(int lifetime) => Ordered.LastOrDefault(t => lifetime >= t.MinPoints) ?? Ordered.FirstOrDefault();
    public LoyaltyTierDef? TierById(int? id) => id.HasValue ? Tiers.FirstOrDefault(t => t.Id == id) : null;
    public LoyaltyTierDef? NextTier(int lifetime) => Ordered.FirstOrDefault(t => t.MinPoints > lifetime);
}

public record RedeemResult(bool Ok, string? Error = null, decimal Value = 0);

/// <summary>Tích / đổi / điều chỉnh / hết hạn điểm thưởng và xét hạng thành viên</summary>
public class LoyaltyService
{
    private readonly CrmDbContext _db;
    private readonly SettingsStore _settings;
    private readonly NotificationService _notify;
    public LoyaltyService(CrmDbContext db, SettingsStore settings, NotificationService notify) { _db = db; _settings = settings; _notify = notify; }

    public static bool Eligible(CustomerType t) => t is CustomerType.Customer or CustomerType.Partner;
    public Task<LoyaltySettings> SettingsAsync() => _settings.GetAsync<LoyaltySettings>(LoyaltySettings.Key);

    private static int PointsFor(LoyaltySettings s, decimal amount, decimal multiplier) =>
        s.SpendPerPoint <= 0 || amount <= 0 ? 0 : (int)Math.Floor(amount / s.SpendPerPoint * multiplier);

    /// <summary>Tích điểm cho một phiếu thu (mỗi phiếu chỉ tích một lần). Trả về số điểm được cộng.</summary>
    public async Task<int> EarnForPaymentAsync(Payment p, CancellationToken ct = default)
    {
        var s = await SettingsAsync();
        if (!s.Enabled || p.CustomerId is not > 0) return 0;
        if (await _db.LoyaltyTransactions.AnyAsync(t => t.PaymentId == p.Id && t.Type == LoyaltyTxType.Earn, ct)) return 0;
        var c = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == p.CustomerId, ct);
        if (c == null || !Eligible(c.Type)) return 0;
        var pts = PointsFor(s, p.Amount, s.TierFor(c.LoyaltyLifetime)?.EarnMultiplier ?? 1);
        if (pts <= 0) return 0;
        _db.LoyaltyTransactions.Add(new LoyaltyTransaction
        {
            CustomerId = c.Id, Type = LoyaltyTxType.Earn, Points = pts, Remaining = pts, PaymentId = p.Id,
            ExpiresAt = s.ExpiryMonths > 0 ? p.PaymentDate.Date.AddMonths(s.ExpiryMonths) : null,
            Description = I18n.T("Thanh toán {0}: {1}", p.Number, p.Amount.ToMoney()), CreatedById = p.OwnerId, CreatedAt = DateTime.Now
        });
        await _db.SaveChangesAsync(ct);
        await RecalcAsync(c, s, ct);
        return pts;
    }

    /// <summary>Thu hồi điểm đã tích khi phiếu thu bị xóa (phần đã đổi / hết hạn không thu hồi lại)</summary>
    public async Task ReverseForPaymentAsync(int paymentId, CancellationToken ct = default)
    {
        var lots = await _db.LoyaltyTransactions.Where(t => t.PaymentId == paymentId && t.Type == LoyaltyTxType.Earn && !t.Reversed).ToListAsync(ct);
        if (lots.Count == 0) return;
        var s = await SettingsAsync();
        foreach (var lot in lots)
        {
            _db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                CustomerId = lot.CustomerId, Type = LoyaltyTxType.Reverse, Points = -lot.Remaining, PaymentId = paymentId,
                Description = I18n.T("Thu hồi điểm do xóa phiếu thu"), CreatedAt = DateTime.Now
            });
            lot.Reversed = true; lot.Remaining = 0;
        }
        await _db.SaveChangesAsync(ct);
        foreach (var id in lots.Select(l => l.CustomerId).Distinct())
        {
            var c = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c != null) await RecalcAsync(c, s, ct, allowDowngrade: true);
        }
    }

    /// <summary>Đổi điểm lấy ưu đãi (trừ điểm theo lô cũ nhất trước)</summary>
    public async Task<RedeemResult> RedeemAsync(int customerId, int points, string? description, int? userId, CancellationToken ct = default)
    {
        var s = await SettingsAsync();
        if (!s.Enabled) return new(false, I18n.T("Chương trình khách hàng thân thiết đang tắt."));
        var c = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == customerId, ct);
        if (c == null) return new(false, I18n.T("Không tìm thấy khách hàng."));
        if (points < Math.Max(1, s.MinRedeem)) return new(false, I18n.T("Mỗi lần đổi tối thiểu {0} điểm.", Math.Max(1, s.MinRedeem)));
        if (points > c.LoyaltyPoints) return new(false, I18n.T("Khách hàng chỉ còn {0} điểm.", c.LoyaltyPoints));
        await ConsumeAsync(customerId, points, ct);
        _db.LoyaltyTransactions.Add(new LoyaltyTransaction
        {
            CustomerId = customerId, Type = LoyaltyTxType.Redeem, Points = -points, Description = string.IsNullOrWhiteSpace(description) ? I18n.T("Đổi điểm") : description.Trim(),
            CreatedById = userId, CreatedAt = DateTime.Now
        });
        await _db.SaveChangesAsync(ct);
        await RecalcAsync(c, s, ct);
        return new(true, null, points * s.PointValue);
    }

    /// <summary>Điều chỉnh thủ công (thưởng thêm / trừ do sai sót). Điểm cộng thêm có hạn dùng như điểm tích.</summary>
    public async Task<RedeemResult> AdjustAsync(int customerId, int points, string reason, int? userId, CancellationToken ct = default)
    {
        if (points == 0) return new(false, I18n.T("Số điểm điều chỉnh phải khác 0."));
        if (string.IsNullOrWhiteSpace(reason)) return new(false, I18n.T("Vui lòng nhập lý do điều chỉnh."));
        var s = await SettingsAsync();
        var c = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == customerId, ct);
        if (c == null) return new(false, I18n.T("Không tìm thấy khách hàng."));
        if (points < 0)
        {
            if (-points > c.LoyaltyPoints) return new(false, I18n.T("Khách hàng chỉ còn {0} điểm.", c.LoyaltyPoints));
            await ConsumeAsync(customerId, -points, ct);
        }
        _db.LoyaltyTransactions.Add(new LoyaltyTransaction
        {
            CustomerId = customerId, Type = LoyaltyTxType.Adjust, Points = points, Remaining = Math.Max(0, points),
            ExpiresAt = points > 0 && s.ExpiryMonths > 0 ? DateTime.Today.AddMonths(s.ExpiryMonths) : null,
            Description = reason.Trim(), CreatedById = userId, CreatedAt = DateTime.Now
        });
        await _db.SaveChangesAsync(ct);
        await RecalcAsync(c, s, ct, allowDowngrade: points < 0);
        return new(true);
    }

    private async Task ConsumeAsync(int customerId, int points, CancellationToken ct)
    {
        var lots = await _db.LoyaltyTransactions.Where(t => t.CustomerId == customerId && t.Remaining > 0)
            .OrderBy(t => t.ExpiresAt == null).ThenBy(t => t.ExpiresAt).ThenBy(t => t.Id).ToListAsync(ct);
        foreach (var lot in lots)
        {
            if (points <= 0) break;
            var take = Math.Min(points, lot.Remaining);
            lot.Remaining -= take; points -= take;
        }
    }

    /// <summary>Cho hết hạn các lô điểm quá hạn; trả về tổng số điểm đã hết hạn</summary>
    public async Task<int> ExpireAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var lots = await _db.LoyaltyTransactions.Where(t => t.Remaining > 0 && t.ExpiresAt != null && t.ExpiresAt < today).ToListAsync(ct);
        if (lots.Count == 0) return 0;
        var total = 0;
        foreach (var lot in lots)
        {
            total += lot.Remaining;
            _db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                CustomerId = lot.CustomerId, Type = LoyaltyTxType.Expire, Points = -lot.Remaining,
                Description = I18n.T("Hết hạn điểm tích ngày {0}", lot.CreatedAt.ToString("dd/MM/yyyy")), CreatedAt = DateTime.Now
            });
            lot.Remaining = 0;
        }
        await _db.SaveChangesAsync(ct);
        var s = await SettingsAsync();
        foreach (var id in lots.Select(l => l.CustomerId).Distinct())
        {
            var c = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c != null) await RecalcAsync(c, s, ct);
        }
        return total;
    }

    /// <summary>Tính lại số dư, tổng điểm tích lũy và hạng của khách từ sổ điểm. Hạng chỉ tăng, trừ khi điểm bị thu hồi / trừ.</summary>
    public async Task RecalcAsync(Customer c, LoyaltySettings s, CancellationToken ct = default, bool allowDowngrade = false)
    {
        var tx = await _db.LoyaltyTransactions.Where(t => t.CustomerId == c.Id).Select(t => new { t.Type, t.Points, t.Remaining, t.Reversed }).ToListAsync(ct);
        var balance = tx.Sum(t => t.Remaining);
        var lifetime = tx.Where(t => !t.Reversed && (t.Type == LoyaltyTxType.Earn || (t.Type == LoyaltyTxType.Adjust && t.Points > 0))).Sum(t => t.Points)
            + tx.Where(t => t.Type == LoyaltyTxType.Adjust && t.Points < 0).Sum(t => t.Points);
        lifetime = Math.Max(0, lifetime);
        var oldTier = s.TierById(c.LoyaltyTierId);
        var newTier = s.TierFor(lifetime);
        if (!allowDowngrade && oldTier != null && newTier != null && newTier.MinPoints < oldTier.MinPoints) newTier = oldTier;
        c.LoyaltyPoints = balance; c.LoyaltyLifetime = lifetime; c.LoyaltyTierId = newTier?.Id;
        if (newTier != null && oldTier != null && newTier.MinPoints > oldTier.MinPoints && c.OwnerId.HasValue)
            _notify.Add(c.OwnerId, "loyalty", I18n.T("{0} đạt hạng {1} (tích lũy {2} điểm)", c.Name, newTier.Label, lifetime), $"/Loyalty/Customer/{c.Id}", key: $"loyalty-tier:{c.Id}:{newTier.Id}");
        var suppress = _db.SuppressAudit; _db.SuppressAudit = true;
        await _db.SaveChangesAsync(ct);
        _db.SuppressAudit = suppress;
    }

    /// <summary>Tích điểm bù cho các phiếu thu chưa có điểm (lịch sử trước khi bật chương trình) rồi tính lại mọi khách. Trả về (số phiếu, số điểm).</summary>
    public async Task<(int Payments, int Points)> BackfillAsync(CancellationToken ct = default)
    {
        var s = await SettingsAsync();
        if (!s.Enabled) return (0, 0);
        var done = (await _db.LoyaltyTransactions.Where(t => t.PaymentId != null && t.Type == LoyaltyTxType.Earn).Select(t => t.PaymentId!.Value).ToListAsync(ct)).ToHashSet();
        var customers = await _db.Customers.IgnoreQueryFilters().ToDictionaryAsync(c => c.Id, ct);
        var running = customers.ToDictionary(c => c.Key, c => c.Value.LoyaltyLifetime);
        var payments = await _db.Payments.IgnoreQueryFilters().Where(p => p.CustomerId != null).OrderBy(p => p.PaymentDate).ThenBy(p => p.Id).ToListAsync(ct);
        int n = 0, total = 0; var today = DateTime.Today;
        foreach (var p in payments.Where(p => !done.Contains(p.Id)))
        {
            if (!customers.TryGetValue(p.CustomerId!.Value, out var c) || !Eligible(c.Type)) continue;
            var pts = PointsFor(s, p.Amount, s.TierFor(running[c.Id])?.EarnMultiplier ?? 1);
            if (pts <= 0) continue;
            var expires = s.ExpiryMonths > 0 ? p.PaymentDate.Date.AddMonths(s.ExpiryMonths) : (DateTime?)null;
            var expired = expires.HasValue && expires.Value < today;
            _db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                CustomerId = c.Id, Type = LoyaltyTxType.Earn, Points = pts, Remaining = expired ? 0 : pts, ExpiresAt = expires, PaymentId = p.Id,
                Description = I18n.T("Thanh toán {0}: {1}", p.Number, p.Amount.ToMoney()), CreatedById = p.OwnerId, CreatedAt = p.PaymentDate.Date
            });
            if (expired)
                _db.LoyaltyTransactions.Add(new LoyaltyTransaction
                {
                    CustomerId = c.Id, Type = LoyaltyTxType.Expire, Points = -pts, Description = I18n.T("Hết hạn điểm tích ngày {0}", p.PaymentDate.ToString("dd/MM/yyyy")), CreatedAt = expires!.Value
                });
            running[c.Id] += pts; n++; total += pts;
        }
        await _db.SaveChangesAsync(ct);
        foreach (var c in customers.Values.Where(c => Eligible(c.Type)))
            if (running[c.Id] != c.LoyaltyLifetime || c.LoyaltyTierId == null) await RecalcAsync(c, s, ct, allowDowngrade: true);
        return (n, total);
    }
}
