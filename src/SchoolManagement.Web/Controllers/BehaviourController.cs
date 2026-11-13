using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Students → Behavioural / Discipline: a landing page with two independent sections — Incident (official
/// incident reports) and Discipline (its own landing page, to be implemented later). Incident records are
/// confidential: responses are never cached or indexed, every change needs a named author and is audited,
/// and every PDF export is logged.
/// </summary>
[Route("students/behaviour")]
public class BehaviourController(IIncidentService incidents, ISettingsService settings, IDutyService duties, ICurrentUser currentUser, SchoolDbContext db) : AppController
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        // Confidential student information: not stored by browsers or proxies, not indexed.
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        base.OnActionExecuting(context);
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var open = await db.Incidents.CountAsync(i => !i.IsClosed, ct);
        var total = await db.Incidents.CountAsync(ct);
        return View("~/Views/Staff/Hub.cshtml", new HubViewModel
        {
            Title = "Behavioural / Discipline",
            Subtitle = "Two separate sections: official incident reports, and discipline. Choose a section.",
            Crumbs = [("Dashboard", "/"), ("Students", "/students/hub"), ("Behavioural / Discipline", null)],
            Large = true,
            Nav = "Behaviour",
            Cards =
            [
                new("/students/behaviour/incidents", "flag", "Incident",
                    "Official student incident reports: students involved, chronological timeline, investigation findings and actions, administrative sign-off and PDF.",
                    $"{Ui.Plural(total, "report", "reports")} · {open} open", "rose"),
                new("/students/behaviour/discipline", "shield-check", "Discipline",
                    "The discipline section, kept separate from incident reports.", "Section ready — records to be added", "indigo"),
            ],
        });
    }

    [HttpGet("discipline")]
    public IActionResult Discipline() => View();

    // ------------------------------------------------------------------ incident list, details, history

    [HttpGet("incidents")]
    public async Task<IActionResult> Incidents(string? search, string? category, string? department, IncidentStatus? status, IncidentSeverity? severity,
        string? from, string? to, string? closure, CancellationToken ct)
    {
        var query = new IncidentListQuery(Blank(search), Blank(category), Blank(department), status, severity, Date(from), Date(to),
            closure is "open" or "closed" ? closure : null);
        return View(new IncidentListViewModel
        {
            Incidents = await incidents.ListAsync(query, ct), Query = query, Suggestions = await incidents.SuggestionsAsync(ct),
            CanEdit = currentUser.Name is not null,
        });
    }

    [HttpGet("incidents/{id:int}")]
    public async Task<IActionResult> Incident(int id, CancellationToken ct)
    {
        var details = await incidents.GetAsync(id, ct);
        if (details is null) return NotFound();
        return View(new IncidentPageViewModel { Incident = details, SchoolName = (await settings.GetProfileAsync(ct)).SchoolName, Identified = currentUser.Name is not null });
    }

    [HttpGet("incidents/student/{studentId:int}")]
    public async Task<IActionResult> StudentIncidents(int studentId, CancellationToken ct)
    {
        var s = await db.Students.AsNoTracking().Where(x => x.Id == studentId).Select(x => new { x.FullName, ClassName = x.SchoolClass.Name }).FirstOrDefaultAsync(ct);
        if (s is null) return NotFound();
        return View(new StudentIncidentsViewModel
        {
            StudentId = studentId, StudentName = s.FullName, ClassName = s.ClassName,
            Incidents = await incidents.ListAsync(new IncidentListQuery(StudentId: studentId), ct),
        });
    }

    /// <summary>The official report as a PDF, with every saved value. The export is written to the incident's history.</summary>
    [HttpGet("incidents/{id:int}/pdf")]
    public async Task<IActionResult> Pdf(int id, CancellationToken ct)
    {
        var details = await incidents.GetAsync(id, ct);
        if (details is null) return NotFound();
        var report = IncidentReports.Official(details, (await settings.GetProfileAsync(ct)).SchoolName);
        var result = await PdfFile(report, ct);
        await incidents.LogExportAsync(id, ct);
        return result;
    }

    // ------------------------------------------------------------------ A. create / edit header

    [HttpGet("incidents/new")]
    public async Task<IActionResult> Create(CancellationToken ct) =>
        PageOrPartial("HeaderForm", new IncidentHeaderForm
        {
            Date = SchoolWeek.Today, Severity = IncidentSeverity.Level1, Status = IncidentStatus.Reported, Suggestions = await incidents.SuggestionsAsync(ct),
        });

    [HttpPost("incidents/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(IncidentHeaderForm form, CancellationToken ct)
    {
        var result = await incidents.CreateAsync(form.ToInput(), ct);
        if (result.Succeeded)
        {
            var reference = await db.Incidents.Where(i => i.Id == result.EntityId).Select(i => i.ReferenceId).FirstAsync(ct);
            return Saved($"Incident report {reference} created. Add the students, timeline and actions.", $"/students/behaviour/incidents/{result.EntityId}", navigate: true);
        }
        AddErrors(result);
        form.Suggestions = await incidents.SuggestionsAsync(ct);
        return FormView("HeaderForm", form);
    }

    [HttpGet("incidents/{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var d = await incidents.GetAsync(id, ct);
        if (d is null) return NotFoundResult("This incident report no longer exists.");
        return PageOrPartial("HeaderForm", new IncidentHeaderForm
        {
            Id = id, Reference = d.Reference, Category = d.Category, Date = d.Date, Department = d.Department, Severity = d.Severity, Status = d.Status,
            Suggestions = await incidents.SuggestionsAsync(ct),
        });
    }

    [HttpPost("incidents/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, IncidentHeaderForm form, CancellationToken ct)
    {
        form.Id = id;
        var result = await incidents.UpdateHeaderAsync(id, form.ToInput(), ct);
        if (result.Succeeded) return Saved("Incident information saved.", Details(id));
        return await Again("HeaderForm", form, result, async () => { form.Suggestions = await incidents.SuggestionsAsync(ct); form.Reference = await RefAsync(id, ct); });
    }

    // ------------------------------------------------------------------ B. students involved

    [HttpGet("incidents/{id:int}/students/add")]
    public async Task<IActionResult> AddStudent(int id, CancellationToken ct) =>
        await RefAsync(id, ct) is { } reference
            ? PageOrPartial("StudentForm", new IncidentStudentForm { IncidentId = id, Reference = reference, Role = IncidentRole.ReportedConduct, Students = await StudentsAsync(ct) })
            : NotFoundResult("This incident report no longer exists.");

    [HttpPost("incidents/{id:int}/students/add"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddStudent(int id, IncidentStudentForm form, CancellationToken ct)
    {
        form.IncidentId = id;
        var result = await incidents.AddStudentAsync(id, form.ToInput(), ct);
        if (result.Succeeded) return Saved("Student added to the report.", Details(id));
        return await Again("StudentForm", form, result, async () => { form.Reference = await RefAsync(id, ct); form.Students = await StudentsAsync(ct); });
    }

    [HttpGet("incidents/{id:int}/students/{rowId:int}/edit")]
    public async Task<IActionResult> EditStudent(int id, int rowId, CancellationToken ct)
    {
        var d = await incidents.GetAsync(id, ct);
        var row = d?.Students.FirstOrDefault(s => s.Id == rowId);
        if (d is null || row is null) return NotFoundResult("This student is no longer in the report.");
        return PageOrPartial("StudentForm", new IncidentStudentForm
        {
            IncidentId = id, RowId = rowId, Reference = d.Reference, StudentId = row.StudentId, StudentName = $"{row.Name} · {row.GradeSection}",
            Role = row.Role, RoleOther = row.RoleOther, Involvement = row.Involvement,
        });
    }

    [HttpPost("incidents/{id:int}/students/{rowId:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditStudent(int id, int rowId, IncidentStudentForm form, CancellationToken ct)
    {
        (form.IncidentId, form.RowId) = (id, rowId);
        var result = await incidents.UpdateStudentAsync(id, rowId, form.ToInput(), ct);
        if (result.Succeeded) return Saved("Student involvement saved.", Details(id));
        return await Again("StudentForm", form, result, async () =>
        {
            form.Reference = await RefAsync(id, ct);
            form.StudentName = (await incidents.GetAsync(id, ct))?.Students.FirstOrDefault(s => s.Id == rowId) is { } r ? $"{r.Name} · {r.GradeSection}" : null;
        });
    }

    [HttpPost("incidents/{id:int}/students/{rowId:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveStudent(int id, int rowId, CancellationToken ct) =>
        Done(await incidents.RemoveStudentAsync(id, rowId, ct), "Student removed from the report.", Details(id));

    // ------------------------------------------------------------------ C. timeline

    [HttpGet("incidents/{id:int}/timeline/add")]
    public async Task<IActionResult> AddTimeline(int id, CancellationToken ct)
    {
        var d = await incidents.GetAsync(id, ct);
        if (d is null) return NotFoundResult("This incident report no longer exists.");
        return PageOrPartial("TimelineForm", new TimelineForm { IncidentId = id, Reference = d.Reference, Involved = d.Students, Suggestions = await incidents.SuggestionsAsync(ct) });
    }

    [HttpPost("incidents/{id:int}/timeline/add"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTimeline(int id, TimelineForm form, CancellationToken ct)
    {
        form.IncidentId = id;
        var result = await incidents.AddTimelineAsync(id, form.ToInput(), ct);
        if (result.Succeeded) return Saved("Timeline event added.", Details(id));
        return await Again("TimelineForm", form, result, () => FillTimelineAsync(form, ct));
    }

    [HttpGet("incidents/{id:int}/timeline/{entryId:int}/edit")]
    public async Task<IActionResult> EditTimeline(int id, int entryId, CancellationToken ct)
    {
        var d = await incidents.GetAsync(id, ct);
        var e = d?.Timeline.FirstOrDefault(t => t.Id == entryId);
        if (d is null || e is null) return NotFoundResult("This timeline event no longer exists.");
        return PageOrPartial("TimelineForm", new TimelineForm
        {
            IncidentId = id, EntryId = entryId, Reference = d.Reference, Involved = d.Students, Suggestions = await incidents.SuggestionsAsync(ct),
            TimePeriod = e.TimePeriod, Time = e.Time, Location = e.Location, PresentIds = e.Present.Select(p => p.IncidentStudentId).ToList(),
            OthersPresent = e.OthersPresent, Account = e.Account,
        });
    }

    [HttpPost("incidents/{id:int}/timeline/{entryId:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTimeline(int id, int entryId, TimelineForm form, CancellationToken ct)
    {
        (form.IncidentId, form.EntryId) = (id, entryId);
        var result = await incidents.UpdateTimelineAsync(id, entryId, form.ToInput(), ct);
        if (result.Succeeded) return Saved("Timeline event saved.", Details(id));
        return await Again("TimelineForm", form, result, () => FillTimelineAsync(form, ct));
    }

    [HttpPost("incidents/{id:int}/timeline/{entryId:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTimeline(int id, int entryId, CancellationToken ct) =>
        Done(await incidents.DeleteTimelineAsync(id, entryId, ct), "Timeline event deleted.", Details(id));

    [HttpPost("incidents/{id:int}/timeline/{entryId:int}/move"), ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveTimeline(int id, int entryId, int direction, CancellationToken ct) =>
        Done(await incidents.MoveTimelineAsync(id, entryId, direction, ct), direction < 0 ? "Event moved up." : "Event moved down.", Details(id));

    // ------------------------------------------------------------------ D. findings & actions

    [HttpGet("incidents/{id:int}/actions/add")]
    public async Task<IActionResult> AddAction(int id, CancellationToken ct) =>
        await RefAsync(id, ct) is { } reference
            ? PageOrPartial("ActionForm", new IncidentActionForm { IncidentId = id, Reference = reference, Status = IncidentActionStatus.Pending, Suggestions = await incidents.SuggestionsAsync(ct) })
            : NotFoundResult("This incident report no longer exists.");

    [HttpPost("incidents/{id:int}/actions/add"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAction(int id, IncidentActionForm form, CancellationToken ct)
    {
        form.IncidentId = id;
        var result = await incidents.AddActionAsync(id, form.ToInput(), ct);
        if (result.Succeeded) return Saved("Action added.", Details(id));
        return await Again("ActionForm", form, result, async () => { form.Reference = await RefAsync(id, ct); form.Suggestions = await incidents.SuggestionsAsync(ct); });
    }

    [HttpGet("incidents/{id:int}/actions/{actionId:int}/edit")]
    public async Task<IActionResult> EditAction(int id, int actionId, CancellationToken ct)
    {
        var d = await incidents.GetAsync(id, ct);
        var a = d?.Actions.FirstOrDefault(x => x.Id == actionId);
        if (d is null || a is null) return NotFoundResult("This action no longer exists.");
        return PageOrPartial("ActionForm", new IncidentActionForm
        {
            IncidentId = id, ActionId = actionId, Reference = d.Reference, ActionItem = a.ActionItem, Finding = a.Finding, Details = a.Details, Status = a.Status,
            Suggestions = await incidents.SuggestionsAsync(ct),
        });
    }

    [HttpPost("incidents/{id:int}/actions/{actionId:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditAction(int id, int actionId, IncidentActionForm form, CancellationToken ct)
    {
        (form.IncidentId, form.ActionId) = (id, actionId);
        var result = await incidents.UpdateActionAsync(id, actionId, form.ToInput(), ct);
        if (result.Succeeded) return Saved("Action saved.", Details(id));
        return await Again("ActionForm", form, result, async () => { form.Reference = await RefAsync(id, ct); form.Suggestions = await incidents.SuggestionsAsync(ct); });
    }

    [HttpPost("incidents/{id:int}/actions/{actionId:int}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ActionStatus(int id, int actionId, IncidentActionStatus status, CancellationToken ct) =>
        Done(await incidents.SetActionStatusAsync(id, actionId, status, ct), $"Action status: {IncidentLabels.ActionStatus(status)}.", Details(id));

    [HttpPost("incidents/{id:int}/actions/{actionId:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAction(int id, int actionId, CancellationToken ct) =>
        Done(await incidents.DeleteActionAsync(id, actionId, ct), "Action deleted.", Details(id));

    // ------------------------------------------------------------------ E. sign-off & closure

    [HttpGet("incidents/{id:int}/signoff")]
    public async Task<IActionResult> SignOff(int id, CancellationToken ct)
    {
        var d = await incidents.GetAsync(id, ct);
        if (d is null) return NotFoundResult("This incident report no longer exists.");
        return PageOrPartial("SignOffForm", new SignOffForm
        {
            IncidentId = id, Reference = d.Reference, PreparedById = d.PreparedById ?? currentUser.StaffId,
            School = d.SignOffSchool ?? (await settings.GetProfileAsync(ct)).SchoolName, ReportDate = d.ReportDate ?? SchoolWeek.Today,
            ReviewedById = d.ReviewedById, Approval = d.Approval, ApprovalNotes = d.ApprovalNotes, Staff = await duties.StaffOptionsAsync(ct),
        });
    }

    [HttpPost("incidents/{id:int}/signoff"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOff(int id, SignOffForm form, CancellationToken ct)
    {
        form.IncidentId = id;
        var result = await incidents.SaveSignOffAsync(id, form.ToInput(), ct);
        if (result.Succeeded) return Saved("Administrative sign-off saved.", Details(id));
        return await Again("SignOffForm", form, result, async () => { form.Reference = await RefAsync(id, ct); form.Staff = await duties.StaffOptionsAsync(ct); });
    }

    [HttpPost("incidents/{id:int}/close"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, CancellationToken ct) =>
        Done(await incidents.CloseAsync(id, ct), "Record closed. The report is now read-only.", Details(id));

    [HttpGet("incidents/{id:int}/reopen")]
    public async Task<IActionResult> Reopen(int id, CancellationToken ct) =>
        await RefAsync(id, ct) is { } reference
            ? PageOrPartial("ReopenForm", new ReopenForm { IncidentId = id, Reference = reference })
            : NotFoundResult("This incident report no longer exists.");

    [HttpPost("incidents/{id:int}/reopen"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(int id, ReopenForm form, CancellationToken ct)
    {
        form.IncidentId = id;
        var result = await incidents.ReopenAsync(id, form.Reason, ct);
        if (result.Succeeded) return Saved("Record reopened.", Details(id));
        return await Again("ReopenForm", form, result, async () => form.Reference = await RefAsync(id, ct));
    }

    // ------------------------------------------------------------------ helpers

    private static string Details(int id) => $"/students/behaviour/incidents/{id}";

    private async Task<string?> RefAsync(int id, CancellationToken ct) =>
        await db.Incidents.Where(i => i.Id == id).Select(i => i.ReferenceId).FirstOrDefaultAsync(ct);

    private async Task<IReadOnlyList<StudentPick>> StudentsAsync(CancellationToken ct) =>
        await db.Students.AsNoTracking().OrderBy(s => s.SchoolClass.SortOrder).ThenBy(s => s.FullName)
            .Select(s => new StudentPick(s.Id, s.FullName, s.SchoolClassId, s.SchoolClass.Name)).ToListAsync(ct);

    private async Task FillTimelineAsync(TimelineForm form, CancellationToken ct)
    {
        var d = await incidents.GetAsync(form.IncidentId, ct);
        form.Reference = d?.Reference;
        form.Involved = d?.Students ?? [];
        form.Suggestions = await incidents.SuggestionsAsync(ct);
    }

    /// <summary>
    /// Success. AJAX from a dialog: a toast and the page refreshes in place, or (navigate) goes to the new report —
    /// the toast is also kept for the next page. Without JavaScript: redirect with a toast.
    /// </summary>
    private IActionResult Saved(string message, string redirect, bool navigate = false)
    {
        if (IsAjax && !navigate) return AjaxSuccess(message);
        Toast(message);
        return IsAjax ? AjaxSuccess(message, redirect) : LocalRedirect(redirect);
    }

    /// <summary>A form that failed: messages without a field (closed record, no author…) as a toast; field errors shown in the form.</summary>
    private async Task<IActionResult> Again(string view, object form, ServiceResult result, Func<Task> fill)
    {
        if (result.Errors.All(e => e.Key.Length == 0) && IsAjax) return AjaxFailure(string.Join(" ", result.Errors.Select(e => e.Value)), 422);
        AddErrors(result);
        await fill();
        return FormView(view, form);
    }

    private IActionResult Done(ServiceResult result, string success, string redirect)
    {
        var message = result.Succeeded ? success : string.Join(" ", result.Errors.Select(e => e.Value).Distinct());
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 422);
        Toast(message, result.Succeeded ? "success" : "error");
        return LocalRedirect(redirect);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateOnly? Date(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
