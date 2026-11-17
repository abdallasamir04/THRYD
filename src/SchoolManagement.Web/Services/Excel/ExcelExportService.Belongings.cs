using ClosedXML.Excel;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Excel;

/// <summary>Belongings History & Reports → Download Excel. Every record of the filtered scope, not only the page shown.</summary>
public sealed partial class ExcelExportService
{
    private const string BelongingsRules = "Readiness = items Available ÷ items checked (Available + Missing); N/A and items not checked never count as Missing. A check is complete when every item has a status.";

    public byte[] BelongingsPersonal(PersonalHistory h, string schoolName)
    {
        using var wb = new XLWorkbook();
        BelongingsSummary(wb.Worksheets.Add("Summary"), schoolName, "Personal Belongings Report", h.Scope,
        [
            ("Student-days recorded", h.Total), ("Complete checks", h.Complete), ("Incomplete checks", h.Incomplete),
            ("With missing items", h.WithMissing), ("Readiness", h.Readiness.Percent is { } p ? p / 100.0 : null),
        ]);
        GroupSheet(wb.Worksheets.Add("By class"), "Class", h.ByClass);

        var rep = wb.Worksheets.Add("Repeated missing");
        WriteHeader(rep, ["Student", "Student ID", "Class", "Days with missing items", "Items"]);
        var r = 2;
        foreach (var x in h.Repeated)
        {
            rep.Cell(r, 1).Value = x.Name; rep.Cell(r, 2).Value = x.StudentId; rep.Cell(r, 3).Value = x.ClassName;
            rep.Cell(r, 4).Value = x.Days; rep.Cell(r, 5).Value = x.Items; r++;
        }
        FinishTable(rep, h.Repeated.Count, 5);
        TrendSheet(wb.Worksheets.Add("Weekly trend"), h.Trend);

        var ws = wb.Worksheets.Add("Records");
        WriteHeader(ws, ["Date", "Class", "Student", "Student ID", .. PersonalItems.All.Select(i => i.Label), "Complete", "Missing items", "Notes", "Recorded by", "Last modified by", "Last modified", "Corrections"]);
        r = 2;
        foreach (var x in h.Rows)
        {
            var c = 1;
            DateCell(ws.Cell(r, c++), x.Date);
            ws.Cell(r, c++).Value = x.ClassName;
            ws.Cell(r, c++).Value = x.StudentName;
            ws.Cell(r, c++).Value = x.StudentId;
            foreach (var s in x.Items) BelongingsStatusCell(ws.Cell(r, c++), s);
            ws.Cell(r, c++).Value = x.Complete ? "Yes" : "No";
            ws.Cell(r, c++).Value = string.Join(", ", x.Missing);
            ws.Cell(r, c++).Value = x.Notes ?? "";
            ws.Cell(r, c++).Value = x.RecordedBy ?? "";
            ws.Cell(r, c++).Value = x.UpdatedBy ?? "";
            ws.Cell(r, c).Value = SchoolTime.Current.ToLocal(x.UpdatedAt);
            ws.Cell(r, c++).Style.DateFormat.Format = "d mmm yyyy, h:mm AM/PM";
            ws.Cell(r, c).Value = x.Corrections;
            r++;
        }
        FinishTable(ws, h.Rows.Count, 16);
        return Save(wb);
    }

    public byte[] BelongingsAcademic(AcademicHistory h, string schoolName)
    {
        using var wb = new XLWorkbook();
        BelongingsSummary(wb.Worksheets.Add("Summary"), schoolName, "Academic Belongings Report", h.Scope,
        [
            ("Lessons recorded", h.Lessons), ("Student lesson checks", h.Checks), ("Complete", h.Complete), ("Incomplete", h.Incomplete),
            ("Missing Book", h.MissingBook), ("Missing Copy Book", h.MissingCopyBook), ("Readiness", h.Readiness.Percent is { } p ? p / 100.0 : null),
        ]);
        GroupSheet(wb.Worksheets.Add("By class"), "Class", h.ByClass);
        GroupSheet(wb.Worksheets.Add("By subject"), "Subject", h.BySubject);
        GroupSheet(wb.Worksheets.Add("By teacher"), "Teacher", h.ByTeacher);
        GroupSheet(wb.Worksheets.Add("By quarter"), "Quarter", h.ByQuarter);
        GroupSheet(wb.Worksheets.Add("Monthly trend"), "Month", h.ByMonth);
        TrendSheet(wb.Worksheets.Add("Weekly trend"), h.Trend);

        var ws = wb.Worksheets.Add("Records");
        WriteHeader(ws, ["Date", "Class", "Period", "Start", "End", "Subject", "Teacher", "Student", "Student ID", "Book", "Copy Book", "Complete", "Notes", "Recorded by", "Last modified by", "Last modified", "Corrections"]);
        var r = 2;
        foreach (var x in h.Rows)
        {
            DateCell(ws.Cell(r, 1), x.Date);
            ws.Cell(r, 2).Value = x.ClassName;
            ws.Cell(r, 3).Value = x.Period;
            TimeCell(ws.Cell(r, 4), x.Start);
            TimeCell(ws.Cell(r, 5), x.End);
            ws.Cell(r, 6).Value = x.Subject;
            ws.Cell(r, 7).Value = x.TeacherName ?? "";
            ws.Cell(r, 8).Value = x.StudentName;
            ws.Cell(r, 9).Value = x.StudentId;
            BelongingsStatusCell(ws.Cell(r, 10), x.Book);
            BelongingsStatusCell(ws.Cell(r, 11), x.CopyBook);
            ws.Cell(r, 12).Value = x.Complete ? "Yes" : "No";
            ws.Cell(r, 13).Value = x.Notes ?? "";
            ws.Cell(r, 14).Value = x.RecordedBy ?? "";
            ws.Cell(r, 15).Value = x.UpdatedBy ?? "";
            ws.Cell(r, 16).Value = SchoolTime.Current.ToLocal(x.UpdatedAt);
            ws.Cell(r, 16).Style.DateFormat.Format = "d mmm yyyy, h:mm AM/PM";
            ws.Cell(r, 17).Value = x.Corrections;
            r++;
        }
        FinishTable(ws, h.Rows.Count, 17);
        return Save(wb);
    }

    private static void BelongingsSummary(IXLWorksheet ws, string schoolName, string title, BelongingsScope s, IReadOnlyList<(string Label, object? Value)> stats)
    {
        ws.Cell(1, 1).Value = schoolName;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#6D28D9");
        ws.Cell(2, 1).Value = title;
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.FontSize = 14;
        var r = 4;
        foreach (var (label, value) in new (string, object?)[]
                 {
                     ("Academic year", s.YearName ?? "—"), ("Quarter", s.QuarterName ?? "All quarters in the period"),
                     ("Period", s.PeriodLabel), ("Dates", s.DatesLabel), ("Scope", s.Audience),
                 }.Concat(stats))
        {
            ws.Cell(r, 1).Value = label;
            ws.Cell(r, 1).Style.Font.Bold = true;
            if (label == "Readiness") PercentCell(ws.Cell(r, 2), value is double d ? d * 100 : null);
            else ws.Cell(r, 2).Value = value switch { int i => i, null => "—", _ => value.ToString() };
            r++;
        }
        ws.Cell(r + 1, 1).Value = "Generated";
        ws.Cell(r + 1, 2).Value = SchoolTime.Current.Now;
        ws.Cell(r + 1, 2).Style.DateFormat.Format = "d mmm yyyy, h:mm AM/PM";
        ws.Cell(r + 2, 1).Value = "Rules";
        ws.Cell(r + 2, 2).Value = BelongingsRules;
        ws.Column(1).Width = 26;
        ws.Column(2).Width = 70;
    }

    private static void GroupSheet(IXLWorksheet ws, string column, IReadOnlyList<GroupStat> groups)
    {
        WriteHeader(ws, [column, "Records", "Complete", "Completion", "Readiness"]);
        var r = 2;
        foreach (var g in groups)
        {
            ws.Cell(r, 1).Value = g.Label;
            ws.Cell(r, 2).Value = g.Records;
            ws.Cell(r, 3).Value = g.Complete;
            PercentCell(ws.Cell(r, 4), g.Records == 0 ? null : 100.0 * g.Complete / g.Records);
            PercentCell(ws.Cell(r, 5), g.Readiness.Percent);
            r++;
        }
        FinishTable(ws, groups.Count, 5);
    }

    private static void TrendSheet(IXLWorksheet ws, IReadOnlyList<TrendPoint> points)
    {
        WriteHeader(ws, ["Week starting", "Records", "Complete", "Completion", "Readiness"]);
        var r = 2;
        foreach (var p in points)
        {
            DateCell(ws.Cell(r, 1), p.WeekStart);
            ws.Cell(r, 2).Value = p.Records;
            ws.Cell(r, 3).Value = p.Complete;
            PercentCell(ws.Cell(r, 4), p.Records == 0 ? null : 100.0 * p.Complete / p.Records);
            PercentCell(ws.Cell(r, 5), p.Readiness.Percent);
            r++;
        }
        FinishTable(ws, points.Count, 5);
    }

    private static void DateCell(IXLCell cell, DateOnly date)
    {
        cell.Value = date.ToDateTime(TimeOnly.MinValue);
        cell.Style.DateFormat.Format = "d mmm yyyy";
    }

    private static void TimeCell(IXLCell cell, TimeOnly? time)
    {
        if (time is { } t) { cell.Value = t.ToTimeSpan(); cell.Style.NumberFormat.Format = "h:mm AM/PM"; }
        else cell.Value = "";
    }

    private static void BelongingsStatusCell(IXLCell cell, ItemStatus? s)
    {
        cell.Value = BelongingsStatus.Label(s);
        var (fill, ink) = s switch
        {
            ItemStatus.Green => ("#E3F5EE", "#0B6B4F"),
            ItemStatus.Red => ("#FDECEC", "#B42318"),
            ItemStatus.NotApplicable => ("#EEF1F7", "#5F6A82"),
            _ => ("#FFF3DC", "#92400E"),
        };
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(fill);
        cell.Style.Font.FontColor = XLColor.FromHtml(ink);
    }
}
