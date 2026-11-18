using ClosedXML.Excel;
using SchoolManagement.Web.Common;

namespace SchoolManagement.Web.Services.Excel;

/// <summary>
/// Staff → All School Staff → Download Excel: a Staff Directory sheet (each person once) and one sheet per staff group, built
/// from the same <see cref="StaffDirectoryExport"/> as the PDF. Names are written exactly as stored; contact numbers as text;
/// no database ids. Read-only.
/// </summary>
public sealed partial class ExcelExportService
{
    private const int StaffHeaderRow = 5;

    public byte[] StaffDirectory(StaffDirectoryExport x, string schoolName)
    {
        using var wb = new XLWorkbook();
        string Text(string? value) => value ?? "";
        string Roles(StaffExportRow p) => string.Join("; ", p.Roles);

        var main = StaffSheet(wb, "Staff Directory", "All School Staff Directory", "StaffDirectory", x, schoolName, x.Ordered,
        [
            ("#", _ => ""), ("Full Name", p => p.FullName), ("Category", p => p.CategoryName), ("Job Title / Position", p => Text(p.Title)),
            ("Department", p => Text(p.Department)), ("Contact Number", p => Text(p.ContactNumber)), ("Additional Roles", Roles),
            ("Employment Type", p => Text(p.Employment)),
        ]);

        StaffSheet(wb, "Management Team", "Management Team", "ManagementTeam", x, schoolName, x.In(StaffCategory.Management),
        [
            ("#", _ => ""), ("Full Name", p => p.FullName), ("Position", p => Text(p.Title)), ("Department", p => Text(p.Department)),
            ("Contact Number", p => Text(p.ContactNumber)), ("Email", p => Text(p.Email)), ("Additional Roles", Roles),
            ("Employment Type", p => Text(p.Employment)),
        ]);
        foreach (var (category, sheet, table) in new[]
                 {
                     (StaffCategory.Academic, "Academic Staff", "AcademicStaff"),
                     (StaffCategory.Support, "Administrative & Support", "AdministrativeSupport"),
                 })
            StaffSheet(wb, sheet, StaffDirectoryExport.GroupName(category), table, x, schoolName, x.In(category),
            [
                ("#", _ => ""), ("Full Name", p => p.FullName), ("Job Title", p => Text(p.Title)), ("Department", p => Text(p.Department)),
                ("Contact Number", p => Text(p.ContactNumber)), ("Additional Roles", Roles), ("Employment Type", p => Text(p.Employment)),
            ]);

        main.SetTabActive();
        return Save(wb);
    }

    /// <summary>
    /// Title block (rows 1–3), then the staff table at row <see cref="StaffHeaderRow"/>: an Excel table with filters, or —
    /// for a group without anyone — just the formatted, filterable header row.
    /// </summary>
    private static IXLWorksheet StaffSheet(XLWorkbook wb, string name, string title, string tableName, StaffDirectoryExport x, string schoolName,
        IReadOnlyList<StaffExportRow> rows, IReadOnlyList<(string Header, Func<StaffExportRow, string> Value)> columns)
    {
        var ws = wb.Worksheets.Add(name);
        var last = columns.Count;
        TitleBlock(ws, title, schoolName, x.AcademicYear, last);
        ws.Cell(3, 1).Value = $"{rows.Count} staff member{(rows.Count == 1 ? "" : "s")}"
            + (x.Filtered ? " · Filters: " + string.Join(" · ", x.Filters.Select(f => $"{f.Label}: {f.Value}")) : " · All staff");
        ws.Cell(3, 1).Style.Font.Italic = true;
        ws.Cell(3, 1).Style.Font.FontColor = Muted;

        var headers = columns.Select(c => c.Header).ToList();
        if (rows.Count > 0) DataTable(ws, StaffHeaderRow, headers, rows.Count, tableName);
        else
        {
            for (var i = 0; i < last; i++) ws.Cell(StaffHeaderRow, i + 1).Value = headers[i];
            var head = ws.Range(StaffHeaderRow, 1, StaffHeaderRow, last);
            head.Style.Font.Bold = true;
            head.Style.Font.FontColor = HeaderText;
            head.Style.Fill.BackgroundColor = HeaderFill;
            head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            head.SetAutoFilter();
            ws.Row(StaffHeaderRow).Height = 22;
        }

        var contact = headers.IndexOf("Contact Number") + 1;
        for (var r = 0; r < rows.Count; r++)
        {
            var line = StaffHeaderRow + 1 + r;
            ws.Cell(line, 1).Value = r + 1; // serial number, never a database id
            ws.Cell(line, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            for (var c = 1; c < last; c++)
            {
                // SetValue with a string keeps every value as text: names exactly as stored, 010… numbers with their zeros.
                ws.Cell(line, c + 1).SetValue(columns[c].Value(rows[r]));
            }
            ws.Cell(line, 2).Style.Font.Bold = true;
        }
        if (contact > 0) ws.Range(StaffHeaderRow + 1, contact, StaffHeaderRow + Math.Max(1, rows.Count), contact).Style.NumberFormat.Format = "@";

        ws.SheetView.FreezeRows(StaffHeaderRow);
        ws.Columns(1, last).AdjustToContents(StaffHeaderRow, StaffHeaderRow + Math.Max(1, rows.Count), 6, 60);
        ws.Column(1).Width = 6;
        ws.Column(2).Width = Math.Max(ws.Column(2).Width, 28);
        var roles = headers.IndexOf("Additional Roles") + 1;
        if (roles > 0)
        {
            ws.Column(roles).Width = Math.Max(ws.Column(roles).Width, 24);
            ws.Column(roles).Style.Alignment.WrapText = true;
        }
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(StaffHeaderRow, StaffHeaderRow);
        return ws;
    }
}
