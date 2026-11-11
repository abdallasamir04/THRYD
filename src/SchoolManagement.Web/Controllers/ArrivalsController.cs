using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>Daily Operations → Student Arrivals: the fast daily checklist and the correction workflow.</summary>
[Route("arrivals")]
public class ArrivalsController(IArrivalService arrivals, IClassService classes, IDutyService duties, ICurrentUser currentUser) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, int? classId, string? show, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var model = new ArrivalChecklistViewModel
        {
            Checklist = await arrivals.ChecklistAsync(day, classId, ct),
            Classes = await classes.AllOptionsAsync(ct),
            Staff = await duties.StaffOptionsAsync(ct),
            Show = show is "missing" or "late" or "absent" or "present" ? show : "all",
            OperatorStaffId = currentUser.StaffId,
        };
        return IsAjax ? PartialView("_Checklist", model) : View(model);
    }

    /// <summary>Saves the rows entered in the checklist (new records only; existing ones go through Correct).</summary>
    [HttpPost("save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] ArrivalBatchRequest request, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", out var day))
            return AjaxFailure("Choose a valid date.");
        if (request.Entries.Count == 0) return AjaxFailure("Nothing to save: mark at least one student.");
        if (request.Entries.Count > 2000) return AjaxFailure("Save at most 2,000 rows at a time.");
        var results = await arrivals.RecordAsync(day, request.Entries.Select(e => e.ToEntry()).ToList(), ct);
        var saved = results.Count(r => r.Saved);
        return Json(new
        {
            success = saved > 0 && saved == results.Count,
            saved,
            failed = results.Count - saved,
            message = saved == results.Count ? $"{saved} arrival record(s) saved." : $"{saved} saved, {results.Count - saved} not saved — see the highlighted rows.",
            results = results.Select(r => new { r.StudentId, r.Saved, r.Error }),
        });
    }

    [HttpGet("{id:int}/correct")]
    public async Task<IActionResult> Correct(int id, CancellationToken ct)
    {
        var record = await arrivals.GetAsync(id, ct);
        if (record is null) return NotFoundResult("This arrival record no longer exists.");
        return PageOrPartial("Correct", new ArrivalCorrectionModel
        {
            Id = id, Status = record.Status, Time = record.Time?.ToString("HH:mm:ss"), Method = record.Method, Location = record.Location,
            ReceivedByStaffId = record.ReceivedById, ReceivingConfirmed = record.Confirmed, Notes = record.Notes,
            Record = record, Staff = await duties.StaffOptionsAsync(ct),
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
            var result = await arrivals.CorrectAsync(id, model.ToEntry(record.StudentId), model.Reason, ct);
            if (result.Succeeded)
            {
                var message = $"Arrival record of {record.StudentName} corrected.";
                if (IsAjax) return AjaxSuccess(message);
                Toast(message);
                return Redirect($"/arrivals?date={SchoolWeek.Iso(record.Date)}");
            }
            AddErrors(result);
        }
        model.Record = record;
        model.Staff = await duties.StaffOptionsAsync(ct);
        return FormView("Correct", model);
    }
}
