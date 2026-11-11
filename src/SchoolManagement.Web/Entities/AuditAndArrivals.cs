namespace SchoolManagement.Web.Entities;

// ============================================================ Duty audit

/// <summary>Duty families. Only Morning exists now; new values are added, never reused.</summary>
public enum DutyType
{
    Morning = 1,
    Midday = 2,
    Dismissal = 3,
}

/// <summary>
/// The four operational statuses. Pending = scheduled, not started; InProgress = being performed;
/// Covered = completed with confirmed staff coverage; NotCovered = not adequately covered.
/// </summary>
public enum DutyStatus
{
    Pending = 1,
    InProgress = 2,
    Covered = 3,
    NotCovered = 4,
}

public enum DutyAuditAction
{
    Created = 1,
    Updated = 2,
    StatusChanged = 3,
    AttendanceRecorded = 4,
    BackupRequested = 5,
    BackupResponded = 6,
    BackupAttendanceRecorded = 7,
    AuditNotesUpdated = 8,
    Deleted = 9,
}

/// <summary>
/// Append-only trail of every change to a duty session. It has no foreign key to the duty so the
/// trail (with its snapshot of date, component and description) survives if the duty is deleted.
/// </summary>
public class DutyAudit
{
    public const int SummaryMaxLength = 500;

    public long Id { get; set; }
    public int DutyAssignmentId { get; set; }
    public DutyType DutyType { get; set; }
    public DutyComponent Component { get; set; }
    public DateOnly DutyDate { get; set; }
    public string Description { get; set; } = "";
    public DutyAuditAction Action { get; set; }
    public DutyStatus StatusAfter { get; set; }
    public string Summary { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
}

// ============================================================ Staff on a duty

public enum DutyRole
{
    Primary = 1,
    Backup = 2,
}

/// <summary>
/// One person's assignment to one duty session, in a role. Every primary and every backup ever
/// asked keeps a row (replaced or declined rows stay with IsCurrent = false), so each person has
/// a full history and each assignment can be evaluated separately.
/// </summary>
public class DutyStaffAssignment
{
    public const int EndReasonMaxLength = 200;

    public int Id { get; set; }
    public int DutyAssignmentId { get; set; }
    public DutyAssignment DutyAssignment { get; set; } = null!;
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public DutyRole Role { get; set; }
    public AttendanceStatus Attendance { get; set; }

    /// <summary>Backup rows only: the request's answer.</summary>
    public BackupRequestStatus BackupStatus { get; set; }

    public bool IsCurrent { get; set; } = true;
    public DateTime AssignedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public string? AssignedBy { get; set; }

    public ICollection<StaffDutyFeedback> Feedback { get; set; } = new List<StaffDutyFeedback>();
}

/// <summary>A supervisor's evaluation of one staff assignment. Never edited or deleted: a new evaluation is added instead.</summary>
public class StaffDutyFeedback
{
    public const int FeedbackMaxLength = 2000;
    public const int ListMaxLength = 1000;

    public int Id { get; set; }
    public int DutyStaffAssignmentId { get; set; }
    public DutyStaffAssignment DutyStaffAssignment { get; set; } = null!;
    public int Rating { get; set; }
    public string Feedback { get; set; } = "";
    public string? Strengths { get; set; }
    public string? Issues { get; set; }
    public bool FollowUpRequired { get; set; }

    /// <summary>The supervisor, from the staff directory.</summary>
    public int EvaluatorStaffId { get; set; }
    public StaffMember Evaluator { get; set; } = null!;

    public DateOnly EvaluationDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? RecordedBy { get; set; }
}

// ============================================================ Student arrival

public enum ArrivalStatus
{
    Present = 1,
    Absent = 2,
}

public enum ArrivalMethod
{
    SchoolBus = 1,
    PrivateCar = 2,
    ParentDropOff = 3,
}

public enum LateStatus
{
    OnTime = 1,
    Late = 2,
}

/// <summary>One student's arrival on one school day (unique per student and date).</summary>
public class StudentArrivalRecord : IAuditable
{
    public const int LocationMaxLength = 120;
    public const int NotesMaxLength = 500;

    public int Id { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;

    /// <summary>The student's class on that day (kept even if the student moves class later).</summary>
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    public DateOnly ArrivalDate { get; set; }
    public ArrivalStatus Status { get; set; }

    /// <summary>Exact time as recorded (Present only).</summary>
    public TimeOnly? ArrivalTime { get; set; }
    public ArrivalMethod? Method { get; set; }
    public string? ArrivalLocation { get; set; }

    /// <summary>Computed from ArrivalTime and the deadline in force when recorded; null when absent or no deadline was set.</summary>
    public LateStatus? LateStatus { get; set; }
    public TimeOnly? DeadlineUsed { get; set; }

    public int? ReceivedByStaffId { get; set; }
    public StaffMember? ReceivedBy { get; set; }
    public bool ReceivingConfirmed { get; set; }
    public string? Notes { get; set; }
    public string? RecordedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<StudentArrivalCorrection> Corrections { get; set; } = new List<StudentArrivalCorrection>();
}

/// <summary>Audit trail of corrections to an arrival record: what it said before, after, why and who.</summary>
public class StudentArrivalCorrection
{
    public const int TextMaxLength = 600;
    public const int ReasonMaxLength = 500;

    public int Id { get; set; }
    public int StudentArrivalRecordId { get; set; }
    public StudentArrivalRecord Record { get; set; } = null!;
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CorrectedAt { get; set; }
    public string? CorrectedBy { get; set; }
}
