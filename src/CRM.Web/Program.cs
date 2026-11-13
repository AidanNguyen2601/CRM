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
    });

// Mặc định mọi trang đều yêu cầu đăng nhập (trừ [AllowAnonymous])
builder.Services.AddControllersWithViews(o =>
{
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.Filters.Add(new AuthorizeFilter(policy));
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
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<KpiService>();

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
    var retries = app.Configuration.GetValue("Database:StartupRetries", 12);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await DbSeeder.SeedAsync(app.Services, seedSample);
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

app.Run();
