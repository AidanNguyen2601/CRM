# Lịch sử xây dựng CRM

Tóm tắt các phiên bản, quyết định thiết kế và việc còn dở. Mục đích: ai (hoặc Claude ở tài khoản khác) đọc file này cùng `README.md` là làm tiếp được, không cần lịch sử chat.

## Bối cảnh

- Chủ dự án: PM mảng HRM/ERP, không chuyên code. CRM là một trong bộ ứng dụng tự xây (HRM, CRM, ERP, E-Procurement, Quản lý dự án, Sổ Sức Khỏe), cùng một khuôn: **ASP.NET Core 8 MVC + Razor + EF Core + SQL Server, chạy bằng Docker** trên máy Windows cá nhân.
- Đối tượng: đội kinh doanh B2B. Luồng chính: Lead → Khách hàng → Cơ hội → Báo giá / Phương án kinh doanh → Hợp đồng → Hoá đơn → Thanh toán, kèm chăm sóc, tiếp thị, KPI.
- Triển khai trên máy hiện tại theo **Phương án B**: dùng chung SQL Server của Sổ Sức Khỏe (container `healthtracker-db`, network `healthtracker_default`), login riêng `crm_app`, database `CRM_DB`, web `crm-web` ở cổng **8100**. Chạy/cập nhật bằng `run-crm-B.cmd`.
- Code quản lý bằng Git, sửa trong VS Code với Claude Code. Không có `CLAUDE.md` riêng cho repo này.

## Các phiên bản

| Ngày | Commit | Nội dung chính |
|---|---|---|
| 12/11/2026 | Khởi tạo | 3 vai trò (Quản trị, Quản lý KD, Nhân viên KD – sales chỉ thấy dữ liệu mình phụ trách). Dashboard, Lead (chuyển đổi thành khách hàng + liên hệ + cơ hội), Khách hàng, Người liên hệ, Cơ hội + Pipeline Kanban, Hoạt động, Sản phẩm, Báo giá (in/PDF), Người dùng. Docker PA A (SQL riêng) và PA B (dùng chung SQL) |
| 12/11 | Thêm ngôn ngữ | Song ngữ Việt / Anh (`I18n`), **danh mục dùng chung** (ngành, tỉnh/thành, nhóm SP, ĐVT, lý do thua…) quản lý trên giao diện, `CatalogService` có cache |
| 13/11 | Thêm Icon, Menu | Bộ icon SVG nội tuyến (`Icons.cs`); menu trái rút gọn chỉ còn icon, mở/thu từng nhóm, nhớ trạng thái trong trình duyệt |
| 13/11 | Phương án kinh doanh | PAKD: giá vốn – giá bán – chi phí – lợi nhuận; Nháp → Chờ duyệt → Đã duyệt / Bị từ chối (Quản lý duyệt, từ chối bắt buộc ý kiến); lập từ cơ hội hoặc báo giá. Bắt đầu có `SchemaUpgrader` |
| 13/11 | Hợp đồng → Hoá đơn → Thanh toán | Hợp đồng có lịch thanh toán theo đợt (% giá trị); ký hợp đồng → cơ hội chuyển Thắng. Hoá đơn theo đợt / theo hợp đồng / lẻ, số 8 chữ số theo ký hiệu; phát hành → công nợ. Phiếu thu tự cập nhật số đã thu & trạng thái hoá đơn, không sửa được (sai thì xoá lập lại). Sản phẩm thêm giá vốn, VAT, hãng, xuất xứ, bảo hành |
| 13/11 | Chỉnh sửa dạng grid | Bảng danh sách kéo thả đổi thứ tự cột, kéo mép đổi độ rộng, lưu theo trình duyệt; phân trang chọn số dòng |
| 13/11 | Thêm data | Trang **Dữ liệu mẫu** (chỉ Quản trị): sinh thêm 20/50/100/200 khách hàng kèm trọn chuỗi nghiệp vụ trong 12 tháng (`DemoDataGenerator`, nạp thêm, không xoá dữ liệu cũ) |
| 13/11 | Thêm mã code | Danh mục có cột **Mã** (tự gợi ý từ tên: "Hà Nội" → HN), duy nhất trong nhóm |
| 13/11 | File đính kèm | Đính kèm cho khách hàng, lead, cơ hội, báo giá, PAKD, hợp đồng, hoá đơn, phiếu thu, hoạt động, sản phẩm. Lưu đĩa ngoài `wwwroot` (tên GUID), quyền theo bản ghi gốc; volume Docker riêng |
| 13/11 | Thêm KPI | Giao chỉ tiêu tháng cho nhân viên (quý/năm = tổng tháng). 5 chỉ tiêu: doanh số, thu tiền, hợp đồng ký mới, khách hàng mới, hoạt động hoàn thành – thực tế tự lấy từ nghiệp vụ. Màu theo tiến độ thời gian của kỳ; dự báo từ pipeline trọng số |
| 14/11 | Deploy | Gói cài đặt cho máy khác không kèm mã nguồn (`deploy/build-package.ps1` → `dist/crm-x.y.z.zip`: image + SQL + install/backup/restore/uninstall). `Database:SampleCustomers` để cài mới với N khách hàng mẫu cố định. Script PA B tự đợi / khởi động SQL Server |
| 14/11 | Tiếp thị khách hàng | Chiến dịch theo kênh, ngân sách / chi phí / doanh số kỳ vọng; lập danh sách đối tượng từ phân khúc khách hàng hoặc lead (bỏ trùng, bỏ thiếu email/SĐT, loại người từ chối nhận tin); cập nhật trạng thái hàng loạt; xuất CSV; gắn lead / cơ hội với chiến dịch → phễu, chi phí/lead, ROI |
| 14/11 | Add data Campaign | Nạp chiến dịch mẫu trên dữ liệu đang có (giao diện hoặc `dotnet CRM.Web.dll --seed-marketing [n]`) |
| 16/11 | Thêm docs | Thêm thư mục `docs` (bản đầu chép nhầm lịch sử của Sổ Sức Khỏe – đã viết lại thành file này) |
| 16/11 | *(chưa commit)* Khách hàng 360° | Trang chi tiết khách hàng viết lại thành 7 tab: Tổng quan, Dòng thời gian, Bán hàng, Hợp đồng & tài chính, Sản phẩm đã mua, Liên hệ & tiếp thị, Tài liệu. **Điểm sức khỏe 0–100**, mục **Cần chú ý**, dòng thời gian hợp nhất có lọc, biểu đồ xuất HĐ – thu tiền 12 tháng. Không đổi database |

## Quyết định thiết kế (và lý do)

- **Monolith MVC, Razor Views, Bootstrap 5**: một project, dễ học, không tách frontend. Biểu đồ vẽ bằng CSS/HTML thuần (không dùng thư viện chart) để không phụ thuộc CDN.
- **`EnsureCreated` + `SchemaUpgrader`** thay vì EF Migrations: `EnsureCreated` không thêm bảng vào DB đã có, nên mỗi tính năng có bảng/cột mới phải thêm một bước SQL vào `Data/SchemaUpgrader.cs` (kiểm tra tồn tại trước, chạy lại nhiều lần vẫn an toàn). **Quên bước này thì DB cũ sẽ lỗi khi chạy bản mới.**
- **Phân quyền tự xây** bằng cookie auth + 3 vai trò. Dữ liệu có người phụ trách (`IOwned`): nhân viên KD chỉ thấy của mình (`VisibleTo`, `CanAccess`); Quản lý / Quản trị thấy hết. Trang 360° coi như ai xem được khách hàng thì xem được toàn bộ dữ liệu liên quan.
- **Đa ngôn ngữ bằng từ điển trong code** (`Infrastructure/I18n.cs`): chuỗi tiếng Việt chính là khoá; thiếu bản dịch thì hiện tiếng Việt. Định dạng số/ngày luôn vi-VN, chỉ đổi ngôn ngữ giao diện. Thêm chuỗi mới nhớ thêm bản dịch tiếng Anh.
- **Danh mục lưu giá trị là tên tiếng Việt** (không lưu Id) để dữ liệu cũ không mất khi đổi/xoá mục danh mục; hiển thị qua `Catalogs.Text(...)` theo ngôn ngữ.
- **Tiền VNĐ `decimal(18,0)`**, khoá ngoại `ON DELETE NO ACTION`; xoá bị chặn khi đã có dữ liệu liên quan, khách hàng thì chuyển sang "Ngừng giao dịch".
- **Chứng từ**: hoá đơn chỉ huỷ được khi chưa thu tiền; phiếu thu không sửa. Công nợ = hoá đơn đã phát hành − đã thu; quá hạn theo hạn thanh toán.
- **KPI** tính thực tế cho **người phụ trách** (thu tiền: ưu tiên người phụ trách hợp đồng → hoá đơn → người lập phiếu); điểm tổng = trung bình % hoàn thành, mỗi chỉ tiêu tối đa 150%.
- **Khách hàng 360°**: logic ở `Infrastructure/Customer360Service.cs`; điểm sức khỏe là hàm thuần C# `CustomerHealth.Evaluate` (tương tác gần đây 30, thanh toán đúng hạn 25, doanh thu 12 tháng 20 – lấy max(hoá đơn, cơ hội thắng), cơ hội đang mở 15, chăm sóc đúng hẹn 10; ≥ 70 Tốt, 40–69 Cần chú ý, < 40 Rủi ro). Khách "Ngừng giao dịch" không bị cảnh báo lâu không tương tác.
- **Dữ liệu mẫu** sinh bằng chạy qua nghiệp vụ thật, seed cố định để lần nào cũng giống nhau; luôn là nạp thêm.
- **Đặt tên tách biệt** với các app khác: project `crm`, container `crm-web` / `crm-sqlserver`, DB `CRM_DB`, login `crm_app`, cổng 8100 / 1435.

## Bài học khi vận hành

- PA B phụ thuộc container SQL của Sổ Sức Khỏe: nếu container đó dừng, CRM không lên. Script deploy đã tự đợi tối đa 60 giây và hỏi khởi động container đang dừng.
- Sửa code xong phải **chạy lại `run-crm-B.cmd`** (build lại image) thì `crm-web` mới có bản mới.
- Trên máy này, Git Bash tự đổi đường dẫn khi gọi `docker exec ... /opt/...` → thêm `MSYS_NO_PATHCONV=1` (hoặc chạy từ PowerShell). Máy không cài Python.
- Muốn thử tính năng có dữ liệu đầy đủ mà không đụng DB thật: chạy app trỏ vào một database tạm (ví dụ `CRM_360TEST`, `crm_app` có quyền tạo DB), nạp dữ liệu mẫu ở trang Dữ liệu mẫu, kiểm tra xong thì xoá DB tạm.
- DB thật `CRM_DB` (tại 16/11/2026) chưa có hợp đồng / hoá đơn / thanh toán, nên các phần tài chính, KPI thu tiền, sản phẩm đã mua còn trống.

## Việc còn dở / ý tưởng tiếp theo

1. Commit tính năng **Khách hàng 360°** và chạy lại `run-crm-B.cmd`.
2. Chuyển sang **EF Core Migrations** thay cho `SchemaUpgrader`.
3. **Unit test** (xUnit) cho `CustomerHealth`, `KpiService`, `CatalogCode`, tính tiền hợp đồng / hoá đơn.
4. Thêm `CLAUDE.md` (quy tắc khi sửa code: luôn thêm bước `SchemaUpgrader`, bản dịch `I18n`, icon trong `Icons.cs`).
5. README mục 2 (cấu trúc) và mục 6 (hướng mở rộng) chưa cập nhật theo các tính năng đã làm sau bản khởi tạo.
6. Ý tưởng: nhắc việc / cảnh báo 360° qua email, phân bổ lead tự động, import Excel, tích hợp Zalo OA / tổng đài, audit log, Web API cho mobile, đồng bộ khách hàng – đơn bán với ERP và nhân sự với HRM.

## Tài liệu liên quan

- `README.md`: tính năng, cấu trúc, cách chạy, Docker PA A/B, đóng gói cài đặt.
- `deploy/package/HUONG-DAN.txt`, `HUONG-DAN-CAI-DAT.html`: hướng dẫn cài cho máy khác.
