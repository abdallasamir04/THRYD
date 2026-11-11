using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

public class StudentsController(IStudentService students, IClassService classes, IExcelExportService excel, IArrivalService arrivals) : AppController
{
    public const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Student profile with the Arrival & Attendance History (recorded days only) and Transfer History.</summary>
    [HttpGet]
    public async Task<IActionResult> Profile(int id, string? from, string? to, string? order, CancellationToken ct)
    {
        var student = await students.GetAsync(id, ct);
        if (student is null) return NotFound();
        static DateOnly? Parse(string? v) => DateOnly.TryParseExact(v, "yyyy-MM-dd", out var d) ? d : null;
        var newestFirst = order != "oldest";
        var currentClass = await classes.GetAsync(student.ClassId, ct);
        return View(new StudentProfileViewModel
        {
            Student = student,
            CurrentClass = currentClass,
            History = await arrivals.StudentHistoryAsync(id, Parse(from), Parse(to), newestFirst, ct),
            TransferHistory = await students.GetTransferHistoryAsync(id, ct),
            From = from,
            To = to,
            NewestFirst = newestFirst,
        });
    }

    [HttpGet]
    public async Task<IActionResult> Transfer(int id, CancellationToken ct)
    {
        var student = await students.GetAsync(id, ct);
        if (student is null) return NotFoundResult("This student no longer exists.");

        var model = new StudentTransferModel
        {
            StudentId = id,
            Student = student,
            AvailableClasses = await classes.AllOptionsAsync(ct),
        };
        return PageOrPartial("Transfer", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Transfer(int id, StudentTransferModel model, [FromServices] ICurrentUser currentUser, CancellationToken ct)
    {
        var student = await students.GetAsync(id, ct);
        if (student is null) return NotFoundResult("This student no longer exists.");

        var result = await students.TransferClassAsync(id, model.TargetClassId, currentUser.Name, ct);
        if (result.Succeeded)
        {
            var targetClass = (await classes.GetAsync(model.TargetClassId, ct))?.Name ?? "new class";
            var message = $"{student.FullName} was transferred to {targetClass}.";
            if (IsAjax) return AjaxSuccess(message);
            Toast(message);
            return RedirectToAction(nameof(Profile), new { id });
        }

        AddErrors(result);
        model.StudentId = id;
        model.Student = student;
        model.AvailableClasses = await classes.AllOptionsAsync(ct);
        return FormView("Transfer", model);
    }

    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] StudentListQuery query, CancellationToken ct)
    {
        var model = new StudentIndexViewModel
        {
            Query = query,
            Result = await students.ListAsync(query, ct),
            Classes = await classes.AllOptionsAsync(ct),
            TotalStudents = await students.CountAsync(ct),
        };
        return IsAjax ? PartialView("_Table", model) : View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var student = await students.GetAsync(id, ct);
        if (student is null) return NotFoundResult("This student no longer exists.");
        return PageOrPartial("Details", student);
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? classId, CancellationToken ct)
    {
        var model = new StudentFormModel { SchoolClassId = classId, ClassOptions = await classes.OptionsAsync(null, ct) };
        return PageOrPartial("Form", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(StudentFormModel model, CancellationToken ct)
    {
        model.Id = null;
        if (ModelState.IsValid)
        {
            var result = await students.CreateAsync(model.FullName, model.SchoolClassId!.Value, ct);
            if (result.Succeeded) return Saved($"{model.FullName.Trim()} was added.");
            AddErrors(result);
        }
        model.ClassOptions = await classes.OptionsAsync(model.SchoolClassId, ct);
        return FormView("Form", model);
    }

    /// <summary>
    /// Add a student from inside a class. The class comes from the route, never from a form field, so
    /// the form shows no class dropdown and a tampered field cannot move the student elsewhere.
    /// </summary>
    [HttpGet("/classes/{classId:int}/students/create")]
    public async Task<IActionResult> CreateInClass(int classId, CancellationToken ct)
    {
        var cls = await classes.GetAsync(classId, ct);
        if (cls is null) return NotFoundResult("This class no longer exists.");
        return PageOrPartial("Form", new StudentFormModel { SchoolClassId = classId, LockedClass = cls });
    }

    [HttpPost("/classes/{classId:int}/students/create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateInClass(int classId, StudentFormModel model, CancellationToken ct)
    {
        var cls = await classes.GetAsync(classId, ct);
        if (cls is null) return NotFoundResult("This class no longer exists.");
        model.Id = null;
        model.SchoolClassId = classId;
        ModelState.Remove(nameof(StudentFormModel.SchoolClassId));
        if (ModelState.IsValid)
        {
            var result = await students.CreateAsync(model.FullName, classId, ct);
            if (result.Succeeded)
            {
                var message = $"{model.FullName.Trim()} was added to {cls.Name}.";
                if (IsAjax) return AjaxSuccess(message);
                Toast(message);
                return RedirectToAction(nameof(Index), new { classId });
            }
            AddErrors(result);
        }
        model.LockedClass = await classes.GetAsync(classId, ct) ?? cls;
        return FormView("Form", model);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var student = await students.GetAsync(id, ct);
        if (student is null) return NotFoundResult("This student no longer exists.");
        var model = new StudentFormModel
        {
            Id = student.Id,
            FullName = student.FullName,
            SchoolClassId = student.ClassId,
            ClassOptions = await classes.OptionsAsync(student.ClassId, ct),
        };
        return PageOrPartial("Form", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, StudentFormModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await students.UpdateAsync(id, model.FullName, model.SchoolClassId!.Value, ct);
            if (result.Succeeded) return Saved($"{model.FullName.Trim()} was updated.");
            AddErrors(result);
        }
        var current = await students.GetAsync(id, ct);
        model.ClassOptions = await classes.OptionsAsync(current?.ClassId ?? model.SchoolClassId, ct);
        return FormView("Form", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var student = await students.GetAsync(id, ct);
        var result = await students.DeleteAsync(id, ct);
        var message = result.Succeeded ? $"{student?.FullName} was deleted." : result.Errors[0].Value;
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 404);
        Toast(message, result.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] StudentListQuery query, CancellationToken ct)
    {
        var rows = await students.ListAllAsync(query, ct);
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Search)) filters.Add($"Search \"{query.Search.Trim()}\"");
        if (query.ClassId is { } classId)
        {
            var c = await classes.GetAsync(classId, ct);
            if (c is not null) filters.Add($"Class: {c.Name}");
        }
        var bytes = excel.Students(rows, filters.Count == 0 ? "All students" : string.Join("; ", filters));
        return File(bytes, ExcelContentType, $"students-{DateTime.Now:yyyy-MM-dd}.xlsx");
    }

    /// <summary>
    /// Live duplicate check used by the [Remote] rule on StudentFormModel.FullName. The action name must
    /// match that attribute: when it did not, the browser's check got a 404, stayed pending, and the
    /// form never submitted. The server repeats the check on save.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> CheckDuplicate(string? fullName, int? schoolClassId, int? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fullName) || schoolClassId is not > 0) return Json(true);
        var duplicate = await students.FindDuplicateAsync(fullName, schoolClassId.Value, id, ct);
        return duplicate is null ? Json(true) : Json($"\"{duplicate.FullName}\" is already in {duplicate.ClassName}.");
    }

    private IActionResult Saved(string message)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return RedirectToAction(nameof(Index));
    }
}
