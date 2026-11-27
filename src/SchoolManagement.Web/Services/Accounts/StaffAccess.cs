using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Services.OrgStructure;

namespace SchoolManagement.Web.Services.Accounts;

/// <summary>Why a signed-in staff member (not the Super Admin) may open a controller or action.</summary>
public enum StaffAccessRule
{
    /// <summary>Their own data (dashboard, own PTC interviews, own account): every staff account.</summary>
    Self = 1,

    /// <summary>Floor Management / Floor Audit: a current floor-team assignment or a Floor Audit access grant.</summary>
    FloorTeam = 2,

    /// <summary>Duties and Duty Audit: the configured Duty Audit grant ("DutyAudit" section).</summary>
    DutyAudit = 3,

    /// <summary>Management / Organizational Structure: the configured Org Structure grant ("OrgStructure" section).</summary>
    OrgStructure = 4,

    /// <summary>Students and Classes: the configured Student Records grant ("StudentRecords" section).</summary>
    StudentRecords = 5,
}

/// <summary>
/// Marks a controller or action a signed-in staff member may open under <see cref="Rule"/>. Anything without it is Super Admin
/// only (least privilege): <see cref="StaffAccessFilter"/> refuses it for every other account. The services inside still
/// apply their own checks (own records only, assigned floors only…).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class StaffAccessAttribute(StaffAccessRule rule) : Attribute
{
    public StaffAccessRule Rule { get; } = rule;
}

/// <summary>Which modules the current staff member may open (for the filter and the navigation).</summary>
public sealed record StaffAccessSummary(bool FloorTeam, bool DutyAudit, bool OrgStructure, bool StudentRecords);

public interface IStaffAccessEvaluator
{
    Task<bool> AllowsAsync(StaffAccessRule rule, CancellationToken ct = default);
    Task<StaffAccessSummary> SummaryAsync(CancellationToken ct = default);
}

public sealed class StaffAccessEvaluator(SchoolDbContext db, ICurrentUser user, IDutyAuditAccess dutyAudit, IOrgStructureAccess orgStructure,
    IStudentRecordsAccess studentRecords, IOptionsMonitor<StudentRecordsOptions> studentRecordsOptions) : IStaffAccessEvaluator
{
    public async Task<bool> AllowsAsync(StaffAccessRule rule, CancellationToken ct = default)
    {
        if (user.IsSuperAdmin) return true;
        if (user.StaffId is not { } me) return false;
        return rule switch
        {
            StaffAccessRule.Self => true,
            StaffAccessRule.FloorTeam => await db.FloorStaffAssignments.AnyAsync(a => a.StaffMemberId == me && a.IsCurrent, ct)
                                         || await db.FloorAuditAccess.AnyAsync(a => a.StaffMemberId == me, ct),
            StaffAccessRule.DutyAudit => await dutyAudit.CanAccessAsync(ct),
            StaffAccessRule.OrgStructure => await orgStructure.CanManageAsync(ct),
            // Least privilege: Student Records is "open until configured" for its own edit checks, but a staff account opens
            // Students / Classes only when the grant is actually configured and includes them.
            StaffAccessRule.StudentRecords => studentRecordsOptions.CurrentValue.IsRestricted && await studentRecords.CanEditAsync(ct),
            _ => false,
        };
    }

    public async Task<StaffAccessSummary> SummaryAsync(CancellationToken ct = default) => new(
        await AllowsAsync(StaffAccessRule.FloorTeam, ct), await AllowsAsync(StaffAccessRule.DutyAudit, ct),
        await AllowsAsync(StaffAccessRule.OrgStructure, ct), await AllowsAsync(StaffAccessRule.StudentRecords, ct));
}

/// <summary>
/// Server-side least-privilege gate for every MVC action once sign-in is required: the Super Admin passes everything; an
/// anonymous endpoint ([AllowAnonymous]) passes; a staff account passes only a controller / action marked
/// <see cref="StaffAccessAttribute"/> whose rule it meets; everything else is refused (403). Hidden links are never the guard.
/// </summary>
public sealed class StaffAccessFilter(IOptionsMonitor<AccountOptions> options, ICurrentUser user, IStaffAccessEvaluator evaluator) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!options.CurrentValue.RequireAuthentication) return;                       // tests / legacy mode: unchanged behaviour
        var metadata = context.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any()) return;
        if (context.HttpContext.User.Identity?.IsAuthenticated != true) return; // the fallback policy challenges (login page)
        if (user.IsSuperAdmin) return;

        // The most specific marker wins (action over controller).
        var rule = metadata.OfType<StaffAccessAttribute>().LastOrDefault()?.Rule;
        if (rule is { } r && await evaluator.AllowsAsync(r, context.HttpContext.RequestAborted)) return;

        context.Result = Denied(context);
    }

    public static IActionResult Denied(FilterContext context)
    {
        const string message = "Your account does not have access to this page. Ask the Super Admin if you need it.";
        if (context.HttpContext.Request.IsAjax())
            return new JsonResult(new { success = false, message }) { StatusCode = StatusCodes.Status403Forbidden };
        var services = context.HttpContext.RequestServices;
        return new ViewResult
        {
            ViewName = "~/Views/Account/Denied.cshtml",
            StatusCode = StatusCodes.Status403Forbidden,
            ViewData = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), context.ModelState) { ["Message"] = message },
        };
    }
}
