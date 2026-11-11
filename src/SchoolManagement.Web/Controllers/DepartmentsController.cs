using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>Academic Staff → Departments: the department grid, each department's page, HOD changes.</summary>
[Route("staff/departments")]
public class DepartmentsController(IDepartmentService departments, IPeService pe, ITimetableService timetable, ITeacherService teachers) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] DepartmentListQuery query, CancellationToken ct)
    {
        var all = await departments.ListAsync(new DepartmentListQuery(), ct);
        var model = new DepartmentIndexViewModel
        {
            Query = query,
            Departments = query.HasFilters ? await departments.ListAsync(query, ct) : all,
            TotalDepartments = all.Count,
            WithHead = all.Count(d => d.Head is not null),
            TotalMembers = all.Sum(d => d.MemberCount),
        };
        return IsAjax ? PartialView("_Grid", model) : View(model);
    }

    /// <summary>Add department: a new academic department with its own name and details (no staff or HOD yet).</summary>
    [HttpGet("create")]
    public IActionResult Create() => PageOrPartial("Create", new DepartmentCreateModel());

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(DepartmentCreateModel model, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await departments.CreateAsync(model.Name, model.Description, model.SubcategoryLabel, model.SubjectLabel, ct);
            if (result.Succeeded)
                return Saved($"{TextNormalizer.CleanText(model.Name)} was added. Open it to add members and assign an HOD.", result.EntityId!.Value, "/staff/departments");
            AddErrors(result);
        }
        return FormView("Create", model);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id, [FromQuery] DepartmentMemberQuery query, string? date, CancellationToken ct)
    {
        var dept = await departments.GetAsync(id, ct);
        if (dept is null) return NotFoundResult("This department no longer exists.");
        // The PE department page embeds the P.E. module (same data as /pe) under Department Staff.
        var isPe = !IsAjax && await pe.DepartmentIdAsync(ct) == id;
        var model = new DepartmentDetailsViewModel
        {
            Department = dept,
            Query = query,
            Members = await departments.MembersAsync(id, query, ct),
            HeadHistory = IsAjax ? [] : await departments.HeadHistoryAsync(id, ct),
            Pe = isPe ? await PePageViewModel.LoadAsync(SchoolWeek.ParseDayOrToday(date), pe, timetable, teachers, ct) : null,
        };
        return IsAjax ? PartialView("_Members", model) : View(model);
    }

    /// <summary>Department Report: the department's staff (same search, subcategory and sort as the page) and HOD history.</summary>
    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> Pdf(int id, [FromQuery] DepartmentMemberQuery query, CancellationToken ct)
    {
        var dept = await departments.GetAsync(id, ct);
        if (dept is null) return NotFound();
        return await PdfFile(PdfReports.Department(dept, await departments.MembersAsync(id, query, ct), await departments.HeadHistoryAsync(id, ct), query), ct);
    }

    [HttpGet("heads/pdf")]
    public async Task<IActionResult> HeadsPdf(string? filter, CancellationToken ct)
    {
        var hod = filter is "missing" or "assigned" ? filter : null;
        return await PdfFile(PdfReports.Heads(await departments.ListAsync(new DepartmentListQuery { Hod = hod }, ct), hod), ct);
    }

    /// <summary>Academic Staff → HOD Management: every department's current HOD and recent changes.</summary>
    [HttpGet("heads")]
    public async Task<IActionResult> Heads(string? filter, CancellationToken ct)
    {
        var list = await departments.ListAsync(new DepartmentListQuery { Hod = filter is "missing" or "assigned" ? filter : null }, ct);
        return View(new HeadsViewModel { Departments = list, Recent = await departments.RecentHeadChangesAsync(30, ct), Filter = filter });
    }

    /// <summary>Department subcategories (e.g. Second Language → languages): add, rename, activate / deactivate.</summary>
    [HttpGet("{id:int}/subcategories")]
    public async Task<IActionResult> Subcategories(int id, CancellationToken ct)
    {
        var dept = await departments.GetAsync(id, ct);
        if (dept is null) return NotFoundResult("This department no longer exists.");
        return View(new SubcategoriesViewModel { Department = dept, Items = await departments.SubcategoriesAsync(id, ct) });
    }

    [HttpGet("{id:int}/subcategories/create")]
    public async Task<IActionResult> CreateSubcategory(int id, int? parentId, CancellationToken ct)
    {
        var model = new SubcategoryFormModel { DepartmentId = id, ParentId = parentId };
        if (!await FillAsync(model, ct)) return NotFoundResult("This department no longer exists.");
        return PageOrPartial("SubcategoryForm", model);
    }

    [HttpPost("{id:int}/subcategories/create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSubcategory(int id, SubcategoryFormModel model, CancellationToken ct)
    {
        model.Id = null;
        model.DepartmentId = id;
        if (!await FillAsync(model, ct)) return NotFoundResult("This department no longer exists.");
        if (ModelState.IsValid)
        {
            var result = await departments.AddSubcategoryAsync(id, model.ParentId, model.Name, ct);
            if (result.Succeeded) return Saved($"{TextNormalizer.CleanText(model.Name)} was added to {model.DepartmentName}. It is available on the staff form now.", id, $"/staff/departments/{id}/subcategories");
            AddErrors(result);
        }
        return FormView("SubcategoryForm", model);
    }

    [HttpGet("subcategories/{subId:int}/edit")]
    public async Task<IActionResult> EditSubcategory(int subId, CancellationToken ct)
    {
        var item = await departments.GetSubcategoryAsync(subId, ct);
        var deptId = item is null ? null : await DepartmentOfAsync(subId, ct);
        if (item is null || deptId is null) return NotFoundResult("This option no longer exists.");
        var model = new SubcategoryFormModel { Id = subId, DepartmentId = deptId.Value, ParentId = item.ParentId, Name = item.Name, IsActive = item.IsActive };
        await FillAsync(model, ct);
        return PageOrPartial("SubcategoryForm", model);
    }

    [HttpPost("subcategories/{subId:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSubcategory(int subId, SubcategoryFormModel model, CancellationToken ct)
    {
        var deptId = await DepartmentOfAsync(subId, ct);
        if (deptId is null) return NotFoundResult("This option no longer exists.");
        (model.Id, model.DepartmentId) = (subId, deptId.Value);
        await FillAsync(model, ct);
        if (ModelState.IsValid)
        {
            var result = await departments.UpdateSubcategoryAsync(subId, model.Name, model.IsActive, ct);
            if (result.Succeeded) return Saved($"{TextNormalizer.CleanText(model.Name)} was saved{(model.IsActive ? "" : " (inactive: no longer offered for new choices)")}.", deptId.Value, $"/staff/departments/{deptId}/subcategories");
            AddErrors(result);
        }
        return FormView("SubcategoryForm", model);
    }

    private async Task<int?> DepartmentOfAsync(int subId, CancellationToken ct) =>
        (await departments.GetSubcategoryAsync(subId, ct))?.DepartmentId;

    private async Task<bool> FillAsync(SubcategoryFormModel model, CancellationToken ct)
    {
        var dept = await departments.GetAsync(model.DepartmentId, ct);
        if (dept is null) return false;
        model.DepartmentName = dept.Name;
        model.Noun = dept.Name == "Second Language" ? "language" : "option";
        model.Parents = (await departments.SubcategoriesAsync(model.DepartmentId, ct)).Where(s => s.ParentId is null && s.Id != model.Id).ToList();
        return true;
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var dept = await departments.GetAsync(id, ct);
        if (dept is null) return NotFoundResult("This department no longer exists.");
        var labels = await departments.LabelsAsync(id, ct);
        return PageOrPartial("Edit", new DepartmentEditModel
        {
            Id = id, Name = dept.Name, Description = dept.Description, SubcategoryLabel = labels.Subcategory, SubjectLabel = labels.Subject,
        });
    }

    [HttpPost("{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, DepartmentEditModel model, CancellationToken ct)
    {
        model.Id = id;
        var dept = await departments.GetAsync(id, ct);
        if (dept is null) return NotFoundResult("This department no longer exists.");
        model.Name = dept.Name;
        if (ModelState.IsValid)
        {
            var result = await departments.UpdateAsync(id, model.Description, model.SubcategoryLabel, model.SubjectLabel, ct);
            if (result.Succeeded) return Saved($"{dept.Name} details were updated.", id);
            AddErrors(result);
        }
        return FormView("Edit", model);
    }

    [HttpGet("{id:int}/head")]
    public async Task<IActionResult> AssignHead(int id, CancellationToken ct)
    {
        var model = new AssignHeadModel { DepartmentId = id, StartDate = SchoolWeek.Today };
        if (!await FillAsync(model, ct)) return NotFoundResult("This department no longer exists.");
        return PageOrPartial("AssignHead", model);
    }

    [HttpPost("{id:int}/head"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignHead(int id, AssignHeadModel model, CancellationToken ct)
    {
        model.DepartmentId = id;
        if (!await FillAsync(model, ct)) return NotFoundResult("This department no longer exists.");
        if (ModelState.IsValid)
        {
            var result = await departments.AssignHeadAsync(id, model.TeacherId, model.StartDate, model.Note, ct);
            if (result.Succeeded)
            {
                var name = model.Candidates.FirstOrDefault(c => c.Id == model.TeacherId)?.FullName ?? "The new HOD";
                return Saved($"{name} is now Head of {model.Department!.Name}.", id);
            }
            AddErrors(result);
        }
        return FormView("AssignHead", model);
    }

    [HttpPost("{id:int}/members/{staffId:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(int id, int staffId, CancellationToken ct)
    {
        var result = await departments.RemoveMemberAsync(id, staffId, ct);
        var message = result.Succeeded ? "The staff member was removed from the department. Their staff record is kept." : result.Errors[0].Value;
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 404);
        Toast(message, result.Succeeded ? "success" : "error");
        return Redirect($"/staff/departments/{id}");
    }

    private async Task<bool> FillAsync(AssignHeadModel model, CancellationToken ct)
    {
        model.Department = await departments.GetAsync(model.DepartmentId, ct);
        if (model.Department is null) return false;
        model.Candidates = await departments.MembersAsync(model.DepartmentId, new DepartmentMemberQuery(), ct);
        return true;
    }

    private IActionResult Saved(string message, int id, string? back = null)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return Redirect(back ?? $"/staff/departments/{id}");
    }
}
