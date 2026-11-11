using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>Management Team → Team Members: the seven positions, member records and member profiles.</summary>
[Route("management")]
public class ManagementController(IManagementService members, IManagementTaskService tasks) : AppController
{
    /// <summary>Management Team landing page.</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var all = await members.ListAsync(null, ct);
        var slots = await members.PositionsAsync(ct);
        var counts = await tasks.CountsAsync(null, ct);
        return View("~/Views/Staff/Hub.cshtml", new HubViewModel
        {
            Title = "Management Team",
            Subtitle = "School leadership: its members, the seven positions, and each member's tasks. Separate from academic staff.",
            Crumbs = [("Dashboard", "/"), ("Staff", "/staff"), ("Management Team", null)],
            Cards =
            [
                new("/management/members", "users", "Team Members", "Profiles of every Management Team member, with their position and tasks.", Ui.Plural(all.Count, "member", "members"), "blue"),
                new("/management/positions", "crown", "Management Positions", "The seven positions, who holds each one, and the appointment history.",
                    $"{slots.Count(s => s.Holder is not null)} of {slots.Count} filled", "violet"),
                new("/management/tasks", "clipboard-check", "Tasks", "Individual tasks with priority, due date and status.",
                    counts.Overdue > 0 ? $"{counts.Open} open · {counts.Overdue} overdue" : $"{counts.Open} open", counts.Overdue > 0 ? "rose" : "teal"),
            ],
        });
    }

    /// <summary>Management Team → Team Members.</summary>
    [HttpGet("members")]
    public async Task<IActionResult> Members(string? search, string? position, CancellationToken ct)
    {
        var all = await members.ListAsync(null, ct);
        IEnumerable<ManagementMemberRow> list = await members.ListAsync(search, ct);
        if (position == "none") list = list.Where(m => m.Position is null);
        else if (Enum.TryParse<ManagementPosition>(position, out var p)) list = list.Where(m => m.Position == p);
        return View(new ManagementMembersViewModel { Members = list.ToList(), Total = all.Count, Search = search, Position = position });
    }

    /// <summary>Team Members → Download Report (same search and position filter).</summary>
    [HttpGet("members/pdf")]
    public async Task<IActionResult> MembersPdf(string? search, string? position, CancellationToken ct)
    {
        IEnumerable<ManagementMemberRow> list = await members.ListAsync(search, ct);
        if (position == "none") list = list.Where(m => m.Position is null);
        else if (Enum.TryParse<ManagementPosition>(position, out var p)) list = list.Where(m => m.Position == p);
        return await PdfFile(Services.Pdf.PdfReports.ManagementMembers(list.ToList(), search, position), ct);
    }

    /// <summary>Management Team → Management Positions: the seven positions, holders and history.</summary>
    [HttpGet("positions")]
    public async Task<IActionResult> Positions(CancellationToken ct)
    {
        var all = await members.ListAsync(null, ct);
        return View("Index", new ManagementIndexViewModel
        {
            Positions = await members.PositionsAsync(ct),
            Unpositioned = all.Where(m => m.Position is null).ToList(),
            Tasks = await tasks.CountsAsync(null, ct),
            History = await members.PositionHistoryAsync(null, ct),
        });
    }

    [HttpGet("positions/{position}/assign")]
    public async Task<IActionResult> AssignPosition(ManagementPosition position, CancellationToken ct)
    {
        if (!Enum.IsDefined(position)) return NotFoundResult("Unknown position.");
        var model = new AssignPositionModel { Position = position, StartDate = Common.SchoolWeek.Today };
        await FillAsync(model, ct);
        return PageOrPartial("AssignPosition", model);
    }

    [HttpPost("positions/{position}/assign"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignPosition(ManagementPosition position, AssignPositionModel model, CancellationToken ct)
    {
        if (!Enum.IsDefined(position)) return NotFoundResult("Unknown position.");
        model.Position = position;
        if (ModelState.IsValid)
        {
            var result = await members.ReassignPositionAsync(position, model.MemberId, model.StartDate, ct);
            if (result.Succeeded)
            {
                var who = model.MemberId is null ? null : (await members.GetAsync(model.MemberId.Value, ct))?.FullName;
                return Saved(who is null ? $"{Common.ManagementPositions.Name(position)} is now vacant." : $"{who} is now {Common.ManagementPositions.Name(position)}.", null);
            }
            AddErrors(result);
        }
        await FillAsync(model, ct);
        return FormView("AssignPosition", model);
    }

    private async Task FillAsync(AssignPositionModel model, CancellationToken ct)
    {
        model.Members = await members.OptionsAsync(ct);
        model.Current = (await members.PositionsAsync(ct)).First(s => s.Position == model.Position).Holder;
    }

    [HttpGet("members/create")]
    public async Task<IActionResult> Create(ManagementPosition? position, CancellationToken ct)
    {
        var model = new ManagementMemberFormModel { Position = position is { } p && Enum.IsDefined(p) ? p : null };
        model.Holders = await members.PositionHoldersAsync(null, ct);
        if (model.Position is { } held && model.Holders.ContainsKey(held)) model.Position = null;
        return PageOrPartial("MemberForm", model);
    }

    [HttpPost("members/create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ManagementMemberFormModel model, CancellationToken ct)
    {
        model.Id = null;
        if (ModelState.IsValid)
        {
            var result = await members.CreateAsync(model.ToInput(), ct);
            if (result.Succeeded) return Saved($"{model.FullName.Trim()} was added to the Management Team.", $"/management/members/{result.EntityId}");
            AddErrors(result);
        }
        model.Holders = await members.PositionHoldersAsync(null, ct);
        return FormView("MemberForm", model);
    }

    [HttpGet("members/{id:int}")]
    public async Task<IActionResult> Profile(int id, [FromQuery] TaskListQuery query, CancellationToken ct)
    {
        var member = await members.GetAsync(id, ct);
        if (member is null) return NotFoundResult("This Management Team member no longer exists.");
        query.MemberId = id;
        var counts = await tasks.CountsAsync(id, ct);
        var list = new TaskIndexViewModel
        {
            Query = query,
            Result = await tasks.ListAsync(query, ct),
            Counts = counts,
            BaseUrl = $"/management/members/{id}",
        };
        if (IsAjax) return PartialView("~/Views/ManagementTasks/_TaskList.cshtml", list);
        return View(new ManagementProfileViewModel
        {
            Member = member,
            Counts = counts,
            Tasks = list,
            History = await tasks.HistoryAsync(id, 40, ct),
            Positions = await members.PositionHistoryAsync(id, ct),
        });
    }

    [HttpGet("members/{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var m = await members.GetAsync(id, ct);
        if (m is null) return NotFoundResult("This Management Team member no longer exists.");
        return PageOrPartial("MemberForm", new ManagementMemberFormModel
        {
            Id = m.Id,
            FullName = m.FullName,
            Position = m.Position,
            ContactNumber = m.ContactNumber,
            Email = m.Email,
            AppointedOn = m.AppointedOn,
            Holders = await members.PositionHoldersAsync(id, ct),
        });
    }

    [HttpPost("members/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ManagementMemberFormModel model, CancellationToken ct)
    {
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await members.UpdateAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return Saved($"{model.FullName.Trim()} was updated.", null);
            AddErrors(result);
        }
        model.Holders = await members.PositionHoldersAsync(id, ct);
        return FormView("MemberForm", model);
    }

    [HttpPost("members/{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var member = await members.GetAsync(id, ct);
        var result = await members.DeleteAsync(id, ct);
        var message = result.Succeeded ? $"{member?.FullName} was removed from the Management Team." : result.Errors[0].Value;
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message, "/management/members") : AjaxFailure(message, 409);
        Toast(message, result.Succeeded ? "success" : "error");
        return Redirect(result.Succeeded ? "/management/members" : $"/management/members/{id}");
    }

    [HttpGet("members/check-duplicate")]
    public async Task<IActionResult> CheckDuplicate(string? fullName, string? contactNumber, int? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return Json(true);
        var duplicate = await members.FindDuplicateAsync(fullName, contactNumber, id, ct);
        return duplicate is null
            ? Json(true)
            : Json($"\"{duplicate.FullName}\" is already on the Management Team{(duplicate.ContactNumber is null ? "" : $" ({duplicate.ContactNumber})")}.");
    }

    /// <summary>AJAX: toast + refresh (or go to <paramref name="redirect"/>); otherwise a redirect with a toast.</summary>
    private IActionResult Saved(string message, string? redirect)
    {
        if (IsAjax) return AjaxSuccess(message, redirect);
        Toast(message);
        return Redirect(redirect ?? "/management/members");
    }
}
