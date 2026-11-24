using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.OrgStructure;

/// <summary>
/// Who may change the organizational structure (tiers, units, positions, assignments, reporting lines, the official import).
/// Everyone may view it. The Super Admin (<see cref="ICurrentUser.IsSuperAdmin"/>) always may; staff are configured in the
/// "OrgStructure" section of appsettings.json (or environment variables), like Duty Audit. Nothing is granted by default.
/// <list type="bullet">
/// <item><see cref="AuthorizedStaffIds"/>: specific staff members (StaffMembers.Id), as chosen under "Working as".</item>
/// <item><see cref="AuthorizedPositions"/>: organizational position titles (e.g. "School Principal / Head of School") or a
/// leadership position's name ("SchoolDirector"); whoever currently holds one has access.</item>
/// <item><see cref="AuthorizedRoles"/>: role names of a signed-in user, for when sign-in exists.</item>
/// </list>
/// </summary>
public sealed class OrgStructureOptions
{
    public const string Section = "OrgStructure";

    public int[] AuthorizedStaffIds { get; set; } = [];
    public string[] AuthorizedPositions { get; set; } = [];
    public string[] AuthorizedRoles { get; set; } = [];
}

public sealed class OrgStructureAccessDeniedException() : UnauthorizedAccessException(OrgStructureAccess.DeniedMessage);

public interface IOrgStructureAccess
{
    /// <summary>True when the person working in this request may change the organizational structure.</summary>
    Task<bool> CanManageAsync(CancellationToken ct = default);

    async Task EnsureAsync(CancellationToken ct = default)
    {
        if (!await CanManageAsync(ct)) throw new OrgStructureAccessDeniedException();
    }
}

public sealed class OrgStructureAccess(IOptionsMonitor<OrgStructureOptions> options, ICurrentUser currentUser, IHttpContextAccessor http, SchoolDbContext db)
    : IOrgStructureAccess
{
    public const string DeniedMessage =
        "Only the Super Admin and staff authorized for the organizational structure can change positions and assignments. Ask the school administration for access.";

    private bool? _allowed;

    public async Task<bool> CanManageAsync(CancellationToken ct = default) => _allowed ??= await DecideAsync(ct);

    private async Task<bool> DecideAsync(CancellationToken ct)
    {
        if (currentUser.IsSuperAdmin) return true;
        var o = options.CurrentValue;
        if (http.HttpContext?.User is { Identity.IsAuthenticated: true } user && (o.AuthorizedRoles ?? []).Any(r => !string.IsNullOrWhiteSpace(r) && user.IsInRole(r.Trim())))
            return true;
        if (currentUser.StaffId is not { } staffId) return false;
        if ((o.AuthorizedStaffIds ?? []).Contains(staffId)) return true;

        var configured = (o.AuthorizedPositions ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (configured.Count == 0) return false;
        var leadership = configured.Select(p => Enum.TryParse<ManagementPosition>(p, true, out var v) && Enum.IsDefined(v) && !int.TryParse(p, out _) ? v : (ManagementPosition?)null)
            .OfType<ManagementPosition>().ToList();
        if (leadership.Count > 0 && await db.ManagementMembers.AsNoTracking().AnyAsync(m => m.Id == staffId && m.Position != null && leadership.Contains(m.Position.Value), ct))
            return true;
        var titles = configured.Select(TextNormalizer.NameKey).ToList();
        return await db.StaffPositionAssignments.AsNoTracking()
            .AnyAsync(a => a.StaffMemberId == staffId && a.IsCurrent && titles.Contains(a.Position.NormalizedTitle), ct);
    }
}

/// <summary>Trusted code outside a user's request (the import command line, the test suite's direct service calls).</summary>
public sealed class TrustedOrgStructureAccess : IOrgStructureAccess
{
    public static readonly TrustedOrgStructureAccess Instance = new();

    public Task<bool> CanManageAsync(CancellationToken ct = default) => Task.FromResult(true);
}
