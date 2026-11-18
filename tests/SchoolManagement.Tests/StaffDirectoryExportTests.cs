using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using UglyToad.PdfPig;

namespace SchoolManagement.Tests;

/// <summary>
/// Staff → All School Staff → Download PDF / Download Excel: the same staff as the page (same filters and category tab),
/// each person once with all their roles, Arabic and English names exactly as stored, no database ids. Read-only.
/// Set PDF_PREVIEW_DIR to also save the downloaded files there for a visual check.
/// </summary>
[Collection("db")]
public sealed class StaffDirectoryExportTests(DbFixture fx)
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly string[] Sheets = ["Staff Directory", "Management Team", "Academic Staff", "Administrative & Support"];
    private static readonly Regex Card = new("<article class=\"dir-row\" aria-labelledby=\"sd-(\\d+)\"");

    private static string Squash(string s) => Regex.Replace(s, @"\s+", "");
    private static bool IsArabic(string s) => s.Any(ch => ch is >= '؀' and <= 'ۿ');
    private static string Today => SchoolTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task Preview(string name, byte[] bytes)
    {
        if (Environment.GetEnvironmentVariable("PDF_PREVIEW_DIR") is { Length: > 0 } dir) await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes);
    }

    private static async Task<(string Text, int Pages, string FileName)> PdfAsync(HttpClient client, string url, string? preview = null)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        if (preview is not null) await Preview(preview + ".pdf", bytes);
        using var doc = PdfDocument.Open(bytes);
        return (Squash(string.Concat(doc.GetPages().Select(p => p.Text))), doc.NumberOfPages, response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "");
    }

    private static async Task<(XLWorkbook Book, string FileName)> ExcelAsync(HttpClient client, string url, string? preview = null)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Xlsx, response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(bytes, 0, 2));
        if (preview is not null) await Preview(preview + ".xlsx", bytes);
        return (new XLWorkbook(new MemoryStream(bytes)), response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "");
    }

    /// <summary>The data rows of a staff sheet (header on row 5), as header → text.</summary>
    private static List<Dictionary<string, string>> Rows(XLWorkbook book, string sheet)
    {
        var ws = book.Worksheet(sheet);
        var headers = ws.Row(5).CellsUsed().Select(c => c.GetString()).ToList();
        var rows = new List<Dictionary<string, string>>();
        for (var r = 6; !ws.Cell(r, 2).IsEmpty(); r++)
            rows.Add(headers.Select((h, i) => (h, ws.Cell(r, i + 1).GetFormattedString())).ToDictionary(x => x.h, x => x.Item2));
        return rows;
    }

    [Fact]
    public async Task Exports_list_every_staff_member_once_with_roles_and_respect_the_page_filters()
    {
        // An Arabic-named teacher, a librarian (support) with no phone, a Management Team member, and a teacher who is also an HOD.
        var arabic = "سارة حسن " + DbFixture.Unique();
        int arabicId, head, manager, art;
        int? previousHead;
        string headName, managerName = "Export Leader " + DbFixture.Unique();
        await using (var db = fx.NewContext())
        {
            arabicId = (await DbFixture.Teachers(db).CreateAsync(arabic, "Science Teacher", "0100 123 4567")).EntityId!.Value;
            manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = managerName, ContactNumber = "0111 222 3333" })).EntityId!.Value;
        }
        var (librarian, librarianName) = await fx.NewTeacherAsync("Librarian");
        (head, headName) = await fx.NewTeacherAsync("Senior Art Teacher");
        await using (var db = fx.NewContext())
        {
            art = await db.Departments.Where(d => d.Name == "Art").Select(d => d.Id).SingleAsync();
            previousHead = await db.DepartmentHeads.Where(h => h.DepartmentId == art && h.IsCurrent).Select(h => (int?)h.TeacherId).FirstOrDefaultAsync();
            await db.Teachers.Where(t => t.Id == head).ExecuteUpdateAsync(s => s.SetProperty(t => t.DepartmentId, art));
            Assert.True((await DbFixture.Departments(db).AssignHeadAsync(art, head, SchoolWeek.Today, null)).Succeeded);
        }
        try
        {
            var client = fx.Factory.CreateClient();
            await using var db = fx.NewContext();
            var before = (await db.StaffMembers.CountAsync(), await db.DepartmentHeads.CountAsync(), await db.ManagementMembers.CountAsync());
            var names = await db.StaffMembers.Select(s => s.FullName).ToListAsync();
            var managers = await db.ManagementMembers.CountAsync();
            var ids = await db.StaffMembers.Select(s => s.Id).ToListAsync();

            // The buttons sit with the page actions and carry the page's query string.
            var page = await client.GetStringAsync("/staff/all?category=academic");
            Assert.Contains("href=\"/staff/all/pdf?category=academic\" data-download=\"/staff/all/pdf\"", page);
            Assert.Contains("href=\"/staff/all/excel?category=academic\" data-download=\"/staff/all/excel\"", page);
            var pageCount = Card.Matches(await client.GetStringAsync("/staff/all")).Count;

            // ---------- Excel, whole directory
            var (book, fileName) = await ExcelAsync(client, "/staff/all/excel", "staff-all");
            using (book)
            {
                Assert.Equal($"THRYD_All_School_Staff_{Today}.xlsx", fileName);
                Assert.Equal(Sheets, book.Worksheets.Select(w => w.Name));
                var main = book.Worksheet("Staff Directory");
                Assert.Equal(["#", "Full Name", "Category", "Job Title / Position", "Department", "Contact Number", "Additional Roles", "Employment Type"],
                    main.Tables.Single().Fields.Select(f => f.Name));
                Assert.True(main.Tables.Single().ShowAutoFilter);
                Assert.Equal(5, main.SheetView.SplitRow); // title block + header row frozen
                var rows = Rows(book, "Staff Directory");
                Assert.Equal(names.Count, rows.Count);           // everyone…
                Assert.Equal(pageCount, rows.Count);             // …the page's count…
                Assert.Equal(names.Order(StringComparer.Ordinal), rows.Select(r => r["Full Name"]).Order(StringComparer.Ordinal)); // …once, names exact
                Assert.Equal(Enumerable.Range(1, rows.Count).Select(i => i.ToString()), rows.Select(r => r["#"])); // serial numbers, not ids
                Assert.Equal(rows.Count, Sheets.Skip(1).Sum(s => Rows(book, s).Count)); // the groups split the directory
                Assert.Equal(managers, Rows(book, "Management Team").Count);

                var a = rows.Single(r => r["Full Name"] == arabic);
                Assert.True(IsArabic(a["Full Name"]));
                Assert.Equal("Academic staff", a["Category"]);
                Assert.Equal("0100 123 4567", a["Contact Number"]); // text, zeros kept
                var h = rows.Single(r => r["Full Name"] == headName);
                Assert.Equal("Senior Art Teacher", h["Job Title / Position"]);
                Assert.Equal("Art", h["Department"]);
                Assert.Contains("Head of Art", h["Additional Roles"]);
                var l = rows.Single(r => r["Full Name"] == librarianName);
                Assert.Equal("Administrative & support", l["Category"]);
                Assert.Equal("", l["Contact Number"]); // missing optional fields stay blank
                Assert.Equal("", l["Department"]);
                Assert.Contains(Rows(book, "Administrative & Support"), r => r["Full Name"] == librarianName);
                var m = Rows(book, "Management Team").Single(r => r["Full Name"] == managerName);
                Assert.Equal("Management Team", m["Department"]);
                Assert.Equal("0111 222 3333", m["Contact Number"]);
                // No database id columns on any sheet.
                foreach (var ws in book.Worksheets)
                    Assert.DoesNotContain(ws.Row(5).CellsUsed(), c => c.GetString().Contains("Id", StringComparison.Ordinal));
            }

            // ---------- Excel, filtered: the Management tab leaves the other groups as header-only sheets.
            var (mbook, _) = await ExcelAsync(client, "/staff/all/excel?category=management");
            using (mbook)
            {
                Assert.Equal(Sheets, mbook.Worksheets.Select(w => w.Name));
                Assert.Equal(managers, Rows(mbook, "Staff Directory").Count);
                Assert.All(Rows(mbook, "Staff Directory"), r => Assert.Equal("Management Team", r["Category"]));
                foreach (var empty in new[] { "Academic Staff", "Administrative & Support" })
                {
                    var ws = mbook.Worksheet(empty);
                    Assert.Empty(Rows(mbook, empty));
                    Assert.Equal(["#", "Full Name", "Job Title", "Department", "Contact Number", "Additional Roles", "Employment Type"],
                        ws.Row(5).CellsUsed().Select(c => c.GetString()));
                    Assert.True(ws.AutoFilter.IsEnabled);
                }
                Assert.Contains("Filters: Category: Management Team", mbook.Worksheet("Staff Directory").Cell(3, 1).GetString());
            }

            // Search by an Arabic name and the HOD role: the same people as the page.
            var (sbook, _) = await ExcelAsync(client, "/staff/all/excel?search=" + Uri.EscapeDataString(arabic));
            using (sbook) Assert.Equal([arabic], Rows(sbook, "Staff Directory").Select(r => r["Full Name"]));
            var hodPage = Card.Matches(await client.GetStringAsync("/staff/all?role=hod")).Select(x => int.Parse(x.Groups[1].Value)).ToList();
            var (hbook, _) = await ExcelAsync(client, "/staff/all/excel?role=hod");
            using (hbook)
            {
                var hods = Rows(hbook, "Staff Directory");
                Assert.Equal(hodPage.Count, hods.Count);
                Assert.Contains(hods, r => r["Full Name"] == headName);
                Assert.DoesNotContain(hods, r => r["Full Name"] == arabic);
            }

            // ---------- PDF, whole directory
            var pdf = await PdfAsync(client, "/staff/all/pdf", "staff-all");
            Assert.Equal($"THRYD_All_School_Staff_{Today}.pdf", pdf.FileName);
            foreach (var text in new[] { "THRYD", "All School Staff Directory", "Management Team", "Academic Staff", "Administrative & Support Staff",
                         "Heads of Department", "All Staff", $"Staff members: {names.Count}", headName, librarianName, managerName, "Head of Art", "Page" })
                Assert.True(pdf.Text.Contains(Squash(text)), $"\"{text}\" not in the PDF");
            Assert.Single(Regex.Matches(pdf.Text, Regex.Escape(Squash(headName)))); // the HOD appears once, with both roles
            Assert.Contains(pdf.Text, ch => ch is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﻿'); // the Arabic name is embedded as Arabic text
            foreach (var latin in names.Where(n => !IsArabic(n)))
                Assert.True(pdf.Text.Contains(Squash(latin)), $"\"{latin}\" not in the PDF");

            // PDF, filtered by the HOD role: the same people as the page, with the filter named.
            var hodPdf = await PdfAsync(client, "/staff/all/pdf?role=hod", "staff-hod");
            Assert.Contains(Squash("Filtered export"), hodPdf.Text);
            Assert.Contains(Squash(headName), hodPdf.Text);
            Assert.DoesNotContain(Squash(librarianName), hodPdf.Text);
            Assert.Contains(Squash($"Staff members: {hodPage.Count}"), hodPdf.Text);

            // No matches: both files are still produced.
            var none = await PdfAsync(client, "/staff/all/pdf?search=zzqxnomatch");
            Assert.Contains(Squash("No Management Team members match the filters."), none.Text);
            var (nbook, _) = await ExcelAsync(client, "/staff/all/excel?search=zzqxnomatch");
            using (nbook) Assert.All(Sheets, s => Assert.Empty(Rows(nbook, s)));

            // Read-only.
            Assert.Equal(before, (await db.StaffMembers.CountAsync(), await db.DepartmentHeads.CountAsync(), await db.ManagementMembers.CountAsync()));
            Assert.Equal(ids.Order(), (await db.StaffMembers.Select(s => s.Id).ToListAsync()).Order());
        }
        finally
        {
            await using var db = fx.NewContext();
            await DbFixture.Departments(db).AssignHeadAsync(art, previousHead, SchoolWeek.Today, null); // as it was
        }
    }

    [Fact]
    public void Each_person_is_one_row_with_every_role_and_missing_fields_are_blank()
    {
        StaffRoleBadge Role(string key, string label) => new(key, label, "icon", "tone");
        var people = new List<StaffDirectoryEntry>
        {
            // A Management position holder who is also HOD and on a KG teaching team: one row, the position not repeated in roles.
            new(1, "Mona Adel", ManagementPositions.Name(ManagementPosition.SchoolDirector), 4, "Science", "0100", "mona@example.com", StaffCategory.Management,
                ManagementPosition.SchoolDirector,
                [Role("SchoolDirector", ManagementPositions.Name(ManagementPosition.SchoolDirector)), Role("hod", "Head of Science"), Role("kg-co", "Co-Teacher · KG1A")], false),
            // A Management Team member without a position or contact details.
            new(2, "عبد الرحمن محمود", "Management Team", null, null, null, null, StaffCategory.Management, null, [], false),
            // A teacher with nothing optional set.
            new(3, "Ali Hassan", null, null, null, "  ", null, StaffCategory.Academic, null, [], true),
        };
        var directory = new StaffDirectory(people, people, new StaffDirectoryCounts(3, 1, 2, 0, 1, 1), [(4, "Science")], [], "2026–2027",
            new Dictionary<StaffCategory, int>());
        var x = StaffDirectoryExport.From(directory, new StaffDirectoryQuery { Department = "4", Role = "hod" },
            new Dictionary<int, EmploymentType?> { [1] = EmploymentType.PartTime, [2] = null });

        Assert.Equal(3, x.Rows.Count);
        var mona = x.Rows[0];
        Assert.Equal("School Director", mona.Title);
        Assert.Equal(["Head of Science", "Co-Teacher · KG1A"], mona.Roles);
        Assert.True(mona.IsHead);
        Assert.Equal("Part Time", mona.Employment);
        var arabic = x.Rows[1];
        Assert.Equal("عبد الرحمن محمود", arabic.FullName); // exactly as stored
        Assert.Null(arabic.Title);
        Assert.Equal("Management Team", arabic.Department);
        Assert.Null(arabic.ContactNumber);
        Assert.Null(arabic.Employment);
        Assert.Null(x.Rows[2].ContactNumber);
        Assert.Null(x.Rows[2].Department);
        Assert.Equal(1, x.Heads);
        Assert.Equal([("Department", "Science"), ("Position / role", "Head of Department")], x.Filters);
        Assert.Equal(["Mona Adel", "عبد الرحمن محمود", "Ali Hassan"], x.Ordered.Select(r => r.FullName));
        Assert.Equal("THRYD_All_School_Staff_2026-11-18.pdf", StaffDirectoryExport.FileName(new DateOnly(2026, 11, 18), "pdf"));

        // The workbook: the Support group has no one, so its sheet has the headers and no data rows.
        using var book = new XLWorkbook(new MemoryStream(new ExcelExportService().StaffDirectory(x, "Test School")));
        Assert.Equal(Sheets, book.Worksheets.Select(w => w.Name));
        Assert.Equal(3, Rows(book, "Staff Directory").Count);
        Assert.Equal("Head of Science; Co-Teacher · KG1A", Rows(book, "Staff Directory")[0]["Additional Roles"]);
        Assert.Equal("mona@example.com", Rows(book, "Management Team")[0]["Email"]);
        Assert.Equal("عبد الرحمن محمود", Rows(book, "Management Team")[1]["Full Name"]);
        Assert.Empty(Rows(book, "Administrative & Support"));
        Assert.Equal("Employment Type", book.Worksheet("Administrative & Support").Cell(5, 7).GetString());
    }
}
