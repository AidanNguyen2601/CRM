using System.Linq.Expressions;
using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static CRM.Web.Infrastructure.I18n;

namespace CRM.Web.Infrastructure;

/// <summary>Chứng từ mẫu đang ở đúng bước (bấm để mở)</summary>
public record ScenarioLink(string Label, string? Sub, string Url);
/// <summary>Nút thao tác của bước (tạo mới, mở màn hình cấu hình…)</summary>
public record ScenarioAction(string Label, string Url);
/// <summary>Một bước của luồng: ai làm (vai trò), làm gì, chứng từ mẫu đang ở bước này</summary>
public record ScenarioStep(string Title, string[] Roles, string Description, List<ScenarioLink> Samples, List<ScenarioAction> Actions);
public record ScenarioFlow(string Key, string Icon, string Title, string Intro, List<ScenarioStep> Steps);
/// <summary>Tài khoản dùng thử; Password chỉ có khi tài khoản còn dùng mật khẩu mẫu của hệ thống</summary>
public record ScenarioAccount(string Username, string FullName, string Role, string Branch, string? Password);

/// <summary>
/// Kịch bản nghiệp vụ: các luồng xuyên suốt của CRM (lead → cơ hội → báo giá → hợp đồng → hoá đơn → thu tiền, hỗ trợ, tiếp thị, kế hoạch…),
/// mỗi bước nêu tài khoản nên đăng nhập và dẫn tới chứng từ mẫu đang ở đúng bước. Chứng từ mẫu lấy từ dữ liệu thật và chỉ hiện
/// những bản ghi người xem được quyền thấy (cùng giới hạn như danh sách chức năng tương ứng).
/// </summary>
public class ScenarioService
{
    private const int Samples = 4;
    /// <summary>Mật khẩu mẫu theo vai trò do bộ nạp dữ liệu mẫu tạo</summary>
    private static readonly Dictionary<string, string> SeedPasswords = new()
    {
        [AppRoles.Admin] = "Admin@123", [AppRoles.Manager] = "Manager@123", [AppRoles.Sales] = "Sales@123", [AppRoles.Support] = "Support@123"
    };

    private readonly CrmDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;
    public ScenarioService(CrmDbContext db, IPasswordHasher<AppUser> hasher) { _db = db; _hasher = hasher; }

    // ======================= Tài khoản =======================

    public async Task<List<ScenarioAccount>> AccountsAsync(bool revealDefaultPasswords)
    {
        var users = await _db.Users.AsNoTracking().Include(u => u.Branch).Where(u => u.IsActive)
            .OrderBy(u => u.Role == AppRoles.Admin ? 0 : u.Role == AppRoles.Manager ? 1 : u.Role == AppRoles.Sales ? 2 : 3).ThenBy(u => u.Username).ToListAsync();
        var list = new List<ScenarioAccount>();
        foreach (var u in users)
        {
            // Chỉ lộ mật khẩu khi tài khoản vẫn dùng đúng mật khẩu mẫu (đã đổi thì không hiện)
            string? pwd = null;
            if (revealDefaultPasswords && SeedPasswords.TryGetValue(u.Role, out var def)
                && _hasher.VerifyHashedPassword(u, u.PasswordHash, def) != PasswordVerificationResult.Failed) pwd = def;
            list.Add(new(u.Username, u.FullName, u.Role, u.Branch?.DisplayName ?? "", pwd));
        }
        return list;
    }

    // ======================= Luồng nghiệp vụ =======================

    public async Task<List<ScenarioFlow>> FlowsAsync(ClaimsPrincipal user)
    {
        var db = _db;
        static ScenarioLink Lk(string label, string? sub, string url) => new(label, string.IsNullOrWhiteSpace(sub) ? null : sub, url);
        static ScenarioAction Go(string label, string url) => new(label, url);
        static string[] R(params string[] roles) => roles;
        var none = new List<ScenarioLink>();
        var seesAll = user.SeesAll();

        async Task<List<ScenarioLink>> Leads(Expression<Func<Lead, bool>> p) =>
            (await db.Leads.VisibleTo(user, db).Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.FullName, x.Company }).ToListAsync())
            .Select(x => Lk(x.FullName, x.Company, $"/Leads/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Opps(Expression<Func<Opportunity, bool>> p) =>
            (await db.Opportunities.VisibleTo(user, db).Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Name, Cust = x.Customer != null ? x.Customer.Name : null }).ToListAsync())
            .Select(x => Lk(x.Name, x.Cust, $"/Opportunities/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Quotes(Expression<Func<Quote, bool>> p) =>
            (await db.Quotes.VisibleTo(user, db).Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Number, Cust = x.Customer != null ? x.Customer.Name : null }).ToListAsync())
            .Select(x => Lk(x.Number, x.Cust, $"/Quotes/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Plans(Expression<Func<BusinessPlan, bool>> p) =>
            (await db.BusinessPlans.VisibleTo(user, db).Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Number, x.Title }).ToListAsync())
            .Select(x => Lk(x.Number, x.Title, $"/BusinessPlans/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Contracts(Expression<Func<Contract, bool>> p) =>
            (await db.Contracts.VisibleTo(user, db).Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Number, x.Title }).ToListAsync())
            .Select(x => Lk(x.Number, x.Title, $"/Contracts/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Invoices(Expression<Func<Invoice, bool>> p) =>
            (await db.Invoices.VisibleTo(user, db).Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Number, Cust = x.Customer != null ? x.Customer.Name : null }).ToListAsync())
            .Select(x => Lk(x.Number, x.Cust, $"/Invoices/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Payments() =>
            (await db.Payments.VisibleTo(user, db).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Number, x.Amount }).ToListAsync())
            .Select(x => Lk(x.Number, x.Amount.ToShortMoney(), $"/Payments/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Campaigns(Expression<Func<Campaign, bool>> p) =>
            (await db.Campaigns.VisibleTo(user, db).Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Name }).ToListAsync())
            .Select(x => Lk(x.Name, null, $"/Campaigns/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Tickets(Expression<Func<Ticket, bool>> p) =>
            (await db.Tickets.VisibleTickets(user).Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Number, x.Subject }).ToListAsync())
            .Select(x => Lk(x.Number, x.Subject, $"/Tickets/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> SalesPlans(Expression<Func<SalesPlan, bool>> p) => !seesAll ? none :
            (await db.SalesPlans.Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Number, x.Title }).ToListAsync())
            .Select(x => Lk(x.Number, x.Title, $"/SalesPlans/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> Surveys(Expression<Func<NpsSurvey, bool>> p) => !user.IsSupportDesk() ? none :
            (await db.NpsSurveys.Where(p).OrderByDescending(x => x.Id).Take(Samples).Select(x => new { x.Id, x.Name }).ToListAsync())
            .Select(x => Lk(x.Name, null, $"/Nps/Details/{x.Id}")).ToList();
        async Task<List<ScenarioLink>> AtRisk() =>
            (await db.Customers.VisibleTo(user, db).Where(c => c.HealthScore != null && c.HealthScore < CustomerHealth.WarnFrom).OrderBy(c => c.HealthScore).Take(Samples)
                .Select(c => new { c.Id, c.Name, c.HealthScore }).ToListAsync())
            .Select(x => Lk(x.Name, T("Điểm {0}/100", x.HealthScore), $"/Customers/Details/{x.Id}")).ToList();

        var today = DateTime.Today;
        var flows = new List<ScenarioFlow>();

        // ---------- 1. Lead → khách hàng & cơ hội ----------
        flows.Add(new("lead", "lead", T("Lead → khách hàng & cơ hội"),
            T("Lead vào từ form website, file Excel hoặc nhập tay → hệ thống chấm điểm và phân bổ cho nhân viên → chăm sóc → chuyển đổi thành khách hàng, người liên hệ và cơ hội bán hàng."),
            new()
            {
                new(T("Thu lead"), R(AppRoles.Sales, AppRoles.Manager),
                    T("Lead tạo từ form website (kèm nguồn quảng cáo UTM), nhập Excel hoặc nhập tay. Khi tạo, quản lý có thể chọn \"Tự phân bổ\" để giao theo cấu hình."),
                    await Leads(l => l.Status == LeadStatus.New),
                    new() { Go(T("Tạo lead mới"), "/Leads/Create"), Go(T("Form thu lead website"), "/WebForms"), Go(T("Nhập Excel"), "/Import") }),
                new(T("Chấm điểm & ưu tiên"), R(AppRoles.Manager),
                    T("Điểm lead 0–100 tự tính từ mức độ, nguồn, thông tin, giá trị dự kiến và tương tác. Lọc lead điểm cao để gọi trước; lead mới chưa liên hệ sau N ngày báo quản lý."),
                    await Leads(l => l.Score >= 60 && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted)),
                    new() { Go(T("Cấu hình tự phân bổ lead"), "/Automation/LeadAssign"), Go(T("Quy tắc tự động"), "/Automation") }),
                new(T("Chăm sóc lead"), R(AppRoles.Sales),
                    T("Ghi cuộc gọi, email, cuộc họp; đổi trạng thái Đã liên hệ → Đủ điều kiện. Công việc đến hạn xuất hiện ở \"Công việc của tôi\" và nhắc qua thông báo."),
                    await Leads(l => l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified),
                    new() { Go(T("Công việc"), "/Tasks"), Go(T("Danh sách lead"), "/Leads") }),
                new(T("Chuyển đổi"), R(AppRoles.Sales),
                    T("Lead đủ điều kiện được chuyển thành khách hàng + người liên hệ + cơ hội (giai đoạn Đánh giá); hoạt động cũ chuyển sang khách hàng. Hệ thống cảnh báo nếu trùng khách hàng đã có."),
                    await Leads(l => l.Status == LeadStatus.Converted),
                    new() { Go(T("Kiểm tra trùng"), "/Duplicates") })
            }));

        // ---------- 2. Cơ hội → báo giá → duyệt chiết khấu ----------
        flows.Add(new("sales", "opportunity", T("Cơ hội → báo giá → duyệt chiết khấu"),
            T("Nhân viên theo dõi cơ hội và lập báo giá; chiết khấu vượt hạn mức phải được quản lý duyệt mới gửi được cho khách. Báo giá được chấp nhận thì chuyển sang phương án kinh doanh và hợp đồng."),
            new()
            {
                new(T("Theo dõi cơ hội"), R(AppRoles.Sales),
                    T("Cập nhật giai đoạn, xác suất, ngày dự kiến chốt và nhóm dự báo; kéo thả cơ hội trên bảng Pipeline."),
                    await Opps(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost),
                    new() { Go(T("Pipeline"), "/Opportunities/Pipeline"), Go(T("Tạo cơ hội mới"), "/Opportunities/Create") }),
                new(T("Lập báo giá"), R(AppRoles.Sales),
                    T("Chọn khách hàng, giá tự lấy theo bảng giá của khách; chiết khấu thực tế so với giá chuẩn được tính tự động."),
                    await Quotes(q => q.Status == QuoteStatus.Draft && q.ApprovalStatus == QuoteApproval.None),
                    new() { Go(T("Tạo báo giá"), "/Quotes/Create"), Go(T("Bảng giá & chiết khấu"), "/PriceLists") }),
                new(T("Duyệt chiết khấu"), R(AppRoles.Manager),
                    T("Chiết khấu vượt hạn mức (mặc định 10%) → báo giá chờ duyệt, chưa gửi được cho khách. Quản lý duyệt trong hạn mức của mình, vượt nữa cần quản trị."),
                    await Quotes(q => q.ApprovalStatus == QuoteApproval.Pending),
                    new() { Go(T("Chính sách chiết khấu"), "/PriceLists") }),
                new(T("Gửi báo giá cho khách"), R(AppRoles.Sales),
                    T("Gửi email kèm file PDF tự tạo theo mẫu in; báo giá chuyển sang Đã gửi và ghi vào lịch sử chăm sóc khách hàng."),
                    await Quotes(q => q.Status == QuoteStatus.Sent),
                    new() { Go(T("Mẫu in PDF"), "/Pdf"), Go(T("Email đã gửi"), "/Emails") }),
                new(T("Khách chấp nhận / từ chối"), R(AppRoles.Sales),
                    T("Báo giá được chấp nhận → lập phương án kinh doanh và hợp đồng; bị từ chối → ghi lý do thua trên cơ hội để phân tích."),
                    await Quotes(q => q.Status == QuoteStatus.Accepted || q.Status == QuoteStatus.Rejected),
                    new() { Go(T("Dự báo doanh số"), "/Forecast"), Go(T("Phân tích lý do thua"), "/Reports/Run/lost-reasons") })
            }));

        // ---------- 3. Phương án kinh doanh → hợp đồng ----------
        flows.Add(new("contract", "contract", T("Phương án kinh doanh → hợp đồng"),
            T("Báo giá được chấp nhận → lập phương án kinh doanh (doanh thu, chi phí, lợi nhuận) trình quản lý duyệt → lập hợp đồng với lịch thanh toán theo đợt → ký và thực hiện."),
            new()
            {
                new(T("Lập phương án kinh doanh"), R(AppRoles.Sales),
                    T("Từ báo giá lập phương án: doanh thu, giá vốn, các khoản chi phí, lợi nhuận dự kiến; gửi trình duyệt."),
                    await Plans(p => p.Status == BusinessPlanStatus.Draft),
                    new() { Go(T("Tạo phương án kinh doanh"), "/BusinessPlans/Create") }),
                new(T("Quản lý duyệt phương án"), R(AppRoles.Manager),
                    T("Duyệt hoặc từ chối kèm ý kiến; phương án bị từ chối được sửa và gửi lại."),
                    await Plans(p => p.Status == BusinessPlanStatus.Pending || p.Status == BusinessPlanStatus.Rejected),
                    new() { Go(T("Danh sách phương án"), "/BusinessPlans") }),
                new(T("Lập hợp đồng"), R(AppRoles.Sales),
                    T("Từ báo giá hoặc phương án sinh hợp đồng: hàng hoá, lịch thanh toán theo đợt, điều khoản. Có thể đặt chu kỳ thanh toán định kỳ và gói SLA hỗ trợ."),
                    await Contracts(c => c.Status == ContractStatus.Draft),
                    new() { Go(T("Tạo hợp đồng"), "/Contracts/Create") }),
                new(T("Ký & thực hiện"), R(AppRoles.Sales, AppRoles.Manager),
                    T("Chuyển sang Đang thực hiện khi ký; xuất PDF hợp đồng gửi khách; theo dõi đợt thanh toán để xuất hoá đơn."),
                    await Contracts(c => c.Status == ContractStatus.Active),
                    new() { Go(T("Danh sách hợp đồng"), "/Contracts") }),
                new(T("Hoàn thành / thanh lý"), R(AppRoles.Manager),
                    T("Hợp đồng hoàn thành hoặc thanh lý sau khi nghiệm thu và thu đủ tiền."),
                    await Contracts(c => c.Status == ContractStatus.Completed || c.Status == ContractStatus.Liquidated),
                    new() { Go(T("Hợp đồng sắp hết hạn"), "/Reports/Run/contracts-expiring") })
            }));

        // ---------- 4. Hoá đơn → thu tiền → công nợ ----------
        flows.Add(new("billing", "invoice", T("Hoá đơn → thu tiền → công nợ"),
            T("Từ đợt thanh toán của hợp đồng lập hoá đơn, phát hành, ghi nhận thanh toán và theo dõi công nợ quá hạn."),
            new()
            {
                new(T("Lập hoá đơn theo đợt"), R(AppRoles.Sales, AppRoles.Manager),
                    T("Từ đợt thanh toán của hợp đồng lập hoá đơn nháp; số tiền, thuế và hạn thanh toán lấy theo hợp đồng."),
                    await Invoices(i => i.Status == InvoiceStatus.Draft),
                    new() { Go(T("Tạo hoá đơn"), "/Invoices/Create") }),
                new(T("Phát hành hoá đơn"), R(AppRoles.Manager),
                    T("Phát hành để hoá đơn có hiệu lực và tính công nợ. Xuất PDF \"Đề nghị thanh toán\" gửi khách (không thay thế hoá đơn điện tử)."),
                    await Invoices(i => i.Status == InvoiceStatus.Issued),
                    new() { Go(T("Danh sách hoá đơn"), "/Invoices") }),
                new(T("Ghi nhận thanh toán"), R(AppRoles.Sales, AppRoles.Manager),
                    T("Lập phiếu thu cho hoá đơn: thu một phần hoặc đủ; hệ thống tự cập nhật trạng thái hoá đơn và số tiền còn nợ."),
                    await Invoices(i => i.Status == InvoiceStatus.PartiallyPaid),
                    new() { Go(T("Lập phiếu thu"), "/Payments/Create") }),
                new(T("Phiếu thu gần đây"), R(AppRoles.Manager),
                    T("Hoá đơn thu đủ chuyển sang Đã thanh toán; tiền thu được tính vào KPI và hoa hồng của người phụ trách."),
                    await Payments(),
                    new() { Go(T("Thanh toán"), "/Payments"), Go(T("Hoa hồng"), "/Commissions") }),
                new(T("Theo dõi công nợ"), R(AppRoles.Manager),
                    T("Tuổi nợ phải thu theo khách hàng; hoá đơn quá hạn được quy tắc tự động nhắc người phụ trách thu tiền."),
                    await Invoices(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid) && i.DueDate != null && i.DueDate < today),
                    new() { Go(T("Tuổi nợ phải thu"), "/Reports/Run/ar-aging"), Go(T("Quy tắc tự động"), "/Automation") })
            }));

        // ---------- 5. Gia hạn & thanh toán định kỳ ----------
        flows.Add(new("renewal", "contract", T("Gia hạn & thanh toán định kỳ"),
            T("Hợp đồng dịch vụ thu phí định kỳ: hệ thống tự lập hoá đơn nháp từng kỳ và nhắc gia hạn trước ngày hết hạn."),
            new()
            {
                new(T("Thiết lập định kỳ"), R(AppRoles.Sales, AppRoles.Manager),
                    T("Trên hợp đồng chọn chu kỳ (tháng / quý / năm), số tiền mỗi kỳ, kỳ lập hoá đơn tiếp theo và số ngày nhắc gia hạn."),
                    await Contracts(c => c.BillingCycle != BillingCycle.None),
                    new() { Go(T("Gia hạn & định kỳ"), "/Renewals") }),
                new(T("Hoá đơn nháp tự lập"), R("System"),
                    T("Mỗi giờ hệ thống lập hoá đơn nháp cho kỳ đến hạn (không trùng); kế toán kiểm tra rồi phát hành."),
                    await Invoices(i => i.Status == InvoiceStatus.Draft && i.Contract != null && i.Contract.BillingCycle != BillingCycle.None),
                    new()),
                new(T("Cơ hội gia hạn"), R(AppRoles.Sales),
                    T("Trước ngày hết hạn (mặc định 60 ngày) hệ thống tự tạo cơ hội gia hạn và thông báo người phụ trách."),
                    await Contracts(c => c.RenewalOpportunityId != null),
                    new() { Go(T("Hợp đồng sắp hết hạn"), "/Reports/Run/contracts-expiring") }),
                new(T("Gia hạn hợp đồng"), R(AppRoles.Sales),
                    T("Bấm Gia hạn để tạo hợp đồng nháp nối tiếp hợp đồng cũ; kiểm tra giá, thời hạn rồi chuyển Đang thực hiện khi ký."),
                    await Contracts(c => c.RenewedFromId != null),
                    new())
            }));

        // ---------- 6. Phiếu hỗ trợ & SLA ----------
        flows.Add(new("support", "support", T("Phiếu hỗ trợ & SLA"),
            T("Khách yêu cầu qua điện thoại, email hoặc cổng khách hàng → nhân viên CSKH xử lý trong hạn SLA → giải quyết → khách đánh giá mức hài lòng."),
            new()
            {
                new(T("Tiếp nhận yêu cầu"), R(AppRoles.Support),
                    T("Phiếu tạo từ điện thoại, email (tự nhận qua hộp thư), cổng khách hàng hoặc nhập tay. Hạn phản hồi / giải quyết tính theo chính sách SLA của khách hàng hoặc hợp đồng."),
                    await Tickets(t => t.Status == TicketStatus.New),
                    new() { Go(T("Tạo phiếu hỗ trợ"), "/Tickets/Create"), Go(T("Cổng khách hàng"), "/Portal/Login"), Go(T("Email → phiếu hỗ trợ"), "/Inbound") }),
                new(T("Xử lý"), R(AppRoles.Support),
                    T("Phân công, trả lời khách (gửi kèm email), chèn mẫu trả lời và bài viết kho kiến thức; phiếu sắp / đã trễ SLA báo người xử lý và quản lý."),
                    await Tickets(t => t.Status == TicketStatus.InProgress),
                    new() { Go(T("Kho kiến thức"), "/Kb"), Go(T("SLA & giờ làm việc"), "/Sla") }),
                new(T("Chờ khách phản hồi"), R(AppRoles.Support),
                    T("Đồng hồ SLA tạm dừng; khi khách trả lời (email hoặc cổng) phiếu tự quay lại Đang xử lý."),
                    await Tickets(t => t.Status == TicketStatus.WaitingCustomer),
                    new() { Go(T("Phân công việc"), "/Tickets/Workload") }),
                new(T("Giải quyết"), R(AppRoles.Support),
                    T("Ghi cách giải quyết; phiếu chuyển Đã giải quyết và chờ khách đánh giá."),
                    await Tickets(t => t.Status == TicketStatus.Resolved),
                    new()),
                new(T("Đóng & đánh giá"), R(AppRoles.Support, AppRoles.Manager),
                    T("Khách chấm mức hài lòng (CSAT) và phiếu đóng; quản lý theo dõi chất lượng dịch vụ theo tháng và theo nhân viên."),
                    await Tickets(t => t.Status == TicketStatus.Closed),
                    new() { Go(T("Chất lượng dịch vụ theo tháng"), "/Reports/Run/support-by-month") })
            }));

        // ---------- 7. Chiến dịch tiếp thị ----------
        flows.Add(new("marketing", "campaign", T("Chiến dịch tiếp thị"),
            T("Lập chiến dịch với danh sách đối tượng → gửi email hàng loạt → đối tượng phản hồi trở thành lead / cơ hội → đánh giá hiệu quả và ROI."),
            new()
            {
                new(T("Lập chiến dịch"), R(AppRoles.Manager),
                    T("Đặt mục tiêu, ngân sách, thời gian; chọn đối tượng từ khách hàng, người liên hệ hoặc lead."),
                    await Campaigns(c => c.Status == CampaignStatus.Planned),
                    new() { Go(T("Tạo chiến dịch"), "/Campaigns/Create") }),
                new(T("Gửi email hàng loạt"), R(AppRoles.Manager, AppRoles.Sales),
                    T("Soạn nội dung theo mẫu có biến cá nhân hoá, gửi cho đối tượng chưa tiếp cận; có liên kết từ chối nhận tin."),
                    await Campaigns(c => c.Status == CampaignStatus.Active),
                    new() { Go(T("Email đã gửi"), "/Emails") }),
                new(T("Phản hồi → lead / cơ hội"), R(AppRoles.Sales),
                    T("Đối tượng phản hồi được chuyển thành lead hoặc cơ hội gắn với chiến dịch để theo dõi chuyển đổi."),
                    await Leads(l => l.CampaignId != null && l.Status != LeadStatus.Converted),
                    new() { Go(T("Form thu lead website"), "/WebForms") }),
                new(T("Đánh giá hiệu quả"), R(AppRoles.Manager),
                    T("Doanh số thắng, chi phí và ROI từng chiến dịch; hiệu quả theo nguồn quảng cáo (UTM) trên trang form website."),
                    await Campaigns(c => c.Status == CampaignStatus.Completed),
                    new() { Go(T("Hiệu quả chiến dịch tiếp thị"), "/Reports/Run/campaign-performance") })
            }));

        // ---------- 8. Kế hoạch bán hàng, KPI & hoa hồng ----------
        flows.Add(new("planning", "target", T("Kế hoạch bán hàng, KPI & hoa hồng"),
            T("Quản lý lập kế hoạch năm của đơn vị → được duyệt thì áp thành chỉ tiêu KPI cho nhân viên → theo dõi thực tế, dự báo và tính hoa hồng."),
            new()
            {
                new(T("Lập kế hoạch năm"), R(AppRoles.Manager),
                    T("Gợi ý chỉ tiêu từ lịch sử, chia theo tháng, sản phẩm, nhân viên và kế hoạch hành động; gửi duyệt."),
                    await SalesPlans(p => p.Status == SalesPlanStatus.Draft),
                    new() { Go(T("Tạo kế hoạch bán hàng"), "/SalesPlans/Create") }),
                new(T("Duyệt kế hoạch"), R(AppRoles.Admin),
                    T("Quản trị hoặc quản lý khác duyệt kế hoạch (người lập không tự duyệt); kế hoạch được duyệt áp thành chỉ tiêu KPI."),
                    await SalesPlans(p => p.Status == SalesPlanStatus.Pending || p.Status == SalesPlanStatus.Approved),
                    new() { Go(T("Danh sách kế hoạch"), "/SalesPlans") }),
                new(T("Theo dõi KPI & dự báo"), R(AppRoles.Manager, AppRoles.Sales),
                    T("Doanh số, thu tiền, hợp đồng thực tế so với chỉ tiêu; dự báo chắc chắn / khả quan theo nhóm dự báo của cơ hội."),
                    none,
                    new() { Go(T("KPI bán hàng"), "/Kpi"), Go(T("Dự báo doanh số"), "/Forecast") }),
                new(T("Tính hoa hồng"), R(AppRoles.Manager),
                    T("Hoa hồng = cơ sở tính × tỷ lệ theo bậc % hoàn thành chỉ tiêu; xuất Excel."),
                    none,
                    new() { Go(T("Hoa hồng"), "/Commissions") })
            }));

        // ---------- 9. NPS & sức khoẻ khách hàng ----------
        flows.Add(new("nps", "star", T("Khảo sát NPS & sức khoẻ khách hàng"),
            T("Gửi khảo sát NPS cho người liên hệ → khách bấm điểm ngay trong email → khách phản đối được báo cho người phụ trách → theo dõi điểm sức khoẻ khách hàng."),
            new()
            {
                new(T("Gửi khảo sát"), R(AppRoles.Support),
                    T("Chọn đối tượng, gửi email có thang điểm 0–10 bấm được ngay; mỗi người nhận có liên kết riêng."),
                    await Surveys(s => s.Status == NpsStatus.Sent),
                    new() { Go(T("Tạo đợt khảo sát NPS"), "/Nps/Create") }),
                new(T("Khách trả lời"), R("Customer"),
                    T("Khách bấm điểm và để lại góp ý, không cần đăng nhập; kết quả cộng dồn thành điểm NPS."),
                    await Surveys(s => s.Status != NpsStatus.Draft),
                    new() { Go(T("Khảo sát NPS"), "/Nps") }),
                new(T("Chăm sóc khách phản đối"), R(AppRoles.Sales),
                    T("Khách chấm 6 điểm trở xuống → người phụ trách khách hàng nhận thông báo để liên hệ lại."),
                    none,
                    new() { Go(T("Thông báo"), "/Notifications") }),
                new(T("Theo dõi sức khoẻ khách hàng"), R(AppRoles.Manager, AppRoles.Sales),
                    T("Điểm 0–100 gộp tương tác, hỗ trợ, công nợ, NPS, hợp đồng; khách rơi xuống mức rủi ro được báo người phụ trách."),
                    await AtRisk(),
                    new() { Go(T("Sức khoẻ khách hàng"), "/Health") })
            }));

        // ---------- 10. Quản trị & chất lượng dữ liệu ----------
        flows.Add(new("admin", "key", T("Quản trị & chất lượng dữ liệu"),
            T("Thiết lập hệ thống, nạp dữ liệu, giữ dữ liệu sạch và kiểm soát quyền truy cập."),
            new()
            {
                new(T("Nạp & làm sạch dữ liệu"), R(AppRoles.Admin, AppRoles.Manager),
                    T("Nhập khách hàng, liên hệ, lead từ Excel (ghép cột, chạy thử, chọn bỏ qua / cập nhật / tạo mới); phát hiện và gộp bản ghi trùng; tra cứu MST khi nhập."),
                    none, new() { Go(T("Nhập dữ liệu Excel"), "/Import"), Go(T("Kiểm tra trùng"), "/Duplicates") }),
                new(T("Phân quyền & bảo mật"), R(AppRoles.Admin),
                    T("Ma trận quyền theo vai trò, chính sách mật khẩu, xác thực 2 bước; phân quyền từng báo cáo kèm vùng dữ liệu."),
                    none, new() { Go(T("Bảo mật & phân quyền"), "/Security"), Go(T("Phân quyền báo cáo"), "/Reports/Access"), Go(T("Người dùng"), "/Users") }),
                new(T("Tự động hoá & thông báo"), R(AppRoles.Admin, AppRoles.Manager),
                    T("Quy tắc \"khi … thì …\" tự tạo công việc, thông báo, email; nhãn và trường tùy biến theo đặc thù doanh nghiệp."),
                    none, new() { Go(T("Quy tắc tự động"), "/Automation"), Go(T("Nhãn & trường tùy biến"), "/Tags") }),
                new(T("Theo dõi hệ thống"), R(AppRoles.Admin),
                    T("Nhật ký ai sửa gì, nhật ký đăng nhập, hộp thư đi; cơ cấu công ty / chi nhánh và phạm vi dữ liệu."),
                    none, new() { Go(T("Nhật ký hệ thống"), "/Audit"), Go(T("Công ty & chi nhánh"), "/Branches") })
            }));

        return flows;
    }
}
