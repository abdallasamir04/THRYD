using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Accounts;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Staff → Accounts (Super Admin only): every non-student person with their account, role, responsibilities and review
/// status; create accounts, reset passwords (a new random password shown once), disable / reactivate, change role.
/// Every change is audited. No staff marker: the least-privilege filter refuses it to every other account, and every
/// action checks the Super Admin again.
/// </summary>
[Route("staff/accounts")]
public class StaffAccountsController(ICurrentUser currentUser, IPeopleDirectoryService people, IAccountProvisioningService provisioning,
    SchoolDbContext db) : AppController
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!currentUser.IsSuperAdmin) { context.Result = StaffAccessFilter.Denied(context); return; }
        Response.Headers.CacheControl = "no-store";
        await next();
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? status, string? department, string? role, string? category, string? setup, string? q, CancellationToken ct) =>
        View(new StaffAccountsListModel
        {
            Directory = await people.BuildAsync(null, ct), Status = status, Department = department, Role = role, Category = category, Setup = setup, Query = q,
        });

    [HttpGet("{staffId:int}")]
    public async Task<IActionResult> Details(int staffId, CancellationToken ct)
    {
        var dir = await people.BuildAsync(staffId, ct);
        if (dir.Person(staffId) is not { } person) return NotFound();
        var account = dir.AccountOf(staffId);
        var history = await db.AccountAuditEvents.AsNoTracking()
            .Where(e => e.TargetStaffMemberId == staffId || (account != null && e.TargetUserId == account.UserId))
            .OrderByDescending(e => e.At).Take(100).ToListAsync(ct);
        return View(new StaffAccountDetailsModel { Person = new PersonDashboardModel { Directory = dir, Person = person, Account = account }, History = history });
    }

    private IActionResult Credential(OneTimeCredentialModel model)
    {
        Response.Headers.CacheControl = "no-store";
        return View("Credential", model);
    }

    [HttpPost("{staffId:int}/create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int staffId, bool identityReviewed, CancellationToken ct)
    {
        var (result, credential) = await provisioning.CreateForPersonAsync(staffId, identityReviewed, ct);
        if (!result.Succeeded || credential is null)
        {
            Toast(string.Join(" ", result.Errors.Select(e => e.Value)), "error");
            return Redirect($"/staff/accounts/{staffId}");
        }
        return Credential(new OneTimeCredentialModel(credential.FullName, credential.Email, credential.Password, "Account created", staffId));
    }

    [HttpPost("users/{userId}/reset"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(string userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound();
        var (result, password) = await provisioning.ResetPasswordAsync(userId, ct);
        if (!result.Succeeded || password is null)
        {
            Toast(string.Join(" ", result.Errors.Select(e => e.Value)), "error");
            return Redirect(user.StaffMemberId is { } sid ? $"/staff/accounts/{sid}" : "/staff/accounts");
        }
        return Credential(new OneTimeCredentialModel(user.DisplayName, user.Email ?? "", password, "Password reset", user.StaffMemberId));
    }

    private async Task<IActionResult> Done(string userId, ServiceResult result, string message, CancellationToken ct)
    {
        var staffId = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.StaffMemberId).FirstOrDefaultAsync(ct);
        if (result.Succeeded) Toast(message); else Toast(string.Join(" ", result.Errors.Select(e => e.Value)), "error");
        return Redirect(staffId is { } sid ? $"/staff/accounts/{sid}" : "/staff/accounts");
    }

    [HttpPost("users/{userId}/disable"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(string userId, CancellationToken ct) =>
        await Done(userId, await provisioning.SetDisabledAsync(userId, true, ct), "Account disabled; its open session ends within a minute.", ct);

    [HttpPost("users/{userId}/enable"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable(string userId, CancellationToken ct) =>
        await Done(userId, await provisioning.SetDisabledAsync(userId, false, ct), "Account reactivated.", ct);

    [HttpPost("users/{userId}/role"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Role(string userId, string role, CancellationToken ct) =>
        await Done(userId, await provisioning.SetRoleAsync(userId, role, ct), $"Role changed to {role}.", ct);
}
