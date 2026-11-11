namespace SchoolManagement.Web.Entities;

// ============================================================ Duty management

/// <summary>
/// The Morning Duty components. Exactly three for now; the numbers are stored in the database,
/// so new components must get new numbers (never reuse one).
/// </summary>
public enum DutyComponent
{
    StudentsArrival = 1,
    InClassMorning = 2,
    MorningAssembly = 3,

    // Midday Duty
    MiddleHighBreak = 4,
    UpperElementaryBreak = 5,
    LowerElementaryBreak = 6,
    Kg2Break = 7,
    PreKKg1Break = 8,

    // Dismissal Duty
    DismissalBus = 9,
    DismissalPickup = 10,
}

public enum AttendanceStatus
{
    NotRecorded = 0,
    Present = 1,
    Absent = 2,
}

public enum BackupRequestStatus
{
    None = 0,
    Pending = 1,
    Accepted = 2,
    Rejected = 3,
}

/// <summary>One duty on one school day: who is assigned, who attended, and how an absence was covered.</summary>
public class DutyAssignment : IAuditable
{
    public const int DescriptionMaxLength = 300;
    public const int LocationMaxLength = 120;
    public const int NotesMaxLength = 2000;

    public int Id { get; set; }
    public DutyComponent Component { get; set; }
    public DateOnly DutyDate { get; set; }
    public string Description { get; set; } = "";
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public string? Location { get; set; }

    /// <summary>Any staff member (not only teachers).</summary>
    public int PrimaryStaffId { get; set; }
    public StaffMember PrimaryStaff { get; set; } = null!;
    public AttendanceStatus PrimaryAttendance { get; set; }

    public int? BackupStaffId { get; set; }
    public StaffMember? BackupStaff { get; set; }
    public BackupRequestStatus BackupStatus { get; set; }

    /// <summary>Recorded separately from acceptance: accepting is not attending.</summary>
    public AttendanceStatus BackupAttendance { get; set; }

    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // ---- Duty audit record (a duty row is the audited duty session) ----

    /// <summary>Morning today; other duty types (break, dismissal…) get new values later.</summary>
    public DutyType DutyType { get; set; } = DutyType.Morning;

    /// <summary>Operational status, set by staff. Separate from attendance and backup acceptance.</summary>
    public DutyStatus Status { get; set; } = DutyStatus.Pending;

    public string? AuditNotes { get; set; }
    public string? IssuesEncountered { get; set; }
    public bool FollowUpRequired { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    public ICollection<DutyAssignmentClass> Classes { get; set; } = new List<DutyAssignmentClass>();
    public ICollection<DutyBackupResponse> BackupResponses { get; set; } = new List<DutyBackupResponse>();
    public ICollection<DutyStaffAssignment> StaffAssignments { get; set; } = new List<DutyStaffAssignment>();
}

/// <summary>Classes covered by a duty (In-Class Morning).</summary>
public class DutyAssignmentClass
{
    public int DutyAssignmentId { get; set; }
    public DutyAssignment DutyAssignment { get; set; } = null!;
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;
}

public enum BackupResponseKind
{
    Requested = 1,
    Accepted = 2,
    Rejected = 3,
    Withdrawn = 4,
}

/// <summary>History of backup requests and the backup's answers for a duty.</summary>
public class DutyBackupResponse
{
    public const int NoteMaxLength = 500;

    public int Id { get; set; }
    public int DutyAssignmentId { get; set; }
    public DutyAssignment DutyAssignment { get; set; } = null!;
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public BackupResponseKind Response { get; set; }
    public string? Note { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? RecordedBy { get; set; }
}

// ============================================================ Teacher workload

/// <summary>The four parts of a teacher's 24-period week.</summary>
public enum WorkloadCategory
{
    Class = 1,
    Substitution = 2,
    Duty = 3,
    Office = 4,
}

/// <summary>Periods given to a teacher in one school week, in one category.</summary>
public class TeacherWorkloadAssignment : IAuditable
{
    public const int MaxWeeklyPeriods = 24;
    public const int NearCapacityFrom = 20;
    public const int DescriptionMaxLength = 200;

    public int Id { get; set; }
    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;

    /// <summary>First day of the school week (see SchoolWeek).</summary>
    public DateOnly WeekStart { get; set; }

    public WorkloadCategory Category { get; set; }
    public int Periods { get; set; }

    /// <summary>Required for Class periods, optional for Substitution.</summary>
    public int? SchoolClassId { get; set; }
    public SchoolClass? SchoolClass { get; set; }

    /// <summary>Optional link from Duty periods to a recorded duty.</summary>
    public int? DutyAssignmentId { get; set; }
    public DutyAssignment? DutyAssignment { get; set; }

    public string? Description { get; set; }

    /// <summary>Set (with <see cref="TimeSlotId"/>) when the period was placed in a timetable slot (Assign Period).</summary>
    public DayOfWeek? Day { get; set; }
    public int? TimeSlotId { get; set; }
    public TimeSlot? TimeSlot { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum WorkloadChange
{
    Created = 1,
    Updated = 2,
    Removed = 3,
}

/// <summary>Append-only log of workload changes (kept after an assignment is removed).</summary>
public class WorkloadAssignmentHistory
{
    public const int SummaryMaxLength = 400;

    public long Id { get; set; }
    public int AssignmentId { get; set; }
    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;
    public DateOnly WeekStart { get; set; }
    public WorkloadChange Change { get; set; }
    public WorkloadCategory Category { get; set; }
    public int? PeriodsBefore { get; set; }
    public int? PeriodsAfter { get; set; }
    public int WeekTotalAfter { get; set; }
    public string Summary { get; set; } = "";
    public DateTime ChangedAt { get; set; }

    /// <summary>The signed-in user's name; null while the app has no sign-in.</summary>
    public string? ChangedBy { get; set; }
}
