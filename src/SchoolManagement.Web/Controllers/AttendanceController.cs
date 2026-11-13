using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Students → Student Attendance: the landing page with its two sections (School Attendance at /arrivals,
/// unchanged, and Session Attendance), the per-lesson registers, the attendance alerts and school holidays.
/// </summary>
[Route("attendance")]
public class AttendanceController(
    IArrivalService arrivals, ISessionAttendanceService sessions, IAttendanceAlertService alerts,
    IAttendanceCalendarService calendar, IClassService classes) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        return View(new AttendanceHubViewModel
        {
            Date = day,
            School = await arrivals.DaySummaryAsync(day, ct),
            Sessions = await sessions.DayAsync(new SessionFilter(day), ct),
            Alerts = await alerts.CountsAsync(ct),
            RecentAlerts = (await alerts.ListAsync(new AlertFilter(), ct)).Take(5).ToList(),
        });
    }

    // ------------------------------------------------------------------ session attendance

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(string? date, int? classId, int? slotId, string? subject, int? teacherId, int? studentId, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var filter = new SessionFilter(day, classId, slotId, string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(), teacherId, studentId);
        return View(new SessionListViewModel
        {
            Day = await sessions.DayAsync(filter, ct),
            Options = await sessions.OptionsAsync(day, classId, ct),
        });
    }

    [HttpGet("sessions/{id:int}")]
    public async Task<IActionResult> Register(int id, string? date, string? back, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var register = await sessions.RegisterAsync(id, day, ct);
        if (register is null)
        {
            Toast($"This lesson is not on the timetable on {day.ToString("dddd d MMM yyyy", CultureInfo.InvariantCulture)}.", "warning");
            return LocalRedirect($"/attendance/sessions?date={SchoolWeek.Iso(day)}");
        }
        return View(new SessionRegisterViewModel { Register = register, Back = SafeBack(back) ?? $"/attendance/sessions?date={SchoolWeek.Iso(day)}" });
    }

    [HttpPost("sessions/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, SessionSaveForm form, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(form.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return Done(ServiceResult.Fail("", "Choose a valid date."), "", null);
        if (!ModelState.IsValid) return Done(ServiceResult.Fail("", FirstError()), "", null);
        var result = await sessions.SaveAsync(id, day, form.Entries.Select(e => new SessionEntry(e.StudentId, e.Status, e.Notes)).ToList(), form.Reason, ct);
        return Done(result, $"Lesson attendance saved ({result.EntityId} student{(result.EntityId == 1 ? "" : "s")}).",
            $"/attendance/sessions/{id}?date={SchoolWeek.Iso(day)}");
    }

    [HttpGet("sessions/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, string? date, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var register = await sessions.RegisterAsync(id, day, ct);
        if (register is null) return NotFoundResult("This lesson is not on the timetable on this date.");
        return PageOrPartial("Cancel", new LessonCancelModel { SessionId = id, Date = SchoolWeek.Iso(day), Lesson = register.Lesson });
    }

    [HttpPost("sessions/{id:int}/cancel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, LessonCancelModel model, CancellationToken ct)
    {
        model.SessionId = id;
        var day = SchoolWeek.ParseDayOrToday(model.Date);
        var register = await sessions.RegisterAsync(id, day, ct);
        if (register is null) return NotFoundResult("This lesson is not on the timetable on this date.");
        if (ModelState.IsValid)
        {
            var result = await sessions.CancelAsync(id, day, model.Reason, ct);
            if (result.Succeeded) return Done(result, $"{register.Lesson.Subject} ({register.Lesson.ClassName}, {register.Lesson.Period}) cancelled for this date.", $"/attendance/sessions/{id}?date={SchoolWeek.Iso(day)}");
            AddErrors(result);
        }
        model.Lesson = register.Lesson;
        return FormView("Cancel", model);
    }

    [HttpPost("sessions/{id:int}/restore"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id, string? date, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        return Done(await sessions.RestoreAsync(id, day, ct), "The lesson is back on for this date.", $"/attendance/sessions/{id}?date={SchoolWeek.Iso(day)}");
    }

    // ------------------------------------------------------------------ alerts

    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts(string? type, string? state, int? classId, string? search, CancellationToken ct)
    {
        var filter = new AlertFilter(
            type switch { "absences" => AttendanceAlertType.ConsecutiveAbsence, "lessons" => AttendanceAlertType.AbsentFromSession, _ => null },
            state is "resolved" or "all" ? state : null, classId, string.IsNullOrWhiteSpace(search) ? null : search.Trim());
        return View(new AlertListViewModel
        {
            Alerts = await alerts.ListAsync(filter, ct),
            Counts = await alerts.CountsAsync(ct),
            Filter = filter,
            Classes = await classes.AllOptionsAsync(ct),
        });
    }

    [HttpGet("alerts/{id:int}")]
    public async Task<IActionResult> Alert(int id, CancellationToken ct)
    {
        var details = await alerts.GetAsync(id, ct);
        if (details is null) return NotFoundResult("This alert no longer exists.");
        return PageOrPartial("Alert", new AlertActionModel { Id = id, Details = details });
    }

    [HttpPost("alerts/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Alert(int id, AlertActionModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var (result, message) = model.Action switch
            {
                "resolve" => (await alerts.ResolveAsync(id, model.Note, ct), "Alert resolved."),
                "reopen" => (await alerts.ReopenAsync(id, model.Note, ct), "Alert reopened."),
                _ => (await alerts.FollowUpAsync(id, model.Note, ct), "Follow-up added."),
            };
            if (result.Succeeded) return Done(result, message, "/attendance/alerts");
            AddErrors(result);
        }
        model.Details = await alerts.GetAsync(id, ct);
        if (model.Details is null) return NotFoundResult("This alert no longer exists.");
        return FormView("Alert", model);
    }

    [HttpPost("alerts/recheck"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Recheck(CancellationToken ct)
    {
        var changes = await alerts.RecheckAsync(SchoolWeek.Today.AddDays(-120), SchoolWeek.Today, ct);
        return Done(ServiceResult.Ok(), changes == 0 ? "Alerts are up to date: nothing changed." : $"Alerts re-checked: {changes} change(s).", "/attendance/alerts");
    }

    // ------------------------------------------------------------------ school holidays

    [HttpGet("holidays")]
    public async Task<IActionResult> Holidays(CancellationToken ct) =>
        View(new HolidaysViewModel { Holidays = await calendar.HolidaysAsync(ct) });

    [HttpPost("holidays"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddHoliday(HolidayForm form, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await calendar.AddHolidayAsync(form.Name, form.StartDate, form.EndDate, ct);
            if (result.Succeeded) return Done(result, $"Holiday “{form.Name?.Trim()}” added. Absence alerts around it were re-checked.", "/attendance/holidays");
            AddErrors(result);
        }
        if (IsAjax) return AjaxFailure(FirstError(), 422);
        return View("Holidays", new HolidaysViewModel { Holidays = await calendar.HolidaysAsync(ct), Form = form });
    }

    [HttpPost("holidays/{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteHoliday(int id, CancellationToken ct) =>
        Done(await calendar.RemoveHolidayAsync(id, ct), "Holiday removed. Absence alerts around it were re-checked.", "/attendance/holidays");

    // ------------------------------------------------------------------ helpers

    private string FirstError() =>
        ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault(e => !string.IsNullOrEmpty(e)) ?? "Check the form.";

    private static string? SafeBack(string? back) =>
        back is not null && back.StartsWith("/attendance/", StringComparison.Ordinal) && !back.StartsWith("//", StringComparison.Ordinal) ? back : null;

    /// <summary>AJAX: toast (+ refresh); otherwise redirect with a toast. Failures show the first error.</summary>
    private IActionResult Done(ServiceResult result, string success, string? redirect)
    {
        var message = result.Succeeded ? success : string.Join(" ", result.Errors.Select(e => e.Value).Distinct());
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 422);
        Toast(message, result.Succeeded ? "success" : "error");
        return LocalRedirect(redirect ?? "/attendance");
    }
}
