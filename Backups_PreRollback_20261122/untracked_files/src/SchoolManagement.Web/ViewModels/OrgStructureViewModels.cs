using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.ViewModels;

// ============================================================ Org Chart View Models

public sealed class OrgChartNodeViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string? RoleType { get; set; }
    public string? GradeRange { get; set; }
    public string? ContactNumber { get; set; }
    public string? Email { get; set; }
    public string? UnitName { get; set; }
    public string? TierName { get; set; }
    public int? StaffId { get; set; }
    public int? PositionId { get; set; }
    public int? UnitId { get; set; }
    public bool IsPrimary { get; set; } = true;
    public bool IsVacant => StaffId == null;
    public bool RequiresConfirmation { get; set; }
    public string Tone { get; set; } = "blue";
    public List<OrgChartNodeViewModel> Children { get; set; } = [];
}

public sealed class OrgChartViewModel
{
    public IReadOnlyList<OrgTierDto> Tiers { get; set; } = [];
    public IReadOnlyList<OrgChartNodeViewModel> RootNodes { get; set; } = [];
    public int TotalTiers { get; set; }
    public int TotalUnits { get; set; }
    public int TotalPositions { get; set; }
    public int TotalAssignments { get; set; }
    public int TotalStaffInLeadership { get; set; }
    public int PendingConfirmationCount { get; set; }
    public string? Search { get; set; }
    public int? SelectedTierId { get; set; }
}

// ============================================================ Tier & Unit DTOs

public sealed record OrgTierDto(int Id, int TierLevel, string Name, string? Description, int SortOrder, int UnitCount, int PositionCount, int StaffCount);

public sealed record OrgUnitDto(
    int Id,
    string Name,
    string? Code,
    int? TierId,
    string? TierName,
    int? ParentUnitId,
    string? ParentUnitName,
    int? DepartmentId,
    string? DepartmentName,
    OrgUnitType UnitType,
    string? Description,
    int SortOrder,
    bool IsActive,
    int PositionCount,
    int StaffCount,
    IReadOnlyList<PositionAssignmentDto> Leadership
);

// ============================================================ Position & Assignment DTOs

public sealed record StaffPositionDto(
    int Id,
    string Title,
    int? TierId,
    string? TierName,
    int? OrgUnitId,
    string? OrgUnitName,
    int? ReportsToPositionId,
    string? ReportsToPositionTitle,
    LeadershipRoleType RoleType,
    string? DefaultGradeRange,
    string? Description,
    int SortOrder,
    bool IsActive,
    IReadOnlyList<PositionAssignmentDto> CurrentHolders
);

public sealed record PositionAssignmentDto(
    int Id,
    int StaffMemberId,
    string StaffFullName,
    string? ContactNumber,
    int PositionId,
    string PositionTitle,
    int? OrgUnitId,
    string? OrgUnitName,
    int? TierId,
    string? TierName,
    string? GradeRange,
    LeadershipRoleType RoleType,
    bool IsPrimary,
    bool IsCurrent,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? ReportsToStaffMemberId,
    string? ReportsToStaffName,
    string? Notes,
    string? AssignedBy,
    DateTime CreatedAt
)
{
    public bool RequiresConfirmation => !string.IsNullOrEmpty(Notes) && Notes.Contains("requires confirmation", StringComparison.OrdinalIgnoreCase);
}

// ============================================================ Query and Index Models

public sealed class PositionQuery
{
    public string? Search { get; set; }
    public int? TierId { get; set; }
    public int? OrgUnitId { get; set; }
    public LeadershipRoleType? RoleType { get; set; }
    public string? Status { get; set; } // "active", "vacant", "all"

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || TierId.HasValue || OrgUnitId.HasValue || RoleType.HasValue || !string.IsNullOrWhiteSpace(Status);
}

public sealed class PositionIndexViewModel
{
    public PositionQuery Query { get; set; } = new();
    public IReadOnlyList<StaffPositionDto> Positions { get; set; } = [];
    public IReadOnlyList<OrgTierDto> Tiers { get; set; } = [];
    public IReadOnlyList<OrgUnitDto> Units { get; set; } = [];
    public int TotalPositions { get; set; }
    public int FilledPositions { get; set; }
    public int VacantPositions { get; set; }
}

public sealed class OrgUnitIndexViewModel
{
    public string? Search { get; set; }
    public int? TierId { get; set; }
    public OrgUnitType? UnitType { get; set; }
    public IReadOnlyList<OrgUnitDto> Units { get; set; } = [];
    public IReadOnlyList<OrgTierDto> Tiers { get; set; } = [];
    public int TotalUnits { get; set; }
}

public sealed class TierLeadershipViewModel
{
    public OrgTierDto Tier { get; set; } = null!;
    public IReadOnlyList<OrgUnitDto> Units { get; set; } = [];
    public IReadOnlyList<PositionAssignmentDto> Assignments { get; set; } = [];
    public IReadOnlyList<StaffPositionDto> Positions { get; set; } = [];
    public string? Search { get; set; }
}

public sealed class AcademicLeadershipViewModel
{
    public OrgTierDto? AcademicTier { get; set; }
    public IReadOnlyList<PositionAssignmentDto> TierLeadership { get; set; } = [];
    public IReadOnlyList<OrgUnitDto> AcademicDepartments { get; set; } = [];
    public IReadOnlyList<PositionAssignmentDto> DepartmentLeaders { get; set; } = [];
    public string? Search { get; set; }
}

public sealed class OperationsLeadershipViewModel
{
    public OrgTierDto? ParentsCareTier { get; set; }
    public OrgTierDto? FloorSupervisorsTier { get; set; }
    public OrgTierDto? OperationsTier { get; set; }
    public IReadOnlyList<PositionAssignmentDto> ParentsCareStaff { get; set; } = [];
    public IReadOnlyList<PositionAssignmentDto> FloorSupervisors { get; set; } = [];
    public IReadOnlyList<OrgUnitDto> OperationalUnits { get; set; } = [];
    public IReadOnlyList<PositionAssignmentDto> OperationalAssignments { get; set; } = [];
    public string? Search { get; set; }
}

// ============================================================ Form Models

public sealed class StaffPositionFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Position Title")]
    [Required(ErrorMessage = "Enter the position title.")]
    [StringLength(StaffPosition.TitleMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Title { get; set; } = "";

    [Display(Name = "Organizational Tier")]
    [Required(ErrorMessage = "Select an organizational tier.")]
    public int? TierId { get; set; }

    [Display(Name = "Department / Operational Unit")]
    public int? OrgUnitId { get; set; }

    [Display(Name = "Reports to Position")]
    public int? ReportsToPositionId { get; set; }

    [Display(Name = "Leadership Role Type")]
    public LeadershipRoleType RoleType { get; set; } = LeadershipRoleType.None;

    [Display(Name = "Default Grade Range")]
    [StringLength(StaffPosition.GradeRangeMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? DefaultGradeRange { get; set; }

    [Display(Name = "Description")]
    [StringLength(StaffPosition.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    public IReadOnlyList<OrgTierDto> Tiers { get; set; } = [];
    public IReadOnlyList<OrgUnitDto> Units { get; set; } = [];
    public IReadOnlyList<StaffPositionDto> Positions { get; set; } = [];
    public bool IsEdit => Id is > 0;
}

public sealed class AssignPositionAssignmentFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Position")]
    [Required(ErrorMessage = "Select a position.")]
    public int PositionId { get; set; }

    [Display(Name = "Staff Member")]
    [Required(ErrorMessage = "Select a staff member.")]
    public int StaffMemberId { get; set; }

    [Display(Name = "Department / Unit")]
    public int? OrgUnitId { get; set; }

    [Display(Name = "Grade Range")]
    [StringLength(StaffPositionAssignment.GradeRangeMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? GradeRange { get; set; }

    [Display(Name = "Leadership Role Type")]
    public LeadershipRoleType RoleType { get; set; } = LeadershipRoleType.None;

    [Display(Name = "Primary Position for Employee")]
    public bool IsPrimary { get; set; } = true;

    [Display(Name = "Start Date")]
    [Required(ErrorMessage = "Enter the start date.")]
    public DateOnly StartDate { get; set; } = Common.SchoolWeek.Today;

    [Display(Name = "End Date (Optional)")]
    public DateOnly? EndDate { get; set; }

    [Display(Name = "Reports to Staff Member")]
    public int? ReportsToStaffMemberId { get; set; }

    [Display(Name = "Notes")]
    [StringLength(StaffPositionAssignment.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    public string? PositionTitle { get; set; }
    public string? StaffName { get; set; }
    public IReadOnlyList<StaffPositionDto> Positions { get; set; } = [];
    public IReadOnlyList<OrgUnitDto> Units { get; set; } = [];
    public IReadOnlyList<StaffOptionDto> StaffMembers { get; set; } = [];
    public bool IsEdit => Id is > 0;
}

public sealed class OrgUnitFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Unit / Department Name")]
    [Required(ErrorMessage = "Enter the unit or department name.")]
    [StringLength(OrgUnit.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Name { get; set; } = "";

    [Display(Name = "Short Code")]
    [StringLength(OrgUnit.CodeMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Code { get; set; }

    [Display(Name = "Organizational Tier")]
    public int? TierId { get; set; }

    [Display(Name = "Parent Unit (Optional)")]
    public int? ParentUnitId { get; set; }

    [Display(Name = "Academic Department Link (Optional)")]
    public int? DepartmentId { get; set; }

    [Display(Name = "Unit Type")]
    public OrgUnitType UnitType { get; set; } = OrgUnitType.OperationalUnit;

    [Display(Name = "Description")]
    [StringLength(OrgUnit.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    public IReadOnlyList<OrgTierDto> Tiers { get; set; } = [];
    public IReadOnlyList<OrgUnitDto> ParentUnits { get; set; } = [];
    public IReadOnlyList<(int Id, string Name)> AcademicDepartments { get; set; } = [];
    public bool IsEdit => Id is > 0;
}

public sealed record StaffOptionDto(int Id, string FullName, string? JobTitle, string? ContactNumber);

// ============================================================ Staff Profile Enhancement

public sealed class StaffProfileOrgDetailsViewModel
{
    public int StaffId { get; set; }
    public string FullName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? ContactNumber { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public IReadOnlyList<PositionAssignmentDto> ActiveAssignments { get; set; } = [];
    public IReadOnlyList<PositionAssignmentDto> PastAssignments { get; set; } = [];
    public bool HasMultiplePositions => ActiveAssignments.Count > 1;
}
