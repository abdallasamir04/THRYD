namespace SchoolManagement.Web.Entities;

// ============================================================ Organizational structure (Phase 1)
//
// The school's official organizational structure: tiers → units → positions → assignments of staff members.
// It sits beside the existing staff records and never copies them: a person is always one StaffMember row, however
// many positions they hold. Ending an assignment keeps it as history; nothing here is ever cascade-deleted with a person.

/// <summary>The Management area a tier belongs to (School Leadership or Operation Team). Numbers are stored.</summary>
public enum OrgSection
{
    SchoolLeadership = 1,
    OperationTeam = 2,
}

/// <summary>The kind of responsibility a position carries. Numbers are stored: add new values, never reuse one.</summary>
public enum LeadershipRoleType
{
    Executive = 1,
    AcademicLeadership = 2,
    Hod = 3,
    Coordinator = 4,
    TeamLeader = 5,
    FloorSupervisor = 6,
    OperationsAdmin = 7,
    Specialist = 8,
    Support = 9,
    Operational = 10,
}

/// <summary>One of the six organizational tiers (Executive Advisory &amp; Governance … Academic Department Leadership).</summary>
public class OrgTier : IAuditable
{
    public const int NameMaxLength = 120;
    public const int DescriptionMaxLength = 500;
    public const int KeyMaxLength = 60;

    public int Id { get; set; }

    /// <summary>Stable source key of a seeded tier ("tier-1"); null for a tier added by the school.</summary>
    public string? SourceKey { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
    public OrgSection Section { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<OrgUnit> Units { get; set; } = new List<OrgUnit>();
}

/// <summary>
/// An organizational unit inside a tier (Executive Directorate, Floor Supervisors, LMS Support, Math Department…).
/// A unit linked to academic departments (<see cref="Departments"/>) is an academic department's leadership; the others
/// are operational or administrative units.
/// </summary>
public class OrgUnit : IAuditable
{
    public const int NameMaxLength = 150;
    public const int DescriptionMaxLength = 500;

    public int Id { get; set; }
    public string? SourceKey { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
    public int OrgTierId { get; set; }
    public OrgTier Tier { get; set; } = null!;
    public int? ParentUnitId { get; set; }
    public OrgUnit? ParentUnit { get; set; }
    public ICollection<OrgUnit> Children { get; set; } = new List<OrgUnit>();
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<OrgUnitDepartment> Departments { get; set; } = new List<OrgUnitDepartment>();
    public ICollection<StaffPosition> Positions { get; set; } = new List<StaffPosition>();
}

/// <summary>
/// Links a unit to an existing academic department (and optionally one of its subcategories): "English Department
/// (English - ESS - CB)" → English and ESS; "French Department" → Second Language / French. The departments themselves are
/// never copied or changed.
/// </summary>
public class OrgUnitDepartment
{
    public int OrgUnitId { get; set; }
    public OrgUnit Unit { get; set; } = null!;
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public int? DepartmentSubcategoryId { get; set; }
    public DepartmentSubcategory? Subcategory { get; set; }
}

/// <summary>A position in a unit (School Principal, Floor Supervisor, HOD — Grades 4–10…). May have several holders.</summary>
public class StaffPosition : IAuditable
{
    public const int TitleMaxLength = 150;
    public const int DescriptionMaxLength = 500;
    public const int GradeRangeMaxLength = 60;

    public int Id { get; set; }
    public string? SourceKey { get; set; }
    public string Title { get; set; } = "";
    public string NormalizedTitle { get; set; } = "";
    public string? Description { get; set; }
    public int OrgUnitId { get; set; }
    public OrgUnit Unit { get; set; } = null!;
    public LeadershipRoleType RoleType { get; set; }

    /// <summary>The grades the position covers ("Grades 2–10", "PreK to Grade 9"), when the source gives them.</summary>
    public string? GradeRange { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<StaffPositionAssignment> Assignments { get; set; } = new List<StaffPositionAssignment>();
}

/// <summary>
/// A staff member holding a position. One person may hold several positions at once (one of them primary). A change of
/// position ends the old row (IsCurrent = false, EndDate) and adds a new one; rows are never deleted.
/// </summary>
public class StaffPositionAssignment : IAuditable
{
    public const int NotesMaxLength = 500;
    public const int ScopeMaxLength = 100;
    public const int ReasonMaxLength = 200;

    public int Id { get; set; }
    public string? SourceKey { get; set; }
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public int StaffPositionId { get; set; }
    public StaffPosition Position { get; set; } = null!;

    /// <summary>A narrower grade range for this holder than the position's own (null = the position's).</summary>
    public string? GradeRange { get; set; }

    /// <summary>Where the holder works, when configured (e.g. a floor for a Floor Supervisor). Never invented by the import.</summary>
    public string? Scope { get; set; }

    /// <summary>Only when configured by an authorized administrator; the official source gives no reporting lines.</summary>
    public int? ReportsToStaffMemberId { get; set; }
    public StaffMember? ReportsTo { get; set; }

    public bool IsPrimary { get; set; }
    public bool IsCurrent { get; set; } = true;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? EndReason { get; set; }
    public string? Notes { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>What needs a person's attention on a staff record. Numbers are stored.</summary>
public enum DataQualityFlagKind
{
    /// <summary>The source's job title could not be read: no position is assigned until someone confirms it.</summary>
    PositionRequiresConfirmation = 1,

    /// <summary>The source gives the same phone number to two different people; the records are kept separate.</summary>
    SharedPhoneNumber = 2,

    /// <summary>The import could not decide whether a source person is an existing record.</summary>
    IdentityReview = 3,
}

/// <summary>A data-quality warning on a staff record, shown on the profile and the organizational pages until resolved.</summary>
public class StaffDataQualityFlag
{
    public const int MessageMaxLength = 500;

    public int Id { get; set; }
    public string? SourceKey { get; set; }
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public DataQualityFlagKind Kind { get; set; }
    public string Message { get; set; } = "";
    public bool IsResolved { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
