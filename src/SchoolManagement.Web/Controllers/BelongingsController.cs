using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Belongings (Students → Belongings). Two modules, each with Daily Checks and History &amp; Reports:
/// <list type="bullet">
/// <item><b>Personal Belongings</b>: classes → a class's students → the five items and notes for the school day. No timetable.</item>
/// <item><b>Academic Belongings</b>: classes with their current lesson → the lesson in progress (from the timetable and the
/// school clock, never chosen by hand) → each student's Book and Copy Book for that lesson.</item>
/// </list>
/// </summary>
[Route("belongings")]
public class BelongingsController(IBelongingsDailyService daily, IBelongingsHistoryService history, IExcelExportService excel, ISettingsService settings) : AppController
{
    /// <summary>Belongings landing page: Personal Belongings and Academic Belongings. Old links with a selection open Personal Belongings.</summary>
    [HttpGet("")]
    public IActionResult Index()
    {
        if (Request.QueryString.HasValue) return LocalRedirect("/belongings/personal");
        return View("~/Views/Staff/Hub.cshtml", new HubViewModel
        {
            Title = "Belongings",
            Subtitle = "Daily belongings checks, class by class, with complete history and reports. Choose an area.",
            Crumbs = [("Dashboard", "/"), ("Students", "/students/hub"), ("Belongings", null)],
            Large = true,
            Nav = "Belongings",
            Cards =
            [
                new("/belongings/personal", "briefcase", "Personal Belongings",
                    "Jacket, School Bag, Pencil Case, Water Flask and Lunch Box / Bag for every student, every school day, with notes.", "Daily checks per class", "teal"),
                new("/belongings/academic", "graduation-cap", "Academic Belongings",
                    "Book and Copy Book for every student in the lesson in progress, found from the class timetable.", "Lesson-based checks", "blue"),
            ],
        });
    }

    // ================================================================== Personal Belongings

    [HttpGet("personal")]
    public async Task<IActionResult> Personal(string? date, CancellationToken ct) =>
        View("Personal", await daily.PersonalClassesAsync(Day(date), ct));

    [HttpGet("personal/classes/{classId:int}")]
    public async Task<IActionResult> PersonalClass(int classId, string? date, CancellationToken ct)
    {
        var day = await daily.PersonalClassAsync(classId, Day(date), ct);
        return day is null ? NotFound() : View("PersonalClass", day);
    }

    [HttpPost("personal/mark"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PersonalMark(int studentId, string? date, string? item, ItemStatus status, CancellationToken ct) =>
        await PersonalJson(await daily.SetPersonalAsync(studentId, Day(date), item ?? "", status, ct), Day(date));

    [HttpPost("personal/all"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PersonalAll(int studentId, string? date, CancellationToken ct) =>
        await PersonalJson(await daily.PersonalAllAvailableAsync(studentId, Day(date), ct), Day(date));

    [HttpPost("personal/notes"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PersonalNotes(int studentId, string? date, string? notes, CancellationToken ct) =>
        await PersonalJson(await daily.SavePersonalNotesAsync(studentId, Day(date), notes, ct), Day(date));

    private async Task<IActionResult> PersonalJson(PersonalMarkResult r, DateOnly date)
    {
        if (!r.Saved) return AjaxFailure(r.Message, 422);
        return Json(new
        {
            success = true, message = r.Message,
            row = r.Row is null ? null : await RenderAsync("_PersonalRow", new PersonalRowModel(r.Row, date, true)),
            counts = Counts(r.Counts),
        });
    }

    [HttpGet("personal/history")]
    public async Task<IActionResult> PersonalHistory([FromQuery] BelongingsQuery query, CancellationToken ct)
    {
        var h = await history.PersonalAsync(query, ct);
        return View("PersonalHistory", new PersonalHistoryViewModel(h, await history.OptionsAsync(query, false, ct), query));
    }

    [HttpGet("personal/history/pdf")]
    public async Task<IActionResult> PersonalPdf([FromQuery] BelongingsQuery query, CancellationToken ct) =>
        await PdfFile(BelongingsReports.Personal(await history.PersonalAsync(query, ct)), ct);

    [HttpGet("personal/history/excel")]
    public async Task<IActionResult> PersonalExcel([FromQuery] BelongingsQuery query, CancellationToken ct)
    {
        var h = await history.PersonalAsync(query, ct);
        return Workbook(excel.BelongingsPersonal(h, (await settings.GetProfileAsync(ct)).SchoolName), "Personal Belongings Report", h.Scope);
    }

    [HttpGet("personal/records/{id:int}")]
    public async Task<IActionResult> PersonalRecord(int id, CancellationToken ct)
    {
        var r = await history.PersonalRecordAsync(id, ct);
        return r is null ? NotFoundResult("This record no longer exists.") : PageOrPartial("PersonalRecord", r);
    }

    // ================================================================== Academic Belongings

    [HttpGet("academic")]
    public async Task<IActionResult> Academic(string? date, CancellationToken ct)
    {
        var day = Day(date);
        return View("Academic", new AcademicDailyViewModel(day, day == SchoolWeek.Today, await daily.AcademicClassesAsync(day, ct)));
    }

    [HttpGet("academic/classes/{classId:int}")]
    public async Task<IActionResult> AcademicClass(int classId, string? date, int? lesson, CancellationToken ct)
    {
        var day = await daily.AcademicClassAsync(classId, Day(date), lesson, ct);
        return day is null ? NotFound() : View("AcademicClass", day);
    }

    [HttpPost("academic/mark"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AcademicMark(int sessionId, string? date, int studentId, string? item, ItemStatus status, CancellationToken ct) =>
        await AcademicJson(await daily.SetAcademicAsync(sessionId, Day(date), studentId, item ?? "", status, ct), sessionId, Day(date));

    [HttpPost("academic/all"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AcademicAll(int sessionId, string? date, int studentId, CancellationToken ct) =>
        await AcademicJson(await daily.AcademicAllAvailableAsync(sessionId, Day(date), studentId, ct), sessionId, Day(date));

    [HttpPost("academic/notes"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AcademicNotes(int sessionId, string? date, int studentId, string? notes, CancellationToken ct) =>
        await AcademicJson(await daily.SaveAcademicNotesAsync(sessionId, Day(date), studentId, notes, ct), sessionId, Day(date));

    private async Task<IActionResult> AcademicJson(AcademicMarkResult r, int sessionId, DateOnly date)
    {
        if (!r.Saved) return AjaxFailure(r.Message, 422);
        return Json(new
        {
            success = true, message = r.Message,
            row = r.Row is null ? null : await RenderAsync("_AcademicRow", new AcademicRowModel(r.Row, sessionId, date, true)),
            counts = Counts(r.Counts),
        });
    }

    [HttpGet("academic/history")]
    public async Task<IActionResult> AcademicHistory([FromQuery] BelongingsQuery query, CancellationToken ct)
    {
        var h = await history.AcademicAsync(query, ct);
        return View("AcademicHistory", new AcademicHistoryViewModel(h, await history.OptionsAsync(query, true, ct), query));
    }

    [HttpGet("academic/history/pdf")]
    public async Task<IActionResult> AcademicPdf([FromQuery] BelongingsQuery query, CancellationToken ct) =>
        await PdfFile(BelongingsReports.Academic(await history.AcademicAsync(query, ct)), ct);

    [HttpGet("academic/history/excel")]
    public async Task<IActionResult> AcademicExcel([FromQuery] BelongingsQuery query, CancellationToken ct)
    {
        var h = await history.AcademicAsync(query, ct);
        return Workbook(excel.BelongingsAcademic(h, (await settings.GetProfileAsync(ct)).SchoolName), "Academic Belongings Report", h.Scope);
    }

    [HttpGet("academic/records/{id:int}")]
    public async Task<IActionResult> AcademicRecord(int id, CancellationToken ct)
    {
        var r = await history.AcademicRecordAsync(id, ct);
        return r is null ? NotFoundResult("This record no longer exists.") : PageOrPartial("AcademicRecord", r);
    }

    // ================================================================== former addresses

    [HttpGet("calendar")]
    public IActionResult Calendar() => RedirectPermanent(AcademicCalendarController.Href);

    [HttpGet("quarters/{id:int}/edit")]
    public IActionResult EditQuarter(int id) => RedirectPermanent($"{AcademicCalendarController.Href}/quarters/{id}/edit");

    /// <summary>A form still open on an old page: forwarded with its method and fields (307/308).</summary>
    [HttpPost("years")]
    public IActionResult CreateYear() => RedirectPermanentPreserveMethod($"{AcademicCalendarController.Href}/years");

    [HttpPost("quarters/{id:int}/edit")]
    public IActionResult EditQuarterPost(int id) => RedirectPermanentPreserveMethod($"{AcademicCalendarController.Href}/quarters/{id}/edit");

    // ================================================================== helpers

    /// <summary>The date in the query (yyyy-mm-dd), or today in the school's time zone (Africa/Cairo).</summary>
    private static DateOnly Day(string? date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d) ? d : SchoolWeek.Today;

    private static object? Counts(DailyCounts? c) => c is null ? null : new
    {
        students = c.Students, complete = c.Complete, pending = c.Pending, percent = c.Percent,
        readiness = ReadinessCounts.Text(c.Readiness.Percent), state = DailyStates.Label(c.State),
    };

    private FileContentResult Workbook(byte[] bytes, string title, BelongingsScope scope) =>
        File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", PdfFileName.From([title, scope.Audience, scope.DatesLabel]).Replace(".pdf", ".xlsx"));

    /// <summary>A partial view as HTML, so a saved row is redrawn from the same markup as the page.</summary>
    private async Task<string> RenderAsync(string view, object model)
    {
        var engine = HttpContext.RequestServices.GetRequiredService<ICompositeViewEngine>();
        var found = engine.FindView(ControllerContext, view, isMainPage: false);
        if (!found.Success) throw new InvalidOperationException($"The view {view} was not found.");
        await using var writer = new StringWriter();
        var data = new ViewDataDictionary(ViewData) { Model = model };
        var context = new ViewContext(ControllerContext, found.View, data, TempData, writer, new HtmlHelperOptions());
        await found.View.RenderAsync(context);
        return writer.ToString();
    }
}
