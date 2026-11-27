using SchoolManagement.Web.Services.Accounts;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Daily Operations → Duties (/duties): two cards, Duties and Duty Audit.
/// <list type="bullet">
/// <item>Duties (/duties/board): the Main Duties as cards (each opens its full board — Sub-Duties, coverage points, staff,
/// backups, schedules and reports — below the cards). The duty management pages are unchanged and open from there.</item>
/// <item>Duty Audit (/duties/audit, /duties/audit/staff-records, the audit report and audit records): only for users
/// authorized in the "DutyAudit" configuration (<see cref="IDutyAuditAccess"/>), checked here and again in the services.</item>
/// </list>
/// </summary>
[Route("duties")]
[StaffAccess(StaffAccessRule.DutyAudit)]
public class DutiesController(IDutyService duties, IClassService classes, IStaffDutyService staffDuty, IActivityService activities, IDutyBoardService board,
    IDutyAuditAccess auditAccess, ICurrentUser currentUser) : AppController
{
    /// <summary>
    /// The Duties landing page: exactly two cards, Duties and Duty Audit. The former addresses (?type=morning | midday |
    /// dismissal, ?date=) lead to the duty board with that duty selected. A reload of the old board (AJAX) still gets its partial.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, string? type, CancellationToken ct)
    {
        if (!IsAjax)
        {
            if (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(date)) return Redirect(DutiesPages.Board(ValidSlug(type), date));
            var canAudit = await auditAccess.CanAccessAsync(ct);
            return View("~/Views/Staff/Hub.cshtml", new HubViewModel
            {
                Title = DutiesPages.Title,
                Subtitle = "Manage the school's duties, and audit how they are carried out.",
                Crumbs = [("Dashboard", "/"), ("Daily Operations", null), (DutiesPages.Title, null)],
                Nav = "DutyAudit",
                Large = true,
                Cards =
                [
                    new(DutiesPages.BoardUrl, "shield-check", "Duties",
                        "Every Main Duty of the day with its Sub-Duties, coverage points, staff and backups, schedules and reports.",
                        "Morning · Midday · Dismissal · Activities", "blue"),
                    new(DutiesPages.AuditUrl, "clipboard-check", "Duty Audit",
                        "Every duty session with its status, staffing, attendance and audit notes, and each staff member's duty records.",
                        canAudit ? "Authorized" : "Restricted · authorized staff only", canAudit ? "teal" : "rose"),
                ],
            });
        }
        var day = SchoolWeek.ParseDayOrToday(date);
        var dutyDay = await board.DayAsync(day, ct);
        if (string.IsNullOrWhiteSpace(type))
        {
            // Every Main Duty with its calculated status and Sub-Duties; the board refreshes itself as time passes.
            var hub = new DutyHubViewModel { Day = dutyDay, Activities = (await activities.ListAsync(false, ct)).Count };
            return PartialView("_HubBoard", hub);
        }
        var mainType = DutyComponents.TypeFromSlug(type);
        var model = new MainDutyPageViewModel
        {
            Day = dutyDay, Main = dutyDay.Get(mainType),
            Revisions = mainType == DutyType.Morning ? await duties.RevisionCountAsync(day, ct) : 0,
        };
        return PartialView("_MainBoard", model);
    }

    [HttpGet("{type:dutytype}/{slug}")]
    public async Task<IActionResult> Component(string type, string slug, string? date, CancellationToken ct)
    {
        var info = DutyComponents.FromSlug(slug);
        if (info is null || DutyComponents.TypeSlug(info.Type) != type) return NotFound();
        var day = SchoolWeek.ParseDayOrToday(date);
        var dutyDay = await board.DayAsync(day, ct);
        var main = dutyDay.Get(info.Type);
        var model = new DutyListViewModel
        {
            Info = info, Date = day, Duties = await duties.ListAsync(info.Component, day, ct), Main = main,
            Sub = main.Subs.FirstOrDefault(s => s.Info.Component == info.Component),
            ReportHistory = await board.ReportHistoryAsync(day, info.Component, ct),
            Revisions = info.Component == DutyService.RevisionComponent ? await duties.RevisionsAsync(day, ct) : [],
        };
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
            Revisions = duty.Component == DutyService.RevisionComponent ? await duties.RevisionsForDutyAsync(id, ct) : [],
        });
    }

    /// <summary>The final coverage report of a Main Duty on a date, as a PDF.</summary>
    [HttpGet("{type:dutytype}/report.pdf")]
    public async Task<IActionResult> CoveragePdf(string type, string? date, CancellationToken ct)
    {
        var day = await board.DayAsync(SchoolWeek.ParseDayOrToday(date), ct);
        return await PdfFile(DutyCoverageReports.Build(day, day.Get(DutyComponents.TypeFromSlug(type))), ct);
    }

    /// <summary>Department view: each department's Sub-Duties (from their schedules) and its coverage on a date.</summary>
    [HttpGet("departments")]
    public async Task<IActionResult> Departments(string? date, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        return View(new DutyDepartmentsViewModel { Date = day, Departments = await board.DepartmentsAsync(day, ct) });
    }

    // ------------------------------------------------------------------ schedules

    [HttpGet("{type:dutytype}/schedule")]
    public async Task<IActionResult> Schedule(string type, CancellationToken ct) =>
        View(new DutySchedulePageViewModel { Schedule = await board.ScheduleAsync(DutyComponents.TypeFromSlug(type), ct), Departments = await board.DepartmentOptionsAsync(ct) });

    [HttpPost("{type:dutytype}/schedule"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Schedule(string type, MainScheduleForm form, CancellationToken ct)
    {
        var t = DutyComponents.TypeFromSlug(type);
        var result = await board.SaveMainScheduleAsync(t, DutyForms.Date(form.EffectiveFrom) ?? SchoolWeek.Today, DutyForms.Time(form.StartTime),
            DutyForms.Time(form.EndTime), PeriodDays.Mask(form.AllDays, form.Days), ct);
        return Finish(result, $"{DutyComponents.TypeName(t)} time saved. Earlier days keep the schedule they had.", $"/duties/{type}/schedule");
    }

    [HttpGet("sections/new")]
    public async Task<IActionResult> AddSection(string? type, CancellationToken ct)
    {
        var t = DutyComponents.TypeFromSlug(type);
        return PageOrPartial("SectionForm", await SectionFormAsync(new SectionForm { Type = t, EffectiveFrom = SchoolWeek.Iso(SchoolWeek.Today) }, ct));
    }

    [HttpPost("sections/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSection(SectionForm form, CancellationToken ct)
    {
        form.Id = null;
        if (DutyComponents.IsTypeSlug(form.Duty)) form.Type = DutyComponents.TypeFromSlug(form.Duty);
        var result = await board.AddSectionAsync(form.Type, form.ToInput(SchoolWeek.Today), ct);
        if (result.Succeeded) return Finish(result, $"Sub-Duty “{form.Name?.Trim()}” added.", $"/duties/{DutyComponents.TypeSlug(form.Type)}/schedule");
        AddErrors(result);
        return FormView("SectionForm", await SectionFormAsync(form, ct));
    }

    [HttpGet("sections/{id:int}/edit")]
    public async Task<IActionResult> EditSection(int id, CancellationToken ct)
    {
        var info = DutyComponents.All.FirstOrDefault(c => (int)c.Component == id);
        if (info is null) return NotFoundResult("This Sub-Duty no longer exists.");
        var current = (await board.ScheduleAsync(info.Type, ct)).Sections.First(s => s.Info.Component == info.Component).Current;
        var form = new SectionForm
        {
            Id = id, Type = info.Type, Name = info.Name, Summary = info.Summary, EffectiveFrom = SchoolWeek.Iso(SchoolWeek.Today),
            StartTime = current?.StartTime.ToString("HH:mm"), EndTime = current?.EndTime.ToString("HH:mm"), Required = current?.IsRequired ?? true,
            DepartmentId = current?.DepartmentId, AllDays = current is null || PeriodDays.IsAll(current.DayMask),
            Days = current is null ? [] : PeriodDays.Days(current.DayMask).ToList(),
        };
        return PageOrPartial("SectionForm", await SectionFormAsync(form, ct));
    }

    [HttpPost("sections/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSection(int id, SectionForm form, CancellationToken ct)
    {
        form.Id = id;
        var info = DutyComponents.All.FirstOrDefault(c => (int)c.Component == id);
        if (info is null) return NotFoundResult("This Sub-Duty no longer exists.");
        form.Type = info.Type;
        var result = await board.SaveSectionAsync(info.Component, form.ToInput(SchoolWeek.Today), ct);
        if (result.Succeeded) return Finish(result, $"{form.Name?.Trim() ?? info.Name} saved. Earlier days keep the schedule they had.", $"/duties/{DutyComponents.TypeSlug(info.Type)}/schedule");
        AddErrors(result);
        return FormView("SectionForm", await SectionFormAsync(form, ct));
    }

    [HttpPost("sections/{id:int}/retire"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RetireSection(int id, string? from, CancellationToken ct)
    {
        var info = DutyComponents.All.FirstOrDefault(c => (int)c.Component == id);
        if (info is null) return NotFoundResult("This Sub-Duty no longer exists.");
        return Finish(await board.RetireSectionAsync(info.Component, DutyForms.Date(from) ?? SchoolWeek.Today, ct),
            $"{info.Name} removed from the schedule. Its earlier records stay.", $"/duties/{DutyComponents.TypeSlug(info.Type)}/schedule");
    }

    [HttpPost("sections/{id:int}/restore"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RestoreSection(int id, CancellationToken ct)
    {
        var info = DutyComponents.All.FirstOrDefault(c => (int)c.Component == id);
        if (info is null) return NotFoundResult("This Sub-Duty no longer exists.");
        return Finish(await board.RestoreSectionAsync(info.Component, ct), $"{info.Name} is scheduled again.", $"/duties/{DutyComponents.TypeSlug(info.Type)}/schedule");
    }

    private async Task<SectionForm> SectionFormAsync(SectionForm form, CancellationToken ct)
    {
        form.Departments = await board.DepartmentOptionsAsync(ct);
        var main = (await board.ScheduleAsync(form.Type, ct)).Main;
        (form.MainStart, form.MainEnd) = (main?.StartTime, main?.EndTime);
        return form;
    }

    // ------------------------------------------------------------------ not covered: area, reason, notes

    [HttpGet("report-uncovered")]
    public async Task<IActionResult> ReportUncovered(string? component, string? date, int? point, int? supersedes, CancellationToken ct)
    {
        var info = DutyComponents.FromSlug(component);
        if (info is null) return NotFoundResult("Unknown Sub-Duty.");
        var day = SchoolWeek.ParseDayOrToday(date);
        var form = new CoverageReportForm { Component = info.Slug, Date = SchoolWeek.Iso(day), PointId = point, Supersedes = supersedes };
        if (supersedes is { } sid && (await board.ReportHistoryAsync(day, info.Component, ct)).FirstOrDefault(r => r.Id == sid) is { } old)
            (form.PointId, form.Area, form.Reason, form.Notes) = (old.PointId, old.Area, old.Reason, old.Notes);
        form.Sub = (await board.DayAsync(day, ct)).Get(info.Type).Subs.FirstOrDefault(s => s.Info.Component == info.Component);
        return PageOrPartial("ReportUncovered", form);
    }

    [HttpPost("report-uncovered"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReportUncovered(CoverageReportForm form, CancellationToken ct)
    {
        var info = DutyComponents.FromSlug(form.Component);
        if (info is null) return NotFoundResult("Unknown Sub-Duty.");
        var day = DutyForms.Date(form.Date) ?? SchoolWeek.Today;
        var result = await board.ReportUncoveredAsync(new CoverageReportInput(day, info.Component, form.PointId, form.Area, form.Reason, form.Notes, form.Supersedes), ct);
        if (result.Succeeded) return Finish(result, "Not-covered report saved. It explains the gap; it does not count as coverage.", $"{DutyComponents.Href(info)}?date={SchoolWeek.Iso(day)}");
        AddErrors(result);
        form.Sub = (await board.DayAsync(day, ct)).Get(info.Type).Subs.FirstOrDefault(s => s.Info.Component == info.Component);
        return FormView("ReportUncovered", form);
    }

    // ------------------------------------------------------------------ Add Duty (a new Main Duty)

    /// <summary>Duties → Add Duty: a new Main Duty with its schedule and first Sub-Duties (Super Admin only, checked again in the service).</summary>
    [HttpGet("main/new")]
    public IActionResult NewMainDuty()
    {
        if (!currentUser.IsSuperAdmin) return AddDutyDenied();
        return PageOrPartial("MainDutyForm", new MainDutyFormModel { EffectiveFrom = SchoolWeek.Iso(SchoolWeek.Today), SubDuties = [new()] });
    }

    [HttpPost("main/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> NewMainDuty(MainDutyFormModel model, CancellationToken ct)
    {
        if (!currentUser.IsSuperAdmin) return AddDutyDenied();
        var result = await board.CreateMainDutyAsync(model.ToInput(SchoolWeek.Today), ct);
        if (result.Succeeded)
        {
            var type = (DutyType)result.EntityId!.Value;
            var message = $"“{DutyComponents.TypeName(type)}” added. Open its Sub-Duties to add coverage points and staff.";
            var href = DutiesPages.Board(DutyComponents.TypeSlug(type), null);
            if (IsAjax) return AjaxSuccess(message, href);
            Toast(message);
            return Redirect(href);
        }
        AddErrors(result);
        if (model.SubDuties.Count == 0) model.SubDuties.Add(new());
        return FormView("MainDutyForm", model);
    }

    private IActionResult AddDutyDenied() =>
        IsAjax ? AjaxFailure("Only the Super Admin can add a duty.", StatusCodes.Status403Forbidden) : StatusCode(StatusCodes.Status403Forbidden);

    /// <summary>Duties → the duty board: every Main Duty of the day as a card (?duty= selects one and shows its board, ?date= the day).</summary>
    [HttpGet("board")]
    public async Task<IActionResult> Board(string? duty, string? date, CancellationToken ct) => View(await OverviewAsync(duty, date, ct));

    [HttpPost("reports/{id:int}/withdraw"), ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawReport(int id, string? back, CancellationToken ct) =>
        Finish(await board.WithdrawReportAsync(id, ct), "Report withdrawn (kept in the history).", back is not null && back.StartsWith("/duties", StringComparison.Ordinal) ? back : DutiesPages.BoardUrl);

    private IActionResult Finish(Common.ServiceResult result, string message, string redirect)
    {
        var error = result.Succeeded ? null : string.Join(" ", result.Errors.Select(e => e.Value).Distinct());
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(error!, 422);
        Toast(error ?? message, result.Succeeded ? "success" : "error");
        return LocalRedirect(redirect);
    }

    /// <summary>
    /// Duty Audit: every duty session with its audit fields, filterable (authorized users only). The filters (AJAX) return
    /// only the table. A former card address (/duties/audit?duty=…, when the cards were on this page) opens the duty board.
    /// </summary>
    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] DutyAuditQuery query, string? duty, string? date, CancellationToken ct)
    {
        if (!IsAjax && !string.IsNullOrWhiteSpace(duty)) return Redirect(DutiesPages.Board(ValidSlug(duty), date));
        if (await DeniedAsync(ct) is { } denied) return denied;
        var model = new DutyAuditViewModel
        {
            Query = query,
            Result = await duties.AuditListAsync(query, ct),
            Staff = await duties.StaffOptionsAsync(ct),
            Locations = await duties.LocationsAsync(ct),
        };
        return IsAjax ? PartialView("_AuditTable", model) : View(model);
    }

    /// <summary>The duty board's cards and selected duty, reloaded every 30 seconds (duties.js).</summary>
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(string? duty, string? date, CancellationToken ct) =>
        IsAjax ? PartialView("_Overview", await OverviewAsync(duty, date, ct)) : Redirect(DutiesPages.Board(ValidSlug(duty), date));

    /// <summary>Null when the user may use Duty Audit; otherwise the refusal (403): the explanation page, or JSON for AJAX.</summary>
    private async Task<IActionResult?> DeniedAsync(CancellationToken ct)
    {
        if (await auditAccess.CanAccessAsync(ct)) return null;
        if (IsAjax) return AjaxFailure(DutyAuditAccess.DeniedMessage, StatusCodes.Status403Forbidden);
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("AuditDenied");
    }

    private static string? ValidSlug(string? slug) =>
        DutyComponents.Types.Select(DutyComponents.TypeSlug).FirstOrDefault(s => string.Equals(s, slug, StringComparison.OrdinalIgnoreCase));

    private async Task<DutyHubViewModel> OverviewAsync(string? duty, string? date, CancellationToken ct)
    {
        var dutyDay = await board.DayAsync(SchoolWeek.ParseDayOrToday(date), ct);
        DutyType? selected = ValidSlug(duty) is { } slug ? DutyComponents.TypeFromSlug(slug) : null;
        return new DutyHubViewModel
        {
            Day = dutyDay, Activities = (await activities.ListAsync(false, ct)).Count, Selected = selected, CanAudit = await auditAccess.CanAccessAsync(ct),
            CanAddDuty = currentUser.IsSuperAdmin,
            Details = selected is { } t ? new MainDutyPageViewModel
            {
                Day = dutyDay, Main = dutyDay.Get(t),
                Revisions = t == DutyType.Morning ? await duties.RevisionCountAsync(dutyDay.Date, ct) : 0,
            } : null,
        };
    }

    /// <summary>
    /// Duty Audit → Staff Duty Records tab: each staff member's duty totals and average rating (the page that
    /// was /staff?view=duties, which now redirects here). Same service, table and PDF as before.
    /// </summary>
    [HttpGet("audit/staff-records")]
    public async Task<IActionResult> StaffRecords(string? search, CancellationToken ct)
    {
        if (await DeniedAsync(ct) is { } denied) return denied;
        var model = new StaffOverviewViewModel { Rows = await staffDuty.OverviewAsync(search, ct), Search = search };
        return IsAjax ? PartialView("~/Views/Staff/_Overview.cshtml", model) : View(model);
    }

    /// <summary>Download Report: every duty session matching the audit filters (all pages).</summary>
    [HttpGet("audit/pdf")]
    public async Task<IActionResult> AuditPdf([FromQuery] DutyAuditQuery query, CancellationToken ct)
    {
        if (await DeniedAsync(ct) is { } denied) return denied;
        var staff = query.StaffId is { } sid ? (await duties.StaffOptionsAsync(ct)).FirstOrDefault(s => s.Id == sid)?.FullName : null;
        return await PdfFile(PdfReports.DutyAudit(await duties.AuditReportAsync(query, ct), query, staff), ct);
    }

    [HttpGet("{id:int}/audit-record")]
    public async Task<IActionResult> AuditRecord(int id, CancellationToken ct)
    {
        if (await DeniedAsync(ct) is { } denied) return denied;
        var duty = await duties.GetAsync(id, ct);
        if (duty is null) return NotFoundResult("This duty no longer exists.");
        return PageOrPartial("AuditForm", new DutyAuditFormModel
        {
            Id = id, AuditNotes = duty.AuditNotes, IssuesEncountered = duty.IssuesEncountered,
            FollowUpRequired = duty.FollowUpRequired, Duty = duty,
        });
    }

    [HttpPost("{id:int}/audit-record"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AuditRecord(int id, DutyAuditFormModel model, CancellationToken ct)
    {
        if (await DeniedAsync(ct) is { } denied) return denied;
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await duties.UpdateAuditAsync(id, model.AuditNotes, model.IssuesEncountered, model.FollowUpRequired, ct);
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

    // ------------------------------------------------------------------ Morning Revision records

    /// <summary>+ Add Revision Record: a record of a Morning Revision session of the date (<paramref name="dutyId"/> preselects the session).</summary>
    [HttpGet("revisions/new")]
    public async Task<IActionResult> NewRevision(string? date, int? dutyId, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var model = new RevisionFormModel { Date = day, Options = await duties.RevisionOptionsAsync(day, ct) };
        model.DutyAssignmentId = model.Options.Sessions.Any(s => s.Id == dutyId) ? dutyId : model.Options.Sessions.Count == 1 ? model.Options.Sessions[0].Id : null;
        return PageOrPartial("RevisionForm", model);
    }

    [HttpPost("revisions/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> NewRevision(RevisionFormModel model, CancellationToken ct)
    {
        model.Id = null;
        return await SaveRevisionAsync(model, ct);
    }

    [HttpGet("revisions/{id:int}/edit")]
    public async Task<IActionResult> EditRevision(int id, CancellationToken ct)
    {
        var r = await duties.GetRevisionAsync(id, ct);
        if (r is null) return NotFoundResult("This revision record no longer exists.");
        return PageOrPartial("RevisionForm", new RevisionFormModel
        {
            Id = r.Id, Date = r.Date, DutyAssignmentId = r.DutyAssignmentId, SchoolClassId = r.ClassId, TeacherId = r.TeacherId,
            Lesson = r.Lesson, EntryTime = r.EntryTime, ExitTime = r.ExitTime, Options = await duties.RevisionOptionsAsync(r.Date, ct),
        });
    }

    [HttpPost("revisions/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRevision(int id, RevisionFormModel model, CancellationToken ct)
    {
        model.Id = id;
        return await SaveRevisionAsync(model, ct);
    }

    [HttpPost("revisions/{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRevision(int id, CancellationToken ct)
    {
        var r = await duties.GetRevisionAsync(id, ct);
        if (r is null) return NotFoundResult("This revision record no longer exists.");
        return Finish(await duties.DeleteRevisionAsync(id, ct), "Revision record removed (kept in the session's audit trail).",
            $"{DutyComponents.Href(DutyComponents.Get(DutyService.RevisionComponent))}?date={SchoolWeek.Iso(r.Date)}");
    }

    private async Task<IActionResult> SaveRevisionAsync(RevisionFormModel model, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await duties.SaveRevisionAsync(model.Id, model.ToInput(), ct);
            if (result.Succeeded) return Saved(model.Id is null ? "Revision record saved." : "Revision record updated.", DutyComponents.Get(DutyService.RevisionComponent), model.Date);
            AddErrors(result);
        }
        model.Options = await duties.RevisionOptionsAsync(model.Date, ct);
        return FormView("RevisionForm", model);
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
        if (!DutyComponents.Exists(model.Component)) return NotFoundResult("Unknown duty component.");
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
