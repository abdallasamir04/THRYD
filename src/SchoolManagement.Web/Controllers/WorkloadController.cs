using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Teacher workload (24 periods per school week). Every save goes through IWorkloadService, which
/// enforces the limit in a locked transaction; nothing here trusts values computed in the browser.
/// </summary>
[Route("workload")]
public class WorkloadController(
    IWorkloadService workload,
    ITeacherService teachers,
    IClassService classes,
    IExcelExportService excel) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] WorkloadOverviewQuery query, CancellationToken ct)
    {
        var week = SchoolWeek.ParseOrCurrent(query.Week);
        var model = new WorkloadOverviewViewModel
        {
            Week = week,
            Query = query,
            Rows = await workload.OverviewAsync(week, query, ct),
            AllRows = await workload.OverviewAsync(week, new WorkloadOverviewQuery(), ct),
            JobTitles = await teachers.JobTitlesAsync(ct),
            Departments = await teachers.DepartmentsAsync(ct),
            MaxWeeklyPeriods = await workload.MaxAsync(ct),
        };
        return IsAjax ? PartialView("_Table", model) : View(model);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(string? week, CancellationToken ct)
    {
        var start = SchoolWeek.ParseOrCurrent(week);
        var bytes = excel.WorkloadReport(start,
            await workload.OverviewAsync(start, new WorkloadOverviewQuery { Sort = "name" }, ct),
            await workload.WeekAssignmentsAsync(start, ct));
        return File(bytes, StudentsController.ExcelContentType, $"teacher-workload-{SchoolWeek.Iso(start)}.xlsx");
    }

    [HttpGet("teacher/{id:int}")]
    public async Task<IActionResult> Teacher(int id, string? week, CancellationToken ct)
    {
        var teacher = await teachers.GetAsync(id, ct);
        if (teacher is null) return NotFound();
        var start = SchoolWeek.ParseOrCurrent(week);
        var model = new TeacherWorkloadViewModel
        {
            Teacher = teacher,
            Summary = (await workload.SummaryAsync(id, start, ct))!,
            Assignments = await workload.AssignmentsAsync(id, start, ct),
            History = await workload.HistoryAsync(id, 100, ct),
        };
        return View(model);
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create(int? teacherId, string? week, WorkloadCategory? category, int? dutyId, CancellationToken ct)
    {
        var model = new WorkloadFormModel
        {
            TeacherId = teacherId,
            Week = SchoolWeek.ParseOrCurrent(week),
            Category = category is { } c && Enum.IsDefined(c) ? c : WorkloadCategory.Class,
            TeacherLocked = teacherId is not null,
        };
        // "Count in workload" from a staff profile: Duty periods linked to that duty, in the duty's week.
        if (dutyId is not null)
        {
            model.Category = WorkloadCategory.Duty;
            model.DutyAssignmentId = dutyId;
            model.Periods ??= 1;
        }
        await FillAsync(model, ct);
        return PageOrPartial("Form", model);
    }

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(WorkloadFormModel model, CancellationToken ct)
    {
        model.Id = null;
        if (ModelState.IsValid)
        {
            var result = await workload.CreateAsync(model.ToInput(), ct);
            if (result.Succeeded) return await Saved(model, "added", ct);
            AddErrors(result);
        }
        await FillAsync(model, ct);
        return FormView("Form", model);
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var row = await workload.GetAsync(id, ct);
        if (row is null) return NotFoundResult("This assignment no longer exists.");
        var model = new WorkloadFormModel
        {
            Id = row.Id,
            TeacherId = row.TeacherId,
            Week = row.WeekStart,
            Category = row.Category,
            Periods = row.Periods,
            SchoolClassId = row.SchoolClassId,
            DutyAssignmentId = row.DutyAssignmentId,
            Description = row.Description,
            TeacherLocked = true,
            Day = row.Day,
            TimeSlotId = row.TimeSlotId,
        };
        await FillAsync(model, ct, row);
        return PageOrPartial("Form", model);
    }

    [HttpPost("{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, WorkloadFormModel model, CancellationToken ct)
    {
        model.Id = id;
        var row = await workload.GetAsync(id, ct);
        if (row is null) return NotFoundResult("This assignment no longer exists.");
        if (ModelState.IsValid)
        {
            var result = await workload.UpdateAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return await Saved(model, "updated", ct);
            AddErrors(result);
        }
        model.TeacherLocked = true;
        await FillAsync(model, ct, row);
        return FormView("Form", model);
    }

    [HttpPost("{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var row = await workload.GetAsync(id, ct);
        var result = await workload.DeleteAsync(id, ct);
        var message = result.Succeeded ? await TotalMessage(row!.TeacherId, row.WeekStart, $"Removed {row.Periods} {row.Category} period(s)", ct) : result.Errors[0].Value;
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message) : AjaxFailure(message, 404);
        Toast(message, result.Succeeded ? "success" : "error");
        return row is null ? RedirectToAction(nameof(Index)) : Redirect($"/workload/teacher/{row.TeacherId}?week={SchoolWeek.Iso(row.WeekStart)}");
    }

    /// <summary>Live capacity shown in the form while choosing a teacher, week and periods (display only; the save re-checks).</summary>
    [HttpGet("capacity")]
    public async Task<IActionResult> Capacity(int teacherId, string? week, int? excludeId, CancellationToken ct)
    {
        var start = SchoolWeek.ParseOrCurrent(week);
        var summary = await workload.SummaryAsync(teacherId, start, ct);
        if (summary is null) return NotFound();
        var excluded = excludeId is { } ex ? (await workload.GetAsync(ex, ct)) : null;
        var own = excluded is not null && excluded.TeacherId == teacherId && excluded.WeekStart == start ? excluded.Periods : 0;
        var total = summary.Total - own;
        var max = summary.Max; // the configured maximum (Settings), not a fixed 24
        return Json(new
        {
            total,
            max,
            remaining = Math.Max(0, max - total),
            status = WorkloadRules.Tone(WorkloadRules.StatusOf(total, max)),
            statusLabel = WorkloadRules.Label(WorkloadRules.StatusOf(total, max)),
            week = SchoolWeek.Iso(start),
            weekLabel = SchoolWeek.Label(start),
            breakdown = new { summary.Breakdown.Class, summary.Breakdown.Substitution, summary.Breakdown.Duty, summary.Breakdown.Office },
        });
    }

    [HttpGet("duty-options")]
    public async Task<IActionResult> DutyOptions(int teacherId, string? week, int? includeDutyId, CancellationToken ct) =>
        Json(await workload.DutyOptionsAsync(teacherId, SchoolWeek.ParseOrCurrent(week), includeDutyId, ct));

    private async Task<IActionResult> Saved(WorkloadFormModel model, string verb, CancellationToken ct)
    {
        var week = SchoolWeek.StartOf(model.Week!.Value);
        var message = await TotalMessage(model.TeacherId!.Value, week, $"{model.Periods} {model.Category} period(s) {verb}", ct);
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return Redirect($"/workload/teacher/{model.TeacherId}?week={SchoolWeek.Iso(week)}");
    }

    private async Task<string> TotalMessage(int teacherId, DateOnly week, string prefix, CancellationToken ct)
    {
        var s = await workload.SummaryAsync(teacherId, week, ct);
        if (s is null) return prefix + ".";
        var note = s.Status switch
        {
            WorkloadStatus.Full => " Teacher is now FULL.",
            WorkloadStatus.NearCapacity => $" Approaching the maximum: {s.Remaining} left.",
            _ => "",
        };
        return $"{prefix}. {s.TeacherName}: {s.Total}/{s.Max} periods.{note}";
    }

    private async Task FillAsync(WorkloadFormModel model, CancellationToken ct, WorkloadAssignmentRow? current = null)
    {
        model.Teachers = await teachers.ListAllAsync(new TeacherListQuery(), ct);
        var all = await classes.AllOptionsAsync(ct);
        model.Classes = all.Where(c => c.IsActive || c.Id == current?.SchoolClassId).ToList();
        var week = SchoolWeek.StartOf(model.Week ?? SchoolWeek.Current);
        if (model.TeacherId is { } teacherId)
        {
            model.Duties = await workload.DutyOptionsAsync(teacherId, week, current?.DutyAssignmentId, ct);
            model.Summary = await workload.SummaryAsync(teacherId, week, ct);
        }
        model.CurrentPeriods = current is not null && current.TeacherId == model.TeacherId && current.WeekStart == week ? current.Periods : 0;
    }
}
