using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// The task actions of the school's one Tasks system (any staff member can be assigned a task): create / edit / details /
/// status / delete, instruction attachments and the PDF report. The task list itself is the central Tasks page
/// (<see cref="TasksController"/>); the former separate Management Team tasks list now leads there.
/// </summary>
[Route("management/tasks")]
public class ManagementTasksController(IManagementTaskService tasks, ICurrentUser currentUser) : AppController
{
    /// <summary>
    /// The former Management Team Tasks page: removed, as every task is on the one Tasks page. Old links and bookmarks
    /// lead there with their filters (the same query: memberId, status, priority, search …).
    /// </summary>
    [HttpGet("")]
    public IActionResult Index() => RedirectPermanent(TasksController.Url + Request.QueryString.Value);

    /// <summary>Download Report: every task matching the page's filters (all pages), in its sort order.</summary>
    [HttpGet("pdf")]
    public async Task<IActionResult> Pdf([FromQuery] TaskListQuery query, CancellationToken ct)
    {
        var staff = await tasks.StaffAsync(ct);
        string? NameOf(int? id) => id is { } v ? staff.FirstOrDefault(s => s.Id == v)?.FullName : null;
        var department = query.Department == "management" ? "Management Team"
            : int.TryParse(query.Department, out var d) ? (await tasks.DepartmentsAsync(ct)).FirstOrDefault(x => x.Id == d)?.Name : null;
        return await PdfFile(Services.Pdf.PdfReports.Tasks(await tasks.ListAllAsync(query, ct), query, NameOf(query.MemberId), NameOf(query.ReviewerId), department), ct);
    }

    /// <summary>Task details: what is expected, who is involved, the deadline, the files and the history.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var task = await tasks.GetAsync(id, ct);
        if (task is null) return NotFoundResult("This task no longer exists.");
        return View(new TaskDetailsViewModel
        {
            Task = task,
            Attachments = await tasks.AttachmentsAsync(id, ct),
            History = await tasks.TaskHistoryAsync(id, ct),
            Remaining = TaskLabels.RemainingText(task.Status, task.DueDate, task.DueTime, SchoolTime.Current),
        });
    }

    /// <summary>
    /// The Add Task form. From Academic Departments: <c>?by=department</c> (Departments page: choose a department, then one
    /// of its members) or <c>?departmentId=</c> (a department's page: that department, fixed).
    /// </summary>
    [HttpGet("create")]
    public async Task<IActionResult> Create(int? memberId, int? departmentId, string? by, CancellationToken ct)
    {
        var model = new TaskFormModel { AssignedToId = memberId, DepartmentMode = departmentId is not null || by == "department" };
        await FillAsync(model, null, ct);
        if (model.DepartmentMode && departmentId is { } dept && model.Departments.Any(d => d.Id == dept))
            (model.DepartmentId, model.DepartmentLocked) = (dept, true);
        if (model.AssignedToId is { } id && model.Assignees.All(s => s.Id != id)) model.AssignedToId = null;
        return PageOrPartial("Form", model);
    }

    /// <summary>
    /// The executor options of a department (its active members), for the Add Task form when the department changes.
    /// Display only: the save checks the department and the membership again.
    /// </summary>
    [HttpGet("department-staff")]
    public async Task<IActionResult> DepartmentStaff(int departmentId, int? selected, CancellationToken ct)
    {
        if ((await tasks.DepartmentsAsync(ct)).All(d => d.Id != departmentId)) return NotFound();
        var members = (await tasks.StaffAsync(ct)).Where(s => s.DepartmentId == departmentId).ToList();
        return PartialView("_StaffOptions", ((IReadOnlyList<TaskStaffOption>)members, members.Any(s => s.Id == selected && s.IsActive) ? selected : null));
    }

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TaskFormModel model, CancellationToken ct)
    {
        model.Id = null;
        model.RemoveAttachmentIds = [];
        // New Task has no Status field: every new task starts as To Do, whatever a request posts (status changes come later).
        model.Status = ManagementTaskStatus.ToDo;
        ModelState.Remove(nameof(model.Status));
        if (model.DepartmentMode && model.DepartmentId is null) ModelState.AddModelError(nameof(model.DepartmentId), "Choose a department.");
        if (ModelState.IsValid)
        {
            var result = await tasks.CreateAsync(model.ToInput(), ct);
            if (result.Succeeded) return Saved($"Task \"{TextNormalizer.CleanText(model.Title)}\" was created.");
            AddErrors(result);
        }
        await FillAsync(model, null, ct);
        return FormView("Form", model);
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var t = await tasks.GetAsync(id, ct);
        if (t is null) return NotFoundResult("This task no longer exists.");
        var model = new TaskFormModel
        {
            Id = t.Id,
            AssignedToId = t.AssignedTo.Id,
            ReviewerId = t.Reviewer?.Id,
            Title = t.Title,
            Description = t.Description,
            Notes = t.Notes,
            Status = t.Status,
            Priority = t.Priority,
            DueDate = t.DueDate,
            DueTime = t.DueTime,
        };
        await FillAsync(model, t, ct);
        return PageOrPartial("Form", model);
    }

    [HttpPost("{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, TaskFormModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await tasks.UpdateAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return Saved($"Task \"{TextNormalizer.CleanText(model.Title)}\" was updated.");
            if (result.Errors.Any(e => e.Key == "" && e.Value.Contains("no longer exists"))) return NotFoundResult(result.Errors[0].Value);
            AddErrors(result);
        }
        var t = await tasks.GetAsync(id, ct);
        if (t is null) return NotFoundResult("This task no longer exists.");
        await FillAsync(model, t, ct);
        return FormView("Form", model);
    }

    [HttpPost("{id:int}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(int id, ManagementTaskStatus status, CancellationToken ct)
    {
        if (!ModelState.IsValid) return Failure("Choose a status from the list.", 400);
        var result = await tasks.SetStatusAsync(id, status, ct);
        if (!result.Succeeded) return Failure(result.Errors[0].Value, result.Errors[0].Key == "" ? 404 : 400);
        return Saved($"Status set to {TaskLabels.Status(status)}.");
    }

    [HttpPost("{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var task = await tasks.GetAsync(id, ct);
        var result = await tasks.DeleteAsync(id, ct);
        if (!result.Succeeded) return Failure(result.Errors[0].Value, 404);
        if (IsAjax) return AjaxSuccess($"Task \"{task?.Title}\" was deleted.", IsDetailsReferer(id) ? TasksController.Url : null);
        Toast($"Task \"{task?.Title}\" was deleted.");
        return IsDetailsReferer(id) ? Redirect(TasksController.Url) : RedirectBack();
    }

    /// <summary>
    /// Uploads one attachment for the task form (before the task is saved): the file is checked and stored, and the
    /// form keeps the returned key. JSON for the form's file list; the file joins the task only when the form is saved.
    /// </summary>
    [HttpPost("attachments"), ValidateAntiForgeryToken]
    [RequestSizeLimit(TaskAttachmentRules.MaxBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = TaskAttachmentRules.MaxBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (file is null) return AjaxFailure("Choose a file to attach.", 400);
        await using var stream = file.OpenReadStream();
        var (result, view) = await tasks.StageAttachmentAsync(file.FileName, file.Length, stream, ct);
        if (!result.Succeeded || view is null) return AjaxFailure(result.Errors[0].Value, 422);
        return Json(new { success = true, key = view.Key, name = view.FileName, kind = view.Kind, size = view.Size, extension = view.Extension });
    }

    /// <summary>Opens (PDF, images) or downloads an attachment of a saved task.</summary>
    [HttpGet("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> Attachment(int id, int attachmentId, bool download, CancellationToken ct)
    {
        var file = await tasks.OpenAttachmentAsync(id, attachmentId, download, ct);
        if (file is null) return NotFound();
        // The browser must use the type the server checked, never guess another one from the bytes.
        Response.Headers.XContentTypeOptions = "nosniff";
        if (file.Inline)
        {
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(file.FileName);
            Response.Headers.ContentDisposition = disposition.ToString();
            // An image opened on its own page can load nothing else and run nothing.
            if (file.ContentType.StartsWith("image/", StringComparison.Ordinal))
                Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'";
            return File(file.Content, file.ContentType, enableRangeProcessing: true);
        }
        return File(file.Content, file.ContentType, file.FileName, enableRangeProcessing: true);
    }

    private async Task FillAsync(TaskFormModel model, TaskRow? saved, CancellationToken ct)
    {
        model.Staff = await tasks.StaffAsync(ct);
        if (model.DepartmentMode) model.Departments = await tasks.DepartmentsAsync(ct);
        model.Staged = await tasks.StagedAttachmentsAsync(model.AttachmentKeys, ct);
        model.AttachmentKeys = model.Staged.Select(s => s.Key).ToList(); // expired uploads drop out
        if (saved is not null)
        {
            model.Attachments = await tasks.AttachmentsAsync(saved.Id, ct);
            model.Creator = saved.Creator;
            model.CreatorName = saved.CreatorName;
            model.SavedStatus = saved.Status;
        }
        else
        {
            var me = currentUser.StaffId is { } sid ? model.Staff.FirstOrDefault(s => s.Id == sid) : null;
            model.CreatorName = currentUser.AccountName();   // "Super Admin" in Administration mode
            model.Creator = me is null ? null : new TaskPerson(me.Id, me.FullName, null, null, null, me.IsManagement);
        }
    }

    private bool IsDetailsReferer(int id) =>
        Uri.TryCreate(Request.Headers.Referer.ToString(), UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/') == $"/management/tasks/{id}";

    private IActionResult Failure(string message, int status)
    {
        if (IsAjax) return AjaxFailure(message, status);
        Toast(message, "error");
        return RedirectBack();
    }

    private IActionResult Saved(string message)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return RedirectBack();
    }

    /// <summary>Non-AJAX posts return to the page they came from (a Tasks page or a member profile), only within this site.</summary>
    private IActionResult RedirectBack()
    {
        var referer = Request.Headers.Referer.ToString();
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == Request.Host.Host
            && (uri.AbsolutePath.StartsWith("/management", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.TrimEnd('/').Equals(TasksController.Url, StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/staff/departments", StringComparison.OrdinalIgnoreCase))
            && !uri.AbsolutePath.EndsWith("/edit", StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.EndsWith("/create", StringComparison.OrdinalIgnoreCase))
            return LocalRedirect(uri.PathAndQuery);
        return Redirect(TasksController.Url);
    }
}
