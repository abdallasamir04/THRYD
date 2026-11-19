using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Import;
using static SchoolManagement.Tests.TaskImportExportTests;

namespace SchoolManagement.Tests;

/// <summary>
/// Import / Export → Classes, through the running app: the fourth card, the template, upload (.xlsx and .csv), column
/// matching, a preview that saves nothing and reports every invalid or duplicate row, confirm creating classes with
/// Add Class's own rules (default capacity 22, a given capacity kept, existing classes never changed), and the export
/// with each class's own capacity, current students and available seats.
/// </summary>
[Collection("db")]
public sealed class ClassImportExportTests(DbFixture fx) : IAsyncLifetime
{
    /// <summary>Classes a test imports; removed after it (they have no students), so other tests see the usual class list.</summary>
    private readonly List<string> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_created.Count == 0) return;
        await using var db = fx.NewContext();
        await db.Classes.Where(c => _created.Contains(c.Name) && !c.Students.Any()).ExecuteDeleteAsync();
    }

    private HttpClient Client() => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A section of three letters no other test uses (letters only).</summary>
    private static string Section() => DbFixture.Unique()[..3].ToUpperInvariant();

    private static byte[] Workbook(params string?[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Classes");
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (rows[r][c] is { } v) ws.Cell(r + 1, c + 1).SetValue(v);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("Pre-K", "PREK", 0)]
    [InlineData("pre kg", "PREK", 0)]
    [InlineData("KG1", "KG", 1)]
    [InlineData("KG 2", "KG", 2)]
    [InlineData("Grade 3", "G", 3)]
    [InlineData("G10", "G", 10)]
    [InlineData("7", "G", 7)]
    [InlineData("Year 4", "G", 4)]
    public void Grade_levels_are_read_in_the_usual_spellings(string text, string stage, int grade) =>
        Assert.Equal((stage, grade), ImportService.ParseGradeLevel(text));

    [Theory]
    [InlineData("Primary")]
    [InlineData("Grade")]
    [InlineData("")]
    public void Other_text_is_not_a_grade_level(string text) => Assert.Null(ImportService.ParseGradeLevel(text));

    [Fact]
    public async Task Classes_is_the_fourth_card_with_the_school_s_classes_and_its_own_import_and_export()
    {
        var client = Client();
        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/import-export?kind=Classes"));
        Assert.Equal(4, Regex.Matches(page, "<a class=\"kind-tab\"").Count);
        Assert.Contains("<a class=\"kind-tab\" href=\"/import-export?kind=Classes\" aria-current=\"true\">", page);
        await using (var db = fx.NewContext())
            Assert.Contains($">Classes</span><br /><span class=\"kind-tab__meta\">{await db.Classes.CountAsync()} classes now · Pre-K – ", page);
        Assert.Contains("Import classes", page);
        Assert.Contains("/import-export/classes/export?format=xlsx", page);
        Assert.Contains("/import-export/classes/export?format=csv", page);
        Assert.DoesNotContain("tasks/export", page);
        Assert.DoesNotContain("All students (", page);
        // The other cards keep their own sections.
        Assert.Contains("All students (", WebUtility.HtmlDecode(await client.GetStringAsync("/import-export?kind=Students")));
        Assert.Contains("tasks/export", await client.GetStringAsync("/import-export?kind=Tasks"));

        using var t = new XLWorkbook(new MemoryStream(await client.GetByteArrayAsync("/import-export/template/Classes")));
        Assert.Equal(["Class Name", "Grade Level", "Section", "Capacity", "Active Status"], t.Worksheet("Classes").Row(1).CellsUsed().Select(c => c.GetString()));
        Assert.Contains(t.Worksheet("Existing classes").Column(1).CellsUsed(), c => c.GetString() == "G3A");
    }

    [Fact]
    public async Task An_excel_import_checks_every_row_never_changes_existing_classes_and_creates_new_ones_by_Add_Class_rules()
    {
        var (a, b, c, d, e, f) = (Section(), Section(), Section(), Section(), Section(), Section());
        int g3aCapacity, g3aStudents, before;
        await using (var db = fx.NewContext())
        {
            (g3aCapacity, g3aStudents) = await db.Classes.Where(x => x.Name == "G3A").Select(x => new ValueTuple<int, int>(x.Capacity, x.Students.Count)).SingleAsync();
            before = await db.Classes.CountAsync();
        }
        _created.AddRange([$"G3{a}", $"KG1{b}", $"G6{f}"]);
        var client = Client();
        var token = await UploadAndMatchAsync(client, "Classes", "classes.xlsx", Workbook(
            ["Class Name", "Grade Level", "Section", "Capacity", "Active Status"],
            [$"G3{a}", "Grade 3", a, null, null],                // 2 ready: capacity 22, active
            [$"KG1{b}", "KG1", b, "30", "Inactive"],              // 3 ready: capacity 30 kept, inactive
            ["G3A", "Grade 3", "A", "40", "Active"],              // 4 duplicate: exists, not changed
            [$"G3{a}", "3", null, "25", null],                    // 5 duplicate: row 2 again
            [$"G5{c}", "Grade 4", null, null, null],              // 6 grade does not match the name
            ["KG3A", "KG3", "A", null, null],                     // 7 no KG3 in the school
            [$"G2{d}", "Grade 2", "1A", null, null],              // 8 bad section
            [$"G2{d}", "Grade 2", null, "abc", null],             // 9 bad capacity
            [$"G2{e}", "Grade 2", null, "0", null],               // 10 capacity out of range
            [null, "Grade 2", "B", null, null],                   // 11 missing name
            [$"G2{f}", "Grade 2", null, null, "maybe"],           // 12 bad status
            [$"g 6-{f.ToLowerInvariant()}", "6", null, "18", "yes"],   // 13 ready, saved as G6…
            ["Hello", null, null, null, null]));                   // 14 not a class name, no grade
        var (html, rows) = await PreviewAsync(client, token);
        string Status(int row) => rows.Single(r => r.Number == row).Status;
        string Text(int row) => rows.Single(r => r.Number == row).Text;
        Assert.Equal(["Ready", "Ready", "Duplicate", "Duplicate", "Invalid", "Invalid", "Invalid", "Invalid", "Invalid", "Invalid", "Invalid", "Ready", "Invalid"],
            Enumerable.Range(2, 13).Select(Status));
        Assert.Contains($"No capacity given: the standard capacity of {SchoolClass.DefaultCapacity} is used.", Text(2));
        Assert.Contains("(standard)", Text(2));
        Assert.Contains($"G3A already exists ({g3aStudents} students, capacity {g3aCapacity}, active). It is not changed.", Text(4));
        Assert.Contains("is already in this file (row 2)", Text(5));
        Assert.Contains("is Grade 5, but the grade level says Grade 4", Text(6));
        Assert.Contains("KG3 is not a grade in THRYD", Text(7));
        Assert.Contains("“1A” is not a section", Text(8));
        Assert.Contains("“abc” is not a capacity", Text(9));
        Assert.Contains("out of range", Text(10));
        Assert.Contains("The class name is missing", Text(11));
        Assert.Contains("“maybe” is not a status", Text(12));
        Assert.Contains($"<span class=\"badge badge--class\">G6{f}</span>", Text(13));        // the name it is saved as
        Assert.Contains("is not a class name in THRYD's format", Text(14));
        Assert.Contains("The grade level is missing", Text(14));
        await using (var db = fx.NewContext()) Assert.Equal(before, await db.Classes.CountAsync());   // the preview saved nothing

        await ConfirmAsync(client, token, html);
        var result = WebUtility.HtmlDecode(await client.GetStringAsync($"/import-export/{token}/result"));
        Assert.Contains("/classes/all", result);
        await using (var db = fx.NewContext())
        {
            Assert.Equal(before + 3, await db.Classes.CountAsync());
            var made = await db.Classes.AsNoTracking().Where(x => x.Name == $"G3{a}" || x.Name == $"KG1{b}" || x.Name == $"G6{f}").ToDictionaryAsync(x => x.Name);
            Assert.Equal((SchoolClass.DefaultCapacity, true), (made[$"G3{a}"].Capacity, made[$"G3{a}"].IsActive));
            Assert.Equal((30, false), (made[$"KG1{b}"].Capacity, made[$"KG1{b}"].IsActive));
            Assert.Equal((18, true), (made[$"G6{f}"].Capacity, made[$"G6{f}"].IsActive));
            Assert.All(made.Values, x => Assert.Equal(Web.Common.TextNormalizer.ClassKey(x.Name), x.NormalizedName));
            // The existing class is exactly as it was.
            Assert.Equal((g3aCapacity, g3aStudents), await db.Classes.Where(x => x.Name == "G3A").Select(x => new ValueTuple<int, int>(x.Capacity, x.Students.Count)).SingleAsync());
        }
    }

    [Fact]
    public async Task A_csv_import_creates_a_class_with_the_capacity_given()
    {
        var sec = Section();
        _created.Add($"G7{sec}");
        var client = Client();
        var token = await UploadAndMatchAsync(client, "Classes", "classes.csv", Encoding.UTF8.GetBytes($"Class,Grade,Capacity\r\nG7{sec},G7,25\r\n"));
        var (html, rows) = await PreviewAsync(client, token);
        Assert.Equal("Ready", Assert.Single(rows).Status);
        await ConfirmAsync(client, token, html);
        await using var db = fx.NewContext();
        var cls = await db.Classes.AsNoTracking().SingleAsync(x => x.Name == $"G7{sec}");
        Assert.Equal((25, true), (cls.Capacity, cls.IsActive));
    }

    [Fact]
    public async Task The_export_lists_every_class_with_its_own_capacity_students_and_available_seats()
    {
        var cls = await fx.NewClassAsync();
        for (var i = 0; i < 3; i++) await fx.NewStudentAsync(cls);
        string name;
        await using (var db = fx.NewContext())
        {
            await db.Classes.Where(x => x.Id == cls).ExecuteUpdateAsync(x => x.SetProperty(c => c.Capacity, 5));
            name = await db.Classes.Where(x => x.Id == cls).Select(x => x.Name).SingleAsync();
        }
        var client = Client();
        var xlsx = await client.GetAsync("/import-export/classes/export?format=xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        using (var wb = new XLWorkbook(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync())))
        {
            var ws = wb.Worksheet("Classes");
            Assert.Equal(ExcelExportService.ClassExportHeaders, ws.Row(1).CellsUsed().Select(c => c.GetString()));
            await using var db = fx.NewContext();
            Assert.Equal(await db.Classes.CountAsync(), ws.RowsUsed().Count() - 1);
            var row = ws.RowsUsed().Single(r => r.Cell(2).GetString() == name);
            Assert.Equal((5, "Active", 3, 2, 0), (row.Cell(5).GetValue<int>(), row.Cell(6).GetString(), row.Cell(7).GetValue<int>(), row.Cell(8).GetValue<int>(), row.Cell(9).GetValue<int>()));
            var g3a = ws.RowsUsed().Single(r => r.Cell(2).GetString() == "G3A");
            Assert.Equal(("Grade 3", "A"), (g3a.Cell(3).GetString(), g3a.Cell(4).GetString()));
            var prek = ws.RowsUsed().Single(r => r.Cell(2).GetString() == "PreK");
            Assert.Equal(("Pre-K", "A"), (prek.Cell(3).GetString(), prek.Cell(4).GetString()));
        }

        var csv = await client.GetAsync("/import-export/classes/export?format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var grid = SpreadsheetReader.Read(new MemoryStream(await csv.Content.ReadAsByteArrayAsync()), "classes.csv").Single();
        Assert.Equal(ExcelExportService.ClassExportHeaders, grid.Rows[0].Cells.Take(ExcelExportService.ClassExportHeaders.Count));
        var line = grid.Rows.Single(r => r.Cell(1) == name);
        Assert.Equal(["5", "Active", "3", "2", "0"], new[] { line.Cell(4), line.Cell(5), line.Cell(6), line.Cell(7), line.Cell(8) });

        // The export can be imported again: every class is reported as existing and nothing changes.
        var token = await UploadAndMatchAsync(client, "Classes", "export.csv", await (await client.GetAsync("/import-export/classes/export?format=csv")).Content.ReadAsByteArrayAsync());
        var (_, rows) = await PreviewAsync(client, token);
        Assert.DoesNotContain(rows, r => r.Status == "Ready");
        Assert.Contains(rows, r => r.Status == "Duplicate" && r.Text.Contains("G3A already exists"));
    }
}
