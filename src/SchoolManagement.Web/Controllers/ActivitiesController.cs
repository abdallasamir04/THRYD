using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Duties → Activities. The page shows the built-in P.E. card (the existing P.E. module at /pe, unchanged), the Add
/// Activity card (always) and one card per administrator-created activity, each with its own management page.
/// </summary>
[Route("duties/activities")]
public class ActivitiesController(IActivityService activities, IPeService pe, ITeacherService teachers, IDutyService duties, IClassService classes) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, string? show, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var peWeek = await pe.WeekAsync(SchoolWeek.StartOf(day), ct);
        var all = show == "all";
        return View(new ActivitiesPageViewModel
        {
            Date = day,
            PeSessionsThisWeek = peWeek.Count,
            PeSessionsToday = ScheduleRules.IsSchoolDay(day.DayOfWeek) ? peWeek.Count(s => s.Day == day.DayOfWeek) : 0,
            Sessions = await activities.SessionsOnAsync(day, ct),
            Activities = await activities.ListAsync(all, ct),
            InactiveCount = await activities.InactiveCountAsync(ct),
            ShowInactive = all,
        });
    }

    /// <summary>Records the coverage of one P.E. lesson or activity meeting scheduled on a date.</summary>
    [HttpPost("sessions/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SessionStatus(ActivitySessionKind kind, int refId, string? date, ActivitySessionStatus status, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var result = await activities.SetSessionStatusAsync(kind, refId, day, status, ct);
        Toast(result.Succeeded ? $"Session marked {ActivitySessionStatuses.Label(status)}." : result.Errors[0].Value, result.Succeeded ? "success" : "error");
        return Redirect($"/duties/activities?date={SchoolWeek.Iso(day)}#sessions");
    }

    // ------------------------------------------------------------------ add

    [HttpGet("new")]
    public async Task<IActionResult> New(CancellationToken ct) => View("Form", await FillAsync(new ActivityFormModel(), ct));

    [HttpPost("new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> New(ActivityFormModel model, CancellationToken ct)
    {
        model.Id = null;
        if (ModelState.IsValid)
        {
            var result = await activities.CreateAsync(model.ToInput(), ct);
            if (result.Succeeded)
            {
                Toast($"Activity “{model.Name?.Trim()}” created.");
                return Redirect("/duties/activities");
            }
            AddErrors(result);
        }
        return FormView("Form", await FillAsync(model, ct));
    }

    // ------------------------------------------------------------------ manage

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var a = await activities.GetAsync(id, ct);
        return a is null ? NotFound() : View(new ActivityDetailsViewModel { Activity = a });
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var a = await activities.GetAsync(id, ct);
        if (a is null) return NotFound();
        return View("Form", await FillAsync(new ActivityFormModel
        {
            Id = id, Name = a.Name, Description = a.Description, Category = a.Category, Icon = a.Icon, Color = a.Color, DepartmentId = a.DepartmentId,
            Location = a.Location, IsActive = a.IsActive, StaffIds = a.Staff.Select(s => s.Id).ToList(), ClassIds = a.Classes.Select(c => c.Id).ToList(),
        }, ct));
    }

    [HttpPost("{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ActivityFormModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await activities.UpdateAsync(id, model.ToInput(), ct);
            if (result.Succeeded)
            {
                Toast($"{model.Name?.Trim()} saved.");
                return Redirect($"/duties/activities/{id}");
            }
            AddErrors(result);
        }
        return FormView("Form", await FillAsync(model, ct));
    }

    [HttpPost("{id:int}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(int id, bool active, CancellationToken ct)
    {
        var result = await activities.SetActiveAsync(id, active, ct);
        Toast(result.Succeeded ? (active ? "Activity activated." : "Activity deactivated. Its staff, classes, schedule and history are kept.") : result.Errors[0].Value,
            result.Succeeded ? "success" : "error");
        return Redirect($"/duties/activities/{id}");
    }

    [HttpPost("{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await activities.DeleteAsync(id, ct);
        if (!result.Succeeded)
        {
            Toast(result.Errors[0].Value, "error");
            return Redirect($"/duties/activities/{id}");
        }
        Toast("Activity deleted.");
        return Redirect("/duties/activities");
    }

    // ------------------------------------------------------------------ schedule

    [HttpPost("{id:int}/schedule"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMeeting(int id, [Bind(Prefix = "Meeting")] ActivityScheduleFormModel meeting, CancellationToken ct)
    {
        var a = await activities.GetAsync(id, ct);
        if (a is null) return NotFound();
        if (ModelState.IsValid)
        {
            var result = await activities.AddScheduleAsync(id, meeting.ToInput(), ct);
            if (result.Succeeded)
            {
                Toast($"Meeting added: {ScheduleRules.DayName(meeting.Day!.Value)} {ScheduleRules.Times(meeting.Start, meeting.End)}.");
                return Redirect($"/duties/activities/{id}#schedule");
            }
            foreach (var (field, message) in result.Errors) ModelState.AddModelError(field.Length == 0 ? "" : $"Meeting.{field}", message);
        }
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return View("Details", new ActivityDetailsViewModel { Activity = (await activities.GetAsync(id, ct))!, Meeting = meeting });
    }

    [HttpPost("{id:int}/schedule/{scheduleId:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMeeting(int id, int scheduleId, CancellationToken ct)
    {
        var result = await activities.RemoveScheduleAsync(id, scheduleId, ct);
        Toast(result.Succeeded ? "Meeting removed from the schedule." : result.Errors[0].Value, result.Succeeded ? "success" : "error");
        return Redirect($"/duties/activities/{id}#schedule");
    }

    private async Task<ActivityFormModel> FillAsync(ActivityFormModel model, CancellationToken ct)
    {
        model.Departments = await teachers.DepartmentsAsync(ct);
        model.Staff = await duties.StaffOptionsAsync(ct);
        model.Classes = (await classes.AllOptionsAsync(ct)).Where(c => c.IsActive || model.ClassIds.Contains(c.Id)).ToList();
        model.Categories = await activities.CategoriesAsync(ct);
        return model;
    }
}
