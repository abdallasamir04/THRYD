using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Classes: a landing page with the four academic stages (see <see cref="ClassStages"/>), each stage's
/// classes, and the full class list (/classes/all). Every page reads the same Classes table.
/// </summary>
public class ClassesController(IClassService classes, IClassTeamService teams, IStudentService studentList) : AppController
{
    // ---------- class details / KG Class Teaching Team ----------

    /// <summary>
    /// Class page. By default it shows the class's students (the same list as /students, limited to this class). For KG
    /// Stage classes (Pre-K, KG1, KG2) a Teaching Team button opens the team panel (<c>?panel=team</c>), and the edit form
    /// appears only on request (<c>&amp;edit=1</c>); other stages have no teaching team at all.
    /// </summary>
    [HttpGet("/classes/{id:int}")]
    public async Task<IActionResult> Details(int id, int? year, string? panel, string? edit, [FromQuery] StudentListQuery students, CancellationToken ct)
    {
        var row = await classes.GetAsync(id, ct);
        if (row is null) return NotFound();
        var isKg = ClassStages.IsKgStage(row.Name);
        var showTeam = isKg && panel == "team";

        students.ClassId = id;
        students.Search = null;
        // Sorting, paging and refresh after a student is edited or deleted reload just the list.
        if (IsAjax && !showTeam) return PartialView("~/Views/Students/_Table.cshtml", await StudentListAsync(students, row, ct));

        var model = await DetailsModelAsync(row, year, showTeam ? (edit is "1" or "true" ? ClassPanel.TeamEdit : ClassPanel.Team) : ClassPanel.Students, ct);
        if (!showTeam) model.Students = await StudentListAsync(students, row, ct);
        if (model.Team is { } t)
        {
            model.HomeroomId = t.Homeroom?.StaffId;
            model.CoTeacherId = t.CoTeacher?.StaffId;
        }
        return View(model);
    }

    /// <summary>Save Changes: the Homeroom Teacher (one) and the Co-Teacher (one) for the class and year; either may be empty.</summary>
    [HttpPost("/classes/{id:int}/team"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTeam(int id, int yearId, int? homeroomId, int? coTeacherId, [FromForm(Name = "CoTeacherIds")] List<int>? coTeacherIds, CancellationToken ct)
    {
        // Older forms and direct requests may still send a list: one Co-Teacher at most.
        var result = (coTeacherIds?.Distinct().Count() ?? 0) > 1
            ? Common.ServiceResult.Fail("CoTeacherId", "A KG class has one Co-Teacher. Choose a single staff member.")
            : await teams.SaveAsync(id, yearId, homeroomId, coTeacherId ?? coTeacherIds?.FirstOrDefault(), ct);
        if (result.Succeeded)
        {
            Toast("Class teaching team saved.");
            return Redirect(TeamHref(id, yearId));
        }
        var row = await classes.GetAsync(id, ct);
        if (row is null) return NotFound();
        var model = await DetailsModelAsync(row, yearId, ClassPanel.TeamEdit, ct);
        if (model.Team is null)
        {
            // Not a KG class (or the year is gone): nothing to re-show, say why.
            Toast(result.Errors[0].Value, "error");
            return Redirect($"/classes/{id}");
        }
        // Back to the edit form with the posted choices and the messages.
        AddErrors(result);
        model.HomeroomId = homeroomId;
        model.CoTeacherId = coTeacherId ?? coTeacherIds?.FirstOrDefault();
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return View("Details", model);
    }

    /// <summary>Remove Assignment: ends one Homeroom Teacher or Co-Teacher assignment; the rest of the team is unchanged.</summary>
    [HttpPost("/classes/{id:int}/team/{assignmentId:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveTeamMember(int id, int assignmentId, int yearId, CancellationToken ct)
    {
        var result = await teams.RemoveAsync(id, assignmentId, ct);
        Toast(result.Succeeded ? "Assignment removed. It stays in the class history." : result.Errors[0].Value, result.Succeeded ? "success" : "error");
        return Redirect(TeamHref(id, yearId));
    }

    /// <summary>The Teaching Team panel of a class for an academic year.</summary>
    public static string TeamHref(int classId, int? yearId, bool edit = false) =>
        $"/classes/{classId}?panel=team{(yearId is { } y ? $"&year={y}" : "")}{(edit ? "&edit=1" : "")}";

    private async Task<ClassDetailsViewModel> DetailsModelAsync(ClassRow row, int? yearId, ClassPanel panel, CancellationToken ct)
    {
        var place = ClassStages.Locate(row.Name);
        var isKg = ClassStages.IsKgStage(row.Name);
        IReadOnlyList<AcademicYearOption> years = isKg ? await teams.YearsAsync(ct) : [];
        var year = years.FirstOrDefault(y => y.Id == yearId) ?? years.FirstOrDefault(y => y.IsCurrent);
        return new ClassDetailsViewModel
        {
            Class = row,
            StageTitle = place?.Stage.Title,
            StageHref = place?.Stage.Href,
            GroupTitle = place?.Group.Title,
            IsKg = isKg,
            Panel = isKg ? panel : ClassPanel.Students,
            Years = years,
            Team = year is null ? null : await teams.TeamAsync(row.Id, year.Id, ct),
            // The staff list is loaded only for the edit form; it is never on the page otherwise.
            Staff = isKg && panel == ClassPanel.TeamEdit ? await teams.StaffOptionsAsync(ct) : [],
        };
    }

    private async Task<StudentIndexViewModel> StudentListAsync(StudentListQuery query, ClassRow row, CancellationToken ct) => new()
    {
        Query = query,
        Result = await studentList.ListAsync(query, ct),
        Classes = [new ClassOption(row.Id, row.Name, row.IsActive, row.StudentCount, row.Capacity)],
        TotalStudents = row.StudentCount,
        FixedClass = true,
    };

    /// <summary>Classes landing page: KG Stages, Elementary, Middle School and High School.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(string? search, string? status, string? view, CancellationToken ct)
    {
        // Older links to the list (/classes?status=inactive, /classes?view=table) open All classes with the same filters.
        if (search is not null || status is not null || view is not null) return Redirect("/classes/all" + Request.QueryString);

        var all = await classes.ListAsync(null, null, ct);
        var outside = all.Count(c => ClassStages.Locate(c.Name) is null);
        var extra = new List<(string Label, string Href, string Icon)> { ("All classes", "/classes/all", "list") };
        if (outside > 0) extra.Add(($"{Ui.Plural(outside, "class", "classes")} outside the four stages", "/classes/stages/other", "info"));
        return View(new HubViewModel
        {
            Title = "Classes",
            Subtitle = "Choose a stage to see its classes and add new ones. Every class belongs to the stage of its grade.",
            Crumbs = [("Dashboard", "/"), ("Classes", null)],
            Large = true,
            Nav = "Classes",
            Cards = ClassStages.All.Select(s => new HubCard(s.Href, s.Icon, s.Title, s.Description,
                Meta(all.Where(c => ClassStages.Locate(c.Name)?.Stage == s).ToList()), s.Tone)).ToList(),
            Extra = extra,
        });
    }

    /// <summary>The full class list with search, status filter and cards / table layouts.</summary>
    [HttpGet]
    public async Task<IActionResult> All(string? search, string? status, string? view, CancellationToken ct)
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

    /// <summary>
    /// A stage: KG Stages lists Pre-K, KG1 and KG2 with their classes; Elementary shows its two subgroup
    /// cards; Middle School and High School show their classes directly. "other" lists classes in no stage.
    /// </summary>
    [HttpGet("/classes/stages/{stage}")]
    public async Task<IActionResult> Stage(string stage, CancellationToken ct)
    {
        var all = await classes.ListAsync(null, null, ct);
        if (string.Equals(stage, "other", StringComparison.OrdinalIgnoreCase))
        {
            return View("Stage", new ClassStagePageViewModel
            {
                Title = "Other classes",
                Subtitle = "Classes whose names match no stage grade (Pre-K, KG1–KG2, G1 onward). Rename a class to place it in its stage.",
                Crumbs = [("Dashboard", "/"), ("Classes", "/classes"), ("Other classes", null)],
                Blocks = [new ClassBlock("Other classes", null, null, null, all.Where(c => ClassStages.Locate(c.Name) is null).ToList())],
            });
        }

        var s = ClassStages.Find(stage);
        if (s is null) return NotFound();
        switch (s.Layout)
        {
            case StageLayout.Single:
                return View("Stage", GroupPage(s, s.Groups[0], all));
            case StageLayout.Cards:
                return View("~/Views/Staff/Hub.cshtml", new HubViewModel
                {
                    Title = s.Title,
                    Subtitle = s.Description,
                    Crumbs = [("Dashboard", "/"), ("Classes", "/classes"), (s.Title, null)],
                    Large = true,
                    Nav = "Classes",
                    Cards = s.Groups.Select((g, i) => new HubCard(s.GroupHref(g), i == 0 ? "door" : "school", g.Title,
                        $"{g.Grades}: {GradeList(g)} classes.", Meta(InGroup(all, g)), i == 0 ? "cyan" : "blue")).ToList(),
                });
            default:
                return View("Stage", new ClassStagePageViewModel
                {
                    Title = s.Title,
                    Subtitle = s.Description,
                    Crumbs = [("Dashboard", "/"), ("Classes", "/classes"), (s.Title, null)],
                    Blocks = s.Groups.Select(g => new ClassBlock(g.Title, g.Grades, g.CanAdd ? AddHref(g) : null, s.GroupHref(g), InGroup(all, g))).ToList(),
                });
        }
    }

    /// <summary>A group inside a stage (KG1, Lower Elementary…): its classes by grade, with Add class.</summary>
    [HttpGet("/classes/stages/{stage}/{group}")]
    public async Task<IActionResult> Group(string stage, string group, CancellationToken ct)
    {
        var s = ClassStages.Find(stage);
        var g = s?.Groups.FirstOrDefault(x => string.Equals(x.Slug, group, StringComparison.OrdinalIgnoreCase));
        if (s is null || g is null) return NotFound();
        if (s.Layout == StageLayout.Single) return Redirect(s.Href);
        return View("Stage", GroupPage(s, g, await classes.ListAsync(null, null, ct)));
    }

    /// <summary>
    /// Classes page → Download Students: one A4 portrait page per class (active and inactive), with its information and
    /// the numbered list of its enrolled students. Read-only.
    /// </summary>
    [HttpGet("/classes/students-report")]
    public async Task<IActionResult> StudentsReport([FromServices] IClassRosterService roster, [FromServices] ISettingsService settings, CancellationToken ct) =>
        await PdfFile(Services.Pdf.ClassRosterReports.Students(await roster.LoadAsync(ct), (await settings.GetProfileAsync(ct)).SchoolName), ct);

    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Classes page → Download Summary Excel: the same data and rules as the summary PDF, as a workbook. Read-only.</summary>
    [HttpGet("/classes/summary-excel")]
    public async Task<IActionResult> SummaryExcel([FromServices] IClassRosterService roster, [FromServices] ISettingsService settings,
        [FromServices] Services.Excel.IExcelExportService excel, CancellationToken ct)
    {
        var data = await roster.LoadAsync(ct);
        var profile = await settings.GetProfileAsync(ct);
        return File(excel.ClassesSummary(data, profile.SchoolName, profile.AcademicYear), XlsxType,
            Services.Pdf.PdfFileName.From(["Classes Summary", data.YearName]).Replace(".pdf", ".xlsx"));
    }

    /// <summary>Classes page → Download Students Excel: a Class Index and one worksheet per class with its students. Read-only.</summary>
    [HttpGet("/classes/students-excel")]
    public async Task<IActionResult> StudentsExcel([FromServices] IClassRosterService roster, [FromServices] ISettingsService settings,
        [FromServices] Services.Excel.IExcelExportService excel, CancellationToken ct)
    {
        var data = await roster.LoadAsync(ct);
        var profile = await settings.GetProfileAsync(ct);
        return File(excel.ClassesStudents(data, profile.SchoolName, profile.AcademicYear), XlsxType,
            Services.Pdf.PdfFileName.From(["Classes and Students Report", data.YearName]).Replace(".pdf", ".xlsx"));
    }

    /// <summary>
    /// Classes page → Download Summary (All classes → Download Report): school statistics, the stage summary and the class
    /// summary of the classes matching the page's search and status. A4 portrait.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Pdf(string? search, string? status, [FromServices] IClassRosterService roster, [FromServices] ISettingsService settings,
        CancellationToken ct)
    {
        var matching = (await classes.ListAsync(search, status, ct)).Select(c => c.Id).ToHashSet();
        return await PdfFile(Services.Pdf.ClassRosterReports.Summary((await roster.LoadAsync(ct)).Only(matching),
            (await settings.GetProfileAsync(ct)).SchoolName, search, status), ct);
    }

    /// <summary>
    /// Add class: only the grade is chosen; the server gives the class the next section letter of that grade (KG1A, KG1B
    /// → KG1C; a grade without classes starts at A), each grade counted on its own. With <paramref name="group"/> (opened
    /// from a stage) the grade list is that group's; without it (Classes → All classes) every grade of every stage.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Create(string? group, int? grade, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(group))
        {
            if (ClassStages.FindGroup(group) is not { } found || !found.Group.CanAdd) return NotFoundResult("Classes cannot be added to this group.");
            var grouped = new ClassFormModel { Group = found.Group.Slug, GradeLevel = found.Group.Code, GradeNumber = grade };
            await FillGroupAsync(grouped, found.Stage, found.Group, ct);
            if (!grouped.GradeOptions.Any(o => o.Number == grouped.GradeNumber)) grouped.GradeNumber = grouped.GradeOptions.FirstOrDefault().Number;
            return PageOrPartial("Form", grouped);
        }

        var model = new ClassFormModel();
        await FillAllGradesAsync(model, ct);
        return PageOrPartial("Form", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ClassFormModel model, CancellationToken ct)
    {
        model.Id = null;
        // The class name, number and section are never taken from the form: the server calculates them.
        ModelState.Remove(nameof(ClassFormModel.Name));
        ModelState.Remove(nameof(ClassFormModel.SortOrder));

        // Every grade (no group): the chosen "group:grade" option picks the group and the grade.
        var allGrades = string.IsNullOrEmpty(model.Group);
        if (allGrades)
        {
            if (ClassFormModel.ParseKey(model.GradeKey) is { } key && ClassStages.FindGroup(key.Group) is { Group.CanAdd: true })
                (model.Group, model.GradeNumber) = key;
            else
            {
                ModelState.AddModelError(nameof(ClassFormModel.GradeKey), "Choose a grade.");
                await FillAllGradesAsync(model, ct);
                return FormView("Form", model);
            }
        }

        if (ClassStages.FindGroup(model.Group) is not { } found || !found.Group.CanAdd) return NotFoundResult("Classes cannot be added to this group.");
        var (stage, group) = found;
        // The stage code comes from the group, never from the form, so a class cannot land in another stage.
        model.GradeLevel = group.Code;
        await FillGroupAsync(model, stage, group, ct);
        if (model.GradeNumber is not { } grade || !model.GradeOptions.Any(o => o.Number == grade))
            ModelState.AddModelError(allGrades ? nameof(ClassFormModel.GradeKey) : nameof(ClassFormModel.GradeNumber), allGrades ? "Choose a grade." : $"Choose a grade of {group.Title}.");
        if (ModelState.IsValid)
        {
            // No section is passed: the service takes the grade's next letter under its numbering lock and refuses duplicates.
            var result = await classes.CreateInGradeAsync(group.Code, model.GradeNumber!.Value, null, model.Capacity, model.IsActive, ct);
            if (result.Succeeded)
            {
                var created = await classes.GetAsync(result.EntityId!.Value, ct);
                return allGrades ? Saved($"Class {created?.Name} was added.") : Saved($"Class {created?.Name} was added to {group.Title}.", stage.GroupHref(group));
            }
            AddErrors(result);
        }
        if (allGrades)
        {
            model.Group = null;
            await FillAllGradesAsync(model, ct);
        }
        return FormView("Form", model);
    }

    /// <summary>Every grade of every stage group, with the name the next class of each grade will get.</summary>
    private async Task FillAllGradesAsync(ClassFormModel model, CancellationToken ct)
    {
        var names = (await classes.ListAsync(null, null, ct)).Select(c => c.Name).ToList();
        var stages = new List<(string, IReadOnlyList<(string, string)>)>();
        foreach (var stage in ClassStages.All)
        {
            var grades = new List<(string, string)>();
            foreach (var group in stage.Groups.Where(g => g.CanAdd))
                foreach (var n in ClassStages.AddableGrades(group, names))
                {
                    var grade = new ClassGrade(group.Code, n);
                    var isNew = group.IsOpenEnded && !names.Any(x => ClassStages.GradeOf(x) == grade);
                    var next = await classes.NextNameInGradeAsync(group.Code, n, ct);
                    grades.Add((ClassFormModel.KeyOf(group.Slug, n), $"{grade.Label}{(isNew ? " (new grade)" : "")}{(next is null ? "" : $" — next class {next}")}"));
                }
            stages.Add((stage.Title, grades));
        }
        model.AllGradeOptions = stages;
    }

    private async Task FillGroupAsync(ClassFormModel model, ClassStages.Stage stage, ClassStages.Group group, CancellationToken ct)
    {
        var names = (await classes.ListAsync(null, null, ct)).Select(c => c.Name).ToList();
        var options = new List<(int, string, string?)>();
        foreach (var n in ClassStages.AddableGrades(group, names))
        {
            var isNew = group.IsOpenEnded && !names.Any(x => ClassStages.GradeOf(x) == new ClassGrade(group.Code, n));
            options.Add((n, $"{new ClassGrade(group.Code, n).Label}{(isNew ? " (new grade)" : "")}", await classes.NextNameInGradeAsync(group.Code, n, ct)));
        }
        model.GradeOptions = options;
        model.StageLink = stage.Layout == StageLayout.Single ? null : (stage.Title, stage.Href);
        model.GroupLink = (group.Title, stage.GroupHref(group));
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

    private IActionResult Saved(string message, string? returnUrl = null)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return returnUrl is null ? RedirectToAction(nameof(Index)) : Redirect(returnUrl);
    }

    // ---------- stage pages ----------

    private static List<ClassRow> InGroup(IEnumerable<ClassRow> all, ClassStages.Group g) =>
        all.Where(c => ClassStages.GradeOf(c.Name) is { } grade && g.Contains(grade)).ToList();

    private static string AddHref(ClassStages.Group g, int? grade = null) =>
        $"/classes/create?group={g.Slug}{(grade is null ? "" : $"&grade={grade}")}";

    private static string GradeList(ClassStages.Group g)
    {
        var grades = Enumerable.Range(g.MinGrade, (g.MaxGrade ?? g.MinGrade) - g.MinGrade + 1).Select(n => $"{g.Code}{n}").ToList();
        return grades.Count == 1 ? grades[0] : $"{string.Join(", ", grades[..^1])} and {grades[^1]}";
    }

    private static string Meta(IReadOnlyCollection<ClassRow> rows) =>
        $"{Ui.Plural(rows.Count, "class", "classes")} · {Ui.Plural(rows.Sum(c => c.StudentCount), "student", "students")}";

    /// <summary>A group's page: one block of class cards per grade (Pre-K is a single block; Add class is in the page head).</summary>
    private static ClassStagePageViewModel GroupPage(ClassStages.Stage s, ClassStages.Group g, IReadOnlyList<ClassRow> all)
    {
        var rows = InGroup(all, g);
        var crumbs = new List<(string, string?)> { ("Dashboard", "/"), ("Classes", "/classes") };
        if (s.Layout != StageLayout.Single) crumbs.Add((s.Title, s.Href));
        crumbs.Add((g.Title, null));

        IReadOnlyList<ClassBlock> blocks = g.IsPreK
            ? [new ClassBlock("Pre-K", "Pre-Kindergarten", null, null, rows)]
            : ClassStages.ShownGrades(g, rows.Select(r => r.Name))
                .Select(n => new ClassBlock($"{g.Code}{n}", g.Code == "KG" ? $"Kindergarten {n}" : $"Grade {n}", AddHref(g, n), null,
                    rows.Where(r => ClassStages.GradeOf(r.Name)?.Number == n).ToList()))
                .ToList();

        return new ClassStagePageViewModel
        {
            Title = g.Title,
            Subtitle = g.IsPreK ? "Pre-Kindergarten classes." : $"{g.Grades}: {GradeList(g)} classes. Open a class to see its students.",
            Crumbs = crumbs,
            Blocks = blocks,
            AddHref = g.CanAdd ? AddHref(g) : null,
            Note = g.IsOpenEnded
                ? "To start a new grade (for example G11), choose it in Add class. Its classes then appear here under High School."
                : null,
        };
    }
}
