namespace SchoolManagement.Web.Entities;

// ============================================================ KG class teaching teams
//
// KG Stage classes only (Pre-K, KG1, KG2 — see ClassStages.IsKgStage): one Homeroom Teacher and any number of
// Co-Teachers per class and academic year. These are class-level responsibilities, separate from subject teaching:
// nothing here touches timetable sessions, P.E. staff, workload periods or lesson attendance.

/// <summary>A staff member's role in a KG class teaching team. Numbers are stored: never reuse one.</summary>
public enum ClassTeachingRole
{
    HomeroomTeacher = 1,
    CoTeacher = 2,
}

/// <summary>
/// One staff member's role in one KG class for one academic year. Rows are never deleted: a removed or replaced
/// assignment is ended (IsActive = false, EndedAt set) and stays as history. Database indexes allow at most one
/// active Homeroom Teacher per class and year, and at most one active row per staff member in a class and year
/// (so nobody holds both roles, and no Co-Teacher is listed twice).
/// </summary>
public class ClassTeachingAssignment : IAuditable
{
    public const int EndReasonMaxLength = 200;

    public int Id { get; set; }
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public int AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;
    public ClassTeachingRole Role { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime AssignedAt { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndedBy { get; set; }
    public string? EndReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>What happened in a KG class teaching team. Numbers are stored: never reuse one.</summary>
public enum ClassTeachingOperation
{
    Assigned = 1,
    Removed = 2,

    /// <summary>The Homeroom Teacher was replaced by another staff member (logged for the outgoing teacher).</summary>
    Replaced = 3,

    /// <summary>The same staff member moved between Homeroom Teacher and Co-Teacher in the class and year.</summary>
    RoleChanged = 4,
}

/// <summary>Audit trail of teaching-team changes: who, which class, which staff member, roles before / after, year and when. Never edited.</summary>
public class ClassTeachingHistory
{
    public const int DetailsMaxLength = 300;

    public int Id { get; set; }
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;
    public int AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public ClassTeachingOperation Operation { get; set; }
    public ClassTeachingRole? PreviousRole { get; set; }
    public ClassTeachingRole? NewRole { get; set; }
    public string? Details { get; set; }
    public DateTime At { get; set; }
    public string? By { get; set; }
}
