using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

public class ClassesController(IClassService classes) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(string? search, string? status, string? view, CancellationToken ct)
    {
        var all = await classes.ListAsync(null, null, ct);
        var model = new ClassIndexViewModel
        {
            Classes = await classes.ListAsync(search, status, ct),
            Search = search,
            Status = status,
            View = view == "table" ? "table" : "grid",
            TotalClasses = all.Count,
            ActiveClasses = all.Count(c => c.IsActive),
            TotalStudents = all.Sum(c => c.StudentCount),
        };
        return IsAjax ? PartialView("_List", model) : View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var model = new ClassFormModel { GradeLevel = "KG" };
        await FillNumberingAsync(model, ct);
        return PageOrPartial("Form", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ClassFormModel model, CancellationToken ct)
    {
        model.Id = null;
        // The class name and number are never taken from the form: the server calculates them.
        ModelState.Remove(nameof(ClassFormModel.Name));
        ModelState.Remove(nameof(ClassFormModel.SortOrder));
        if (ModelState.IsValid)
        {
            var result = await classes.CreateInSectionAsync(model.GradeLevel ?? "", model.Section ?? "", model.Capacity, model.IsActive, ct);
            if (result.Succeeded)
            {
                var created = await classes.GetAsync(result.EntityId!.Value, ct);
                return Saved($"Class {created?.Name} was added.");
            }
            AddErrors(result);
        }
        await FillNumberingAsync(model, ct);
        return FormView("Form", model);
    }

    /// <summary>Returns the name the next class in a stage + section will get (used for the live preview).</summary>
    [HttpGet]
    public async Task<IActionResult> NextName(string stage, string section, CancellationToken ct) =>
        Json(new { name = await classes.NextNameAsync(stage, section, ct) });

    private async Task FillNumberingAsync(ClassFormModel model, CancellationToken ct)
    {
        var sections = new Dictionary<string, IReadOnlyList<string>>();
        var next = new List<string>();
        foreach (var (code, _) in ClassNumbering.Stages)
        {
            sections[code] = await classes.SectionsAsync(code, ct);
            foreach (var s in sections[code])
                next.Add((await classes.NextNameAsync(code, s, ct))!);
        }
        model.Sections = sections;
        model.NextNames = next;
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var row = await classes.GetAsync(id, ct);
        if (row is null) return NotFoundResult("This class no longer exists.");
        return PageOrPartial("Form", new ClassFormModel
        {
            Id = row.Id, Name = row.Name, Capacity = row.Capacity, SortOrder = row.SortOrder, IsActive = row.IsActive, StudentCount = row.StudentCount,
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ClassFormModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await classes.UpdateAsync(id, model.Name, model.SortOrder, model.Capacity, model.IsActive, ct);
            if (result.Succeeded) return Saved($"Class {model.Name.Trim()} was updated.");
            AddErrors(result);
        }
        model.StudentCount = (await classes.GetAsync(id, ct))?.StudentCount ?? 0;
        return FormView("Form", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id, bool active, CancellationToken ct)
    {
        var row = await classes.GetAsync(id, ct);
        var result = await classes.SetActiveAsync(id, active, ct);
        var message = !result.Succeeded
            ? result.Errors[0].Value
            : active ? $"{row?.Name} is active again." : $"{row?.Name} was deactivated. Its students keep their class; no new students can be placed in it.";
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 404);
        Toast(message, result.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var row = await classes.GetAsync(id, ct);
        if (row is null) return NotFoundResult("This class no longer exists.");
        return PageOrPartial("Delete", new ClassDeleteModel
        {
            Id = id,
            Class = row,
            Targets = (await classes.OptionsAsync(null, ct)).Where(c => c.Id != id).ToList(),
        });
    }

    [HttpPost, ValidateAntiForgeryToken, ActionName("Delete")]
    public async Task<IActionResult> DeleteConfirmed(int id, ClassDeleteModel model, CancellationToken ct)
    {
        var row = await classes.GetAsync(id, ct);
        if (row is null) return NotFoundResult("This class no longer exists.");
        var result = await classes.DeleteAsync(id, model.ReassignToClassId, ct);
        if (result.Succeeded)
        {
            var moved = row.StudentCount > 0 ? $" {row.StudentCount} student(s) were moved first." : "";
            return Saved($"Class {row.Name} was deleted.{moved}");
        }

        AddErrors(result);
        model.Id = id;
        model.Class = row;
        model.Targets = (await classes.OptionsAsync(null, ct)).Where(c => c.Id != id).ToList();
        return FormView("Delete", model);
    }

    [HttpGet]
    public async Task<IActionResult> CheckName(string? name, int? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return Json(true);
        return await classes.NameExistsAsync(name, id, ct) ? Json($"A class named \"{name.Trim()}\" already exists.") : Json(true);
    }

    private IActionResult Saved(string message)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return RedirectToAction(nameof(Index));
    }
}
