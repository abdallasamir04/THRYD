using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Accounts;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// "My dashboard": the signed-in person's own profile, tasks, duties, schedule, responsibilities and PTC — only theirs, from
/// the account's linked staff record (never a request parameter). Sections without data for the person are not shown.
/// </summary>
[Route("me")]
[StaffAccess(StaffAccessRule.Self)]
public class MeController(ICurrentUser currentUser, IPeopleDirectoryService people, IStaffAccessEvaluator access) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (currentUser.StaffId is not { } me)
        {
            // The dedicated Super Admin account has no personal record: its home is the whole application.
            if (currentUser.IsSuperAdmin) return Redirect("/staff/accounts");
            return View("NoPerson");
        }
        var dir = await people.BuildAsync(me, ct);
        var person = dir.Person(me);
        if (person is null) return View("NoPerson");
        var s = await access.SummaryAsync(ct);
        var modules = new List<string>();
        if (s.FloorTeam) modules.Add("Floor Management");
        if (s.DutyAudit) modules.Add("Duties & Duty Audit");
        if (s.OrgStructure) modules.Add("Management & Organizational Structure");
        if (s.StudentRecords) modules.Add("Students & Classes");
        return View(new PersonDashboardModel { Directory = dir, Person = person, Account = dir.AccountOf(me), Modules = modules });
    }
}
