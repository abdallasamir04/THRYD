using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Import;

/// <summary>
/// Import / Export → Tasks. Each row becomes a <see cref="TaskInput"/>: the names in the file are matched to staff records
/// and departments here, then the Tasks module's own checks (<see cref="IManagementTaskService.ValidateNewAsync"/>) and
/// save (<see cref="IManagementTaskService.CreateAsync"/>) are used, so an imported task is an ordinary task with the same
/// rules, creator and history as one added with Add Task. Students are a separate table and can never be matched.
/// </summary>
public sealed partial class ImportService
{
    private IManagementTaskService Tasks => tasks ?? throw new InvalidOperationException("Task import needs the Tasks service.");

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Accepted Priority values (English and Arabic, any case or spacing).</summary>
    private static readonly Dictionary<string, TaskPriority> PriorityWords = Words(new (string, TaskPriority)[]
    {
        ("Low", TaskPriority.Low), ("Medium", TaskPriority.Medium), ("Normal", TaskPriority.Medium), ("High", TaskPriority.High),
        ("Critical", TaskPriority.Critical), ("Urgent", TaskPriority.Critical),
        ("منخفضة", TaskPriority.Low), ("منخفض", TaskPriority.Low), ("متوسطة", TaskPriority.Medium), ("متوسط", TaskPriority.Medium),
        ("عالية", TaskPriority.High), ("عالي", TaskPriority.High), ("مرتفعة", TaskPriority.High), ("حرجة", TaskPriority.Critical), ("عاجلة", TaskPriority.Critical),
    });

    /// <summary>Accepted Status values. On Hold is recognised only to explain that new tasks cannot start on hold.</summary>
    private static readonly Dictionary<string, ManagementTaskStatus> StatusWords = Words(new (string, ManagementTaskStatus)[]
    {
        ("To Do", ManagementTaskStatus.ToDo), ("Todo", ManagementTaskStatus.ToDo), ("Not Started", ManagementTaskStatus.ToDo), ("New", ManagementTaskStatus.ToDo),
        ("In Progress", ManagementTaskStatus.InProgress), ("Started", ManagementTaskStatus.InProgress), ("Ongoing", ManagementTaskStatus.InProgress),
        ("Completed", ManagementTaskStatus.Completed), ("Complete", ManagementTaskStatus.Completed), ("Done", ManagementTaskStatus.Completed),
        ("Cancelled", ManagementTaskStatus.Cancelled), ("Canceled", ManagementTaskStatus.Cancelled),
        ("On Hold", ManagementTaskStatus.OnHold),
        ("لم تبدأ", ManagementTaskStatus.ToDo), ("جديدة", ManagementTaskStatus.ToDo), ("قيد التنفيذ", ManagementTaskStatus.InProgress),
        ("مكتملة", ManagementTaskStatus.Completed), ("منجزة", ManagementTaskStatus.Completed), ("ملغاة", ManagementTaskStatus.Cancelled),
    });

    private static Dictionary<string, T> Words<T>(IEnumerable<(string Word, T Value)> words) =>
        words.GroupBy(w => TextNormalizer.HeaderKey(w.Word)).ToDictionary(g => g.Key, g => g.First().Value);

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
        "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "dd.MM.yyyy",
        "d MMM yyyy", "d MMMM yyyy", "dd MMM yyyy", "d-MMM-yyyy", "MMM d, yyyy", "MMMM d, yyyy",
    ];

    private static readonly string[] TimeFormats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "h:mmtt", "hh:mm tt", "h tt", "htt", "h:mm:ss tt"];

    /// <summary>Text with its line breaks kept (descriptions and notes); spaces inside a line are tidied.</summary>
    private static string CleanMultiline(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(TextNormalizer.CleanText).ToList();
        return string.Join("\n", lines).Trim('\n', ' ');
    }

    /// <summary>Due date (and a time, when the cell holds one). Slash dates are read day first (25/11/2026).</summary>
    internal static (DateOnly Date, TimeOnly? Time)? ParseDue(string value)
    {
        if (DateTime.TryParseExact(value, DateFormats, Inv, DateTimeStyles.AllowWhiteSpaces, out var d))
            return (DateOnly.FromDateTime(d), d.TimeOfDay == TimeSpan.Zero ? null : TimeOnly.FromTimeSpan(d.TimeOfDay));
        return null;
    }

    internal static TimeOnly? ParseTime(string value) =>
        DateTime.TryParseExact(value.ToUpperInvariant(), TimeFormats, Inv, DateTimeStyles.AllowWhiteSpaces, out var t) ? TimeOnly.FromDateTime(t) : null;

    private async Task ValidateTasksAsync(List<PreviewRow> rows, CancellationToken ct)
    {
        // Staff only (Management Team and all other staff share one table); students are another table and never match.
        var staff = await Tasks.StaffAsync(ct);
        var byName = staff.ToLookup(s => TextNormalizer.NameKey(s.FullName));
        var departments = (await Tasks.DepartmentsAsync(ct)).ToDictionary(d => TextNormalizer.HeaderKey(d.Name));
        var studentNames = (await db.Students.AsNoTracking().Select(s => s.NormalizedName).ToListAsync(ct)).ToHashSet();
        // An existing task with the same title, assignee and due date is a duplicate (an export imported again, a file imported twice).
        var existing = (await db.ManagementTasks.AsNoTracking().Select(t => new { t.Title, t.AssignedToId, t.DueDate }).ToListAsync(ct))
            .Select(t => (TextNormalizer.NameKey(t.Title), t.AssignedToId, t.DueDate)).ToHashSet();
        var seen = new Dictionary<(string, int, DateOnly?), int>();

        foreach (var row in rows)
        {
            var title = row.Value(ImportDefinitions.TaskTitle);
            if (title.Length == 0) row.Errors.Add("The task title is missing.");

            TaskDepartmentOption? dept = null;
            var deptText = row.Value(ImportDefinitions.Department);
            if (deptText.Length > 0 && !departments.TryGetValue(TextNormalizer.HeaderKey(deptText), out dept))
                row.Errors.Add($"Department \"{deptText}\" does not exist. Use a department name from the Departments page.");

            var assignee = Person(row, ImportDefinitions.AssignedTo, "Assigned To", required: true, dept);
            if (assignee is not null) row.AssigneeNote = assignee.Note;
            var reviewer = Person(row, ImportDefinitions.Reviewer, "Reviewer", required: false, null);

            var priority = TaskPriority.Medium;
            var priorityText = row.Value(ImportDefinitions.Priority);
            if (priorityText.Length > 0 && !PriorityWords.TryGetValue(TextNormalizer.HeaderKey(priorityText), out priority))
                row.Errors.Add($"Priority \"{priorityText}\" is not one of Low, Medium, High or Critical.");

            var status = ManagementTaskStatus.ToDo;
            var statusText = row.Value(ImportDefinitions.Status);
            if (statusText.Length > 0)
            {
                if (!StatusWords.TryGetValue(TextNormalizer.HeaderKey(statusText), out status))
                    row.Errors.Add($"Status \"{statusText}\" is not one of To Do, In Progress, Completed or Cancelled.");
                else if (status == ManagementTaskStatus.OnHold)
                    row.Errors.Add("On Hold is not available for new tasks. Use To Do or In Progress.");
            }

            DateOnly? due = null;
            TimeOnly? time = null;
            var dueText = row.Value(ImportDefinitions.DueDate);
            if (dueText.Length > 0)
            {
                if (ParseDue(dueText) is { } parsed) (due, time) = parsed;
                else row.Errors.Add($"Due date \"{dueText}\" is not a date. Use an Excel date, YYYY-MM-DD or DD/MM/YYYY.");
            }
            var timeText = row.Value(ImportDefinitions.DueTime);
            if (timeText.Length > 0)
            {
                if (ParseTime(timeText) is { } t) time = t;
                else row.Errors.Add($"Due time \"{timeText}\" is not a time. Use 14:30 or 2:30 PM.");
            }

            if (row.Errors.Count > 0) { row.Status = RowStatus.Invalid; continue; }

            var input = new TaskInput
            {
                Title = title, Description = Blank(row.Value(ImportDefinitions.Description)), Notes = Blank(row.Value(ImportDefinitions.Notes)),
                AssignedToId = assignee!.Id, ReviewerId = reviewer?.Id, Priority = priority, Status = status, DueDate = due, DueTime = time,
                // With a department, the Tasks module checks that the assignee belongs to it (as Add Task from a department does).
                DepartmentId = dept?.Id,
            };
            // The Tasks module's own rules: title length, current staff (contract not ended), reviewer, due date, status…
            var check = await Tasks.ValidateNewAsync(input, ct);
            if (!check.Succeeded)
            {
                row.Errors.AddRange(check.Errors.Select(e => e.Value).Distinct());
                row.Status = RowStatus.Invalid;
                continue;
            }
            row.Task = input;

            var key = (TextNormalizer.NameKey(title), assignee.Id, due);
            if (existing.Contains(key))
            {
                row.Status = RowStatus.Duplicate;
                row.Errors.Add($"{assignee.FullName} already has a task with this title{(due is { } d ? $" due {d.ToString("d MMM yyyy", Inv)}" : " and no due date")}.");
            }
            else if (seen.TryGetValue(key, out var first))
            {
                row.Status = RowStatus.Duplicate;
                row.Errors.Add($"Same task as row {first} of this file.");
            }
            else seen[key] = row.RowNumber;
        }

        // A staff member by name: exactly one current staff record (within the department, when one is given).
        TaskStaffOption? Person(PreviewRow row, string field, string label, bool required, TaskDepartmentOption? inDept)
        {
            var name = row.Value(field);
            if (name.Length == 0)
            {
                if (required) row.Errors.Add($"{label} is missing. Enter a staff member's full name.");
                return null;
            }
            var key = TextNormalizer.NameKey(name);
            var all = byName[key].ToList();
            var matches = inDept is null ? all : all.Where(s => s.DepartmentId == inDept.Id).ToList();
            if (matches.Count > 1) matches = matches.Where(s => s.IsActive).ToList() is { Count: > 0 } active ? active : matches;
            switch (matches.Count)
            {
                case 1: return matches[0];
                case 0 when all.Count > 0 && inDept is not null:
                    row.Errors.Add($"{all[0].FullName} is not a member of the {inDept.Name} department.");
                    return null;
                case 0 when studentNames.Contains(key):
                    row.Errors.Add($"{label}: \"{name}\" is a student. Tasks can only be given to staff members.");
                    return null;
                case 0:
                    row.Errors.Add($"{label}: no staff member is named \"{name}\". Use the full name as it appears on the staff lists.");
                    return null;
                default:
                    row.Errors.Add($"{label}: {matches.Count} staff members are named \"{name}\". Add the Department column to tell them apart.");
                    return null;
            }
        }

        static string? Blank(string value) => value.Length == 0 ? null : value;
    }

    /// <summary>Creates the ready rows one by one with the Tasks module's Add Task save; a row it refuses is reported, never dropped.</summary>
    private async Task<int> CreateTasksAsync(List<PreviewRow> ready, CancellationToken ct)
    {
        var created = 0;
        foreach (var row in ready)
        {
            ServiceResult result;
            try
            {
                result = await Tasks.CreateAsync(row.Task!, ct);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Task import: row {Row} could not be saved", row.RowNumber);
                db.ChangeTracker.Clear();
                result = ServiceResult.Fail("", "The task could not be saved. Try this row again.");
            }
            if (result.Succeeded) { created++; continue; }
            row.Status = RowStatus.Invalid;
            row.Errors.AddRange(result.Errors.Select(e => e.Value));
        }
        return created;
    }
}
