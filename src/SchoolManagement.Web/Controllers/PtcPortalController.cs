using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using SchoolManagement.Web.Services.Ptc;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// The public parent booking portal: /ptc-booking/{link code}. No staff account; only Published events open. A parent
/// identifies their child (registered parent number + the child's first name), chooses one Round and the subjects, and the
/// booking is confirmed at once. Every value is validated on the server (<see cref="IPtcPortalService"/>, which books through
/// <see cref="IPtcBookingService"/>). Rate limited per client address; nothing administrative is reachable from here.
/// </summary>
[Route("ptc-booking/{token}")]
[EnableRateLimiting(RequestPolicy)]
[AllowAnonymous]
public class PtcPortalController(IPtcPortalService portal) : Controller
{
    public const string RequestPolicy = "ptc-portal";
    public const string SubmitPolicy = "ptc-portal-submit";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        // Pages carry personal details and one-time codes: never cached, never sent on as a referrer, never indexed.
        var h = Response.Headers;
        h.CacheControl = "no-store";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Robots-Tag"] = "noindex, nofollow";
        base.OnActionExecuting(context);
    }

    // Links are built from the event's exact code: route generation would lower-case it (LowercaseUrls) and the code is case-sensitive.
    private static string BookHref(PortalEvent ev, string childToken) => $"/ptc-booking/{ev.Token}/book?c={Uri.EscapeDataString(childToken)}";

    private IActionResult Closed()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return View("Closed");
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string token, CancellationToken ct) =>
        await portal.EventAsync(token, ct) is { } ev ? View(new PtcPortalStartModel { Event = ev }) : Closed();

    [HttpPost("identify"), ValidateAntiForgeryToken, EnableRateLimiting(SubmitPolicy)]
    public async Task<IActionResult> Identify(string token, string? phone, string? firstName, CancellationToken ct)
    {
        if (await portal.EventAsync(token, ct) is not { } ev) return Closed();
        var result = await portal.IdentifyAsync(ev, phone, firstName, ct);
        if (result.Status != PortalIdentifyStatus.Found)
            return View("Index", new PtcPortalStartModel
            {
                Event = ev, Phone = phone, FirstName = firstName,
                Error = result.Status == PortalIdentifyStatus.Throttled ? PtcPortalService.ThrottledMessage : PtcPortalService.NotFoundMessage,
            });
        // One child: straight to the booking step. Siblings with the same registered number: choose the child first.
        return result.Children.Count == 1
            ? Redirect(BookHref(ev, result.Children[0].Token))
            : View("Children", new PtcPortalChildrenModel { Event = ev, Children = result.Children });
    }

    [HttpGet("book")]
    public async Task<IActionResult> Book(string token, string? c, CancellationToken ct)
    {
        if (await portal.EventAsync(token, ct) is not { } ev) return Closed();
        var page = await portal.BookingPageAsync(ev, c, ct);
        if (page is null) return View("Index", new PtcPortalStartModel { Event = ev, Error = "Your session has expired. Please enter your details again." });
        return View(new PtcPortalBookModel { Page = page });
    }

    [HttpPost("book"), ValidateAntiForgeryToken, EnableRateLimiting(SubmitPolicy)]
    public async Task<IActionResult> Book(string token, string? c, int? round, List<string>? subjects, CancellationToken ct)
    {
        if (await portal.EventAsync(token, ct) is not { } ev) return Closed();
        var selected = subjects ?? [];
        if (round is null)
            return await BookAgain(ev, c, null, selected, "Please choose one session.", ct);
        var result = await portal.BookAsync(ev, c, round.Value, selected, ct);
        if (result.ConfirmationToken is not null)
            return Redirect($"/ptc-booking/{ev.Token}/confirmation?b={Uri.EscapeDataString(result.ConfirmationToken)}" + (result.AlreadyBooked ? "&already=1" : ""));
        if (result.Restart) return View("Index", new PtcPortalStartModel { Event = ev, Error = result.Error });
        return await BookAgain(ev, c, round, selected, result.Error, ct);
    }

    private async Task<IActionResult> BookAgain(PortalEvent ev, string? c, int? round, List<string> subjects, string? error, CancellationToken ct)
    {
        var page = await portal.BookingPageAsync(ev, c, ct);
        if (page is null) return View("Index", new PtcPortalStartModel { Event = ev, Error = "Your session has expired. Please enter your details again." });
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return View("Book", new PtcPortalBookModel { Page = page, Round = round, Subjects = subjects, Error = error });
    }

    [HttpGet("confirmation")]
    public async Task<IActionResult> Confirmation(string token, string? b, int? already, CancellationToken ct)
    {
        if (await portal.EventAsync(token, ct) is not { } ev) return Closed();
        var confirmation = await portal.ConfirmationAsync(ev, b, ct);
        if (confirmation is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("Index", new PtcPortalStartModel { Event = ev, Error = "This confirmation link is not valid. Enter your details to see your booking." });
        }
        return View(new PtcPortalConfirmationModel { Event = ev, Confirmation = confirmation, AlreadyBooked = already == 1 });
    }
}
