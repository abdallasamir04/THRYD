using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Academic Staff → Class Timetable / Teacher Timetable / Time Slots, and the Assign Period workflow.
/// Both timetables read the same session rows; every save goes through ITimetableService or
/// IWorkloadService, which enforce conflicts and the weekly maximum on the server.
/// </summary>
[Route("timetable")]
public class TimetableController(ITimetableService timetable, IWorkloadService workload, ITeacherService teachers, IClassService classes) : AppController
{
    [HttpGet("")]
    public IActionResult Index() => Redirect("/timetable/classes");

    // ------------------------------------------------------------------ class timetable

    [HttpGet("classes")]
    public async Task<IActionResult> Classes(CancellationToken ct) =>
        View(new ClassPickerViewModel { Classes = await timetable.ClassesAsync(ct), ActiveSlots = (await timetable.SlotsAsync(ct)).Count(s => s.IsActive) });

    [HttpGet("classes/{id:int}")]
    public async Task<IActionResult> Class(int id, string? week, CancellationToken ct)
    {
        var all = await timetable.ClassesAsync(ct);
        var cls = all.FirstOrDefault(c => c.Id == id);
        if (cls is null) return NotFound();
        var grid = await timetable.ClassGridAsync(id, SchoolWeek.ParseOrCurrent(week), ct);
        var model = new TimetablePageViewModel
        {
            Grid = grid, Mode = "class", EntityId = id, Name = cls.Name,
            Subtitle = $"{Ui.Plural(grid.Sessions.Count, "teaching session", "teaching sessions")} a week · {Ui.Plural(cls.Students, "student", "students")}{(cls.IsActive ? "" : " · inactive class")}",
            Others = all.Where(c => c.IsActive || c.Id == id).Select(c => (c.Id, c.Name, (string?)$"{c.Sessions} sessions")).ToList(),
        };
        return IsAjax ? PartialView("_Grid", model) : View("Timetable", model);
    }

    // ------------------------------------------------------------------ teacher timetable

    [HttpGet("teachers")]
    public async Task<IActionResult> Teachers(string? search, int? departmentId, CancellationToken ct) =>
        View(new TeacherPickerViewModel
        {
            Teachers = await timetable.TeachersAsync(search, departmentId, ct),
            Departments = await teachers.DepartmentsAsync(ct),
            Search = search,
            DepartmentId = departmentId,
        });

    [HttpGet("teachers/{id:int}")]
    public async Task<IActionResult> Teacher(int id, string? week, CancellationToken ct)
    {
        var t = await teachers.GetAsync(id, ct);
        if (t is null) return NotFound();
        var start = SchoolWeek.ParseOrCurrent(week);
        var model = new TimetablePageViewModel
        {
            Grid = await timetable.TeacherGridAsync(id, start, ct), Mode = "teacher", EntityId = id, Name = t.FullName,
            Subtitle = t.DepartmentName is null ? t.JobTitle : $"{t.DepartmentName}{(t.SubcategoryLabel.Length == 0 ? "" : " · " + t.SubcategoryLabel)}",
            Workload = await workload.SummaryAsync(id, start, ct),
            Others = (await timetable.TeachersAsync(null, t.DepartmentId, ct)).Select(x => (x.Id, x.Name, (string?)$"{x.Sessions} sessions")).ToList(),
        };
        return IsAjax ? PartialView("_Grid", model) : View("Timetable", model);
    }

    // ------------------------------------------------------------------ PDF downloads (same week and data as the pages)

    [HttpGet("classes/{id:int}/pdf")]
    public async Task<IActionResult> ClassPdf(int id, string? week, CancellationToken ct)
    {
        var cls = (await timetable.ClassesAsync(ct)).FirstOrDefault(c => c.Id == id);
        if (cls is null) return NotFound();
        var grid = await timetable.ClassGridAsync(id, SchoolWeek.ParseOrCurrent(week), ct);
        var details = $"{Ui.Plural(grid.Sessions.Count, "teaching session", "teaching sessions")} a week · {Ui.Plural(cls.Students, "student", "students")}{(cls.IsActive ? "" : " · inactive class")}";
        return await PdfFile(PdfReports.ClassTimetable(grid, cls.Name, details), ct);
    }

    [HttpGet("teachers/{id:int}/pdf")]
    public async Task<IActionResult> TeacherPdf(int id, string? week, CancellationToken ct)
    {
        var t = await teachers.GetAsync(id, ct);
        if (t is null) return NotFound();
        var start = SchoolWeek.ParseOrCurrent(week);
        var department = t.DepartmentName is null ? null : $"{t.DepartmentName}{(t.SubcategoryLabel.Length == 0 ? "" : " · " + t.SubcategoryLabel)}";
        return await PdfFile(PdfReports.TeacherTimetable(await timetable.TeacherGridAsync(id, start, ct), t.FullName, department,
            await workload.SummaryAsync(id, start, ct)), ct);
    }

    // ------------------------------------------------------------------ time slots

    [HttpGet("slots")]
    public async Task<IActionResult> Slots(CancellationToken ct)
    {
        var slots = await timetable.SlotsAsync(ct);
        var inUse = new Dictionary<int, int>();
        foreach (var s in slots) inUse[s.Id] = await timetable.SessionsInSlotAsync(s.Id, ct);
        return View(new SlotsViewModel { Slots = slots, InUse = inUse });
    }

    [HttpGet("slots/{id:int}/edit")]
    public async Task<IActionResult> EditSlot(int id, CancellationToken ct)
    {
        var s = await timetable.GetSlotAsync(id, ct);
        if (s is null) return NotFoundResult("This time slot no longer exists.");
        return PageOrPartial("SlotForm", new SlotFormModel
        {
            Id = s.Id, Number = s.Number, Name = s.Name, StartTime = s.Start, EndTime = s.End, IsActive = s.IsActive,
            InUse = await timetable.SessionsInSlotAsync(id, ct),
        });
    }

    [HttpPost("slots/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSlot(int id, SlotFormModel model, CancellationToken ct)
    {
        var s = await timetable.GetSlotAsync(id, ct);
        if (s is null) return NotFoundResult("This time slot no longer exists.");
        (model.Id, model.Number, model.InUse) = (id, s.Number, await timetable.SessionsInSlotAsync(id, ct));
        if (ModelState.IsValid)
        {
            var result = await timetable.UpdateSlotAsync(id, model.Name, model.StartTime, model.EndTime, model.IsActive, model.ConfirmAffected, ct);
            if (result.Succeeded) return Saved($"Period {s.Number} saved{(model.StartTime is { } st && model.EndTime is { } et ? $" ({ScheduleRules.Times(st, et)})" : "")}.", "/timetable/slots");
            AddErrors(result);
        }
        return FormView("SlotForm", model);
    }

    // ------------------------------------------------------------------ assign period (class timetable)

    /// <summary>A clicked class slot: the Assign Period form when empty, the session panel when occupied.</summary>
    [HttpGet("assign")]
    public async Task<IActionResult> Assign(int classId, DayOfWeek day, int slotId, string? week, CancellationToken ct)
    {
        var start = SchoolWeek.ParseOrCurrent(week);
        var grid = await timetable.ClassGridAsync(classId, start, ct);
        var slot = grid.Slots.FirstOrDefault(s => s.Id == slotId);
        if (slot is null || !ScheduleRules.IsSchoolDay(day)) return NotFoundResult("This slot does not exist.");
        if (grid.Session(day, slotId) is { } session)
            return PageOrPartial("SlotPanel", new SlotPanelModel
            {
                Session = session, Slot = slot, Week = start, Editable = grid.Editable,
                Substitutions = grid.PeriodsAt(day, slotId).Where(p => p.Category == WorkloadCategory.Substitution).ToList(),
            });
        if (!grid.Editable) return NotFoundResult("Past weeks are history and cannot be changed. Go to this week to assign periods.");
        var model = new AssignPeriodModel { ClassId = classId, Day = day, SlotId = slotId, Week = SchoolWeek.Iso(start) };
        if (!await FillAsync(model, ct)) return NotFoundResult("This class no longer exists.");
        return PageOrPartial("AssignPeriod", model);
    }

    [HttpPost("sessions"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSession(AssignPeriodModel model, CancellationToken ct)
    {
        model.SessionId = null;
        if (ModelState.IsValid)
        {
            var result = await timetable.AssignAsync(model.ToInput(), ct);
            if (result.Succeeded) return await SessionSaved(result.EntityId!.Value, "assigned", ct);
            AddErrors(result);
        }
        if (!await FillAsync(model, ct)) return NotFoundResult("This class no longer exists.");
        return FormView("AssignPeriod", model);
    }

    [HttpGet("sessions/{id:int}/edit")]
    public async Task<IActionResult> EditSession(int id, CancellationToken ct)
    {
        var s = await timetable.GetSessionAsync(id, ct);
        if (s is null || s.EndedOn is not null) return NotFoundResult("This session is no longer on the timetable.");
        var model = new AssignPeriodModel
        {
            SessionId = id, ClassId = s.ClassId, Day = s.Day, SlotId = s.SlotId, Week = SchoolWeek.Iso(SchoolWeek.Current),
            DepartmentId = s.DepartmentId, Subject = s.Subject, TeacherId = s.TeacherId, Notes = s.Notes,
        };
        await FillAsync(model, ct);
        return PageOrPartial("AssignPeriod", model);
    }

    [HttpPost("sessions/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSession(int id, AssignPeriodModel model, CancellationToken ct)
    {
        var s = await timetable.GetSessionAsync(id, ct);
        if (s is null || s.EndedOn is not null) return NotFoundResult("This session is no longer on the timetable.");
        (model.SessionId, model.ClassId, model.Day, model.SlotId) = (id, s.ClassId, s.Day, s.SlotId);
        if (ModelState.IsValid)
        {
            var result = await timetable.UpdateSessionAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return await SessionSaved(result.EntityId!.Value, "updated", ct);
            AddErrors(result);
        }
        await FillAsync(model, ct);
        return FormView("AssignPeriod", model);
    }

    [HttpPost("sessions/{id:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveSession(int id, CancellationToken ct)
    {
        var s = await timetable.GetSessionAsync(id, ct);
        var result = await timetable.RemoveSessionAsync(id, ct);
        if (!result.Succeeded) return Failure(result.Errors[0].Value);
        return Saved($"{s!.Subject} with {s.TeacherName} removed from {s.ClassName} ({ScheduleRules.DayName(s.Day)}). Workload updated from this week.", $"/timetable/classes/{s.ClassId}");
    }

    /// <summary>Teachers for the chosen department + subject, with workload and availability at this slot (display only; the save re-checks).</summary>
    [HttpGet("candidates")]
    public async Task<IActionResult> Candidates(int departmentId, string? subject, DayOfWeek day, int slotId, string? week, int? excludeSessionId, bool single, CancellationToken ct)
    {
        var list = await timetable.CandidatesAsync(departmentId, subject, day, slotId, single ? SchoolWeek.ParseOrCurrent(week) : null, excludeSessionId, ct);
        return Json(list.Select(c => new
        {
            c.Id, c.Name, c.Department, c.Total, c.Max, c.Remaining, c.Available, c.Reason,
            status = WorkloadRules.Tone(c.Status), statusLabel = WorkloadRules.Label(c.Status),
        }));
    }

    // ------------------------------------------------------------------ substitution (occupied class slot, one week)

    [HttpGet("substitute")]
    public async Task<IActionResult> Substitute(int sessionId, string? week, CancellationToken ct)
    {
        var model = new SubstituteModel { SessionId = sessionId, Week = week };
        if (!await FillAsync(model, ct)) return NotFoundResult("This session is no longer on the timetable.");
        return PageOrPartial("Substitute", model);
    }

    [HttpPost("substitute"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Substitute(SubstituteModel model, CancellationToken ct)
    {
        if (!await FillAsync(model, ct)) return NotFoundResult("This session is no longer on the timetable.");
        var s = model.Session!;
        if (model.TeacherId == s.TeacherId) ModelState.AddModelError("TeacherId", "Choose a teacher other than the class's own teacher.");
        if (ModelState.IsValid)
        {
            var note = string.IsNullOrWhiteSpace(model.Description) ? $"Covering {s.Subject} for {s.TeacherName}" : model.Description;
            var result = await workload.CreateAsync(new WorkloadInput(model.TeacherId!.Value, model.WeekStart, WorkloadCategory.Substitution, 1,
                s.ClassId, null, note, s.Day, s.SlotId), ct);
            if (result.Succeeded) return Saved($"Substitution saved for {ScheduleRules.DayName(s.Day)}, period {model.Slot!.Number}, week of {SchoolWeek.Label(model.WeekStart)}.", $"/timetable/classes/{s.ClassId}?week={SchoolWeek.Iso(model.WeekStart)}");
            AddErrors(result);
        }
        return FormView("Substitute", model);
    }

    // ------------------------------------------------------------------ teacher slot: office hours / duty

    [HttpGet("teacher-slot")]
    public async Task<IActionResult> TeacherSlot(int teacherId, DayOfWeek day, int slotId, string? week, CancellationToken ct)
    {
        var model = new TeacherSlotModel { TeacherId = teacherId, Day = day, SlotId = slotId, Week = week };
        if (!await FillAsync(model, ct)) return NotFoundResult("This teacher or slot no longer exists.");
        return PageOrPartial("TeacherSlot", model);
    }

    [HttpPost("teacher-slot"), ValidateAntiForgeryToken]
    public async Task<IActionResult> TeacherSlot(TeacherSlotModel model, CancellationToken ct)
    {
        if (!await FillAsync(model, ct)) return NotFoundResult("This teacher or slot no longer exists.");
        if (model.Kind == "duty" && model.DutyAssignmentId is null) ModelState.AddModelError("DutyAssignmentId", "Choose the duty to count.");
        if (ModelState.IsValid)
        {
            // Office hours sit in the slot; a duty keeps its own date and times and is linked to the existing duty record.
            var input = model.Kind == "duty"
                ? new WorkloadInput(model.TeacherId, model.WeekStart, WorkloadCategory.Duty, 1, null, model.DutyAssignmentId, model.Description)
                : new WorkloadInput(model.TeacherId, model.WeekStart, WorkloadCategory.Office, 1, null, null, model.Description, model.Day, model.SlotId);
            var result = await workload.CreateAsync(input, ct);
            if (result.Succeeded)
                return Saved(model.Kind == "duty" ? "Duty counted in the teacher's workload (Duties)." : $"Office hours added on {ScheduleRules.DayName(model.Day)}, period {model.Slot!.Number}.",
                    $"/timetable/teachers/{model.TeacherId}?week={SchoolWeek.Iso(model.WeekStart)}");
            AddErrors(result);
        }
        return FormView("TeacherSlot", model);
    }

    /// <summary>Removes a single-week period (substitution, office hours) placed in a slot.</summary>
    [HttpPost("periods/{id:int}/remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RemovePeriod(int id, CancellationToken ct)
    {
        var row = await workload.GetAsync(id, ct);
        if (row is null || row.Day is null) return Failure("This period no longer exists.");
        var result = await workload.DeleteAsync(id, ct);
        if (!result.Succeeded) return Failure(result.Errors[0].Value);
        return Saved($"{WorkloadRules.Label(row.Category)} period removed. Workload updated.", $"/timetable/teachers/{row.TeacherId}?week={SchoolWeek.Iso(row.WeekStart)}");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<bool> FillAsync(AssignPeriodModel model, CancellationToken ct)
    {
        var cls = (await classes.AllOptionsAsync(ct)).FirstOrDefault(c => c.Id == model.ClassId);
        if (cls is null) return false;
        model.ClassName = cls.Name;
        model.Slot = await timetable.GetSlotAsync(model.SlotId, ct);
        var depts = new List<(int, string, IReadOnlyList<string>)>();
        foreach (var d in await teachers.DepartmentsAsync(ct)) depts.Add((d.Id, d.Name, await timetable.SubjectsAsync(d.Id, ct)));
        model.Departments = depts;
        if (model.DepartmentId is { } did)
            model.Candidates = await timetable.CandidatesAsync(did, model.Subject, model.Day, model.SlotId, null, model.SessionId, ct);
        return model.Slot is not null;
    }

    private async Task<bool> FillAsync(SubstituteModel model, CancellationToken ct)
    {
        model.Session = await timetable.GetSessionAsync(model.SessionId, ct);
        if (model.Session is null) return false;
        model.WeekStart = SchoolWeek.ParseOrCurrent(model.Week);
        model.Week = SchoolWeek.Iso(model.WeekStart);
        model.Slot = await timetable.GetSlotAsync(model.Session.SlotId, ct);
        // Any academic teacher may substitute: show every department's teachers, the session's department first.
        var all = new List<TeacherCandidate>();
        foreach (var d in (await teachers.DepartmentsAsync(ct)).OrderByDescending(d => d.Id == model.Session.DepartmentId))
            all.AddRange((await timetable.CandidatesAsync(d.Id, null, model.Session.Day, model.Session.SlotId, model.WeekStart, null, ct))
                .Where(c => c.Id != model.Session.TeacherId));
        model.Candidates = all;
        return model.Slot is not null;
    }

    private async Task<bool> FillAsync(TeacherSlotModel model, CancellationToken ct)
    {
        var t = await teachers.GetAsync(model.TeacherId, ct);
        model.Slot = await timetable.GetSlotAsync(model.SlotId, ct);
        if (t is null || model.Slot is null || !ScheduleRules.IsSchoolDay(model.Day)) return false;
        model.TeacherName = t.FullName;
        model.WeekStart = SchoolWeek.ParseOrCurrent(model.Week);
        model.Week = SchoolWeek.Iso(model.WeekStart);
        var grid = await timetable.TeacherGridAsync(model.TeacherId, model.WeekStart, ct);
        model.Duties = grid.DutiesOn(model.Day).ToList();
        model.Periods = grid.PeriodsAt(model.Day, model.SlotId).ToList();
        model.Workload = await workload.SummaryAsync(model.TeacherId, model.WeekStart, ct);
        return true;
    }

    private async Task<IActionResult> SessionSaved(int id, string verb, CancellationToken ct)
    {
        var s = (await timetable.GetSessionAsync(id, ct))!;
        var w = await workload.SummaryAsync(s.TeacherId, SchoolWeek.Current, ct);
        return Saved($"{s.Subject} with {s.TeacherName} {verb} for {s.ClassName}, {ScheduleRules.DayName(s.Day)}. {s.TeacherName}: {w?.Total}/{w?.Max} periods this week.",
            $"/timetable/classes/{s.ClassId}");
    }

    private IActionResult Saved(string message, string back)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return Redirect(back);
    }

    private IActionResult Failure(string message)
    {
        if (IsAjax) return AjaxFailure(message, 409);
        Toast(message, "error");
        return Redirect("/timetable/classes");
    }
}
