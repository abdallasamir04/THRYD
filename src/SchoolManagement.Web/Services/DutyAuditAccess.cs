using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>
/// Who may open Duty Audit (the duty audit list, its report, audit records, Staff Duty Records). The Super Admin
/// (<see cref="ICurrentUser.IsSuperAdmin"/>) always may. Staff are configured in the
/// "DutyAudit" section of appsettings.json (or environment variables, e.g. DutyAudit__AuthorizedStaffIds__0=12); the section
/// is re-read when the file changes. Nothing is granted by default: with every list empty, nobody has access.
/// <list type="bullet">
/// <item><see cref="AuthorizedStaffIds"/>: specific staff members (StaffMembers.Id), as chosen under "Working as".</item>
/// <item><see cref="AuthorizedPositions"/>: Management Team positions — a leadership position's name (e.g. "SchoolDirector",
/// "Sqam") or the title of a school-added position (e.g. "Student Affairs Manager"); whoever currently holds it has access.</item>
/// <item><see cref="AuthorizedRoles"/>: role names of a signed-in user, for when sign-in exists (ClaimsPrincipal.IsInRole).</item>
/// </list>
/// </summary>
public sealed class DutyAuditOptions
{
    public const string Section = "DutyAudit";

    public int[] AuthorizedStaffIds { get; set; } = [];
    public string[] AuthorizedPositions { get; set; } = [];
    public string[] AuthorizedRoles { get; set; } = [];
}

/// <summary>Thrown by the duty audit services when the current user is not authorized for Duty Audit.</summary>
public sealed class DutyAuditAccessDeniedException() : UnauthorizedAccessException(DutyAuditAccess.DeniedMessage);

public interface IDutyAuditAccess
{
    /// <summary>True when the person working in this request may use Duty Audit.</summary>
    Task<bool> CanAccessAsync(CancellationToken ct = default);

    /// <summary>Throws <see cref="DutyAuditAccessDeniedException"/> unless <see cref="CanAccessAsync"/>.</summary>
    async Task EnsureAsync(CancellationToken ct = default)
    {
        if (!await CanAccessAsync(ct)) throw new DutyAuditAccessDeniedException();
    }
}

/// <summary>
/// Duty Audit access for the current request: the Super Admin always; otherwise the signed-in user's roles (once sign-in
/// exists), else the staff member chosen under "Working as" — by id or by the Management Team position they currently hold.
/// Decided once per request.
/// </summary>
public sealed class DutyAuditAccess(IOptionsMonitor<DutyAuditOptions> options, ICurrentUser currentUser, IHttpContextAccessor http, SchoolDbContext db)
    : IDutyAuditAccess
{
    public const string DeniedMessage = "Duty Audit is open only to the Super Admin and to staff authorized for it. Ask the school administration for access.";

    private bool? _allowed;

    public async Task<bool> CanAccessAsync(CancellationToken ct = default) => _allowed ??= await DecideAsync(ct);

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
        if (leadership.Count > 0 && await db.PositionAssignments.AsNoTracking().AnyAsync(a => a.ManagementMemberId == staffId && a.IsCurrent && leadership.Contains(a.Position), ct))
            return true;
        var titles = configured.Select(TextNormalizer.NameKey).ToList();
        return await db.ManagementMembers.AsNoTracking().AnyAsync(m => m.Id == staffId && m.CustomPosition != null && titles.Contains(m.CustomPosition.NormalizedTitle), ct);
    }
}

/// <summary>
/// Duty Audit access for trusted code running outside a user's request: the test-data command line and the test suite's
/// direct service calls. Never registered for web requests.
/// </summary>
public sealed class TrustedDutyAuditAccess : IDutyAuditAccess
{
    public static readonly TrustedDutyAuditAccess Instance = new();

    public Task<bool> CanAccessAsync(CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>No Duty Audit access: what a duty service constructed without an access check uses.</summary>
public sealed class NoDutyAuditAccess : IDutyAuditAccess
{
    public static readonly NoDutyAuditAccess Instance = new();

    public Task<bool> CanAccessAsync(CancellationToken ct = default) => Task.FromResult(false);
}
