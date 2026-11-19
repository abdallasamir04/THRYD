using ClosedXML.Excel;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Timetables.Import;

namespace SchoolManagement.Tests;

/// <summary>Reading a timetable grid from a spreadsheet (no database): days, periods, times, breaks and cell kinds.</summary>
public class TimetableExtractorTests
{
    /// <summary>An .xlsx built from rows of cell texts (null = blank); merges are "A1:F1"-style ranges.</summary>
    internal static byte[] Workbook(params (string Sheet, string?[][] Rows, string[] Merges)[] sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var (name, rows, merges) in sheets)
        {
            var ws = wb.AddWorksheet(name);
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] is { } text) ws.Cell(r + 1, c + 1).Value = text;
            foreach (var m in merges) ws.Range(m).Merge();
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static ExtractedTimetable Read(byte[] bytes, string? classKey = null)
    {
        using var ms = new MemoryStream(bytes);
        var table = TimetableExtractor.Extract(SpreadsheetReader.Read(ms, "t.xlsx", fillMergedCells: true), classKey, out var error);
        Assert.True(table is not null, error);
        return table!;
    }

    [Fact]
    public void Days_across_with_times_a_merged_break_teachers_and_a_shorter_day()
    {
        var bytes = Workbook(("G1A", [
            ["Timetable — Class G1A", null, null, null, null, null, null],
            ["Period", "Time", "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday"],
            ["1", "08:00 - 08:45", "Math\nMs Hana", "English - Mr Ali", "Science", "Arabic", "PE"],
            ["2", "08:45-09:30", "Art", "Free", "Math", "-", "Music"],
            [null, "09:30 – 09:50", "BREAK", null, null, null, null],
            ["Period 3", "9:50 AM to 10:35 AM", "ICT (Room 4)", "Math", "English", "Science", null],
            ["Class teacher: Ms Hana", null, null, null, null, null, null],
        ], ["C5:G5"]));
        var t = Read(bytes);

        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday], t.Days);
        Assert.Equal(4, t.Periods.Count);                                   // the footer is not a period
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(8, 45)), (t.Periods[0].Start, t.Periods[0].End));
        Assert.Equal((new TimeOnly(9, 50), new TimeOnly(10, 35)), (t.Periods[3].Start, t.Periods[3].End));
        Assert.Equal(1, t.Periods[0].Number);
        Assert.Equal(3, t.Periods[3].Number);
        Assert.Contains("G1A", t.ClassesMentioned);

        Assert.Equal((ImportCellKind.Lesson, "Math\nMs Hana"), (t.Cell(0, DayOfWeek.Sunday)!.Kind, t.Cell(0, DayOfWeek.Sunday)!.Raw));
        Assert.Equal(ImportCellKind.Free, t.Cell(1, DayOfWeek.Monday)!.Kind);       // "Free"
        Assert.Equal(ImportCellKind.Free, t.Cell(1, DayOfWeek.Wednesday)!.Kind);    // "-"
        // The merged BREAK row: a break on every day, named after the cell.
        Assert.All(t.Days, d => Assert.Equal(ImportCellKind.Break, t.Cell(2, d)!.Kind));
        Assert.Equal("BREAK", t.Cell(2, DayOfWeek.Monday)!.Label);
        // Thursday has no period 3 (the day ends earlier): not a free period.
        Assert.Equal(ImportCellKind.NoPeriod, t.Cell(3, DayOfWeek.Thursday)!.Kind);
        Assert.Contains(t.Notes, n => n.Contains("Class teacher"));
    }

    [Fact]
    public void Days_down_the_side_are_read_transposed()
    {
        var bytes = Workbook(("Week", [
            ["Day", "Period 1", "Period 2", "Lunch", "Period 3"],
            [null, "8:00-8:40", "8:40-9:20", "9:20-9:50", "9:50-10:30"],
            ["Sun", "Math", "English", "Lunch", "Science"],
            ["Mon", "Arabic", "Math", "Lunch", "Art"],
            ["Tue", "Science", "PE", "Lunch", "Math"],
        ], []));
        var t = Read(bytes);
        Assert.True(t.DaysInRows);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday], t.Days);
        Assert.Equal(["Period 1", "Period 2", "Lunch", "Period 3"], t.Periods.Select(p => p.Label));
        Assert.Equal(new TimeOnly(9, 50), t.Periods[3].Start);
        Assert.Equal(ImportCellKind.Break, t.Cell(2, DayOfWeek.Tuesday)!.Kind);
        Assert.Equal("English", t.Cell(1, DayOfWeek.Sunday)!.Raw);
        Assert.Contains(t.Notes, n => n.Contains("Wednesday") && n.Contains("Thursday"));   // missing days are reported
    }

    [Fact]
    public void Arabic_days_and_a_sheet_named_after_the_class_are_found()
    {
        var other = ("G1B", new[] { new string?[] { "", "الأحد", "الاثنين" }, ["1", "عربي", "علوم"] }, Array.Empty<string>());
        var mine = ("G 1-A", new[] { new string?[] { "الحصة", "الأحد", "الاثنين" }, ["1", "رياضيات", "انجليزي"] }, Array.Empty<string>());
        var t = Read(Workbook(other, mine), classKey: "G1A");
        Assert.Equal("G 1-A", t.Sheet);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Monday], t.Days);
        Assert.Equal("رياضيات", t.Cell(0, DayOfWeek.Sunday)!.Raw);
        Assert.Contains(t.Notes, n => n.Contains("G1B"));
    }

    [Fact]
    public void A_sheet_without_days_is_not_a_timetable()
    {
        using var ms = new MemoryStream(Workbook(("List", [["Name", "Class"], ["Ali", "G1A"]], [])));
        Assert.Null(TimetableExtractor.Extract(SpreadsheetReader.Read(ms, "t.xlsx", true), null, out var error));
        Assert.Contains("No timetable was found", error);
    }

    [Theory]
    [InlineData("Break", ImportCellKind.Break)]
    [InlineData("Lunch break", ImportCellKind.Break)]
    [InlineData("الاستراحة", ImportCellKind.Break)]
    [InlineData("Morning Assembly", ImportCellKind.Activity)]
    [InlineData("Clubs (Robotics)", ImportCellKind.Activity)]
    [InlineData("—", ImportCellKind.Free)]
    [InlineData("Free period", ImportCellKind.Free)]
    [InlineData("Arabic\nMs Hanapi", null)]          // a name that contains "nap" is still a lesson
    [InlineData("Math", null)]
    public void Special_cells_are_recognised_by_whole_words(string text, ImportCellKind? expected) =>
        Assert.Equal(expected, TimetableExtractor.SpecialKind(text));

    [Theory]
    [InlineData("08:00 - 08:45", "08:00", "08:45")]
    [InlineData("8.00–8.45", "08:00", "08:45")]
    [InlineData("1:30-2:15", "13:30", "14:15")]                // a school afternoon
    [InlineData("12:00 PM - 12:40 PM", "12:00", "12:40")]
    [InlineData("Period 4 (11:10–11:55)", "11:10", "11:55")]
    public void Times_are_read_from_labels(string text, string start, string end)
    {
        var (s, e) = TimetableExtractor.ParseTimes(text);
        Assert.Equal((TimeOnly.Parse(start), TimeOnly.Parse(end)), (s, e));
    }

    [Fact]
    public void Uploads_are_checked_by_their_content_not_only_their_name()
    {
        var pdf = "%PDF-1.7 ..."u8.ToArray();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var text = "08:00 - 08:45"u8.ToArray();
        Assert.Contains("PDF", FileCheck.Problem(pdf, ".xlsx"));                 // a PDF renamed to .xlsx is recognised
        Assert.Contains("picture", FileCheck.Problem(png, ".xlsx"));             // so is a picture
        Assert.Contains("picture", FileCheck.Problem(text, ".jpg"));
        Assert.Contains("not a valid Excel workbook", FileCheck.Problem(text, ".xlsx"));
        Assert.Contains(".xls", FileCheck.Problem(text, ".xls"));
        Assert.Contains("Excel workbook (.xlsx) or a CSV", FileCheck.Problem(text, ".docx"));
        Assert.Contains("not a text CSV", FileCheck.Problem(Workbook(("S", [["a"]], [])), ".csv"));
        Assert.Null(FileCheck.Problem("Day,Sunday\n1,Math"u8.ToArray(), ".csv"));
        Assert.Null(FileCheck.Problem(Workbook(("S", [["a"]], [])), ".xlsx"));
    }
}
