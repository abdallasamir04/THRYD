using SchoolManagement.Web.Services.Accounts;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.FloorManagement;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Floor Management (sidebar): the floors and their teams, reports / escalations along the reporting lines, and Floor Social
/// Worker tickets. The floor inspections and student movements stay at /floor-audit (now part of Floor Management).
/// Access comes from the person's floor roles and configured positions only, checked here and in the service.
/// </summary>
[Route("floor-management")]
[StaffAccess(StaffAccessRule.FloorTeam)]
public class FloorManagementController(IFloorManagementService floors) : AppController
{
    // ================================================================== floors

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var u = await floors.CurrentAsync(ct);
        if (!u.HasAccess) return Denied();
        var all = await floors.FloorsAsync(false, ct);
        var visible = all.Where(f => f.GradeRange is not null && (f.IsActive || u.IsAdmin) && u.CanView(f.Id)).ToList();
        return View(new FloorDashboardViewModel
        {
            User = u, Floors = visible, Consultant = await floors.ConsultantAsync(ct),
            Unconfigured = u.IsAdmin ? all.Where(f => f.GradeRange is null && f.IsActive).ToList() : [],
            OpenReports = visible.Sum(f => f.OpenReports), OpenTickets = visible.Sum(f => f.OpenTickets),
        });
    }

    [HttpGet("floors/{id:int}")]
    public async Task<IActionResult> Floor(int id, CancellationToken ct)
    {
        var u = await floors.CurrentAsync(ct);
        if (!u.CanView(id)) return Denied();
        var floor = await floors.FloorAsync(id, ct);
        if (floor is null) return NotFound();
        return View(new FloorDetailsViewModel { User = u, Floor = floor, Consultant = await floors.ConsultantAsync(ct), History = await floors.HistoryAsync(id, ct) });
    }

    [HttpGet("floors/new")]
    public async Task<IActionResult> NewFloor(CancellationToken ct)
    {
        if (!(await floors.CurrentAsync(ct)).CanAddFloor) return Denied();
        return PageOrPartial("FloorForm", new FloorFormModel { SortOrder = (await floors.FloorsAsync(false, ct)).Count, Staff = await floors.StaffOptionsAsync(ct) });
    }

    [HttpGet("floors/{id:int}/edit")]
    public async Task<IActionResult> EditFloor(int id, CancellationToken ct)
    {
        if (!(await floors.CurrentAsync(ct)).CanAddFloor) return Denied();
        var f = await floors.FloorAsync(id, ct);
        if (f is null) return NotFoundResult("This floor no longer exists.");
        return PageOrPartial("FloorForm", new FloorFormModel { Id = f.Id, Name = f.Name, GradeRange = f.GradeRange, SortOrder = f.SortOrder, IsActive = f.IsActive });
    }

    [HttpPost("floors/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFloor(FloorFormModel model, CancellationToken ct)
    {
        if (!(await floors.CurrentAsync(ct)).CanAddFloor) return Denied();
        var result = await floors.SaveFloorAsync(model.Id, new FloorInput { Name = model.Name, GradeRange = model.GradeRange, SortOrder = model.SortOrder, IsActive = model.IsActive }, ct);
        if (result.Succeeded && model.Id is null && result.EntityId is { } floorId)
        {
            // The new floor's first team members (each recorded as its own assignment, with history).
            foreach (var (staff, role) in new[] { (model.StageCoordinatorId, FloorRole.StageCoordinator), (model.FloorSupervisorId, FloorRole.FloorSupervisor),
                         (model.FloatingMatronId, FloorRole.FloatingMatron), (model.SocialWorkerId, FloorRole.SocialWorker) })
                if (staff is { } s)
                {
                    var assigned = await floors.AssignAsync(floorId, new FloorAssignInput { StaffMemberId = s, Role = role, Coverage = model.GradeRange }, ct);
                    if (!assigned.Succeeded) Toast(assigned.Errors[0].Value, "warning");
                }
        }
        if (result.Succeeded) return Saved($"{model.Name.Trim()} was saved.", $"/floor-management/floors/{result.EntityId}");
        AddErrors(result);
        model.Staff = model.Id is null ? await floors.StaffOptionsAsync(ct) : [];
        return FormView("FloorForm", model);
    }

    [HttpGet("floors/{id:int}/assign")]
    public async Task<IActionResult> Assign(int id, FloorRole? role, CancellationToken ct)
    {
        if (!(await floors.CurrentAsync(ct)).CanManageTeam(id)) return Denied();
        var f = await floors.FloorAsync(id, ct);
        if (f is null) return NotFoundResult("This floor no longer exists.");
        var model = new FloorAssignFormModel { FloorId = id, FloorName = f.Name, Role = role ?? FloorRole.Matron };
        if (model.Role is not FloorRole.Matron) model.Coverage = f.GradeRange;
        return PageOrPartial("AssignForm", await FillAsync(model, ct));
    }

    [HttpPost("floors/{id:int}/assign"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int id, FloorAssignFormModel model, CancellationToken ct)
    {
        if (!(await floors.CurrentAsync(ct)).CanManageTeam(id)) return Denied();
        var result = await floors.AssignAsync(id, new FloorAssignInput { StaffMemberId = model.StaffMemberId, Role = model.Role, Coverage = model.Coverage, ClassIds = model.ClassIds }, ct);
        if (result.Succeeded) return Saved($"{FloorManagementService.Label(model.Role)} assigned.", $"/floor-management/floors/{id}");
        AddErrors(result);
        model.FloorId = id;
        model.FloorName = (await floors.FloorAsync(id, ct))?.Name ?? "";
        return FormView("AssignForm", await FillAsync(model, ct));
    }

    private async Task<FloorAssignFormModel> FillAsync(FloorAssignFormModel m, CancellationToken ct)
    {
        m.Staff = await floors.StaffOptionsAsync(ct);
        m.Classes = await floors.ClassOptionsAsync(ct);
        return m;
    }

    /// <summary>Edit a current assignment's grade range / classes (the person and role stay; a different person is a new assignment).</summary>
    [HttpGet("assignments/{id:int}/edit")]
    public async Task<IActionResult> EditAssignment(int id, CancellationToken ct)
    {
        var a = await floors.AssignmentAsync(id, ct);
        if (a is null) return NotFoundResult("This assignment has ended.");
        if (!(await floors.CurrentAsync(ct)).CanManageTeam(a.FloorId)) return Denied();
        return PageOrPartial("EditAssignmentForm", new FloorAssignmentEditFormModel
        {
            Id = a.Id, FloorId = a.FloorId, FloorName = a.FloorName, StaffName = a.StaffName, Role = a.Role, Coverage = a.Coverage, ClassIds = a.ClassIds.ToList(),
            Classes = await floors.ClassOptionsAsync(ct),
        });
    }

    [HttpPost("assignments/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditAssignment(int id, FloorAssignmentEditFormModel model, CancellationToken ct)
    {
        var a = await floors.AssignmentAsync(id, ct);
        if (a is null) return NotFoundResult("This assignment has ended.");
        if (!(await floors.CurrentAsync(ct)).CanManageTeam(a.FloorId)) return Denied();
        var result = await floors.UpdateAssignmentAsync(id, model.Coverage, model.ClassIds, ct);
        if (result.Succeeded) return Saved($"{a.StaffName}'s grade range was saved.", $"/floor-management/floors/{a.FloorId}");
        AddErrors(result);
        (model.Id, model.FloorId, model.FloorName, model.StaffName, model.Role) = (a.Id, a.FloorId, a.FloorName, a.StaffName, a.Role);
        model.Classes = await floors.ClassOptionsAsync(ct);
        return FormView("EditAssignmentForm", model);
    }

    [HttpPost("assignments/{id:int}/end"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EndAssignment(int id, int floor, string? reason, CancellationToken ct) =>
        Done(await floors.EndAssignmentAsync(id, reason, ct), "The assignment ended and is kept in the history.", $"/floor-management/floors/{floor}");

    // ================================================================== reports

    [HttpGet("reports")]
    public async Task<IActionResult> Reports(CancellationToken ct)
    {
        var u = await floors.CurrentAsync(ct);
        if (!u.HasAccess) return Denied();
        return View(new FloorReportsViewModel { User = u, Reports = await floors.ReportsAsync(ct), Floors = await MyFloorsAsync(u, ct) });
    }

    [HttpGet("reports/new")]
    public async Task<IActionResult> NewReport(int? floor, CancellationToken ct)
    {
        var u = await floors.CurrentAsync(ct);
        var mine = (await MyFloorsAsync(u, ct)).Where(f => u.CanSubmit(f.Id)).ToList();
        if (mine.Count == 0) return Denied();
        var model = new FloorReportFormModel { FloorId = floor is { } f && mine.Any(x => x.Id == f) ? f : mine[0].Id, OccurredAt = SchoolTime.Now };
        return PageOrPartial("ReportForm", await FillAsync(model, mine, ct));
    }

    [HttpPost("reports/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> NewReport(FloorReportFormModel model, CancellationToken ct)
    {
        var u = await floors.CurrentAsync(ct);
        if (!u.CanSubmit(model.FloorId)) return Denied();
        var result = await floors.SubmitReportAsync(new FloorReportInput
        {
            FloorId = model.FloorId, Title = model.Title, Description = model.Description, Category = model.Category, RelatedStaffId = model.RelatedStaffId,
            OccurredAt = model.OccurredAt, Recipient = model.Recipient,
        }, ct);
        if (result.Succeeded) return Saved("The report was submitted.", $"/floor-management/reports/{result.EntityId}");
        AddErrors(result);
        return FormView("ReportForm", await FillAsync(model, (await MyFloorsAsync(u, ct)).Where(f => u.CanSubmit(f.Id)).ToList(), ct));
    }

    private async Task<FloorReportFormModel> FillAsync(FloorReportFormModel m, IReadOnlyList<FloorTeamCard> mine, CancellationToken ct)
    {
        m.Floors = mine;
        m.Recipients = await floors.RecipientsAsync(m.FloorId, ct);
        m.Staff = await floors.StaffOptionsAsync(ct);
        return m;
    }

    [HttpGet("reports/{id:int}")]
    public async Task<IActionResult> Report(int id, CancellationToken ct)
    {
        var r = await floors.ReportAsync(id, ct);
        if (r is null) return Denied();
        var u = await floors.CurrentAsync(ct);
        var canReview = await floors.CanReviewAsync(r, ct);
        var open = r.Row.Status is not (FloorReportStatus.Resolved or FloorReportStatus.Closed);
        return View(new FloorReportViewModel
        {
            Report = r, CanReview = canReview && open,
            CanEscalate = open && r.Row.Recipient != FloorReportRecipient.SchoolConsultant && (canReview || (u.StaffId is { } me && me == r.CreatedByStaffId)),
        });
    }

    [HttpPost("reports/{id:int}/review"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewReport(int id, FloorReportStatus status, string? note, CancellationToken ct) =>
        Done(await floors.ReviewReportAsync(id, status, note, ct), "The report was updated.", $"/floor-management/reports/{id}");

    [HttpPost("reports/{id:int}/escalate"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EscalateReport(int id, string? note, CancellationToken ct) =>
        Done(await floors.EscalateReportAsync(id, note, ct), "The report was escalated to the next level.", $"/floor-management/reports/{id}");

    // ================================================================== tickets

    [HttpGet("tickets")]
    public async Task<IActionResult> Tickets(CancellationToken ct)
    {
        var u = await floors.CurrentAsync(ct);
        if (!u.HasAccess) return Denied();
        return View(new FloorTicketsViewModel { User = u, Tickets = await floors.TicketsAsync(ct) });
    }

    [HttpGet("tickets/new")]
    public async Task<IActionResult> NewTicket(int? floor, CancellationToken ct)
    {
        var u = await floors.CurrentAsync(ct);
        var mine = await MyFloorsAsync(u, ct);
        if (mine.Count == 0) return Denied();
        return PageOrPartial("TicketForm", new FloorTicketFormModel
        {
            FloorId = floor is { } f && mine.Any(x => x.Id == f) ? f : mine[0].Id, Floors = mine, Students = await floors.StudentOptionsAsync(ct),
        });
    }

    [HttpPost("tickets/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> NewTicket(FloorTicketFormModel model, CancellationToken ct)
    {
        var u = await floors.CurrentAsync(ct);
        if (!u.CanView(model.FloorId)) return Denied();
        var result = await floors.OpenTicketAsync(new FloorTicketInput
        {
            FloorId = model.FloorId, StudentId = model.StudentId, Category = model.Category, Priority = model.Priority, Description = model.Description,
        }, ct);
        if (result.Succeeded) return Saved("The ticket was opened.", $"/floor-management/tickets/{result.EntityId}");
        AddErrors(result);
        model.Floors = await MyFloorsAsync(u, ct);
        model.Students = await floors.StudentOptionsAsync(ct);
        return FormView("TicketForm", model);
    }

    [HttpGet("tickets/{id:int}")]
    public async Task<IActionResult> Ticket(int id, CancellationToken ct)
    {
        var t = await floors.TicketAsync(id, ct);
        if (t is null) return Denied();
        var floor = await floors.FloorAsync(t.Row.FloorId, ct);
        return View(new FloorTicketViewModel
        {
            Ticket = t, CanFollowUp = await floors.CanFollowUpAsync(t.Row, ct), SocialWorkers = floor?.Of(FloorRole.SocialWorker) ?? [],
        });
    }

    [HttpPost("tickets/{id:int}/update"), ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTicket(int id, FloorTicketStatus status, int? socialWorkerId, string? resolution, string? note, CancellationToken ct) =>
        Done(await floors.UpdateTicketAsync(id, status, socialWorkerId, resolution, note, ct), "The ticket was updated.", $"/floor-management/tickets/{id}");

    // ================================================================== helpers

    private async Task<IReadOnlyList<FloorTeamCard>> MyFloorsAsync(FloorUser u, CancellationToken ct) =>
        (await floors.FloorsAsync(true, ct)).Where(f => f.IsActive && u.CanView(f.Id)).ToList();

    /// <summary>403 with the reason: Floor Management is open only to its configured roles.</summary>
    private IActionResult Denied()
    {
        const string message = "Floor Management is open to the Super Admin, the School Consultant, Floor Management administrators and the staff assigned to a floor.";
        if (IsAjax) return AjaxFailure(message, StatusCodes.Status403Forbidden);
        Response.StatusCode = StatusCodes.Status403Forbidden;
        ViewData["Message"] = message;
        return View("Denied");
    }

    private IActionResult Saved(string message, string redirect)
    {
        if (IsAjax) return AjaxSuccess(message, redirect);
        Toast(message);
        return Redirect(redirect);
    }

    private IActionResult Done(ServiceResult result, string message, string redirect)
    {
        var text = result.Succeeded ? message : result.Errors[0].Value;
        if (IsAjax) return result.Succeeded ? AjaxSuccess(text, redirect) : AjaxFailure(text, StatusCodes.Status409Conflict);
        Toast(text, result.Succeeded ? "success" : "error");
        return Redirect(redirect);
    }
}
