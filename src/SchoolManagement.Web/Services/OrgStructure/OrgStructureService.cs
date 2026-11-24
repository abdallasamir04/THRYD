using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.OrgStructure;

// ------------------------------------------------------------------ read models

/// <summary>One person holding a position (a current assignment).</summary>
public sealed record OrgHolder(int AssignmentId, int StaffId, string FullName, string? Phone, bool IsManagement, bool IsPrimary,
    string? GradeRange, string? Scope, int? ReportsToId, string? ReportsToName, DateOnly StartDate, string? Notes)
{
    public string ProfileHref => OrgLabels.ProfileHref(StaffId, IsManagement);
}

public sealed record OrgPositionNode(int Id, string Title, string? GradeRange, LeadershipRoleType RoleType, string? Description, bool IsActive,
    int SortOrder, IReadOnlyList<OrgHolder> Holders)
{
    public string DisplayTitle => OrgLabels.PositionTitle(Title, GradeRange);
    public bool IsVacant => Holders.Count == 0;
}

public sealed record OrgDepartmentLink(int DepartmentId, string Department, int? SubcategoryId, string? Subcategory)
{
    public string Label => Subcategory is null ? Department : $"{Department} · {Subcategory}";
}

public sealed record OrgUnitNode(int Id, string Name, string? Description, int TierId, int? ParentUnitId, bool IsActive, int SortOrder,
    IReadOnlyList<OrgDepartmentLink> Departments, IReadOnlyList<OrgPositionNode> Positions, IReadOnlyList<OrgUnitNode> Children)
{
    public bool IsAcademicDepartment => Departments.Count > 0;
    public IEnumerable<OrgPositionNode> AllPositions => Positions.Concat(Children.SelectMany(c => c.AllPositions));
}

public sealed record OrgTierNode(int Id, string Name, string? Description, OrgSection Section, int SortOrder, bool IsActive, IReadOnlyList<OrgUnitNode> Units)
{
    /// <summary>The official tier's source key ("tier-1"…"tier-6"), or null for a tier added by the school.</summary>
    public string? SourceKey { get; init; }

    /// <summary>"Tier 1", "Tier 2"… by order among the tiers shown.</summary>
    public int Number { get; init; }
    public IEnumerable<OrgUnitNode> AllUnits => Units.SelectMany(Flatten);
    public IEnumerable<OrgPositionNode> AllPositions => Units.SelectMany(u => u.AllPositions);

    private static IEnumerable<OrgUnitNode> Flatten(OrgUnitNode u) => new[] { u }.Concat(u.Children.SelectMany(Flatten));
}

public sealed record OrgFlagRow(int Id, int StaffId, string StaffName, bool IsManagement, DataQualityFlagKind Kind, string Message, DateTime CreatedAt)
{
    public string ProfileHref => OrgLabels.ProfileHref(StaffId, IsManagement);
}

public sealed record OrgStructureTree(IReadOnlyList<OrgTierNode> Tiers, IReadOnlyList<OrgFlagRow> Flags)
{
    /// <summary>The same structure with only the tiers matching <paramref name="keep"/> (flags of their people only).</summary>
    public OrgStructureTree Only(Func<OrgTierNode, bool> keep)
    {
        var tiers = Tiers.Where(keep).ToList();
        var people = tiers.SelectMany(t => t.AllPositions).SelectMany(p => p.Holders).Select(h => h.StaffId).ToHashSet();
        return new OrgStructureTree(tiers, Flags.Where(f => people.Contains(f.StaffId)).ToList());
    }

    public IEnumerable<OrgPositionNode> AllPositions => Tiers.SelectMany(t => t.AllPositions);

    /// <summary>Distinct people holding at least one current position.</summary>
    public int PeopleCount => AllPositions.SelectMany(p => p.Holders).Select(h => h.StaffId).Distinct().Count();
}

/// <summary>A position of one staff member (current or ended).</summary>
public sealed record StaffOrgPosition(int AssignmentId, int PositionId, string Title, string? GradeRange, LeadershipRoleType RoleType,
    int UnitId, string Unit, int TierId, string Tier, int TierSort, OrgSection Section, IReadOnlyList<string> Departments, bool IsPrimary, bool IsCurrent,
    DateOnly StartDate, DateOnly? EndDate, string? EndReason, string? Scope, int? ReportsToId, string? ReportsToName, string? Notes)
{
    public string DisplayTitle => OrgLabels.PositionTitle(Title, GradeRange);
    public bool IsAcademicDepartment => Departments.Count > 0;
}

public sealed record StaffOrgSummary(IReadOnlyList<StaffOrgPosition> Current, IReadOnlyList<StaffOrgPosition> Ended, IReadOnlyList<OrgFlagRow> Flags)
{
    public static readonly StaffOrgSummary Empty = new([], [], []);
    public StaffOrgPosition? Primary => Current.FirstOrDefault(p => p.IsPrimary) ?? Current.FirstOrDefault();
    public IEnumerable<StaffOrgPosition> Additional => Current.Where(p => p != Primary);
    public bool PendingConfirmation => Flags.Any(f => f.Kind == DataQualityFlagKind.PositionRequiresConfirmation);
}

/// <summary>A row of the assignments table on Positions &amp; Assignments.</summary>
public sealed record AssignmentRow(int Id, int StaffId, string FullName, string? Phone, bool IsManagement, int PositionId, string PositionTitle, string? PositionGrade,
    string? GradeRange, LeadershipRoleType RoleType, int UnitId, string Unit, int TierId, string Tier, bool IsPrimary, bool IsCurrent, DateOnly StartDate,
    DateOnly? EndDate, string? EndReason, string? Scope, int? ReportsToId, string? ReportsToName, string? Notes)
{
    public string DisplayTitle => OrgLabels.PositionTitle(PositionTitle, GradeRange ?? PositionGrade);
    public string ProfileHref => OrgLabels.ProfileHref(StaffId, IsManagement);
}

public sealed record OrgStaffOption(int Id, string FullName, string? Phone, bool IsManagement, string? Department);

// ------------------------------------------------------------------ inputs

public sealed class OrgTierInput
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public OrgSection Section { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class OrgUnitInput
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int TierId { get; set; }
    public int? ParentUnitId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Existing academic departments this unit leads (empty for operational units).</summary>
    public List<int> DepartmentIds { get; set; } = [];
}

public sealed class StaffPositionInput
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int UnitId { get; set; }
    public LeadershipRoleType RoleType { get; set; }
    public string? GradeRange { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class AssignmentInput
{
    public int StaffMemberId { get; set; }
    public int PositionId { get; set; }
    public string? GradeRange { get; set; }
    public string? Scope { get; set; }
    public int? ReportsToStaffMemberId { get; set; }
    public bool IsPrimary { get; set; }
    public DateOnly? StartDate { get; set; }
    public string? Notes { get; set; }
}

public interface IOrgStructureService
{
    Task<OrgStructureTree> StructureAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<StaffOrgSummary> StaffAsync(int staffId, CancellationToken ct = default);

    /// <summary>Current positions per staff member (primary first), for lists and cards; <paramref name="staffIds"/> null = everyone.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<StaffOrgPosition>>> CurrentByStaffAsync(IReadOnlyCollection<int>? staffIds = null, CancellationToken ct = default);
    Task<IReadOnlyList<OrgFlagRow>> OpenFlagsAsync(int? staffId = null, CancellationToken ct = default);
    Task<IReadOnlyList<AssignmentRow>> AssignmentsAsync(bool includeEnded, CancellationToken ct = default);
    Task<AssignmentRow?> AssignmentAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<OrgStaffOption>> StaffOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(int Id, string Name)>> DepartmentOptionsAsync(CancellationToken ct = default);

    Task<OrgTier?> TierAsync(int id, CancellationToken ct = default);
    Task<OrgUnit?> UnitAsync(int id, CancellationToken ct = default);
    Task<StaffPosition?> PositionAsync(int id, CancellationToken ct = default);

    Task<ServiceResult> SaveTierAsync(int? id, OrgTierInput input, CancellationToken ct = default);
    Task<ServiceResult> SaveUnitAsync(int? id, OrgUnitInput input, CancellationToken ct = default);
    Task<ServiceResult> SavePositionAsync(int? id, StaffPositionInput input, CancellationToken ct = default);
    Task<ServiceResult> DeletePositionAsync(int id, CancellationToken ct = default);

    Task<ServiceResult> AssignAsync(AssignmentInput input, CancellationToken ct = default);

    /// <summary>
    /// Edits a current assignment. A different position ends this assignment (kept as history) and starts a new one on
    /// <see cref="AssignmentInput.StartDate"/>; otherwise the grade range, scope, reporting line, primary flag and notes change in place.
    /// </summary>
    Task<ServiceResult> UpdateAssignmentAsync(int id, AssignmentInput input, CancellationToken ct = default);
    Task<ServiceResult> EndAssignmentAsync(int id, DateOnly? endDate, string? reason, CancellationToken ct = default);
    Task<ServiceResult> SetPrimaryAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> ResolveFlagAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// The organizational structure: reads for the Management pages, staff cards and profiles, and the changes made on
/// Positions &amp; Assignments. Every change requires <see cref="IOrgStructureAccess"/> (checked here, server side). Nothing
/// is ever deleted except a position that was never assigned; assignments end and stay as history.
/// </summary>
public sealed class OrgStructureService(SchoolDbContext db, IOrgStructureAccess access, ICurrentUser? currentUser = null) : IOrgStructureService
{
    public static readonly DateOnly EarliestDate = new(1980, 1, 1);

    // ================================================================== reads

    public async Task<OrgStructureTree> StructureAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var tiers = await db.OrgTiers.AsNoTracking().Where(t => includeInactive || t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        var units = await db.OrgUnits.AsNoTracking().Where(u => includeInactive || u.IsActive).OrderBy(u => u.SortOrder).ThenBy(u => u.Name).ToListAsync(ct);
        var links = await DepartmentLinksAsync(ct);
        var positions = await db.StaffPositions.AsNoTracking().Where(p => includeInactive || p.IsActive).OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ThenBy(p => p.GradeRange).ToListAsync(ct);
        var holders = (await db.StaffPositionAssignments.AsNoTracking().Where(a => a.IsCurrent)
                .Select(a => new
                {
                    a.StaffPositionId,
                    Holder = new OrgHolder(a.Id, a.StaffMemberId, a.StaffMember.FullName, a.StaffMember.ContactNumber, a.StaffMember is ManagementMember, a.IsPrimary,
                        a.GradeRange, a.Scope, a.ReportsToStaffMemberId, a.ReportsTo != null ? a.ReportsTo.FullName : null, a.StartDate, a.Notes),
                })
                .ToListAsync(ct))
            .ToLookup(x => x.StaffPositionId, x => x.Holder);

        OrgUnitNode Build(OrgUnit u) => new(u.Id, u.Name, u.Description, u.OrgTierId, u.ParentUnitId, u.IsActive, u.SortOrder,
            links[u.Id].ToList(),
            positions.Where(p => p.OrgUnitId == u.Id)
                .Select(p => new OrgPositionNode(p.Id, p.Title, p.GradeRange, p.RoleType, p.Description, p.IsActive, p.SortOrder,
                    holders[p.Id].OrderBy(h => h.FullName, StringComparer.CurrentCultureIgnoreCase).ToList()))
                .ToList(),
            units.Where(c => c.ParentUnitId == u.Id).Select(Build).ToList());

        var unitIds = units.Select(u => u.Id).ToHashSet();
        var nodes = tiers.Select((t, i) => new OrgTierNode(t.Id, t.Name, t.Description, t.Section, t.SortOrder, t.IsActive,
                units.Where(u => u.OrgTierId == t.Id && (u.ParentUnitId is null || !unitIds.Contains(u.ParentUnitId.Value))).Select(Build).ToList())
            { Number = i + 1, SourceKey = t.SourceKey })
            .ToList();
        return new OrgStructureTree(nodes, await OpenFlagsAsync(null, ct));
    }

    private async Task<ILookup<int, OrgDepartmentLink>> DepartmentLinksAsync(CancellationToken ct) =>
        (await db.OrgUnitDepartments.AsNoTracking()
            .Select(l => new { l.OrgUnitId, Link = new OrgDepartmentLink(l.DepartmentId, l.Department.Name, l.DepartmentSubcategoryId, l.Subcategory != null ? l.Subcategory.Name : null), l.Department.SortOrder })
            .ToListAsync(ct))
        .OrderBy(x => x.SortOrder).ToLookup(x => x.OrgUnitId, x => x.Link);

    public async Task<StaffOrgSummary> StaffAsync(int staffId, CancellationToken ct = default)
    {
        var rows = await PositionsQuery(db.StaffPositionAssignments.AsNoTracking().Where(a => a.StaffMemberId == staffId), ct);
        return new StaffOrgSummary(
            Order(rows.Where(r => r.IsCurrent)).ToList(),
            rows.Where(r => !r.IsCurrent).OrderByDescending(r => r.EndDate).ThenByDescending(r => r.AssignmentId).ToList(),
            await OpenFlagsAsync(staffId, ct));
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<StaffOrgPosition>>> CurrentByStaffAsync(IReadOnlyCollection<int>? staffIds = null, CancellationToken ct = default)
    {
        var query = db.StaffPositionAssignments.AsNoTracking().Where(a => a.IsCurrent);
        if (staffIds is not null) query = query.Where(a => staffIds.Contains(a.StaffMemberId));
        var rows = await PositionsWithStaff(query, ct);
        return rows.GroupBy(r => r.Staff).ToDictionary(g => g.Key, g => (IReadOnlyList<StaffOrgPosition>)Order(g.Select(x => x.Position)).ToList());
    }

    /// <summary>Primary first, then by tier and position order.</summary>
    private static IEnumerable<StaffOrgPosition> Order(IEnumerable<StaffOrgPosition> list) =>
        list.OrderByDescending(p => p.IsPrimary).ThenBy(p => p.TierSort).ThenBy(p => p.Unit).ThenBy(p => p.DisplayTitle);

    private async Task<List<StaffOrgPosition>> PositionsQuery(IQueryable<StaffPositionAssignment> query, CancellationToken ct) =>
        (await PositionsWithStaff(query, ct)).Select(x => x.Position).ToList();

    private async Task<List<(int Staff, StaffOrgPosition Position)>> PositionsWithStaff(IQueryable<StaffPositionAssignment> query, CancellationToken ct)
    {
        var rows = await query.Select(a => new
        {
            a.Id, a.StaffMemberId, a.StaffPositionId, a.Position.Title, PositionGrade = a.Position.GradeRange, a.GradeRange, a.Position.RoleType,
            a.Position.OrgUnitId, Unit = a.Position.Unit.Name, a.Position.Unit.OrgTierId, Tier = a.Position.Unit.Tier.Name, TierSort = a.Position.Unit.Tier.SortOrder,
            a.Position.Unit.Tier.Section, a.IsPrimary, a.IsCurrent, a.StartDate, a.EndDate, a.EndReason, a.Scope, a.ReportsToStaffMemberId,
            ReportsTo = a.ReportsTo != null ? a.ReportsTo.FullName : null, a.Notes,
        }).ToListAsync(ct);
        var links = await DepartmentLinksAsync(ct);
        return rows.Select(r => (r.StaffMemberId, new StaffOrgPosition(r.Id, r.StaffPositionId, r.Title, r.GradeRange ?? r.PositionGrade, r.RoleType, r.OrgUnitId, r.Unit,
            r.OrgTierId, r.Tier, r.TierSort, r.Section, links[r.OrgUnitId].Select(l => l.Label).ToList(), r.IsPrimary, r.IsCurrent, r.StartDate, r.EndDate,
            r.EndReason, r.Scope, r.ReportsToStaffMemberId, r.ReportsTo, r.Notes))).ToList();
    }

    public async Task<IReadOnlyList<OrgFlagRow>> OpenFlagsAsync(int? staffId = null, CancellationToken ct = default) =>
        await db.StaffDataQualityFlags.AsNoTracking().Where(f => !f.IsResolved && (staffId == null || f.StaffMemberId == staffId))
            .OrderBy(f => f.Kind).ThenBy(f => f.StaffMember.FullName)
            .Select(f => new OrgFlagRow(f.Id, f.StaffMemberId, f.StaffMember.FullName, f.StaffMember is ManagementMember, f.Kind, f.Message, f.CreatedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AssignmentRow>> AssignmentsAsync(bool includeEnded, CancellationToken ct = default) =>
        (await AssignmentRows(db.StaffPositionAssignments.AsNoTracking().Where(a => includeEnded || a.IsCurrent)).ToListAsync(ct))
            .OrderByDescending(a => a.IsCurrent).ThenBy(a => a.FullName, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(a => a.IsPrimary)
            .ThenByDescending(a => a.StartDate).ToList();

    public Task<AssignmentRow?> AssignmentAsync(int id, CancellationToken ct = default) =>
        AssignmentRows(db.StaffPositionAssignments.AsNoTracking().Where(a => a.Id == id)).FirstOrDefaultAsync(ct);

    private static IQueryable<AssignmentRow> AssignmentRows(IQueryable<StaffPositionAssignment> query) =>
        query.Select(a => new AssignmentRow(a.Id, a.StaffMemberId, a.StaffMember.FullName, a.StaffMember.ContactNumber, a.StaffMember is ManagementMember,
            a.StaffPositionId, a.Position.Title, a.Position.GradeRange, a.GradeRange, a.Position.RoleType, a.Position.OrgUnitId, a.Position.Unit.Name,
            a.Position.Unit.OrgTierId, a.Position.Unit.Tier.Name, a.IsPrimary, a.IsCurrent, a.StartDate, a.EndDate, a.EndReason, a.Scope,
            a.ReportsToStaffMemberId, a.ReportsTo != null ? a.ReportsTo.FullName : null, a.Notes));

    public async Task<IReadOnlyList<OrgStaffOption>> StaffOptionsAsync(CancellationToken ct = default) =>
        (await db.StaffMembers.AsNoTracking().Where(s => s.ArchivedAt == null)   // current staff only
            .Select(s => new OrgStaffOption(s.Id, s.FullName, s.ContactNumber, s is ManagementMember, s.Department != null ? s.Department.Name : null))
            .ToListAsync(ct))
        .OrderBy(s => s.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();

    public async Task<IReadOnlyList<(int Id, string Name)>> DepartmentOptionsAsync(CancellationToken ct = default) =>
        (await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
        .Select(d => (d.Id, d.Name)).ToList();

    public Task<OrgTier?> TierAsync(int id, CancellationToken ct = default) => db.OrgTiers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<OrgUnit?> UnitAsync(int id, CancellationToken ct = default) =>
        db.OrgUnits.AsNoTracking().Include(u => u.Departments).FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<StaffPosition?> PositionAsync(int id, CancellationToken ct = default) => db.StaffPositions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    // ================================================================== tiers, units, positions

    public async Task<ServiceResult> SaveTierAsync(int? id, OrgTierInput input, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var name = TextNormalizer.CleanText(input.Name);
        var result = ServiceResult.Ok();
        RequireText(result, "Name", name, OrgTier.NameMaxLength, "Enter the tier name.");
        MaxText(result, "Description", input.Description, OrgTier.DescriptionMaxLength);
        if (!Enum.IsDefined(input.Section)) result.AddError("Section", "Choose School Leadership or Operation Team.");
        if (input.SortOrder is < 0 or > 999) result.AddError("SortOrder", "Use an order between 0 and 999.");
        if (!result.Succeeded) return result;

        var key = TextNormalizer.NameKey(name);
        if (await db.OrgTiers.AnyAsync(t => t.NormalizedName == key && t.Id != id, ct))
            return ServiceResult.Fail("Name", $"A tier named \"{name}\" already exists.");
        var tier = id is null ? new OrgTier() : await db.OrgTiers.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tier is null) return ServiceResult.Fail("", "This tier no longer exists.");
        (tier.Name, tier.NormalizedName, tier.Description, tier.Section, tier.SortOrder, tier.IsActive) =
            (name, key, Optional(input.Description), input.Section, input.SortOrder, input.IsActive);
        if (id is null) db.OrgTiers.Add(tier);
        return await SaveAsync(tier, () => tier.Id, ct);
    }

    public async Task<ServiceResult> SaveUnitAsync(int? id, OrgUnitInput input, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var name = TextNormalizer.CleanText(input.Name);
        var result = ServiceResult.Ok();
        RequireText(result, "Name", name, OrgUnit.NameMaxLength, "Enter the unit name.");
        MaxText(result, "Description", input.Description, OrgUnit.DescriptionMaxLength);
        if (input.SortOrder is < 0 or > 999) result.AddError("SortOrder", "Use an order between 0 and 999.");
        if (!await db.OrgTiers.AnyAsync(t => t.Id == input.TierId, ct)) result.AddError("TierId", "Choose a tier.");
        if (!result.Succeeded) return result;

        if (input.ParentUnitId is { } parentId)
        {
            var parent = await db.OrgUnits.AsNoTracking().Where(u => u.Id == parentId).Select(u => new { u.OrgTierId }).FirstOrDefaultAsync(ct);
            if (parent is null || parent.OrgTierId != input.TierId) return ServiceResult.Fail("ParentUnitId", "Choose a parent unit in the same tier.");
            // No cycles: the parent may not be this unit or one of its descendants.
            if (id is { } self)
            {
                var parents = await db.OrgUnits.AsNoTracking().Select(u => new { u.Id, u.ParentUnitId }).ToDictionaryAsync(u => u.Id, u => u.ParentUnitId, ct);
                for (int? cursor = parentId; cursor is { } c; cursor = parents.GetValueOrDefault(c))
                    if (c == self) return ServiceResult.Fail("ParentUnitId", "A unit cannot be placed under itself or one of its own sub-units.");
            }
        }
        var key = TextNormalizer.NameKey(name);
        if (await db.OrgUnits.AnyAsync(u => u.OrgTierId == input.TierId && u.NormalizedName == key && u.Id != id, ct))
            return ServiceResult.Fail("Name", $"This tier already has a unit named \"{name}\".");
        var departmentIds = input.DepartmentIds.Distinct().ToList();
        if (departmentIds.Count > 0 && await db.Departments.CountAsync(d => departmentIds.Contains(d.Id), ct) != departmentIds.Count)
            return ServiceResult.Fail("DepartmentIds", "Choose departments from the list.");

        var unit = id is null ? new OrgUnit() : await db.OrgUnits.Include(u => u.Departments).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (unit is null) return ServiceResult.Fail("", "This unit no longer exists.");
        if (id is not null && unit.OrgTierId != input.TierId && await db.OrgUnits.AnyAsync(u => u.ParentUnitId == unit.Id, ct))
            return ServiceResult.Fail("TierId", "Move or remove this unit's sub-units before moving it to another tier.");
        (unit.Name, unit.NormalizedName, unit.Description, unit.OrgTierId, unit.ParentUnitId, unit.SortOrder, unit.IsActive) =
            (name, key, Optional(input.Description), input.TierId, input.ParentUnitId, input.SortOrder, input.IsActive);
        // Department links: keep the existing ones (with their subcategory), add new ones, drop the ones not chosen.
        foreach (var gone in unit.Departments.Where(l => !departmentIds.Contains(l.DepartmentId)).ToList()) unit.Departments.Remove(gone);
        foreach (var add in departmentIds.Where(d => unit.Departments.All(l => l.DepartmentId != d)))
            unit.Departments.Add(new OrgUnitDepartment { DepartmentId = add });
        if (id is null) db.OrgUnits.Add(unit);
        return await SaveAsync(unit, () => unit.Id, ct);
    }

    public async Task<ServiceResult> SavePositionAsync(int? id, StaffPositionInput input, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var title = TextNormalizer.CleanText(input.Title);
        var grade = Optional(input.GradeRange);
        var result = ServiceResult.Ok();
        RequireText(result, "Title", title, StaffPosition.TitleMaxLength, "Enter the position title.");
        MaxText(result, "Description", input.Description, StaffPosition.DescriptionMaxLength);
        MaxText(result, "GradeRange", grade, StaffPosition.GradeRangeMaxLength);
        if (!Enum.IsDefined(input.RoleType)) result.AddError("RoleType", "Choose a role category.");
        if (input.SortOrder is < 0 or > 999) result.AddError("SortOrder", "Use an order between 0 and 999.");
        if (!await db.OrgUnits.AnyAsync(u => u.Id == input.UnitId, ct)) result.AddError("UnitId", "Choose a unit.");
        if (!result.Succeeded) return result;

        var key = TextNormalizer.NameKey(title);
        if (await db.StaffPositions.AnyAsync(p => p.OrgUnitId == input.UnitId && p.NormalizedTitle == key && p.GradeRange == grade && p.Id != id, ct))
            return ServiceResult.Fail("Title", $"This unit already has the position \"{OrgLabels.PositionTitle(title, grade)}\".");
        var position = id is null ? new StaffPosition() : await db.StaffPositions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position is null) return ServiceResult.Fail("", "This position no longer exists.");
        if (id is not null && !input.IsActive && position.IsActive && await db.StaffPositionAssignments.AnyAsync(a => a.StaffPositionId == position.Id && a.IsCurrent, ct))
            return ServiceResult.Fail("IsActive", "End the current assignments of this position before deactivating it.");
        (position.Title, position.NormalizedTitle, position.Description, position.OrgUnitId, position.RoleType, position.GradeRange, position.SortOrder, position.IsActive) =
            (title, key, Optional(input.Description), input.UnitId, input.RoleType, grade, input.SortOrder, input.IsActive);
        if (id is null) db.StaffPositions.Add(position);
        var saved = await SaveAsync(position, () => position.Id, ct);
        // Holders whose job title mirrors this position see the new title.
        if (saved.Succeeded && id is not null)
            foreach (var staffId in await db.StaffPositionAssignments.Where(a => a.StaffPositionId == position.Id && a.IsCurrent && a.IsPrimary).Select(a => a.StaffMemberId).ToListAsync(ct))
                await SyncJobTitleAsync(staffId, ct);
        return saved;
    }

    public async Task<ServiceResult> DeletePositionAsync(int id, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var position = await db.StaffPositions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position is null) return ServiceResult.Fail("", "This position no longer exists.");
        // Assignment history is never deleted: a position that was ever held can only be deactivated.
        if (await db.StaffPositionAssignments.AnyAsync(a => a.StaffPositionId == id, ct))
            return ServiceResult.Fail("", "This position has assignment history, so it is kept. Deactivate it instead.");
        db.StaffPositions.Remove(position);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ================================================================== assignments

    public async Task<ServiceResult> AssignAsync(AssignmentInput input, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var check = await ValidateAssignmentAsync(input, null, ct);
        if (!check.Succeeded) return check;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var created = await CreateAssignmentAsync(input, ct);
            await tx.CommitAsync(ct);
            return ServiceResult.Ok(created.Id);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This assignment was changed by someone else at the same time. Reload and try again.");
        }
    }

    public async Task<ServiceResult> UpdateAssignmentAsync(int id, AssignmentInput input, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var row = await db.StaffPositionAssignments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (row is null) return ServiceResult.Fail("", "This assignment no longer exists.");
        if (!row.IsCurrent) return ServiceResult.Fail("", "This assignment has ended; ended assignments are kept unchanged as history.");
        input.StaffMemberId = row.StaffMemberId; // the person of an assignment never changes
        var check = await ValidateAssignmentAsync(input, row, ct);
        if (!check.Succeeded) return check;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (input.PositionId != row.StaffPositionId)
            {
                // A change of position: the old assignment ends (history kept) and a new one starts.
                var start = input.StartDate ?? SchoolWeek.Today;
                var wasPrimary = row.IsPrimary;
                var newTitle = await db.StaffPositions.Where(p => p.Id == input.PositionId).Select(p => p.Title).FirstAsync(ct);
                End(row, start < row.StartDate ? row.StartDate : start, $"Moved to {newTitle}");
                await db.SaveChangesAsync(ct);
                input.IsPrimary = input.IsPrimary || wasPrimary;
                input.StartDate = start;
                var created = await CreateAssignmentAsync(input, ct);
                await tx.CommitAsync(ct);
                return ServiceResult.Ok(created.Id);
            }
            (row.GradeRange, row.Scope, row.ReportsToStaffMemberId, row.Notes) =
                (Optional(input.GradeRange), Optional(input.Scope), input.ReportsToStaffMemberId, Optional(input.Notes));
            if (input.StartDate is { } s) row.StartDate = s;
            if (input.IsPrimary && !row.IsPrimary) await MakePrimaryAsync(row, ct);
            await db.SaveChangesAsync(ct);
            await EnsurePrimaryAsync(row.StaffMemberId, ct);
            await SyncJobTitleAsync(row.StaffMemberId, ct);
            await tx.CommitAsync(ct);
            return ServiceResult.Ok(row.Id);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This assignment was changed by someone else at the same time. Reload and try again.");
        }
    }

    public async Task<ServiceResult> EndAssignmentAsync(int id, DateOnly? endDate, string? reason, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var row = await db.StaffPositionAssignments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (row is null) return ServiceResult.Fail("", "This assignment no longer exists.");
        if (!row.IsCurrent) return ServiceResult.Fail("", "This assignment has already ended.");
        var end = endDate ?? SchoolWeek.Today;
        if (end < row.StartDate) return ServiceResult.Fail("EndDate", $"The end date cannot be before the start date ({row.StartDate:d MMM yyyy}).");
        if (end > SchoolWeek.Today.AddYears(1)) return ServiceResult.Fail("EndDate", "Enter an end date within one year from today.");
        var text = Optional(reason);
        if (text?.Length > StaffPositionAssignment.ReasonMaxLength) return ServiceResult.Fail("Reason", $"Use {StaffPositionAssignment.ReasonMaxLength} characters or fewer.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        End(row, end, text ?? "Assignment ended");
        await db.SaveChangesAsync(ct);
        await EnsurePrimaryAsync(row.StaffMemberId, ct);
        await SyncJobTitleAsync(row.StaffMemberId, ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(row.Id);
    }

    public async Task<ServiceResult> SetPrimaryAsync(int id, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var row = await db.StaffPositionAssignments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (row is null) return ServiceResult.Fail("", "This assignment no longer exists.");
        if (!row.IsCurrent) return ServiceResult.Fail("", "Only a current assignment can be the primary position.");
        if (row.IsPrimary) return ServiceResult.Ok(row.Id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await MakePrimaryAsync(row, ct);
        await db.SaveChangesAsync(ct);
        await SyncJobTitleAsync(row.StaffMemberId, ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(row.Id);
    }

    public async Task<ServiceResult> ResolveFlagAsync(int id, CancellationToken ct = default)
    {
        await access.EnsureAsync(ct);
        var flag = await db.StaffDataQualityFlags.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (flag is null) return ServiceResult.Fail("", "This warning no longer exists.");
        if (flag.IsResolved) return ServiceResult.Ok(id);
        (flag.IsResolved, flag.ResolvedAt, flag.ResolvedBy) = (true, DateTime.UtcNow, currentUser?.Name);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    private async Task<ServiceResult> ValidateAssignmentAsync(AssignmentInput input, StaffPositionAssignment? existing, CancellationToken ct)
    {
        var result = ServiceResult.Ok();
        MaxText(result, "GradeRange", input.GradeRange, StaffPosition.GradeRangeMaxLength);
        MaxText(result, "Scope", input.Scope, StaffPositionAssignment.ScopeMaxLength);
        MaxText(result, "Notes", input.Notes, StaffPositionAssignment.NotesMaxLength);
        if (input.StartDate is { } d && (d < EarliestDate || d > SchoolWeek.Today.AddYears(1)))
            result.AddError("StartDate", "Enter a start date between 1980 and one year from today.");
        if (!result.Succeeded) return result;

        var staff = await db.StaffMembers.AsNoTracking().Where(s => s.Id == input.StaffMemberId).Select(s => s.FullName).FirstOrDefaultAsync(ct);
        if (staff is null) return ServiceResult.Fail("StaffMemberId", "Choose a staff member from the list.");
        var position = await db.StaffPositions.AsNoTracking().Where(p => p.Id == input.PositionId)
            .Select(p => new { p.Title, p.GradeRange, p.IsActive }).FirstOrDefaultAsync(ct);
        if (position is null) return ServiceResult.Fail("PositionId", "Choose a position from the list.");
        var moving = existing is null || existing.StaffPositionId != input.PositionId;
        if (moving && !position.IsActive) return ServiceResult.Fail("PositionId", "This position is inactive. Activate it first or choose another.");
        if (moving && await db.StaffPositionAssignments.AnyAsync(a => a.StaffMemberId == input.StaffMemberId && a.StaffPositionId == input.PositionId && a.IsCurrent, ct))
            return ServiceResult.Fail("PositionId", $"{staff} already holds {OrgLabels.PositionTitle(position.Title, position.GradeRange)}.");
        if (input.ReportsToStaffMemberId is { } boss)
        {
            if (boss == input.StaffMemberId) return ServiceResult.Fail("ReportsToStaffMemberId", "A person cannot report to themselves.");
            if (!await db.StaffMembers.AnyAsync(s => s.Id == boss, ct)) return ServiceResult.Fail("ReportsToStaffMemberId", "Choose who this person reports to from the list.");
        }
        return result;
    }

    private async Task<StaffPositionAssignment> CreateAssignmentAsync(AssignmentInput input, CancellationToken ct)
    {
        var hasPrimary = await db.StaffPositionAssignments.AnyAsync(a => a.StaffMemberId == input.StaffMemberId && a.IsCurrent && a.IsPrimary, ct);
        var row = new StaffPositionAssignment
        {
            StaffMemberId = input.StaffMemberId, StaffPositionId = input.PositionId, GradeRange = Optional(input.GradeRange), Scope = Optional(input.Scope),
            ReportsToStaffMemberId = input.ReportsToStaffMemberId, Notes = Optional(input.Notes), StartDate = input.StartDate ?? SchoolWeek.Today,
            IsCurrent = true, IsPrimary = false, AssignedBy = currentUser?.Name,
        };
        db.StaffPositionAssignments.Add(row);
        await db.SaveChangesAsync(ct);
        // The first position is primary; a new primary takes over from the old one (saved in that order: one primary at a time).
        if (!hasPrimary || input.IsPrimary)
        {
            await MakePrimaryAsync(row, ct);
            await db.SaveChangesAsync(ct);
        }
        await SyncJobTitleAsync(row.StaffMemberId, ct);
        return row;
    }

    private async Task MakePrimaryAsync(StaffPositionAssignment row, CancellationToken ct)
    {
        var others = await db.StaffPositionAssignments.Where(a => a.StaffMemberId == row.StaffMemberId && a.IsCurrent && a.IsPrimary && a.Id != row.Id).ToListAsync(ct);
        if (others.Count > 0)
        {
            foreach (var o in others) o.IsPrimary = false;
            await db.SaveChangesAsync(ct);
        }
        row.IsPrimary = true;
    }

    /// <summary>A person with current positions always has one primary: the earliest remaining one when the primary ended.</summary>
    private async Task EnsurePrimaryAsync(int staffId, CancellationToken ct)
    {
        var current = await db.StaffPositionAssignments.Where(a => a.StaffMemberId == staffId && a.IsCurrent).OrderBy(a => a.StartDate).ThenBy(a => a.Id).ToListAsync(ct);
        if (current.Count == 0 || current.Any(a => a.IsPrimary)) return;
        current[0].IsPrimary = true;
        await db.SaveChangesAsync(ct);
    }

    private static void End(StaffPositionAssignment row, DateOnly end, string reason)
    {
        row.IsCurrent = false;
        row.IsPrimary = false;
        row.EndDate = end;
        row.EndReason = reason.Length > StaffPositionAssignment.ReasonMaxLength ? reason[..StaffPositionAssignment.ReasonMaxLength] : reason;
    }

    /// <summary>
    /// The job title shown by other modules (duties, evaluations) mirrors the primary position. A Management Team member in
    /// one of the seven leadership positions keeps that position's title; nobody's title is cleared when positions end.
    /// </summary>
    public async Task SyncJobTitleAsync(int staffId, CancellationToken ct)
    {
        var staff = await db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (staff is null || staff is ManagementMember { Position: not null }) return;
        var primary = await db.StaffPositionAssignments.AsNoTracking().Where(a => a.StaffMemberId == staffId && a.IsCurrent && a.IsPrimary)
            .Select(a => new { a.Position.Title, Grade = a.GradeRange ?? a.Position.GradeRange }).FirstOrDefaultAsync(ct);
        if (primary is null) return;
        var title = OrgLabels.PositionTitle(primary.Title, primary.Grade);
        if (title.Length > StaffMember.JobTitleMaxLength) title = title[..StaffMember.JobTitleMaxLength];
        if (staff.JobTitle == title) return;
        staff.JobTitle = title;
        await db.SaveChangesAsync(ct);
    }

    // ================================================================== helpers

    private async Task<ServiceResult> SaveAsync(object entity, Func<int> id, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return ServiceResult.Ok(id());
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This name was saved by someone else at the same time. Reload and try again.");
        }
    }

    private static string? Optional(string? value)
    {
        var text = TextNormalizer.CleanText(value);
        return text.Length == 0 ? null : text;
    }

    private static void RequireText(ServiceResult result, string field, string value, int max, string missing)
    {
        if (value.Length == 0) result.AddError(field, missing);
        else if (value.Length > max) result.AddError(field, $"Use {max} characters or fewer.");
        else if (!TextNormalizer.ContainsLetter(value)) result.AddError(field, "Use letters, not only numbers or symbols.");
    }

    private static void MaxText(ServiceResult result, string field, string? value, int max)
    {
        if (TextNormalizer.CleanText(value).Length > max) result.AddError(field, $"Use {max} characters or fewer.");
    }
}
