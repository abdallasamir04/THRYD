using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>Belongings (administration): daily checklist per student, academic materials, notes and readiness analytics.</summary>
[Route("belongings")]
public class BelongingsController(IBelongingsService belongings, ITimetableService timetable, ITeacherService teachers) : AppController
{
    /// <summary>
    /// Belongings landing page: two cards, Personal Belongings and Academic Belongings, each opening its own section.
    /// Old links with a selection in the query string (/belongings?grade=…) still open the personal section.
    /// </summary>
    [HttpGet("")]
    public IActionResult Index()
    {
        if (Request.QueryString.HasValue) return LocalRedirect("/belongings/personal" + Request.QueryString);
        return View("~/Views/Staff/Hub.cshtml", new HubViewModel
        {
            Title = "Belongings",
            Subtitle = "Daily belongings checks per student, with readiness by day, week and quarter. Choose an area.",
            Crumbs = [("Dashboard", "/"), ("Students", "/students/hub"), ("Belongings", null)],
            Large = true,
            Nav = "Belongings",
            Cards =
            [
                new("/belongings/personal", "briefcase", "Personal Belongings",
                    "Jacket, School Bag, Pencil Case, Water Flask and Lunch Box / Bag, with daily notes.", "Green · Red · N/A per item", "teal"),
                new("/belongings/academic", "graduation-cap", "Academic Belongings",
                    "Book and Copy Book per subject, chosen by department and subject.", "Green · Red · N/A per subject", "blue"),
            ],
            Extra = [("Academic calendar", "/belongings/calendar", "calendar")],
        });
    }

    /// <summary>Personal Belongings section. Year → Quarter → Week → Date → Grade → Student in the query string; defaults to today.</summary>
    [HttpGet("personal")]
    public Task<IActionResult> PersonalSection(int? yearId, int? quarterId, int? week, string? date, string? grade, int? studentId, CancellationToken ct) =>
        SectionAsync(BelongingsSection.Personal, yearId, quarterId, week, date, grade, studentId, ct);

    /// <summary>Academic Belongings section (Book and Copy Book per department and subject). Same selection as the personal section.</summary>
    [HttpGet("academic")]
    public Task<IActionResult> AcademicSection(int? yearId, int? quarterId, int? week, string? date, string? grade, int? studentId, CancellationToken ct) =>
        SectionAsync(BelongingsSection.Academic, yearId, quarterId, week, date, grade, studentId, ct);

    private async Task<IActionResult> SectionAsync(BelongingsSection section, int? yearId, int? quarterId, int? week, string? date, string? grade, int? studentId, CancellationToken ct)
    {
        var (years, year, quarter, w, day) = await SelectionAsync(yearId, quarterId, week, date, ct);

        var gradeKey = GradeCatalog.All.Any(g => g.Key == grade) ? grade : null;
        var students = await belongings.StudentsAsync(gradeKey, ct);
        var student = students.FirstOrDefault(s => s.Id == studentId); // only students of the selected grade

        var depts = new List<(int, string, IReadOnlyList<string>)>();
        if (section == BelongingsSection.Academic && student is not null && day is not null)
            foreach (var dept in await teachers.DepartmentsAsync(ct)) depts.Add((dept.Id, dept.Name, await timetable.SubjectsAsync(dept.Id, ct)));

        return View("Index", new BelongingsPageViewModel
        {
            Section = section,
            Years = years, Year = year, Quarter = quarter, Week = w, Date = day, Grade = gradeKey, Students = students, Student = student,
            Day = student is not null && day is { } dd ? await belongings.DayAsync(student.Id, dd, ct) : null,
            WeekStats = student is not null && quarter is not null && w is not null ? await belongings.WeekAsync(student.Id, quarter, w.Number, ct) : null,
            QuarterStats = student is not null && quarter is not null ? await belongings.QuarterAnalyticsAsync(student.Id, quarter, ct) : null,
            GradeWeek = gradeKey is not null && quarter is not null && w is not null ? await belongings.GradeWeekAsync(gradeKey, quarter, w.Number, ct) : [],
            Departments = depts,
        });
    }

    /// <summary>Download Report: the selected grade's weekly readiness table (same year, quarter and week as the page).</summary>
    [HttpGet("grade-week/pdf")]
    public async Task<IActionResult> GradeWeekPdf(int? yearId, int? quarterId, int? week, string? date, string? grade, CancellationToken ct)
    {
        var (_, _, quarter, w, _) = await SelectionAsync(yearId, quarterId, week, date, ct);
        var gradeKey = GradeCatalog.All.Any(g => g.Key == grade) ? grade : null;
        if (gradeKey is null || quarter is null || w is null) return NotFound();
        return await PdfFile(PdfReports.BelongingsGradeWeek(gradeKey, quarter, w, await belongings.GradeWeekAsync(gradeKey, quarter, w.Number, ct)), ct);
    }

    /// <summary>Year → Quarter → Week → Date from the query string; defaults to the quarter and week holding the date (or today).</summary>
    private async Task<(IReadOnlyList<YearView> Years, YearView? Year, QuarterView? Quarter, WeekInfo? Week, DateOnly? Day)> SelectionAsync(
        int? yearId, int? quarterId, int? week, string? date, CancellationToken ct)
    {
        var years = await belongings.YearsAsync(ct);
        var picked = DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d) ? d : (DateOnly?)null;

        // Default: the quarter holding the chosen date (or today).
        var todayQuarter = quarterId is null && yearId is null ? await belongings.QuarterForDateAsync(picked ?? SchoolWeek.Today, ct) : null;
        var year = years.FirstOrDefault(y => y.Id == (yearId ?? todayQuarter?.YearId)) ?? (yearId is null ? years.FirstOrDefault() : null);
        var quarter = year?.Quarters.FirstOrDefault(q => q.Id == (quarterId ?? todayQuarter?.Id) && q.IsConfigured)
                      ?? year?.Quarters.FirstOrDefault(q => q.IsConfigured && quarterId is null);
        var weeks = quarter?.Weeks ?? [];
        var w = weeks.FirstOrDefault(x => x.Number == week)
                ?? weeks.FirstOrDefault(x => picked is { } p && x.Days.Contains(p))
                ?? weeks.FirstOrDefault(x => x.Days.Contains(SchoolWeek.Today))
                ?? weeks.FirstOrDefault();
        DateOnly? day = w is null ? null : picked is { } pd && w.Days.Contains(pd) ? pd : w.Days.Contains(SchoolWeek.Today) ? SchoolWeek.Today : w.Days.FirstOrDefault();
        return (years, year, quarter, w, day);
    }

    [HttpGet("subjects")]
    public async Task<IActionResult> Subjects(int departmentId, CancellationToken ct) => Json(await timetable.SubjectsAsync(departmentId, ct));

    [HttpPost("personal"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Personal(PersonalFormModel model, string? back, CancellationToken ct) =>
        Done(ModelState.IsValid ? await belongings.SavePersonalAsync(model.ToInput(), ct) : Invalid(), "Personal belongings and notes saved.", back);

    [HttpPost("materials"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Material(MaterialFormModel model, string? back, CancellationToken ct) =>
        Done(ModelState.IsValid ? await belongings.SaveMaterialAsync(model.ToInput(), ct) : Invalid(), $"Academic materials saved for {model.Subject}.", back);

    [HttpPost("materials/{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMaterial(int id, string? back, CancellationToken ct) =>
        Done(await belongings.DeleteMaterialAsync(id, ct), "Subject record removed.", back);

    // ------------------------------------------------------------------ calendar (academic years and quarters)

    [HttpGet("calendar")]
    public async Task<IActionResult> Calendar(CancellationToken ct) => View(await belongings.YearsAsync(ct));

    [HttpPost("years"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateYear(string? name, CancellationToken ct) =>
        Done(await belongings.CreateYearAsync(name, ct), $"Academic year {name?.Trim()} added with four quarters. Set each quarter's dates.", "/belongings/calendar");

    [HttpGet("quarters/{id:int}/edit")]
    public async Task<IActionResult> EditQuarter(int id, CancellationToken ct)
    {
        var q = await belongings.QuarterAsync(id, ct);
        if (q is null) return NotFoundResult("This quarter no longer exists.");
        return PageOrPartial("QuarterForm", new QuarterFormModel { Id = id, Name = q.Name, StartDate = q.StartDate, EndDate = q.EndDate, Quarter = q });
    }

    [HttpPost("quarters/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditQuarter(int id, QuarterFormModel model, CancellationToken ct)
    {
        model.Id = id;
        model.Quarter = await belongings.QuarterAsync(id, ct);
        if (model.Quarter is null) return NotFoundResult("This quarter no longer exists.");
        if (ModelState.IsValid)
        {
            var result = await belongings.UpdateQuarterAsync(id, model.Name, model.StartDate, model.EndDate, ct);
            if (result.Succeeded) return Done(result, $"{model.Name.Trim()} saved: 8 school weeks.", "/belongings/calendar");
            AddErrors(result);
        }
        return FormView("QuarterForm", model);
    }

    // ------------------------------------------------------------------ helpers

    private ServiceResult Invalid() =>
        ServiceResult.Fail("", string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).Distinct()));

    /// <summary>AJAX: toast + page refresh, or the first error as an error toast. Otherwise redirect back with a toast.</summary>
    private IActionResult Done(ServiceResult result, string success, string? back)
    {
        var message = result.Succeeded ? success : string.Join(" ", result.Errors.Select(e => e.Value).Distinct());
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 422);
        Toast(message, result.Succeeded ? "success" : "error");
        var safe = back is not null && back.StartsWith("/belongings", StringComparison.Ordinal) ? back : "/belongings";
        return LocalRedirect(safe);
    }
}
