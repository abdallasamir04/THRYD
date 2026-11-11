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

    public static string Icon(ManagementPosition p) => p switch
    {
        ManagementPosition.SchoolDirector => "crown",
        ManagementPosition.Sqam => "shield-check",
        _ => "layers",
    };
}

public static class TaskLabels
{
    public static readonly ManagementTaskStatus[] Statuses =
        [ManagementTaskStatus.ToDo, ManagementTaskStatus.InProgress, ManagementTaskStatus.OnHold, ManagementTaskStatus.Completed];

    public static readonly TaskPriority[] Priorities = [TaskPriority.Urgent, TaskPriority.High, TaskPriority.Medium, TaskPriority.Low];

    public static string Status(ManagementTaskStatus s) => s switch
    {
        ManagementTaskStatus.ToDo => "To Do",
        ManagementTaskStatus.InProgress => "In Progress",
        ManagementTaskStatus.Completed => "Completed",
        ManagementTaskStatus.OnHold => "On Hold",
        _ => s.ToString(),
    };

    /// <summary>CSS modifier: task-status--todo, --in-progress, --completed, --on-hold.</summary>
    public static string StatusSlug(ManagementTaskStatus s) => s switch
    {
        ManagementTaskStatus.ToDo => "todo",
        ManagementTaskStatus.InProgress => "in-progress",
        ManagementTaskStatus.Completed => "completed",
        ManagementTaskStatus.OnHold => "on-hold",
        _ => "todo",
    };

    public static string Priority(TaskPriority p) => p.ToString();

    public static string PrioritySlug(TaskPriority p) => p.ToString().ToLowerInvariant();

    public static string Due(DateOnly? date, TimeOnly? time)
    {
        if (date is not { } d) return "No due date";
        var c = CultureInfo.InvariantCulture;
        var text = d.Year == SchoolWeek.Today.Year ? d.ToString("ddd d MMM", c) : d.ToString("d MMM yyyy", c);
        return time is { } t ? $"{text}, {t.ToString("HH:mm", c)}" : text;
    }

    /// <summary>Overdue = not completed and the due moment has passed (a date without a time is due at the end of that day).</summary>
    public static bool IsOverdue(ManagementTaskStatus status, DateOnly? date, TimeOnly? time, DateTime now)
    {
        if (status == ManagementTaskStatus.Completed || date is not { } d) return false;
        var today = DateOnly.FromDateTime(now);
        if (d < today) return true;
        return d == today && time is { } t && t < TimeOnly.FromDateTime(now);
    }
}
