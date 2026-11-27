using SchoolManagement.Web.Services.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.OrgStructure;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// The school's organizational structure inside the Management area: the Organizational Chart, the tier views (Executive
/// Management, Academic Leadership, Operations &amp; Administration, Departments Leadership) and Positions &amp; Assignments.
/// Every view reads the same tiers, units, positions and assignments; people are their existing staff records. Viewing is
/// open; every change checks <see cref="IOrgStructureAccess"/> here and again in the service.
/// </summary>
[Route("management")]
[StaffAccess(StaffAccessRule.OrgStructure)]
public class OrgStructureController(IOrgStructureService org, IOrgStructureAccess access, IOrgStructureSeeder seeder, SchoolDbContext db) : AppController
{
    private const string Tier1 = "tier-1", Tier2 = "tier-2", Tier3 = "tier-3", Tier4 = "tier-4", Tier5 = "tier-5", Tier6 = "tier-6";

    // ================================================================== views

    /// <summary>The suggested address of School Leadership: the existing page.</summary>
    [HttpGet("school-leadership")]
    public IActionResult SchoolLeadership() => Redirect(ManagementTeams.LeadershipHref);

    [HttpGet("org-chart")]
    public async Task<IActionResult> OrgChart(int? tier, string? search, CancellationToken ct) =>
        View(new OrgChartViewModel { Structure = await org.StructureAsync(false, ct), CanManage = await access.CanManageAsync(ct), Tier = tier, Search = search });

    [HttpGet("executive")]
    public Task<IActionResult> Executive(CancellationToken ct) => Section(ManagementNav.Executive, "Executive Management",
        "The school's executive advisory and governance: each position, who holds it and how to reach them.", "crown", t => t.SourceKey == Tier1, ct);

    [HttpGet("academic-leadership")]
    public Task<IActionResult> AcademicLeadership(CancellationToken ct) => Section(ManagementNav.Academic, "Academic Leadership",
        "Academic leadership and the academic departments' leaders — HODs, coordinators and team leaders — with their grade ranges.", "graduation-cap",
        t => t.SourceKey is Tier2 or Tier6, ct);

    /// <summary>Operations &amp; Administration: parents care, PR, admissions, reception, counseling, discipline and the Operation Team's units.</summary>
    [HttpGet("operations")]
    public Task<IActionResult> Operations(CancellationToken ct) => Section(ManagementNav.Operations, "Operations & Administration",
        "Parents care, PR, admissions, reception, counseling and discipline, with the Operation Team's floor supervisors and operational units.", "briefcase",
        t => t.SourceKey is Tier3 or Tier4 or Tier5 || (t.SourceKey is null && t.Section == OrgSection.OperationTeam), ct);

    private async Task<IActionResult> Section(string nav, string title, string subtitle, string icon, Func<OrgTierNode, bool> tiers, CancellationToken ct) =>
        View("Section", new OrgSectionViewModel
        {
            Nav = nav, Title = title, Subtitle = subtitle, Icon = icon,
            Structure = (await org.StructureAsync(false, ct)).Only(tiers),
            CanManage = await access.CanManageAsync(ct),
        });

    [HttpGet("departments-leadership")]
    public async Task<IActionResult> DepartmentsLeadership(CancellationToken ct)
    {
        var tree = (await org.StructureAsync(false, ct)).Only(t => t.AllUnits.Any(u => u.IsAcademicDepartment));
        var hods = await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder)
            .Select(d => new { d.Id, d.Name, Hod = d.HeadAssignments.Where(h => h.IsCurrent).Select(h => h.Teacher.FullName).FirstOrDefault() })
            .ToListAsync(ct);
        return View(new DepartmentsLeadershipViewModel
        {
            Structure = tree, CanManage = await access.CanManageAsync(ct),
            HodRecords = hods.Select(h => (h.Id, h.Name, h.Hod)).ToList(),
        });
    }

    [HttpGet("positions-management")]
    public async Task<IActionResult> Positions(string? tab, int? tier, int? unit, int? position, string? status, string? search, bool ended, CancellationToken ct)
    {
        tab = tab is PositionsManagementViewModel.PositionsTab or PositionsManagementViewModel.UnitsTab or PositionsManagementViewModel.TiersTab
            or PositionsManagementViewModel.ReviewTab ? tab : PositionsManagementViewModel.AssignmentsTab;
        return View(new PositionsManagementViewModel
        {
            Tab = tab,
            Structure = await org.StructureAsync(true, ct),
            Assignments = await org.AssignmentsAsync(ended, ct),
            CanManage = await access.CanManageAsync(ct),
            Tier = tier, Unit = unit, Position = position, Search = search,
            Status = status is "vacant" or "filled" ? status : null,
            IncludeEnded = ended,
        });
    }

    /// <summary>Records the official structure (idempotent) and shows what it did.</summary>
    [HttpPost("org/import"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var report = await seeder.SeedAsync(ct);
        return View("Positions", new PositionsManagementViewModel
        {
            Tab = PositionsManagementViewModel.ReviewTab,
            Structure = await org.StructureAsync(true, ct),
            Assignments = await org.AssignmentsAsync(false, ct),
            CanManage = true,
            ImportReport = report,
        });
    }

    // ================================================================== tiers

    [HttpGet("org/tiers/create")]
    public async Task<IActionResult> CreateTier(CancellationToken ct) =>
        !await access.CanManageAsync(ct) ? Denied()
        : PageOrPartial("TierForm", new OrgTierFormModel { SortOrder = (await org.StructureAsync(true, ct)).Tiers.Count + 1 });

    [HttpGet("org/tiers/{id:int}/edit")]
    public async Task<IActionResult> EditTier(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var t = await org.TierAsync(id, ct);
        if (t is null) return NotFoundResult("This tier no longer exists.");
        return PageOrPartial("TierForm", new OrgTierFormModel { Id = t.Id, Name = t.Name, Description = t.Description, Section = t.Section, SortOrder = t.SortOrder, IsActive = t.IsActive });
    }

    [HttpPost("org/tiers/create"), HttpPost("org/tiers/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTier(int? id, OrgTierFormModel model, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await org.SaveTierAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return Saved($"Tier \"{model.Name.Trim()}\" was saved.", Tab(PositionsManagementViewModel.TiersTab));
            AddErrors(result);
        }
        return FormView("TierForm", model);
    }

    // ================================================================== units

    [HttpGet("org/units/create")]
    public async Task<IActionResult> CreateUnit(int? tier, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var model = new OrgUnitFormModel { TierId = tier ?? 0, SortOrder = 1 };
        await FillAsync(model, ct);
        return PageOrPartial("UnitForm", model);
    }

    [HttpGet("org/units/{id:int}/edit")]
    public async Task<IActionResult> EditUnit(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var u = await org.UnitAsync(id, ct);
        if (u is null) return NotFoundResult("This unit no longer exists.");
        var model = new OrgUnitFormModel
        {
            Id = u.Id, Name = u.Name, Description = u.Description, TierId = u.OrgTierId, ParentUnitId = u.ParentUnitId, SortOrder = u.SortOrder,
            IsActive = u.IsActive, DepartmentIds = u.Departments.Select(d => d.DepartmentId).ToList(),
        };
        await FillAsync(model, ct);
        return PageOrPartial("UnitForm", model);
    }

    [HttpPost("org/units/create"), HttpPost("org/units/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveUnit(int? id, OrgUnitFormModel model, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await org.SaveUnitAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return Saved($"Unit \"{model.Name.Trim()}\" was saved.", Tab(PositionsManagementViewModel.UnitsTab));
            AddErrors(result);
        }
        await FillAsync(model, ct);
        return FormView("UnitForm", model);
    }

    private async Task FillAsync(OrgUnitFormModel model, CancellationToken ct)
    {
        model.Tiers = (await org.StructureAsync(true, ct)).Tiers;
        model.Departments = await org.DepartmentOptionsAsync(ct);
    }

    // ================================================================== positions

    [HttpGet("org/positions/create")]
    public async Task<IActionResult> CreatePosition(int? unit, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        return PageOrPartial("PositionForm", new StaffPositionFormModel { UnitId = unit ?? 0, SortOrder = 1, Tiers = (await org.StructureAsync(true, ct)).Tiers });
    }

    [HttpGet("org/positions/{id:int}/edit")]
    public async Task<IActionResult> EditPosition(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var p = await org.PositionAsync(id, ct);
        if (p is null) return NotFoundResult("This position no longer exists.");
        return PageOrPartial("PositionForm", new StaffPositionFormModel
        {
            Id = p.Id, Title = p.Title, Description = p.Description, UnitId = p.OrgUnitId, RoleType = p.RoleType, GradeRange = p.GradeRange,
            SortOrder = p.SortOrder, IsActive = p.IsActive, HasHistory = await db.StaffPositionAssignments.AnyAsync(a => a.StaffPositionId == id, ct),
            Tiers = (await org.StructureAsync(true, ct)).Tiers,
        });
    }

    [HttpPost("org/positions/create"), HttpPost("org/positions/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePosition(int? id, StaffPositionFormModel model, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await org.SavePositionAsync(id, model.ToInput(), ct);
            if (result.Succeeded) return Saved($"Position \"{OrgLabels.PositionTitle(model.Title.Trim(), model.GradeRange?.Trim())}\" was saved.", Tab(PositionsManagementViewModel.PositionsTab));
            AddErrors(result);
        }
        model.Tiers = (await org.StructureAsync(true, ct)).Tiers;
        return FormView("PositionForm", model);
    }

    [HttpPost("org/positions/{id:int}/delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePosition(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var result = await org.DeletePositionAsync(id, ct);
        return Done(result, "The position was deleted.", Tab(PositionsManagementViewModel.PositionsTab));
    }

    // ================================================================== assignments

    [HttpGet("org/assignments/create")]
    public async Task<IActionResult> Assign(int? position, int? staff, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var model = new AssignmentFormModel { PositionId = position ?? 0, StaffMemberId = staff ?? 0, StartDate = SchoolWeek.Today };
        await FillAsync(model, ct);
        return PageOrPartial("AssignmentForm", model);
    }

    [HttpGet("org/assignments/{id:int}/edit")]
    public async Task<IActionResult> EditAssignment(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var a = await org.AssignmentAsync(id, ct);
        if (a is null) return NotFoundResult("This assignment no longer exists.");
        if (!a.IsCurrent) return NotFoundResult("This assignment has ended; ended assignments are kept unchanged as history.");
        var model = new AssignmentFormModel
        {
            Id = a.Id, StaffMemberId = a.StaffId, PositionId = a.PositionId, GradeRange = a.GradeRange, Scope = a.Scope,
            ReportsToStaffMemberId = a.ReportsToId, IsPrimary = a.IsPrimary, StartDate = a.StartDate, Notes = a.Notes, Current = a,
        };
        await FillAsync(model, ct);
        return PageOrPartial("AssignmentForm", model);
    }

    [HttpPost("org/assignments/create"), HttpPost("org/assignments/{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAssignment(int? id, AssignmentFormModel model, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        model.Id = id;
        if (id is { } existing)
        {
            model.Current = await org.AssignmentAsync(existing, ct);
            if (model.Current is null) return NotFoundResult("This assignment no longer exists.");
            model.StaffMemberId = model.Current.StaffId;
            ModelState.Remove(nameof(model.StaffMemberId));
        }
        if (ModelState.IsValid)
        {
            var input = model.ToInput();
            var result = id is null ? await org.AssignAsync(input, ct) : await org.UpdateAssignmentAsync(id.Value, input, ct);
            if (result.Succeeded)
            {
                var moved = id is not null && model.Current!.PositionId != model.PositionId;
                return Saved(id is null ? "The position was assigned." : moved ? "The position was changed; the previous one is kept in the history." : "The assignment was updated.",
                    Tab(PositionsManagementViewModel.AssignmentsTab));
            }
            AddErrors(result);
        }
        await FillAsync(model, ct);
        return FormView("AssignmentForm", model);
    }

    [HttpGet("org/assignments/{id:int}/end")]
    public async Task<IActionResult> EndAssignment(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        var a = await org.AssignmentAsync(id, ct);
        if (a is null || !a.IsCurrent) return NotFoundResult("This assignment has already ended.");
        return PageOrPartial("EndAssignmentForm", new EndAssignmentFormModel { Id = id, EndDate = SchoolWeek.Today, Current = a });
    }

    [HttpPost("org/assignments/{id:int}/end"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EndAssignment(int id, EndAssignmentFormModel model, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        model.Id = id;
        if (ModelState.IsValid)
        {
            var result = await org.EndAssignmentAsync(id, model.EndDate, model.Reason, ct);
            if (result.Succeeded) return Saved("The assignment was ended and kept in the history.", Tab(PositionsManagementViewModel.AssignmentsTab));
            AddErrors(result);
        }
        model.Current = await org.AssignmentAsync(id, ct);
        return FormView("EndAssignmentForm", model);
    }

    [HttpPost("org/assignments/{id:int}/primary"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrimary(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        return Done(await org.SetPrimaryAsync(id, ct), "The primary position was changed.", Tab(PositionsManagementViewModel.AssignmentsTab));
    }

    [HttpPost("org/flags/{id:int}/resolve"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveFlag(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(ct)) return Denied();
        return Done(await org.ResolveFlagAsync(id, ct), "The warning was marked as resolved.", Tab(PositionsManagementViewModel.ReviewTab));
    }

    private async Task FillAsync(AssignmentFormModel model, CancellationToken ct)
    {
        model.Staff = await org.StaffOptionsAsync(ct);
        model.Tiers = (await org.StructureAsync(false, ct)).Tiers;
    }

    // ================================================================== helpers

    private static string Tab(string tab) => $"{OrgLabels.PositionsHref}?tab={tab}";

    /// <summary>403: the change needs organizational-structure access (checked on the server for every change).</summary>
    private IActionResult Denied()
    {
        if (IsAjax) return AjaxFailure(OrgStructureAccess.DeniedMessage, StatusCodes.Status403Forbidden);
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("Denied");
    }

    private IActionResult Saved(string message, string redirect)
    {
        if (IsAjax) return AjaxSuccess(message);
        Toast(message);
        return Redirect(redirect);
    }

    private IActionResult Done(Common.ServiceResult result, string message, string redirect)
    {
        var text = result.Succeeded ? message : result.Errors[0].Value;
        if (IsAjax) return result.Succeeded ? AjaxSuccess(text) : AjaxFailure(text, StatusCodes.Status409Conflict);
        Toast(text, result.Succeeded ? "success" : "error");
        return Redirect(redirect);
    }
}
