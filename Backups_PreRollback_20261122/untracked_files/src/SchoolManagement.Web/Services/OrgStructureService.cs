using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Services;

public partial class OrgStructureService(SchoolDbContext db, ICurrentUser currentUser) : IOrgStructureService
{
    // ============================================================ Tiers

    public async Task<IReadOnlyList<OrgTierDto>> GetTiersAsync(CancellationToken ct = default)
    {
        var tiers = await db.OrgTiers.AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.TierLevel)
            .Select(t => new OrgTierDto(
                t.Id,
                t.TierLevel,
                t.Name,
                t.Description,
                t.SortOrder,
                t.Units.Count(u => u.IsActive),
                t.Positions.Count(p => p.IsActive),
                t.Positions.SelectMany(p => p.Assignments).Count(a => a.IsCurrent)
            ))
            .ToListAsync(ct);

        return tiers;
    }

    public async Task<OrgTierDto?> GetTierAsync(int id, CancellationToken ct = default)
    {
        return await db.OrgTiers.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new OrgTierDto(
                t.Id,
                t.TierLevel,
                t.Name,
                t.Description,
                t.SortOrder,
                t.Units.Count(u => u.IsActive),
                t.Positions.Count(p => p.IsActive),
                t.Positions.SelectMany(p => p.Assignments).Count(a => a.IsCurrent)
            ))
            .FirstOrDefaultAsync(ct);
    }

    // ============================================================ Units

    public async Task<IReadOnlyList<OrgUnitDto>> GetUnitsAsync(int? tierId = null, CancellationToken ct = default)
    {
        var query = db.OrgUnits.AsNoTracking();
        if (tierId.HasValue) query = query.Where(u => u.TierId == tierId.Value);

        var units = await query
            .OrderBy(u => u.SortOrder).ThenBy(u => u.Name)
            .Select(u => new OrgUnitDto(
                u.Id,
                u.Name,
                u.Code,
                u.TierId,
                u.Tier != null ? u.Tier.Name : null,
                u.ParentUnitId,
                u.ParentUnit != null ? u.ParentUnit.Name : null,
                u.DepartmentId,
                u.Department != null ? u.Department.Name : null,
                u.UnitType,
                u.Description,
                u.SortOrder,
                u.IsActive,
                u.Positions.Count(p => p.IsActive),
                u.PositionAssignments.Count(a => a.IsCurrent),
                u.PositionAssignments.Where(a => a.IsCurrent).Select(a => new PositionAssignmentDto(
                    a.Id,
                    a.StaffMemberId,
                    a.StaffMember.FullName,
                    a.StaffMember.ContactNumber,
                    a.PositionId,
                    a.Position.Title,
                    a.OrgUnitId,
                    u.Name,
                    u.TierId,
                    u.Tier != null ? u.Tier.Name : null,
                    a.GradeRange ?? a.Position.DefaultGradeRange,
                    a.RoleType != LeadershipRoleType.None ? a.RoleType : a.Position.RoleType,
                    a.IsPrimary,
                    a.IsCurrent,
                    a.StartDate,
                    a.EndDate,
                    a.ReportsToStaffMemberId,
                    a.ReportsToStaffMember != null ? a.ReportsToStaffMember.FullName : null,
                    a.Notes,
                    a.AssignedBy,
                    a.CreatedAt
                )).ToList()
            ))
            .ToListAsync(ct);

        return units;
    }

    public async Task<OrgUnitDto?> GetUnitAsync(int id, CancellationToken ct = default)
    {
        return await db.OrgUnits.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new OrgUnitDto(
                u.Id,
                u.Name,
                u.Code,
                u.TierId,
                u.Tier != null ? u.Tier.Name : null,
                u.ParentUnitId,
                u.ParentUnit != null ? u.ParentUnit.Name : null,
                u.DepartmentId,
                u.Department != null ? u.Department.Name : null,
                u.UnitType,
                u.Description,
                u.SortOrder,
                u.IsActive,
                u.Positions.Count(p => p.IsActive),
                u.PositionAssignments.Count(a => a.IsCurrent),
                u.PositionAssignments.Where(a => a.IsCurrent).Select(a => new PositionAssignmentDto(
                    a.Id,
                    a.StaffMemberId,
                    a.StaffMember.FullName,
                    a.StaffMember.ContactNumber,
                    a.PositionId,
                    a.Position.Title,
                    a.OrgUnitId,
                    u.Name,
                    u.TierId,
                    u.Tier != null ? u.Tier.Name : null,
                    a.GradeRange ?? a.Position.DefaultGradeRange,
                    a.RoleType != LeadershipRoleType.None ? a.RoleType : a.Position.RoleType,
                    a.IsPrimary,
                    a.IsCurrent,
                    a.StartDate,
                    a.EndDate,
                    a.ReportsToStaffMemberId,
                    a.ReportsToStaffMember != null ? a.ReportsToStaffMember.FullName : null,
                    a.Notes,
                    a.AssignedBy,
                    a.CreatedAt
                )).ToList()
            ))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ServiceResult> CreateUnitAsync(OrgUnitFormModel model, CancellationToken ct = default)
    {
        var name = TextNormalizer.CleanText(model.Name);
        if (string.IsNullOrWhiteSpace(name)) return ServiceResult.Fail("Name", "Enter a unit name.");
        var key = TextNormalizer.NameKey(name);

        if (await db.OrgUnits.AnyAsync(u => u.NormalizedName == key && u.TierId == model.TierId, ct))
            return ServiceResult.Fail("Name", $"A unit named \"{name}\" already exists in this tier.");

        var unit = new OrgUnit
        {
            Name = name,
            NormalizedName = key,
            Code = model.Code?.Trim(),
            TierId = model.TierId,
            ParentUnitId = model.ParentUnitId,
            DepartmentId = model.DepartmentId,
            UnitType = model.UnitType,
            Description = model.Description?.Trim(),
            SortOrder = model.SortOrder,
            IsActive = model.IsActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.OrgUnits.Add(unit);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(unit.Id);
    }

    public async Task<ServiceResult> UpdateUnitAsync(int id, OrgUnitFormModel model, CancellationToken ct = default)
    {
        var unit = await db.OrgUnits.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (unit == null) return ServiceResult.Fail("", "Unit not found.");

        var name = TextNormalizer.CleanText(model.Name);
        if (string.IsNullOrWhiteSpace(name)) return ServiceResult.Fail("Name", "Enter a unit name.");
        var key = TextNormalizer.NameKey(name);

        if (await db.OrgUnits.AnyAsync(u => u.Id != id && u.NormalizedName == key && u.TierId == model.TierId, ct))
            return ServiceResult.Fail("Name", $"Another unit named \"{name}\" already exists in this tier.");

        unit.Name = name;
        unit.NormalizedName = key;
        unit.Code = model.Code?.Trim();
        unit.TierId = model.TierId;
        unit.ParentUnitId = model.ParentUnitId != id ? model.ParentUnitId : null;
        unit.DepartmentId = model.DepartmentId;
        unit.UnitType = model.UnitType;
        unit.Description = model.Description?.Trim();
        unit.SortOrder = model.SortOrder;
        unit.IsActive = model.IsActive;
        unit.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(unit.Id);
    }

    public async Task<ServiceResult> DeleteUnitAsync(int id, CancellationToken ct = default)
    {
        var unit = await db.OrgUnits.Include(u => u.PositionAssignments).Include(u => u.Positions).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (unit == null) return ServiceResult.Fail("", "Unit not found.");

        if (unit.PositionAssignments.Any(a => a.IsCurrent))
            return ServiceResult.Fail("", "This unit has active staff assignments. Reassign or remove them first.");

        if (await db.OrgUnits.AnyAsync(u => u.ParentUnitId == id, ct))
            return ServiceResult.Fail("", "This unit has child units. Reassign them first.");

        db.OrgUnits.Remove(unit);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ============================================================ Positions

    public async Task<IReadOnlyList<StaffPositionDto>> GetPositionsAsync(PositionQuery query, CancellationToken ct = default)
    {
        var q = db.StaffPositions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var key = TextNormalizer.NameKey(query.Search);
            q = q.Where(p => p.NormalizedTitle.Contains(key));
        }

        if (query.TierId.HasValue) q = q.Where(p => p.TierId == query.TierId.Value);
        if (query.OrgUnitId.HasValue) q = q.Where(p => p.OrgUnitId == query.OrgUnitId.Value);
        if (query.RoleType.HasValue) q = q.Where(p => p.RoleType == query.RoleType.Value);
        if (query.Status == "active") q = q.Where(p => p.IsActive);
        else if (query.Status == "vacant") q = q.Where(p => !p.Assignments.Any(a => a.IsCurrent));

        var positions = await q
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .Select(p => new StaffPositionDto(
                p.Id,
                p.Title,
                p.TierId,
                p.Tier != null ? p.Tier.Name : null,
                p.OrgUnitId,
                p.OrgUnit != null ? p.OrgUnit.Name : null,
                p.ReportsToPositionId,
                p.ReportsToPosition != null ? p.ReportsToPosition.Title : null,
                p.RoleType,
                p.DefaultGradeRange,
                p.Description,
                p.SortOrder,
                p.IsActive,
                p.Assignments.Where(a => a.IsCurrent).Select(a => new PositionAssignmentDto(
                    a.Id,
                    a.StaffMemberId,
                    a.StaffMember.FullName,
                    a.StaffMember.ContactNumber,
                    a.PositionId,
                    p.Title,
                    a.OrgUnitId ?? p.OrgUnitId,
                    a.OrgUnit != null ? a.OrgUnit.Name : (p.OrgUnit != null ? p.OrgUnit.Name : null),
                    p.TierId,
                    p.Tier != null ? p.Tier.Name : null,
                    a.GradeRange ?? p.DefaultGradeRange,
                    a.RoleType != LeadershipRoleType.None ? a.RoleType : p.RoleType,
                    a.IsPrimary,
                    a.IsCurrent,
                    a.StartDate,
                    a.EndDate,
                    a.ReportsToStaffMemberId,
                    a.ReportsToStaffMember != null ? a.ReportsToStaffMember.FullName : null,
                    a.Notes,
                    a.AssignedBy,
                    a.CreatedAt
                )).ToList()
            ))
            .ToListAsync(ct);

        return positions;
    }

    public async Task<StaffPositionDto?> GetPositionAsync(int id, CancellationToken ct = default)
    {
        return await db.StaffPositions.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new StaffPositionDto(
                p.Id,
                p.Title,
                p.TierId,
                p.Tier != null ? p.Tier.Name : null,
                p.OrgUnitId,
                p.OrgUnit != null ? p.OrgUnit.Name : null,
                p.ReportsToPositionId,
                p.ReportsToPosition != null ? p.ReportsToPosition.Title : null,
                p.RoleType,
                p.DefaultGradeRange,
                p.Description,
                p.SortOrder,
                p.IsActive,
                p.Assignments.Where(a => a.IsCurrent).Select(a => new PositionAssignmentDto(
                    a.Id,
                    a.StaffMemberId,
                    a.StaffMember.FullName,
                    a.StaffMember.ContactNumber,
                    a.PositionId,
                    p.Title,
                    a.OrgUnitId ?? p.OrgUnitId,
                    a.OrgUnit != null ? a.OrgUnit.Name : (p.OrgUnit != null ? p.OrgUnit.Name : null),
                    p.TierId,
                    p.Tier != null ? p.Tier.Name : null,
                    a.GradeRange ?? p.DefaultGradeRange,
                    a.RoleType != LeadershipRoleType.None ? a.RoleType : p.RoleType,
                    a.IsPrimary,
                    a.IsCurrent,
                    a.StartDate,
                    a.EndDate,
                    a.ReportsToStaffMemberId,
                    a.ReportsToStaffMember != null ? a.ReportsToStaffMember.FullName : null,
                    a.Notes,
                    a.AssignedBy,
                    a.CreatedAt
                )).ToList()
            ))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ServiceResult> CreatePositionAsync(StaffPositionFormModel model, CancellationToken ct = default)
    {
        var title = TextNormalizer.CleanText(model.Title);
        if (string.IsNullOrWhiteSpace(title)) return ServiceResult.Fail("Title", "Enter a position title.");
        var key = TextNormalizer.NameKey(title);

        var position = new StaffPosition
        {
            Title = title,
            NormalizedTitle = key,
            TierId = model.TierId,
            OrgUnitId = model.OrgUnitId,
            ReportsToPositionId = model.ReportsToPositionId,
            RoleType = model.RoleType,
            DefaultGradeRange = model.DefaultGradeRange?.Trim(),
            Description = model.Description?.Trim(),
            SortOrder = model.SortOrder,
            IsActive = model.IsActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.StaffPositions.Add(position);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(position.Id);
    }

    public async Task<ServiceResult> UpdatePositionAsync(int id, StaffPositionFormModel model, CancellationToken ct = default)
    {
        var position = await db.StaffPositions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position == null) return ServiceResult.Fail("", "Position not found.");

        var title = TextNormalizer.CleanText(model.Title);
        if (string.IsNullOrWhiteSpace(title)) return ServiceResult.Fail("Title", "Enter a position title.");

        position.Title = title;
        position.NormalizedTitle = TextNormalizer.NameKey(title);
        position.TierId = model.TierId;
        position.OrgUnitId = model.OrgUnitId;
        position.ReportsToPositionId = model.ReportsToPositionId != id ? model.ReportsToPositionId : null;
        position.RoleType = model.RoleType;
        position.DefaultGradeRange = model.DefaultGradeRange?.Trim();
        position.Description = model.Description?.Trim();
        position.SortOrder = model.SortOrder;
        position.IsActive = model.IsActive;
        position.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(position.Id);
    }

    public async Task<ServiceResult> DeletePositionAsync(int id, CancellationToken ct = default)
    {
        var position = await db.StaffPositions.Include(p => p.Assignments).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position == null) return ServiceResult.Fail("", "Position not found.");

        if (position.Assignments.Any(a => a.IsCurrent))
            return ServiceResult.Fail("", "This position has active holders. Remove or end assignments first.");

        db.StaffPositions.Remove(position);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ============================================================ Assignments

    public async Task<IReadOnlyList<PositionAssignmentDto>> GetAssignmentsAsync(int? staffId = null, int? positionId = null, int? unitId = null, bool activeOnly = true, CancellationToken ct = default)
    {
        var q = db.StaffPositionAssignments.AsNoTracking();
        if (staffId.HasValue) q = q.Where(a => a.StaffMemberId == staffId.Value);
        if (positionId.HasValue) q = q.Where(a => a.PositionId == positionId.Value);
        if (unitId.HasValue) q = q.Where(a => a.OrgUnitId == unitId.Value || a.Position.OrgUnitId == unitId.Value);
        if (activeOnly) q = q.Where(a => a.IsCurrent);

        var list = await q
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Position.SortOrder)
            .Select(a => new PositionAssignmentDto(
                a.Id,
                a.StaffMemberId,
                a.StaffMember.FullName,
                a.StaffMember.ContactNumber,
                a.PositionId,
                a.Position.Title,
                a.OrgUnitId ?? a.Position.OrgUnitId,
                a.OrgUnit != null ? a.OrgUnit.Name : (a.Position.OrgUnit != null ? a.Position.OrgUnit.Name : null),
                a.Position.TierId,
                a.Position.Tier != null ? a.Position.Tier.Name : null,
                a.GradeRange ?? a.Position.DefaultGradeRange,
                a.RoleType != LeadershipRoleType.None ? a.RoleType : a.Position.RoleType,
                a.IsPrimary,
                a.IsCurrent,
                a.StartDate,
                a.EndDate,
                a.ReportsToStaffMemberId,
                a.ReportsToStaffMember != null ? a.ReportsToStaffMember.FullName : null,
                a.Notes,
                a.AssignedBy,
                a.CreatedAt
            ))
            .ToListAsync(ct);

        return list;
    }

    public async Task<PositionAssignmentDto?> GetAssignmentAsync(int id, CancellationToken ct = default)
    {
        return await db.StaffPositionAssignments.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new PositionAssignmentDto(
                a.Id,
                a.StaffMemberId,
                a.StaffMember.FullName,
                a.StaffMember.ContactNumber,
                a.PositionId,
                a.Position.Title,
                a.OrgUnitId ?? a.Position.OrgUnitId,
                a.OrgUnit != null ? a.OrgUnit.Name : (a.Position.OrgUnit != null ? a.Position.OrgUnit.Name : null),
                a.Position.TierId,
                a.Position.Tier != null ? a.Position.Tier.Name : null,
                a.GradeRange ?? a.Position.DefaultGradeRange,
                a.RoleType != LeadershipRoleType.None ? a.RoleType : a.Position.RoleType,
                a.IsPrimary,
                a.IsCurrent,
                a.StartDate,
                a.EndDate,
                a.ReportsToStaffMemberId,
                a.ReportsToStaffMember != null ? a.ReportsToStaffMember.FullName : null,
                a.Notes,
                a.AssignedBy,
                a.CreatedAt
            ))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ServiceResult> AssignStaffAsync(AssignPositionAssignmentFormModel model, CancellationToken ct = default)
    {
        var staff = await db.StaffMembers.FirstOrDefaultAsync(s => s.Id == model.StaffMemberId, ct);
        if (staff == null) return ServiceResult.Fail("StaffMemberId", "Staff member not found.");

        var position = await db.StaffPositions.FirstOrDefaultAsync(p => p.Id == model.PositionId, ct);
        if (position == null) return ServiceResult.Fail("PositionId", "Position not found.");

        // Check if exact same active assignment already exists
        var existing = await db.StaffPositionAssignments.FirstOrDefaultAsync(a =>
            a.StaffMemberId == model.StaffMemberId &&
            a.PositionId == model.PositionId &&
            a.OrgUnitId == (model.OrgUnitId ?? position.OrgUnitId) &&
            a.GradeRange == model.GradeRange &&
            a.IsCurrent, ct);

        if (existing != null)
            return ServiceResult.Fail("", $"{staff.FullName} is already actively assigned to {position.Title} with this scope.");

        if (model.IsPrimary)
        {
            // Clear primary on other active assignments for this staff member
            var currentPrimaries = await db.StaffPositionAssignments.Where(a => a.StaffMemberId == model.StaffMemberId && a.IsPrimary && a.IsCurrent).ToListAsync(ct);
            foreach (var p in currentPrimaries) p.IsPrimary = false;
        }

        var assignment = new StaffPositionAssignment
        {
            StaffMemberId = model.StaffMemberId,
            PositionId = model.PositionId,
            OrgUnitId = model.OrgUnitId ?? position.OrgUnitId,
            GradeRange = model.GradeRange?.Trim() ?? position.DefaultGradeRange,
            RoleType = model.RoleType != LeadershipRoleType.None ? model.RoleType : position.RoleType,
            IsPrimary = model.IsPrimary,
            IsCurrent = true,
            StartDate = model.StartDate,
            EndDate = model.EndDate,
            ReportsToStaffMemberId = model.ReportsToStaffMemberId != model.StaffMemberId ? model.ReportsToStaffMemberId : null,
            Notes = model.Notes?.Trim(),
            AssignedBy = currentUser?.Name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.StaffPositionAssignments.Add(assignment);

        // Update JobTitle on staff member if it's empty or if primary
        if (model.IsPrimary || string.IsNullOrWhiteSpace(staff.JobTitle))
        {
            staff.JobTitle = position.Title;
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(assignment.Id);
    }

    public async Task<ServiceResult> UpdateAssignmentAsync(int id, AssignPositionAssignmentFormModel model, CancellationToken ct = default)
    {
        var assignment = await db.StaffPositionAssignments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (assignment == null) return ServiceResult.Fail("", "Assignment not found.");

        assignment.PositionId = model.PositionId;
        assignment.OrgUnitId = model.OrgUnitId;
        assignment.GradeRange = model.GradeRange?.Trim();
        assignment.RoleType = model.RoleType;
        assignment.StartDate = model.StartDate;
        assignment.EndDate = model.EndDate;
        assignment.ReportsToStaffMemberId = model.ReportsToStaffMemberId != assignment.StaffMemberId ? model.ReportsToStaffMemberId : null;
        assignment.Notes = model.Notes?.Trim();
        assignment.UpdatedAt = DateTime.UtcNow;

        if (model.IsPrimary && !assignment.IsPrimary)
        {
            var otherPrimaries = await db.StaffPositionAssignments.Where(a => a.StaffMemberId == assignment.StaffMemberId && a.Id != id && a.IsPrimary).ToListAsync(ct);
            foreach (var p in otherPrimaries) p.IsPrimary = false;
            assignment.IsPrimary = true;
        }
        else
        {
            assignment.IsPrimary = model.IsPrimary;
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(assignment.Id);
    }

    public async Task<ServiceResult> EndAssignmentAsync(int id, DateOnly? endDate, string? reason, CancellationToken ct = default)
    {
        var assignment = await db.StaffPositionAssignments.Include(a => a.StaffMember).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (assignment == null) return ServiceResult.Fail("", "Assignment not found.");

        assignment.IsCurrent = false;
        assignment.EndDate = endDate ?? SchoolWeek.Today;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            assignment.Notes = string.IsNullOrWhiteSpace(assignment.Notes)
                ? $"Ended: {reason.Trim()}"
                : $"{assignment.Notes} | Ended: {reason.Trim()}";
        }
        assignment.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(assignment.Id);
    }

    // ============================================================ Views & Aggregations

    public async Task<OrgChartViewModel> GetOrgChartAsync(string? search = null, int? tierId = null, CancellationToken ct = default)
    {
        var tiers = await GetTiersAsync(ct);
        var units = await GetUnitsAsync(tierId, ct);
        var positions = await GetPositionsAsync(new PositionQuery { TierId = tierId, Search = search }, ct);

        // Build tree:
        // Level 1: Executive Advisory & Governance
        //   Level 2: Academic Leadership
        //     Level 6: Academic Departments & Leaders
        //   Level 3: Parents Care, PR & Counseling
        //   Level 4: Floor Supervisors
        //   Level 5: Administrative & Operational Units

        var rootNodes = new List<OrgChartNodeViewModel>();

        // Executive Tier Root
        var execTier = tiers.FirstOrDefault(t => t.TierLevel == 1);
        var execPositions = positions.Where(p => p.TierId == execTier?.Id).ToList();

        var execRoot = new OrgChartNodeViewModel
        {
            Id = "tier-1",
            Name = execTier?.Name ?? "Executive Advisory & Governance",
            Title = "Executive Tier",
            RoleType = "Executive",
            TierName = "Executive Tier",
            Tone = "violet"
        };

        foreach (var pos in execPositions)
        {
            foreach (var h in pos.CurrentHolders)
            {
                execRoot.Children.Add(new OrgChartNodeViewModel
                {
                    Id = $"pos-assign-{h.Id}",
                    Name = h.StaffFullName,
                    Title = h.PositionTitle,
                    RoleType = "Executive",
                    ContactNumber = h.ContactNumber,
                    StaffId = h.StaffMemberId,
                    PositionId = h.PositionId,
                    UnitName = h.OrgUnitName,
                    TierName = execTier?.Name,
                    IsPrimary = h.IsPrimary,
                    Tone = "violet",
                    RequiresConfirmation = h.RequiresConfirmation
                });
            }
        }
        rootNodes.Add(execRoot);

        // Academic Leadership Branch
        var acadTier = tiers.FirstOrDefault(t => t.TierLevel == 2);
        var acadPositions = positions.Where(p => p.TierId == acadTier?.Id).ToList();
        var acadRoot = new OrgChartNodeViewModel
        {
            Id = "tier-2",
            Name = acadTier?.Name ?? "Academic Leadership",
            Title = "Academic Tier",
            RoleType = "Academic Leadership",
            TierName = "Academic Tier",
            Tone = "blue"
        };

        foreach (var pos in acadPositions)
        {
            foreach (var h in pos.CurrentHolders)
            {
                acadRoot.Children.Add(new OrgChartNodeViewModel
                {
                    Id = $"pos-assign-{h.Id}",
                    Name = h.StaffFullName,
                    Title = h.PositionTitle,
                    RoleType = "Academic Leadership",
                    GradeRange = h.GradeRange,
                    ContactNumber = h.ContactNumber,
                    StaffId = h.StaffMemberId,
                    PositionId = h.PositionId,
                    UnitName = h.OrgUnitName,
                    TierName = acadTier?.Name,
                    IsPrimary = h.IsPrimary,
                    Tone = "blue",
                    RequiresConfirmation = h.RequiresConfirmation
                });
            }
        }

        // Add Academic Departments under Academic Leadership
        var deptTier = tiers.FirstOrDefault(t => t.TierLevel == 6);
        var deptUnits = units.Where(u => u.TierId == deptTier?.Id || u.UnitType == OrgUnitType.AcademicDepartment).ToList();
        var deptBranch = new OrgChartNodeViewModel
        {
            Id = "tier-6",
            Name = "Academic Department Leadership",
            Title = "Departments & HODs",
            RoleType = "Academic Departments",
            TierName = "Academic Departments",
            Tone = "indigo"
        };

        foreach (var unit in deptUnits)
        {
            var unitNode = new OrgChartNodeViewModel
            {
                Id = $"unit-{unit.Id}",
                Name = unit.Name,
                Title = "Department",
                RoleType = "Department",
                UnitId = unit.Id,
                UnitName = unit.Name,
                Tone = "indigo"
            };

            foreach (var h in unit.Leadership)
            {
                unitNode.Children.Add(new OrgChartNodeViewModel
                {
                    Id = $"pos-assign-{h.Id}",
                    Name = h.StaffFullName,
                    Title = h.PositionTitle,
                    RoleType = h.RoleType.ToString(),
                    GradeRange = h.GradeRange,
                    ContactNumber = h.ContactNumber,
                    StaffId = h.StaffMemberId,
                    PositionId = h.PositionId,
                    UnitId = unit.Id,
                    UnitName = unit.Name,
                    Tone = "teal",
                    RequiresConfirmation = h.RequiresConfirmation
                });
            }
            deptBranch.Children.Add(unitNode);
        }
        acadRoot.Children.Add(deptBranch);
        rootNodes.Add(acadRoot);

        // Parents Care & Counseling Branch
        var parentsTier = tiers.FirstOrDefault(t => t.TierLevel == 3);
        var parentsPositions = positions.Where(p => p.TierId == parentsTier?.Id).ToList();
        var parentsRoot = new OrgChartNodeViewModel
        {
            Id = "tier-3",
            Name = parentsTier?.Name ?? "Parents Care, PR & Counseling",
            Title = "Parents Care & PR",
            RoleType = "Support Service",
            TierName = parentsTier?.Name,
            Tone = "teal"
        };
        foreach (var pos in parentsPositions)
        {
            foreach (var h in pos.CurrentHolders)
            {
                parentsRoot.Children.Add(new OrgChartNodeViewModel
                {
                    Id = $"pos-assign-{h.Id}",
                    Name = h.StaffFullName,
                    Title = h.PositionTitle,
                    RoleType = "Specialist",
                    ContactNumber = h.ContactNumber,
                    StaffId = h.StaffMemberId,
                    PositionId = h.PositionId,
                    UnitName = h.OrgUnitName,
                    TierName = parentsTier?.Name,
                    IsPrimary = h.IsPrimary,
                    Tone = "teal",
                    RequiresConfirmation = h.RequiresConfirmation
                });
            }
        }
        rootNodes.Add(parentsRoot);

        // Floor Supervisors Branch
        var floorTier = tiers.FirstOrDefault(t => t.TierLevel == 4);
        var floorPositions = positions.Where(p => p.TierId == floorTier?.Id).ToList();
        var floorRoot = new OrgChartNodeViewModel
        {
            Id = "tier-4",
            Name = floorTier?.Name ?? "Floor Supervisors",
            Title = "Floor Supervision",
            RoleType = "Supervisory",
            TierName = floorTier?.Name,
            Tone = "amber"
        };
        foreach (var pos in floorPositions)
        {
            foreach (var h in pos.CurrentHolders)
            {
                floorRoot.Children.Add(new OrgChartNodeViewModel
                {
                    Id = $"pos-assign-{h.Id}",
                    Name = h.StaffFullName,
                    Title = h.PositionTitle,
                    RoleType = "Floor Supervisor",
                    ContactNumber = h.ContactNumber,
                    StaffId = h.StaffMemberId,
                    PositionId = h.PositionId,
                    UnitName = h.OrgUnitName,
                    TierName = floorTier?.Name,
                    IsPrimary = h.IsPrimary,
                    Tone = "amber",
                    RequiresConfirmation = h.RequiresConfirmation
                });
            }
        }
        rootNodes.Add(floorRoot);

        // Administrative & Operational Tier Branch
        var opsTier = tiers.FirstOrDefault(t => t.TierLevel == 5);
        var opsUnits = units.Where(u => u.TierId == opsTier?.Id && u.UnitType != OrgUnitType.AcademicDepartment).ToList();
        var opsRoot = new OrgChartNodeViewModel
        {
            Id = "tier-5",
            Name = opsTier?.Name ?? "Administrative & Operational Tier",
            Title = "Administrative & Operations",
            RoleType = "Operations",
            TierName = opsTier?.Name,
            Tone = "green"
        };
        foreach (var unit in opsUnits)
        {
            var unitNode = new OrgChartNodeViewModel
            {
                Id = $"unit-{unit.Id}",
                Name = unit.Name,
                Title = unit.Name,
                RoleType = "Operational Unit",
                UnitId = unit.Id,
                UnitName = unit.Name,
                Tone = "green"
            };
            foreach (var h in unit.Leadership)
            {
                unitNode.Children.Add(new OrgChartNodeViewModel
                {
                    Id = $"pos-assign-{h.Id}",
                    Name = h.StaffFullName,
                    Title = h.PositionTitle,
                    RoleType = "Staff Member",
                    ContactNumber = h.ContactNumber,
                    StaffId = h.StaffMemberId,
                    PositionId = h.PositionId,
                    UnitId = unit.Id,
                    UnitName = unit.Name,
                    Tone = "green",
                    RequiresConfirmation = h.RequiresConfirmation
                });
            }
            opsRoot.Children.Add(unitNode);
        }
        rootNodes.Add(opsRoot);

        var allAssignments = await db.StaffPositionAssignments.AsNoTracking().Where(a => a.IsCurrent).ToListAsync(ct);
        var pendingCount = allAssignments.Count(a => !string.IsNullOrEmpty(a.Notes) && a.Notes.Contains("requires confirmation", StringComparison.OrdinalIgnoreCase));

        return new OrgChartViewModel
        {
            Tiers = tiers,
            RootNodes = rootNodes,
            TotalTiers = tiers.Count,
            TotalUnits = units.Count,
            TotalPositions = positions.Count,
            TotalAssignments = allAssignments.Count,
            TotalStaffInLeadership = allAssignments.Select(a => a.StaffMemberId).Distinct().Count(),
            PendingConfirmationCount = pendingCount,
            Search = search,
            SelectedTierId = tierId
        };
    }

    public async Task<TierLeadershipViewModel> GetTierLeadershipAsync(int tierLevel, string? search = null, CancellationToken ct = default)
    {
        var tier = (await GetTiersAsync(ct)).FirstOrDefault(t => t.TierLevel == tierLevel);
        if (tier == null) return new TierLeadershipViewModel();

        var units = await GetUnitsAsync(tier.Id, ct);
        var positions = await GetPositionsAsync(new PositionQuery { TierId = tier.Id, Search = search }, ct);
        var assignments = await GetAssignmentsAsync(null, null, null, true, ct);
        var filteredAssignments = assignments.Where(a => a.TierId == tier.Id).ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var key = TextNormalizer.NameKey(search);
            filteredAssignments = filteredAssignments.Where(a =>
                TextNormalizer.NameKey(a.StaffFullName).Contains(key) ||
                TextNormalizer.NameKey(a.PositionTitle).Contains(key) ||
                (a.ContactNumber != null && a.ContactNumber.Contains(search))
            ).ToList();
        }

        return new TierLeadershipViewModel
        {
            Tier = tier,
            Units = units,
            Positions = positions,
            Assignments = filteredAssignments,
            Search = search
        };
    }

    public async Task<AcademicLeadershipViewModel> GetAcademicLeadershipAsync(string? search = null, CancellationToken ct = default)
    {
        var tiers = await GetTiersAsync(ct);
        var acadTier = tiers.FirstOrDefault(t => t.TierLevel == 2);
        var deptTier = tiers.FirstOrDefault(t => t.TierLevel == 6);

        var assignments = await GetAssignmentsAsync(null, null, null, true, ct);
        var acadAssignments = assignments.Where(a => a.TierId == acadTier?.Id).ToList();
        var deptAssignments = assignments.Where(a => a.TierId == deptTier?.Id || a.RoleType is LeadershipRoleType.HOD or LeadershipRoleType.Coordinator or LeadershipRoleType.TeamLeader).ToList();

        var departments = await GetUnitsAsync(deptTier?.Id, ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var key = TextNormalizer.NameKey(search);
            acadAssignments = acadAssignments.Where(a => TextNormalizer.NameKey(a.StaffFullName).Contains(key) || TextNormalizer.NameKey(a.PositionTitle).Contains(key)).ToList();
            deptAssignments = deptAssignments.Where(a => TextNormalizer.NameKey(a.StaffFullName).Contains(key) || TextNormalizer.NameKey(a.PositionTitle).Contains(key)).ToList();
        }

        return new AcademicLeadershipViewModel
        {
            AcademicTier = acadTier,
            TierLeadership = acadAssignments,
            AcademicDepartments = departments,
            DepartmentLeaders = deptAssignments,
            Search = search
        };
    }

    public async Task<OperationsLeadershipViewModel> GetOperationsLeadershipAsync(string? search = null, CancellationToken ct = default)
    {
        var tiers = await GetTiersAsync(ct);
        var parentsTier = tiers.FirstOrDefault(t => t.TierLevel == 3);
        var floorTier = tiers.FirstOrDefault(t => t.TierLevel == 4);
        var opsTier = tiers.FirstOrDefault(t => t.TierLevel == 5);

        var assignments = await GetAssignmentsAsync(null, null, null, true, ct);

        var parents = assignments.Where(a => a.TierId == parentsTier?.Id).ToList();
        var floors = assignments.Where(a => a.TierId == floorTier?.Id).ToList();
        var opsUnits = await GetUnitsAsync(opsTier?.Id, ct);
        var opsAssignments = assignments.Where(a => a.TierId == opsTier?.Id).ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var key = TextNormalizer.NameKey(search);
            parents = parents.Where(a => TextNormalizer.NameKey(a.StaffFullName).Contains(key) || TextNormalizer.NameKey(a.PositionTitle).Contains(key)).ToList();
            floors = floors.Where(a => TextNormalizer.NameKey(a.StaffFullName).Contains(key) || TextNormalizer.NameKey(a.PositionTitle).Contains(key)).ToList();
            opsAssignments = opsAssignments.Where(a => TextNormalizer.NameKey(a.StaffFullName).Contains(key) || TextNormalizer.NameKey(a.PositionTitle).Contains(key)).ToList();
        }

        return new OperationsLeadershipViewModel
        {
            ParentsCareTier = parentsTier,
            FloorSupervisorsTier = floorTier,
            OperationsTier = opsTier,
            ParentsCareStaff = parents,
            FloorSupervisors = floors,
            OperationalUnits = opsUnits,
            OperationalAssignments = opsAssignments,
            Search = search
        };
    }

    public async Task<StaffProfileOrgDetailsViewModel?> GetStaffOrgProfileAsync(int staffId, CancellationToken ct = default)
    {
        var staff = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (staff == null) return null;

        var allAssignments = await db.StaffPositionAssignments.AsNoTracking()
            .Where(a => a.StaffMemberId == staffId)
            .OrderByDescending(a => a.IsCurrent).ThenByDescending(a => a.IsPrimary).ThenBy(a => a.StartDate)
            .Select(a => new PositionAssignmentDto(
                a.Id,
                a.StaffMemberId,
                staff.FullName,
                staff.ContactNumber,
                a.PositionId,
                a.Position.Title,
                a.OrgUnitId ?? a.Position.OrgUnitId,
                a.OrgUnit != null ? a.OrgUnit.Name : (a.Position.OrgUnit != null ? a.Position.OrgUnit.Name : null),
                a.Position.TierId,
                a.Position.Tier != null ? a.Position.Tier.Name : null,
                a.GradeRange ?? a.Position.DefaultGradeRange,
                a.RoleType != LeadershipRoleType.None ? a.RoleType : a.Position.RoleType,
                a.IsPrimary,
                a.IsCurrent,
                a.StartDate,
                a.EndDate,
                a.ReportsToStaffMemberId,
                a.ReportsToStaffMember != null ? a.ReportsToStaffMember.FullName : null,
                a.Notes,
                a.AssignedBy,
                a.CreatedAt
            ))
            .ToListAsync(ct);

        return new StaffProfileOrgDetailsViewModel
        {
            StaffId = staff.Id,
            FullName = staff.FullName,
            JobTitle = staff.JobTitle,
            ContactNumber = staff.ContactNumber,
            IsActive = staff.IsActive,
            Notes = staff.Notes,
            ActiveAssignments = allAssignments.Where(a => a.IsCurrent).ToList(),
            PastAssignments = allAssignments.Where(a => !a.IsCurrent).ToList()
        };
    }

    public async Task<IReadOnlyList<StaffOptionDto>> GetStaffOptionsAsync(CancellationToken ct = default)
    {
        return await db.StaffMembers.AsNoTracking()
            .OrderBy(s => s.FullName)
            .Select(s => new StaffOptionDto(s.Id, s.FullName, s.JobTitle, s.ContactNumber))
            .ToListAsync(ct);
    }
}
