using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;

namespace SchoolManagement.Web.Services;

/// <summary>
/// Who is making a change, for "Created by / Recorded by / Updated by" fields.
/// With a signed-in user (once authentication exists) this is the user's name. Until then it is
/// the staff member chosen under "Working as" (a cookie on this browser): self-selected, not verified.
/// </summary>
public interface ICurrentUser
{
    /// <summary>The role that will mark a signed-in Super Admin once sign-in exists.</summary>
    public const string SuperAdminRole = "SuperAdmin";

    string? Name { get; }
    int? StaffId { get; }
    bool IsVerified { get; }

    /// <summary>
    /// The Super Admin: full administrative access (Duty Audit, Floor Audit management and every module) without a staff
    /// identity. The application has no sign-in, so this is the Administration mode — a request with no staff member chosen
    /// under "Working as" (it always was the app's administrator view). Choosing a staff member under "Working as" leaves
    /// it, and that person's own permissions apply. Once sign-in exists, a signed-in user in the <see cref="SuperAdminRole"/>
    /// role. Not secure while there is no sign-in: anyone using the app without "Working as" is the Super Admin.
    /// </summary>
    bool IsSuperAdmin => false;
}

public static class CurrentUserNames
{
    public const string SuperAdmin = "Super Admin";

    /// <summary>
    /// The name recorded for whoever is working now: the signed-in user's name, else the "Working as" staff member, else
    /// "Super Admin" for the Administration mode (no staff record — none is invented). Null only outside a request.
    /// </summary>
    public static string? AccountName(this ICurrentUser user) => user.Name ?? (user.IsSuperAdmin ? SuperAdmin : null);
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor, SchoolDbContext db) : ICurrentUser
{
    public const string OperatorCookie = "sm.operator";

    private bool _loaded;
    private string? _name;
    private int? _staffId;

    public string? Name { get { Load(); return _name; } }
    public int? StaffId { get { Load(); return _staffId; } }
    public bool IsVerified => accessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public bool IsSuperAdmin
    {
        get
        {
            var http = accessor.HttpContext;
            if (http is null) return false;                                     // background work is never the Super Admin
            if (http.User.Identity?.IsAuthenticated == true) return http.User.IsInRole(ICurrentUser.SuperAdminRole);
            return StaffId is null;                                             // Administration mode: no "Working as" identity
        }
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var http = accessor.HttpContext;
        if (http is null) return;
        if (http.User.Identity is { IsAuthenticated: true } identity)
        {
            _name = identity.Name;
            return;
        }
        if (int.TryParse(http.Request.Cookies[OperatorCookie], out var id))
        {
            var staff = db.StaffMembers.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.Id, s.FullName }).FirstOrDefault();
            if (staff is not null)
            {
                _staffId = staff.Id;
                _name = staff.FullName;
            }
        }
    }
}
