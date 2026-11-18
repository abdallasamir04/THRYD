using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>Staff & Departments management, duty history and supervisor evaluations.</summary>
[Route("staff")]
public class StaffController(IStaffDutyService staffDuty, IDutyService duties, IWorkloadService workload, ITeacherService teachers, ICurrentUser currentUser,
    IDepartmentService departments, IManagementService management, IManagementTaskService tasks, ITimetableService timetable, IClassTeamService classTeams,
    IStaffDirectoryService directory, IStaffEmploymentService employment) : AppController
{
    /// <summary>
    /// Staff landing page: choose Academic Staff or Management Team. ?view=duties (the old Staff Duty Records
    /// page) now lives in Duty Audit → Staff Duty Records; old links redirect there with their search.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] TeacherListQuery query, string? view, CancellationToken ct)
    {
        if (view == "duties")
        {
            if (IsAjax)
                return PartialView("_Overview", new StaffOverviewViewModel { Rows = await staffDuty.OverviewAsync(query.Search, ct), Search = query.Search });
            return Redirect("/duties/audit/staff-records"
                + (string.IsNullOrWhiteSpace(query.Search) ? "" : "?search=" + Uri.EscapeDataString(query.Search)));
        }

        var depts = await departments.ListAsync(new DepartmentListQuery(), ct);
        var taskCounts = await tasks.CountsAsync(null, ct);
        return View("Hub", new HubViewModel
        {
            Title = "Staff Management",
            Subtitle = "Choose an area. Academic Staff and the Management Team are managed separately.",
            Crumbs = [("Dashboard", "/"), ("Staff", null)],
            Large = true,
            Cards =
            [
                new("/staff/academic", "graduation-cap", "Academic Staff",
                    "Manage academic departments, teachers, HODs, workload, and timetables.",
                    $"{Ui.Plural(await teachers.CountAsync(ct), "teacher", "teachers")} · {depts.Count} departments · {depts.Count(d => d.Head is not null)} HODs", "violet"),
                new("/management", "briefcase", "Management Team",
                    "Manage school management members, positions, and individual tasks.",
                    $"{Ui.Plural(await management.CountAsync(ct), "member", "members")} · {taskCounts.Open} open tasks", "blue"),
            ],
            Extra =
            [
                ("Staff Attendance — today's register for every staff member, with history and reports", "/staff/attendance", "clipboard-check"),
                ("All School Staff — the whole workforce in one directory", "/staff/all", "users"),
            ],
        });
    }

    /// <summary>
    /// Staff → All School Staff: every staff record (academic, Management Team, administrative and support), each person
    /// once, with search and filters. Cards open the person's existing profile.
    /// </summary>
    [HttpGet("all")]
    public async Task<IActionResult> All([FromQuery] StaffDirectoryQuery query, CancellationToken ct) =>
        View("All", new StaffDirectoryViewModel { Query = query, Directory = await directory.LoadAsync(query, ct) });

    /// <summary>
    /// All School Staff → Download PDF: "All School Staff Directory" with exactly the staff the page shows for the same query
    /// (search, category tab, department, job title, position / role), grouped like the page, each person once. Read-only.
    /// </summary>
    [HttpGet("all/pdf")]
    public async Task<IActionResult> AllPdf([FromQuery] StaffDirectoryQuery query, [FromServices] IStaffDirectoryExportService export,
        [FromServices] ISettingsService settings, CancellationToken ct)
    {
        var data = await export.LoadAsync(query, ct);
        return await PdfFile(StaffDirectoryReports.Directory(data, (await settings.GetProfileAsync(ct)).SchoolName), ct); // THRYD_All_School_Staff_<date>.pdf
    }

    /// <summary>All School Staff → Download Excel: the same staff as the PDF (and the page), as a workbook. Read-only.</summary>
    [HttpGet("all/excel")]
    public async Task<IActionResult> AllExcel([FromQuery] StaffDirectoryQuery query, [FromServices] IStaffDirectoryExportService export,
        [FromServices] ISettingsService settings, [FromServices] Services.Excel.IExcelExportService excel, CancellationToken ct)
    {
        var data = await export.LoadAsync(query, ct);
        return File(excel.StaffDirectory(data, (await settings.GetProfileAsync(ct)).SchoolName),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StaffDirectoryExport.FileName(SchoolTime.Today, "xlsx"));
    }

    /// <summary>Staff Duty Records → Download Report (same search as the page).</summary>
    [HttpGet("duty-records/pdf")]
    public async Task<IActionResult> DutyRecordsPdf(string? search, CancellationToken ct) =>
        await PdfFile(PdfReports.StaffDutyRecords(await staffDuty.OverviewAsync(search, ct), search), ct);

    /// <summary>Academic Staff → Staff Members → Download Report (all pages, same filters and sort).</summary>
    [HttpGet("members/pdf")]
    public async Task<IActionResult> MembersPdf([FromQuery] TeacherListQuery query, CancellationToken ct) =>
        await PdfFile(PdfReports.StaffMembers(await teachers.ListAllAsync(query, ct), query), ct);

    /// <summary>Academic Staff landing page: Departments, or Department Management.</summary>
    [HttpGet("academic")]
    public async Task<IActionResult> Academic(CancellationToken ct)
    {
        var depts = await departments.ListAsync(new DepartmentListQuery(), ct);
        return View("Hub", new HubViewModel
        {
            Title = "Academic Staff",
            Subtitle = "Departments, teachers, Heads of Department, workload and timetables.",
            Crumbs = [("Dashboard", "/"), ("Staff", "/staff"), ("Academic Staff", null)],
            Large = true,
            Cards =
            [
                new("/staff/departments", "layers", "Departments", "The 15 academic departments, their members and subcategories.", $"{depts.Count} departments", "violet"),
                new("/staff/academic/management", "settings", "Department Management",
                    "Staff members, Heads of Department, teacher workload, and class and teacher timetables.",
                    $"{Ui.Plural(await teachers.CountAsync(ct), "staff member", "staff members")} · {depts.Count(d => d.Head is not null)} HODs", "blue"),
            ],
        });
    }

    /// <summary>Academic Staff → Department Management landing page (the other five academic modules).</summary>
    [HttpGet("academic/management")]
    public async Task<IActionResult> DepartmentManagement(CancellationToken ct)
    {
        var depts = await departments.ListAsync(new DepartmentListQuery(), ct);
        var slots = await timetable.SlotsAsync(ct);
        var classes = await timetable.ClassesAsync(ct);
        var overview = await workload.OverviewAsync(SchoolWeek.Current, new WorkloadOverviewQuery(), ct);
        var full = overview.Count(o => o.Status is WorkloadStatus.Full or WorkloadStatus.OverLimit);
        var missing = depts.Count(d => d.Head is null);
        return View("Hub", new HubViewModel
        {
            Title = "Department Management",
            Subtitle = "Teachers, Heads of Department, workload and timetables.",
            Crumbs = [("Dashboard", "/"), ("Staff", "/staff"), ("Academic Staff", "/staff/academic"), ("Department Management", null)],
            Cards =
            [
                new("/staff/members", "users", "Staff Members", "Every academic staff member, with department, subject and contact.", Ui.Plural(await teachers.CountAsync(ct), "staff member", "staff members"), "blue"),
                new("/staff/departments/heads", "crown", "HOD Management", "One Head of Department per department: assign, replace and see the history.",
                    missing == 0 ? "Every department has an HOD" : $"{missing} without an HOD", missing == 0 ? "green" : "amber"),
                new("/workload", "chart", "Teacher Workload", $"Weekly periods per teacher: teaching, office hours, substitutions and duties (max {await workload.MaxAsync(ct)}).",
                    full == 0 ? "No teacher is full this week" : $"{full} full this week", "teal"),
                new("/timetable/classes", "grid", "Class Timetable", "Each class's week, Sunday–Thursday × 10 periods. Click a slot to assign a period.",
                    $"{classes.Count(c => c.Sessions > 0)} of {classes.Count} classes timetabled", "indigo"),
                new("/timetable/teachers", "calendar", "Teacher Timetable", "Each teacher's week, built from the same class timetable sessions.", null, "cyan"),
            ],
            Extra = [("Periods", "/timetable/periods", "clock"), ($"{slots.Count(s => s.HasTimes)} of {slots.Count} school-wide periods have times", "/timetable/periods", "info")],
        });
    }

    /// <summary>Academic Staff → Staff Members (the staff list).</summary>
    [HttpGet("members")]
    public async Task<IActionResult> Members([FromQuery] TeacherListQuery query, CancellationToken ct)
    {
        var model = new TeacherIndexViewModel
        {
            Query = query,
            Result = await teachers.ListAsync(query, ct),
            JobTitles = await teachers.JobTitlesAsync(ct),
            Departments = await teachers.DepartmentSummariesAsync(ct),
            TotalTeachers = await teachers.CountAsync(ct),
        };
        return IsAjax ? PartialView("~/Views/Teachers/_Table.cshtml", model) : View("~/Views/Teachers/Index.cshtml", model);
    }

    /// <summary>
    /// Add Staff Member: one form for Academic staff (existing teacher rules) and Management Team members (existing
    /// Management Team rules), with contract, probation, employment type and working hours. ?category=academic|management
    /// preselects the category; ?departmentId= preselects an academic department.
    /// </summary>
    [HttpGet("create")]
    public async Task<IActionResult> Create(int? departmentId, string? category, CancellationToken ct)
    {
        var model = new StaffCreateModel
        {
            Category = Enum.TryParse<NewStaffCategory>(category, true, out var c) && Enum.IsDefined(c) ? c : departmentId is not null ? NewStaffCategory.Academic : null,
        };
        await FillAsync(model, ct);
        model.DepartmentId = model.Departments.Any(d => d.Id == departmentId) ? departmentId : null;
        return PageOrPartial("Create", model);
    }

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(StaffCreateModel model, CancellationToken ct)
    {
        // Only the chosen category's fields apply; the other category's hidden fields are ignored.
        if (model.Category == NewStaffCategory.Management) { model.DepartmentId = null; model.Subcategory = null; model.Subject = null; model.JobTitle = null; }
        else
        {
            (model.Position, model.PositionChoice, model.NewPositionTitle, model.Email, model.AppointedOn) = (null, null, null, null, null);
            ModelState.Remove(nameof(model.Email));
            ModelState.Remove(nameof(model.NewPositionTitle));
        }
        // Management: the chosen leadership position, an existing school-added one, or a new one (created with the member).
        var member = new ManagementMemberInput { FullName = model.FullName, ContactNumber = model.ContactNumber, Email = model.Email, AppointedOn = model.AppointedOn };
        if (!PositionChoices.Apply(model.PositionChoice, model.Position, model.NewPositionTitle, member))
            ModelState.AddModelError(nameof(model.Position), "Choose a position from the list.");
        if (model.ProbationDays is null) model.ProbationStartDate = null;
        if (model.EmploymentType != EmploymentType.PartTime) { model.WorkingDays = []; }
        foreach (var (field, value) in new[] { (nameof(model.WorkStart), model.WorkStart), (nameof(model.WorkEnd), model.WorkEnd) })
            if (!string.IsNullOrWhiteSpace(value) && StaffCreateModel.Time(value) is null) ModelState.AddModelError(field, "Enter a valid time.");
        // Employment rules are checked before anyone is created, so a saved person always has a valid configuration.
        var input = model.Employment();
        AddErrors(employment.Validate(input));

        if (ModelState.IsValid)
        {
            var result = model.Category == NewStaffCategory.Management
                ? await management.CreateAsync(member, ct)
                : await teachers.CreateAsync(model.FullName, model.JobTitle, model.ContactNumber, model.DepartmentId, model.Subcategory, model.Subject, ct);
            if (result.Succeeded && result.EntityId is { } id)
            {
                ServiceResult saved;
                try { saved = await employment.SaveAsync(id, input, ct); }
                catch (Microsoft.EntityFrameworkCore.DbUpdateException) { saved = ServiceResult.Fail("", "The employment details could not be saved. Try again."); }
                if (saved.Succeeded)
                {
                    var where = model.Category == NewStaffCategory.Management ? "the Management Team" : "Academic Staff";
                    var message = $"{model.FullName.Trim()} was added to {where}.";
                    var profile = model.Category == NewStaffCategory.Management ? $"/management/members/{id}" : $"/staff/{id}";
                    if (IsAjax) return AjaxSuccess(message, model.FullPage ? profile : null);
                    Toast(message);
                    return LocalRedirect(profile);
                }
                // Nothing half-saved: the person just created is removed again (they have no other records yet).
                if (model.Category == NewStaffCategory.Management) await management.DeleteAsync(id, ct); else await teachers.DeleteAsync(id, ct);
                result = saved;
            }
            AddErrors(result);
        }
        await FillAsync(model, ct);
        return FormView("Create", model);
    }

    private async Task FillAsync(StaffCreateModel model, CancellationToken ct)
    {
        model.Departments = await teachers.DepartmentsAsync(ct);
        model.JobTitleSuggestions = await teachers.JobTitlesAsync(ct);
        model.Holders = await management.PositionHoldersAsync(null, ct);
        model.CustomPositions = await management.CustomPositionsAsync(null, ct);
        if (model.Position is { } held && model.Holders.ContainsKey(held)) model.Position = null;
        model.PositionChoice ??= PositionChoices.Of(model.Position, null);
    }

    [HttpGet("edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var teacher = await teachers.GetAsync(id, ct);
        if (teacher is null) return NotFoundResult("This staff member no longer exists.");
        return PageOrPartial("~/Views/Teachers/Form.cshtml", new TeacherFormModel
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

    [HttpPost("edit/{id:int}"), ValidateAntiForgeryToken]
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
        return FormView("~/Views/Teachers/Form.cshtml", model);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Profile(int id, CancellationToken ct)
    {
        var profile = await staffDuty.ProfileAsync(id, ct);
        if (profile is null) return NotFound();
        var teacher = await teachers.GetAsync(id, ct);
        return View(new StaffProfileViewModel
        {
            Profile = profile,
            Teacher = teacher,
            Workload = profile.Staff.StaffType == "Teacher" ? await workload.SummaryAsync(id, SchoolWeek.Current, ct) : null,
            WorkloadAssignments = await workload.AssignmentsAsync(id, SchoolWeek.Current, ct),
            WorkloadHistory = await workload.HistoryAsync(id, 50, ct),
            ClassAssignments = await classTeams.ForStaffAsync(id, ct),
        });
    }

    [HttpGet("assignments/{assignmentId:int}/evaluate")]
    public async Task<IActionResult> Evaluate(int assignmentId, CancellationToken ct)
    {
        var context = await staffDuty.EvaluationContextAsync(assignmentId, ct);
        if (context is null) return NotFoundResult("This duty assignment no longer exists.");
        var model = new FeedbackFormModel
        {
            AssignmentId = assignmentId,
            EvaluationDate = SchoolWeek.Today,
            EvaluatorStaffId = currentUser.StaffId != context.StaffId ? currentUser.StaffId : null,
        };
        await FillAsync(model, context, ct);
        return PageOrPartial("Evaluate", model);
    }

    [HttpPost("assignments/{assignmentId:int}/evaluate"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(int assignmentId, FeedbackFormModel model, CancellationToken ct)
    {
        model.AssignmentId = assignmentId;
        var context = await staffDuty.EvaluationContextAsync(assignmentId, ct);
        if (context is null) return NotFoundResult("This duty assignment no longer exists.");
        if (ModelState.IsValid)
        {
            var result = await staffDuty.AddFeedbackAsync(model.ToInput(), ct);
            if (result.Succeeded)
            {
                var message = $"Evaluation of {context.StaffName} saved ({model.Rating}/5).";
                if (IsAjax) return AjaxSuccess(message);
                Toast(message);
                return Redirect($"/staff/{context.StaffId}");
            }
            AddErrors(result);
        }
        await FillAsync(model, context, ct);
        return FormView("Evaluate", model);
    }

    private async Task FillAsync(FeedbackFormModel model, EvaluationContext context, CancellationToken ct)
    {
        model.Context = context;
        model.Evaluators = (await duties.StaffOptionsAsync(ct)).Where(s => s.Id != context.StaffId).ToList();
    }

    private IActionResult Saved(string message)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return RedirectToAction(nameof(Members));
    }
}
