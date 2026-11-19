using System.Globalization;
using ClosedXML.Excel;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services.Import;

namespace SchoolManagement.Web.Services.Excel;

/// <summary>
/// Import / Export → Classes: the import template and the class export (Excel and CSV). The export starts with the
/// template's columns (Class Name, Grade Level, Section, Capacity, Active Status), so an exported file can be imported
/// again (every row is then reported as an existing class and nothing changes), followed by what only the system knows:
/// current students, available seats, over capacity and the stage. Every class's own stored capacity is used.
/// </summary>
public sealed partial class ExcelExportService
{
    public static readonly IReadOnlyList<string> ClassExportHeaders =
        ["#", "Class Name", "Grade Level", "Section", "Capacity", "Active Status", "Current Students", "Available Seats", "Over Capacity", "Stage"];

    /// <summary>One class's export values (after the "#" column), in <see cref="ClassExportHeaders"/> order.</summary>
    private static object?[] ClassValues(ClassRow c)
    {
        var id = ImportService.ClassIdentity(c.Name);
        var stage = ClassStages.Locate(c.Name)?.Stage.Title;
        return
        [
            c.Name,
            id is { } x ? ImportService.GradeLevelLabel(x.Stage, x.Grade) : null,
            id?.Section,
            c.Capacity,
            c.IsActive ? "Active" : "Inactive",
            c.StudentCount,
            Math.Max(0, c.Capacity - c.StudentCount),
            Math.Max(0, c.StudentCount - c.Capacity),
            stage,
        ];
    }

    public byte[] ClassTemplate(IReadOnlyList<ClassRow> existing)
    {
        using var wb = new XLWorkbook();
        var fields = ImportDefinitions.ClassFields;
        var ws = wb.Worksheets.Add("Classes");
        WriteHeader(ws, fields.Select(f => f.Label).ToList());
        double[] widths = [16, 16, 10, 12, 16];
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        // Text columns: "KG1A" or a section "A" must never be turned into anything else by Excel.
        ws.Range(2, 1, TemplateRows + 1, 3).Style.NumberFormat.Format = "@";
        ws.SheetView.FreezeRows(1);

        // Pick lists (a value typed by hand is still checked in the preview).
        var lists = wb.Worksheets.Add("Lists");
        WriteHeader(lists, ["Grade Level", "Active Status"]);
        string[] grades = ["Pre-K", "KG1", "KG2", .. Enumerable.Range(1, 12).Select(g => $"Grade {g}")];
        for (var i = 0; i < grades.Length; i++) lists.Cell(i + 2, 1).SetValue(grades[i]);
        lists.Cell(2, 2).SetValue("Active");
        lists.Cell(3, 2).SetValue("Inactive");
        lists.Column(1).Width = 16;
        lists.Column(2).Width = 16;
        foreach (var (col, source) in new[] { (2, lists.Range(2, 1, grades.Length + 1, 1)), (5, lists.Range(2, 2, 3, 2)) })
        {
            var validation = ws.Range(2, col, TemplateRows + 1, col).CreateDataValidation();
            validation.List(source, true);
            validation.IgnoreBlanks = true;
            validation.ShowErrorMessage = false; // other spellings (G3, 3, Yes) are accepted and checked in the preview
        }

        // The classes that already exist (they are skipped as duplicates if listed again).
        var current = wb.Worksheets.Add("Existing classes");
        WriteHeader(current, ["Class Name", "Grade Level", "Section", "Capacity", "Active Status"]);
        for (var i = 0; i < existing.Count; i++)
        {
            var v = ClassValues(existing[i]);
            for (var c = 0; c < 5; c++)
                if (v[c] is { } value) current.Cell(i + 2, c + 1).SetValue(value is int n ? n : value.ToString());
        }
        current.Columns(1, 5).AdjustToContents();

        var help = wb.Worksheets.Add("How to fill");
        help.Cell(1, 1).SetValue("Classes import");
        help.Cell(1, 1).Style.Font.Bold = true;
        string[] lines =
        [
            "Class Name: grade and section, e.g. PreK, PreKB, KG1A, KG2B, G3C (the same names as Add Class gives).",
            "Grade Level: Pre-K, KG1, KG2 or Grade 1, Grade 2 … (G3 or 3 also work). It must match the class name.",
            "Section: optional; 1–3 letters. When given it must match the class name.",
            $"Capacity: optional; 1–1,000 students. Empty: {Entities.SchoolClass.DefaultCapacity}.",
            "Active Status: optional; Active or Inactive. Empty: Active.",
            "A class that already exists is skipped and never changed (capacity, status and students stay as they are).",
            "High School adds one new grade at a time: Grade 11 classes before Grade 12.",
        ];
        for (var i = 0; i < lines.Length; i++) help.Cell(i + 3, 1).SetValue(lines[i]);
        help.Column(1).Width = 110;
        return Save(wb);
    }

    public byte[] Classes(IReadOnlyList<ClassRow> classes)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Classes");
        WriteHeader(ws, ClassExportHeaders);
        var r = 2;
        foreach (var c in classes)
        {
            ws.Cell(r, 1).Value = r - 1; // serial number, never the class id
            var values = ClassValues(c);
            for (var i = 0; i < values.Length; i++)
            {
                var cell = ws.Cell(r, i + 2);
                switch (values[i])
                {
                    case null: break;
                    case int n: cell.Value = n; break;
                    case string s: cell.SetValue(s); break;
                }
            }
            r++;
        }
        FinishTable(ws, classes.Count, ClassExportHeaders.Count);
        AddInfoSheet(wb, "Classes export", classes.Count,
            $"Every class (active and inactive) with its own capacity, from the Classes page. Students: {classes.Sum(c => c.StudentCount)}; capacity: {classes.Sum(c => c.Capacity)}.");
        return Save(wb);
    }

    public byte[] ClassesCsv(IReadOnlyList<ClassRow> classes)
    {
        var inv = CultureInfo.InvariantCulture;
        return CsvWriter.Write(ClassExportHeaders,
            classes.Select((c, i) => (IReadOnlyList<string?>)[(i + 1).ToString(inv), .. ClassValues(c).Select(v => v switch
            {
                null => null,
                int n => n.ToString(inv),
                _ => v.ToString(),
            })]));
    }
}
