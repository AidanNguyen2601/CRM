using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Data;

public record ExtendedDemoResult(int PriceLists, int PricedCustomers, int Tags, int TaggedRecords, int CustomFields, int CustomValues,
    int VipCustomers, int VipContracts, int ForecastSet, int RecurringContracts, int QuoteApprovals,
    int NpsSurveys, int NpsResponses, int NpsAnswered, int WebForms, int WebLeads, int HealthScored);

/// <summary>
/// Dữ liệu mẫu cho các nghiệp vụ mở rộng, dựng trên dữ liệu đang có (khách hàng, sản phẩm, cơ hội, hợp đồng…):
/// bảng giá + gắn khách, nhãn và trường tùy biến, SLA VIP cho khách / hợp đồng, nhóm dự báo của cơ hội, hợp đồng định kỳ,
/// báo giá chờ / đã duyệt chiết khấu, khảo sát NPS và câu trả lời, form thu lead website + lead có UTM; cuối cùng chấm lại điểm lead và sức khoẻ khách hàng.
/// Chạy lại được: phần cấu hình (bảng giá, nhãn, trường, form) không tạo trùng; khảo sát NPS và lead website được nạp thêm.
/// </summary>
public static class DemoDataExtended
{
    public static async Task<ExtendedDemoResult> GenerateAsync(CrmDbContext db, IServiceProvider services, int? seed = null)
    {
        var r = new Random(seed ?? Environment.TickCount);
        var today = DateTime.Today; var now = DateTime.Now;
        T Pick<T>(IList<T> xs) => xs[r.Next(xs.Count)];
        db.SuppressAudit = true;
        await using var tx = await db.Database.BeginTransactionAsync();

        var sales = await db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Sales || u.Role == AppRoles.Manager)).ToListAsync();
        var approver = await db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Manager || u.Role == AppRoles.Admin)).OrderBy(u => u.Role == AppRoles.Admin).FirstOrDefaultAsync();
        var customers = await db.Customers.IgnoreQueryFilters().ToListAsync();

        // ---------- 1. Bảng giá ----------
        int priceLists = 0, priced = 0;
        var products = await db.Products.Where(p => p.IsActive && p.UnitPrice > 0).ToListAsync();
        async Task<PriceList?> EnsureList(string name, string desc, decimal factor)
        {
            var pl = await db.PriceLists.Include(x => x.Items).FirstOrDefaultAsync(x => x.Name == name);
            if (pl != null || products.Count == 0) return pl;
            pl = new PriceList { Name = name, Description = desc, IsActive = true, ValidFrom = new DateTime(today.Year, 1, 1), UpdatedAt = now };
            foreach (var p in products) pl.Items.Add(new PriceListItem { ProductId = p.Id, Price = Math.Round(p.UnitPrice * factor / 1000m, 0) * 1000m });
            db.PriceLists.Add(pl); priceLists++;
            return pl;
        }
        var dealer = await EnsureList("Đại lý cấp 1", "Giá cho đại lý / đối tác phân phối (giảm 10% so với giá chuẩn)", 0.90m);
        var project = await EnsureList("Khách hàng dự án", "Giá cho khách hàng dự án lớn (giảm 5%)", 0.95m);
        await db.SaveChangesAsync();
        foreach (var c in customers.Where(c => c.PriceListId == null))
        {
            if (c.Type == CustomerType.Partner && dealer != null) { c.PriceListId = dealer.Id; priced++; }
            else if (c.Type == CustomerType.Customer && project != null && r.Next(100) < 8) { c.PriceListId = project.Id; priced++; }
        }

        // ---------- 2. Nhãn ----------
        var tagDefs = new[] { ("VIP", "#dc3545"), ("Khách quay lại", "#198754"), ("Đối thủ đang chăm sóc", "#fd7e14"), ("Cần chăm sóc lại", "#ffc107"),
            ("Hội chợ triển lãm 2026", "#0dcaf0"), ("Đối tác chiến lược", "#6f42c1"), ("Quan tâm chuyển đổi số", "#0d6efd") };
        var tags = new List<Tag>();
        foreach (var (name, color) in tagDefs)
        {
            var t = await db.Tags.FirstOrDefaultAsync(x => x.Name == name);
            if (t == null) { t = new Tag { Name = name, Color = color }; db.Tags.Add(t); }
            tags.Add(t);
        }
        await db.SaveChangesAsync();
        var existingTags = (await db.EntityTags.Select(e => new { e.TagId, e.EntityType, e.EntityId }).ToListAsync()).Select(e => (e.TagId, e.EntityType, e.EntityId)).ToHashSet();
        var tagged = 0;
        void TagIt(Tag t, string type, int id)
        {
            if (!existingTags.Add((t.Id, type, id))) return;
            db.EntityTags.Add(new EntityTag { TagId = t.Id, EntityType = type, EntityId = id }); tagged++;
        }
        foreach (var c in customers.Where(_ => r.Next(100) < 30))
        {
            var t = c.Type == CustomerType.Partner ? tags[5] : c.Type == CustomerType.Customer ? Pick(new[] { tags[0], tags[1], tags[3], tags[6] }) : Pick(new[] { tags[2], tags[4], tags[6] });
            TagIt(t, "Customer", c.Id);
        }
        foreach (var id in await db.Leads.IgnoreQueryFilters().Where(l => l.Status != LeadStatus.Converted).Select(l => l.Id).ToListAsync())
            if (r.Next(100) < 20) TagIt(Pick(new[] { tags[4], tags[6], tags[2] }), "Lead", id);
        foreach (var id in await db.Opportunities.IgnoreQueryFilters().Where(o => o.Stage != OpportunityStage.ClosedLost).Select(o => o.Id).ToListAsync())
            if (r.Next(100) < 15) TagIt(Pick(new[] { tags[0], tags[2], tags[6] }), "Opportunity", id);

        // ---------- 3. Trường tùy biến ----------
        int fields = 0, values = 0;
        async Task<CustomFieldDef> EnsureField(string entity, string label, CustomFieldType type, string? options, bool inList, int order)
        {
            var f = await db.CustomFieldDefs.FirstOrDefaultAsync(x => x.EntityType == entity && x.Label == label);
            if (f != null) return f;
            f = new CustomFieldDef { EntityType = entity, Label = label, Type = type, Options = options, ShowInList = inList, SortOrder = order, IsActive = true };
            db.CustomFieldDefs.Add(f); fields++;
            return f;
        }
        var fSize = await EnsureField("Customer", "Quy mô doanh thu", CustomFieldType.Select, "Dưới 10 tỷ\n10–50 tỷ\n50–200 tỷ\nTrên 200 tỷ", true, 1);
        var fSoft = await EnsureField("Customer", "Phần mềm đang dùng", CustomFieldType.Text, null, false, 2);
        var fBudget = await EnsureField("Lead", "Ngân sách dự kiến (triệu đồng)", CustomFieldType.Number, null, true, 1);
        var fDemo = await EnsureField("Opportunity", "Đã demo sản phẩm", CustomFieldType.Checkbox, null, false, 1);
        await db.SaveChangesAsync();
        var hasValue = (await db.CustomFieldValues.Select(v => new { v.FieldId, v.EntityId }).ToListAsync()).Select(v => (v.FieldId, v.EntityId)).ToHashSet();
        void Val(CustomFieldDef f, int id, string v) { if (hasValue.Add((f.Id, id))) { db.CustomFieldValues.Add(new CustomFieldValue { FieldId = f.Id, EntityId = id, Value = v }); values++; } }
        var sizes = fSize.Options!.Split('\n');
        var softs = new[] { "MISA SME", "Fast Accounting", "Excel", "SAP Business One", "Bravo", "Odoo", "Phần mềm tự viết" };
        foreach (var c in customers.Where(c => c.Type is CustomerType.Customer or CustomerType.Partner))
        {
            if (r.Next(100) < 75) Val(fSize, c.Id, (c.EmployeeCount ?? 0) switch { < 50 => sizes[0], < 200 => sizes[1], < 1000 => sizes[2], _ => sizes[3] });
            if (r.Next(100) < 50) Val(fSoft, c.Id, Pick(softs));
        }
        foreach (var l in await db.Leads.IgnoreQueryFilters().Where(l => l.Status != LeadStatus.Converted).Select(l => new { l.Id, l.EstimatedValue }).ToListAsync())
            if (r.Next(100) < 60) Val(fBudget, l.Id, Math.Max(20, Math.Round(l.EstimatedValue / 1_000_000m * (0.8m + (decimal)r.NextDouble() * 0.4m), 0)).ToString("0"));
        foreach (var id in await db.Opportunities.IgnoreQueryFilters().Where(o => o.Stage >= OpportunityStage.Proposal || o.Stage == OpportunityStage.ClosedWon).Select(o => o.Id).ToListAsync())
            if (r.Next(100) < 55) Val(fDemo, id, "true");

        // ---------- 4. SLA: gói VIP cho khách doanh thu cao và một phần hợp đồng ----------
        int vipCustomers = 0, vipContracts = 0;
        var vip = await db.SlaPolicies.FirstOrDefaultAsync(p => p.Name == "Khách hàng VIP" && p.IsActive);
        if (vip != null)
        {
            var revenue = await db.Opportunities.IgnoreQueryFilters().Where(o => o.Stage == OpportunityStage.ClosedWon && o.CustomerId != null)
                .GroupBy(o => o.CustomerId!.Value).Select(g => new { g.Key, V = g.Sum(o => o.Amount) }).OrderByDescending(x => x.V).ToListAsync();
            var topIds = revenue.Take(Math.Max(3, revenue.Count / 10)).Select(x => x.Key).ToHashSet();
            foreach (var c in customers.Where(c => topIds.Contains(c.Id) && c.SlaPolicyId == null)) { c.SlaPolicyId = vip.Id; vipCustomers++; if (r.Next(100) < 70) TagIt(tags[0], "Customer", c.Id); }
            foreach (var k in await db.Contracts.IgnoreQueryFilters().Where(k => k.Status == ContractStatus.Active && k.SlaPolicyId == null).ToListAsync())
                if (r.Next(100) < 15) { k.SlaPolicyId = vip.Id; vipContracts++; }
        }

        // ---------- 5. Nhóm dự báo do nhân viên đánh giá ----------
        var forecastSet = 0;
        foreach (var o in await db.Opportunities.IgnoreQueryFilters().Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost && o.ForecastCategory == null).ToListAsync())
        {
            var x = r.Next(100);
            if (x >= 25) continue;
            o.ForecastCategory = x < 3 ? ForecastCategory.Omitted
                : o.Probability >= 50 && x < 15 ? ForecastCategory.Commit
                : o.Probability >= 25 ? ForecastCategory.BestCase : ForecastCategory.Pipeline;
            forecastSet++;
        }

        // ---------- 6. Hợp đồng thanh toán định kỳ ----------
        var recurring = 0;
        foreach (var k in await db.Contracts.IgnoreQueryFilters().Where(k => k.Status == ContractStatus.Active && k.BillingCycle == BillingCycle.None && k.EndDate > today).ToListAsync())
        {
            if (r.Next(100) >= 15) continue;
            k.BillingCycle = Pick(new[] { BillingCycle.Monthly, BillingCycle.Monthly, BillingCycle.Quarterly, BillingCycle.Yearly });
            var months = k.BillingCycle switch { BillingCycle.Monthly => 1, BillingCycle.Quarterly => 3, _ => 12 };
            var term = Math.Max(1, (int)Math.Round(((k.EndDate ?? today.AddYears(1)) - (k.StartDate ?? k.SignDate ?? today)).TotalDays / 30.4));
            k.RecurringAmount = Math.Max(1_000_000m, Math.Round(k.Total * months / term / 100_000m, 0) * 100_000m);
            k.NextBillingDate = new DateTime(today.Year, today.Month, 1).AddMonths(1);
            recurring++;
        }

        // ---------- 7. Báo giá vượt hạn mức chiết khấu: chờ duyệt / đã duyệt ----------
        var approvals = 0;
        var quotes = await db.Quotes.IgnoreQueryFilters().Where(q => q.ApprovalStatus == QuoteApproval.None && (q.Status == QuoteStatus.Draft || q.Status == QuoteStatus.Sent))
            .OrderByDescending(q => q.QuoteDate).Take(40).ToListAsync();
        foreach (var q in quotes.Where(_ => r.Next(100) < 20).Take(8))
        {
            q.EffectiveDiscount = 11 + r.Next(9);
            if (q.Status == QuoteStatus.Draft) q.ApprovalStatus = QuoteApproval.Pending;
            else if (approver != null) { q.ApprovalStatus = QuoteApproval.Approved; q.DiscountApprovedById = approver.Id; q.DiscountApprovedAt = q.QuoteDate.AddHours(4 + r.Next(30)); q.ApprovalNote = "Đồng ý – khách hàng chiến lược"; }
            approvals++;
        }
        await db.SaveChangesAsync();

        // ---------- 8. Khảo sát NPS: một đợt đã đóng (quý trước) và một đợt đang mở ----------
        int surveys = 0, responses = 0, answered = 0;
        var contacts = await db.Contacts.IgnoreQueryFilters().Where(c => c.Email != null && c.CustomerId != null && c.IsPrimary
                && (c.Customer!.Type == CustomerType.Customer || c.Customer.Type == CustomerType.Partner)).ToListAsync();
        var comments = new Dictionary<string, string[]>
        {
            ["pro"] = new[] { "Đội ngũ hỗ trợ nhiệt tình, xử lý nhanh.", "Phần mềm ổn định, dễ dùng.", "Tư vấn rất chuyên nghiệp.", "Triển khai đúng tiến độ." },
            ["pas"] = new[] { "Sản phẩm tốt nhưng giá còn cao.", "Cần thêm tài liệu hướng dẫn.", "Thời gian phản hồi đôi lúc hơi chậm." },
            ["det"] = new[] { "Lỗi phát sinh nhiều sau khi nâng cấp.", "Gọi hotline khó gặp người hỗ trợ.", "Chưa đáp ứng được nhu cầu báo cáo." }
        };
        async Task Survey(string name, DateTime sentAt, NpsStatus status, int answerRate)
        {
            if (contacts.Count == 0) return;
            var s = new NpsSurvey { Name = name, Question = "Anh/Chị sẵn sàng giới thiệu chúng tôi tới bạn bè, đối tác ở mức nào?", Status = status, CreatedById = approver?.Id, CreatedAt = sentAt.AddHours(-2), SentAt = sentAt };
            db.NpsSurveys.Add(s); surveys++;
            foreach (var c in contacts.Where(_ => r.Next(100) < 70))
            {
                var resp = new NpsResponse { Survey = s, ContactId = c.Id, CustomerId = c.CustomerId, Email = c.Email!, Token = SecurityText.NewToken()[..40], SentAt = sentAt };
                if (r.Next(100) < answerRate)
                {
                    var x = r.Next(100);
                    var (score, kind) = x < 45 ? (9 + r.Next(2), "pro") : x < 75 ? (7 + r.Next(2), "pas") : (r.Next(7), "det");
                    resp.Score = score; resp.RespondedAt = sentAt.AddHours(2 + r.Next(24 * 9));
                    if (resp.RespondedAt > now) resp.RespondedAt = now.AddMinutes(-r.Next(600));
                    if (r.Next(100) < 55) resp.Comment = Pick(comments[kind]);
                    answered++;
                }
                db.NpsResponses.Add(resp); responses++;
            }
        }
        var q0 = (today.Month - 1) / 3;   // quý hiện tại (0-3)
        var prevQ = q0 == 0 ? 4 : q0; var prevQYear = q0 == 0 ? today.Year - 1 : today.Year;
        await Survey($"Khảo sát NPS quý {prevQ}/{prevQYear}", new DateTime(prevQYear, (prevQ - 1) * 3 + 2, 10, 9, 0, 0), NpsStatus.Closed, 50);
        await Survey($"Khảo sát NPS tháng {today.Month}/{today.Year}", today.AddDays(-8).AddHours(9), NpsStatus.Sent, 30);
        await db.SaveChangesAsync();

        // ---------- 9. Form thu lead website + lead mang UTM ----------
        int forms = 0, webLeads = 0;
        async Task<WebForm> EnsureForm(string name, string title, LeadSource source, bool askCompany)
        {
            var f = await db.WebForms.FirstOrDefaultAsync(x => x.Name == name);
            if (f != null) return f;
            f = new WebForm { Name = name, Token = WebFormService.NewToken(), Title = title, Intro = "Để lại thông tin, chuyên viên của chúng tôi sẽ liên hệ trong thời gian sớm nhất.",
                SubmitText = "Gửi thông tin", Source = source, AskCompany = askCompany, AskMessage = true, RequirePhone = true, AskConsent = true, IsActive = true, CreatedAt = now.AddDays(-150) };
            db.WebForms.Add(f); forms++;
            return f;
        }
        var fLanding = await EnsureForm("Landing page – Đăng ký tư vấn ERP", "Đăng ký tư vấn giải pháp ERP", LeadSource.Website, true);
        var fContact = await EnsureForm("Form liên hệ trên website", "Liên hệ với chúng tôi", LeadSource.Website, true);
        var fEvent = await EnsureForm("Đăng ký hội thảo chuyển đổi số", "Đăng ký tham dự hội thảo", LeadSource.Event, true);
        await db.SaveChangesAsync();
        var utm = new (string Src, string Med, string Camp, WebForm Form, int W)[]
        {
            ("google", "cpc", "erp-q4", fLanding, 30), ("facebook", "social", "erp-q4", fLanding, 20), ("zalo", "social", "zalo-oa", fContact, 10),
            ("newsletter", "email", "ban-tin-thang", fContact, 10), ("google", "organic", "", fContact, 15), ("facebook", "social", "hoi-thao-cds", fEvent, 15)
        };
        var ho = new[] { "Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ", "Võ", "Đặng", "Bùi", "Đỗ" };
        var dem = new[] { "Văn", "Thị", "Minh", "Thanh", "Quốc", "Ngọc", "Hữu", "Thu", "Đức", "Hoài" };
        var ten = new[] { "An", "Bình", "Châu", "Dũng", "Giang", "Hải", "Hạnh", "Khoa", "Lan", "Long", "Mai", "Nam", "Phúc", "Quân", "Sơn", "Thảo", "Trang", "Tuấn", "Vy", "Yến" };
        var comp = new[] { "Công ty TNHH Thương mại", "Công ty CP Đầu tư", "Công ty CP Công nghệ", "Công ty TNHH Sản xuất", "Công ty CP Dịch vụ" };
        var brand = new[] { "Minh Phát", "Thành Đạt", "An Khánh", "Việt Tín", "Hưng Long", "Bảo Ngọc", "Phú Gia", "Tân Á", "Nam Sơn", "Đông Dương" };
        var needs = new[] { "Cần tư vấn phần mềm ERP cho khoảng 50 người dùng.", "Muốn tìm hiểu giải pháp CRM cho đội kinh doanh.", "Cần báo giá triển khai hệ thống kế toán.",
            "Quan tâm giải pháp chấm công, tính lương.", "Đăng ký tham dự hội thảo, đi 2 người.", "Cần demo sản phẩm trong tuần tới." };
        string Slug(string s) => new string(ImportService.Norm(s).Where(char.IsLetterOrDigit).ToArray());
        var wonOpps = await db.Opportunities.IgnoreQueryFilters().Where(o => o.Stage == OpportunityStage.ClosedWon && o.CustomerId != null && o.ClosedAt >= today.AddDays(-120)
                && !db.Leads.Any(l => l.ConvertedOpportunityId == o.Id)).OrderByDescending(o => o.ClosedAt).Take(10).ToListAsync();
        var totalW = utm.Sum(u => u.W);
        for (var i = 0; i < 45; i++)
        {
            var pick = r.Next(totalW); var u = utm[0];
            foreach (var x in utm) { if (pick < x.W) { u = x; break; } pick -= x.W; }
            var full = $"{Pick(ho)} {Pick(dem)} {Pick(ten)}";
            var company = $"{Pick(comp)} {Pick(brand)}";
            var created = now.AddDays(-r.Next(120)).AddMinutes(-r.Next(600));
            var st = r.Next(100);
            var lead = new Lead
            {
                FullName = full, Company = company, JobTitle = Pick(new[] { "Giám đốc", "Kế toán trưởng", "Trưởng phòng IT", "Trưởng phòng kinh doanh", null }),
                Phone = $"09{r.Next(10_000_000, 99_999_999)}", Email = $"{Slug(full.Split(' ')[^1])}.{Slug(full.Split(' ')[0])}{r.Next(10, 99)}@{Slug(company.Split(' ')[^2] + company.Split(' ')[^1])}.vn",
                Description = Pick(needs), Source = u.Form.Source, Rating = Pick(new[] { LeadRating.Hot, LeadRating.Warm, LeadRating.Warm, LeadRating.Cold }),
                Status = st < 40 ? LeadStatus.New : st < 65 ? LeadStatus.Contacted : st < 82 ? LeadStatus.Qualified : LeadStatus.Unqualified,
                EstimatedValue = r.Next(5, 80) * 10_000_000m, OwnerId = sales.Count > 0 ? Pick(sales).Id : null, CreatedAt = created,
                WebFormId = u.Form.Id, UtmSource = u.Src, UtmMedium = u.Med, UtmCampaign = u.Camp == "" ? null : u.Camp,
                LandingPage = u.Form == fEvent ? "https://congty.vn/hoi-thao" : u.Form == fLanding ? "https://congty.vn/erp" : "https://congty.vn/lien-he",
                Referrer = u.Src == "google" ? "https://www.google.com/" : u.Src == "facebook" ? "https://m.facebook.com/" : null, ConsentAt = created
            };
            // Một phần lead đã chuyển đổi thành đơn thắng → báo cáo UTM có doanh số
            if (wonOpps.Count > 0 && r.Next(100) < 25)
            {
                var o = wonOpps[0]; wonOpps.RemoveAt(0);
                lead.Status = LeadStatus.Converted; lead.ConvertedCustomerId = o.CustomerId; lead.ConvertedOpportunityId = o.Id;
                lead.ConvertedAt = o.CreatedAt; lead.CreatedAt = lead.ConvertedAt.Value.AddDays(-r.Next(3, 20)); lead.ConsentAt = lead.CreatedAt; lead.OwnerId = o.OwnerId ?? lead.OwnerId;
            }
            db.Leads.Add(lead);
            u.Form.Submissions++;
            if (u.Form.LastSubmissionAt == null || lead.CreatedAt > u.Form.LastSubmissionAt) u.Form.LastSubmissionAt = lead.CreatedAt;
            webLeads++;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.SuppressAudit = false;

        // ---------- 10. Chấm lại điểm lead, sức khoẻ khách hàng ----------
        await LeadScoring.RecalculateAsync(db, null);
        var health = await services.GetRequiredService<CustomerHealthService>().RecalculateAsync();
        await services.GetRequiredService<LoyaltyService>().BackfillAsync();   // điểm thưởng từ các phiếu thu mẫu
        return new ExtendedDemoResult(priceLists, priced, tags.Count, tagged, fields, values, vipCustomers, vipContracts, forecastSet, recurring, approvals,
            surveys, responses, answered, forms, webLeads, health);
    }
}
