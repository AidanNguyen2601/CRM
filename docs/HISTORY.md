# Lịch sử xây dựng Sổ Sức Khỏe

Tóm tắt các phiên bản, quyết định thiết kế và việc còn dở. Mục đích: ai (hoặc Claude ở tài khoản khác) đọc file này cùng `CLAUDE.md` và `README.md` là làm tiếp được, không cần lịch sử chat.

## Bối cảnh

- Chủ dự án: PM mảng HRM/ERP, không chuyên code, dùng dự án vừa để tự theo dõi sức khỏe (tăng huyết áp, rối loạn lipid máu) vừa để học domain HIS.
- Chạy trên máy cá nhân Windows bằng Docker Desktop, chưa có VPS. Chỉ truy cập từ `127.0.0.1`.
- Từ 08/11/2026: code quản lý bằng Git (kho GitHub riêng tư), sửa trực tiếp trong VS Code với Claude Code. **Không còn** gửi file zip rồi giải nén đè.

## Các phiên bản

| Bản | Ngày | Nội dung chính |
|---|---|---|
| v1 | 05/11/2026 | ASP.NET Core 8 MVC + SQL Server trong Docker. 4 module: sinh hiệu, đơn thuốc & uống thuốc, xét nghiệm & tái khám, ăn uống & tập luyện. Một bệnh nhân |
| v2 | 06/11/2026 | Nhiều bệnh nhân; 7 danh mục (loại bệnh nhân, thuốc, ICD-10, loại xét nghiệm có khoảng tham chiếu, cơ sở khám, bác sĩ, hoạt động); ngưỡng huyết áp/BMI/nhịp tim cấu hình trên giao diện; thêm/sửa/xoá mọi form; đọc huyết áp từ ảnh máy đo (Claude API); đăng nhập + phân quyền theo tính năng × loại bệnh nhân. **Phải tạo lại database** |
| v3 | 07/11/2026 | Thông báo qua Gmail SMTP: nhắc giờ uống thuốc, cảnh báo quá giờ chưa uống, nhắc tái khám trước N ngày, báo sắp hết thuốc. `SchemaUpgrader` để nâng cấp DB không mất dữ liệu |
| v4 | 07/11/2026 | Tuỳ chỉnh giao diện theo người dùng (menu trên/dưới/trái/phải, sáng/tối, màu, font, cỡ chữ), trang Yêu thích, tự đăng ký tài khoản (mặc định tắt), cải thiện đổi mật khẩu |
| v5 | 07/11/2026 | Sao lưu & khôi phục database trong app (thủ công, tự động hằng ngày, tải về, tải lên, bản an toàn trước khi khôi phục). Volume `db-backups` |
| — | 08/11/2026 | Chuyển sang Git + VS Code + Claude Code. Thêm `CLAUDE.md`, `name: healthtracker` trong `docker-compose.yml` |

## Quyết định thiết kế (và lý do)

- **Monolith MVC, Razor Views**: đơn giản, một project, dễ học; không tách frontend.
- **`EnsureCreated` + `SchemaUpgrader` (script SQL tự viết, có kiểm tra trước)** thay vì EF Migrations: lúc đầu không có NuGet để sinh migration. Hệ quả: mỗi lần thêm bảng/cột phải thêm bước vào `SchemaUpgrader`. Việc nên làm tiếp: chuyển sang EF Core Migrations.
- **Phân quyền tự xây** (cookie auth + `PermissionFilter` + mã quyền `Feature.View/Edit`), không dùng ASP.NET Identity đầy đủ, để dễ hiểu và đủ dùng.
- **Ngưỡng đánh giá lưu trong DB** (`ThresholdRules`), logic phân loại ở `ThresholdSet` thuần C#. Huyết áp: đánh giá riêng tâm thu/tâm trương, lấy mức nặng hơn. Mục tiêu mặc định < 130/80; BMI theo chuẩn châu Á.
- **Nhắc nhở**: `ReminderPlanner` thuần C# quyết định gửi gì; `NotificationLogs.DedupKey` duy nhất để không gửi trùng; gửi lỗi thử lại tối đa 3 lần. Chỉ gửi khi app đang chạy.
- **Đọc ảnh huyết áp**: interface `IBpImageReader`, bản hiện tại gọi Claude API; không lưu ảnh; người dùng luôn kiểm tra trước khi lưu.
- **Sao lưu**: lệnh `BACKUP/RESTORE` của SQL Server qua volume dùng chung; tên file theo mẫu cố định để chống truy cập đường dẫn lạ.
- **Bảo mật cơ bản**: cổng 8080/1433 chỉ mở trên `127.0.0.1`; `.env` và `*.bak` nằm trong `.gitignore`.

## Bài học khi vận hành

- Cài Docker Desktop trên máy Intel: chọn bản **AMD64**.
- SSMS kết nối bằng `127.0.0.1,1433` (dùng `localhost` bị lỗi 1225 do IPv6).
- Giải nén đè làm sót file cũ → lỗi trùng class khi build. Đây là lý do chuyển sang Git.
- Đổi tên thư mục dự án làm Docker tạo database mới → đã cố định bằng `name: healthtracker`.
- VS Code cần cài Git for Windows riêng (Git trong GitHub Desktop không dùng chung được).

## Việc còn dở / ý tưởng tiếp theo

1. Chuyển sang **EF Core Migrations**.
2. **Unit test** (xUnit) cho `ThresholdSet`, `ReminderPlanner`, `BackupNaming`, `ParseReading`.
3. **Báo cáo tái khám** 4 tuần (in mang cho bác sĩ).
4. Theo dõi thêm viêm gan B: thêm thuốc Tenofovir vào danh mục, mã ICD **B18.1**; theo dõi định kỳ creatinine/eGFR, kali, phosphat, men gan, HBV DNA ở mục Xét nghiệm.
5. **Audit log** (ai sửa gì, khi nào).
6. API theo chuẩn **HL7 FHIR** (`Observation`, `MedicationRequest`).
7. Khi cần dùng trên điện thoại: VPS + HTTPS, sau đó PWA + Web Push.

## Tài liệu liên quan

- `CLAUDE.md`: quy tắc cho Claude Code khi sửa code.
- `README.md`: cài đặt, kiến trúc, đối chiếu FHIR, xử lý lỗi.
- File Word "Hướng dẫn cài đặt Sổ Sức Khỏe" (bản 5): hướng dẫn cho người dùng.
