using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Management Team, in two sections: School Leadership (the seven leadership positions, their holders, appointments) and
/// the Operation Team (members holding a position added by the school). Both read the same member records
/// (<see cref="ManagementTeams"/>); member profiles, forms, tasks and permissions are shared.
/// </summary>
[Route("management")]
public class ManagementController(IManagementService members, IManagementTaskService tasks,
    Services.OrgStructure.IOrgStructureService org, Services.OrgStructure.IOrgStructureAccess orgAccess) : AppController
{
    public const string LeadershipHref = ManagementTeams.LeadershipHref;
    public const string OperationHref = ManagementTeams.OperationHref;

    /// <summary>
    /// Management Team landing page: two cards, School Leadership and Operation Team. Tasks are reached from the sections
    /// (All Tasks / Add Task), not from a card of their own.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var all = await members.ListAsync(null, ct);
        var slots = await members.PositionsAsync(ct);
        var filled = slots.Count(s => s.Holder is not null);
        var operation = all.Count(ManagementTeams.IsOperation);
        var structure = await org.StructureAsync(false, ct);
        return View("~/Views/Staff/Hub.cshtml", new HubViewModel
        {
            Title = "Management Team",
            Subtitle = "School Leadership and the Operation Team: their members, positions and each member's tasks. Separate from academic staff.",
            Crumbs = [("Dashboard", "/"), ("Staff", "/staff"), ("Management Team", null)],
            Large = true,
            Cards =
            [
                new(LeadershipHref, "crown", ManagementTeams.LeadershipName,
                    "Manage school leadership members, positions, responsibilities, and appointment history in one place.",
                    $"{Ui.Plural(all.Count - operation, "member", "members")} · {filled} of {slots.Count} positions filled", "violet"),
                new(OperationHref, "briefcase", ManagementTeams.OperationName,
                    "Management Team members in the positions added by the school, with their responsibilities and tasks.",
                    Ui.Plural(operation, "member", "members"), "teal"),
            ],
            // The organizational structure views read the same records; the two section cards stay the landing page.
            Extra =
            [
                ("Organizational Chart", OrgLabels.OrgChartHref, "layers"),
                ("Executive Management", OrgLabels.ExecutiveHref, "shield-check"),
                ("Academic Leadership", OrgLabels.AcademicLeadershipHref, "graduation-cap"),
                ("Operations & Administration", OrgLabels.OperationsHref, "clipboard-check"),
                ("Departments Leadership", OrgLabels.DepartmentsLeadershipHref, "users"),
                ($"Positions & Assignments · {structure.AllPositions.Count(p => p.IsVacant)} vacant", OrgLabels.PositionsHref, "settings"),
            ],
        });
    }

    /// <summary>
    /// Management Team → Operation Team: the members holding a school-added position (cards; the same profiles, forms and
    /// tasks as School Leadership).
    /// </summary>
    [HttpGet("operation-team")]
    public async Task<IActionResult> OperationTeam(CancellationToken ct)
    {
        var all = await members.ListAsync(null, ct);
        return View(new OperationTeamViewModel
        {
            Members = all.Where(ManagementTeams.IsOperation).ToList(),
            Org = (await org.StructureAsync(false, ct)).Only(t => t.Section == OrgSection.OperationTeam),
            CanManageOrg = await orgAccess.CanManageAsync(ct),
        });
    }

    /// <summary>Operation Team → Download Report: the section's members.</summary>
    [HttpGet("operation-team/pdf")]
    public async Task<IActionResult> OperationTeamPdf(string? search, CancellationToken ct) =>
        await PdfFile(Services.Pdf.PdfReports.ManagementMembers((await members.ListAsync(search, ct)).Where(ManagementTeams.IsOperation).ToList(),
            search, null, ManagementTeams.OperationName), ct);

    /// <summary>School Leadership → Download Report: the section's members (same search and position filter).</summary>
    [HttpGet("leadership/pdf")]
    public async Task<IActionResult> LeadershipPdf(string? search, string? position, CancellationToken ct) =>
        await PdfFile(Services.Pdf.PdfReports.ManagementMembers(Filter((await members.ListAsync(search, ct)).Where(m => !ManagementTeams.IsOperation(m)), position),
            search, position, ManagementTeams.LeadershipName), ct);

    /// <summary>
    /// Management Team → School Leadership: one section with the leadership members (cards, with search and position
    /// filter) and the seven positions (holders, vacancies, appointment history). A link from the former tabs
    /// (<c>?tab=positions</c>) lands on the positions part.
    /// </summary>
    [HttpGet("leadership")]
    public async Task<IActionResult> Leadership(string? tab, string? search, string? position, string? status, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(tab))
            return Redirect(QueryHelpers.AddQueryString(LeadershipHref, Params(("search", search), ("position", position), ("status", status)))
                            + (tab == LeadershipViewModel.PositionsTab ? "#positions" : "#members"));
        // Leadership members only: the Operation Team's members (school-added positions) have their own section.
        var all = (await members.ListAsync(null, ct)).Where(m => !ManagementTeams.IsOperation(m)).ToList();
        return View(new LeadershipViewModel
        {
            AllMembers = all,
            Positions = await members.PositionsAsync(ct),
            Tasks = await tasks.CountsAsync(null, ct),
            Members = Filter(string.IsNullOrWhiteSpace(search) ? all : (await members.ListAsync(search, ct)).Where(m => !ManagementTeams.IsOperation(m)), position),
            Search = search,
            Position = position,
            Status = status is "filled" or "vacant" ? status : null,
            History = await members.PositionHistoryAsync(null, ct),
            Org = (await org.StructureAsync(false, ct)).Only(t => t.Section == OrgSection.SchoolLeadership),
            CanManageOrg = await orgAccess.CanManageAsync(ct),
        });
    }

    /// <summary>School Leadership → one position: description, holder, the holder's tasks and the appointment history.</summary>
    [HttpGet("leadership/positions/{position}")]
    public async Task<IActionResult> Position(ManagementPosition position, [FromQuery] TaskListQuery query, CancellationToken ct)
    {
        if (!Enum.IsDefined(position)) return NotFoundResult("Unknown position.");
        var slot = (await members.PositionsAsync(ct)).First(s => s.Position == position);
        TaskIndexViewModel? list = null;
        if (slot.Holder is { } holder)
        {
            query.MemberId = holder.Id;
            list = new TaskIndexViewModel
            {
                Query = query,
                Result = await tasks.ListAsync(query, ct),
                Counts = await tasks.CountsAsync(holder.Id, ct),
                BaseUrl = ManagementPositions.Href(position),
            };
            if (IsAjax) return PartialView("~/Views/ManagementTasks/_TaskList.cshtml", list);
        }
        var history = await members.PositionHistoryAsync(null, ct);
        return View(new LeadershipPositionViewModel { Slot = slot, Tasks = list, History = history.Where(h => h.Position == position).ToList() });
    }

    /// <summary>Former Team Members page: now the members part of School Leadership (filters kept).</summary>
    [HttpGet("members")]
    public IActionResult Members(string? search, string? position) =>
        Redirect(QueryHelpers.AddQueryString(LeadershipHref, Params(("search", search), ("position", position))) + "#members");

    /// <summary>Former Management Positions page: now the positions part of School Leadership.</summary>
    [HttpGet("positions")]
    public IActionResult Positions() => Redirect($"{LeadershipHref}#positions");

    /// <summary>Every Management Team member (both sections) → Download Report (same search and position filter).</summary>
    [HttpGet("members/pdf")]
    public async Task<IActionResult> MembersPdf(string? search, string? position, CancellationToken ct) =>
        await PdfFile(Services.Pdf.PdfReports.ManagementMembers(Filter(await members.ListAsync(search, ct), position), search, position), ct);

    private static IReadOnlyList<ManagementMemberRow> Filter(IEnumerable<ManagementMemberRow> list, string? position)
    {
        if (position == "none") list = list.Where(m => !m.HasPosition);
        else if (Enum.TryParse<ManagementPosition>(position, out var p)) list = list.Where(m => m.Position == p);
        return list.ToList();
    }

    private static Dictionary<string, string?> Params(params (string Key, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value);

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
                var message = who is null ? $"{ManagementPositions.Name(position)} is now vacant." : $"{who} is now {ManagementPositions.Name(position)}.";
                if (IsAjax) return AjaxSuccess(message);
                Toast(message);
                return Redirect(ManagementPositions.Href(position));
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
        model.PositionChoice = PositionChoices.Of(model.Position, null);
        model.CustomPositions = await members.CustomPositionsAsync(null, ct);
        return PageOrPartial("MemberForm", model);
    }

    [HttpPost("members/create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ManagementMemberFormModel model, CancellationToken ct)
    {
        model.Id = null;
        var input = model.ToInput();
        if (input is null) ModelState.AddModelError(nameof(model.Position), "Choose a position from the list.");
        if (ModelState.IsValid && input is not null)
        {
            var result = await members.CreateAsync(input, ct);
            if (result.Succeeded) return Saved($"{model.FullName.Trim()} was added to the Management Team.", $"/management/members/{result.EntityId}");
            AddErrors(result);
        }
        model.Holders = await members.PositionHoldersAsync(null, ct);
        model.CustomPositions = await members.CustomPositionsAsync(null, ct);
        model.PositionChoice ??= PositionChoices.Of(model.Position, null);
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
            Org = await org.StaffAsync(id, ct),
            CanManageOrg = await orgAccess.CanManageAsync(ct),
            Roster = await HttpContext.RequestServices.GetRequiredService<Services.TeachingRoster.IStaffRosterQuery>().ProfileAsync(id, ct),
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
            PositionChoice = PositionChoices.Of(m.Position, m.CustomPositionId),
            ContactNumber = m.ContactNumber,
            Email = m.Email,
            AppointedOn = m.AppointedOn,
            Holders = await members.PositionHoldersAsync(id, ct),
            CustomPositions = await members.CustomPositionsAsync(id, ct),
        });
    }

    [HttpPost("members/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ManagementMemberFormModel model, CancellationToken ct)
    {
        model.Id = id;
        var input = model.ToInput();
        if (input is null) ModelState.AddModelError(nameof(model.Position), "Choose a position from the list.");
        if (ModelState.IsValid && input is not null)
        {
            var result = await members.UpdateAsync(id, input, ct);
            if (result.Succeeded)
                return Saved($"{model.FullName.Trim()} was updated.", IsAjax ? null : (await members.GetAsync(id, ct)) is { } saved ? ManagementTeams.Href(saved) : null);
            AddErrors(result);
        }
        model.Holders = await members.PositionHoldersAsync(id, ct);
        model.CustomPositions = await members.CustomPositionsAsync(id, ct);
        model.PositionChoice ??= PositionChoices.Of(model.Position, null);
        return FormView("MemberForm", model);
    }

    [HttpPost("members/{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var member = await members.GetAsync(id, ct);
        var result = await members.DeleteAsync(id, ct);
        var message = result.Succeeded ? $"{member?.FullName} was removed from the Management Team." : result.Errors[0].Value;
        var section = member is null ? LeadershipHref : ManagementTeams.Href(member);
        if (IsAjax) return result.Succeeded ? AjaxSuccess(message, section) : AjaxFailure(message, 409);
        Toast(message, result.Succeeded ? "success" : "error");
        return Redirect(result.Succeeded ? section : $"/management/members/{id}");
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
        return Redirect(redirect ?? LeadershipHref);
    }
}
