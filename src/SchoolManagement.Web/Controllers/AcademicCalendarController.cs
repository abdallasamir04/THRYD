using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Academics → School Calendar (formerly "Academic Calendar"; /academic-calendar still works). The year's
/// teachers' calendar by quarter and month: coloured dates and each month's event lines
/// (<see cref="ISchoolCalendarService"/>). Below it, the academic years and their four 8-week quarters, which
/// Belongings, attendance, timetables and teaching teams read (<see cref="IBelongingsService"/>, unchanged).
/// </summary>
[Route("school-calendar")]
[Route("academic-calendar")]
public class AcademicCalendarController(IBelongingsService calendar, ISchoolCalendarService schoolCalendar, ISettingsService settings) : AppController
{
    public const string Href = "/school-calendar";

    [HttpGet("")]
    public async Task<IActionResult> Index(int? year, CancellationToken ct)
    {
        var page = await schoolCalendar.PageAsync(year, ct);
        var profile = await settings.GetProfileAsync(ct);
        return View(new SchoolCalendarIndexModel(page, await calendar.YearsAsync(ct), profile.SchoolName));
    }

    // ------------------------------------------------------------------ academic years and quarters

    [HttpPost("years"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateYear(string? name, CancellationToken ct) =>
        Done(await calendar.CreateYearAsync(name, ct), $"Academic year {name?.Trim()} added with four quarters. Set each quarter's dates.");

    [HttpGet("quarters/{id:int}/edit")]
    public async Task<IActionResult> EditQuarter(int id, CancellationToken ct)
    {
        var q = await calendar.QuarterAsync(id, ct);
        if (q is null) return NotFoundResult("This quarter no longer exists.");
        var (cs, ce) = await schoolCalendar.QuarterDatesAsync(id, ct);
        return PageOrPartial("QuarterForm", new QuarterFormModel
        {
            Id = id, Name = q.Name, StartDate = q.StartDate, EndDate = q.EndDate, CalendarStartDate = cs, CalendarEndDate = ce, Quarter = q,
        });
    }

    [HttpPost("quarters/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditQuarter(int id, QuarterFormModel model, CancellationToken ct)
    {
        model.Id = id;
        model.Quarter = await calendar.QuarterAsync(id, ct);
        if (model.Quarter is null) return NotFoundResult("This quarter no longer exists.");
        foreach (var (k, v) in SchoolCalendarService.ValidateQuarterDates(model.CalendarStartDate, model.CalendarEndDate)) ModelState.AddModelError(k, v);
        if (ModelState.IsValid)
        {
            var result = await calendar.UpdateQuarterAsync(id, model.Name, model.StartDate, model.EndDate, ct);
            if (result.Succeeded) result = await schoolCalendar.UpdateQuarterDatesAsync(id, model.CalendarStartDate, model.CalendarEndDate, ct);
            if (result.Succeeded) return Done(result, $"{model.Name.Trim()} saved: 8 school weeks.");
            AddErrors(result);
        }
        return FormView("QuarterForm", model);
    }

    // ------------------------------------------------------------------ calendar events

    [HttpGet("events/new")]
    public async Task<IActionResult> CreateEvent(int year, DateOnly? date, CancellationToken ct)
    {
        var name = await schoolCalendar.YearNameAsync(year, ct);
        if (name is null) return NotFoundResult("This academic year no longer exists.");
        return PageOrPartial("EventForm", await Fill(new CalendarEventFormModel { YearId = year, StartDate = date, SortOrder = 100 }, ct));
    }

    [HttpPost("events/new"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateEvent(CalendarEventFormModel model, CancellationToken ct) => SaveEvent(null, model, ct);

    [HttpGet("events/{id:int}/edit")]
    public async Task<IActionResult> EditEvent(int id, CancellationToken ct)
    {
        var e = await schoolCalendar.EventAsync(id, ct);
        if (e is null) return NotFoundResult("This event no longer exists.");
        return PageOrPartial("EventForm", await Fill(CalendarEventFormModel.From(e), ct));
    }

    [HttpPost("events/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditEvent(int id, CalendarEventFormModel model, CancellationToken ct)
    {
        var e = await schoolCalendar.EventAsync(id, ct);
        if (e is null) return NotFoundResult("This event no longer exists.");
        model.YearId = e.YearId;
        return await SaveEvent(id, model, ct);
    }

    [HttpPost("events/{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEvent(int id, CancellationToken ct) =>
        Done(await schoolCalendar.DeleteEventAsync(id, ct), "Event deleted from the School Calendar.");

    private async Task<IActionResult> SaveEvent(int? id, CalendarEventFormModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await schoolCalendar.SaveEventAsync(id, model.YearId, model.ToInput(), ct);
            if (result.Succeeded) return Done(result, $"{model.Title.Trim()} saved.", model.YearId);
            AddErrors(result);
        }
        return FormView("EventForm", await Fill(model, ct));
    }

    private async Task<CalendarEventFormModel> Fill(CalendarEventFormModel model, CancellationToken ct)
    {
        model.YearName = await schoolCalendar.YearNameAsync(model.YearId, ct) ?? "";
        model.Months = await schoolCalendar.MonthsAsync(model.YearId, ct);
        return model;
    }

    // ------------------------------------------------------------------ one day: its events and its colour

    [HttpGet("days/{date}")]
    public async Task<IActionResult> Day(DateOnly date, int year, CancellationToken ct)
    {
        var day = await schoolCalendar.DayAsync(year, date, ct);
        if (day is null) return NotFoundResult("This academic year no longer exists.");
        return PageOrPartial("Day", day);
    }

    [HttpPost("marks"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SetMark(int year, DateOnly date, CalendarColor color, CancellationToken ct) =>
        Done(await schoolCalendar.SetMarkAsync(year, date, color, ct),
            color == CalendarColor.None ? $"{date:d MMM yyyy} no longer coloured." : $"{date:d MMM yyyy} coloured {CalendarColors.Name(color).ToLowerInvariant()}.", year);

    // ------------------------------------------------------------------ the printed 2026/2027 calendar

    [HttpPost("years/{id:int}/load-reference"), ValidateAntiForgeryToken]
    public async Task<IActionResult> LoadReference(int id, CancellationToken ct)
    {
        var r = await schoolCalendar.LoadReferenceAsync(id, ct);
        var message = $"Loaded {r.Events} events and {r.Marks} coloured dates from the 2026/2027 teachers' calendar." +
            (r.Kept.Count > 0 ? $" Kept your own school calendar dates for {string.Join(", ", r.Kept)}." : "");
        return Done(r.Result, message, id);
    }

    /// <summary>AJAX: toast + page refresh, or the first error as an error toast. Otherwise back to the calendar with a toast.</summary>
    private IActionResult Done(ServiceResult result, string success, int? year = null)
    {
        var message = result.Succeeded ? success : string.Join(" ", result.Errors.Select(e => e.Value).Distinct());
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 422);
        Toast(message, result.Succeeded ? "success" : "error");
        return LocalRedirect(year is null ? Href : $"{Href}?year={year}");
    }
}
