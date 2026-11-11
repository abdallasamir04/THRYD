using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>Daily Operations → Morning Duty (Students Arrival Check, In-Class Morning, Morning Assembly / Proadcast).</summary>
[Route("duties")]
public class DutiesController(IDutyService duties, IClassService classes, IPeService pe) : AppController
{
    /// <summary>
    /// Duties landing page: Morning, Midday and Dismissal cards (?type=morning | midday | dismissal opens
    /// that duty's page) plus a P.E. card that opens the P.E. module (/pe).
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, string? type, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        if (string.IsNullOrWhiteSpace(type))
        {
            var cards = new List<HubCard>();
            foreach (var t in DutyComponents.Types)
            {
                var summary = await duties.SummaryAsync(t, day, ct);
                var (icon, text) = DutyComponents.TypeCard(t);
                var total = summary.Sum(s => s.Total);
                var open = summary.Sum(s => s.NeedsStaffAction);
                cards.Add(new HubCard($"/duties?type={DutyComponents.TypeSlug(t)}&date={SchoolWeek.Iso(day)}", icon, DutyComponents.TypeName(t), text,
                    $"{summary.Count} sections · {(total == 0 ? "no duties today" : $"{total} today{(open > 0 ? $" · {open} need action" : "")}")}",
                    t switch { DutyType.Midday => "amber", DutyType.Dismissal => "teal", _ => "violet" }));
            }
            var peWeek = await pe.WeekAsync(SchoolWeek.StartOf(day), ct);
            var peToday = ScheduleRules.IsSchoolDay(day.DayOfWeek) ? peWeek.Count(s => s.Day == day.DayOfWeek) : 0;
            cards.Add(new HubCard($"/pe?date={SchoolWeek.Iso(day)}", "activity", "P.E.", "Physical Education",
                $"{Ui.Plural(peWeek.Count, "session", "sessions")} this week{(peToday > 0 ? $" · {peToday} today" : "")}", "green"));
            return View("~/Views/Staff/Hub.cshtml", new HubViewModel
            {
                Title = "Duties",
                Subtitle = "Choose a duty to manage its sections, staff, status and feedback.",
                Crumbs = [("Dashboard", "/"), ("Duties", null)],
                Cards = cards,
                Nav = "MorningDuty",
                Extra = [("Duty audit", $"/duties/audit?from={SchoolWeek.Iso(day)}&to={SchoolWeek.Iso(day)}", "clipboard-check")],
            });
        }

        var dutyType = DutyComponents.TypeFromSlug(type);
        return View(new MorningDutyViewModel { Date = day, Type = dutyType, Components = await duties.SummaryAsync(dutyType, day, ct) });
    }

    [HttpGet("{type:regex(^(morning|midday|dismissal)$)}/{slug}")]
    public async Task<IActionResult> Component(string type, string slug, string? date, CancellationToken ct)
    {
        var info = DutyComponents.FromSlug(slug);
        if (info is null || DutyComponents.TypeSlug(info.Type) != type) return NotFound();
        var day = SchoolWeek.ParseDayOrToday(date);
        var model = new DutyListViewModel { Info = info, Date = day, Duties = await duties.ListAsync(info.Component, day, ct) };
        return IsAjax ? PartialView("_List", model) : View(model);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var duty = await duties.GetAsync(id, ct);
        if (duty is null) return NotFoundResult("This duty no longer exists.");
        return PageOrPartial("Details", new DutyDetailsViewModel
        {
            Duty = duty,
            Info = DutyComponents.Get(duty.Component),
            History = await duties.BackupHistoryAsync(id, ct),
            AuditTrail = await duties.AuditTrailAsync(id, ct),
            Staff = await duties.StaffAsync(id, ct),
        });
    }

    /// <summary>Duty audit: every duty session with its audit fields, filterable.</summary>
    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] DutyAuditQuery query, CancellationToken ct)
    {
        var model = new DutyAuditViewModel
        {
            Query = query,
            Result = await duties.AuditListAsync(query, ct),
            Staff = await duties.StaffOptionsAsync(ct),
            Locations = await duties.LocationsAsync(ct),
        };
        return IsAjax ? PartialView("_AuditTable", model) : View(model);
    }

    /// <summary>Download Report: every duty session matching the audit filters (all pages).</summary>
    [HttpGet("audit/pdf")]
    public async Task<IActionResult> AuditPdf([FromQuery] DutyAuditQuery query, CancellationToken ct)
    {
        var staff = query.StaffId is { } sid ? (await duties.StaffOptionsAsync(ct)).FirstOrDefault(s => s.Id == sid)?.FullName : null;
        return await PdfFile(PdfReports.DutyAudit(await duties.AuditListAllAsync(query, ct), query, staff), ct);
    }

    [HttpPost("{id:int}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(int id, DutyStatus status, CancellationToken ct) =>
        await Done(id, await duties.SetStatusAsync(id, status, ct), $"Status set to {DutyStatuses.Label(status)}.", ct);

    [HttpGet("{id:int}/audit-record")]
    public async Task<IActionResult> AuditRecord(int id, CancellationToken ct)
    {
        var duty = await duties.GetAsync(id, ct);
        if (duty is null) return NotFoundResult("This duty no longer exists.");
        return PageOrPartial("AuditForm", new DutyAuditFormModel
        {
            Id = id, Status = duty.Status, AuditNotes = duty.AuditNotes, IssuesEncountered = duty.IssuesEncountered,
            FollowUpRequired = duty.FollowUpRequired, Duty = duty,
        });
    }

    [HttpPost("{id:int}/audit-record"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AuditRecord(int id, DutyAuditFormModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await duties.UpdateAuditAsync(id, model.Status, model.AuditNotes, model.IssuesEncountered, model.FollowUpRequired, ct);
            if (result.Succeeded)
            {
                var duty = await duties.GetAsync(id, ct);
                return Saved("Duty audit saved.", DutyComponents.Get(duty!.Component), duty.DutyDate);
            }
            AddErrors(result);
        }
        model.Duty = await duties.GetAsync(id, ct);
        if (model.Duty is null) return NotFoundResult("This duty no longer exists.");
        return FormView("AuditForm", model);
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create(string component, string? date, CancellationToken ct)
    {
        var info = DutyComponents.FromSlug(component);
        if (info is null) return NotFoundResult("Unknown duty component.");
        var model = new DutyFormModel { Component = info.Component, DutyDate = SchoolWeek.ParseDayOrToday(date) };
        await FillAsync(model, ct);
        return PageOrPartial("Form", model);
    }

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(DutyFormModel model, CancellationToken ct)
    {
        model.Id = null;
        if (!Enum.IsDefined(model.Component)) return NotFoundResult("Unknown duty component.");
        if (ModelState.IsValid)
        {
            var result = await duties.CreateAsync(model.ToInput(), ct);
            if (result.Succeeded) return Saved("Duty saved.", model.Info, model.DutyDate);
            AddErrors(result);
        }
        await FillAsync(model, ct);
        return FormView("Form", model);
    }

    /// <summary><paramref name="attendance"/> pre-selects the assigned staff's attendance (the "Absent" quick action opens this form).</summary>
    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, AttendanceStatus? attendance, CancellationToken ct)
    {
        var d = await duties.GetAsync(id, ct);
        if (d is null) return NotFoundResult("This duty no longer exists.");
        var model = new DutyFormModel
        {
            Id = d.Id,
            Component = d.Component,
            DutyDate = d.DutyDate,
            Description = d.Description,
            StartTime = d.StartTime,
            EndTime = d.EndTime,
            Location = d.Location,
            PrimaryStaffId = d.PrimaryStaffId,
            PrimaryAttendance = attendance is { } a && Enum.IsDefined(a) ? a : d.PrimaryAttendance,
            BackupStaffId = d.BackupStaffId,
            Notes = d.Notes,
            ClassIds = d.ClassIds.ToList(),
        };
        await FillAsync(model, ct, d);
        return PageOrPartial("Form", model);
    }

    [HttpPost("{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, DutyFormModel model, CancellationToken ct)
    {
        model.Id = id;
        var current = await duties.GetAsync(id, ct);
        if (current is null) return NotFoundResult("This duty no longer exists.");
        model.Component = current.Component;
        if (ModelState.IsValid)
        {
            var result = await duties.UpdateAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return Saved("Duty updated.", model.Info, model.DutyDate);
            AddErrors(result);
        }
        await FillAsync(model, ct, current);
        return FormView("Form", model);
    }

    [HttpPost("{id:int}/attendance"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Attendance(int id, AttendanceStatus status, CancellationToken ct) =>
        await Done(id, await duties.SetPrimaryAttendanceAsync(id, status, ct),
            status == AttendanceStatus.Present ? "Marked present." : "Attendance updated.", ct);

    [HttpPost("{id:int}/backup-response"), ValidateAntiForgeryToken]
    public async Task<IActionResult> BackupResponse(int id, bool accepted, string? note, CancellationToken ct) =>
        await Done(id, await duties.RecordBackupResponseAsync(id, accepted, note, ct),
            accepted ? "Backup accepted. Record their attendance when they arrive." : "Backup declined. Choose another backup.", ct);

    [HttpPost("{id:int}/backup-attendance"), ValidateAntiForgeryToken]
    public async Task<IActionResult> BackupAttendance(int id, AttendanceStatus status, CancellationToken ct) =>
        await Done(id, await duties.SetBackupAttendanceAsync(id, status, ct),
            status == AttendanceStatus.Present ? "Backup marked present." : "Backup attendance updated.", ct);

    [HttpPost("{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var duty = await duties.GetAsync(id, ct);
        var result = await duties.DeleteAsync(id, ct);
        if (IsAjax) return result.Succeeded ? AjaxSuccess("Duty deleted.") : AjaxFailure(result.Errors[0].Value, 409);
        Toast(result.Succeeded ? "Duty deleted." : result.Errors[0].Value, result.Succeeded ? "success" : "error");
        return duty is null ? RedirectToAction(nameof(Index)) : RedirectToList(DutyComponents.Get(duty.Component), duty.DutyDate);
    }

    private async Task<IActionResult> Done(int id, ServiceResult result, string message, CancellationToken ct)
    {
        var error = result.Succeeded ? null : result.Errors[0].Value;
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(error!, 409);
        Toast(error ?? message, result.Succeeded ? "success" : "error");
        var duty = await duties.GetAsync(id, ct);
        return duty is null ? RedirectToAction(nameof(Index)) : RedirectToList(DutyComponents.Get(duty.Component), duty.DutyDate);
    }

    private IActionResult Saved(string message, DutyComponentInfo info, DateOnly? date)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return RedirectToList(info, date ?? SchoolWeek.Today);
    }

    private IActionResult RedirectToList(DutyComponentInfo info, DateOnly date) =>
        Redirect($"{DutyComponents.Href(info)}?date={SchoolWeek.Iso(date)}");

    private async Task FillAsync(DutyFormModel model, CancellationToken ct, DutyRow? current = null)
    {
        model.Staff = await duties.StaffOptionsAsync(ct);
        var all = await classes.AllOptionsAsync(ct);
        var keep = current?.ClassIds ?? [];
        model.Classes = all.Where(c => c.IsActive || keep.Contains(c.Id)).ToList();
        if (current is not null)
        {
            model.CurrentBackupId = current.BackupStaffId;
            model.BackupStatus = current.BackupStatus;
            model.BackupAttendance = current.BackupAttendance;
        }
    }
}
