using SchoolManagement.Web.Services.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// "My PTC": a teacher's own PTC interviews and the interview timer. The teacher is the staff member of this request's
/// identity (<see cref="ICurrentUser.StaffId"/>), resolved on the server; no teacher id is ever taken from the request, and
/// every Start / End checks that the interview is theirs (<see cref="IPtcOperationsService"/>). Times come from the server.
/// </summary>
[Route("ptc/my")]
[StaffAccess(StaffAccessRule.Self)]
public class PtcTeacherController(IPtcOperationsService operations, ICurrentUser currentUser) : AppController
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (currentUser.StaffId is null)
        {
            // The Super Admin has no staff identity: the live operations view is theirs. Anyone else: no access.
            if (currentUser.IsSuperAdmin) { context.Result = View("NoStaff"); return; }
            Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Result = IsAjax ? AjaxFailure("Choose who you are working as to see your PTC interviews.", 403) : View("NoStaff");
            return;
        }
        await next();
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(DateOnly? date, CancellationToken ct) =>
        await operations.TeacherDashboardAsync(date, ct) is { } model ? View(model) : View("NoStaff");

    [HttpPost("start"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(string? key, DateOnly? date, CancellationToken ct) => Done(await operations.StartAsync(key, ct), date);

    [HttpPost("end"), ValidateAntiForgeryToken]
    public async Task<IActionResult> End(string? key, DateOnly? date, CancellationToken ct) => Done(await operations.EndAsync(key, ct), date);

    /// <summary>JSON with the authoritative server times for fetch(); otherwise back to the dashboard with a message.</summary>
    private IActionResult Done(PtcTimerResult r, DateOnly? date)
    {
        if (IsAjax)
        {
            if (!r.Succeeded) Response.StatusCode = r.Error == PtcOperationsService.NotYoursMessage ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict;
            return Json(new { success = r.Succeeded, message = r.Error, alreadyDone = r.AlreadyDone, actualStart = r.ActualStartUtc, actualEnd = r.ActualEndUtc, state = r.State?.ToString() });
        }
        if (r.Succeeded) Toast(r.AlreadyDone ? "Already recorded: the original time is kept." : r.ActualEndUtc is null ? "Interview started." : "Interview ended.");
        else Toast(r.Error ?? "The interview could not be updated.", "error");
        return Redirect(date is { } d ? $"/ptc/my?date={d:yyyy-MM-dd}" : "/ptc/my");
    }
}
