using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using SchoolManagement.Web.Services.Ptc;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// The public parent feedback pages (no account, no sign-in, no OTP). /ptc-feedback/{event link code}: the parent identifies
/// their child exactly as for booking (registered number + first name). /ptc-feedback/f/{code}: the feedback form of one
/// booking, from a signed, time-limited code (also the parent-specific link the Admin can copy and share by hand). Every
/// value is checked on the server (<see cref="IPtcFeedbackService"/>); failures are generic; rate limited per address.
/// </summary>
[Route("ptc-feedback")]
[EnableRateLimiting(PtcPortalController.RequestPolicy)]
[AllowAnonymous]
public class PtcFeedbackController(IPtcFeedbackService feedback) : Controller
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var h = Response.Headers;
        h.CacheControl = "no-store";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Robots-Tag"] = "noindex, nofollow";
        base.OnActionExecuting(context);
    }

    private IActionResult Invalid()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return View("Invalid");
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> Index(string token, CancellationToken ct) =>
        await feedback.EventAsync(token, ct) is { } ev ? View(new PtcFeedbackStartModel { Event = ev }) : Invalid();

    [HttpPost("{token}/identify"), ValidateAntiForgeryToken, EnableRateLimiting(PtcPortalController.SubmitPolicy)]
    public async Task<IActionResult> Identify(string token, string? phone, string? firstName, CancellationToken ct)
    {
        if (await feedback.EventAsync(token, ct) is not { } ev) return Invalid();
        var result = await feedback.IdentifyAsync(ev, phone, firstName, ct);
        if (result.Status != PortalIdentifyStatus.Found)
            return View("Index", new PtcFeedbackStartModel
            {
                Event = ev, Phone = phone, FirstName = firstName,
                Error = result.Status == PortalIdentifyStatus.Throttled ? PtcPortalService.ThrottledMessage : PtcPortalService.NotFoundMessage,
            });
        return View("Children", new PtcFeedbackChildrenModel { Event = ev, Children = result.Children });
    }

    [HttpGet("f/{code}")]
    public async Task<IActionResult> Form(string code, CancellationToken ct) =>
        await feedback.FormAsync(code, ct) is { } form ? View(new PtcFeedbackFormModel { Form = form }) : Invalid();

    [HttpPost("f/{code}"), ValidateAntiForgeryToken, EnableRateLimiting(PtcPortalController.SubmitPolicy)]
    public async Task<IActionResult> Submit(string code, PtcFeedbackPost post, CancellationToken ct)
    {
        var input = new PtcFeedbackInput(post.Overall, post.Communication, post.Organization, post.Helpful, post.Comments, post.ConcernCategory, post.Concern,
            (post.Teachers ?? []).Select(t => new PtcTeacherFeedbackInput(t.Code ?? "", t.Communication, t.Helpful, t.Comment)).ToList());
        var result = await feedback.SubmitAsync(code, input, ct);
        if (result.Succeeded) return Redirect($"/ptc-feedback/f/{Uri.EscapeDataString(code)}?sent=1");
        if (await feedback.FormAsync(code, ct) is not { } form) return Invalid();
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return View("Form", new PtcFeedbackFormModel { Form = form, Post = post, Error = result.Error });
    }
}
