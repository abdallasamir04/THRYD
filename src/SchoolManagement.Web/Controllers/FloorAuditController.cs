using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Floor Audit &amp; Student Movement (Daily Operations): the floors and their areas, the three audit rounds of every
/// timetable session, inspections with their checklist, student access (leaving class and returning), history and
/// reports, and the configuration (managers). Permissions are checked by the services; the pages only hide what the
/// current person cannot do.
/// </summary>
[Route("floor-audit")]
public class FloorAuditController(
    IFloorAuditService audit, IFloorConfigService config, IStudentMovementService movements, IFloorAuditAccessService access,
    IExcelExportService excel, ISettingsService settings) : AppController
{
    public const string Title = "Floor Audit";

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private IActionResult Finish(ServiceResult result, string message, string redirect)
    {
        var error = result.Succeeded ? null : string.Join(" ", result.Errors.Select(e => e.Value).Distinct());
        // A form opened as a full page (not in a dialog) goes on to the page it belongs to after a save.
        var page = Request.HasFormContentType && Request.Form["_page"] == "1";
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message, page ? redirect : null) : AjaxFailure(error!, 422);
        Toast(error ?? message, result.Succeeded ? "success" : "error");
        return LocalRedirect(redirect);
    }

    private IActionResult Denied(string message)
    {
        if (IsAjax) return AjaxFailure(message, StatusCodes.Status403Forbidden);
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("Denied", message);
    }

    private static string Back(string? back, string fallback) => !string.IsNullOrEmpty(back) && back.StartsWith('/') && !back.StartsWith("//") ? back : fallback;

    // ---------------------------------------------------------------- dashboard and floors

    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, CancellationToken ct)
    {
        var model = await audit.HomeAsync(Date(date) ?? SchoolWeek.Today, ct);
        return IsAjax ? PartialView("_HomeBoard", model) : View(model);
    }

    [HttpGet("floors/{id:int}")]
    public async Task<IActionResult> Floor(int id, string? date, int? slot, int? round, CancellationToken ct)
    {
        var model = await audit.FloorAsync(id, Date(date) ?? SchoolWeek.Today, slot, round, ct);
        if (model is null) return NotFoundResult("This floor no longer exists.");
        return IsAjax ? PartialView("_FloorBoard", model) : View(model);
    }

    // ---------------------------------------------------------------- inspections

    /// <summary>Classroom &amp; Facility Inspections: every floor's areas for a session and round, each with Record Audit.</summary>
    [HttpGet("inspections")]
    public async Task<IActionResult> Inspections(string? date, int? slot, int? round, int? floor, string? show, CancellationToken ct)
    {
        var model = await audit.InspectionsAsync(Date(date) ?? SchoolWeek.Today, slot, round is >= 1 and <= 3 ? round : null, floor, show == "pending", ct);
        return IsAjax ? PartialView("_InspectionsBoard", model) : View(model);
    }

    [HttpGet("inspect")]
    public async Task<IActionResult> Inspect(int area, string? date, int slot, int round, CancellationToken ct)
    {
        var form = await audit.InspectionFormAsync(area, Date(date) ?? SchoolWeek.Today, slot, round, ct);
        return form is null ? NotFoundResult("This inspection is not on the schedule of that date.") : PageOrPartial("InspectionForm", form);
    }

    [HttpPost("inspect"), ValidateAntiForgeryToken, RequestSizeLimit(80 * 1024 * 1024)]
    public async Task<IActionResult> Inspect(InspectionFormModel form, string? submit, CancellationToken ct)
    {
        var complete = submit is "complete" or "correct";
        var uploads = Request.HasFormContentType
            ? Request.Form.Files.Where(f => f.Name == "Photos").Select(f => new UploadedFile(f.FileName, f.Length, f.OpenReadStream)).ToList()
            : [];
        var result = await audit.SaveInspectionAsync(form, complete, uploads, ct);
        if (result.Succeeded)
        {
            var message = submit == "correct" ? "Correction saved. The change is in the inspection's audit trail."
                : complete ? "Inspection completed. It is in Audit History with its answers."
                : submit == "save-stay" ? "Saved as In Progress. Keep going, or Complete Inspection when every required item is answered."
                : "Saved as In Progress. Continue it later from the area's card (Continue Inspection).";
            // "Save as In Progress" stays on the form; the others go to the inspection (its status, answers and trail).
            var next = submit == "save-stay"
                ? $"/floor-audit/inspect?area={form.AreaId}&date={form.Date}&slot={form.SlotId}&round={form.Round}"
                : $"/floor-audit/inspections/{result.EntityId}";
            return Finish(result, message, next);
        }
        AddErrors(result);
        var again = await audit.InspectionFormAsync(form.AreaId, Date(form.Date) ?? SchoolWeek.Today, form.SlotId, form.Round, ct);
        if (again is null) return Finish(result, "", "/floor-audit");
        again.Observations = form.Observations;
        again.Reason = form.Reason;
        again.Answers = form.Answers;
        again.Questions = again.Questions.Select(q => q with { Value = form.Answers.GetValueOrDefault(q.ItemId) }).ToList();
        again.Extras = form.Extras;
        return FormView("InspectionForm", again);
    }

    [HttpGet("inspections/{id:int}")]
    public async Task<IActionResult> Inspection(int id, CancellationToken ct)
    {
        var model = await audit.InspectionAsync(id, ct);
        return model is null ? NotFoundResult("This inspection no longer exists.") : PageOrPartial("Inspection", model);
    }

    [HttpGet("inspections/{id:int}/files/{fileId:int}")]
    public async Task<IActionResult> Attachment(int id, int fileId, CancellationToken ct)
    {
        var file = await audit.AttachmentAsync(id, fileId, ct);
        if (file is null) return NotFound();
        var (view, stream) = file.Value;
        var type = TaskAttachmentRules.ContentType(view.Extension);
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";
        Response.Headers.XContentTypeOptions = "nosniff";
        return view.OpensInBrowser ? File(stream, type, enableRangeProcessing: true) : File(stream, type, view.FileName);
    }

    [HttpGet("areas/{id:int}")]
    public async Task<IActionResult> Area(int id, CancellationToken ct)
    {
        var model = await audit.AreaAsync(id, ct);
        return model is null ? NotFoundResult("This area no longer exists.") : View(model);
    }

    [HttpGet("areas/{id:int}/report.pdf")]
    public async Task<IActionResult> AreaPdf(int id, string? from, string? to, CancellationToken ct)
    {
        var model = await audit.AreaAsync(id, ct);
        if (model is null) return NotFound();
        var f = Date(from);
        var t = Date(to);
        var rows = await config.HistoryExportAsync(new InspectionFilter(f ?? DateOnly.MinValue, t ?? DateOnly.MaxValue, AreaId: id), ct);
        return await PdfFile(FloorAuditReports.Area(model.Area, rows, f, t), ct);
    }

    // ---------------------------------------------------------------- history

    private InspectionFilter HistoryFilter(string? from, string? to, int? floor, int? area, int? slot, int? round, int? inspector, string? status, int page, int pageSize)
    {
        var today = SchoolWeek.Today;
        var f = Date(from) ?? today.AddDays(-6);
        var t = Date(to) ?? today;
        if (t < f) (f, t) = (t, f);
        return new InspectionFilter(f, t, floor, area, slot, round is >= 1 and <= 3 ? round : null, inspector,
            status is "completed" or "in-progress" or "issues" or "no-issues" ? status : null, Math.Max(1, page), pageSize <= 0 ? 25 : pageSize);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(string? from, string? to, int? floor, int? area, int? slot, int? round, int? inspector, string? status,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var model = await config.HistoryAsync(HistoryFilter(from, to, floor, area, slot, round, inspector, status, page, pageSize), ct);
        return IsAjax ? PartialView("_HistoryTable", model) : View(model);
    }

    [HttpGet("history/pdf")]
    public async Task<IActionResult> HistoryPdf(string? from, string? to, int? floor, int? area, int? slot, int? round, int? inspector, string? status, CancellationToken ct)
    {
        var filter = HistoryFilter(from, to, floor, area, slot, round, inspector, status, 1, 25);
        var page = await config.HistoryAsync(filter, ct);
        var rows = await config.HistoryExportAsync(filter, ct);
        return await PdfFile(FloorAuditReports.History(filter, rows, FilterSummary(page)), ct);
    }

    [HttpGet("history/excel")]
    public async Task<IActionResult> HistoryExcel(string? from, string? to, int? floor, int? area, int? slot, int? round, int? inspector, string? status, CancellationToken ct)
    {
        var filter = HistoryFilter(from, to, floor, area, slot, round, inspector, status, 1, 25);
        var page = await config.HistoryAsync(filter, ct);
        var rows = await config.HistoryExportAsync(filter, ct);
        var school = (await settings.GetProfileAsync(ct)).SchoolName;
        var bytes = excel.FloorAudit(rows, FilterSummary(page), school);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            PdfFileName.From(["Floor Audit Report", SchoolWeek.Iso(filter.From), SchoolWeek.Iso(filter.To)]).Replace(".pdf", ".xlsx"));
    }

    private static string FilterSummary(InspectionHistoryPage p)
    {
        var f = p.Filter;
        var parts = new List<string> { f.From == f.To ? f.From.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : $"{f.From:d MMM yyyy} – {f.To:d MMM yyyy}" };
        if (f.FloorId is { } fl) parts.Add(p.Floors.FirstOrDefault(x => x.Id == fl).Name ?? "");
        if (f.AreaId is { } a) parts.Add(p.Areas.FirstOrDefault(x => x.Id == a)?.Name ?? "");
        if (f.SlotId is { } s) parts.Add(p.Sessions.FirstOrDefault(x => x.Id == s).Name ?? "");
        if (f.Round is { } r) parts.Add(FloorAuditRounds.Label(r));
        if (f.InspectorId is { } i) parts.Add(p.Inspectors.FirstOrDefault(x => x.Id == i).Name ?? "");
        if (f.Status is { } st) parts.Add(st switch { "completed" => "Completed", "in-progress" => "In progress", "issues" => "With issues", _ => "Without issues" });
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }

    // ---------------------------------------------------------------- student access

    private MovementFilter MovementsFilter(bool history, string? date, string? from, string? to, int? floor, int? classId, string? student, string? destination,
        string? status, int page, int pageSize)
    {
        var today = SchoolWeek.Today;
        var d = Date(date);
        var f = d ?? Date(from) ?? (history ? today.AddDays(-6) : today);
        var t = d ?? Date(to) ?? today;
        if (t < f) (f, t) = (t, f);
        var allowed = history ? status is "returned" or "voided" : status is "outside" or "overdue";
        return new MovementFilter(f, t, floor, classId, string.IsNullOrWhiteSpace(student) ? null : student.Trim(),
            string.IsNullOrWhiteSpace(destination) ? null : destination, allowed ? status : null, Math.Max(1, page), pageSize <= 0 ? 50 : pageSize, history);
    }

    /// <summary>Student Access: the live board of students outside their classrooms (every open movement).</summary>
    [HttpGet("movements")]
    public async Task<IActionResult> Movements(int? floor, [FromQuery(Name = "class")] int? classId, string? student, string? destination, string? status,
        int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        var model = await movements.PageAsync(MovementsFilter(false, null, null, null, floor, classId, student, destination, status, page, pageSize), ct);
        return IsAjax ? PartialView("_MovementList", model) : View(model);
    }

    /// <summary>Student Access → Movement History: returned and voided movements in a date range.</summary>
    [HttpGet("movements/history")]
    public async Task<IActionResult> MovementHistory(string? date, string? from, string? to, int? floor, [FromQuery(Name = "class")] int? classId, string? student,
        string? destination, string? status, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var model = await movements.PageAsync(MovementsFilter(true, date, from, to, floor, classId, student, destination, status, page, pageSize), ct);
        return IsAjax ? PartialView("_MovementHistoryList", model) : View("MovementHistory", model);
    }

    [HttpGet("movements/pdf")]
    public Task<IActionResult> MovementsPdf(int? floor, [FromQuery(Name = "class")] int? classId, string? student, string? destination, string? status, CancellationToken ct) =>
        MovementPdfAsync(MovementsFilter(false, null, null, null, floor, classId, student, destination, status, 1, 50), ct);

    [HttpGet("movements/excel")]
    public Task<IActionResult> MovementsExcel(int? floor, [FromQuery(Name = "class")] int? classId, string? student, string? destination, string? status, CancellationToken ct) =>
        MovementExcelAsync(MovementsFilter(false, null, null, null, floor, classId, student, destination, status, 1, 50), ct);

    [HttpGet("movements/history/pdf")]
    public Task<IActionResult> MovementHistoryPdf(string? date, string? from, string? to, int? floor, [FromQuery(Name = "class")] int? classId, string? student,
        string? destination, string? status, CancellationToken ct) =>
        MovementPdfAsync(MovementsFilter(true, date, from, to, floor, classId, student, destination, status, 1, 50), ct);

    [HttpGet("movements/history/excel")]
    public Task<IActionResult> MovementHistoryExcel(string? date, string? from, string? to, int? floor, [FromQuery(Name = "class")] int? classId, string? student,
        string? destination, string? status, CancellationToken ct) =>
        MovementExcelAsync(MovementsFilter(true, date, from, to, floor, classId, student, destination, status, 1, 50), ct);

    private async Task<IActionResult> MovementPdfAsync(MovementFilter filter, CancellationToken ct)
    {
        var page = await movements.PageAsync(filter, ct);
        return await PdfFile(FloorAuditReports.Movements(filter, await movements.ExportRowsAsync(filter, ct), page.Counts, MovementSummary(page)), ct);
    }

    private async Task<IActionResult> MovementExcelAsync(MovementFilter filter, CancellationToken ct)
    {
        var page = await movements.PageAsync(filter, ct);
        var school = (await settings.GetProfileAsync(ct)).SchoolName;
        var bytes = excel.StudentMovements(await movements.ExportRowsAsync(filter, ct), page.Counts, MovementSummary(page), school);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            PdfFileName.From([filter.History ? "Student Movement History" : "Students Outside", SchoolWeek.Iso(filter.From), SchoolWeek.Iso(filter.To)]).Replace(".pdf", ".xlsx"));
    }

    private static string MovementSummary(MovementPage p)
    {
        var f = p.Filter;
        var parts = new List<string>
        {
            !f.History ? "Students outside now" : f.From == f.To ? f.From.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : $"{f.From:d MMM yyyy} – {f.To:d MMM yyyy}",
        };
        if (f.FloorId is { } fl) parts.Add(p.Floors.FirstOrDefault(x => x.Id == fl).Name ?? "");
        if (f.ClassId is { } c) parts.Add(p.Classes.FirstOrDefault(x => x.Id == c).Name ?? "");
        if (f.Student is { } s) parts.Add($"Student “{s}”");
        if (f.Destination is { } d) parts.Add($"To {d}");
        if (f.Status is { } st) parts.Add(char.ToUpperInvariant(st[0]) + st[1..]);
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }

    [HttpGet("movements/new")]
    public async Task<IActionResult> NewMovement(int? floor, int? student, CancellationToken ct)
    {
        var user = await access.CurrentAsync(ct);
        if (!user.HasIdentity) return Denied("Choose who you are under “Working as” (the panel at the top of the Floor Audit pages) to record student movements.");
        if (!user.CanRecordAnyMovement) return Denied("You are not allowed to record student movements. A Floor Audit manager can give you access.");
        var model = await movements.FormAsync(new MovementFormModel { Floor = floor, StudentId = student }, ct);
        model.RecordedBy = user.Name;
        return PageOrPartial("MovementForm", model);
    }

    [HttpPost("movements/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> NewMovement(MovementFormModel form, CancellationToken ct)
    {
        var result = await movements.RecordAsync(form, ct);
        var back = form.Floor is { } f ? $"/floor-audit/floors/{f}#student-access" : "/floor-audit/movements";
        if (result.Succeeded) return Finish(result, "Movement recorded: the student is shown as outside until marked returned.", back);
        AddErrors(result);
        var model = await movements.FormAsync(form, ct);
        model.RecordedBy = (await access.CurrentAsync(ct)).Name;
        if (form.StudentId is { } sid) model.Existing = await movements.OpenForStudentAsync(sid, ct);
        return FormView("MovementForm", model);
    }

    [HttpGet("movements/{id:int}/return")]
    public async Task<IActionResult> ReturnForm(int id, string? back, CancellationToken ct)
    {
        var row = await movements.RowAsync(id, ct);
        if (row is null) return NotFoundResult("This movement no longer exists.");
        ViewData["Back"] = back;
        return PageOrPartial("ReturnForm", row);
    }

    [HttpPost("movements/{id:int}/return"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Return(int id, string? back, CancellationToken ct) =>
        Finish(await movements.ReturnAsync(id, null, ct), "Marked returned now. The time outside is final.", Back(back, "/floor-audit/movements"));

    [HttpGet("movements/{id:int}/void")]
    public async Task<IActionResult> VoidForm(int id, string? back, CancellationToken ct)
    {
        var user = await access.CurrentAsync(ct);
        if (!user.CanManage) return Denied("Only a Floor Audit manager can void a movement.");
        var row = await movements.RowAsync(id, ct);
        if (row is null) return NotFoundResult("This movement no longer exists.");
        ViewData["Back"] = back;
        return PageOrPartial("VoidForm", row);
    }

    [HttpPost("movements/{id:int}/void"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Void(int id, string? reason, string? back, CancellationToken ct) =>
        Finish(await movements.VoidAsync(id, reason, ct), "Movement voided. It stays in the history, marked as voided.", Back(back, "/floor-audit/movements"));

    // ---------------------------------------------------------------- manage (managers)

    private async Task<IActionResult?> ManagersOnlyAsync(CancellationToken ct)
    {
        var user = await access.CurrentAsync(ct);
        return user.CanManage && user.HasIdentity ? null : Denied("Only a Floor Audit manager (a Management Team member, or staff given the Manager role) can change the floor configuration.");
    }

    [HttpGet("manage")]
    public async Task<IActionResult> Manage(string? tab, CancellationToken ct)
    {
        var t = tab is "checklist" or "access" or "settings" ? tab : "floors";
        var model = await config.ManageAsync(t, ct);
        return IsAjax ? PartialView("_ManageBody", model) : View(model);
    }

    [HttpGet("manage/floors/new")]
    public async Task<IActionResult> NewFloor(CancellationToken ct) => await ManagersOnlyAsync(ct) ?? PageOrPartial("FloorForm", new FloorForm());

    [HttpGet("manage/floors/{id:int}/edit")]
    public async Task<IActionResult> EditFloor(int id, CancellationToken ct)
    {
        if (await ManagersOnlyAsync(ct) is { } denied) return denied;
        var f = (await config.ManageAsync("floors", ct)).Floors.FirstOrDefault(x => x.Id == id);
        return f is null ? NotFoundResult("This floor no longer exists.") : PageOrPartial("FloorForm", new FloorForm { Id = f.Id, Name = f.Name, SortOrder = f.SortOrder });
    }

    [HttpPost("manage/floors/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFloor(FloorForm form, CancellationToken ct)
    {
        var result = await config.SaveFloorAsync(form, ct);
        if (result.Succeeded) return Finish(result, form.Id is null ? "Floor added." : "Floor saved.", "/floor-audit/manage");
        AddErrors(result);
        return FormView("FloorForm", form);
    }

    [HttpPost("manage/floors/{id:int}/active"), ValidateAntiForgeryToken]
    public async Task<IActionResult> FloorActive(int id, bool active, CancellationToken ct) =>
        Finish(await config.SetFloorActiveAsync(id, active, ct), active ? "Floor activated." : "Floor deactivated. Its history stays.", "/floor-audit/manage");

    [HttpGet("manage/classrooms/assign")]
    public async Task<IActionResult> AssignClassrooms(int? floor, CancellationToken ct) =>
        await ManagersOnlyAsync(ct) ?? PageOrPartial("ClassroomForm", await config.ClassroomFormAsync(floor, null, ct));

    [HttpPost("manage/classrooms/assign"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignClassrooms(ClassroomForm form, CancellationToken ct)
    {
        var result = await config.AssignClassroomsAsync(form.FloorId ?? 0, form.ClassIds, ct);
        if (result.Succeeded) return Finish(result, form.ClassIds.Count == 1 ? "Classroom assigned." : $"{form.ClassIds.Count} classrooms assigned.", "/floor-audit/manage");
        AddErrors(result);
        var again = await config.ClassroomFormAsync(form.FloorId, null, ct);
        again.ClassIds = form.ClassIds;
        return FormView("ClassroomForm", again);
    }

    [HttpGet("manage/areas/{id:int}/move")]
    public async Task<IActionResult> MoveArea(int id, CancellationToken ct) =>
        await ManagersOnlyAsync(ct) ?? PageOrPartial("MoveAreaForm", await config.ClassroomFormAsync(null, id, ct));

    [HttpPost("manage/areas/{id:int}/move"), ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveArea(int id, int? floorId, CancellationToken ct)
    {
        var result = await config.MoveAreaAsync(id, floorId ?? 0, ct);
        if (result.Succeeded) return Finish(result, "Moved. Earlier inspections keep the floor they were made on.", "/floor-audit/manage");
        AddErrors(result);
        var again = await config.ClassroomFormAsync(floorId, id, ct);
        return FormView("MoveAreaForm", again);
    }

    [HttpPost("manage/areas/{id:int}/active"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AreaActive(int id, bool active, CancellationToken ct) =>
        Finish(await config.SetAreaActiveAsync(id, active, ct), active ? "Area activated." : "Area deactivated. Its inspection history stays.", "/floor-audit/manage");

    [HttpGet("manage/facilities/new")]
    public async Task<IActionResult> NewFacility(int? floor, CancellationToken ct) =>
        await ManagersOnlyAsync(ct) ?? PageOrPartial("FacilityForm", (await config.FacilityFormAsync(null, floor, ct))!);

    [HttpGet("manage/facilities/{id:int}/edit")]
    public async Task<IActionResult> EditFacility(int id, CancellationToken ct)
    {
        if (await ManagersOnlyAsync(ct) is { } denied) return denied;
        var form = await config.FacilityFormAsync(id, null, ct);
        return form is null ? NotFoundResult("This facility no longer exists.") : PageOrPartial("FacilityForm", form);
    }

    [HttpPost("manage/facilities/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFacility(FacilityForm form, CancellationToken ct)
    {
        var result = await config.SaveFacilityAsync(form, ct);
        if (result.Succeeded) return Finish(result, form.Id is null ? "Facility added." : "Facility saved.", "/floor-audit/manage");
        AddErrors(result);
        var again = (await config.FacilityFormAsync(null, form.FloorId, ct))!;
        form.Floors = again.Floors;
        return FormView("FacilityForm", form);
    }

    [HttpGet("manage/items/new")]
    public async Task<IActionResult> NewItem(CancellationToken ct) => await ManagersOnlyAsync(ct) ?? PageOrPartial("ItemForm", new AuditItemForm());

    [HttpGet("manage/items/{id:int}/edit")]
    public async Task<IActionResult> EditItem(int id, CancellationToken ct)
    {
        if (await ManagersOnlyAsync(ct) is { } denied) return denied;
        var form = await config.ItemFormAsync(id, ct);
        return form is null ? NotFoundResult("This item no longer exists.") : PageOrPartial("ItemForm", form);
    }

    [HttpPost("manage/items/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveItem(AuditItemForm form, CancellationToken ct)
    {
        var result = await config.SaveItemAsync(form, ct);
        if (result.Succeeded) return Finish(result, form.Id is null ? "Audit item added. It appears in inspections started from now on." : "Audit item saved. Inspections already started keep the question as it was.", "/floor-audit/manage?tab=checklist");
        AddErrors(result);
        return FormView("ItemForm", form);
    }

    [HttpPost("manage/items/{id:int}/active"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ItemActive(int id, bool active, CancellationToken ct) =>
        Finish(await config.SetItemActiveAsync(id, active, ct), active ? "Item activated." : "Item deactivated. Recorded answers are kept.", "/floor-audit/manage?tab=checklist");

    [HttpGet("manage/access/new")]
    public async Task<IActionResult> NewAccess(CancellationToken ct) => await ManagersOnlyAsync(ct) ?? PageOrPartial("AccessForm", await config.AccessFormAsync(null, ct));

    [HttpPost("manage/access/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> NewAccess(AccessForm form, CancellationToken ct)
    {
        var result = await config.GrantAccessAsync(form, ct);
        if (result.Succeeded) return Finish(result, "Access given.", "/floor-audit/manage?tab=access");
        AddErrors(result);
        return FormView("AccessForm", await config.AccessFormAsync(form, ct));
    }

    [HttpPost("manage/access/{id:int}/end"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EndAccess(int id, CancellationToken ct) =>
        Finish(await config.EndAccessAsync(id, ct), "Access ended.", "/floor-audit/manage?tab=access");

    [HttpPost("manage/settings"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(int? overdueMinutes, CancellationToken ct) =>
        Finish(await movements.SaveOverdueMinutesAsync(overdueMinutes, ct), "Setting saved.", "/floor-audit/manage?tab=settings");
}
