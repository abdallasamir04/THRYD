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
    IStaffDutyService staffDuty,
    IArrivalService arrivals) : AppController
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var today = SchoolWeek.Today;
        var (average, rated) = await staffDuty.AverageRatingAsync(ct);
        var model = new DashboardViewModel
        {
            Data = await dashboard.GetAsync(ct),
            Profile = await settings.GetProfileAsync(ct),
            // Aggregates only: no individual feedback or student details on the general dashboard.
            Operations = new OperationsIndicators
            {
                Date = today,
                DutyStatus = await duties.StatusCountsAsync(today, ct),
                AverageRating = average,
                RatedAssignments = rated,
                Arrivals = await arrivals.DaySummaryAsync(today, ct),
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
