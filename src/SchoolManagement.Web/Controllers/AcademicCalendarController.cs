using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Academics → Academic Calendar: academic years and their four quarters (8 school weeks each). Its own sidebar
/// item; it used to open from Belongings (/belongings/calendar, which now redirects here). Same service and data
/// as before (<see cref="IBelongingsService"/>): Belongings, attendance, timetables and teaching teams read these years.
/// </summary>
[Route("academic-calendar")]
public class AcademicCalendarController(IBelongingsService calendar) : AppController
{
    public const string Href = "/academic-calendar";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await calendar.YearsAsync(ct));

    [HttpPost("years"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateYear(string? name, CancellationToken ct) =>
        Done(await calendar.CreateYearAsync(name, ct), $"Academic year {name?.Trim()} added with four quarters. Set each quarter's dates.");

    [HttpGet("quarters/{id:int}/edit")]
    public async Task<IActionResult> EditQuarter(int id, CancellationToken ct)
    {
        var q = await calendar.QuarterAsync(id, ct);
        if (q is null) return NotFoundResult("This quarter no longer exists.");
        return PageOrPartial("QuarterForm", new QuarterFormModel { Id = id, Name = q.Name, StartDate = q.StartDate, EndDate = q.EndDate, Quarter = q });
    }

    [HttpPost("quarters/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditQuarter(int id, QuarterFormModel model, CancellationToken ct)
    {
        model.Id = id;
        model.Quarter = await calendar.QuarterAsync(id, ct);
        if (model.Quarter is null) return NotFoundResult("This quarter no longer exists.");
        if (ModelState.IsValid)
        {
            var result = await calendar.UpdateQuarterAsync(id, model.Name, model.StartDate, model.EndDate, ct);
            if (result.Succeeded) return Done(result, $"{model.Name.Trim()} saved: 8 school weeks.");
            AddErrors(result);
        }
        return FormView("QuarterForm", model);
    }

    /// <summary>AJAX: toast + page refresh, or the first error as an error toast. Otherwise back to the calendar with a toast.</summary>
    private IActionResult Done(ServiceResult result, string success)
    {
        var message = result.Succeeded ? success : string.Join(" ", result.Errors.Select(e => e.Value).Distinct());
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 422);
        Toast(message, result.Succeeded ? "success" : "error");
        return LocalRedirect(Href);
    }
}
