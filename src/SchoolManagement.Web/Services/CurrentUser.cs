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
    string? Name { get; }
    int? StaffId { get; }
    bool IsVerified { get; }
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
