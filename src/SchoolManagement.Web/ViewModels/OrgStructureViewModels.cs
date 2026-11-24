using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.OrgStructure;

namespace SchoolManagement.Web.ViewModels;

/// <summary>The Management sub-navigation entries (one shared partial on every Management page).</summary>
public static class ManagementNav
{
    public const string Leadership = "leadership";
    public const string Operation = "operation";
    public const string OrgChart = "org-chart";
    public const string Executive = "executive";
    public const string Academic = "academic";
    public const string Operations = "operations";
    public const string Departments = "departments";
    public const string Positions = "positions";
}

/// <summary>
/// One view of the organizational structure (Executive Management, Academic Leadership, Operations &amp; Administration):
/// the tiers it covers with their units, positions and holders. All of them read the same records.
/// </summary>
public sealed class OrgSectionViewModel
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Nav { get; init; }
    public required OrgStructureTree Structure { get; init; }
    public bool CanManage { get; init; }
    public string Icon { get; init; } = "layers";
}

public sealed class OrgChartViewModel
{
    public required OrgStructureTree Structure { get; init; }
    public bool CanManage { get; init; }
    public int? Tier { get; init; }
    public string? Search { get; init; }
}

/// <summary>Academic departments with their HODs, coordinators and team leaders (Tier 6), and the departments' own HOD records.</summary>
public sealed class DepartmentsLeadershipViewModel
{
    public required OrgStructureTree Structure { get; init; }
    public required IReadOnlyList<(int DepartmentId, string Department, string? HodRecord)> HodRecords { get; init; }
    public bool CanManage { get; init; }
}

public sealed class PositionsManagementViewModel
{
    public const string AssignmentsTab = "assignments";
    public const string PositionsTab = "positions";
    public const string UnitsTab = "units";
    public const string TiersTab = "tiers";
    public const string ReviewTab = "review";

    public required string Tab { get; init; }
    public required OrgStructureTree Structure { get; init; }
    public required IReadOnlyList<AssignmentRow> Assignments { get; init; }
    public bool CanManage { get; init; }
    public int? Tier { get; init; }
    public int? Unit { get; init; }
    public int? Position { get; init; }
    public string? Search { get; init; }

    /// <summary>Positions: "vacant" (no current holder), "filled", or null.</summary>
    public string? Status { get; init; }
    public bool IncludeEnded { get; init; }
    public OrgSeedReport? ImportReport { get; init; }
}

// ------------------------------------------------------------------ forms

public sealed class OrgTierFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Tier name"), Required(ErrorMessage = "Enter the tier name."), StringLength(OrgTier.NameMaxLength)]
    public string Name { get; set; } = "";

    [StringLength(OrgTier.DescriptionMaxLength)]
    public string? Description { get; set; }

    public OrgSection Section { get; set; } = OrgSection.SchoolLeadership;

    [Display(Name = "Order"), Range(0, 999, ErrorMessage = "Use an order between 0 and 999.")]
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsEdit => Id is not null;
    public OrgTierInput ToInput() => new() { Name = Name, Description = Description, Section = Section, SortOrder = SortOrder, IsActive = IsActive };
}

public sealed class OrgUnitFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Unit name"), Required(ErrorMessage = "Enter the unit name."), StringLength(OrgUnit.NameMaxLength)]
    public string Name { get; set; } = "";

    [StringLength(OrgUnit.DescriptionMaxLength)]
    public string? Description { get; set; }

    [Display(Name = "Tier"), Range(1, int.MaxValue, ErrorMessage = "Choose a tier.")]
    public int TierId { get; set; }
    public int? ParentUnitId { get; set; }

    [Display(Name = "Order"), Range(0, 999, ErrorMessage = "Use an order between 0 and 999.")]
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<int> DepartmentIds { get; set; } = [];

    public IReadOnlyList<OrgTierNode> Tiers { get; set; } = [];
    public IReadOnlyList<(int Id, string Name)> Departments { get; set; } = [];

    public bool IsEdit => Id is not null;
    public OrgUnitInput ToInput() => new()
    {
        Name = Name, Description = Description, TierId = TierId, ParentUnitId = ParentUnitId, SortOrder = SortOrder, IsActive = IsActive, DepartmentIds = DepartmentIds,
    };
}

public sealed class StaffPositionFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Position title"), Required(ErrorMessage = "Enter the position title."), StringLength(StaffPosition.TitleMaxLength)]
    public string Title { get; set; } = "";

    [StringLength(StaffPosition.DescriptionMaxLength)]
    public string? Description { get; set; }

    [Display(Name = "Unit"), Range(1, int.MaxValue, ErrorMessage = "Choose a unit.")]
    public int UnitId { get; set; }
    public LeadershipRoleType RoleType { get; set; } = LeadershipRoleType.Support;

    [Display(Name = "Grade range"), StringLength(StaffPosition.GradeRangeMaxLength)]
    public string? GradeRange { get; set; }

    [Display(Name = "Order"), Range(0, 999, ErrorMessage = "Use an order between 0 and 999.")]
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public bool HasHistory { get; set; }

    public IReadOnlyList<OrgTierNode> Tiers { get; set; } = [];

    public bool IsEdit => Id is not null;
    public StaffPositionInput ToInput() => new()
    {
        Title = Title, Description = Description, UnitId = UnitId, RoleType = RoleType, GradeRange = GradeRange, SortOrder = SortOrder, IsActive = IsActive,
    };
}

public sealed class AssignmentFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Staff member"), Range(1, int.MaxValue, ErrorMessage = "Choose a staff member.")]
    public int StaffMemberId { get; set; }

    [Display(Name = "Position"), Range(1, int.MaxValue, ErrorMessage = "Choose a position.")]
    public int PositionId { get; set; }

    [Display(Name = "Grade range"), StringLength(StaffPosition.GradeRangeMaxLength)]
    public string? GradeRange { get; set; }

    [Display(Name = "Scope / floor"), StringLength(StaffPositionAssignment.ScopeMaxLength)]
    public string? Scope { get; set; }

    public int? ReportsToStaffMemberId { get; set; }
    public bool IsPrimary { get; set; }

    [Display(Name = "Start date")]
    public DateOnly? StartDate { get; set; }

    [StringLength(StaffPositionAssignment.NotesMaxLength)]
    public string? Notes { get; set; }

    public IReadOnlyList<OrgStaffOption> Staff { get; set; } = [];
    public IReadOnlyList<OrgTierNode> Tiers { get; set; } = [];

    /// <summary>The assignment being edited (its person and current position), or null for a new one.</summary>
    public AssignmentRow? Current { get; set; }

    public bool IsEdit => Id is not null;
    public AssignmentInput ToInput() => new()
    {
        StaffMemberId = StaffMemberId, PositionId = PositionId, GradeRange = GradeRange, Scope = Scope, ReportsToStaffMemberId = ReportsToStaffMemberId,
        IsPrimary = IsPrimary, StartDate = StartDate, Notes = Notes,
    };
}

public sealed class EndAssignmentFormModel
{
    public int Id { get; set; }

    [Display(Name = "End date")]
    public DateOnly? EndDate { get; set; }

    [StringLength(StaffPositionAssignment.ReasonMaxLength)]
    public string? Reason { get; set; }

    public AssignmentRow? Current { get; set; }
}
