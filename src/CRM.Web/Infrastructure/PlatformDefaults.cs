using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Dữ liệu mặc định của nền tảng (chạy mỗi lần khởi động, chỉ thêm khi chưa có)</summary>
public static class PlatformDefaults
{
    public const string ResetCode = "password-reset";
    public const string NotificationCode = "notification";

    public static async Task EnsureAsync(CrmDbContext db)
    {
        var codes = await db.EmailTemplates.Select(t => t.Code).ToListAsync();
        var any = await db.EmailTemplates.AnyAsync();
        void Add(string? code, string? scope, string name, string subject, string body)
        {
            if (code != null ? codes.Contains(code) : any) return;
            db.EmailTemplates.Add(new EmailTemplate { Code = code, Scope = scope, Name = name, Subject = subject, Body = body, IsActive = true });
        }
        Add(ResetCode, null, "Đặt lại mật khẩu", "[CRM] Đặt lại mật khẩu",
            "Xin chào {{NhanVien}},\n\nChúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản CRM của bạn. Bấm vào liên kết sau để đặt mật khẩu mới (hiệu lực 30 phút):\n{{Link}}\n\nNếu bạn không yêu cầu, hãy bỏ qua email này – mật khẩu hiện tại vẫn giữ nguyên.");
        Add(NotificationCode, null, "Thông báo công việc", "[CRM] {{TieuDe}}",
            "Xin chào {{NhanVien}},\n\n{{NoiDung}}\n\nXem chi tiết: {{Link}}\n\n(Bạn nhận email này vì đã bật \"Nhận thông báo qua email\" trong Tài khoản của tôi.)");
        Add(null, nameof(Quote), "Gửi báo giá", "Báo giá {{SoChungTu}} – {{CongTy}}",
            "Kính gửi {{NguoiLienHe}},\n\n{{CongTy}} xin gửi Quý khách báo giá số {{SoChungTu}} với tổng giá trị {{TongTien}} (đã gồm VAT). Báo giá có hiệu lực đến ngày {{HanThanhToan}}.\n\nChi tiết như bảng bên dưới. Nếu cần trao đổi thêm, Quý khách vui lòng phản hồi email này.\n\nTrân trọng,\n{{NhanVien}}");
        Add(null, nameof(Contract), "Gửi hợp đồng", "Hợp đồng {{SoChungTu}} – {{TieuDe}}",
            "Kính gửi {{NguoiLienHe}},\n\n{{CongTy}} xin gửi Quý khách hợp đồng {{SoChungTu}} – {{TieuDe}}, tổng giá trị {{TongTien}}.\n\nQuý khách vui lòng xem xét, ký và gửi lại cho chúng tôi.\n\nTrân trọng,\n{{NhanVien}}");
        Add(null, nameof(Invoice), "Thông báo hoá đơn / nhắc thanh toán", "Hoá đơn {{SoChungTu}} – {{CongTy}}",
            "Kính gửi {{NguoiLienHe}},\n\n{{CongTy}} xin thông báo hoá đơn {{SoChungTu}}, tổng thanh toán {{TongTien}}, số tiền còn phải thanh toán {{ConPhaiThu}}, hạn thanh toán {{HanThanhToan}}.\n\nNếu Quý khách đã thanh toán, vui lòng bỏ qua email này.\n\nTrân trọng,\n{{NhanVien}}");
        Add(null, nameof(Ticket), "Cập nhật phiếu hỗ trợ", "[{{SoChungTu}}] {{TieuDe}}",
            "Kính gửi {{NguoiLienHe}},\n\nChúng tôi xin cập nhật yêu cầu hỗ trợ {{SoChungTu}} – \"{{TieuDe}}\" (trạng thái: {{TrangThai}}):\n\n\n\nTrân trọng,\n{{NhanVien}}");
        Add(null, nameof(Customer), "Thư cảm ơn khách hàng", "Cảm ơn Quý khách đã đồng hành cùng {{CongTy}}",
            "Kính gửi {{NguoiLienHe}},\n\nThay mặt {{CongTy}}, xin chân thành cảm ơn {{KhachHang}} đã tin tưởng sử dụng sản phẩm, dịch vụ của chúng tôi.\n\nTrân trọng,\n{{NhanVien}}");
        Add(null, nameof(Campaign), "Thư mời hội thảo", "Thư mời: Hội thảo giải pháp chuyển đổi số",
            "Kính gửi Anh/Chị {{Ten}},\n\nTrân trọng kính mời Anh/Chị tham dự hội thảo giới thiệu giải pháp chuyển đổi số cho doanh nghiệp.\n\nVui lòng phản hồi email này để xác nhận tham dự.\n\nTrân trọng,\n{{NhanVien}}");
        // Kho kiến thức mẫu (bài hướng dẫn công khai + mẫu trả lời nhanh)
        if (!await db.KbArticles.AnyAsync())
        {
            db.KbArticles.AddRange(
                new KbArticle { Title = "Mẫu: xác nhận đã tiếp nhận yêu cầu", IsTemplate = true, Keywords = "tiếp nhận, xác nhận",
                    Body = "Kính gửi {{NguoiLienHe}},\n\nChúng tôi đã tiếp nhận yêu cầu {{SoPhieu}} và đang kiểm tra. Bộ phận kỹ thuật sẽ phản hồi trong thời gian sớm nhất.\n\nTrân trọng," },
                new KbArticle { Title = "Mẫu: đề nghị khách cung cấp thêm thông tin", IsTemplate = true, Keywords = "bổ sung, thông tin, ảnh chụp",
                    Body = "Kính gửi {{NguoiLienHe}},\n\nĐể xử lý nhanh yêu cầu {{SoPhieu}}, Quý khách vui lòng cung cấp thêm:\n- Ảnh chụp màn hình lỗi\n- Thời điểm xảy ra và các bước thao tác\n- Tài khoản đang sử dụng\n\nTrân trọng," },
                new KbArticle { Title = "Mẫu: thông báo đã xử lý xong", IsTemplate = true, Keywords = "hoàn thành, đã xử lý",
                    Body = "Kính gửi {{NguoiLienHe}},\n\nYêu cầu {{SoPhieu}} đã được xử lý xong. Quý khách vui lòng kiểm tra lại và phản hồi nếu còn vướng mắc.\n\nTrân trọng," },
                new KbArticle { Title = "Không đăng nhập được hệ thống / quên mật khẩu", IsPublic = true, Keywords = "đăng nhập, mật khẩu, khóa tài khoản",
                    Body = "1. Kiểm tra lại tên đăng nhập, bộ gõ tiếng Việt và phím Caps Lock.\n2. Bấm \"Quên mật khẩu?\" trên màn hình đăng nhập để nhận liên kết đặt lại qua email.\n3. Nếu tài khoản bị khóa tạm do nhập sai nhiều lần, chờ 15 phút hoặc liên hệ quản trị.\n4. Vẫn không được: gửi yêu cầu hỗ trợ kèm ảnh chụp màn hình." },
                new KbArticle { Title = "Cách xuất dữ liệu báo cáo ra Excel", IsPublic = true, Keywords = "báo cáo, excel, xuất",
                    Body = "Mở trang Báo cáo → chọn báo cáo và kỳ → bấm \"Xuất Excel\". File .xlsx giữ định dạng số và ngày, có thể lọc / cộng tổng ngay trong Excel." });
            await db.SaveChangesAsync();
        }

        // Quy tắc tự động mẫu (tắt sẵn – quản lý xem lại rồi bật)
        if (!await db.AutomationRules.AnyAsync())
        {
            db.AutomationRules.AddRange(
                new AutomationRule { Name = "Lead mới → gọi điện trong 1 ngày", IsActive = false, Trigger = AutomationTrigger.LeadCreated, Action = AutomationAction.CreateTask,
                    Assignee = AutomationAssignee.Owner, Title = "Gọi điện tìm hiểu nhu cầu: {{Ten}}", TaskDueDays = 1, TaskPriority = Priority.High },
                new AutomationRule { Name = "Lead chưa liên hệ sau 3 ngày → báo quản lý", IsActive = false, Trigger = AutomationTrigger.LeadIdle, Days = 3, Action = AutomationAction.Notify,
                    Assignee = AutomationAssignee.Manager, Title = "Lead chưa được liên hệ sau {{SoNgay}} ngày: {{Ten}}" },
                new AutomationRule { Name = "Cơ hội sang Đàm phán → chuẩn bị hợp đồng", IsActive = false, Trigger = AutomationTrigger.OpportunityStageChanged, Condition = (int)OpportunityStage.Negotiation,
                    Action = AutomationAction.CreateTask, Assignee = AutomationAssignee.Owner, Title = "Chuẩn bị dự thảo hợp đồng: {{Ten}}", TaskDueDays = 3 },
                new AutomationRule { Name = "Cơ hội 14 ngày không có hoạt động → nhắc người phụ trách", IsActive = false, Trigger = AutomationTrigger.OpportunityIdle, Days = 14,
                    Action = AutomationAction.CreateTask, Assignee = AutomationAssignee.Owner, Title = "Liên hệ lại khách hàng: {{Ten}} ({{SoNgay}} ngày chưa tương tác)", TaskDueDays = 1 },
                new AutomationRule { Name = "Hoá đơn quá hạn 7 ngày → nhắc thu tiền", IsActive = false, Trigger = AutomationTrigger.InvoiceOverdue, Days = 7, Action = AutomationAction.CreateTask,
                    Assignee = AutomationAssignee.Owner, Title = "Nhắc thanh toán hoá đơn {{Ten}} – {{KhachHang}}", TaskDueDays = 1, TaskPriority = Priority.High },
                new AutomationRule { Name = "Phiếu hỗ trợ khẩn cấp → báo quản lý", IsActive = false, Trigger = AutomationTrigger.TicketCreated, Condition = (int)TicketPriority.Urgent,
                    Action = AutomationAction.Notify, Assignee = AutomationAssignee.Manager, Title = "Phiếu hỗ trợ khẩn cấp: {{Ten}}" });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Gửi thông báo qua email cho người dùng bật "Nhận thông báo qua email": quét thông báo mới (theo Id đã xử lý lần trước).
    /// Lần chạy đầu chỉ ghi nhận mốc, không gửi lại thông báo cũ.
    /// </summary>
    public static async Task<int> QueueNotificationEmailsAsync(CrmDbContext db, EmailService mail, SettingsStore settings, CancellationToken ct = default)
    {
        var state = await settings.GetAsync<NotifyMailState>(NotifyMailState.Key);
        var maxId = await db.Notifications.MaxAsync(n => (int?)n.Id, ct) ?? 0;
        if (!state.Init) { await settings.SaveAsync(NotifyMailState.Key, new NotifyMailState { Init = true, LastId = maxId }); return 0; }
        if (maxId <= state.LastId) return 0;
        var s = await mail.SettingsAsync();
        var n = 0;
        if (s.IsReady)
        {
            var tpl = await db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Code == NotificationCode && t.IsActive, ct);
            var items = await db.Notifications.AsNoTracking().Where(x => x.Id > state.LastId && x.Id <= maxId
                    && x.User!.EmailNotifications && x.User.IsActive && x.User.Email != null && x.User.Email != "")
                .Select(x => new { x.Title, x.Body, x.Url, x.User!.Email, x.User.FullName }).ToListAsync(ct);
            foreach (var x in items)
            {
                var vars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NhanVien"] = x.FullName, ["TieuDe"] = x.Title, ["NoiDung"] = x.Title + (string.IsNullOrEmpty(x.Body) ? "" : "\n" + x.Body),
                    ["Link"] = EmailService.Absolute(s, x.Url), ["Ngay"] = DateTime.Today.ToDate()
                };
                var subject = tpl != null ? EmailService.Merge(tpl.Subject, vars) : "[CRM] " + x.Title;
                var body = tpl != null ? EmailService.Merge(tpl.Body, vars) : x.Title + "\n" + vars["Link"];
                mail.Queue(x.Email!, subject, EmailService.ToHtml(body), "Notification");
                n++;
            }
            if (n > 0) await db.SaveChangesAsync(ct);
        }
        await settings.SaveAsync(NotifyMailState.Key, new NotifyMailState { Init = true, LastId = maxId });
        return n;
    }

    public class NotifyMailState
    {
        public const string Key = "notify.mail";
        public bool Init { get; set; }
        public int LastId { get; set; }
    }
}
