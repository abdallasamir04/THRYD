using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>
/// Who may add, edit, transfer or delete students (the student list, profiles and their details). Configured in the
/// "StudentRecords" section of appsettings.json (or environment variables), like Duty Audit:
/// <list type="bullet">
/// <item><see cref="AuthorizedStaffIds"/>: staff members (StaffMembers.Id), as chosen under "Working as".</item>
/// <item><see cref="AuthorizedPositions"/>: a Management Team leadership position's name ("SchoolDirector"), a school-added
/// position title, or an organizational position title (e.g. "School Receptionist"); whoever currently holds it may edit.</item>
/// <item><see cref="AuthorizedRoles"/>: role names of a signed-in user, for when sign-in exists.</item>
/// </list>
/// The Super Admin always may. While all three lists are empty the student records keep their existing rule — anyone using
/// the application may edit them — so configuring the section is what restricts editing. Viewing is not changed.
/// </summary>
public sealed class StudentRecordsOptions
{
    public const string Section = "StudentRecords";

    public int[] AuthorizedStaffIds { get; set; } = [];
    public string[] AuthorizedPositions { get; set; } = [];
    public string[] AuthorizedRoles { get; set; } = [];

    public bool IsRestricted =>
        (AuthorizedStaffIds?.Length ?? 0) > 0 || (AuthorizedPositions ?? []).Any(p => !string.IsNullOrWhiteSpace(p))
        || (AuthorizedRoles ?? []).Any(r => !string.IsNullOrWhiteSpace(r));
}

public interface IStudentRecordsAccess
{
    /// <summary>True when the person working in this request may change student records.</summary>
    Task<bool> CanEditAsync(CancellationToken ct = default);
}

public sealed class StudentRecordsAccess(IOptionsMonitor<StudentRecordsOptions> options, ICurrentUser currentUser, IHttpContextAccessor http, SchoolDbContext db)
    : IStudentRecordsAccess
{
    public const string DeniedMessage = "Only the Super Admin and staff authorized for student records can add or change students. Ask the school administration for access.";

    private bool? _allowed;

    public async Task<bool> CanEditAsync(CancellationToken ct = default) => _allowed ??= await DecideAsync(ct);

    private async Task<bool> DecideAsync(CancellationToken ct)
    {
        if (currentUser.IsSuperAdmin) return true;
        var o = options.CurrentValue;
        if (!o.IsRestricted) return true;                                   // existing rule: not restricted until configured
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
        return await db.ManagementMembers.AsNoTracking().AnyAsync(m => m.Id == staffId && m.CustomPosition != null && titles.Contains(m.CustomPosition.NormalizedTitle), ct)
            || await db.StaffPositionAssignments.AsNoTracking().AnyAsync(a => a.StaffMemberId == staffId && a.IsCurrent && titles.Contains(a.Position.NormalizedTitle), ct);
    }
}
