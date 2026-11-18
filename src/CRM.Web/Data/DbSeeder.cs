using CRM.Web.Models.Entities;
using CRM.Web.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Data;

/// <summary>Tạo database (nếu chưa có) và nạp dữ liệu mẫu để chạy thử.</summary>
public static class DbSeeder
{
    /// <summary>
    /// Đảm bảo schema + danh mục dùng chung; nạp dữ liệu mẫu nếu <paramref name="seedSampleData"/> và DB còn trống.
    /// <paramref name="sampleCustomers"/> (cấu hình Database:SampleCustomers): nếu có, chỉ tạo tài khoản + hàng hoá cố định
    /// rồi sinh đúng N khách hàng mẫu kèm trọn chuỗi nghiệp vụ (bộ sinh DemoDataGenerator, seed cố định để lần nào cũng như nhau);
    /// nếu không, nạp bộ 8 khách hàng mẫu viết tay như trước.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, bool seedSampleData = true, int? sampleCustomers = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();

        await db.Database.EnsureCreatedAsync();
        await SchemaUpgrader.EnsureAsync(db);
        if (seedSampleData && !await db.Users.AnyAsync())
        {
            if (sampleCustomers is > 0)
            {
                await SeedSampleDataAsync(db, hasher, usersAndProductsOnly: true);
                await CatalogSeeder.SeedAsync(db);   // bộ sinh dữ liệu dùng danh mục (ngành, tỉnh, loại chi phí...)
                await DemoDataGenerator.GenerateAsync(db, sampleCustomers.Value, seed: 2026);
            }
            else await SeedSampleDataAsync(db, hasher);
        }
        await CatalogSeeder.SeedAsync(db);
        await ReportingSchema.SeedSamplesAsync(db);
        await CRM.Web.Infrastructure.TicketWork.BackfillAsync(db);   // việc chính cho phiếu hỗ trợ đã phân công
    }

    private static async Task SeedSampleDataAsync(CrmDbContext db, IPasswordHasher<AppUser> hasher, bool usersAndProductsOnly = false)
    {

        var today = DateTime.Today;
        var rnd = new Random(2026);

        // ---------- Người dùng ----------
        AppUser U(string username, string fullName, string role, string pwd)
        {
            var u = new AppUser { Username = username, FullName = fullName, Role = role, Email = $"{username}@crm.local" };
            u.PasswordHash = hasher.HashPassword(u, pwd);
            return u;
        }
        var admin = U("admin", "Quản trị hệ thống", AppRoles.Admin, "Admin@123");
        var manager = U("manager", "Nguyễn Hoàng Minh", AppRoles.Manager, "Manager@123");
        var sales1 = U("sales1", "Trần Thu Hà", AppRoles.Sales, "Sales@123");
        var sales2 = U("sales2", "Lê Quốc Bảo", AppRoles.Sales, "Sales@123");
        var support1 = U("support1", "Võ Minh Thư", AppRoles.Support, "Support@123");
        db.Users.AddRange(admin, manager, sales1, sales2, support1);

        // ---------- Sản phẩm ----------
        var products = new List<Product>
        {
            new() { Code = "HRM-STD", Name = "Phần mềm HRM – gói Standard (≤100 nhân sự/năm)", Category = "Phần mềm", Unit = "Gói/năm", UnitPrice = 48_000_000 },
            new() { Code = "HRM-PRO", Name = "Phần mềm HRM – gói Professional (≤500 nhân sự/năm)", Category = "Phần mềm", Unit = "Gói/năm", UnitPrice = 120_000_000 },
            new() { Code = "CRM-USER", Name = "Bản quyền CRM theo người dùng", Category = "Phần mềm", Unit = "User/năm", UnitPrice = 3_600_000 },
            new() { Code = "ERP-CORE", Name = "ERP – phân hệ Tài chính kế toán", Category = "Phần mềm", Unit = "Gói", UnitPrice = 350_000_000 },
            new() { Code = "SV-IMPL", Name = "Dịch vụ triển khai, cấu hình hệ thống", Category = "Dịch vụ", Type = ProductType.Service, Unit = "Ngày công", UnitPrice = 4_500_000 },
            new() { Code = "SV-TRAIN", Name = "Đào tạo người dùng cuối", Category = "Dịch vụ", Type = ProductType.Service, Unit = "Buổi", UnitPrice = 6_000_000 },
            new() { Code = "SV-DATA", Name = "Chuyển đổi dữ liệu", Category = "Dịch vụ", Type = ProductType.Service, Unit = "Gói", UnitPrice = 25_000_000 },
            new() { Code = "SV-SUP", Name = "Hỗ trợ vận hành 12 tháng", Category = "Dịch vụ", Type = ProductType.Service, Unit = "Năm", UnitPrice = 36_000_000 },
        };
        // Giá vốn tham khảo: phần mềm ~60%, dịch vụ ~45% giá bán
        foreach (var p in products)
        {
            p.CostPrice = Math.Round(p.UnitPrice * (p.Type == ProductType.Service ? 0.45m : 0.6m) / 1000m) * 1000m;
            if (p.Type == ProductType.Goods) { p.Brand = "Tự phát triển"; p.Origin = "Việt Nam"; p.WarrantyMonths = 12; }
        }
        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        if (usersAndProductsOnly) return;

        // ---------- Khách hàng & liên hệ ----------
        var custData = new (string Name, string Industry, string City, CustomerType Type, AppUser Owner, string Contact, string Title)[]
        {
            ("Công ty CP Dược phẩm An Khang", "Dược phẩm", "TP. Hồ Chí Minh", CustomerType.Customer, sales1, "Phạm Thị Lan", "Trưởng phòng Nhân sự"),
            ("Bệnh viện Đa khoa Tâm Đức", "Y tế", "TP. Hồ Chí Minh", CustomerType.Customer, sales1, "Võ Minh Tuấn", "Giám đốc CNTT"),
            ("Công ty TNHH Logistics Sao Việt", "Vận tải – Logistics", "Bình Dương", CustomerType.Prospect, sales2, "Đặng Hữu Phước", "Giám đốc Tài chính"),
            ("Tập đoàn Giáo dục Ánh Dương", "Giáo dục", "Hà Nội", CustomerType.Prospect, sales2, "Ngô Thanh Thảo", "Phó Tổng Giám đốc"),
            ("Công ty CP Thực phẩm Xanh", "Thực phẩm – Đồ uống", "Long An", CustomerType.Customer, sales1, "Huỳnh Văn Nam", "Kế toán trưởng"),
            ("Ngân hàng TMCP Phương Nam (CN Q.1)", "Tài chính – Ngân hàng", "TP. Hồ Chí Minh", CustomerType.Prospect, manager, "Lý Gia Hân", "Trưởng phòng Vận hành"),
            ("Công ty TNHH Cơ khí Tiến Phát", "Sản xuất", "Đồng Nai", CustomerType.Partner, sales2, "Trịnh Công Sơn", "Giám đốc"),
            ("Chuỗi bán lẻ MiniMart 24h", "Bán lẻ", "Đà Nẵng", CustomerType.Prospect, sales1, "Bùi Khánh Linh", "Trưởng phòng Mua hàng"),
        };
        var customers = new List<Customer>();
        var contacts = new List<Contact>();
        for (var i = 0; i < custData.Length; i++)
        {
            var d = custData[i];
            var c = new Customer
            {
                Code = $"KH{i + 1:0000}", Name = d.Name, Industry = d.Industry, City = d.City, Type = d.Type, OwnerId = d.Owner.Id,
                Phone = $"028 38{rnd.Next(100000, 999999)}", Email = $"info{i + 1}@example.vn", EmployeeCount = rnd.Next(50, 2000),
                CreatedAt = today.AddDays(-rnd.Next(30, 300))
            };
            customers.Add(c);
        }
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();

        for (var i = 0; i < custData.Length; i++)
        {
            contacts.Add(new Contact
            {
                FullName = custData[i].Contact, JobTitle = custData[i].Title, CustomerId = customers[i].Id, IsPrimary = true,
                OwnerId = customers[i].OwnerId, Email = $"lienhe{i + 1}@example.vn", Phone = $"09{rnd.Next(10000000, 99999999)}",
                BirthDate = new DateTime(1980 + rnd.Next(15), (i % 12) + 1, 10)
            });
        }
        contacts.Add(new Contact { FullName = "Mai Anh Khoa", JobTitle = "Chuyên viên IT", CustomerId = customers[0].Id, OwnerId = sales1.Id, Phone = "0903123456", Email = "khoa.ma@example.vn" });
        contacts.Add(new Contact { FullName = "Tôn Nữ Diệu Hiền", JobTitle = "Trưởng phòng Hành chính", CustomerId = customers[1].Id, OwnerId = sales1.Id, Phone = "0912345678", Email = "hien.tn@example.vn" });
        db.Contacts.AddRange(contacts);
        await db.SaveChangesAsync();

        // ---------- Cơ hội ----------
        Opportunity O(string name, int ci, OpportunityStage st, decimal amount, int closeInDays, AppUser owner, LeadSource src)
        {
            var o = new Opportunity
            {
                Name = name, CustomerId = customers[ci].Id, ContactId = contacts[ci].Id, Stage = st, Amount = amount,
                Probability = (int)st, ExpectedCloseDate = today.AddDays(closeInDays), OwnerId = owner.Id, Source = src,
                CreatedAt = today.AddDays(-rnd.Next(20, 120))
            };
            if (o.IsClosed) o.ClosedAt = today.AddDays(closeInDays);
            if (st == OpportunityStage.ClosedLost) o.LostReason = "Khách chọn nhà cung cấp khác giá thấp hơn";
            return o;
        }
        var opps = new List<Opportunity>
        {
            O("An Khang – Triển khai HRM Professional", 0, OpportunityStage.ClosedWon, 420_000_000, -95, sales1, LeadSource.Referral),
            O("Tâm Đức – Bổ sung phân hệ chấm công", 1, OpportunityStage.ClosedWon, 180_000_000, -60, sales1, LeadSource.Event),
            O("Thực phẩm Xanh – CRM 30 user", 4, OpportunityStage.ClosedWon, 150_000_000, -32, sales1, LeadSource.Website),
            O("Tiến Phát – Gia hạn hỗ trợ vận hành", 6, OpportunityStage.ClosedWon, 72_000_000, -8, sales2, LeadSource.Referral),
            O("Sao Việt – ERP Tài chính kế toán", 2, OpportunityStage.Negotiation, 680_000_000, 20, sales2, LeadSource.ColdCall),
            O("Ánh Dương – HRM cho 12 cơ sở", 3, OpportunityStage.Proposal, 950_000_000, 45, sales2, LeadSource.Event),
            O("Phương Nam – CRM khối bán lẻ", 5, OpportunityStage.Qualification, 1_200_000_000, 75, manager, LeadSource.Referral),
            O("MiniMart 24h – HRM Standard", 7, OpportunityStage.Prospecting, 96_000_000, 60, sales1, LeadSource.Social),
            O("Tâm Đức – Tích hợp HIS – HRM", 1, OpportunityStage.Proposal, 260_000_000, 30, sales1, LeadSource.Referral),
            O("An Khang – Mở rộng 200 nhân sự", 0, OpportunityStage.Negotiation, 140_000_000, 12, sales1, LeadSource.Referral),
            O("Thực phẩm Xanh – Chuyển đổi dữ liệu", 4, OpportunityStage.ClosedLost, 55_000_000, -20, sales1, LeadSource.Website),
            O("Tiến Phát – ERP sản xuất", 6, OpportunityStage.Qualification, 520_000_000, 90, sales2, LeadSource.Referral),
        };
        db.Opportunities.AddRange(opps);

        // ---------- Leads ----------
        var leadData = new (string Name, string Company, LeadSource Src, LeadStatus St, LeadRating R, decimal Val, AppUser Owner)[]
        {
            ("Nguyễn Văn Hùng", "Công ty CP May mặc Việt Tiến Phong", LeadSource.Website, LeadStatus.New, LeadRating.Hot, 200_000_000, sales1),
            ("Trần Ngọc Ánh", "Phòng khám Đa khoa Hòa Bình", LeadSource.Social, LeadStatus.Contacted, LeadRating.Warm, 80_000_000, sales1),
            ("Lê Hoàng Phúc", "Công ty TNHH Xây dựng Phúc Thịnh", LeadSource.ColdCall, LeadStatus.New, LeadRating.Cold, 150_000_000, sales2),
            ("Phạm Minh Châu", "Trường Quốc tế Sunrise", LeadSource.Event, LeadStatus.Qualified, LeadRating.Hot, 350_000_000, sales2),
            ("Hoàng Thị Mỹ", "Công ty CP Du lịch Biển Xanh", LeadSource.EmailCampaign, LeadStatus.Contacted, LeadRating.Warm, 60_000_000, sales1),
            ("Đỗ Đức Thắng", "Nhà máy Nhựa Thắng Lợi", LeadSource.Referral, LeadStatus.Qualified, LeadRating.Warm, 280_000_000, sales2),
            ("Vũ Thanh Tâm", "Hệ thống Nha khoa Smile", LeadSource.Website, LeadStatus.New, LeadRating.Warm, 120_000_000, sales1),
            ("Cao Bảo Ngọc", "Công ty Bảo hiểm An Tâm", LeadSource.Event, LeadStatus.Unqualified, LeadRating.Cold, 0, sales2),
            ("Lâm Chí Thanh", "Siêu thị điện máy Thanh Lâm", LeadSource.Social, LeadStatus.New, LeadRating.Hot, 180_000_000, manager),
            ("Kiều Mai Phương", "Công ty CP Nông sản Mekong", LeadSource.Website, LeadStatus.Contacted, LeadRating.Warm, 90_000_000, sales2),
        };
        db.Leads.AddRange(leadData.Select((l, i) => new Lead
        {
            FullName = l.Name, Company = l.Company, Source = l.Src, Status = l.St, Rating = l.R, EstimatedValue = l.Val, OwnerId = l.Owner.Id,
            JobTitle = "Giám đốc", Email = $"lead{i + 1}@example.vn", Phone = $"09{rnd.Next(10000000, 99999999)}",
            Description = "Quan tâm giải pháp quản trị nhân sự, chấm công và tính lương.", CreatedAt = today.AddDays(-rnd.Next(0, 40))
        }));
        await db.SaveChangesAsync();

        // ---------- Hoạt động ----------
        var subjects = new (ActivityType T, string S)[]
        {
            (ActivityType.Call, "Gọi tư vấn nhu cầu"), (ActivityType.Meeting, "Demo sản phẩm tại văn phòng khách hàng"),
            (ActivityType.Email, "Gửi tài liệu giới thiệu giải pháp"), (ActivityType.Task, "Chuẩn bị báo giá"),
            (ActivityType.Call, "Follow-up sau demo"), (ActivityType.Meeting, "Họp đàm phán điều khoản hợp đồng"),
        };
        var acts = new List<Activity>();
        foreach (var o in opps.Where(o => !o.IsClosed))
        {
            for (var k = 0; k < 3; k++)
            {
                var s = subjects[rnd.Next(subjects.Length)];
                var due = today.AddDays(rnd.Next(-6, 10)).AddHours(8 + rnd.Next(9));
                var done = due < today && rnd.Next(3) > 0;
                acts.Add(new Activity
                {
                    Type = s.T, Subject = s.S, DueDate = due, OwnerId = o.OwnerId, CustomerId = o.CustomerId, ContactId = o.ContactId,
                    OpportunityId = o.Id, Priority = (Priority)rnd.Next(1, 4),
                    Status = done ? ActivityStatus.Completed : ActivityStatus.Planned, CompletedAt = done ? due : null,
                    Description = done ? "Khách phản hồi tích cực, hẹn bước tiếp theo." : null
                });
            }
        }
        db.Activities.AddRange(acts);

        // ---------- Báo giá ----------
        Quote Q(int no, Opportunity o, QuoteStatus st, params (Product P, decimal Qty)[] items)
        {
            var q = new Quote
            {
                Number = $"BG-{today:yyyy}-{no:0000}", CustomerId = o.CustomerId, ContactId = o.ContactId, OpportunityId = o.Id,
                OwnerId = o.OwnerId, Status = st, QuoteDate = today.AddDays(-rnd.Next(3, 20)), VatPercent = 10, DiscountPercent = 5,
                Terms = "Thanh toán 50% khi ký hợp đồng, 50% sau nghiệm thu. Báo giá có hiệu lực 30 ngày."
            };
            q.ValidUntil = q.QuoteDate.AddDays(30);
            var sort = 1;
            foreach (var (p, qty) in items)
                q.Lines.Add(new QuoteLine { ProductId = p.Id, Description = p.Name, Unit = p.Unit, Quantity = qty, UnitPrice = p.UnitPrice, SortOrder = sort++ });
            q.Recalculate();
            return q;
        }
        db.Quotes.AddRange(
            Q(1, opps[4], QuoteStatus.Sent, (products[3], 1), (products[4], 40), (products[5], 6), (products[7], 1)),
            Q(2, opps[5], QuoteStatus.Draft, (products[1], 3), (products[4], 30), (products[6], 1)),
            Q(3, opps[8], QuoteStatus.Sent, (products[4], 25), (products[5], 4)));

        // ---------- Phiếu hỗ trợ (CSKH) ----------
        var now = DateTime.Now;
        var ticketNo = 0;
        Ticket Tk(int ci, string subject, string category, TicketPriority prio, TicketChannel ch, double hoursAgo, AppUser? agent,
            TicketStatus st, double? respAfter = null, double? resolveAfter = null, int? score = null, string? resolution = null)
        {
            var created = now.AddHours(-hoursAgo);
            var t = new Ticket
            {
                Number = $"YC-{created:yyyy}-{++ticketNo:0000}", Subject = subject, CustomerId = customers[ci].Id, ContactId = contacts[ci].Id,
                Category = category, Priority = prio, Channel = ch, Status = st, OwnerId = agent?.Id, CreatedById = support1.Id, CreatedAt = created
            };
            t.ApplySla();
            t.Comments.Add(new TicketComment { Body = $"Tiếp nhận yêu cầu qua kênh {ch.GetDisplayName()}.", IsSystem = true, IsInternal = true, AuthorId = support1.Id, CreatedAt = created });
            if (respAfter.HasValue)
            {
                t.FirstResponseAt = created.AddHours(respAfter.Value);
                t.Comments.Add(new TicketComment { Body = "Chào anh/chị, bộ phận hỗ trợ đã tiếp nhận và đang kiểm tra. Chúng tôi sẽ cập nhật sớm nhất.", AuthorId = agent?.Id ?? support1.Id, CreatedAt = t.FirstResponseAt.Value });
            }
            if (resolveAfter.HasValue)
            {
                t.ResolvedAt = created.AddHours(resolveAfter.Value);
                t.Resolution = resolution;
                t.Comments.Add(new TicketComment { Body = "Đã giải quyết.", IsSystem = true, IsInternal = true, AuthorId = agent?.Id ?? support1.Id, CreatedAt = t.ResolvedAt.Value });
            }
            if (st == TicketStatus.Closed)
            {
                t.ClosedAt = t.ResolvedAt!.Value.AddDays(1);
                t.SatisfactionScore = score;
                t.Comments.Add(new TicketComment { Body = "Đóng phiếu." + (score.HasValue ? $" Khách đánh giá {score}/5." : ""), IsSystem = true, IsInternal = true, AuthorId = support1.Id, CreatedAt = t.ClosedAt.Value });
            }
            return t;
        }
        db.Tickets.AddRange(
            Tk(0, "Không tính được lương tháng cho nhân viên mới", "Hỗ trợ kỹ thuật", TicketPriority.High, TicketChannel.Phone, 300, support1, TicketStatus.Closed, 1.5, 20, 5, "Bổ sung cấu hình ca làm việc cho nhóm nhân viên mới, tính lại lương thành công."),
            Tk(1, "Hướng dẫn phân quyền cho trưởng khoa", "Hướng dẫn sử dụng", TicketPriority.Low, TicketChannel.Email, 200, support1, TicketStatus.Closed, 6, 30, 4, "Gửi tài liệu và hướng dẫn trực tiếp qua điện thoại."),
            Tk(4, "Xuất lại hoá đơn sai mã số thuế", "Hoá đơn / thanh toán", TicketPriority.Normal, TicketChannel.Zalo, 150, sales1, TicketStatus.Closed, 2, 90, 2, "Xuất hoá đơn điều chỉnh và gửi lại cho khách."),
            Tk(1, "Máy chấm công khu B không đồng bộ dữ liệu", "Bảo hành / sửa chữa", TicketPriority.Urgent, TicketChannel.Phone, 6, support1, TicketStatus.InProgress, 0.5),
            Tk(6, "Đề nghị bổ sung báo cáo tồn kho theo lô", "Yêu cầu thay đổi / nâng cấp", TicketPriority.Normal, TicketChannel.Email, 30, support1, TicketStatus.WaitingCustomer, 3),
            Tk(0, "Lỗi đăng nhập sau khi đổi mật khẩu", "Hỗ trợ kỹ thuật", TicketPriority.High, TicketChannel.Web, 5, null, TicketStatus.New));

        await db.SaveChangesAsync();
    }
}
