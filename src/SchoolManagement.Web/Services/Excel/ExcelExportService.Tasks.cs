using System.Globalization;
using ClosedXML.Excel;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.Import;

namespace SchoolManagement.Web.Services.Excel;

/// <summary>
/// Import / Export → Tasks: the import template and the task export (Excel and CSV). The export's columns use the same
/// names as the template, so an exported file can be checked and imported again (its rows are then reported as
/// duplicates rather than added twice). No database ids are written.
/// </summary>
public sealed partial class ExcelExportService
{
    private static readonly string[] TaskPriorityNames = TaskLabels.Priorities.Reverse().Select(TaskLabels.Priority).ToArray(); // Low → Critical
    private static readonly string[] TaskStatusNames =
        [TaskLabels.Status(ManagementTaskStatus.ToDo), TaskLabels.Status(ManagementTaskStatus.InProgress), TaskLabels.Status(ManagementTaskStatus.Completed), TaskLabels.Status(ManagementTaskStatus.Cancelled)];

    /// <summary>The export's columns: the template's (in the same order), then what only the system knows.</summary>
    public static readonly IReadOnlyList<string> TaskExportHeaders =
    [
        "#", "Task Title", "Description", "Assigned To", "Department", "Priority", "Status", "Due Date", "Due Time", "Reviewer", "Notes",
        "Created Date", "Created By", "Completed Date",
    ];

    public byte[] TaskTemplate(IReadOnlyList<TaskStaffOption> staff, IReadOnlyList<TaskDepartmentOption> departments)
    {
        using var wb = new XLWorkbook();
        var fields = ImportDefinitions.TaskFields;
        var ws = wb.Worksheets.Add("Tasks");
        WriteHeader(ws, fields.Select(f => f.Label).ToList());
        int Col(string key) => fields.ToList().FindIndex(f => f.Key == key) + 1;
        double[] widths = [40, 46, 28, 22, 12, 14, 14, 11, 26, 36];
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.Column(Col(ImportDefinitions.Description)).Style.Alignment.WrapText = true;
        ws.Range(2, Col(ImportDefinitions.DueDate), TemplateRows + 1, Col(ImportDefinitions.DueDate)).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(2, Col(ImportDefinitions.DueTime), TemplateRows + 1, Col(ImportDefinitions.DueTime)).Style.DateFormat.Format = "hh:mm";
        ws.SheetView.FreezeRows(1);

        // Pick lists (warnings only: a value typed by hand is still checked in the preview).
        var people = staff.Where(s => s.IsActive).OrderBy(s => s.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var staffSheet = wb.Worksheets.Add("Staff");
        WriteHeader(staffSheet, ["Full Name", "Department / Position"]);
        for (var i = 0; i < people.Count; i++)
        {
            staffSheet.Cell(i + 2, 1).SetValue(people[i].FullName);
            staffSheet.Cell(i + 2, 2).SetValue(people[i].Note);
        }
        staffSheet.Column(1).Width = 34;
        staffSheet.Column(2).Width = 44;
        staffSheet.SheetView.FreezeRows(1);

        var deptSheet = wb.Worksheets.Add("Departments");
        WriteHeader(deptSheet, ["Department"]);
        for (var i = 0; i < departments.Count; i++) deptSheet.Cell(i + 2, 1).SetValue(departments[i].Name);
        deptSheet.Column(1).Width = 28;

        var listsSheet = wb.Worksheets.Add("Lists");
        WriteHeader(listsSheet, ["Priority", "Status"]);
        for (var i = 0; i < TaskPriorityNames.Length; i++) listsSheet.Cell(i + 2, 1).Value = TaskPriorityNames[i];
        for (var i = 0; i < TaskStatusNames.Length; i++) listsSheet.Cell(i + 2, 2).Value = TaskStatusNames[i];
        listsSheet.Columns(1, 2).Width = 16;

        void PickList(int column, IXLRange source, string title, string message)
        {
            var v = ws.Range(2, column, TemplateRows + 1, column).CreateDataValidation();
            v.List(source, true);
            v.IgnoreBlanks = true;
            v.ErrorStyle = XLErrorStyle.Warning;
            v.ErrorTitle = title;
            v.ErrorMessage = message;
        }
        if (people.Count > 0)
        {
            PickList(Col(ImportDefinitions.AssignedTo), staffSheet.Range(2, 1, people.Count + 1, 1), "Unknown staff member", "Choose a name from the Staff sheet. Students cannot be given tasks.");
            PickList(Col(ImportDefinitions.Reviewer), staffSheet.Range(2, 1, people.Count + 1, 1), "Unknown staff member", "Choose a name from the Staff sheet, or leave it empty.");
        }
        if (departments.Count > 0)
            PickList(Col(ImportDefinitions.Department), deptSheet.Range(2, 1, departments.Count + 1, 1), "Unknown department", "Choose a department from the Departments sheet, or leave it empty.");
        PickList(Col(ImportDefinitions.Priority), listsSheet.Range(2, 1, TaskPriorityNames.Length + 1, 1), "Unknown priority", "Choose Low, Medium, High or Critical.");
        PickList(Col(ImportDefinitions.Status), listsSheet.Range(2, 2, TaskStatusNames.Length + 1, 2), "Unknown status", "Choose To Do, In Progress, Completed or Cancelled.");

        // Instructions, with an example row kept here (not on the Tasks sheet) so it is never imported by accident.
        var help = wb.Worksheets.Add("Instructions");
        help.Cell(1, 1).Value = "How to fill this template";
        help.Cell(1, 1).Style.Font.Bold = true;
        help.Cell(1, 1).Style.Font.FontSize = 14;
        string[] lines =
        [
            "Fill one task per row on the \"Tasks\" sheet, starting at row 2. Keep the header row.",
            .. fields.Select(f => $"{f.Label}{(f.Required ? " (required)" : " (optional)")}: {f.Hint}."),
            "Assigned To and Reviewer are staff members' full names (see the \"Staff\" sheet). Students cannot be given tasks.",
            "Each task is created like one added with Add Task: it appears on the Tasks page and in the staff member's tasks.",
            "A task with the same title, assignee and due date as an existing task is skipped as a duplicate; nothing is overwritten.",
            "Upload the file on Import / Export → Tasks. You will see a preview and confirm before anything is saved.",
        ];
        for (var i = 0; i < lines.Length; i++)
        {
            help.Cell(i + 3, 1).Value = $"{i + 1}. {lines[i]}";
            help.Cell(i + 3, 1).Style.Alignment.WrapText = true;
        }
        var exampleRow = lines.Length + 5;
        help.Cell(exampleRow - 1, 1).Value = "Example row (for reference only — copy the layout, not this task):";
        help.Cell(exampleRow - 1, 1).Style.Font.Bold = true;
        var example = new[] { "Prepare the Grade 5 math exam", "Two versions, with the answer key.", people.FirstOrDefault()?.FullName ?? "Staff member's full name",
            "", "High", "To Do", SchoolTime.Today.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "14:00", "", "" };
        for (var i = 0; i < fields.Count; i++)
        {
            help.Cell(exampleRow, i + 1).Value = fields[i].Label;
            help.Cell(exampleRow + 1, i + 1).SetValue(example[i]);
        }
        var head = help.Range(exampleRow, 1, exampleRow, fields.Count);
        head.Style.Font.Bold = true;
        head.Style.Font.FontColor = HeaderText;
        head.Style.Fill.BackgroundColor = HeaderFill;
        help.Range(exampleRow + 1, 1, exampleRow + 1, fields.Count).Style.Font.Italic = true;
        help.Column(1).Width = 110;
        for (var i = 2; i <= fields.Count; i++) help.Column(i).Width = 18;

        ws.SetTabActive();
        return Save(wb);
    }

    /// <summary>One row per task: the values a person needs (names, never ids), in school time.</summary>
    private static object?[] TaskValues(TaskRow t) =>
    [
        t.Title, t.Description, t.AssignedTo.Name, t.AssignedTo.Department, TaskLabels.Priority(t.Priority), TaskLabels.Status(t.Status),
        t.DueDate, t.DueTime, t.Reviewer?.Name, t.Notes,
        SchoolTime.Current.ToLocal(t.CreatedAt), t.CreatedBy ?? t.Creator?.Name, t.CompletedAt is { } c ? SchoolTime.Current.ToLocal(c) : null,
    ];

    public byte[] Tasks(IReadOnlyList<TaskRow> tasks)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Tasks");
        WriteHeader(ws, TaskExportHeaders);
        var r = 2;
        foreach (var t in tasks)
        {
            ws.Cell(r, 1).Value = r - 1; // serial number, never the task id
            var values = TaskValues(t);
            for (var i = 0; i < values.Length; i++)
            {
                var cell = ws.Cell(r, i + 2);
                switch (values[i])
                {
                    case null: break;
                    case string s: cell.SetValue(s); break; // text exactly as stored (Arabic and English); never a formula
                    case DateOnly d: cell.Value = d.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "yyyy-mm-dd"; break;
                    case TimeOnly tm: cell.Value = tm.ToTimeSpan(); cell.Style.DateFormat.Format = "hh:mm"; break;
                    case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
                }
            }
            r++;
        }
        FinishTable(ws, tasks.Count, TaskExportHeaders.Count);
        ws.Column(3).Width = Math.Min(ws.Column(3).Width, 60);
        AddInfoSheet(wb, "Tasks export", tasks.Count, "Every task (all statuses), from the Tasks page.");
        return Save(wb);
    }

    public byte[] TasksCsv(IReadOnlyList<TaskRow> tasks)
    {
        var inv = CultureInfo.InvariantCulture;
        static string? Text(object? v, CultureInfo inv) => v switch
        {
            null => null,
            DateOnly d => d.ToString("yyyy-MM-dd", inv),
            TimeOnly t => t.ToString("HH:mm", inv),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", inv),
            _ => v.ToString(),
        };
        return CsvWriter.Write(TaskExportHeaders,
            tasks.Select((t, i) => (IReadOnlyList<string?>)[(i + 1).ToString(inv), .. TaskValues(t).Select(v => Text(v, inv))]));
    }
}
