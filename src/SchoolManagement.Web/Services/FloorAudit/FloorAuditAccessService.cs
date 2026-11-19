using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>
/// What the person working in this browser may do in Floor Audit. The identity is the application's current user
/// ("Working as" until sign-in exists). Managers: current Management Team members and staff given the Floor Audit
/// Manager role. Inspectors: staff given the Inspector role for a floor (or every floor), optionally allowed to record
/// student movements. Everyone else can view.
/// </summary>
public sealed record FloorAuditUser(
    int? StaffId, string? Name, bool IsManager, bool ManagerByPosition, bool InspectsAll, IReadOnlySet<int> InspectFloors,
    bool MovementsAll, IReadOnlySet<int> MovementFloors)
{
    public static readonly FloorAuditUser Anonymous = new(null, null, false, false, false, new HashSet<int>(), false, new HashSet<int>());

    public bool HasIdentity => StaffId is not null;
    public bool CanManage => IsManager;
    public bool CanInspect(int floorId) => IsManager || InspectsAll || InspectFloors.Contains(floorId);
    public bool CanInspectAny => IsManager || InspectsAll || InspectFloors.Count > 0;

    /// <summary>Movements are recorded by the floor of the class the student leaves (null: the class has no room yet).</summary>
    public bool CanRecordMovements(int? floorId) => IsManager || MovementsAll || (floorId is { } f && MovementFloors.Contains(f));
    public bool CanRecordAnyMovement => IsManager || MovementsAll || MovementFloors.Count > 0;

    public string RoleLabel => IsManager ? (ManagerByPosition ? "Manager (Management Team)" : "Floor Audit Manager")
        : CanInspectAny ? "Floor Inspector" : "View only";
}

/// <summary>A person who can be chosen under "Working as", with what they may do in Floor Audit.</summary>
public sealed record IdentityOption(int Id, string Name, string Role);

/// <summary>"Working as" choices grouped by Floor Audit role: managers, inspectors, everyone else (view only).</summary>
public sealed record IdentityOptions(IReadOnlyList<IdentityOption> Managers, IReadOnlyList<IdentityOption> Inspectors, IReadOnlyList<IdentityOption> Others);

public interface IFloorAuditAccessService
{
    Task<FloorAuditUser> CurrentAsync(CancellationToken ct = default);
    Task<FloorAuditUser> ForStaffAsync(int? staffId, string? name, CancellationToken ct = default);
    Task<IdentityOptions> IdentityOptionsAsync(CancellationToken ct = default);
}

public sealed class FloorAuditAccessService(SchoolDbContext db, ICurrentUser currentUser) : IFloorAuditAccessService
{
    private FloorAuditUser? _current;

    public async Task<FloorAuditUser> CurrentAsync(CancellationToken ct = default) =>
        _current ??= await ForStaffAsync(currentUser.StaffId, currentUser.Name, ct);

    public async Task<IdentityOptions> IdentityOptionsAsync(CancellationToken ct = default)
    {
        var staff = await db.StaffMembers.AsNoTracking().OrderBy(x => x.FullName).Select(x => new { x.Id, x.FullName, x.JobTitle }).ToListAsync(ct);
        var positions = (await db.PositionAssignments.AsNoTracking().Where(p => p.IsCurrent).Select(p => p.ManagementMemberId).ToListAsync(ct)).ToHashSet();
        var roles = await db.FloorAuditAccess.AsNoTracking().Where(a => a.EndedAt == null)
            .Select(a => new { a.StaffMemberId, a.Role, Floor = a.Floor!.Name, a.CanRecordMovements }).ToListAsync(ct);
        var managers = staff.Where(x => positions.Contains(x.Id) || roles.Any(r => r.StaffMemberId == x.Id && r.Role == FloorAuditRole.Manager))
            .Select(x => new IdentityOption(x.Id, x.FullName, positions.Contains(x.Id) ? "Manager · Management Team" : "Floor Audit Manager")).ToList();
        var managerIds = managers.Select(m => m.Id).ToHashSet();
        var inspectors = staff.Where(x => !managerIds.Contains(x.Id) && roles.Any(r => r.StaffMemberId == x.Id && r.Role == FloorAuditRole.Inspector))
            .Select(x =>
            {
                var mine = roles.Where(r => r.StaffMemberId == x.Id && r.Role == FloorAuditRole.Inspector).ToList();
                var floors = string.Join(", ", mine.Select(r => r.Floor ?? "every floor"));
                return new IdentityOption(x.Id, x.FullName, $"Inspector · {floors}{(mine.Any(r => r.CanRecordMovements) ? " · student movements" : "")}");
            }).ToList();
        var others = staff.Where(x => !managerIds.Contains(x.Id) && inspectors.All(i => i.Id != x.Id))
            .Select(x => new IdentityOption(x.Id, x.FullName, string.IsNullOrWhiteSpace(x.JobTitle) ? "View only" : $"{x.JobTitle} · view only")).ToList();
        return new IdentityOptions(managers, inspectors, others);
    }

    public async Task<FloorAuditUser> ForStaffAsync(int? staffId, string? name, CancellationToken ct = default)
    {
        if (staffId is not { } id) return FloorAuditUser.Anonymous with { Name = name };
        var byPosition = await db.PositionAssignments.AsNoTracking().AnyAsync(p => p.ManagementMemberId == id && p.IsCurrent, ct);
        var roles = await db.FloorAuditAccess.AsNoTracking().Where(a => a.StaffMemberId == id && a.EndedAt == null)
            .Select(a => new { a.Role, a.FloorId, a.CanRecordMovements }).ToListAsync(ct);
        var manager = byPosition || roles.Any(r => r.Role == FloorAuditRole.Manager);
        var inspector = roles.Where(r => r.Role == FloorAuditRole.Inspector).ToList();
        return new FloorAuditUser(
            id, name, manager, byPosition,
            inspector.Any(r => r.FloorId is null),
            inspector.Where(r => r.FloorId is not null).Select(r => r.FloorId!.Value).ToHashSet(),
            inspector.Any(r => r.FloorId is null && r.CanRecordMovements),
            inspector.Where(r => r.FloorId is not null && r.CanRecordMovements).Select(r => r.FloorId!.Value).ToHashSet());
    }
}

/// <summary>Writes Floor Audit's audit trail (added to the context; saved with the change it describes).</summary>
public static class FloorAuditLog
{
    public static void Add(SchoolDbContext db, ICurrentUser user, string entity, int? entityId, int? floorId, string action, string text) =>
        db.FloorAuditLog.Add(new FloorAuditLogEntry
        {
            Entity = entity, EntityId = entityId, FloorId = floorId, Action = action,
            Text = text.Length > FloorAuditLogEntry.TextMaxLength ? text[..FloorAuditLogEntry.TextMaxLength] : text,
            At = DateTime.UtcNow, ByStaffId = user.StaffId, By = user.Name,
        });
}
