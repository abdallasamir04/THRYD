using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// PTC (Parent-Teacher Conference) administration: dashboard, events, classes &amp; dates, daily sessions &amp; Rounds,
/// readiness, preview and publishing, parent contact readiness and Round defaults. Super Admin only (<see cref="IPtcAccess"/>):
/// every action is refused (403) for anyone else, before it runs, and the services check again. No parent-facing page exists.
/// </summary>
[Route("ptc")]
public class PtcController(IPtcAccess access, IPtcAdminService admin, IPtcSetupService setup, ISettingsService settings, IOptions<PtcPortalOptions> portal,
    IPtcScheduleService scheduling, IPtcOperationsService operations, IPtcCoverageService coverage) : AppController
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!access.CanManage)
        {
            context.Result = Denied();
            return;
        }
        await next();
    }

    private IActionResult Denied()
    {
        if (IsAjax) return AjaxFailure(PtcAccess.DeniedMessage, StatusCodes.Status403Forbidden);
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("Denied");
    }

    private IActionResult Back(ServiceResult result, string success, string href)
    {
        if (result.Succeeded) Toast(success);
        else Toast(string.Join(" ", result.Errors.Select(e => e.Value).Distinct().Take(3)), "error");
        return Redirect(href);
    }

    // ================================================================== dashboard, contacts, defaults

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await admin.DashboardAsync(ct));

    [HttpGet("parent-contacts")]
    public async Task<IActionResult> ParentContacts(CancellationToken ct) => View(await admin.ParentContactsAsync(ct));

    [HttpGet("settings")]
    public async Task<IActionResult> Defaults(CancellationToken ct) => View(PtcDefaultsFormModel.From(await settings.GetPtcDefaultsAsync(ct)));

    [HttpPost("settings"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Defaults(PtcDefaultsFormModel model, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await settings.SavePtcDefaultsAsync(new PtcRoundDefaults(model.Capacity, model.MeetingMinutes, model.TransitionMinutes, model.DurationMinutes), ct);
            if (result.Succeeded)
            {
                Toast("PTC Round defaults saved. They apply to new Rounds; existing Rounds keep their own values.");
                return Redirect("/ptc");
            }
            AddErrors(result);
        }
        return View(model);
    }

    // ================================================================== events

    [HttpGet("events/new")]
    public async Task<IActionResult> NewEvent(CancellationToken ct)
    {
        var years = await admin.YearsAsync(ct);
        return View("EventForm", new PtcEventFormModel { Years = years, AcademicYearId = years.FirstOrDefault(y => y.IsCurrent)?.Id ?? years.FirstOrDefault()?.Id ?? 0 });
    }

    [HttpGet("events/{id:int}/edit")]
    public async Task<IActionResult> EditEvent(int id, CancellationToken ct)
    {
        var e = await admin.EventAsync(id, ct);
        if (e is null) return NotFound();
        return View("EventForm", new PtcEventFormModel
        {
            Id = e.Id, Title = e.Title, Description = e.Description, AcademicYearId = e.YearId, StartDate = e.StartDate, EndDate = e.EndDate,
            Status = e.Status, Years = await admin.YearsAsync(ct),
        });
    }

    [HttpPost("events/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveEvent(PtcEventFormModel model, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await setup.SaveEventAsync(model.Id, new PtcEventInput(model.Title, model.Description, model.AcademicYearId, model.StartDate, model.EndDate), ct);
            if (result.Succeeded)
            {
                Toast(model.IsEdit ? "PTC event saved." : "PTC event created as a Draft. Next: assign classes to dates.");
                return Redirect($"/ptc/events/{result.EntityId}");
            }
            AddErrors(result);
        }
        model.Years = await admin.YearsAsync(ct);
        if (model.Id is { } id && await admin.EventAsync(id, ct) is { } current) model.Status = current.Status;
        return View("EventForm", model);
    }

    [HttpGet("events/{id:int}")]
    public async Task<IActionResult> Event(int id, CancellationToken ct)
    {
        if (await admin.EventAsync(id, ct) is not { } e) return NotFound();
        ViewData["Share"] = Share(e);
        return View(e);
    }

    /// <summary>
    /// The public booking link (from the configured site address, else this request's address: no domain is invented) and the
    /// WhatsApp share text. Shared manually by the Admin; nothing is sent automatically.
    /// </summary>
    private PtcShareModel Share(PtcEventDetail e)
    {
        if (e.PublicToken is null) return new PtcShareModel(e.Id, false, false, null, null);
        var site = string.IsNullOrWhiteSpace(portal.Value.PublicBaseUrl) ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}" : portal.Value.PublicBaseUrl!;
        var url = PtcLinks.BookingUrl(site, e.PublicToken);
        return new PtcShareModel(e.Id, e.Status == Entities.PtcEventStatus.Published, e.BookingsOpen, url, PtcLinks.InvitationMessage(e.Title, url));
    }

    [HttpGet("events/{id:int}/bookings")]
    public async Task<IActionResult> Bookings(int id, CancellationToken ct)
    {
        if (await admin.EventAsync(id, ct) is not { } e) return NotFound();
        return View(new PtcBookingsViewModel { Event = e, Schedules = await admin.BookingsAsync(id, ct) });
    }

    [HttpGet("events/{id:int}/preview")]
    public async Task<IActionResult> Preview(int id, CancellationToken ct) =>
        await admin.EventAsync(id, ct) is { } e ? View(e) : NotFound();

    [HttpPost("events/{id:int}/publish"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id, bool acknowledgeWarnings, CancellationToken ct)
    {
        var result = await setup.PublishAsync(id, acknowledgeWarnings, ct);
        if (result.Succeeded)
        {
            Toast("The event is Published and shown on the School Calendar. Parent booking pages come in a later phase.");
            return Redirect($"/ptc/events/{id}");
        }
        AddErrors(result);
        return await admin.EventAsync(id, ct) is { } e ? View("Preview", e) : NotFound();
    }

    [HttpPost("events/{id:int}/draft"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReturnToDraft(int id, CancellationToken ct) =>
        Back(await setup.ReturnToDraftAsync(id, ct), "The event is a Draft again and no longer shown on the School Calendar.", $"/ptc/events/{id}");

    [HttpPost("events/{id:int}/cancel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct) =>
        Back(await setup.CancelEventAsync(id, ct), "The event is cancelled. Its configuration is kept as history.", $"/ptc/events/{id}");

    [HttpPost("events/{id:int}/complete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id, CancellationToken ct) =>
        Back(await setup.CompleteEventAsync(id, ct), "The event is marked Completed.", $"/ptc/events/{id}");

    [HttpPost("events/{id:int}/reopen"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(int id, CancellationToken ct) =>
        Back(await setup.ReopenEventAsync(id, ct), "The event is a Draft again.", $"/ptc/events/{id}");

    // ================================================================== classes & dates

    [HttpGet("events/{id:int}/classes")]
    public async Task<IActionResult> Classes(int id, CancellationToken ct)
    {
        var e = await admin.EventAsync(id, ct);
        if (e is null) return NotFound();
        var rows = e.Classes.ToDictionary(c => c.ClassId, c => new PtcClassRowInput
        {
            ClassId = c.ClassId, Include = c.Assigned, Date = c.Schedule?.Date, Start = c.Schedule?.Start, End = c.Schedule?.End,
        });
        return View(new PtcClassesViewModel { Event = e, Rows = rows });
    }

    [HttpPost("events/{id:int}/classes"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Classes(int id, List<PtcClassRowInput> rows, CancellationToken ct)
    {
        var result = await setup.AssignClassesAsync(id,
            rows.Where(r => r.Include).Select(r => new PtcClassDateInput(r.ClassId, r.Date, r.Start, r.End)).ToList(), ct);
        if (result.Succeeded)
        {
            Toast("Classes and dates saved.");
            return Redirect($"/ptc/events/{id}");
        }
        AddErrors(result);
        var e = await admin.EventAsync(id, ct);
        if (e is null) return NotFound();
        return View(new PtcClassesViewModel { Event = e, Rows = rows.GroupBy(r => r.ClassId).ToDictionary(g => g.Key, g => g.First()) });
    }

    // ================================================================== class schedule: daily session and Rounds

    private async Task<IActionResult> ScheduleView(int scheduleId, PtcScheduleFormModel? form, PtcRoundsFormModel? rounds, CancellationToken ct)
    {
        if (await admin.EventOfScheduleAsync(scheduleId, ct) is not { } eventId || await admin.EventAsync(eventId, ct) is not { } e) return NotFound();
        var row = e.Classes.FirstOrDefault(c => c.Schedule?.Id == scheduleId);
        if (row is null) return NotFound();
        var defaults = await settings.GetPtcDefaultsAsync(ct);
        var s = row.Schedule!;
        if (rounds is null)
        {
            // Suggested: the next Round starts where the day (or the last Round) starts / ends.
            rounds = PtcRoundsFormModel.From(defaults);
            rounds.FirstStart = s.Rounds.Count == 0 ? s.Start : s.Rounds.Max(r => r.End);
        }
        return View("Schedule", new PtcScheduleViewModel
        {
            Event = e, Class = row, Defaults = defaults,
            Form = form ?? new PtcScheduleFormModel { Date = s.Date, Start = s.Start, End = s.End, Notes = s.Notes },
            Rounds = rounds,
            SameDayWithoutRounds = e.Assigned.Count(c => c.Schedule!.Id != s.Id && c.Schedule.Date == s.Date && c.Schedule.Rounds.Count == 0),
        });
    }

    [HttpGet("schedules/{id:int}")]
    public Task<IActionResult> Schedule(int id, CancellationToken ct) => ScheduleView(id, null, null, ct);

    [HttpPost("schedules/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Schedule(int id, PtcScheduleFormModel form, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            if (form.Date is null || form.Start is null || form.End is null)
                ModelState.AddModelError("", "Enter the date and the daily start and end time.");
            else
            {
                var result = await setup.UpdateClassScheduleAsync(id, form.Date.Value, form.Start.Value, form.End.Value, form.Notes, ct);
                if (result.Succeeded)
                {
                    Toast("Daily session saved.");
                    return Redirect($"/ptc/schedules/{id}");
                }
                AddErrors(result);
            }
        }
        return await ScheduleView(id, form, null, ct);
    }

    [HttpPost("schedules/{id:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveSchedule(int id, CancellationToken ct)
    {
        var eventId = await admin.EventOfScheduleAsync(id, ct);
        var result = await setup.RemoveClassScheduleAsync(id, ct);
        return Back(result, "The class was removed from the event.", result.Succeeded || eventId is null ? $"/ptc/events/{eventId}" : $"/ptc/schedules/{id}");
    }

    [HttpPost("schedules/{id:int}/rounds"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRounds(int id, PtcRoundsFormModel model, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            if (model.FirstStart is null) ModelState.AddModelError("FirstStart", "Enter the start time of the first Round.");
            else
            {
                var result = await setup.AddRoundsAsync(id, new PtcRoundPlan(model.FirstStart.Value, model.Count, model.DurationMinutes, model.Capacity,
                    model.MeetingMinutes, model.TransitionMinutes, model.GapMinutes), model.SameDayClasses, ct);
                if (result.Succeeded)
                {
                    Toast(model.Count == 1 ? "Round added." : $"{model.Count} Rounds added.");
                    return Redirect($"/ptc/schedules/{id}");
                }
                AddErrors(result);
            }
        }
        return await ScheduleView(id, null, model, ct);
    }

    // ================================================================== scheduling engine (overview, conflicts, audited changes)

    [HttpGet("events/{id:int}/scheduling")]
    public async Task<IActionResult> Scheduling(int id, string? view, DateOnly? date, int? cls, PtcAllocationState? state, int? teacher, CancellationToken ct)
    {
        if (await admin.EventAsync(id, ct) is not { } e || await scheduling.AnalyzeAsync(id, ct) is not { } analysis) return NotFound();
        view = view is PtcSchedulingViewModel.TeachersView or PtcSchedulingViewModel.ConflictsView or PtcSchedulingViewModel.HistoryView ? view : PtcSchedulingViewModel.DatesView;
        return View(new PtcSchedulingViewModel
        {
            Event = e, Analysis = analysis, View = view, Date = date, ClassId = cls, State = state, TeacherId = teacher,
            History = view == PtcSchedulingViewModel.HistoryView ? await scheduling.HistoryAsync(id, ct) : [],
        });
    }

    [HttpGet("events/{id:int}/scheduling/teachers/{teacherId:int}")]
    public async Task<IActionResult> SchedulingTeacher(int id, int teacherId, CancellationToken ct)
    {
        if (await admin.EventAsync(id, ct) is not { } e || await scheduling.TeacherAsync(id, teacherId, ct) is not { } t) return NotFound();
        ViewData["Event"] = e;
        return View(t);
    }

    [HttpPost("bookings/{id:int}/reassign"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReassignSlot(int id, int eventId, int newSlot, int expectedSlot, string? reason, bool confirmed, CancellationToken ct) =>
        Back(await scheduling.ReassignSlotAsync(id, newSlot, expectedSlot, reason, confirmed, ct), "The booking was moved; the change is in the schedule history.",
            $"/ptc/events/{eventId}/scheduling?view=conflicts");

    [HttpPost("events/{id:int}/scheduling/apply"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyProposals(int id, string? fingerprint, string? reason, bool confirmed, CancellationToken ct)
    {
        var result = await scheduling.ApplyProposalsAsync(id, fingerprint, reason, confirmed, ct);
        return Back(result, $"{Ui.Plural(result.EntityId ?? 0, "proposal was", "proposals were")} applied; the changes are in the schedule history.",
            $"/ptc/events/{id}/scheduling?view=conflicts");
    }

    [HttpPost("rounds/{id:int}/move"), ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveRound(int id, int eventId, TimeOnly newStart, TimeOnly expectedStart, string? reason, bool confirmed, CancellationToken ct) =>
        Back(await scheduling.MoveRoundAsync(id, newStart, expectedStart, reason, confirmed, ct), "The Round was moved; the change is in the schedule history.",
            $"/ptc/events/{eventId}/scheduling?view=conflicts");

    // ================================================================== class coverage (Phase 6: timetable lessons during the PTC and substitutes)

    [HttpGet("events/{id:int}/coverage")]
    public async Task<IActionResult> Coverage(int id, DateOnly? date, int? cls, int? dept, int? teacher, PtcCoverageStatus? status, int? round, string? sort,
        bool revalidate, CancellationToken ct)
    {
        // Coverage is never stored: every request (and every publish) computes it again from the live timetable and assignments.
        if (await admin.EventAsync(id, ct) is not { } e) return NotFound();
        if (revalidate) Toast($"Coverage checked again now: {CoverageState(e)}", e.Coverage.Unresolved > 0 ? "error" : "success");
        return View(new PtcCoverageViewModel
        {
            Event = e, Date = date, ClassId = cls, DepartmentId = dept, TeacherId = teacher, Status = status, Round = round,
            Sort = sort is "teacher" or "status" ? sort : "date",
            History = await coverage.HistoryAsync(id, ct),
        });
    }

    [HttpGet("events/{id:int}/coverage/lesson")]
    public async Task<IActionResult> CoverageLesson(int id, int schedule, int lesson, CancellationToken ct)
    {
        if (await coverage.FormAsync(id, schedule, lesson, ct) is { } form) return View(form);
        Toast("This lesson no longer needs a substitute: it does not overlap a Round of that class's PTC any more, or it changed.", "error");
        return Redirect($"/ptc/events/{id}/coverage");
    }

    private static string CoverageState(PtcEventDetail e) =>
        e.Coverage.Unresolved > 0
            ? e.Status == Entities.PtcEventStatus.Published
                ? $"this Published event needs attention: {Ui.Plural(e.Coverage.Unresolved, "lesson has", "lessons have")} no valid substitute (confirmed bookings are not changed)."
                : e.Coverage.BlockSummary!
            : e.Coverage.HasSchedules && e.Coverage.TimetableEmpty ? "the timetable has no lessons for these dates, so coverage cannot be verified."
            : e.Coverage.ConflictCount > 0 ? $"all {Ui.Plural(e.Coverage.ConflictCount, "lesson", "lessons")} in conflict are covered."
            : "no teacher has a lesson during the Rounds.";

    /// <summary>After a change: the result and what the coverage is now.</summary>
    private async Task<IActionResult> AfterCoverageChange(ServiceResult result, string success, int eventId, string backHref, CancellationToken ct)
    {
        if (!result.Succeeded) return Back(result, "", backHref);
        var e = await admin.EventAsync(eventId, ct);
        Toast(e is null ? success : $"{success} Coverage now: {CoverageState(e)}", e is { Coverage.Unresolved: > 0 } ? "error" : "success");
        return Redirect($"/ptc/events/{eventId}/coverage");
    }

    [HttpPost("events/{id:int}/coverage/assign"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignSubstitute(int id, int scheduleId, int lessonId, int substituteId, string? reason, string? notes, CancellationToken ct) =>
        await AfterCoverageChange(await coverage.AssignAsync(id, scheduleId, lessonId, substituteId, reason, notes, ct), "Substitute assigned.", id,
            $"/ptc/events/{id}/coverage/lesson?schedule={scheduleId}&lesson={lessonId}", ct);

    [HttpPost("coverage/{id:int}/replace"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplaceSubstitute(int id, int eventId, int scheduleId, int lessonId, int substituteId, string? reason, string? notes, CancellationToken ct) =>
        await AfterCoverageChange(await coverage.ReplaceAsync(id, substituteId, reason, notes, ct), "Substitute replaced; the previous assignment is kept in the history.", eventId,
            $"/ptc/events/{eventId}/coverage/lesson?schedule={scheduleId}&lesson={lessonId}", ct);

    [HttpPost("coverage/{id:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveSubstitute(int id, int eventId, string? reason, string? back, CancellationToken ct) =>
        await AfterCoverageChange(await coverage.RemoveAsync(id, reason, ct), "Substitute removed (kept in the history).", eventId,
            Url.IsLocalUrl(back) ? back! : $"/ptc/events/{eventId}/coverage", ct);

    [HttpPost("coverage/{id:int}/notes"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SubstituteNotes(int id, int eventId, string? notes, string? back, CancellationToken ct)
    {
        var href = Url.IsLocalUrl(back) ? back! : $"/ptc/events/{eventId}/coverage";
        return Back(await coverage.UpdateNotesAsync(id, notes, ct), "Notes saved (the change is in the history).", href);
    }

    // ================================================================== live operations (Super Admin)

    [HttpGet("events/{id:int}/live")]
    public async Task<IActionResult> Live(int id, DateOnly? date, int? cls, int? teacher, PtcLiveState? state, bool partial, CancellationToken ct)
    {
        if (await operations.LiveAsync(id, date, cls, teacher, state, ct) is not { } live) return NotFound();
        if (partial) return PartialView("_Live", live);
        if (await admin.EventAsync(id, ct) is not { } e) return NotFound();
        ViewData["Event"] = e;
        return View(live);
    }

    private string LiveHref(int eventId, DateOnly? date) => $"/ptc/events/{eventId}/live" + (date is { } d ? $"?date={d:yyyy-MM-dd}" : "");

    [HttpPost("live/start"), ValidateAntiForgeryToken]
    public async Task<IActionResult> LiveStart(string? key, int eventId, DateOnly? date, CancellationToken ct) =>
        Timer(await operations.StartAsync(key, ct), LiveHref(eventId, date));

    [HttpPost("live/end"), ValidateAntiForgeryToken]
    public async Task<IActionResult> LiveEnd(string? key, int eventId, DateOnly? date, CancellationToken ct) =>
        Timer(await operations.EndAsync(key, ct), LiveHref(eventId, date));

    private IActionResult Timer(PtcTimerResult r, string href)
    {
        if (r.Succeeded) Toast(r.AlreadyDone ? "Already recorded: the original time is kept." : r.ActualEndUtc is null ? "Interview started (by the Super Admin)." : "Interview ended (by the Super Admin).");
        else Toast(r.Error ?? "The interview could not be updated.", "error");
        return Redirect(href);
    }

    [HttpPost("live/correct"), ValidateAntiForgeryToken]
    public async Task<IActionResult> LiveCorrect(string? key, int eventId, DateOnly? date, PtcCorrectionTarget target, TimeOnly? start, TimeOnly? end,
        string? reason, bool confirmed, string? version, CancellationToken ct) =>
        Back(await operations.CorrectAsync(key, target, start, end, reason, confirmed, version, ct), "Correction recorded; the previous values are kept in the operations log.",
            LiveHref(eventId, date));

    [HttpPost("rounds/{id:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveRound(int id, int scheduleId, CancellationToken ct) =>
        Back(await setup.RemoveRoundAsync(id, ct), "Round removed; the others were renumbered.", $"/ptc/schedules/{scheduleId}");
}
