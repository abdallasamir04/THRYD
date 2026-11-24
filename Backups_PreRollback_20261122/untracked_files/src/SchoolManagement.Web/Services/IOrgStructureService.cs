using SchoolManagement.Web.Common;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Services;

public interface IOrgStructureService
{
    Task<IReadOnlyList<OrgTierDto>> GetTiersAsync(CancellationToken ct = default);
    Task<OrgTierDto?> GetTierAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<OrgUnitDto>> GetUnitsAsync(int? tierId = null, CancellationToken ct = default);
    Task<OrgUnitDto?> GetUnitAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> CreateUnitAsync(OrgUnitFormModel model, CancellationToken ct = default);
    Task<ServiceResult> UpdateUnitAsync(int id, OrgUnitFormModel model, CancellationToken ct = default);
    Task<ServiceResult> DeleteUnitAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<StaffPositionDto>> GetPositionsAsync(PositionQuery query, CancellationToken ct = default);
    Task<StaffPositionDto?> GetPositionAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> CreatePositionAsync(StaffPositionFormModel model, CancellationToken ct = default);
    Task<ServiceResult> UpdatePositionAsync(int id, StaffPositionFormModel model, CancellationToken ct = default);
    Task<ServiceResult> DeletePositionAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<PositionAssignmentDto>> GetAssignmentsAsync(int? staffId = null, int? positionId = null, int? unitId = null, bool activeOnly = true, CancellationToken ct = default);
    Task<PositionAssignmentDto?> GetAssignmentAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> AssignStaffAsync(AssignPositionAssignmentFormModel model, CancellationToken ct = default);
    Task<ServiceResult> UpdateAssignmentAsync(int id, AssignPositionAssignmentFormModel model, CancellationToken ct = default);
    Task<ServiceResult> EndAssignmentAsync(int id, DateOnly? endDate, string? reason, CancellationToken ct = default);

    Task<OrgChartViewModel> GetOrgChartAsync(string? search = null, int? tierId = null, CancellationToken ct = default);
    Task<TierLeadershipViewModel> GetTierLeadershipAsync(int tierLevel, string? search = null, CancellationToken ct = default);
    Task<AcademicLeadershipViewModel> GetAcademicLeadershipAsync(string? search = null, CancellationToken ct = default);
    Task<OperationsLeadershipViewModel> GetOperationsLeadershipAsync(string? search = null, CancellationToken ct = default);
    Task<StaffProfileOrgDetailsViewModel?> GetStaffOrgProfileAsync(int staffId, CancellationToken ct = default);
    Task<IReadOnlyList<StaffOptionDto>> GetStaffOptionsAsync(CancellationToken ct = default);

    /// <summary>Idempotent seed of Royal Kings Phase 1 structure (tiers, units, positions, staff, assignments).</summary>
    Task<ServiceResult> SeedPhase1RoyalKingsStructureAsync(CancellationToken ct = default);
}
