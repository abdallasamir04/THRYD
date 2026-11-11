using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

public class TeachersController(ITeacherService teachers, IExcelExportService excel, IWorkloadService workload) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] TeacherListQuery query, CancellationToken ct)
    {
        var model = new TeacherIndexViewModel
        {
            Query = query,
            Result = await teachers.ListAsync(query, ct),
            JobTitles = await teachers.JobTitlesAsync(ct),
            Departments = await teachers.DepartmentSummariesAsync(ct),
            TotalTeachers = await teachers.CountAsync(ct),
        };
        return IsAjax ? PartialView("_Table", model) : View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var teacher = await teachers.GetAsync(id, ct);
        if (teacher is null) return NotFoundResult("This teacher no longer exists.");
        var summary = await workload.SummaryAsync(id, Common.SchoolWeek.Current, ct);
        return PageOrPartial("Details", new TeacherDetailsViewModel { Teacher = teacher, Workload = summary! });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? departmentId, CancellationToken ct)
    {
        var depts = await teachers.DepartmentsAsync(ct);
        return PageOrPartial("Form", new TeacherFormModel
        {
            DepartmentId = depts.Any(d => d.Id == departmentId) ? departmentId : null,
            JobTitleSuggestions = await teachers.JobTitlesAsync(ct),
            Departments = depts,
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TeacherFormModel model, CancellationToken ct)
    {
        model.Id = null;
        if (ModelState.IsValid)
        {
            var result = await teachers.CreateAsync(model.FullName, model.JobTitle, model.ContactNumber, model.DepartmentId, model.Subcategory, model.Subject, ct);
            if (result.Succeeded) return Saved($"{model.FullName.Trim()} was added.");
            AddErrors(result);
        }
        model.JobTitleSuggestions = await teachers.JobTitlesAsync(ct);
        model.Departments = await teachers.DepartmentsAsync(ct);
        return FormView("Form", model);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var teacher = await teachers.GetAsync(id, ct);
        if (teacher is null) return NotFoundResult("This teacher no longer exists.");
        return PageOrPartial("Form", new TeacherFormModel
        {
            Id = teacher.Id,
            FullName = teacher.FullName,
            JobTitle = teacher.JobTitle,
            ContactNumber = teacher.ContactNumber,
            DepartmentId = teacher.DepartmentId,
            Subcategory = teacher.Subcategory,
            Subject = teacher.Subject,
            Departments = await teachers.DepartmentsAsync(ct),
            JobTitleSuggestions = await teachers.JobTitlesAsync(ct),
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, TeacherFormModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await teachers.UpdateAsync(id, model.FullName, model.JobTitle, model.ContactNumber, model.DepartmentId, model.Subcategory, model.Subject, ct);
            if (result.Succeeded) return Saved($"{model.FullName.Trim()} was updated.");
            AddErrors(result);
        }
        model.JobTitleSuggestions = await teachers.JobTitlesAsync(ct);
        model.Departments = await teachers.DepartmentsAsync(ct);
        return FormView("Form", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var teacher = await teachers.GetAsync(id, ct);
        var result = await teachers.DeleteAsync(id, ct);
        var message = result.Succeeded ? $"{teacher?.FullName} was deleted." : result.Errors[0].Value;
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 404);
        Toast(message, result.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] TeacherListQuery query, CancellationToken ct)
    {
        var rows = await teachers.ListAllAsync(query, ct);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Search)) filters.Add($"Search \"{query.Search.Trim()}\"");
        if (query.JobTitle == TeacherListQuery.NoJobTitle) filters.Add("Job title: not set");
        else if (!string.IsNullOrWhiteSpace(query.JobTitle)) filters.Add($"Job title: {query.JobTitle}");
        var bytes = excel.Teachers(rows, filters.Count == 0 ? "All teachers" : string.Join("; ", filters));
        return File(bytes, StudentsController.ExcelContentType, $"teachers-{DateTime.Now:yyyy-MM-dd}.xlsx");
    }

    [HttpGet]
    public async Task<IActionResult> CheckDuplicate(string? fullName, string? contactNumber, int? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return Json(true);
        var duplicate = await teachers.FindDuplicateAsync(fullName, contactNumber, id, ct);
        return duplicate is null
            ? Json(true)
            : Json($"\"{duplicate.FullName}\"{(duplicate.ContactNumber is null ? "" : $" ({duplicate.ContactNumber})")} already exists.");
    }

    private IActionResult Saved(string message)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return RedirectToAction(nameof(Index));
    }
}
