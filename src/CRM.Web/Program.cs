using Microsoft.AspNetCore.DataProtection;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Authorization;
using CRM.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

// Ngôn ngữ mặc định ngoài request (khởi tạo dữ liệu, tác vụ nền): tiếng Việt – không phụ thuộc ngôn ngữ hệ điều hành
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo(I18n.Vietnamese);

var builder = WebApplication.CreateBuilder(args);

// ---------- Database (SQL Server) ----------
builder.Services.AddDbContext<CrmDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("CrmConnection")));

// ---------- Authentication: cookie + role ----------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    })
    // Cổng khách hàng: cookie riêng cho người liên hệ của khách hàng (không dùng chung với nhân viên)
    .AddCookie(CRM.Web.Controllers.PortalController.Scheme, o =>
    {
        o.LoginPath = "/Portal/Login";
        o.Cookie.Name = "crm.portal";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });

// Mặc định mọi trang đều yêu cầu đăng nhập (trừ [AllowAnonymous])
builder.Services.AddControllersWithViews(o =>
{
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.Filters.Add(new AuthorizeFilter(policy));
    o.Filters.Add(new SecurityRequirementFilter());   // buộc thiết lập 2 lớp / đổi mật khẩu hết hạn
    o.Filters.Add<PermissionFilter>();   // phân quyền chi tiết theo chức năng
    o.Filters.Add<AutomationFilter>();    // chạy quy tắc tự động sau khi lưu dữ liệu
})
.AddDataAnnotationsLocalization(); // Display / ErrorMessage dịch qua I18nStringLocalizer

// ---------- Đa ngôn ngữ: Tiếng Việt (mặc định) / English ----------
builder.Services.AddLocalization();
builder.Services.Replace(ServiceDescriptor.Singleton<IStringLocalizerFactory, I18nStringLocalizerFactory>());
// Chỉ đổi ngôn ngữ giao diện (UICulture). Culture định dạng/nhập số giữ như cũ (culture của máy chủ)
// để model binding số thập phân không thay đổi hành vi.
var formatCulture = CultureInfo.CurrentCulture;
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture(formatCulture, new CultureInfo(I18n.Vietnamese));
    o.SupportedCultures = new[] { formatCulture };
    o.SupportedUICultures = I18n.SupportedUICultures.Select(c => new CultureInfo(c)).ToList();
    o.RequestCultureProviders = new IRequestCultureProvider[]
    {
        new QueryStringRequestCultureProvider(),
        new CookieRequestCultureProvider()
    };
});

// Lưu khóa mã hóa cookie ra thư mục (volume Docker) để restart container không bị đăng xuất
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("CRM");
}

// ---------- Services ----------
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<BranchDirectory>();
builder.Services.AddSingleton<PermissionService>();
builder.Services.AddScoped<BranchScope>();
builder.Services.AddScoped<UiSettingsService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<KpiService>();
builder.Services.AddScoped<SalesPlanService>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<EmailComposer>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<MergeService>();
builder.Services.AddScoped<AutomationService>();
builder.Services.AddScoped<LeadAssignService>();
builder.Services.AddScoped<RenewalService>();
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<InboundService>();
builder.Services.AddHostedService<InboundWorker>();
builder.Services.AddHostedService<AutomationWorker>();
builder.Services.AddHostedService<EmailWorker>();
builder.Services.AddScoped<CRM.Web.Infrastructure.Reports.CustomReportService>();
builder.Services.AddScoped<CampaignService>();
builder.Services.AddScoped<Customer360Service>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRequestLocalization();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Tạo database + bảng danh mục (luôn chạy, an toàn khi chạy lại); dữ liệu mẫu chỉ nạp khi Database:SeedOnStartup = true.
// Thử lại nhiều lần vì khi chạy bằng Docker, SQL Server có thể khởi động chậm hơn web app.
{
    var seedSample = app.Configuration.GetValue("Database:SeedOnStartup", true);
    // Số khách hàng mẫu khi khởi tạo DB mới (để trống = bộ mẫu mặc định 8 khách hàng)
    int? sampleCustomers = int.TryParse(app.Configuration["Database:SampleCustomers"], out var sc) && sc > 0 ? sc : null;
    var retries = app.Configuration.GetValue("Database:StartupRetries", 12);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await DbSeeder.SeedAsync(app.Services, seedSample, sampleCustomers);
            break;
        }
        catch (Exception ex) when (attempt < retries)
        {
            app.Logger.LogWarning("Chưa kết nối được database (lần {Attempt}/{Max}): {Message}. Thử lại sau 5 giây...",
                attempt, retries, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Không khởi tạo được database. Kiểm tra ConnectionStrings:CrmConnection");
            throw;
        }
    }
}

// Lệnh nạp thêm dữ liệu mẫu rồi thoát (không mở web): dotnet CRM.Web.dll --seed-demo [số khách hàng]
var seedArg = Array.IndexOf(args, "--seed-demo");
if (seedArg >= 0)
{
    var count = seedArg + 1 < args.Length && int.TryParse(args[seedArg + 1], out var c) ? c : 100;
    using var scope = app.Services.CreateScope();
    var r = await DemoDataGenerator.GenerateAsync(scope.ServiceProvider.GetRequiredService<CrmDbContext>(), count);
    app.Logger.LogInformation("Đã nạp dữ liệu mẫu: {Result}", r);
    return;
}

// Chỉ nạp chiến dịch tiếp thị mẫu trên dữ liệu đang có rồi thoát: dotnet CRM.Web.dll --seed-marketing [số chiến dịch]
var mktArg = Array.IndexOf(args, "--seed-marketing");
if (mktArg >= 0)
{
    var count = mktArg + 1 < args.Length && int.TryParse(args[mktArg + 1], out var c) ? c : 8;
    using var scope = app.Services.CreateScope();
    var r = await DemoDataGenerator.GenerateMarketingAsync(scope.ServiceProvider.GetRequiredService<CrmDbContext>(), count);
    app.Logger.LogInformation("Đã nạp dữ liệu tiếp thị: {Campaigns} chiến dịch, {Members} đối tượng, gắn {Leads} lead và {Opps} cơ hội",
        r.Campaigns, r.Members, r.Leads, r.Opportunities);
    return;
}

// Chỉ nạp phiếu hỗ trợ mẫu trên dữ liệu đang có rồi thoát: dotnet CRM.Web.dll --seed-support [số phiếu]
var supArg = Array.IndexOf(args, "--seed-support");
if (supArg >= 0)
{
    var count = supArg + 1 < args.Length && int.TryParse(args[supArg + 1], out var c) ? c : 50;
    using var scope = app.Services.CreateScope();
    var r = await DemoDataGenerator.GenerateSupportAsync(scope.ServiceProvider.GetRequiredService<CrmDbContext>(), count);
    app.Logger.LogInformation("Đã nạp phiếu hỗ trợ: {Tickets} phiếu ({Open} đang mở, {Closed} đã đóng), {Comments} lượt trao đổi", r.Tickets, r.Open, r.Closed, r.Comments);
    return;
}

app.Run();
