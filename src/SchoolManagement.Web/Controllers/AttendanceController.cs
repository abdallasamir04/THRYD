using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
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

    /// <summary>Session Attendance: choose a class (today by default; earlier dates for history).</summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(string? date, int? classId, int? studentId, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        // Older links with filters open the class page or the student's history.
        if (classId is { } cid) return LocalRedirect($"/attendance/sessions/class/{cid}?date={SchoolWeek.Iso(day)}");
        if (studentId is { } sid) return LocalRedirect($"/attendance/sessions/reports?mode=student&studentId={sid}&from={SchoolWeek.Iso(day)}&to={SchoolWeek.Iso(day)}");
        var schoolDay = await arrivals.DayAsync(day, ct);
        return View(new SessionPickerViewModel
        {
            Date = day,
            Cards = await sessions.ClassesAsync(day, ct),
            Students = (await sessions.OptionsAsync(day, null, ct)).Students,
            IsSchoolDay = schoolDay.IsSchoolDay,
            Holiday = schoolDay.Holiday,
        });
    }

    /// <summary>A class's lessons on a date from the timetable; today the lesson in progress opens automatically.</summary>
    [HttpGet("sessions/class/{classId:int}")]
    public async Task<IActionResult> Class(int classId, string? date, int? lesson, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var classDay = await sessions.ClassDayAsync(classId, day, lesson, ct);
        if (classDay is null) return NotFoundResult("This class no longer exists.");
        if (lesson is not null && classDay.Selected is null)
            Toast($"That lesson is not on {classDay.ClassName}'s timetable on {day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}.", "warning");
        var model = new SessionClassViewModel
        {
            Day = classDay,
            Register = classDay.Selected is { } sel ? await sessions.LiveRegisterAsync(sel.Lesson.SessionId, day, ct) : null,
        };
        return IsAjax ? PartialView("_SessionRegister", model) : View("SessionClass", model);
    }

    /// <summary>A lesson's register (older links): opens its class page with that lesson.</summary>
    [HttpGet("sessions/{id:int}")]
    public async Task<IActionResult> Register(int id, string? date, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var register = await sessions.RegisterAsync(id, day, ct);
        if (register is null)
        {
            Toast($"This lesson is not on the timetable on {day.ToString("dddd d MMM yyyy", CultureInfo.InvariantCulture)}.", "warning");
            return LocalRedirect($"/attendance/sessions?date={SchoolWeek.Iso(day)}");
        }
        return LocalRedirect($"/attendance/sessions/class/{register.Lesson.ClassId}?date={SchoolWeek.Iso(day)}&lesson={id}");
    }

    /// <summary>One click: Present, Late or Absent for one student in one lesson (saved at once; the alert with it).</summary>
    [HttpPost("sessions/{id:int}/mark"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Mark(int id, string? date, int studentId, SessionAttendanceStatus status, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return AjaxFailure("Choose a valid date.", 422);
        var r = await sessions.MarkAsync(id, day, studentId, status, ct);
        if (!IsAjax)
        {
            Toast(r.AlertText is null ? r.Message : $"{r.Message} Alert raised: {r.AlertText}", r.Saved ? (r.AlertText is null ? "success" : "warning") : "error");
            return LocalRedirect($"/attendance/sessions/{id}?date={SchoolWeek.Iso(day)}");
        }
        if (!r.Saved) return AjaxFailure(r.Message, 409);
        return Json(new
        {
            success = true, message = r.Message, alert = r.AlertText,
            row = r.Row is null ? null : new
            {
                studentId = r.Row.StudentId, status = r.Row.Status?.ToString(), label = SessionStatuses.Label(r.Row.Status),
                time = r.Row.RecordedAt is { } at ? SchoolTime.Current.ToLocal(at).ToString("h:mm tt", CultureInfo.InvariantCulture) : null,
                by = r.Row.RecordedBy, alerted = r.Row.Alert, corrections = r.Row.Corrections,
            },
            counts = r.Counts is null ? null : new
            {
                students = r.Counts.Students, present = r.Counts.Present, late = r.Counts.Late, absent = r.Counts.Absent,
                schoolAbsent = r.Counts.SchoolAbsent, notRecorded = r.Counts.NotRecorded,
            },
        });
    }

    // ------------------------------------------------------------------ session reports

    [HttpGet("sessions/reports")]
    public async Task<IActionResult> SessionReports([FromQuery] SessionReportForm form, CancellationToken ct)
    {
        var query = form.ToQuery();
        var model = new SessionReportsViewModel
        {
            Form = form,
            Options = await sessions.OptionsAsync(SchoolWeek.Today, null, ct),
            Report = form.IsComplete ? await sessions.ReportAsync(query, ct) : null,
            LessonChoices = form.Mode == "lesson" && form.ClassId is { } cid ? (await sessions.ClassDayAsync(cid, query.From, null, ct))?.Lessons ?? [] : [],
        };
        return View(model);
    }

    [HttpGet("sessions/reports/pdf")]
    public async Task<IActionResult> SessionReportsPdf([FromQuery] SessionReportForm form, CancellationToken ct)
    {
        if (!form.IsComplete) return NotFound();
        return await PdfFile(Services.Pdf.SessionReports.Build(await sessions.ReportAsync(form.ToQuery(), ct)), ct);
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
