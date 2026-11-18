using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Pdf;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Staff → Staff Attendance: today's register of every active staff member (teachers and management), one-click
/// Present / Late / Absent, Record / Correct with a reason (history kept), and History &amp; Reports with PDF / Excel.
/// </summary>
[Route("staff/attendance")]
public class StaffAttendanceController(IStaffAttendanceService attendance, IExcelExportService excel, ISettingsService settings) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, string? department, string? category, string? status, string? search, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        if (day > SchoolWeek.Today) day = SchoolWeek.Today;
        // Every expected row is sent; search, department and status filter in place (and from these query values on load).
        ViewData["Department"] = department;
        ViewData["Category"] = StaffCategories.Parse(category) is { } c ? StaffCategories.Slug(c) : null;
        ViewData["Status"] = status is "present" or "late" or "absent" or "none" ? status : null;
        ViewData["Search"] = search;
        return View(await attendance.DayAsync(day, new StaffAttendanceFilter(), ct));
    }

    /// <summary>
    /// One click today, until the cutoff: Present / Late at the server's school time, or Absent. JSON: the row and the day's
    /// counts; after the cutoff 409 with closed = true (the page reloads and shows the day closed).
    /// </summary>
    [HttpPost("mark"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Mark(int staffId, string? date, StaffAttendanceStatus status, CancellationToken ct)
    {
        var r = await attendance.MarkAsync(staffId, SchoolWeek.ParseDayOrToday(date), status, ct);
        if (!IsAjax)
        {
            Toast(r.Message, r.Saved ? "success" : "error");
            return LocalRedirect("/staff/attendance" + (date is null ? "" : $"?date={Uri.EscapeDataString(date)}"));
        }
        if (r.Closed)
        {
            Response.StatusCode = 409;
            return Json(new { success = false, closed = true, message = r.Message });
        }
        if (!r.Saved) return AjaxFailure(r.Message, 409);
        return Json(new { success = true, message = r.Message, row = Row(r.Row!), counts = Counts(r.Counts!) });
    }

    public static object Row(StaffAttendanceRow r) => new
    {
        staffId = r.StaffId, recordId = r.RecordId, status = StaffAttendanceStatuses.Label(r.Status), slug = StaffAttendanceStatuses.Slug(r.Status),
        time = r.ArrivalTime is { } t ? Arrivals.Time(t) : null, by = r.RecordedBy,
    };

    public static object Counts(StaffAttendanceCounts c) => new
    {
        expected = c.Expected, present = c.Present, late = c.Late, absent = c.Absent, none = c.NotRecorded,
    };

    /// <summary>Correct a saved record (reason required, history kept). Attendance itself is only recorded with one click.</summary>
    [HttpGet("record")]
    public async Task<IActionResult> Record(int staffId, string? date, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var row = await attendance.RowAsync(staffId, day, ct);
        if (row is null) return NotFoundResult("This staff member no longer exists.");
        if (row.RecordId is null) return IsAjax ? AjaxFailure($"{row.Name} has no saved record on this day, so there is nothing to correct.", 409) : NotFound();
        return PageOrPartial("Record", new StaffAttendanceForm
        {
            StaffId = staffId, Date = SchoolWeek.Iso(day), Row = row,
            Status = row.Status, Arrival = row.ArrivalTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            ExpectedStart = row.ExpectedStart ?? await settings.GetArrivalDeadlineAsync(ct),
        });
    }

    [HttpPost("record"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Record(StaffAttendanceForm form, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(form.Date);
        form.Row = await attendance.RowAsync(form.StaffId, day, ct);
        if (form.Row is null) return NotFoundResult("This staff member no longer exists.");
        form.ExpectedStart = form.Row.ExpectedStart ?? await settings.GetArrivalDeadlineAsync(ct);
        TimeOnly? arrival = null;
        if (form.Status is null) ModelState.AddModelError(nameof(form.Status), "Choose Present, Late or Absent.");
        if (form.Status is not StaffAttendanceStatus.Absent && !string.IsNullOrWhiteSpace(form.Arrival))
        {
            if (TimeOnly.TryParseExact(form.Arrival, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) arrival = t;
            else ModelState.AddModelError(nameof(form.Arrival), "Enter a valid time.");
        }
        if (ModelState.IsValid)
        {
            var r = await attendance.CorrectAsync(form.StaffId, day, form.Status!.Value, arrival, form.Reason, ct);
            if (r.Saved)
            {
                if (IsAjax) return AjaxSuccess(r.Message);
                Toast(r.Message);
                return LocalRedirect($"/staff/attendance?date={SchoolWeek.Iso(day)}");
            }
            ModelState.AddModelError(r.Message.StartsWith("Give a reason", StringComparison.Ordinal) ? nameof(form.Reason) : "", r.Message);
        }
        return FormView("Record", form);
    }

    // ------------------------------------------------------------------ history & reports

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] StaffAttendanceQuery query, CancellationToken ct)
    {
        var h = await attendance.HistoryAsync(query, ct);
        return View(new StaffAttendanceHistoryPage(query, h, Scope(query, h)));
    }

    [HttpGet("history/pdf")]
    public async Task<IActionResult> HistoryPdf([FromQuery] StaffAttendanceQuery query, CancellationToken ct)
    {
        var h = await attendance.HistoryAsync(query, ct);
        return await PdfFile(StaffAttendanceReports.History(h, Scope(query, h)), ct);
    }

    [HttpGet("history/excel")]
    public async Task<IActionResult> HistoryExcel([FromQuery] StaffAttendanceQuery query, CancellationToken ct)
    {
        var h = await attendance.HistoryAsync(query, ct);
        var scope = Scope(query, h);
        var bytes = excel.StaffAttendance(h, scope, (await settings.GetProfileAsync(ct)).SchoolName);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            PdfFileName.From(["Staff Attendance Report", scope, StaffAttendanceReports.Dates(h.From, h.To)]).Replace(".pdf", ".xlsx"));
    }

    /// <summary>The staff member the report covers, or "All staff" narrowed by category and department.</summary>
    public static string Scope(StaffAttendanceQuery q, StaffAttendanceHistory h)
    {
        if (h.Staff.FirstOrDefault(s => s.Id == q.StaffId) is { Id: > 0 } s) return s.Name;
        var parts = new List<string>();
        if (StaffCategories.Parse(q.Category) is { } c) parts.Add(StaffCategories.Name(c));
        if (h.Departments.FirstOrDefault(d => d.Value == q.Department) is { Value: not null } d) parts.Add(d.Label);
        return parts.Count == 0 ? "All staff" : string.Join(" · ", parts);
    }
}

public sealed class StaffAttendanceForm
{
    public int StaffId { get; set; }
    public string? Date { get; set; }

    [Display(Name = "Status")]
    public StaffAttendanceStatus? Status { get; set; }

    /// <summary>"HH:mm" — the time the staff member actually arrived (Present / Late).</summary>
    [Display(Name = "Arrival time")]
    public string? Arrival { get; set; }

    [Display(Name = "Reason")]
    [StringLength(StaffAttendanceCorrection.ReasonMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Reason { get; set; }

    public StaffAttendanceRow? Row { get; set; }
    public TimeOnly? ExpectedStart { get; set; }
    public bool IsCorrection => Row?.RecordId is not null;
}

public sealed record StaffAttendanceHistoryPage(StaffAttendanceQuery Query, StaffAttendanceHistory History, string Scope);
