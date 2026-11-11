using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// "Working as": the staff member this browser records changes under (Created by / Recorded by).
/// Self-selected and not verified — a stand-in until sign-in exists.
/// </summary>
public class OperatorController(SchoolDbContext db) : AppController
{
    [HttpPost("/operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Set(int? staffId, string? returnUrl, CancellationToken ct)
    {
        if (staffId is { } id && await db.StaffMembers.AnyAsync(s => s.Id == id, ct))
        {
            Response.Cookies.Append(HttpCurrentUser.OperatorCookie, id.ToString(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddDays(180),
            });
            Toast("Your name will be recorded on the changes you make in this browser.");
        }
        else
        {
            Response.Cookies.Delete(HttpCurrentUser.OperatorCookie);
            Toast("Cleared. Changes will be recorded without a name.", "info");
        }
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/settings");
    }
}
