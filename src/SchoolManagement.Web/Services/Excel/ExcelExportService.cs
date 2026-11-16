using SchoolManagement.Web.Common;
using ClosedXML.Excel;
using SchoolManagement.Web.Services.Import;

namespace SchoolManagement.Web.Services.Excel;

public interface IExcelExportService
{
    byte[] Students(IReadOnlyList<StudentRow> rows, string filterSummary);
    byte[] Teachers(IReadOnlyList<TeacherRow> rows, string filterSummary);
    byte[] StudentTemplate(IReadOnlyList<ClassOption> activeClasses);
    byte[] TeacherTemplate();
    byte[] ImportReport(ImportKind kind, string fileName, IReadOnlyList<PreviewRow> rows);
    byte[] WorkloadReport(DateOnly weekStart, IReadOnlyList<WorkloadSummary> teachers, IReadOnlyList<WorkloadAssignmentRow> assignments);

    /// <summary>One student's attendance over a period: summary sheet and one row per school day.</summary>
    byte[] AttendanceStudent(StudentAttendanceReport report, string schoolName);

    /// <summary>A class, grade or the whole school over a period: summary, by grade, by class and one row per student.</summary>
    byte[] AttendanceGroup(GroupAttendanceReport report, string schoolName);
}

/// <summary>Builds every downloadable workbook. Contact numbers are always written as text.</summary>
public sealed class ExcelExportService : IExcelExportService
{
    private const int TemplateRows = 2000;
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#4F46E5");
    private static readonly XLColor HeaderText = XLColor.White;
    private static readonly XLColor Border = XLColor.FromHtml("#D9DDED");

    public byte[] Students(IReadOnlyList<StudentRow> rows, string filterSummary)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Students");
        WriteHeader(ws, ["#", "Student Name", "Class", "Class Status", "Added On", "Last Updated"]);
        var r = 2;
        foreach (var s in rows)
        {
            ws.Cell(r, 1).Value = r - 1;
            ws.Cell(r, 2).Value = s.FullName;
            ws.Cell(r, 3).Value = s.ClassName;
            ws.Cell(r, 4).Value = s.ClassActive ? "Active" : "Inactive";
            ws.Cell(r, 5).Value = SchoolTime.Current.ToLocal(s.CreatedAt);
            ws.Cell(r, 6).Value = SchoolTime.Current.ToLocal(s.UpdatedAt);
            r++;
        }
        FinishTable(ws, rows.Count, 6);
        ws.Range(2, 5, Math.Max(2, r - 1), 6).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        AddInfoSheet(wb, "Students export", rows.Count, filterSummary);
        return Save(wb);
    }

    public byte[] Teachers(IReadOnlyList<TeacherRow> rows, string filterSummary)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Teachers");
        WriteHeader(ws, ["#", "Teacher Name", "Job Title", "Contact Number", "Added On", "Last Updated"]);
        ws.Column(4).Style.NumberFormat.Format = "@";
        var r = 2;
        foreach (var t in rows)
        {
            ws.Cell(r, 1).Value = r - 1;
            ws.Cell(r, 2).Value = t.FullName;
            ws.Cell(r, 3).Value = t.JobTitle ?? "";
            ws.Cell(r, 4).SetValue(t.ContactNumber ?? ""); // text, keeps leading zeros
            ws.Cell(r, 5).Value = SchoolTime.Current.ToLocal(t.CreatedAt);
            ws.Cell(r, 6).Value = SchoolTime.Current.ToLocal(t.UpdatedAt);
            r++;
        }
        FinishTable(ws, rows.Count, 6);
        ws.Range(2, 5, Math.Max(2, r - 1), 6).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        AddInfoSheet(wb, "Teachers export", rows.Count, filterSummary);
        return Save(wb);
    }

    public byte[] StudentTemplate(IReadOnlyList<ClassOption> activeClasses)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Students");
        WriteHeader(ws, ["Student Name", "Class"]);
        ws.Column(1).Width = 40;
        ws.Column(2).Width = 16;
        ws.SheetView.FreezeRows(1);

        var lists = wb.Worksheets.Add("Classes");
        WriteHeader(lists, ["Class"]);
        for (var i = 0; i < activeClasses.Count; i++) lists.Cell(i + 2, 1).Value = activeClasses[i].Name;
        lists.Column(1).Width = 18;

        if (activeClasses.Count > 0)
        {
            // Class cells offer the current active classes as a dropdown.
            var validation = ws.Range(2, 2, TemplateRows + 1, 2).CreateDataValidation();
            validation.List(lists.Range(2, 1, activeClasses.Count + 1, 1), true);
            validation.IgnoreBlanks = true;
            validation.ErrorStyle = XLErrorStyle.Warning;
            validation.ErrorTitle = "Unknown class";
            validation.ErrorMessage = "Choose a class from the list. Rows with an unknown class are reported during import.";
        }

        AddInstructions(wb,
        [
            "Fill one student per row on the \"Students\" sheet, starting at row 2. Keep the header row.",
            "Student Name (required): the full name, in English or Arabic.",
            "Class (required): one of the classes on the \"Classes\" sheet, e.g. G3A. Inactive classes are not accepted.",
            "A student who is already in the same class is skipped as a duplicate; nothing is overwritten.",
            "Upload the file on Import / Export. You will see a preview and confirm before anything is saved.",
        ]);
        wb.Worksheet(1).SetTabActive();
        return Save(wb);
    }

    public byte[] TeacherTemplate()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Teachers");
        WriteHeader(ws, ["Teacher Name", "Job Title", "Contact Number"]);
        ws.Column(1).Width = 36;
        ws.Column(2).Width = 28;
        ws.Column(3).Width = 20;
        // Text format so leading zeros (010…) and + prefixes are kept as typed.
        ws.Range(2, 3, TemplateRows + 1, 3).Style.NumberFormat.Format = "@";
        ws.SheetView.FreezeRows(1);

        AddInstructions(wb,
        [
            "Fill one teacher per row on the \"Teachers\" sheet, starting at row 2. Keep the header row.",
            "Teacher Name (required): the full name, in English or Arabic.",
            "Job Title (optional): e.g. Math Teacher, Homeroom Teacher, Head of Science.",
            "Contact Number (optional): the column is formatted as text so 010… and +20… are kept exactly.",
            "The same name with the same number (or without a number) is skipped as a duplicate.",
            "Upload the file on Import / Export. You will see a preview and confirm before anything is saved.",
        ]);
        wb.Worksheet(1).SetTabActive();
        return Save(wb);
    }

    public byte[] ImportReport(ImportKind kind, string fileName, IReadOnlyList<PreviewRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Rows not imported");
        var fields = ImportDefinitions.Fields(kind);
        var headers = new List<string> { "Row in file", "Status" };
        headers.AddRange(fields.Select(f => f.Label));
        headers.Add("Reason");
        WriteHeader(ws, headers);

        var r = 2;
        foreach (var row in rows)
        {
            ws.Cell(r, 1).Value = row.RowNumber;
            ws.Cell(r, 2).Value = row.Status == RowStatus.Duplicate ? "Duplicate (skipped)" : "Failed";
            var c = 3;
            foreach (var f in fields) ws.Cell(r, c++).SetValue(row.Value(f.Key));
            ws.Cell(r, c).Value = string.Join(" ", row.Errors);
            r++;
        }
        FinishTable(ws, rows.Count, headers.Count);
        ws.Column(headers.Count).Width = 70;
        AddInfoSheet(wb, $"{ImportDefinitions.Title(kind)} import report — {fileName}", rows.Count, "Rows from the file that were not imported, with the reason.");
        return Save(wb);
    }

    public byte[] WorkloadReport(DateOnly weekStart, IReadOnlyList<WorkloadSummary> teachers, IReadOnlyList<WorkloadAssignmentRow> assignments)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Weekly workload");
        WriteHeader(ws, ["Teacher", "Job Title", "Class", "Substitution", "Duty", "Office", "Total", "Max", "Remaining", "Status"]);
        var r = 2;
        foreach (var t in teachers)
        {
            ws.Cell(r, 1).Value = t.TeacherName;
            ws.Cell(r, 2).Value = t.JobTitle ?? "";
            ws.Cell(r, 3).Value = t.Breakdown.Class;
            ws.Cell(r, 4).Value = t.Breakdown.Substitution;
            ws.Cell(r, 5).Value = t.Breakdown.Duty;
            ws.Cell(r, 6).Value = t.Breakdown.Office;
            ws.Cell(r, 7).Value = t.Total;
            ws.Cell(r, 8).Value = WorkloadRules.Max;
            ws.Cell(r, 9).Value = t.Remaining;
            ws.Cell(r, 10).Value = WorkloadRules.Label(t.Status);
            var fill = t.Status switch
            {
                WorkloadStatus.Available => XLColor.FromHtml("#E3F5EE"),
                WorkloadStatus.NearCapacity => XLColor.FromHtml("#FFF0E3"),
                _ => XLColor.FromHtml("#FDECEC"),
            };
            ws.Cell(r, 10).Style.Fill.BackgroundColor = fill;
            r++;
        }
        FinishTable(ws, teachers.Count, 10);

        var detail = wb.Worksheets.Add("Assignments");
        WriteHeader(detail, ["Teacher", "Category", "Periods", "Class", "Duty", "Note", "Last Updated"]);
        r = 2;
        foreach (var a in assignments)
        {
            detail.Cell(r, 1).Value = a.TeacherName;
            detail.Cell(r, 2).Value = a.Category.ToString();
            detail.Cell(r, 3).Value = a.Periods;
            detail.Cell(r, 4).Value = a.ClassName ?? "";
            detail.Cell(r, 5).Value = a.DutyLabel ?? "";
            detail.Cell(r, 6).Value = a.Description ?? "";
            detail.Cell(r, 7).Value = SchoolTime.Current.ToLocal(a.UpdatedAt);
            r++;
        }
        FinishTable(detail, assignments.Count, 7);
        detail.Range(2, 7, Math.Max(2, r - 1), 7).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";

        AddInfoSheet(wb, $"Teacher workload — week of {Common.SchoolWeek.Label(weekStart)}", teachers.Count,
            $"Maximum {WorkloadRules.Max} periods per teacher per week (Class + Substitution + Duty + Office).");
        return Save(wb);
    }


    // ------------------------------------------------------------------ attendance history & reports

    private static readonly XLColor PresentFill = XLColor.FromHtml("#E3F5EE");
    private static readonly XLColor LateFill = XLColor.FromHtml("#FFF0E3");
    private static readonly XLColor AbsentFill = XLColor.FromHtml("#FDECEC");
    private static readonly XLColor NotRecordedFill = XLColor.FromHtml("#EEF1F7");

    private static string Status(ArrivalOutcome? o) => Arrivals.Label(o);

    private static void StatusCell(IXLCell cell, ArrivalOutcome? o)
    {
        cell.Value = Status(o);
        cell.Style.Fill.BackgroundColor = o switch
        {
            ArrivalOutcome.Present => PresentFill, ArrivalOutcome.Late => LateFill, ArrivalOutcome.Absent => AbsentFill, _ => NotRecordedFill,
        };
    }

    private static string Period(DateOnly from, DateOnly to) =>
        from == to ? from.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
                   : $"{from.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} – {to.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>Title block + label / value pairs on a summary sheet; returns the next free row.</summary>
    private static int SummarySheet(IXLWorksheet ws, string schoolName, string title, IReadOnlyList<(string Label, object? Value)> items)
    {
        ws.Cell(1, 1).Value = schoolName;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#6D28D9");
        ws.Cell(2, 1).Value = title;
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.FontSize = 14;
        var r = 4;
        foreach (var (label, value) in items)
        {
            ws.Cell(r, 1).Value = label;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = value switch { int i => i, double d => d, null => "—", _ => value.ToString() };
            r++;
        }
        ws.Cell(r + 1, 1).Value = "Generated";
        ws.Cell(r + 1, 2).Value = SchoolTime.Current.Now;
        ws.Cell(r + 1, 2).Style.DateFormat.Format = "d mmm yyyy, h:mm AM/PM";
        ws.Cell(r + 2, 1).Value = "Rules";
        ws.Cell(r + 2, 2).Value = "Not Recorded = no attendance record (never an absence). Attendance % = (Present + Late) ÷ days with a record. School days exclude weekends and school holidays.";
        ws.Column(1).Width = 26;
        ws.Column(2).Width = 60;
        return r + 4;
    }

    private static IReadOnlyList<(string, object?)> CountItems(AttendanceCounts k) =>
    [
        ("Present", k.Present), ("Late", k.Late), ("Absent", k.Absent), ("Not Recorded", k.NotRecorded),
        ("Days with a record", k.Recorded), ("Attendance %", k.Percent is { } p ? p / 100.0 : null),
    ];

    private static void PercentCell(IXLCell cell, double? percent)
    {
        if (percent is { } p) { cell.Value = p / 100.0; cell.Style.NumberFormat.Format = "0.0%"; }
        else cell.Value = "—";
    }

    public byte[] AttendanceStudent(StudentAttendanceReport report, string schoolName)
    {
        using var wb = new XLWorkbook();
        var info = wb.Worksheets.Add("Summary");
        var items = new List<(string, object?)>
        {
            ("Student", report.Name), ("Student ID", report.StudentId), ("Current class", report.ClassName), ("Period", Period(report.From, report.To)),
            ("School days", report.Counts.Days),
        };
        items.AddRange(CountItems(report.Counts));
        SummarySheet(info, schoolName, "Student Attendance Report", items);
        var pct = info.Search("Attendance %").FirstOrDefault();
        if (pct is not null && report.Counts.Percent is not null) info.Cell(pct.Address.RowNumber, 2).Style.NumberFormat.Format = "0.0%";

        var ws = wb.Worksheets.Add("Daily attendance");
        WriteHeader(ws, ["Date", "Day", "Status", "Arrival Time", "Class", "Notes", "Recorded By", "Corrections", "Last Correction Reason"]);
        var r = 2;
        foreach (var d in report.Days)
        {
            ws.Cell(r, 1).Value = d.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "d mmm yyyy";
            ws.Cell(r, 2).Value = d.Date.DayOfWeek.ToString();
            StatusCell(ws.Cell(r, 3), d.Outcome);
            if (d.Time is { } t) { ws.Cell(r, 4).Value = t.ToTimeSpan(); ws.Cell(r, 4).Style.NumberFormat.Format = "h:mm AM/PM"; }
            ws.Cell(r, 5).Value = d.ClassName ?? "";
            ws.Cell(r, 6).Value = d.Notes ?? "";
            ws.Cell(r, 7).Value = d.RecordedBy ?? "";
            ws.Cell(r, 8).Value = d.Corrections;
            ws.Cell(r, 9).Value = d.LastCorrection ?? "";
            r++;
        }
        FinishTable(ws, report.Days.Count, 9);

        if (report.Corrections.Count > 0)
        {
            var c = wb.Worksheets.Add("Corrections");
            WriteHeader(c, ["Date", "Before", "After", "Reason", "Corrected At", "Corrected By"]);
            r = 2;
            foreach (var x in report.Corrections)
            {
                c.Cell(r, 1).Value = x.Date.ToDateTime(TimeOnly.MinValue);
                c.Cell(r, 1).Style.DateFormat.Format = "d mmm yyyy";
                c.Cell(r, 2).Value = x.Before;
                c.Cell(r, 3).Value = x.After;
                c.Cell(r, 4).Value = x.Reason;
                c.Cell(r, 5).Value = SchoolTime.Current.ToLocal(x.CorrectedAt);
                c.Cell(r, 5).Style.DateFormat.Format = "d mmm yyyy, h:mm AM/PM";
                c.Cell(r, 6).Value = x.CorrectedBy ?? "";
                r++;
            }
            FinishTable(c, report.Corrections.Count, 6);
        }
        return Save(wb);
    }

    public byte[] AttendanceGroup(GroupAttendanceReport report, string schoolName)
    {
        using var wb = new XLWorkbook();
        var info = wb.Worksheets.Add("Summary");
        var items = new List<(string, object?)>
        {
            ("Report", report.Title), ("Period", Period(report.From, report.To)), ("School days", report.SchoolDays), ("Students", report.Students),
        };
        items.AddRange(CountItems(report.Counts));
        var title = report.Scope switch { "class" => "Class Attendance Report", "grade" => "Grade Attendance Report", _ => "Whole-School Attendance Report" };
        SummarySheet(info, schoolName, title, items);
        var pct = info.Search("Attendance %").FirstOrDefault();
        if (pct is not null && report.Counts.Percent is not null) info.Cell(pct.Address.RowNumber, 2).Style.NumberFormat.Format = "0.0%";

        void Groups(string name, IReadOnlyList<GroupRow> rows)
        {
            var g = wb.Worksheets.Add(name);
            WriteHeader(g, [name == "By grade" ? "Grade" : "Class", "Students", "Present", "Late", "Absent", "Not Recorded", "Attendance %"]);
            var i = 2;
            foreach (var x in rows)
            {
                g.Cell(i, 1).Value = x.Label;
                g.Cell(i, 2).Value = x.Students;
                g.Cell(i, 3).Value = x.Counts.Present;
                g.Cell(i, 4).Value = x.Counts.Late;
                g.Cell(i, 5).Value = x.Counts.Absent;
                g.Cell(i, 6).Value = x.Counts.NotRecorded;
                PercentCell(g.Cell(i, 7), x.Counts.Percent);
                i++;
            }
            // Totals row (the same totals as the summary).
            g.Cell(i, 1).Value = "Total";
            g.Cell(i, 2).Value = rows.Sum(x => x.Students);
            g.Cell(i, 3).Value = report.Counts.Present;
            g.Cell(i, 4).Value = report.Counts.Late;
            g.Cell(i, 5).Value = report.Counts.Absent;
            g.Cell(i, 6).Value = report.Counts.NotRecorded;
            PercentCell(g.Cell(i, 7), report.Counts.Percent);
            g.Range(i, 1, i, 7).Style.Font.Bold = true;
            FinishTable(g, rows.Count, 7);
        }
        if (report.Scope != "class")
        {
            Groups("By grade", report.ByGrade);
            Groups("By class", report.ByClass);
        }

        var ws = wb.Worksheets.Add("Students");
        if (report.SingleDay)
        {
            WriteHeader(ws, ["Student", "Student ID", "Class", "Status", "Arrival Time"]);
            var r = 2;
            foreach (var x in report.StudentRows)
            {
                ws.Cell(r, 1).Value = x.Name;
                ws.Cell(r, 2).Value = x.StudentId;
                ws.Cell(r, 3).Value = x.ClassName;
                StatusCell(ws.Cell(r, 4), x.DayOutcome);
                if (x.DayTime is { } t) { ws.Cell(r, 5).Value = t.ToTimeSpan(); ws.Cell(r, 5).Style.NumberFormat.Format = "h:mm AM/PM"; }
                r++;
            }
            FinishTable(ws, report.StudentRows.Count, 5);
        }
        else
        {
            WriteHeader(ws, ["Student", "Student ID", "Class", "School Days", "Present", "Late", "Absent", "Not Recorded", "Attendance %"]);
            var r = 2;
            foreach (var x in report.StudentRows)
            {
                ws.Cell(r, 1).Value = x.Name;
                ws.Cell(r, 2).Value = x.StudentId;
                ws.Cell(r, 3).Value = x.ClassName;
                ws.Cell(r, 4).Value = x.Counts.Days;
                ws.Cell(r, 5).Value = x.Counts.Present;
                ws.Cell(r, 6).Value = x.Counts.Late;
                ws.Cell(r, 7).Value = x.Counts.Absent;
                ws.Cell(r, 8).Value = x.Counts.NotRecorded;
                PercentCell(ws.Cell(r, 9), x.Counts.Percent);
                r++;
            }
            FinishTable(ws, report.StudentRows.Count, 9);
        }
        return Save(wb);
    }

    private static void WriteHeader(IXLWorksheet ws, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++) ws.Cell(1, i + 1).Value = headers[i];
        var range = ws.Range(1, 1, 1, headers.Count);
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = HeaderText;
        range.Style.Fill.BackgroundColor = HeaderFill;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 22;
    }

    private static void FinishTable(IXLWorksheet ws, int rowCount, int columns)
    {
        ws.SheetView.FreezeRows(1);
        var range = ws.Range(1, 1, Math.Max(1, rowCount + 1), columns);
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        range.Style.Border.BottomBorderColor = Border;
        if (rowCount > 0) range.SetAutoFilter();
        ws.Columns(1, columns).AdjustToContents(1, Math.Min(rowCount + 1, 500), 8, 60);
    }

    private static void AddInfoSheet(XLWorkbook wb, string title, int count, string filterSummary)
    {
        var info = wb.Worksheets.Add("About");
        info.Cell(1, 1).Value = title;
        info.Cell(1, 1).Style.Font.Bold = true;
        info.Cell(1, 1).Style.Font.FontSize = 14;
        info.Cell(3, 1).Value = "Rows";
        info.Cell(3, 2).Value = count;
        info.Cell(4, 1).Value = "Filters";
        info.Cell(4, 2).Value = filterSummary;
        info.Cell(5, 1).Value = "Exported";
        info.Cell(5, 2).Value = SchoolTime.Now;
        info.Cell(5, 2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        info.Range(3, 1, 5, 1).Style.Font.Bold = true;
        info.Column(1).Width = 14;
        info.Column(2).Width = 60;
    }

    private static void AddInstructions(XLWorkbook wb, IReadOnlyList<string> lines)
    {
        var ws = wb.Worksheets.Add("Instructions");
        ws.Cell(1, 1).Value = "How to fill this template";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        for (var i = 0; i < lines.Count; i++)
        {
            ws.Cell(i + 3, 1).Value = $"{i + 1}. {lines[i]}";
            ws.Cell(i + 3, 1).Style.Alignment.WrapText = true;
        }
        ws.Column(1).Width = 110;
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
