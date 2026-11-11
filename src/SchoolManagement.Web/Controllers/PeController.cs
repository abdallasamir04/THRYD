using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// P.E. timetable (sidebar → P.E.). Sessions are the P.E. department's normal timetable sessions:
/// created with ITimetableService (conflicts, one class per slot, workload), changed and removed with
/// the existing timetable forms. This controller adds the P.E. grid and the session details.
/// </summary>
[Route("pe")]
public class PeController(IPeService pe, ITimetableService timetable, ITeacherService teachers, IClassService classes) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, CancellationToken ct)
    {
        var day = SchoolWeek.ParseDayOrToday(date);
        var week = SchoolWeek.StartOf(day);
        var dept = await pe.DepartmentIdAsync(ct);
        return View(new PePageViewModel
        {
            Date = day, Week = week, DepartmentId = dept,
            Slots = (await timetable.SlotsAsync(ct)).Where(s => s.IsActive).ToList(),
            Sessions = await pe.WeekAsync(week, ct),
            PeTeachers = dept is null ? 0 : (await teachers.ListAllAsync(new TeacherListQuery { DepartmentId = dept }, ct)).Count,
        });
    }

    [HttpGet("new")]
    public async Task<IActionResult> New(DayOfWeek day, int slotId, string? date, CancellationToken ct)
    {
        var model = new PeNewModel { Day = day, SlotId = slotId, Date = date };
        if (!await FillAsync(model, ct)) return NotFoundResult("This slot does not exist.");
        return PageOrPartial("New", model);
    }

    [HttpPost("new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> New(PeNewModel model, CancellationToken ct)
    {
        if (!await FillAsync(model, ct)) return NotFoundResult("This slot does not exist.");
        if (ModelState.IsValid)
        {
            var dept = (await pe.DepartmentIdAsync(ct))!.Value;
            var subject = (await timetable.SubjectsAsync(dept, ct)).First();
            var result = await timetable.AssignAsync(new SessionInput(model.ClassId!.Value, model.Day, model.SlotId, dept, subject, model.TeacherId, null), ct);
            if (result.Succeeded)
                return Saved($"P.E. session added for {model.Classes.First(c => c.Id == model.ClassId).Name}, {ScheduleRules.DayName(model.Day)} {model.Slot!.Label}.", model.Date);
            AddErrors(result);
        }
        return FormView("New", model);
    }

    [HttpGet("sessions/{id:int}")]
    public async Task<IActionResult> Details(int id, string? date, CancellationToken ct)
    {
        var view = await pe.GetAsync(id, ct);
        if (view is null) return NotFoundResult("This P.E. session no longer exists.");
        var model = new PeDetailsModel
        {
            SessionId = id, Date = date, CoTeacherId = view.CoTeacherId, Facility = view.Facility, Activity = view.Activity, Notes = view.Notes,
            Equipment = view.Equipment.Select((e, i) => (e, i)).ToDictionary(x => $"k{x.i}", x => new PeEquipmentField { Name = x.e.Name, Checked = x.e.IsChecked }),
        };
        await FillAsync(model, view, ct);
        return PageOrPartial("Details", model);
    }

    [HttpPost("sessions/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Details(int id, PeDetailsModel model, CancellationToken ct)
    {
        var view = await pe.GetAsync(id, ct);
        if (view is null) return NotFoundResult("This P.E. session no longer exists.");
        model.SessionId = id;
        if (ModelState.IsValid)
        {
            var result = await pe.SaveDetailsAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return Saved($"P.E. session details saved for {view.Session.ClassName}.", model.Date);
            AddErrors(result);
        }
        await FillAsync(model, view, ct);
        return FormView("Details", model);
    }

    private async Task<bool> FillAsync(PeNewModel model, CancellationToken ct)
    {
        model.Slot = await timetable.GetSlotAsync(model.SlotId, ct);
        var dept = await pe.DepartmentIdAsync(ct);
        if (model.Slot is null || !model.Slot.IsActive || !ScheduleRules.IsSchoolDay(model.Day) || dept is null) return false;
        // Classes that are free in this day + period (one subject per class slot).
        var busy = await pe.BusyClassIdsAsync(model.Day, model.SlotId, ct);
        model.Classes = (await classes.AllOptionsAsync(ct)).Where(c => c.IsActive && !busy.Contains(c.Id))
            .Select(c => (c.Id, c.Name, PeService.GradeOf(c.Name))).ToList();
        model.Teachers = await timetable.CandidatesAsync(dept.Value, null, model.Day, model.SlotId, null, null, ct);
        return true;
    }

    private async Task FillAsync(PeDetailsModel model, PeSessionView view, CancellationToken ct)
    {
        model.View = view;
        var dept = await pe.DepartmentIdAsync(ct);
        // Any academic teacher can co-teach; P.E. teachers are listed first.
        model.CoTeachers = (await teachers.ListAllAsync(new TeacherListQuery { Sort = "name" }, ct))
            .Where(t => t.Id != view.Session.TeacherId)
            .OrderByDescending(t => t.DepartmentId == dept).ThenBy(t => t.FullName)
            .Select(t => (t.Id, t.FullName, t.DepartmentName)).ToList();
    }

    private IActionResult Saved(string message, string? date)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return Redirect(string.IsNullOrEmpty(date) ? "/pe" : $"/pe?date={date}");
    }
}
