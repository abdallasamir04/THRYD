using System.Globalization;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Common;

public static class ManagementPositions
{
    public static readonly ManagementPosition[] All = Enum.GetValues<ManagementPosition>();

    public static string Name(ManagementPosition p) => p switch
    {
        ManagementPosition.SchoolDirector => "School Director",
        ManagementPosition.Sqam => "SQAM",
        ManagementPosition.KgStageCoordinator => "KG Stage Coordinator",
        ManagementPosition.LowerElementaryCoordinator => "Lower Elementary Coordinator",
        ManagementPosition.UpperElementaryCoordinator => "Upper Elementary Coordinator",
        ManagementPosition.MiddleSchoolCoordinator => "Middle School Coordinator",
        ManagementPosition.HighSchoolCoordinator => "High School Coordinator",
        _ => p.ToString(),
    };

    public static string Name(ManagementPosition? p) => p is { } v ? Name(v) : "No position assigned";

    /// <summary>One-line scope of each position, shown on the School Leadership pages (display text only).</summary>
    public static string Description(ManagementPosition p) => p switch
    {
        ManagementPosition.SchoolDirector => "Leads the school and its Management Team.",
        ManagementPosition.Sqam => "School quality assurance and monitoring.",
        ManagementPosition.KgStageCoordinator => "Coordinates the KG Stage: Pre-K, KG1 and KG2.",
        ManagementPosition.LowerElementaryCoordinator => "Coordinates Lower Elementary.",
        ManagementPosition.UpperElementaryCoordinator => "Coordinates Upper Elementary.",
        ManagementPosition.MiddleSchoolCoordinator => "Coordinates Middle School.",
        ManagementPosition.HighSchoolCoordinator => "Coordinates High School.",
        _ => "",
    };

    /// <summary>The position's page under School Leadership, e.g. /management/leadership/positions/Sqam.</summary>
    public static string Href(ManagementPosition p) => $"/management/leadership/positions/{p}";

    public static string Icon(ManagementPosition p) => p switch
    {
        ManagementPosition.SchoolDirector => "crown",
        ManagementPosition.Sqam => "shield-check",
        _ => "layers",
    };
}

/// <summary>
/// Values of the Position dropdowns (Add Staff, Management Team member form): "" none, a leadership position's name
/// (e.g. "Sqam"), "custom-12" for a school-added position, and "new" for + Add New Position.
/// </summary>
public static class PositionChoices
{
    public const string New = "new";
    private const string CustomPrefix = "custom-";

    public static string Custom(int id) => CustomPrefix + id.ToString(CultureInfo.InvariantCulture);

    public static string Of(ManagementPosition? position, int? customPositionId) =>
        position is { } p ? p.ToString() : customPositionId is { } c ? Custom(c) : "";

    /// <summary>
    /// Puts the chosen position on <paramref name="input"/>. A null <paramref name="choice"/> (forms posting the
    /// leadership position directly, or "No position") keeps <paramref name="legacy"/>. False when the value is not one
    /// the form offers.
    /// </summary>
    public static bool Apply(string? choice, ManagementPosition? legacy, string? newTitle, Services.ManagementMemberInput input)
    {
        (input.Position, input.CustomPositionId, input.NewPositionTitle) = (null, null, null);
        var v = choice?.Trim() ?? "";
        if (v.Length == 0) { input.Position = legacy is { } l && Enum.IsDefined(l) ? l : null; return legacy is null || input.Position is not null; }
        if (v == New) { input.NewPositionTitle = newTitle ?? ""; return true; }
        if (v.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            if (!int.TryParse(v[CustomPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return false;
            input.CustomPositionId = id;
            return true;
        }
        if (int.TryParse(v, out _) || !Enum.TryParse<ManagementPosition>(v, true, out var p) || !Enum.IsDefined(p)) return false;
        input.Position = p;
        return true;
    }
}

public static class TaskLabels
{
    /// <summary>Statuses offered for a task. On Hold is only offered to a task that already has it.</summary>
    public static readonly ManagementTaskStatus[] Statuses =
        [ManagementTaskStatus.ToDo, ManagementTaskStatus.InProgress, ManagementTaskStatus.Completed, ManagementTaskStatus.Cancelled];

    /// <summary>The statuses a task in <paramref name="current"/> can be set to.</summary>
    public static IEnumerable<ManagementTaskStatus> StatusesFor(ManagementTaskStatus current) =>
        current == ManagementTaskStatus.OnHold ? [.. Statuses.Take(2), ManagementTaskStatus.OnHold, .. Statuses.Skip(2)] : Statuses;

    public static readonly TaskPriority[] Priorities = [TaskPriority.Critical, TaskPriority.High, TaskPriority.Medium, TaskPriority.Low];

    /// <summary>Statuses that close a task: no longer open, never overdue.</summary>
    public static bool IsClosed(ManagementTaskStatus s) => s is ManagementTaskStatus.Completed or ManagementTaskStatus.Cancelled;

    public static string Status(ManagementTaskStatus s) => s switch
    {
        ManagementTaskStatus.ToDo => "To Do",
        ManagementTaskStatus.InProgress => "In Progress",
        ManagementTaskStatus.Completed => "Completed",
        ManagementTaskStatus.OnHold => "On Hold",
        ManagementTaskStatus.Cancelled => "Cancelled",
        _ => s.ToString(),
    };

    /// <summary>CSS modifier and filter value: task-status--todo, --in-progress, --completed, --on-hold, --cancelled.</summary>
    public static string StatusSlug(ManagementTaskStatus s) => s switch
    {
        ManagementTaskStatus.ToDo => "todo",
        ManagementTaskStatus.InProgress => "in-progress",
        ManagementTaskStatus.Completed => "completed",
        ManagementTaskStatus.OnHold => "on-hold",
        ManagementTaskStatus.Cancelled => "cancelled",
        _ => "todo",
    };

    public static string Priority(TaskPriority p) => p.ToString();

    public static string PrioritySlug(TaskPriority p) => p.ToString().ToLowerInvariant();

    /// <summary>A priority filter value; "urgent" (the former name of Critical) still works in saved links.</summary>
    public static TaskPriority? ParsePriority(string? value) =>
        string.Equals(value, "urgent", StringComparison.OrdinalIgnoreCase) ? TaskPriority.Critical
        : Enum.TryParse<TaskPriority>(value, true, out var p) && Enum.IsDefined(p) && !int.TryParse(value, out _) ? p : null;

    public static string Due(DateOnly? date, TimeOnly? time)
    {
        if (date is not { } d) return "No due date";
        var c = CultureInfo.InvariantCulture;
        var text = d.Year == SchoolWeek.Today.Year ? d.ToString("ddd d MMM", c) : d.ToString("d MMM yyyy", c);
        return time is { } t ? $"{text}, {Time(t)}" : text;
    }

    /// <summary>12-hour clock with AM/PM ("3:30 PM"); the stored value stays a 24-hour <see cref="TimeOnly"/>.</summary>
    public static string Time(TimeOnly t) => t.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>Overdue = still open and the due moment has passed (a date without a time is due at the end of that day). <paramref name="now"/> is school-local time.</summary>
    public static bool IsOverdue(ManagementTaskStatus status, DateOnly? date, TimeOnly? time, DateTime now)
    {
        if (IsClosed(status) || date is not { } d) return false;
        var today = DateOnly.FromDateTime(now);
        if (d < today) return true;
        return d == today && time is { } t && t < TimeOnly.FromDateTime(now);
    }

    /// <summary>
    /// The real time left until the due moment (negative once overdue), or null without a due date. Due dates and times
    /// are school-local wall times; both ends go through the school time zone, so a daylight-saving change in between
    /// is counted correctly (a day across the spring change is 23 hours). A date without a time is due at the end of it.
    /// </summary>
    public static TimeSpan? Remaining(DateOnly? date, TimeOnly? time, ISchoolTimeService clock)
    {
        if (date is not { } d) return null;
        var dueUtc = time is { } t ? clock.ToUtc(d, t) : clock.StartOfDayUtc(d.AddDays(1));
        return dueUtc - clock.UtcNow;
    }

    /// <summary>"3 days left", "5 h left", "Overdue by 2 days"…</summary>
    public static string? RemainingText(ManagementTaskStatus status, DateOnly? date, TimeOnly? time, ISchoolTimeService clock)
    {
        if (IsClosed(status) || Remaining(date, time, clock) is not { } left) return null;
        var span = left.Duration();
        var amount = span.TotalDays >= 1 ? Ui.Plural((int)span.TotalDays, "day", "days")
            : span.TotalHours >= 1 ? Ui.Plural((int)span.TotalHours, "hour", "hours")
            : Ui.Plural(Math.Max(1, (int)span.TotalMinutes), "minute", "minutes");
        return left < TimeSpan.Zero ? $"Overdue by {amount}" : $"{amount} left";
    }
}
