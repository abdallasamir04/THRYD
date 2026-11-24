using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

// ---------- Duty audit ----------

public sealed class DutyAuditViewModel
{
    public required DutyAuditQuery Query { get; init; }
    public required PagedResult<DutyRow> Result { get; init; }
    public required IReadOnlyList<StaffOption> Staff { get; init; }
    public required IReadOnlyList<string> Locations { get; init; }
}

public sealed class DutyAuditFormModel
{
    public int Id { get; set; }

    [Display(Name = "Audit notes")]
    [StringLength(DutyAssignment.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? AuditNotes { get; set; }

    [Display(Name = "Issues encountered")]
    [StringLength(DutyAssignment.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? IssuesEncountered { get; set; }

    public bool FollowUpRequired { get; set; }

    public DutyRow? Duty { get; set; }
}

// ---------- Staff duty history and evaluations ----------

public sealed class StaffOverviewViewModel
{
    public required IReadOnlyList<StaffDutyOverviewRow> Rows { get; init; }
    public string? Search { get; init; }
}

public sealed class StaffProfileViewModel
{
    public required StaffProfile Profile { get; init; }
    public TeacherRow? Teacher { get; init; }
    public WorkloadSummary? Workload { get; init; }
    public IReadOnlyList<WorkloadAssignmentRow> WorkloadAssignments { get; init; } = [];
    public IReadOnlyList<WorkloadHistoryRow> WorkloadHistory { get; init; } = [];

    /// <summary>KG class teaching-team roles (Homeroom Teacher / Co-Teacher), all academic years — not teaching periods.</summary>
    public IReadOnlyList<StaffClassAssignmentView> ClassAssignments { get; init; } = [];

    /// <summary>Organizational positions (current, history) and data-quality warnings of this person.</summary>
    public Services.OrgStructure.StaffOrgSummary Org { get; init; } = Services.OrgStructure.StaffOrgSummary.Empty;
    public bool CanManageOrg { get; init; }
}

public sealed class FeedbackFormModel
{
    public int AssignmentId { get; set; }

    [Range(1, 5, ErrorMessage = "Choose a rating from 1 to 5 stars.")]
    public int Rating { get; set; }

    [Display(Name = "Written feedback")]
    [Required(ErrorMessage = "Write the feedback.")]
    [StringLength(StaffDutyFeedback.FeedbackMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Feedback { get; set; } = "";

    [StringLength(StaffDutyFeedback.ListMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Strengths { get; set; }

    [StringLength(StaffDutyFeedback.ListMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Issues { get; set; }

    public bool FollowUpRequired { get; set; }

    [Display(Name = "Evaluated by")]
    [Required(ErrorMessage = "Choose who is evaluating.")]
    public int? EvaluatorStaffId { get; set; }

    [Display(Name = "Evaluation date")]
    public DateOnly? EvaluationDate { get; set; }

    public EvaluationContext? Context { get; set; }
    public IReadOnlyList<StaffOption> Evaluators { get; set; } = [];

    public FeedbackInput ToInput() => new(AssignmentId, Rating, Feedback, Strengths, Issues, FollowUpRequired, EvaluatorStaffId, EvaluationDate);
}

// ---------- Student arrivals ----------

/// <summary>Daily Attendance: the day (today by default), the class chosen (or all classes) and its register.</summary>
public sealed class DailyAttendanceViewModel
{
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<ClassOption> Classes { get; init; }
    public int? ClassId { get; init; }
    public bool AllClasses { get; init; }
    public ArrivalChecklist? Checklist { get; init; }
    public required ArrivalDay Day { get; init; }

    /// <summary>School start time (later = Late) and the 09:30 cutoff (registration closes).</summary>
    public required ArrivalRules Rules { get; init; }
    public string? ClassName => ClassId is { } id ? Classes.FirstOrDefault(c => c.Id == id)?.Name : AllClasses ? "All classes" : null;
}

public static class ArrivalForm
{
    public static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
}

/// <summary>A past day: an arrival entered with its time (the outcome still comes from the time).</summary>
public sealed class ArrivalAddModel
{
    public int StudentId { get; set; }
    public string? Date { get; set; }
    public string? Time { get; set; }

    [StringLength(StudentArrivalRecord.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public ArrivalRow? Row { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public ArrivalRules? Rules { get; set; }
}

/// <summary>Explicit correction of a record: the outcome, the arrival time (Present / Late), notes and a reason.</summary>
public sealed class ArrivalCorrectionModel
{
    public int Id { get; set; }
    public ArrivalOutcome Outcome { get; set; } = ArrivalOutcome.Present;
    public string? Time { get; set; }

    [StringLength(StudentArrivalRecord.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    [Display(Name = "Reason for the correction")]
    [Required(ErrorMessage = "Explain why the record is being corrected.")]
    [StringLength(StudentArrivalCorrection.ReasonMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Reason { get; set; }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public ArrivalRow? Record { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public ArrivalRules? Rules { get; set; }
}

/// <summary>Attendance History &amp; Reports: what (student / class / grade / school), which day or period, and an optional status.</summary>
public sealed class AttendanceHistoryQuery
{
    /// <summary>student, class, grade or school.</summary>
    public string Mode { get; set; } = "school";
    public int? StudentId { get; set; }
    public int? ClassId { get; set; }
    public string? Grade { get; set; }

    /// <summary>day (one date) or range (from / to).</summary>
    public string Period { get; set; } = "day";
    public string? Date { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>present, late, absent or notrecorded (class / grade / school).</summary>
    public string? Status { get; set; }
}

public sealed class AttendanceHistoryViewModel
{
    public required AttendanceHistoryQuery Query { get; init; }
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public required IReadOnlyList<AttendanceStudentPick> Students { get; init; }
    public required IReadOnlyList<ClassOption> Classes { get; init; }
    public StudentAttendanceReport? Student { get; set; }
    public GroupAttendanceReport? Group { get; set; }
}

public sealed class StudentProfileViewModel
{
    public required StudentRow Student { get; init; }
    public required StudentArrivalHistory History { get; init; }
    public IReadOnlyList<StudentTransferHistoryRow> TransferHistory { get; init; } = [];
    public ClassRow? CurrentClass { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public bool NewestFirst { get; init; } = true;

    /// <summary>The edit / transfer actions are shown (StudentRecordsAccess; checked again on the server).</summary>
    public bool CanEdit { get; init; } = true;
}

public sealed class StudentTransferModel
{
    public int StudentId { get; set; }
    public StudentRow? Student { get; set; }
    public int TargetClassId { get; set; }
    public IReadOnlyList<ClassOption> AvailableClasses { get; set; } = [];
}

// ---------- Settings additions / dashboard ----------

public sealed class OperationsIndicators
{
    public required DateOnly Date { get; init; }
    public required IReadOnlyDictionary<DutyStatus, int> DutyStatus { get; init; }
    public required ArrivalSummary Arrivals { get; init; }

    /// <summary>Staff Attendance today: Present / Late / Absent records of the active staff (teachers and management).</summary>
    public required StaffAttendanceToday Staff { get; init; }

    /// <summary>Coverage per section for the day: Morning, Midday, Dismissal (covered / total) and Activities (scheduled).</summary>
    public IReadOnlyList<DutyCoverage> Coverage { get; init; } = [];
}

/// <summary>
/// One category of the dashboard's duty coverage: fully covered sections ÷ sections (<see cref="SectionCoverage"/>).
/// <paramref name="One"/> / <paramref name="Many"/> name a section ("section", "activity").
/// </summary>
public sealed record DutyCoverage(string Name, string Href, SectionCoverage Sections, string One, string Many)
{
    public int Percent => Sections.Total == 0 ? 0 : (int)Math.Round(100.0 * Sections.Covered / Sections.Total);

    /// <summary>Tells "no sections" apart from "nothing scheduled", "still open", "not covered" and "fully covered".</summary>
    public string Summary
    {
        get
        {
            var s = Sections;
            if (s.Total == 0) return One == "section" ? $"No {Name.ToLowerInvariant()} sections" : $"No {Many} scheduled today";
            var parts = new List<string> { $"{s.Covered} of {Ui.Plural(s.Total, One, Many)} fully covered" };
            if (s.Open > 0) parts.Add($"{s.Open} pending or in progress");
            if (s.NotCovered > 0) parts.Add($"{s.NotCovered} not covered");
            if (s.NothingScheduled > 0) parts.Add($"{s.NothingScheduled} with no duties scheduled");
            return string.Join(" · ", parts);
        }
    }
}
