namespace SchoolManagement.Web.Entities;

public enum OrgUnitType
{
    Governance = 1,
    AcademicLeadership = 2,
    AcademicDepartment = 3,
    SupportService = 4,
    Supervisory = 5,
    OperationalUnit = 6,
    AdministrativeDepartment = 7
}

public enum LeadershipRoleType
{
    None = 0,
    Executive = 1,
    AcademicLeadership = 2,
    HOD = 3,
    Coordinator = 4,
    TeamLeader = 5,
    FloorSupervisor = 6,
    OperationsAdmin = 7,
    Specialist = 8,
    Support = 9,
    Operational = 10
}

public class OrgTier : IAuditable
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    public int Id { get; set; }
    public int TierLevel { get; set; } // 1: Executive, 2: Academic Leadership, 3: Parents Care & Counseling, 4: Floor Supervisors, 5: Operational & Admin, 6: Academic Department Leadership
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<OrgUnit> Units { get; set; } = new List<OrgUnit>();
    public ICollection<StaffPosition> Positions { get; set; } = new List<StaffPosition>();
}

public class OrgUnit : IAuditable
{
    public const int NameMaxLength = 100;
    public const int CodeMaxLength = 30;
    public const int DescriptionMaxLength = 500;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Code { get; set; }

    public int? TierId { get; set; }
    public OrgTier? Tier { get; set; }

    public int? ParentUnitId { get; set; }
    public OrgUnit? ParentUnit { get; set; }
    public ICollection<OrgUnit> Children { get; set; } = new List<OrgUnit>();

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public OrgUnitType UnitType { get; set; } = OrgUnitType.OperationalUnit;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<StaffPosition> Positions { get; set; } = new List<StaffPosition>();
    public ICollection<StaffPositionAssignment> PositionAssignments { get; set; } = new List<StaffPositionAssignment>();
}

public class StaffPosition : IAuditable
{
    public const int TitleMaxLength = 120;
    public const int GradeRangeMaxLength = 50;
    public const int DescriptionMaxLength = 500;

    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string NormalizedTitle { get; set; } = "";

    public int? TierId { get; set; }
    public OrgTier? Tier { get; set; }

    public int? OrgUnitId { get; set; }
    public OrgUnit? OrgUnit { get; set; }

    public int? ReportsToPositionId { get; set; }
    public StaffPosition? ReportsToPosition { get; set; }
    public ICollection<StaffPosition> Subordinates { get; set; } = new List<StaffPosition>();

    public LeadershipRoleType RoleType { get; set; } = LeadershipRoleType.None;
    public string? DefaultGradeRange { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<StaffPositionAssignment> Assignments { get; set; } = new List<StaffPositionAssignment>();
}

public class StaffPositionAssignment : IAuditable
{
    public const int GradeRangeMaxLength = 50;
    public const int NotesMaxLength = 500;

    public int Id { get; set; }

    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;

    public int PositionId { get; set; }
    public StaffPosition Position { get; set; } = null!;

    public int? OrgUnitId { get; set; }
    public OrgUnit? OrgUnit { get; set; }

    public string? GradeRange { get; set; }
    public LeadershipRoleType RoleType { get; set; } = LeadershipRoleType.None;

    public bool IsPrimary { get; set; } = true;
    public bool IsCurrent { get; set; } = true;

    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public int? ReportsToStaffMemberId { get; set; }
    public StaffMember? ReportsToStaffMember { get; set; }

    public string? Notes { get; set; }
    public string? AssignedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
