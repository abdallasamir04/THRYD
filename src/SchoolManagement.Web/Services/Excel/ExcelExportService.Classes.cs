using ClosedXML.Excel;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services.Pdf;

namespace SchoolManagement.Web.Services.Excel;

/// <summary>
/// Classes page → Download Summary Excel / Download Students Excel. Built from the same <see cref="ClassRoster"/> and the same
/// rules (<see cref="ClassRosterReports"/>: order, stage, grade, available places, capacity status) as the two PDFs, so both
/// formats always agree. Read-only; student ids are never written.
/// </summary>
public sealed partial class ExcelExportService
{
    private const string NotAssignedText = "Not Assigned";
    private static readonly XLColor Brand = XLColor.FromHtml("#6D28D9");
    private static readonly XLColor Ink = XLColor.FromHtml("#131829");
    private static readonly XLColor Muted = XLColor.FromHtml("#5F6A82");
    private static readonly XLColor LabelFill = XLColor.FromHtml("#F1F2F8");
    private static readonly XLColor Zebra = XLColor.FromHtml("#F7F8FC");
    private static readonly XLColor StatFill = XLColor.FromHtml("#F3EEFF");
    private const string DateTimeFormat = "d mmm yyyy, h:mm AM/PM";

    private static string Assigned(string? name) => string.IsNullOrWhiteSpace(name) || name == ClassRosterReports.NotAssigned ? NotAssignedText : name;

    /// <summary>"Lower Elementary (Grades 1–3)" for a multi-grade group; "KG1", "Pre-K"… for a single grade.</summary>
    private static string GroupLabel(RosterGroup g) => g.Grades.StartsWith("Grade", StringComparison.Ordinal) ? $"{g.Title} ({g.Grades})" : g.Title;

    /// <summary>Title (school · report) and the academic year / export line, as on the PDFs.</summary>
    private static void TitleBlock(IXLWorksheet ws, string title, string schoolName, string? academicYear, int lastColumn)
    {
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 15;
        ws.Cell(1, 1).Style.Font.FontColor = Ink;
        ws.Row(1).Height = 24;
        ws.Cell(2, 1).Value = $"THRYD · {schoolName}" + (string.IsNullOrWhiteSpace(academicYear) ? "" : $" · Academic Year {academicYear}")
            + $" · Exported {SchoolTime.Current.Now:d MMM yyyy, h:mm tt}";
        ws.Cell(2, 1).Style.Font.FontColor = Brand;
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Range(3, 1, 3, Math.Max(1, lastColumn)).Style.Border.TopBorder = XLBorderStyleValues.Medium;
        ws.Range(3, 1, 3, Math.Max(1, lastColumn)).Style.Border.TopBorderColor = Brand;
    }

    /// <summary>An Excel table (filters, banded rows) with the THRYD header colours; returns the table.</summary>
    private static IXLTable DataTable(IXLWorksheet ws, int headerRow, IReadOnlyList<string> headers, int rows, string name)
    {
        for (var i = 0; i < headers.Count; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
        var table = ws.Range(headerRow, 1, headerRow + rows, headers.Count).CreateTable(name);
        table.Theme = XLTableTheme.None;
        table.ShowAutoFilter = true;
        var head = ws.Range(headerRow, 1, headerRow, headers.Count);
        head.Style.Font.Bold = true;
        head.Style.Font.FontColor = HeaderText;
        head.Style.Fill.BackgroundColor = HeaderFill;
        head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(headerRow).Height = 22;
        for (var r = 1; r <= rows; r++)
        {
            var line = ws.Range(headerRow + r, 1, headerRow + r, headers.Count);
            if (r % 2 == 0) line.Style.Fill.BackgroundColor = Zebra;
            line.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            line.Style.Border.BottomBorderColor = Border;
            line.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(headerRow + r).Height = 18;
        }
        return table;
    }

    private static void Portrait(IXLWorksheet ws, int titleRows)
    {
        ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(titleRows, titleRows);
    }

    // ------------------------------------------------------------------ Download Summary Excel

    public byte[] ClassesSummary(ClassRoster r, string schoolName, string? academicYear)
    {
        var placed = ClassRosterReports.Placed(r).ToList();
        var all = placed.Select(x => x.Class).ToList();
        using var wb = new XLWorkbook();

        // Overview: key figures in large type.
        var ov = wb.Worksheets.Add("Overview");
        TitleBlock(ov, "Classes Summary", schoolName, academicYear, 3);
        var info = new (string Label, object Value)[]
        {
            ("School", schoolName), ("Academic Year", academicYear ?? "—"), ("Exported", SchoolTime.Current.Now),
        };
        var row = 4;
        foreach (var (label, value) in info)
        {
            ov.Cell(row, 1).Value = label;
            ov.Cell(row, 1).Style.Font.Bold = true;
            ov.Cell(row, 1).Style.Font.FontColor = Muted;
            if (value is DateTime dt) { ov.Cell(row, 2).Value = dt; ov.Cell(row, 2).Style.DateFormat.Format = DateTimeFormat; }
            else ov.Cell(row, 2).Value = value.ToString();
            ov.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            row++;
        }
        row++;
        ov.Cell(row, 1).Value = "Key statistics";
        ov.Cell(row, 1).Style.Font.Bold = true;
        ov.Cell(row, 1).Style.Font.FontSize = 12;
        row++;
        var stats = new (string Label, int Value)[]
        {
            ("Total classes", all.Count), ("Active classes", all.Count(c => c.IsActive)), ("Inactive classes", all.Count(c => !c.IsActive)),
            ("Total enrolled students", all.Sum(c => c.Students.Count)), ("Total capacity", all.Sum(c => c.Capacity)),
            ("Available places", ClassRosterReports.Free(all)),
        };
        var first = row;
        foreach (var (label, value) in stats)
        {
            ov.Cell(row, 1).Value = label;
            ov.Cell(row, 1).Style.Font.Bold = true;
            ov.Cell(row, 2).Value = value;
            ov.Cell(row, 2).Style.NumberFormat.Format = "#,##0";
            ov.Cell(row, 2).Style.Font.Bold = true;
            ov.Cell(row, 2).Style.Font.FontSize = 14;
            ov.Cell(row, 2).Style.Font.FontColor = Brand;
            ov.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            ov.Row(row).Height = 22;
            row++;
        }
        var block = ov.Range(first, 1, row - 1, 2);
        block.Style.Fill.BackgroundColor = StatFill;
        block.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        block.Style.Border.OutsideBorderColor = Border;
        block.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        block.Style.Border.InsideBorderColor = Border;
        ov.Cell(row + 1, 1).Value = "Homeroom Teachers and Co-Teachers are the current academic year's teaching teams (KG Stage classes). "
            + "“Not Assigned” means no one is recorded. Coordinators are the Management Team's stage coordinators.";
        ov.Cell(row + 1, 1).Style.Font.Italic = true;
        ov.Cell(row + 1, 1).Style.Font.FontColor = Muted;
        // A note (not a data table): wrapped over the two columns so the sheet prints at full size.
        var note = ov.Range(row + 1, 1, row + 1, 2).Merge();
        note.Style.Alignment.WrapText = true;
        note.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ov.Row(row + 1).Height = 48;
        ov.Column(1).Width = 28;
        ov.Column(2).Width = 34;
        ov.Column(3).Width = 4;
        Portrait(ov, 1);

        // Stage Summary: one row per stage group (grade), with totals.
        var st = wb.Worksheets.Add("Stage Summary");
        TitleBlock(st, "Stage Summary", schoolName, academicYear, 7);
        var groups = r.Stages.SelectMany(s => s.Groups.Select(g => (Stage: s.Title, Grade: GroupLabel(g), Classes: g.Classes.ToList(), g.Coordinator)))
            .Concat(r.Other.Count == 0 ? [] : [("Other classes", "No stage", r.Other.ToList(), (string?)null)])
            .ToList();
        var stTable = DataTable(st, 4, ["Stage Name", "Grade", "Number of Classes", "Enrolled Students", "Total Capacity", "Available Places", "Coordinator"],
            groups.Count, "StageSummary");
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var line = 5 + i;
            st.Cell(line, 1).Value = g.Stage;
            st.Cell(line, 2).Value = g.Grade;
            st.Cell(line, 3).Value = g.Classes.Count;
            st.Cell(line, 4).Value = g.Classes.Sum(c => c.Students.Count);
            st.Cell(line, 5).Value = g.Classes.Sum(c => c.Capacity);
            st.Cell(line, 6).Value = ClassRosterReports.Free(g.Classes);
            st.Cell(line, 7).Value = Assigned(g.Coordinator);
        }
        stTable.ShowTotalsRow = true;
        stTable.Field(0).TotalsRowLabel = "Total";
        foreach (var f in new[] { 2, 3, 4, 5 }) stTable.Field(f).TotalsRowFunction = XLTotalsRowFunction.Sum;
        st.Row(5 + groups.Count).Style.Font.Bold = true;
        st.Range(5, 3, 5 + groups.Count, 6).Style.NumberFormat.Format = "#,##0";
        st.SheetView.FreezeRows(4);
        st.Columns(1, 7).AdjustToContents(4, 5 + groups.Count, 12, 60);
        Portrait(st, 4);

        // Class Summary: one row per class, in the Classes pages' order.
        var cs = wb.Worksheets.Add("Class Summary");
        TitleBlock(cs, "Class Summary", schoolName, academicYear, 11);
        DataTable(cs, 4, ["Class Name", "Academic Stage", "Grade", "Enrolled Students", "Class Capacity", "Available Places", "Capacity Status", "Status",
            "Homeroom Teacher", "Co-Teacher", "Stage Coordinator"], placed.Count, "ClassSummary");
        for (var i = 0; i < placed.Count; i++)
        {
            var (c, stage, coordinator) = placed[i];
            var line = 5 + i;
            cs.Cell(line, 1).Value = c.Name;
            cs.Cell(line, 1).Style.Font.Bold = true;
            cs.Cell(line, 2).Value = stage;
            cs.Cell(line, 3).Value = ClassRosterReports.Grade(c);
            cs.Cell(line, 4).Value = c.Students.Count;
            cs.Cell(line, 5).Value = c.Capacity;
            cs.Cell(line, 6).Value = ClassRosterReports.Free(c);
            cs.Cell(line, 7).Value = ClassRosterReports.CapacityNote(c) ?? "Available";
            cs.Cell(line, 8).Value = c.IsActive ? "Active" : "Inactive";
            cs.Cell(line, 9).Value = Assigned(c.Homeroom);
            cs.Cell(line, 10).Value = Assigned(c.CoTeacher);
            cs.Cell(line, 11).Value = Assigned(coordinator);
        }
        cs.SheetView.FreezeRows(4);
        cs.SheetView.FreezeColumns(1);
        cs.Columns(1, 11).AdjustToContents(4, 4 + placed.Count, 12, 60);
        cs.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        cs.PageSetup.PaperSize = XLPaperSize.A4Paper;
        cs.PageSetup.FitToPages(1, 0);
        cs.PageSetup.SetRowsToRepeatAtTop(4, 4);

        ov.SetTabActive();
        return Save(wb);
    }

    // ------------------------------------------------------------------ Download Students Excel

    /// <summary>A valid, unique worksheet name (≤ 31 characters, no : \ / ? * [ ], not "History").</summary>
    private static string SheetName(string name, ISet<string> used)
    {
        var clean = new string(name.Select(ch => ":\\/?*[]".Contains(ch) ? '-' : ch).ToArray()).Trim('\'', ' ');
        if (clean.Length == 0 || clean.Equals("History", StringComparison.OrdinalIgnoreCase)) clean = $"Class {clean}".Trim();
        if (clean.Length > 31) clean = clean[..31];
        var candidate = clean;
        for (var n = 2; used.Contains(candidate); n++)
        {
            var suffix = $" ({n})";
            candidate = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }
        used.Add(candidate);
        return candidate;
    }

    /// <summary>An Excel table name: letters, digits and underscores, unique in the workbook.</summary>
    private static string TableName(string prefix, string name, ISet<string> used)
    {
        var clean = prefix + "_" + new string(name.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());
        var candidate = clean;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{clean}_{n}";
        return candidate;
    }

    public byte[] ClassesStudents(ClassRoster r, string schoolName, string? academicYear)
    {
        var placed = ClassRosterReports.Placed(r).ToList();
        using var wb = new XLWorkbook();
        var sheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Class Index" };
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ClassIndex" };
        var sheets = placed.Select(x => SheetName(x.Class.Name, sheetNames)).ToList();

        // Class Index: every class with its worksheet (a link to it).
        var index = wb.Worksheets.Add("Class Index");
        TitleBlock(index, "Classes & Students Report — Class Index", schoolName, academicYear, 8);
        DataTable(index, 4, ["#", "Class Name", "Academic Stage", "Grade", "Enrolled Students", "Class Capacity", "Available Places", "Worksheet"],
            placed.Count, "ClassIndex");
        for (var i = 0; i < placed.Count; i++)
        {
            var (c, stage, _) = placed[i];
            var line = 5 + i;
            index.Cell(line, 1).Value = i + 1;
            index.Cell(line, 2).Value = c.Name;
            index.Cell(line, 2).Style.Font.Bold = true;
            index.Cell(line, 3).Value = stage;
            index.Cell(line, 4).Value = ClassRosterReports.Grade(c);
            index.Cell(line, 5).Value = c.Students.Count;
            index.Cell(line, 6).Value = c.Capacity;
            index.Cell(line, 7).Value = ClassRosterReports.Free(c);
            index.Cell(line, 8).Value = sheets[i];
            index.Cell(line, 8).SetHyperlink(new XLHyperlink($"'{sheets[i].Replace("'", "''")}'!A1"));
            index.Cell(line, 8).Style.Font.FontColor = Brand;
            index.Cell(line, 8).Style.Font.Underline = XLFontUnderlineValues.Single;
        }
        var totals = 5 + placed.Count;
        index.Cell(totals, 2).Value = "Total";
        index.Cell(totals, 5).Value = placed.Sum(x => x.Class.Students.Count);
        index.Cell(totals, 6).Value = placed.Sum(x => x.Class.Capacity);
        index.Cell(totals, 7).Value = ClassRosterReports.Free(placed.Select(x => x.Class));
        index.Range(totals, 1, totals, 8).Style.Font.Bold = true;
        index.Range(totals, 1, totals, 8).Style.Fill.BackgroundColor = StatFill;
        index.Range(totals, 1, totals, 8).Style.Border.TopBorder = XLBorderStyleValues.Medium;
        index.Range(totals, 1, totals, 8).Style.Border.TopBorderColor = Brand;
        index.SheetView.FreezeRows(4);
        index.Columns(1, 8).AdjustToContents(4, totals, 6, 40);
        Portrait(index, 4);

        // One worksheet per class: the student table (# and Student Name only) and, beside it, the class information.
        for (var i = 0; i < placed.Count; i++)
        {
            var (c, stage, coordinator) = placed[i];
            var ws = wb.Worksheets.Add(sheets[i]);
            TitleBlock(ws, $"Class {c.Name} — Classes & Students Report", schoolName, academicYear, 5);

            if (c.Students.Count > 0)
            {
                DataTable(ws, 4, ["#", "Student Name"], c.Students.Count, TableName("Students", c.Name, tableNames));
                for (var s = 0; s < c.Students.Count; s++)
                {
                    ws.Cell(5 + s, 1).Value = s + 1; // serial number 1, 2, 3… — never the database id
                    ws.Cell(5 + s, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(5 + s, 2).Value = c.Students[s].FullName;
                }
            }
            else
            {
                ws.Cell(4, 1).Value = "#";
                ws.Cell(4, 2).Value = "Student Name";
                var head = ws.Range(4, 1, 4, 2);
                head.Style.Font.Bold = true;
                head.Style.Font.FontColor = HeaderText;
                head.Style.Fill.BackgroundColor = HeaderFill;
                ws.Cell(5, 2).Value = "No students are enrolled in this class.";
                ws.Cell(5, 2).Style.Font.Italic = true;
                ws.Cell(5, 2).Style.Font.FontColor = Muted;
            }
            ws.Cell(4, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            var details = new (string Label, object Value)[]
            {
                ("School", schoolName), ("Academic Year", academicYear ?? "—"), ("Class", c.Name), ("Academic Stage", stage),
                ("Grade", ClassRosterReports.Grade(c)), ("Status", c.IsActive ? "Active" : "Inactive"),
                ("Homeroom Teacher", Assigned(c.Homeroom)), ("Co-Teacher", Assigned(c.CoTeacher)), ("Stage Coordinator", Assigned(coordinator)),
                ("Total Enrolled Students", c.Students.Count), ("Class Capacity", c.Capacity), ("Available Places", ClassRosterReports.Free(c)),
                ("Exported", SchoolTime.Current.Now),
            };
            ws.Cell(4, 4).Value = "Class Information";
            ws.Range(4, 4, 4, 5).Style.Font.Bold = true;
            ws.Range(4, 4, 4, 5).Style.Font.FontColor = HeaderText;
            ws.Range(4, 4, 4, 5).Style.Fill.BackgroundColor = HeaderFill;
            for (var d = 0; d < details.Length; d++)
            {
                var (label, value) = details[d];
                ws.Cell(5 + d, 4).Value = label;
                ws.Cell(5 + d, 4).Style.Font.Bold = true;
                ws.Cell(5 + d, 4).Style.Font.FontColor = Muted;
                ws.Cell(5 + d, 4).Style.Fill.BackgroundColor = LabelFill;
                var cell = ws.Cell(5 + d, 5);
                switch (value)
                {
                    case int n: cell.Value = n; cell.Style.Font.Bold = true; break;
                    case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = DateTimeFormat; break;
                    default: cell.Value = value.ToString(); break;
                }
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            }
            var box = ws.Range(4, 4, 4 + details.Length, 5);
            box.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            box.Style.Border.OutsideBorderColor = Border;
            box.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            box.Style.Border.InsideBorderColor = Border;

            ws.Column(1).Width = 6;
            var longest = c.Students.Count == 0 ? 0 : c.Students.Max(s => s.FullName.Length);
            ws.Column(2).Width = Math.Clamp(longest * 1.15 + 4, 34, 70); // long names stay fully visible
            ws.Column(3).Width = 3;
            ws.Column(4).Width = 24;
            ws.Column(5).Width = Math.Clamp(details.Max(x => x.Value.ToString()?.Length ?? 0) * 1.1 + 4, 28, 50);
            ws.SheetView.FreezeRows(4);
            Portrait(ws, 4);
        }

        index.SetTabActive();
        return Save(wb);
    }
}
