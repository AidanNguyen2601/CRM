# CRM System — ASP.NET Core 8 MVC + SQL Server

Hệ thống quản lý quan hệ khách hàng (CRM) cơ bản cho đội kinh doanh B2B: từ lead → khách hàng → cơ hội → báo giá → chốt hợp đồng, kèm lịch chăm sóc và dashboard doanh số.

## 1. Tính năng

| Module | Chức năng chính |
|---|---|
| Đăng nhập & phân quyền | 3 vai trò: Quản trị, Quản lý kinh doanh, Nhân viên kinh doanh. Sales chỉ thấy dữ liệu mình phụ trách; Quản lý/Quản trị thấy toàn bộ và phân công người phụ trách |
| Dashboard | Pipeline đang mở (tổng & trọng số theo xác suất), doanh số thắng trong tháng, tỷ lệ thắng, lead mới 30 ngày và tỷ lệ chuyển đổi, pipeline theo giai đoạn, doanh số 6 tháng, lead theo nguồn, cơ hội sắp chốt, việc cần làm |
| Lead | Quản lý khách hàng tiềm năng theo nguồn, mức độ (nóng/ấm/lạnh), trạng thái; **chuyển đổi lead** thành khách hàng + người liên hệ + cơ hội (gợi ý khách trùng tên, chuyển luôn lịch sử chăm sóc) |
| Khách hàng | Hồ sơ doanh nghiệp, phân loại (tiềm năng/khách hàng/đối tác), kiểm tra trùng mã & MST; trang 360° gồm liên hệ, cơ hội, báo giá, lịch sử chăm sóc, doanh số đã thắng |
| Người liên hệ | Nhiều liên hệ mỗi khách hàng, đánh dấu liên hệ chính |
| Cơ hội bán hàng | 4 giai đoạn mở + Thắng/Thua, xác suất mặc định theo giai đoạn, bắt buộc lý do thua; **Pipeline Kanban** đổi giai đoạn ngay trên thẻ; thắng cơ hội tự nâng khách tiềm năng thành khách hàng |
| Hoạt động | Cuộc gọi, cuộc họp, email, công việc; lọc hôm nay / quá hạn / đã xong; hoàn thành kèm kết quả; timeline trên từng khách hàng, lead, cơ hội |
| Sản phẩm / dịch vụ | Danh mục, đơn vị tính, đơn giá (Quản lý cập nhật) |
| Báo giá | Nhiều dòng sản phẩm, chiết khấu từng dòng + chiết khấu chung, VAT 0/5/8/10%, tính tiền trực tiếp trên form; gửi → khách chấp nhận (tự cập nhật giá trị & giai đoạn cơ hội) / từ chối; nhân bản; in / lưu PDF |
| Người dùng | Tạo tài khoản, vai trò, khóa/mở, thống kê số lead/cơ hội/khách hàng đang phụ trách |

## 2. Cấu trúc (MVC)

```
CRM.sln
src/CRM.Web/
├── Controllers/   Account, Home (Dashboard), Leads, Customers, Contacts, Opportunities,
│                  Activities, Products, Quotes, Users  (+ CrmControllerBase: dropdown, phân quyền)
├── Models/Entities/   AppUser, Customer, Contact, Lead, Opportunity, Activity, Product, Quote, QuoteLine
├── ViewModels/        dữ liệu riêng cho từng màn hình
├── Views/             Razor + Bootstrap 5, tiếng Việt
├── Data/              CrmDbContext (EF Core), DbSeeder (dữ liệu mẫu)
├── Infrastructure/    định dạng tiền VNĐ, lọc dữ liệu theo người phụ trách, phân trang
└── appsettings.json
Dockerfile, docker-compose.yml (PA A), docker-compose.shared-db.yml (PA B)
deploy-crm-shared-db.ps1 + run-crm-B.cmd   (tự triển khai PA B trên Windows)
```

## 3. Chạy trên máy (Visual Studio / dotnet)

1. Sửa `ConnectionStrings:CrmConnection` trong `src/CRM.Web/appsettings.json`.
2. `cd src/CRM.Web && dotnet run` (hoặc mở `CRM.sln`, F5).
3. Lần đầu ứng dụng tự tạo database `CRM_DB` và dữ liệu mẫu (4 người dùng, 8 khách hàng, 10 lead, 12 cơ hội, 3 báo giá, ~24 hoạt động).

| Tài khoản | Mật khẩu | Vai trò |
|---|---|---|
| admin | Admin@123 | Quản trị hệ thống |
| manager | Manager@123 | Quản lý kinh doanh |
| sales1 | Sales@123 | Nhân viên kinh doanh |
| sales2 | Sales@123 | Nhân viên kinh doanh |

## 4. Chạy bằng Docker

**Phương án B – dùng chung SQL Server đang chạy (giống HRM):** double-click `run-crm-B.cmd`. Script tự tìm container SQL Server, network, mật khẩu SA; tạo login riêng `crm_app` (chỉ quyền tạo database); chọn port trống từ 8100; ghi `.env`; build và chạy `crm-web`; mở trình duyệt. Log ghi ở `deploy.log`.

**Phương án A – SQL Server riêng:**
```bash
copy .env.example .env      # sửa port / mật khẩu nếu cần
docker compose up -d --build
```
Web: `http://localhost:8100`, SQL: `localhost,1435`.

Không trùng với HRM / Healthcare: project `crm`, container `crm-web` / `crm-sqlserver`, network `crm-net`, database `CRM_DB`, port 8100/1435.

**Đóng gói cài đặt cho máy khác (không kèm mã nguồn):**
```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\build-package.ps1 -Version 1.0.0
```
Tạo `dist\crm-1.0.0.zip` (~560 MB) gồm image ứng dụng đã biên dịch + SQL Server + script `install.ps1` / `install.sh`, `backup.ps1`, `restore.ps1`, `uninstall.ps1` và `HUONG-DAN.txt`. Máy đích chỉ cần Docker, không cần Internet: giải nén → `install.ps1` (mặc định 5 khách hàng mẫu, tự sinh mật khẩu SQL). Thêm `-SkipSqlImage` để gói nhẹ (~100 MB) nếu máy đích có Internet. Mã nguồn các script gói nằm trong `deploy\package\`.

## 5. Ghi chú kỹ thuật

- Database tạo bằng `EnsureCreated()`; khi lên môi trường thật nên chuyển sang EF Migrations: `dotnet ef migrations add InitialCreate`, rồi đổi `EnsureCreatedAsync()` thành `MigrateAsync()` trong `DbSeeder`.
- Tắt dữ liệu mẫu: `Database__SeedOnStartup=false`.
- Tiền tệ lưu `decimal(18,0)` (VNĐ). Xác suất mặc định = giá trị enum giai đoạn (10/25/50/75/100/0%).
- Mọi khóa ngoại `ON DELETE NO ACTION`; xóa khách hàng / cơ hội bị chặn khi đã có dữ liệu liên quan.

## 6. Hướng mở rộng

Hợp đồng & công nợ, chiến dịch marketing + email hàng loạt, phân bổ lead tự động, tích hợp tổng đài / Zalo OA, nhắc việc qua email, báo cáo KPI theo nhân viên, import Excel, Web API cho mobile, đồng bộ khách hàng với HRM/ERP.
