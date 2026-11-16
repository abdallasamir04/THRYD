using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Students → Student Attendance → School Attendance (route /arrivals), in two areas:
/// <list type="bullet">
/// <item>Daily Attendance (default): today in school time, choose a class, click a student when they arrive — saved at once
/// with the server's time; Present at or before the late threshold, Late after it. Absent and corrections are separate actions.</item>
/// <item>Attendance History &amp; Reports (/arrivals/history): one student, a class, a grade or the whole school over a date
/// or a period, with PDF and Excel.</item>
/// </list>
/// </summary>
[Route("arrivals")]
public class ArrivalsController(IArrivalService arrivals, IAttendanceReportService reports, IClassService classes, IExcelExportService excel,
    ISettingsService settings) : AppController
{
    // ------------------------------------------------------------------ Daily Attendance

    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, int? classId, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var options = await classes.AllOptionsAsync(ct);
        // "all" = every class; no class chosen yet = the class list only (the register opens on a class).
        var all = Request.Query["classId"] == "all";
        if (classId is { } id && options.All(c => c.Id != id)) classId = null;
        var model = new DailyAttendanceViewModel
        {
            Date = day,
            Classes = options,
            ClassId = classId,
            AllClasses = all,
            Checklist = classId is not null || all ? await arrivals.ChecklistAsync(day, classId, ct) : null,
            Day = await arrivals.DayAsync(day, ct),
            Rules = await arrivals.RulesAsync(ct),
        };
        return IsAjax ? PartialView("_Daily", model) : View(model);
    }

    /// <summary>One click: the student arrived now. JSON with the student's row and the class's counts.</summary>
    [HttpPost("register"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(int studentId, string? date, int? classId, CancellationToken ct) =>
        Action(await arrivals.RegisterArrivalAsync(studentId, SchoolWeek.ParseDayOrToday(date), classId, ct));

    /// <summary>Explicit absence (never automatic, never by clicking a name).</summary>
    [HttpPost("absent"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Absent(int studentId, string? date, int? classId, CancellationToken ct) =>
        Action(await arrivals.MarkAbsentAsync(studentId, SchoolWeek.ParseDayOrToday(date), classId, ct));

    private IActionResult Action(ArrivalActionResult r)
    {
        object? Row(ArrivalRow? x) => x is null ? null : new
        {
            recordId = x.RecordId, studentId = x.StudentId, status = Arrivals.Label(x.Outcome), slug = Slug(x.Outcome), time = Arrivals.Time(x.Time),
        };
        object? Counts(ArrivalSummary? s) => s is null ? null : new { total = s.Students, present = s.Present, late = s.Late, absent = s.Absent, notRecorded = s.NotRecorded };
        if (!IsAjax)
        {
            Toast(r.Message, r.Created ? "success" : r.Row is null ? "error" : "info");
            return RedirectBack();
        }
        if (r.Closed)
        {
            // Registration closed at the cutoff: refused on the server whatever the page shows; the page reloads the register.
            Response.StatusCode = 409;
            return Json(new { success = false, closed = true, message = r.Message, row = Row(r.Row), counts = Counts(r.Summary) });
        }
        if (r.Row is null) return AjaxFailure(r.Message, 409);
        Response.StatusCode = r.Created ? 200 : 409;
        return Json(new { success = r.Created, duplicate = !r.Created, message = r.Message, row = Row(r.Row), counts = Counts(r.Summary) });
    }

    public static string Slug(ArrivalOutcome? o) => o switch
    {
        ArrivalOutcome.Present => "present", ArrivalOutcome.Late => "late", ArrivalOutcome.Absent => "absent", _ => "notrecorded",
    };

    /// <summary>
    /// A past day with no record (before automatic absences started): record a missed arrival with its time (before the cutoff;
    /// the outcome is worked out from the time). Today's arrivals are only the one click, before the cutoff.
    /// </summary>
    [HttpGet("add")]
    public async Task<IActionResult> Add(int studentId, string? date, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        if (AddRefused(day, await RowAsync(studentId, day, ct)) is { } refused) return NotFoundResult(refused);
        return PageOrPartial("Add", new ArrivalAddModel
        {
            StudentId = studentId, Date = SchoolWeek.Iso(day), Row = await RowAsync(studentId, day, ct), Rules = await arrivals.RulesAsync(ct),
        });
    }

    private async Task<ArrivalRow?> RowAsync(int studentId, DateOnly day, CancellationToken ct) =>
        (await arrivals.ChecklistAsync(day, null, ct)).Rows.FirstOrDefault(r => r.StudentId == studentId);

    private static string? AddRefused(DateOnly day, ArrivalRow? row) =>
        day >= SchoolWeek.Today ? "Today's arrivals are recorded with one click on Daily Attendance, before the cutoff."
        : row is null ? "This student is not in the register on that day."
        : row.Status is not null ? $"This student is already {Arrivals.Label(row.Outcome)} on that day. Use Correct (with a reason) to change it."
        : null;

    [HttpPost("add"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(ArrivalAddModel model, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(model.Date);
        if (AddRefused(day, await RowAsync(model.StudentId, day, ct)) is { } refused)
        {
            if (IsAjax) return AjaxFailure(refused, 409);
            Toast(refused, "error");
            return LocalRedirect($"/arrivals?date={SchoolWeek.Iso(day)}");
        }
        var time = ArrivalForm.ParseTime(model.Time);
        if (time is null) ModelState.AddModelError(nameof(model.Time), "Enter the arrival time.");
        if (ModelState.IsValid)
        {
            var result = (await arrivals.RecordAsync(day, [new ArrivalEntry(model.StudentId, ArrivalStatus.Present, time, null, null, null, false, model.Notes)], ct)).Single();
            if (result.Saved) return Saved("Arrival recorded.", $"/arrivals?date={SchoolWeek.Iso(day)}");
            ModelState.AddModelError("", result.Error!);
        }
        model.Row = await RowAsync(model.StudentId, day, ct);
        model.Rules = await arrivals.RulesAsync(ct);
        return FormView("Add", model);
    }

    [HttpGet("{id:int}/correct")]
    public async Task<IActionResult> Correct(int id, CancellationToken ct)
    {
        var record = await arrivals.GetAsync(id, ct);
        if (record is null) return NotFoundResult("This arrival record no longer exists.");
        return PageOrPartial("Correct", new ArrivalCorrectionModel
        {
            Id = id, Outcome = record.Outcome ?? ArrivalOutcome.Present, Time = record.Time?.ToString("HH:mm"), Notes = record.Notes, Record = record,
            Rules = await arrivals.RulesAsync(ct),
        });
    }

    [HttpPost("{id:int}/correct"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Correct(int id, ArrivalCorrectionModel model, CancellationToken ct)
    {
        model.Id = id;
        var record = await arrivals.GetAsync(id, ct);
        if (record is null) return NotFoundResult("This arrival record no longer exists.");
        if (ModelState.IsValid)
        {
            var result = await arrivals.CorrectAsync(id, model.Outcome, ArrivalForm.ParseTime(model.Time), model.Notes, model.Reason, ct);
            if (result.Succeeded) return Saved($"Attendance of {record.StudentName} corrected.", $"/arrivals?date={SchoolWeek.Iso(record.Date)}&classId={record.ClassId}");
            AddErrors(result);
        }
        model.Record = record;
        model.Rules = await arrivals.RulesAsync(ct);
        return FormView("Correct", model);
    }

    /// <summary>The day's register as a PDF (class, status filter).</summary>
    [HttpGet("pdf")]
    public async Task<IActionResult> Pdf(string? date, int? classId, string? show, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var className = classId is { } id ? (await classes.AllOptionsAsync(ct)).FirstOrDefault(c => c.Id == id)?.Name : null;
        var tab = show is "late" or "absent" or "present" or "notrecorded" ? show : "all";
        return await PdfFile(PdfReports.Attendance(await arrivals.ChecklistAsync(day, classId, ct), className, tab), ct);
    }

    // ------------------------------------------------------------------ Attendance History & Reports

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] AttendanceHistoryQuery q, CancellationToken ct)
    {
        var (from, to) = Range(q);
        var model = new AttendanceHistoryViewModel
        {
            Query = q, From = from, To = to,
            Students = await reports.StudentsAsync(ct),
            Classes = await classes.AllOptionsAsync(ct),
        };
        if (q.Mode == "student")
        {
            if (q.StudentId is { } sid) model.Student = await reports.StudentAsync(sid, from, to, ct);
        }
        else model.Group = await reports.GroupAsync(q.Mode == "class" ? q.ClassId : null, q.Mode == "grade" ? Blank(q.Grade) : null, from, to, Blank(q.Status), ct);
        return View(model);
    }

    [HttpGet("history/pdf")]
    public async Task<IActionResult> HistoryPdf([FromQuery] AttendanceHistoryQuery q, CancellationToken ct)
    {
        var (from, to) = Range(q);
        if (q.Mode == "student")
        {
            var r = q.StudentId is { } sid ? await reports.StudentAsync(sid, from, to, ct) : null;
            return r is null ? NotFound() : await PdfFile(AttendanceReports.Student(r), ct);
        }
        return await PdfFile(AttendanceReports.Group(await reports.GroupAsync(q.Mode == "class" ? q.ClassId : null, q.Mode == "grade" ? Blank(q.Grade) : null, from, to, Blank(q.Status), ct)), ct);
    }

    [HttpGet("history/excel")]
    public async Task<IActionResult> HistoryExcel([FromQuery] AttendanceHistoryQuery q, CancellationToken ct)
    {
        var (from, to) = Range(q);
        var school = (await settings.GetProfileAsync(ct)).SchoolName;
        if (q.Mode == "student")
        {
            var r = q.StudentId is { } sid ? await reports.StudentAsync(sid, from, to, ct) : null;
            if (r is null) return NotFound();
            return File(excel.AttendanceStudent(r, school), StudentsController.ExcelContentType, PdfFileName.From(["Attendance", r.Name, SchoolWeek.Iso(r.From), r.From == r.To ? null : SchoolWeek.Iso(r.To)]).Replace(".pdf", ".xlsx"));
        }
        var g = await reports.GroupAsync(q.Mode == "class" ? q.ClassId : null, q.Mode == "grade" ? Blank(q.Grade) : null, from, to, Blank(q.Status), ct);
        return File(excel.AttendanceGroup(g, school), StudentsController.ExcelContentType, PdfFileName.From(["Attendance", g.Title, SchoolWeek.Iso(g.From), g.From == g.To ? null : SchoolWeek.Iso(g.To)]).Replace(".pdf", ".xlsx"));
    }

    /// <summary>A single date, or a period (from / to); defaults: today for one date, the last 30 days for a period.</summary>
    private static (DateOnly From, DateOnly To) Range(AttendanceHistoryQuery q)
    {
        var today = SchoolWeek.Today;
        if (q.Period != "range") { var d = Date(q.Date) ?? today; return (d, d); }
        var to = Date(q.To) ?? today;
        var from = Date(q.From) ?? to.AddDays(-29);
        return from <= to ? (from, to) : (to, from);
    }

    private static DateOnly? Date(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ------------------------------------------------------------------ helpers

    private IActionResult Saved(string message, string redirect)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return LocalRedirect(redirect);
    }

    private IActionResult RedirectBack()
    {
        var referer = Request.Headers.Referer.ToString();
        return Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == Request.Host.Host && uri.AbsolutePath.StartsWith("/arrivals", StringComparison.Ordinal)
            ? LocalRedirect(uri.PathAndQuery) : LocalRedirect("/arrivals");
    }
}
