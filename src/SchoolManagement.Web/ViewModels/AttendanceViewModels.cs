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
    public double? SchoolPercent => School.Recorded == 0 ? null : Math.Round(School.Attended * 100.0 / School.Recorded, 1);
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

/// <summary>Session Attendance: the class picker (cards per stage) for a date.</summary>
public sealed class SessionPickerViewModel
{
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<ClassCard> Cards { get; init; }
    public required IReadOnlyList<StudentPick> Students { get; init; }
    public bool IsSchoolDay { get; init; }
    public string? Holiday { get; init; }
}

/// <summary>A class's Session Attendance page: the day's lessons and the register of the lesson shown.</summary>
public sealed class SessionClassViewModel
{
    public required ClassDay Day { get; init; }
    public LiveRegister? Register { get; init; }
}

/// <summary>Session Attendance reports: student, class, lesson (one date) or school, over a period.</summary>
public sealed class SessionReportForm
{
    public string Mode { get; set; } = "school";
    public int? StudentId { get; set; }
    public int? ClassId { get; set; }
    public int? Lesson { get; set; }
    public string? Date { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }

    private static DateOnly? Parse(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Everything the chosen report needs is there.</summary>
    public bool IsComplete => Mode switch
    {
        "student" => StudentId is not null,
        "class" => ClassId is not null,
        "lesson" => Lesson is not null,
        _ => true,
    };

    public SessionReportQuery ToQuery()
    {
        var today = SchoolWeek.Today;
        if (Mode == "lesson") { var d = Parse(Date) ?? today; return new("lesson", null, ClassId, Lesson, d, d); }
        var to = Parse(To) ?? today;
        var from = Parse(From) ?? (Mode == "school" ? to : to.AddDays(-29));
        return new(Mode is "student" or "class" ? Mode : "school", StudentId, ClassId, null, from, to);
    }
}

public sealed class SessionReportsViewModel
{
    public required SessionReportForm Form { get; init; }
    public required SessionFilterOptions Options { get; init; }
    public SessionReport? Report { get; init; }
    public IReadOnlyList<ClassLesson> LessonChoices { get; init; } = [];
}

