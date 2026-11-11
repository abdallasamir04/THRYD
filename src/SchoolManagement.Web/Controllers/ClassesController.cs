using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Classes: a landing page with the four academic stages (see <see cref="ClassStages"/>), each stage's
/// classes, and the full class list (/classes/all). Every page reads the same Classes table.
/// </summary>
public class ClassesController(IClassService classes) : AppController
{
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

    /// <summary>Download Report: the classes matching the page's search and status.</summary>
    [HttpGet]
    public async Task<IActionResult> Pdf(string? search, string? status, CancellationToken ct) =>
        await PdfFile(Services.Pdf.PdfReports.Classes(await classes.ListAsync(search, status, ct), search, status), ct);

    /// <summary>
    /// Add class. With <paramref name="group"/> (opened from a stage) the grade is chosen from that group and
    /// the section letter is assigned automatically; without it, the original stage + section form.
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

        if (!string.IsNullOrEmpty(model.Group))
        {
            if (ClassStages.FindGroup(model.Group) is not { } found || !found.Group.CanAdd) return NotFoundResult("Classes cannot be added to this group.");
            var (stage, group) = found;
            // The stage code comes from the group, never from the form, so a class cannot land in another stage.
            model.GradeLevel = group.Code;
            await FillGroupAsync(model, stage, group, ct);
            if (model.GradeNumber is not { } grade || !model.GradeOptions.Any(o => o.Number == grade))
                ModelState.AddModelError(nameof(ClassFormModel.GradeNumber), $"Choose a grade of {group.Title}.");
            if (ModelState.IsValid)
            {
                var result = await classes.CreateInGradeAsync(group.Code, model.GradeNumber!.Value, model.Section, model.Capacity, model.IsActive, ct);
                if (result.Succeeded)
                {
                    var created = await classes.GetAsync(result.EntityId!.Value, ct);
                    return Saved($"Class {created?.Name} was added to {group.Title}.", stage.GroupHref(group));
                }
                AddErrors(result);
            }
            return FormView("Form", model);
        }

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

    private async Task FillGroupAsync(ClassFormModel model, ClassStages.Stage stage, ClassStages.Group group, CancellationToken ct)
    {
        var names = (await classes.ListAsync(null, null, ct)).Select(c => c.Name).ToList();
        var options = new List<(int, string, string?)>();
        foreach (var n in ClassStages.AddableGrades(group, names))
        {
            var isNew = group.IsOpenEnded && !names.Any(x => ClassStages.GradeOf(x) == new ClassGrade(group.Code, n));
            options.Add((n, $"{group.Code}{n}{(isNew ? " (new grade)" : "")}", await classes.NextNameInGradeAsync(group.Code, n, ct)));
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

    /// <summary>A group's page: one block of class cards per grade (Pre-K is a single block without Add class).</summary>
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
                : g.IsPreK ? "Pre-K classes do not use the KG / Grade numbering, so they are edited from their class card rather than added here." : null,
        };
    }
}
