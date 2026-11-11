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
    public DutyStatus Status { get; set; } = DutyStatus.Pending;

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

public sealed class ArrivalChecklistViewModel
{
    public required ArrivalChecklist Checklist { get; init; }
    public required IReadOnlyList<ClassOption> Classes { get; init; }
    public required IReadOnlyList<StaffOption> Staff { get; init; }
    public string Show { get; init; } = "all";
    public int? OperatorStaffId { get; init; }
}

public sealed class ArrivalBatchRequest
{
    public string? Date { get; set; }
    public List<ArrivalBatchEntry> Entries { get; set; } = [];
}

public sealed class ArrivalBatchEntry
{
    public int StudentId { get; set; }
    public ArrivalStatus? Status { get; set; }
    public string? Time { get; set; }
    public ArrivalMethod? Method { get; set; }
    public string? Location { get; set; }
    public int? ReceivedByStaffId { get; set; }
    public bool ReceivingConfirmed { get; set; }
    public string? Notes { get; set; }

    public ArrivalEntry ToEntry() => new(StudentId, Status, ArrivalForm.ParseTime(Time), Method, Location, ReceivedByStaffId, ReceivingConfirmed, Notes);
}

public static class ArrivalForm
{
    public static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
}

public sealed class ArrivalCorrectionModel
{
    public int Id { get; set; }
    public ArrivalStatus? Status { get; set; }
    public string? Time { get; set; }
    public ArrivalMethod? Method { get; set; }

    [StringLength(StudentArrivalRecord.LocationMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Location { get; set; }

    public int? ReceivedByStaffId { get; set; }
    public bool ReceivingConfirmed { get; set; }

    [StringLength(StudentArrivalRecord.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    [Display(Name = "Reason for the correction")]
    [Required(ErrorMessage = "Explain why the record is being corrected.")]
    [StringLength(StudentArrivalCorrection.ReasonMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Reason { get; set; }

    public ArrivalRow? Record { get; set; }
    public IReadOnlyList<StaffOption> Staff { get; set; } = [];

    public ArrivalEntry ToEntry(int studentId) => new(studentId, Status, ArrivalForm.ParseTime(Time), Method, Location, ReceivedByStaffId, ReceivingConfirmed, Notes);
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
    public required double? AverageRating { get; init; }
    public required int RatedAssignments { get; init; }
    public required ArrivalSummary Arrivals { get; init; }
}
