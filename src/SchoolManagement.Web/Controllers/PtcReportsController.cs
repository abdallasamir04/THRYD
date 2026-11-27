using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.Ptc;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// PTC Phase 7 administration: parent feedback and concerns, feedback links of an event, the analytics dashboard, teacher
/// reports and the Excel export. Super Admin only (<see cref="IPtcAccess"/>, the same check as all PTC administration):
/// refused (403) for anyone else before anything runs, and checked again in the services.
/// </summary>
[Route("ptc")]
public class PtcReportsController(IPtcAccess access, IPtcAdminService admin, IPtcFeedbackService feedback, IPtcReportService reports,
    IOptions<PtcPortalOptions> portal) : AppController
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!access.CanManage)
        {
            if (IsAjax) context.Result = AjaxFailure(PtcAccess.DeniedMessage, StatusCodes.Status403Forbidden);
            else
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Result = View("~/Views/Ptc/Denied.cshtml");
            }
            return;
        }
        await next();
    }

    private string Site => string.IsNullOrWhiteSpace(portal.Value.PublicBaseUrl) ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}" : portal.Value.PublicBaseUrl!;

    private IActionResult Back(ServiceResult result, string success, string href)
    {
        if (result.Succeeded) Toast(success);
        else Toast(string.Join(" ", result.Errors.Select(e => e.Value).Distinct().Take(3)), "error");
        return Redirect(href);
    }

    // ================================================================== feedback management

    [HttpGet("feedback")]
    public async Task<IActionResult> Feedback([FromQuery] PtcFeedbackFilter filter, CancellationToken ct) =>
        View("~/Views/PtcReports/Feedback.cshtml", new PtcFeedbackListModel { Filter = filter, List = await feedback.ListAsync(filter, ct) });

    [HttpGet("feedback/{id:int}")]
    public async Task<IActionResult> FeedbackDetails(int id, CancellationToken ct) =>
        await feedback.DetailsAsync(id, ct) is { } d ? View("~/Views/PtcReports/FeedbackDetails.cshtml", d) : NotFound();

    private static string Detail(int id) => $"/ptc/feedback/{id}";

    [HttpPost("feedback/{id:int}/review"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(int id, string? version, CancellationToken ct) =>
        Back(await feedback.MarkReviewedAsync(id, version, ct), "Marked as reviewed.", Detail(id));

    [HttpPost("feedback/{id:int}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ConcernStatus(int id, PtcConcernStatus status, string? note, string? version, CancellationToken ct) =>
        Back(await feedback.SetConcernStatusAsync(id, status, note, version, ct), "Concern status changed (recorded in the history).", Detail(id));

    [HttpPost("feedback/{id:int}/note"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Note(int id, string? note, CancellationToken ct) =>
        Back(await feedback.AddNoteAsync(id, note, ct), "Internal note added. It is never shown to parents.", Detail(id));

    [HttpPost("feedback/{id:int}/assign"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int id, int? staffId, string? version, CancellationToken ct) =>
        Back(await feedback.AssignAsync(id, staffId, version, ct), staffId is null ? "Assignment removed." : "Concern assigned.", Detail(id));

    [HttpPost("feedback/{id:int}/resolve"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(int id, string? resolution, string? version, CancellationToken ct) =>
        Back(await feedback.ResolveAsync(id, resolution, version, ct), "Concern resolved.", Detail(id));

    [HttpPost("feedback/{id:int}/reopen"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(int id, string? reason, string? version, CancellationToken ct) =>
        Back(await feedback.ReopenAsync(id, reason, version, ct), "Concern reopened.", Detail(id));

    [HttpGet("events/{id:int}/feedback")]
    public async Task<IActionResult> EventFeedback(int id, CancellationToken ct)
    {
        if (await admin.EventAsync(id, ct) is not { } e || await feedback.LinksAsync(id, Site, ct) is not { } links) return NotFound();
        Response.Headers.CacheControl = "no-store";                                              // the page holds parent-specific links
        return View("~/Views/PtcReports/EventFeedback.cshtml", new PtcEventFeedbackModel { Event = e, Links = links });
    }

    // ================================================================== reports

    [HttpGet("reports")]
    public async Task<IActionResult> Reports([FromQuery] PtcReportFilter filter, CancellationToken ct) =>
        View("~/Views/PtcReports/Index.cshtml", await reports.BuildAsync(filter, ct));

    [HttpGet("reports/teachers")]
    public async Task<IActionResult> Teachers([FromQuery] PtcReportFilter filter, CancellationToken ct) =>
        View("~/Views/PtcReports/Teachers.cshtml", await reports.BuildAsync(filter, ct));

    [HttpGet("reports/teachers/{teacherId:int}")]
    public async Task<IActionResult> Teacher(int teacherId, [FromQuery] PtcReportFilter filter, CancellationToken ct) =>
        await reports.TeacherAsync(teacherId, filter, ct) is { } t ? View("~/Views/PtcReports/Teacher.cshtml", t) : NotFound();

    [HttpGet("reports/export")]
    public async Task<IActionResult> Export([FromQuery] PtcReportFilter filter, CancellationToken ct)
    {
        var data = await reports.BuildAsync(filter, ct);
        using var wb = PtcReportExport.Build(data);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        Response.Headers.CacheControl = "no-store";
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            PtcReportExport.FileName(SchoolTime.Current.ToLocal(data.GeneratedUtc)));
    }
}
