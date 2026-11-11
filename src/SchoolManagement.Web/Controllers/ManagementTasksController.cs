using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>Management Team → Tasks: every member's tasks, with create / edit / status / delete.</summary>
[Route("management/tasks")]
public class ManagementTasksController(IManagementTaskService tasks, IManagementService members) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] TaskListQuery query, CancellationToken ct)
    {
        var model = new TaskIndexViewModel
        {
            Query = query,
            Result = await tasks.ListAsync(query, ct),
            Counts = await tasks.CountsAsync(query.MemberId, ct),
            Members = await members.OptionsAsync(ct),
        };
        return IsAjax ? PartialView("_TaskList", model) : View(model);
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create(int? memberId, CancellationToken ct)
    {
        var model = new TaskFormModel { MemberId = memberId };
        model.Members = await members.OptionsAsync(ct);
        if (model.MemberId is { } id && model.Members.All(m => m.Id != id)) model.MemberId = null;
        return PageOrPartial("Form", model);
    }

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TaskFormModel model, CancellationToken ct)
    {
        model.Id = null;
        if (ModelState.IsValid)
        {
            var result = await tasks.CreateAsync(model.ToInput(), ct);
            if (result.Succeeded) return Saved($"Task \"{TextNormalizer.CleanText(model.Title)}\" was created.");
            AddErrors(result);
        }
        model.Members = await members.OptionsAsync(ct);
        return FormView("Form", model);
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var t = await tasks.GetAsync(id, ct);
        if (t is null) return NotFoundResult("This task no longer exists.");
        return PageOrPartial("Form", new TaskFormModel
        {
            Id = t.Id,
            MemberId = t.MemberId,
            Title = t.Title,
            Description = t.Description,
            Notes = t.Notes,
            Status = t.Status,
            Priority = t.Priority,
            DueDate = t.DueDate,
            DueTime = t.DueTime,
            Members = await members.OptionsAsync(ct),
        });
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
        model.Members = await members.OptionsAsync(ct);
        return FormView("Form", model);
    }

    [HttpPost("{id:int}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(int id, ManagementTaskStatus status, CancellationToken ct)
    {
        if (!ModelState.IsValid) return Failure("Choose a status from the list.", 400);
        var result = await tasks.SetStatusAsync(id, status, ct);
        if (!result.Succeeded) return Failure(result.Errors[0].Value, 404);
        return Saved($"Status set to {TaskLabels.Status(status)}.");
    }

    [HttpPost("{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var task = await tasks.GetAsync(id, ct);
        var result = await tasks.DeleteAsync(id, ct);
        if (!result.Succeeded) return Failure(result.Errors[0].Value, 404);
        return Saved($"Task \"{task?.Title}\" was deleted.");
    }

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

    /// <summary>Non-AJAX posts return to the page they came from (Tasks page or a member profile), only within this site.</summary>
    private IActionResult RedirectBack()
    {
        var referer = Request.Headers.Referer.ToString();
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == Request.Host.Host
            && uri.AbsolutePath.StartsWith("/management", StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.EndsWith("/edit", StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.EndsWith("/create", StringComparison.OrdinalIgnoreCase))
            return LocalRedirect(uri.PathAndQuery);
        return RedirectToAction(nameof(Index));
    }
}
