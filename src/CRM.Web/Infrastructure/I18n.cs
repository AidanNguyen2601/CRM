using System.Globalization;
using Microsoft.Extensions.Localization;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Đa ngôn ngữ Việt / Anh. Văn bản gốc trong code là tiếng Việt và được dùng làm khóa tra từ điển;
/// khi UI culture là tiếng Anh thì trả về bản dịch (không có bản dịch → giữ nguyên tiếng Việt).
/// Định dạng số/ngày (Culture) luôn là vi-VN, chỉ UICulture thay đổi theo lựa chọn người dùng.
/// </summary>
public static class I18n
{
    public const string Vietnamese = "vi-VN";
    public const string English = "en-US";
    public static readonly string[] SupportedUICultures = { Vietnamese, English };

    public static bool IsEnglish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en";
    public static string Lang => IsEnglish ? "en" : "vi";

    public static string T(string vi) =>
        IsEnglish && En.TryGetValue(vi, out var en) ? en : vi;

    public static string T(string vi, params object?[] args) => string.Format(T(vi), args);

    /// <summary>Bản dịch viết trực tiếp tại chỗ (dùng khi cùng câu tiếng Việt nhưng tiếng Anh khác theo ngữ cảnh).</summary>
    public static string L(string vi, string en) => IsEnglish ? en : vi;

    public static bool HasTranslation(string vi) => En.ContainsKey(vi);

    private static readonly Dictionary<string, string> En = new()
    {
        // ---------- Layout / chung ----------
        ["Tổng quan"] = "Overview",
        ["Lịch làm việc"] = "Schedule",
        ["Bán hàng"] = "Sales",
        ["Lead (KH tiềm năng)"] = "Leads",
        ["Cơ hội bán hàng"] = "Opportunities",
        ["Báo giá"] = "Quotes",
        ["Khách hàng"] = "Customer",
        ["Người liên hệ"] = "Contact",
        ["Danh mục"] = "Catalog",
        ["Sản phẩm / dịch vụ"] = "Products / services",
        ["Người dùng & phân quyền"] = "Users & permissions",
        ["+ Tạo nhanh"] = "+ Quick create",
        ["Cơ hội"] = "Opportunity",
        ["Hoạt động"] = "Activity",
        ["Đổi mật khẩu"] = "Change password",
        ["Đăng xuất"] = "Log out",
        ["Ngôn ngữ"] = "Language",
        ["Tiếng Việt"] = "Vietnamese",
        ["Tìm kiếm"] = "Search",
        ["Lọc"] = "Filter",
        ["-- Tất cả --"] = "-- All --",
        ["Tất cả"] = "All",
        ["Phụ trách"] = "Owner",
        ["Trạng thái"] = "Status",
        ["Lưu"] = "Save",
        ["Hủy"] = "Cancel",
        ["Sửa"] = "Edit",
        ["Xóa"] = "Delete",
        ["Ngày tạo"] = "Created",
        ["Điện thoại"] = "Phone",
        ["Liên hệ"] = "Contact",
        ["Nguồn"] = "Source",
        ["Giá trị"] = "Amount",
        ["Giá trị dự kiến"] = "Estimated value",
        ["Giai đoạn"] = "Stage",
        ["Dự kiến chốt"] = "Expected close",
        ["Ngày"] = "Date",
        ["Mã"] = "Code",
        ["Họ tên"] = "Name",
        ["Chức vụ"] = "Job title",
        ["Chính"] = "Primary",
        ["Loại"] = "Type",
        ["Tiêu đề"] = "Subject",
        ["Thời hạn"] = "Due",
        ["Ưu tiên"] = "Priority",
        ["Mức độ"] = "Rating",
        ["Phân loại"] = "Type",
        ["Không có."] = "None.",
        ["-- Chọn --"] = "-- Select --",
        ["-- Chọn khách hàng --"] = "-- Select customer --",
        ["-- Không --"] = "-- None --",
        ["+ Hoạt động"] = "+ Activity",
        ["+ Báo giá"] = "+ Quote",
        ["+ Cơ hội"] = "+ Opportunity",
        ["+ Liên hệ"] = "+ Contact",
        ["✓ Xong"] = "✓ Done",
        ["Quá hạn"] = "Overdue",
        ["Lịch sử chăm sóc"] = "Activity history",
        ["Chưa có hoạt động."] = "No activities yet.",
        ["Chưa có báo giá."] = "No quotes yet.",
        ["Chưa có cơ hội."] = "No opportunities yet.",
        ["Số báo giá"] = "Quote no.",
        ["Tổng thanh toán"] = "Grand total",
        ["Lỗi"] = "Error",
        ["Đã có lỗi xảy ra trong quá trình xử lý."] = "An error occurred while processing your request.",
        ["Mã yêu cầu:"] = "Request ID:",

        // ---------- Tài khoản ----------
        ["Không có quyền truy cập"] = "Access denied",
        ["Bạn không có quyền truy cập chức năng này"] = "You do not have permission to access this feature",
        ["Liên hệ quản trị hệ thống nếu bạn cần được cấp quyền."] = "Contact your system administrator if you need access.",
        ["Về Dashboard"] = "Back to Dashboard",
        ["Cập nhật"] = "Update",
        ["Đăng nhập"] = "Log in",
        ["Đăng nhập để tiếp tục"] = "Sign in to continue",
        ["Tài khoản demo:"] = "Demo accounts:",
        ["Tên đăng nhập hoặc mật khẩu không đúng, hoặc tài khoản đã bị khóa."] = "Invalid username or password, or the account is locked.",
        ["Mật khẩu hiện tại không đúng"] = "Current password is incorrect",
        ["Đổi mật khẩu thành công."] = "Password changed successfully.",

        // ---------- Dashboard ----------
        ["Dashboard kinh doanh"] = "Sales dashboard",
        ["Dashboard của tôi"] = "My dashboard",
        ["Pipeline đang mở"] = "Open pipeline",
        ["{0} cơ hội · trọng số {1}"] = "{0} opportunities · weighted {1}",
        ["Doanh số thắng tháng này"] = "Won this month",
        ["{0} hợp đồng · tỷ lệ thắng {1}%"] = "{0} deals · win rate {1}%",
        ["Lead mới (30 ngày)"] = "New leads (30 days)",
        ["Tỷ lệ chuyển đổi {0}%"] = "Conversion rate {0}%",
        ["Hoạt động quá hạn"] = "Overdue activities",
        ["Xem danh sách →"] = "View list →",
        ["Pipeline theo giai đoạn"] = "Pipeline by stage",
        ["Mở Kanban"] = "Open Kanban",
        ["Doanh số thắng 6 tháng gần nhất"] = "Won revenue, last 6 months",
        ["Cơ hội dự kiến chốt trong 30 ngày"] = "Opportunities closing within 30 days",
        ["Lead theo nguồn"] = "Leads by source",
        ["Việc cần làm"] = "To-do",
        ["Không có việc đang chờ."] = "No pending tasks.",

        // ---------- Lead ----------
        ["Lead – khách hàng tiềm năng"] = "Leads – prospective customers",
        ["Tên, công ty, email, SĐT"] = "Name, company, email, phone",
        ["Đang theo đuổi"] = "In progress",
        ["+ Thêm lead"] = "+ Add lead",
        ["Công ty"] = "Company",
        ["Không có lead."] = "No leads.",
        ["Tổng: {0} lead"] = "Total: {0} leads",
        ["Thêm lead"] = "Add lead",
        ["Sửa lead: {0}"] = "Edit lead: {0}",
        ["Lead: {0}"] = "Lead: {0}",
        ["Chuyển đổi thành khách hàng"] = "Convert to customer",
        ["Xóa lead này?"] = "Delete this lead?",
        ["Đã chuyển đổi ngày {0} thành khách hàng"] = "Converted on {0} to customer",
        ["xem cơ hội"] = "view opportunity",
        ["Chuyển đổi lead: {0}"] = "Convert lead: {0}",
        ["Hệ thống sẽ tạo (hoặc gắn vào) <b>khách hàng</b>, tạo <b>người liên hệ</b> từ thông tin lead và tạo <b>cơ hội bán hàng</b> ở giai đoạn \"Đánh giá\". Các hoạt động đã có của lead được chuyển sang khách hàng."]
            = "The system will create (or link to) a <b>customer</b>, create a <b>contact</b> from the lead's details and create an <b>opportunity</b> at the \"Qualification\" stage. The lead's existing activities are moved to the customer.",
        ["1. Khách hàng"] = "1. Customer",
        ["-- Tạo khách hàng mới --"] = "-- Create new customer --",
        ["2. Người liên hệ"] = "2. Contact",
        ["3. Cơ hội bán hàng"] = "3. Opportunity",
        ["Chuyển đổi"] = "Convert",

        // ---------- Khách hàng ----------
        ["Tên, mã, MST, SĐT, email"] = "Name, code, tax ID, phone, email",
        ["+ Thêm khách hàng"] = "+ Add customer",
        ["Ngành"] = "Industry",
        ["Tỉnh/TP"] = "City",
        ["Doanh số đã thắng"] = "Won revenue",
        ["Không có khách hàng."] = "No customers.",
        ["Tổng: {0} khách hàng"] = "Total: {0} customers",
        ["Thêm khách hàng"] = "Add customer",
        ["Sửa khách hàng: {0}"] = "Edit customer: {0}",
        ["Xóa khách hàng {0}?"] = "Delete customer {0}?",
        ["Đã thắng"] = "Won",
        ["Đang mở"] = "Open",
        ["MST"] = "Tax ID",
        ["Số nhân sự"] = "Employees",
        ["Người liên hệ ({0})"] = "Contacts ({0})",
        ["Y tế"] = "Healthcare",
        ["Dược phẩm"] = "Pharmaceuticals",
        ["Giáo dục"] = "Education",
        ["Sản xuất"] = "Manufacturing",
        ["Bán lẻ"] = "Retail",
        ["Tài chính – Ngân hàng"] = "Finance – Banking",
        ["Vận tải – Logistics"] = "Transport – Logistics",
        ["Thực phẩm – Đồ uống"] = "Food – Beverage",
        ["Công nghệ thông tin"] = "Information technology",

        // ---------- Người liên hệ ----------
        ["Tên, email, SĐT, khách hàng"] = "Name, email, phone, customer",
        ["+ Thêm liên hệ"] = "+ Add contact",
        ["Xóa liên hệ {0}?"] = "Delete contact {0}?",
        ["Không có người liên hệ."] = "No contacts.",
        ["Tổng: {0} người liên hệ"] = "Total: {0} contacts",
        ["Thêm người liên hệ"] = "Add contact",
        ["Sửa liên hệ: {0}"] = "Edit contact: {0}",
        ["-- Chưa gắn --"] = "-- Not linked --",

        // ---------- Hoạt động ----------
        ["Lịch làm việc & hoạt động"] = "Schedule & activities",
        ["Đang chờ"] = "Pending",
        ["Hôm nay"] = "Today",
        ["Đã hoàn thành"] = "Completed",
        ["+ Thêm hoạt động"] = "+ Add activity",
        ["Liên quan"] = "Related to",
        ["Kết quả (tùy chọn)"] = "Result (optional)",
        ["Không có hoạt động."] = "No activities.",
        ["Thêm hoạt động"] = "Add activity",
        ["Hoạt động: {0}"] = "Activity: {0}",

        // ---------- Cơ hội ----------
        ["Đã thua"] = "Lost",
        ["Tên cơ hội, khách hàng"] = "Opportunity, customer",
        ["Xem Kanban"] = "Kanban view",
        ["+ Thêm cơ hội"] = "+ Add opportunity",
        ["Xác suất"] = "Probability",
        ["Không có cơ hội."] = "No opportunities.",
        ["{0} cơ hội · tổng giá trị"] = "{0} opportunities · total value",
        ["Thêm cơ hội bán hàng"] = "Add opportunity",
        ["Sửa cơ hội: {0}"] = "Edit opportunity: {0}",
        ["Đánh dấu THẮNG cơ hội này?"] = "Mark this opportunity as WON?",
        ["Lý do thua"] = "Loss reason",
        ["Xóa cơ hội này?"] = "Delete this opportunity?",
        ["Xác suất {0}% · trọng số {1}"] = "Probability {0}% · weighted {1}",
        ["Ngày chốt"] = "Closed on",
        ["Pipeline bán hàng"] = "Sales pipeline",
        ["Tổng pipeline:"] = "Total pipeline:",
        ["Trọng số:"] = "Weighted:",
        ["Dạng bảng"] = "Table view",
        ["xác suất {0}%"] = "probability {0}%",
        ["Lý do thua?"] = "Loss reason?",
        ["Chuyển giai đoạn…"] = "Move to stage…",

        // ---------- Sản phẩm ----------
        ["Mã, tên"] = "Code, name",
        ["Hiện cả ngừng kinh doanh"] = "Include discontinued",
        ["+ Thêm sản phẩm"] = "+ Add product",
        ["Tên sản phẩm / dịch vụ"] = "Product / service name",
        ["Nhóm"] = "Category",
        ["ĐVT"] = "Unit",
        ["Đơn giá"] = "Unit price",
        ["Đang bán"] = "Active",
        ["Ngừng"] = "Discontinued",
        ["Thêm sản phẩm / dịch vụ"] = "Add product / service",
        ["Sửa: {0}"] = "Edit: {0}",

        // ---------- Báo giá ----------
        ["Số báo giá, khách hàng"] = "Quote no., customer",
        ["+ Tạo báo giá"] = "+ New quote",
        ["Hiệu lực đến"] = "Valid until",
        ["(hết hạn)"] = "(expired)",
        ["Báo giá {0}"] = "Quote {0}",
        ["Đánh dấu đã gửi"] = "Mark as sent",
        ["Nhân bản"] = "Duplicate",
        ["In / Lưu PDF"] = "Print / Save PDF",
        ["BÁO GIÁ"] = "QUOTATION",
        ["Số: {0}"] = "No.: {0}",
        ["Ngày: {0}"] = "Date: {0}",
        ["Hiệu lực đến: {0}"] = "Valid until: {0}",
        ["Kính gửi"] = "To",
        ["MST: {0}"] = "Tax ID: {0}",
        ["Người nhận:"] = "Attention:",
        ["Người phụ trách"] = "Account manager",
        ["Cơ hội:"] = "Opportunity:",
        ["STT"] = "No.",
        ["SL"] = "Qty",
        ["CK %"] = "Disc. %",
        ["Thành tiền"] = "Amount",
        ["Cộng tiền hàng"] = "Subtotal",
        ["Chiết khấu ({0}%)"] = "Discount ({0}%)",
        ["Thuế GTGT ({0}%)"] = "VAT ({0}%)",
        ["TỔNG THANH TOÁN"] = "GRAND TOTAL",
        ["Điều khoản:"] = "Terms:",
        ["Tạo báo giá"] = "New quote",
        ["Sửa báo giá {0}"] = "Edit quote {0}",
        ["Chi tiết sản phẩm / dịch vụ"] = "Line items",
        ["+ Thêm dòng"] = "+ Add line",
        ["Sản phẩm"] = "Product",
        ["Diễn giải"] = "Description",
        ["Chiết khấu chung"] = "Overall discount",
        ["Thuế GTGT"] = "VAT",
        ["Lưu báo giá"] = "Save quote",
        ["-- Tự nhập --"] = "-- Custom --",

        // ---------- Người dùng ----------
        ["+ Thêm người dùng"] = "+ Add user",
        ["Cơ hội mở"] = "Open opps",
        ["Đăng nhập gần nhất"] = "Last login",
        ["Đã khóa"] = "Locked",
        ["Phân quyền:"] = "Permissions:",
        ["Quản trị hệ thống"] = "System administrator",
        ["Quản lý kinh doanh"] = "Sales manager",
        ["Nhân viên kinh doanh"] = "Sales representative",
        [": toàn quyền, quản lý người dùng."] = ": full access, manages users.",
        [": chỉ xem và làm việc với lead, khách hàng, cơ hội, báo giá, hoạt động mình phụ trách."]
            = ": only sees and works on the leads, customers, opportunities, quotes and activities they own.",
        ["Thêm người dùng"] = "Add user",
        ["Sửa người dùng {0}"] = "Edit user {0}",
        ["Để trống nếu không đổi"] = "Leave blank to keep current",

        // ---------- Thông báo từ controller ----------
        ["Bạn không có quyền với bản ghi này."] = "You do not have permission for this record.",
        ["Hoạt động cần gắn với ít nhất một khách hàng, lead hoặc cơ hội."] = "An activity must be linked to at least one customer, lead or opportunity.",
        ["Đã lưu hoạt động."] = "Activity saved.",
        ["Đã hoàn thành: {0}."] = "Completed: {0}.",
        ["Đã lưu người liên hệ."] = "Contact saved.",
        ["Người liên hệ đang gắn với cơ hội, báo giá hoặc hoạt động nên không thể xóa."] = "This contact is linked to opportunities, quotes or activities and cannot be deleted.",
        ["Đã xóa người liên hệ."] = "Contact deleted.",
        ["Mã khách hàng đã tồn tại"] = "Customer code already exists",
        ["Mã số thuế đã thuộc về khách hàng khác"] = "This tax ID belongs to another customer",
        ["Đã lưu khách hàng."] = "Customer saved.",
        ["Khách hàng đã có dữ liệu liên quan (liên hệ, cơ hội, báo giá, hoạt động). Hãy chuyển phân loại sang 'Ngừng giao dịch'."]
            = "This customer has related data (contacts, opportunities, quotes, activities). Change its type to 'Inactive' instead.",
        ["Đã xóa khách hàng."] = "Customer deleted.",
        ["Cần ít nhất số điện thoại hoặc email"] = "A phone number or email is required",
        ["Lead đã chuyển đổi, không thể sửa."] = "This lead has been converted and cannot be edited.",
        ["Đã lưu lead."] = "Lead saved.",
        ["Cơ hội mới"] = "New opportunity",
        ["Chọn khách hàng có sẵn hoặc nhập tên khách hàng mới"] = "Select an existing customer or enter a new customer name",
        ["Nhập tên cơ hội"] = "Enter an opportunity name",
        ["Đã chuyển đổi lead thành khách hàng {0} và tạo cơ hội bán hàng."] = "Lead converted to customer {0} and an opportunity was created.",
        ["Đã chuyển đổi lead thành khách hàng {0}."] = "Lead converted to customer {0}.",
        ["Lead đã có hoạt động chăm sóc. Hãy chuyển trạng thái 'Không phù hợp' thay vì xóa."] = "This lead has activities. Set its status to 'Unqualified' instead of deleting it.",
        ["Đã xóa lead."] = "Lead deleted.",
        ["Vui lòng nhập lý do thua"] = "Please enter the loss reason",
        ["Vui lòng nhập lý do thua."] = "Please enter the loss reason.",
        ["Đã lưu cơ hội."] = "Opportunity saved.",
        ["Cơ hội \"{0}\" chuyển sang: {1}."] = "Opportunity \"{0}\" moved to: {1}.",
        ["Cơ hội đã có báo giá hoặc hoạt động. Hãy đóng cơ hội ở trạng thái 'Thua' thay vì xóa."] = "This opportunity has quotes or activities. Close it as 'Lost' instead of deleting it.",
        ["Đã xóa cơ hội."] = "Opportunity deleted.",
        ["Mã sản phẩm đã tồn tại"] = "Product code already exists",
        ["Đã lưu sản phẩm."] = "Product saved.",
        ["Chỉ sửa được báo giá ở trạng thái Nháp."] = "Only draft quotes can be edited.",
        ["Báo giá cần ít nhất một dòng sản phẩm / dịch vụ."] = "A quote needs at least one line item.",
        ["Ngày hiệu lực phải sau ngày báo giá"] = "Valid-until date must be after the quote date",
        ["Đã lưu báo giá {0}."] = "Quote {0} saved.",
        ["Báo giá {0}: {1}."] = "Quote {0}: {1}.",
        ["Đã tạo bản sao {0}."] = "Created copy {0}.",
        ["Vai trò không hợp lệ"] = "Invalid role",
        ["Tên đăng nhập đã tồn tại"] = "Username already exists",
        ["Vui lòng nhập mật khẩu"] = "Please enter a password",
        ["Không thể tự khóa hoặc hạ quyền tài khoản đang đăng nhập."] = "You cannot lock or demote the account you are signed in with.",
        ["Đã lưu tài khoản {0}."] = "Account {0} saved.",

        // ---------- Nhãn trường (Display) & lỗi kiểm tra (DataAnnotations) ----------
        ["Tên đăng nhập"] = "Username",
        ["Mật khẩu"] = "Password",
        ["Ghi nhớ đăng nhập"] = "Remember me",
        ["Mật khẩu hiện tại"] = "Current password",
        ["Mật khẩu mới"] = "New password",
        ["Xác nhận mật khẩu mới"] = "Confirm new password",
        ["Họ và tên"] = "Full name",
        ["Vai trò"] = "Role",
        ["Kích hoạt"] = "Active",
        ["Mã KH"] = "Customer code",
        ["Tên khách hàng"] = "Customer name",
        ["Ngành nghề"] = "Industry",
        ["Mã số thuế"] = "Tax ID",
        ["Địa chỉ"] = "Address",
        ["Tỉnh / Thành phố"] = "City / Province",
        ["Ghi chú"] = "Notes",
        ["Ngày sinh"] = "Date of birth",
        ["Liên hệ chính"] = "Primary contact",
        ["Nhu cầu / ghi chú"] = "Needs / notes",
        ["Tên cơ hội"] = "Opportunity name",
        ["Xác suất (%)"] = "Probability (%)",
        ["Mô tả"] = "Description",
        ["Nội dung / kết quả"] = "Details / outcome",
        ["Mã sản phẩm"] = "Product code",
        ["Đơn vị tính"] = "Unit",
        ["Đang kinh doanh"] = "Active",
        ["Người nhận"] = "Recipient",
        ["Ngày báo giá"] = "Quote date",
        ["Chiết khấu chung (%)"] = "Overall discount (%)",
        ["Thuế GTGT (%)"] = "VAT (%)",
        ["Chiết khấu"] = "Discount",
        ["Điều khoản / ghi chú"] = "Terms / notes",
        ["Gắn vào khách hàng đã có"] = "Link to existing customer",
        ["Tên khách hàng mới"] = "New customer name",
        ["Tạo cơ hội bán hàng"] = "Create opportunity",
        ["Giá trị cơ hội"] = "Opportunity amount",
        ["Vui lòng nhập tên đăng nhập"] = "Please enter a username",
        ["Vui lòng nhập mật khẩu hiện tại"] = "Please enter your current password",
        ["Vui lòng nhập mật khẩu mới"] = "Please enter a new password",
        ["Mật khẩu tối thiểu 6 ký tự"] = "Password must be at least 6 characters",
        ["Vui lòng xác nhận mật khẩu mới"] = "Please confirm the new password",
        ["Xác nhận mật khẩu không khớp"] = "Password confirmation does not match",
        ["Nhập diễn giải"] = "Enter a description",
        ["Số lượng > 0"] = "Quantity must be > 0",
        ["Vui lòng chọn khách hàng"] = "Please select a customer",
        ["Chỉ dùng chữ không dấu, số, dấu chấm, gạch dưới"] = "Use only unaccented letters, digits, dots and underscores",
        ["Vui lòng nhập họ tên"] = "Please enter a full name",
        ["Vui lòng nhập mã khách hàng"] = "Please enter a customer code",
        ["Vui lòng nhập tên khách hàng"] = "Please enter a customer name",
        ["Email không hợp lệ"] = "Invalid email address",
        ["Vui lòng nhập tên cơ hội"] = "Please enter an opportunity name",
        ["Giá trị không hợp lệ"] = "Invalid amount",
        ["Vui lòng nhập tiêu đề"] = "Please enter a subject",
        ["Vui lòng nhập mã"] = "Please enter a code",
        ["Vui lòng nhập tên"] = "Please enter a name",

        // ---------- Menu nhóm & danh mục dùng chung ----------
        ["Nghiệp vụ"] = "Operations",
        ["Hệ thống"] = "System",
        ["Nhóm sản phẩm"] = "Product categories",
        ["Nhóm danh mục"] = "Catalog",
        ["Danh mục: {0}"] = "Catalog: {0}",
        ["Tên (tiếng Việt)"] = "Name (Vietnamese)",
        ["Tên (tiếng Anh)"] = "Name (English)",
        ["Thứ tự"] = "Order",
        ["Đang sử dụng"] = "In use",
        ["Ngừng sử dụng"] = "Inactive",
        ["Đang dùng"] = "Used by",
        ["Tên tiếng Việt / tiếng Anh"] = "Vietnamese / English name",
        ["Hiện cả mục ngừng sử dụng"] = "Include inactive items",
        ["+ Thêm mục"] = "+ Add item",
        ["Xóa mục {0}?"] = "Delete item {0}?",
        ["Danh mục trống."] = "This catalog is empty.",
        ["Đổi tên một mục sẽ cập nhật luôn dữ liệu đang dùng tên cũ. Mục đã được dùng thì không xóa được — hãy chuyển sang ngừng sử dụng để ẩn khỏi danh sách chọn."]
            = "Renaming an item also updates records that use the old name. Items in use cannot be deleted — mark them inactive to hide them from selection lists.",
        ["Thêm mục: {0}"] = "Add item: {0}",
        ["Sửa mục: {0}"] = "Edit item: {0}",
        ["Đổi tên tiếng Việt sẽ cập nhật luôn dữ liệu đang dùng tên cũ."] = "Changing the Vietnamese name also updates records that use the old name.",
        ["Nhóm danh mục không hợp lệ"] = "Invalid catalog",
        ["Tên đã tồn tại trong danh mục này"] = "This name already exists in the catalog",
        ["Đã lưu danh mục và cập nhật {0} bản ghi đang dùng."] = "Catalog item saved; {0} records using it were updated.",
        ["Đã lưu danh mục."] = "Catalog item saved.",
        ["Mục \"{0}\" đang được dùng ở {1} bản ghi. Hãy chuyển sang ngừng sử dụng thay vì xóa."] = "Item \"{0}\" is used by {1} records. Mark it inactive instead of deleting it.",
        ["Đã xóa mục danh mục."] = "Catalog item deleted.",
        [": xem và chỉnh toàn bộ dữ liệu, phân công người phụ trách, quản lý danh mục."]
            = ": views and edits all data, assigns owners, manages catalogs.",

        // ---------- Enum ----------
        ["Tiềm năng"] = "Prospect",
        ["Đối tác"] = "Partner",
        ["Ngừng giao dịch"] = "Inactive",
        ["Giới thiệu"] = "Referral",
        ["Sự kiện / Hội thảo"] = "Event / Seminar",
        ["Gọi điện chủ động"] = "Cold call",
        ["Mạng xã hội"] = "Social media",
        ["Khác"] = "Other",
        ["Mới"] = "New",
        ["Đã liên hệ"] = "Contacted",
        ["Đủ điều kiện"] = "Qualified",
        ["Không phù hợp"] = "Unqualified",
        ["Đã chuyển đổi"] = "Converted",
        ["Nóng"] = "Hot",
        ["Ấm"] = "Warm",
        ["Lạnh"] = "Cold",
        ["Tìm hiểu nhu cầu"] = "Prospecting",
        ["Đánh giá"] = "Qualification",
        ["Gửi đề xuất / báo giá"] = "Proposal / quote",
        ["Đàm phán"] = "Negotiation",
        ["Thắng"] = "Won",
        ["Thua"] = "Lost",
        ["Cuộc gọi"] = "Call",
        ["Cuộc họp"] = "Meeting",
        ["Công việc"] = "Task",
        ["Kế hoạch"] = "Planned",
        ["Hoàn thành"] = "Completed",
        ["Đã hủy"] = "Cancelled",
        ["Thấp"] = "Low",
        ["Trung bình"] = "Normal",
        ["Cao"] = "High",
        ["Nháp"] = "Draft",
        ["Đã gửi"] = "Sent",
        ["Khách chấp nhận"] = "Accepted",
        ["Khách từ chối"] = "Rejected",
    };
}

/// <summary>Localizer dùng cho DataAnnotations (Display, ErrorMessage) và enum trong GetEnumSelectList.</summary>
public class I18nStringLocalizer : IStringLocalizer
{
    public LocalizedString this[string name]
    {
        get
        {
            var value = I18n.T(name);
            return new LocalizedString(name, value, resourceNotFound: !I18n.HasTranslation(name));
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var value = string.Format(I18n.T(name), arguments);
            return new LocalizedString(name, value, resourceNotFound: !I18n.HasTranslation(name));
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Enumerable.Empty<LocalizedString>();
}

public class I18nStringLocalizerFactory : IStringLocalizerFactory
{
    private static readonly I18nStringLocalizer Instance = new();
    public IStringLocalizer Create(Type resourceSource) => Instance;
    public IStringLocalizer Create(string baseName, string location) => Instance;
}
