namespace SchoolManagement.Web.Entities;

// ============================================================ Behavioural / Discipline → Incident
//
// One official incident report: header (A), students involved (B), chronological timeline (C),
// investigation findings & administrative actions (D) and administrative sign-off & closure (E).
// Every important change is written to IncidentHistory. Numbers of enums are stored: add new
// values, never reuse one.

public enum IncidentSeverity
{
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4,
}

public enum IncidentStatus
{
    Reported = 1,
    UnderInvestigation = 2,
    UnderMonitoring = 3,
    Resolved = 4,
}

public enum IncidentRole
{
    ReportedConduct = 1,
    AffectedStudent = 2,
    InterveningStudent = 3,
    Witness = 4,
    Other = 5,
}

public enum IncidentActionStatus
{
    Pending = 1,
    InProgress = 2,
    Completed = 3,
    IssuedAndLogged = 4,
    Resolved = 5,
}

public enum IncidentApproval
{
    Pending = 1,
    Approved = 2,
    ApprovedWithConditions = 3,
    NotApproved = 4,
}

/// <summary>An official student incident report.</summary>
public class Incident : IAuditable
{
    public const int ReferenceMaxLength = 20;
    public const int CategoryMaxLength = 150;
    public const int DepartmentMaxLength = 150;
    public const int NameMaxLength = 150;
    public const int NotesMaxLength = 2000;

    public int Id { get; set; }

    /// <summary>Unique, generated: INC-{year of the incident}-{sequence}, e.g. INC-2026-0007.</summary>
    public string ReferenceId { get; set; } = "";

    // ---- A. header
    public string Category { get; set; } = "";
    public DateOnly IncidentDate { get; set; }
    public string ReportingDepartment { get; set; } = "";
    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Level1;
    public IncidentStatus Status { get; set; } = IncidentStatus.Reported;

    // ---- E. administrative sign-off & record closure
    public int? PreparedByStaffId { get; set; }
    public StaffMember? PreparedBy { get; set; }

    /// <summary>The school as written on the report (defaults to the school name in Settings).</summary>
    public string? SignOffSchool { get; set; }
    public DateOnly? ReportDate { get; set; }
    public int? ReviewedByStaffId { get; set; }
    public StaffMember? ReviewedBy { get; set; }
    public IncidentApproval Approval { get; set; } = IncidentApproval.Pending;
    public string? ApprovalNotes { get; set; }

    /// <summary>Record closure: an open report can still change; a closed one is read-only until reopened.</summary>
    public bool IsClosed { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }

    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<IncidentStudent> Students { get; set; } = new List<IncidentStudent>();
    public ICollection<IncidentTimelineEntry> Timeline { get; set; } = new List<IncidentTimelineEntry>();
    public ICollection<IncidentAction> Actions { get; set; } = new List<IncidentAction>();
    public ICollection<IncidentHistory> History { get; set; } = new List<IncidentHistory>();
}

/// <summary>B. A student involved in the incident (one row per student), with their own account of involvement.</summary>
public class IncidentStudent
{
    public const int InvolvementMaxLength = 4000;
    public const int RoleOtherMaxLength = 100;

    public int Id { get; set; }
    public int IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;

    /// <summary>The student's class when added (grade &amp; section on the report stay as they were).</summary>
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    public IncidentRole Role { get; set; }

    /// <summary>Wording of the role when <see cref="IncidentRole.Other"/>.</summary>
    public string? RoleOther { get; set; }
    public string Involvement { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>C. One event of the chronological timeline.</summary>
public class IncidentTimelineEntry
{
    public const int PeriodMaxLength = 150;
    public const int LocationMaxLength = 150;
    public const int OthersMaxLength = 300;
    public const int AccountMaxLength = 4000;

    public int Id { get; set; }
    public int IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;

    /// <summary>Position in the timeline (1 = first). Kept chronological: new entries with a time go to their place.</summary>
    public int SortOrder { get; set; }

    /// <summary>"Morning Line", "Break Time", "Escalation"…</summary>
    public string TimePeriod { get; set; } = "";

    /// <summary>Optional clock time, used to place the entry in order.</summary>
    public TimeOnly? Time { get; set; }
    public string Location { get; set; } = "";

    /// <summary>People present who are not among the students involved (other students, staff).</summary>
    public string? OthersPresent { get; set; }
    public string Account { get; set; } = "";

    public ICollection<IncidentTimelineStudent> Present { get; set; } = new List<IncidentTimelineStudent>();
}

/// <summary>An involved student present at a timeline event.</summary>
public class IncidentTimelineStudent
{
    public int IncidentTimelineEntryId { get; set; }
    public IncidentTimelineEntry Entry { get; set; } = null!;
    public int IncidentStudentId { get; set; }
    public IncidentStudent Student { get; set; } = null!;
}

/// <summary>D. An investigation finding or administrative action, tracked on its own.</summary>
public class IncidentAction
{
    public const int ItemMaxLength = 200;
    public const int TextMaxLength = 4000;

    public int Id { get; set; }
    public int IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    public int SortOrder { get; set; }
    public string ActionItem { get; set; } = "";
    public string Finding { get; set; } = "";
    public string? Details { get; set; }
    public IncidentActionStatus Status { get; set; } = IncidentActionStatus.Pending;
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Audit history of an incident report (never edited).</summary>
public class IncidentHistory
{
    public const int ActionMaxLength = 100;
    public const int DetailsMaxLength = 2000;

    public long Id { get; set; }
    public int IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public DateTime At { get; set; }
    public string? By { get; set; }
}
