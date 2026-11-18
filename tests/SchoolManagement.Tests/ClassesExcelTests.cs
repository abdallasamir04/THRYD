using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;

namespace SchoolManagement.Tests;

/// <summary>
/// Classes page → Download Summary Excel / Download Students Excel: valid workbooks, read back with ClosedXML, with the same
/// data and rules as the two PDFs (counts, order, grades, places, teaching team), no student ids, and names intact.
/// </summary>
[Collection("db")]
public sealed class ClassesExcelTests(DbFixture fx)
{
    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Excel tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static string V(IXLCell c) => c.GetFormattedString().Trim();

    private async Task<(XLWorkbook Book, string FileName)> DownloadAsync(string url)
    {
        var response = await fx.Factory.CreateClient().GetAsync(url);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(bytes, 0, 2));
        return (new XLWorkbook(new MemoryStream(bytes)), response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "");
    }

    [Fact]
    public async Task Summary_workbook_has_overview_stage_and_class_sheets_matching_the_database()
    {
        await using var db = fx.NewContext();
        var (book, fileName) = await DownloadAsync("/classes/summary-excel");
        using var _ = book;
        Assert.Matches(@"^Classes_Summary(_\w+)?\.xlsx$", fileName);
        Assert.Equal(["Overview", "Stage Summary", "Class Summary"], book.Worksheets.Select(w => w.Name));

        var classes = await db.Classes.AsNoTracking().Select(c => new { c.Id, c.Name, c.Capacity, c.IsActive, Students = c.Students.Count }).ToListAsync();
        var overview = book.Worksheet("Overview").RangeUsed()!.Rows().Where(r => !r.Cell(1).IsEmpty()).ToDictionary(r => V(r.Cell(1)), r => V(r.Cell(2)).Replace(",", ""));
        Assert.Equal(classes.Count.ToString(), overview["Total classes"]);
        Assert.Equal(classes.Count(c => c.IsActive).ToString(), overview["Active classes"]);
        Assert.Equal(classes.Count(c => !c.IsActive).ToString(), overview["Inactive classes"]);
        Assert.Equal(classes.Sum(c => c.Students).ToString(), overview["Total enrolled students"]);
        Assert.Equal(classes.Sum(c => c.Capacity).ToString(), overview["Total capacity"]);
        Assert.Equal(classes.Sum(c => Math.Max(0, c.Capacity - c.Students)).ToString(), overview["Available places"]);
        foreach (var key in new[] { "School", "Academic Year", "Exported" }) Assert.True(overview.ContainsKey(key), key);

        var stage = book.Worksheet("Stage Summary").Tables.Single();
        Assert.Equal(["Stage Name", "Grade", "Number of Classes", "Enrolled Students", "Total Capacity", "Available Places", "Coordinator"], stage.Fields.Select(f => f.Name));
        Assert.True(stage.ShowAutoFilter);
        Assert.True(stage.ShowTotalsRow);
        Assert.Equal(classes.Count, stage.DataRange.Rows().Sum(r => r.Cell(3).GetValue<int>()));
        Assert.Equal(classes.Sum(c => c.Students), stage.DataRange.Rows().Sum(r => r.Cell(4).GetValue<int>()));

        var cs = book.Worksheet("Class Summary");
        var table = cs.Tables.Single();
        Assert.Equal(["Class Name", "Academic Stage", "Grade", "Enrolled Students", "Class Capacity", "Available Places", "Capacity Status", "Status",
            "Homeroom Teacher", "Co-Teacher", "Stage Coordinator"], table.Fields.Select(f => f.Name));
        Assert.Equal(4, cs.SheetView.SplitRow);                                  // header row frozen
        var rows = table.DataRange.Rows().ToList();
        Assert.Equal(classes.Count, rows.Count);
        foreach (var row in rows)
        {
            var c = classes.Single(x => x.Name == V(row.Cell(1)));
            Assert.Equal((c.Students, c.Capacity, Math.Max(0, c.Capacity - c.Students)),
                (row.Cell(4).GetValue<int>(), row.Cell(5).GetValue<int>(), row.Cell(6).GetValue<int>()));
            Assert.Equal(c.IsActive ? "Active" : "Inactive", V(row.Cell(8)));
            Assert.False(string.IsNullOrWhiteSpace(V(row.Cell(9))));            // a name or "Not Assigned"
        }
        // Same order and grades as the PDFs (ClassRosterReports.Placed).
        var roster = await new ClassRosterService(db, new ClassTeamService(db, new TestUser())).LoadAsync();
        Assert.Equal(ClassRosterReports.Placed(roster).Select(x => (x.Class.Name, ClassRosterReports.Grade(x.Class))),
            rows.Select(r => (V(r.Cell(1)), V(r.Cell(3)))));
    }

    [Fact]
    public async Task Students_workbook_has_a_sheet_per_class_with_numbered_names_only()
    {
        await using var db = fx.NewContext();
        var teams = new ClassTeamService(db, new TestUser());
        var year = await teams.CurrentYearAsync();
        // A KG1 class of this test only: a Homeroom Teacher, an Arabic and two English names (removed at the end).
        var created = await DbFixture.Classes(db).CreateInGradeAsync("KG", 1, Web.Common.TextNormalizer.ClassKey(DbFixture.Unique()[..3]).ToUpperInvariant(), 20, true);
        Assert.True(created.Succeeded);
        var kg = created.EntityId!.Value;
        const string arabic = "مريم أحمد السيد";
        try
        {
            var (teacher, teacherName) = await fx.NewTeacherAsync();
            if (year is not null) Assert.True((await teams.SaveAsync(kg, year.Id, teacher, null)).Succeeded);
            foreach (var name in new[] { "Zeina Omar Fahmy", arabic, "Adam Karim Lotfy" })
                db.Students.Add(new Student { FullName = name, NormalizedName = name.ToUpperInvariant(), SchoolClassId = kg });
            await db.SaveChangesAsync();

            var (book, fileName) = await DownloadAsync("/classes/students-excel");
            using var _ = book;
            Assert.Matches(@"^Classes_and_Students_Report(_\w+)?\.xlsx$", fileName);

            var roster = await new ClassRosterService(db, new ClassTeamService(db, new TestUser())).LoadAsync();
            var placed = ClassRosterReports.Placed(roster).ToList();
            var sheets = book.Worksheets.ToList();
            Assert.Equal("Class Index", sheets[0].Name);
            Assert.Equal(placed.Select(x => x.Class.Name), sheets.Skip(1).Select(s => s.Name));   // one sheet per class, named after it
            Assert.Equal(placed.Count, sheets[0].Tables.Single().DataRange.RowCount());

            var listed = 0;
            foreach (var (ws, c) in sheets.Skip(1).Zip(placed.Select(x => x.Class)))
            {
                // Exactly # and Student Name; numbering 1, 2, 3… restarting per class; the PDFs' alphabetical order.
                Assert.Equal(("#", "Student Name", ""), (V(ws.Cell(4, 1)), V(ws.Cell(4, 2)), V(ws.Cell(4, 3))));
                for (var i = 0; i < c.Students.Count; i++)
                {
                    Assert.Equal(i + 1, ws.Cell(5 + i, 1).GetValue<int>());
                    Assert.Equal(c.Students[i].FullName, ws.Cell(5 + i, 2).GetString());
                }
                Assert.True(ws.Cell(5 + c.Students.Count, 1).IsEmpty());
                Assert.Equal(4, ws.SheetView.SplitRow);
                // No student id anywhere in the sheet (the names themselves are skipped: a name such as "Student Idris…" is not an id).
                bool IsName(IXLCell cell) => cell.Address.ColumnNumber == 2 && cell.Address.RowNumber >= 5 && cell.Address.RowNumber < 5 + c.Students.Count;
                Assert.DoesNotContain(ws.RangeUsed()!.Cells(), cell => !IsName(cell) && V(cell).Contains("Student ID", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(ws.Range(5, 2, 5 + Math.Max(0, c.Students.Count - 1), 2).Cells(), cell => cell.DataType == XLDataType.Number);
                var info = Enumerable.Range(5, 13).ToDictionary(r => V(ws.Cell(r, 4)), r => V(ws.Cell(r, 5)));
                Assert.Equal((c.Students.Count.ToString(), c.Capacity.ToString(), ClassRosterReports.Free(c).ToString()),
                    (info["Total Enrolled Students"], info["Class Capacity"], info["Available Places"]));
                Assert.Equal(c.Homeroom ?? "Not Assigned", info["Homeroom Teacher"]);
                listed += c.Students.Count;
            }
            Assert.Equal(await db.Students.CountAsync(), listed);

            // The test class: names stored exactly (Arabic included), the teacher shown, the co-teacher "Not Assigned".
            var mine = book.Worksheet(roster.AllClasses.Single(c => c.Id == kg).Name);
            var names = Enumerable.Range(5, 3).Select(r => mine.Cell(r, 2).GetString()).ToList();
            Assert.Contains(arabic, names);
            Assert.Equal(roster.AllClasses.Single(c => c.Id == kg).Students.Select(s => s.FullName), names);
            var details = Enumerable.Range(5, 13).ToDictionary(r => V(mine.Cell(r, 4)), r => V(mine.Cell(r, 5)));
            if (year is not null) Assert.Equal(teacherName, details["Homeroom Teacher"]);
            Assert.Equal("Not Assigned", details["Co-Teacher"]);
            Assert.Equal(("KG Stages", "KG1"), (details["Academic Stage"], details["Grade"]));
        }
        finally
        {
            await db.Students.Where(s => s.SchoolClassId == kg).ExecuteDeleteAsync();
            await db.ClassTeachingHistory.Where(h => h.SchoolClassId == kg).ExecuteDeleteAsync();
            await db.ClassTeachingAssignments.Where(a => a.SchoolClassId == kg).ExecuteDeleteAsync();
            await db.Classes.Where(c => c.Id == kg).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Pdf_downloads_still_work_beside_the_workbooks()
    {
        var client = fx.Factory.CreateClient();
        foreach (var url in new[] { "/classes/pdf", "/classes/students-report" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await response.Content.ReadAsByteArrayAsync())[..4]));
        }
    }
}
