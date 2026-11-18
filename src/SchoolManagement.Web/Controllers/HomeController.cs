using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

public class HomeController(
    IDashboardService dashboard,
    ISettingsService settings,
    IDutyService duties,
    IArrivalService arrivals,
    IStaffAttendanceService staffAttendance,
    IActivityService activities) : AppController
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var today = SchoolWeek.Today;
        // Section-level coverage for today (school timezone), read per request: fully covered sections ÷ sections.
        // Morning / Midday / Dismissal = the duty cards of each type (DutyComponents), each covered when every duty in it
        // dated today is Covered; Activities = each activity scheduled today (P.E. counts once), covered when every one
        // of its sessions today is Covered.
        var byType = await duties.SectionCoverageAsync(today, ct);
        var coverage = DutyComponents.Types
            .Select(t => new DutyCoverage(DutyComponents.TypeName(t).Replace(" Duty", ""),
                $"/duties?type={DutyComponents.TypeSlug(t)}&date={SchoolWeek.Iso(today)}", byType[t], "section", "sections"))
            .ToList();
        var sessions = await activities.SessionsOnAsync(today, ct);
        coverage.Add(new DutyCoverage("Activities", $"/duties/activities?date={SchoolWeek.Iso(today)}#sessions", sessions.Sections, "activity", "activities"));
        var model = new DashboardViewModel
        {
            Data = await dashboard.GetAsync(ct),
            Profile = await settings.GetProfileAsync(ct),
            // Aggregates only: no individual feedback or student details on the general dashboard.
            Operations = new OperationsIndicators
            {
                Date = today,
                DutyStatus = await duties.StatusCountsAsync(today, ct),
                Arrivals = await arrivals.DaySummaryAsync(today, ct),
                Staff = await staffAttendance.TodayAsync(today, ct),
                Coverage = coverage,
            },
        };
        return View(model);
    }

    [Route("/error/{code:int?}")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? code)
    {
        ViewData["Code"] = code ?? 500;
        ViewData["RequestId"] = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        return View();
    }
}
