using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

// ---------- Students → Student Attendance (School Attendance + Session Attendance) ----------

public sealed class AttendanceHubViewModel
{
    public required DateOnly Date { get; init; }
    public required ArrivalSummary School { get; init; }
    public required SessionDay Sessions { get; init; }
    public required AlertCounts Alerts { get; init; }
    public required IReadOnlyList<AlertRow> RecentAlerts { get; init; }

    /// <summary>Present (on time or late) ÷ recorded; null when nothing is recorded.</summary>
    public double? SchoolPercent => School.Recorded == 0 ? null : Math.Round(School.Present * 100.0 / School.Recorded, 1);
}

public sealed class SessionListViewModel
{
    public required SessionDay Day { get; init; }
    public required SessionFilterOptions Options { get; init; }

    public string Query(params (string Key, object? Value)[] changes)
    {
        var f = Day.Filter;
        var values = new Dictionary<string, string?>
        {
            ["date"] = SchoolWeek.Iso(f.Date), ["classId"] = f.ClassId?.ToString(), ["slotId"] = f.SlotId?.ToString(),
            ["subject"] = f.Subject, ["teacherId"] = f.TeacherId?.ToString(), ["studentId"] = f.StudentId?.ToString(),
        };
        foreach (var (k, v) in changes) values[k] = v?.ToString();
        return "/attendance/sessions?" + string.Join("&", values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!)}"));
    }
}

public sealed class SessionRegisterViewModel
{
    public required SessionRegister Register { get; init; }

    /// <summary>The list the user came from (filters kept).</summary>
    public required string Back { get; init; }
}

/// <summary>The register form: one entry per student (a student without a status stays "Not recorded").</summary>
public sealed class SessionSaveForm
{
    public string? Date { get; set; }

    [StringLength(SessionAttendanceCorrection.ReasonMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Reason { get; set; }

    public List<SessionSaveEntry> Entries { get; set; } = [];
}

public sealed class SessionSaveEntry
{
    public int StudentId { get; set; }
    public SessionAttendanceStatus? Status { get; set; }
    public string? Notes { get; set; }
}

public sealed class LessonCancelModel
{
    public int SessionId { get; set; }
    public string? Date { get; set; }

    [Display(Name = "Reason")]
    [Required(ErrorMessage = "Explain why the lesson does not take place.")]
    [StringLength(SessionCancellation.ReasonMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Reason { get; set; }

    public LessonView? Lesson { get; set; }
}

public sealed class AlertListViewModel
{
    public required IReadOnlyList<AlertRow> Alerts { get; init; }
    public required AlertCounts Counts { get; init; }
    public required AlertFilter Filter { get; init; }
    public required IReadOnlyList<ClassOption> Classes { get; init; }

    public string Query(params (string Key, object? Value)[] changes)
    {
        var values = new Dictionary<string, string?>
        {
            ["type"] = Filter.Type switch { AttendanceAlertType.ConsecutiveAbsence => "absences", AttendanceAlertType.AbsentFromSession => "lessons", _ => null },
            ["state"] = Filter.State, ["classId"] = Filter.ClassId?.ToString(), ["search"] = Filter.Search,
        };
        foreach (var (k, v) in changes) values[k] = v?.ToString();
        var qs = string.Join("&", values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!)}"));
        return "/attendance/alerts" + (qs.Length == 0 ? "" : "?" + qs);
    }
}

/// <summary>Follow-up, resolve or reopen an alert (the note is kept in the alert's history).</summary>
public sealed class AlertActionModel
{
    public int Id { get; set; }
    public string Action { get; set; } = "follow-up";

    [StringLength(AttendanceAlert.ResolutionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Note { get; set; }

    public AlertDetails? Details { get; set; }
}

public sealed class HolidaysViewModel
{
    public required IReadOnlyList<HolidayView> Holidays { get; init; }
    public HolidayForm Form { get; init; } = new();
}

public sealed class HolidayForm
{
    [Display(Name = "Name")]
    [Required(ErrorMessage = "Enter the holiday's name.")]
    [StringLength(SchoolHoliday.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Name { get; set; }

    [Display(Name = "First day")]
    [Required(ErrorMessage = "Enter the first day of the holiday.")]
    public DateOnly? StartDate { get; set; }

    [Display(Name = "Last day")]
    public DateOnly? EndDate { get; set; }
}
