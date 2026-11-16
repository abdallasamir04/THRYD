using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

public class SettingsController(ISettingsService settings, IDutyService duties, ICurrentUser currentUser) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var profile = await settings.GetProfileAsync(ct);
        return View(await BuildAsync(new SchoolProfileForm { SchoolName = profile.SchoolName, AcademicYear = profile.AcademicYear }, ct));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index([Bind(Prefix = "Profile")] SchoolProfileForm profile, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await settings.SaveProfileAsync(profile.SchoolName, profile.AcademicYear, ct);
            if (result.Succeeded)
            {
                Toast("School profile saved.");
                return RedirectToAction(nameof(Index));
            }
            foreach (var (field, message) in result.Errors) ModelState.AddModelError($"Profile.{field}", message);
        }

        return View(await BuildAsync(profile, ct));
    }

    /// <summary>School Attendance times: the school start time (later = Late) and the cutoff (registration closes; no arrival = Absent).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ArrivalDeadline(string? deadline, string? cutoff, CancellationToken ct)
    {
        var start = ArrivalForm.ParseTime(deadline);
        var last = ArrivalForm.ParseTime(cutoff) ?? await settings.GetLateCutoffAsync(ct);
        // Save in the order that keeps start < cutoff valid at every step.
        var current = await settings.GetLateCutoffAsync(ct);
        Common.ServiceResult result;
        if (last > current) { result = await settings.SaveLateCutoffAsync(last, ct); if (result.Succeeded) result = await settings.SaveArrivalDeadlineAsync(start, ct); }
        else { result = await settings.SaveArrivalDeadlineAsync(start, ct); if (result.Succeeded) result = await settings.SaveLateCutoffAsync(last, ct); }
        Toast(result.Succeeded
            ? $"Present until {Arrivals.Time(start)}, Late after; registration closes at {Arrivals.Time(last)}. Existing records keep the status they were saved with."
            : result.Errors[0].Value, result.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> WorkloadLimit(int maxWeeklyPeriods, CancellationToken ct)
    {
        var result = await settings.SaveMaxWeeklyPeriodsAsync(maxWeeklyPeriods, ct);
        Toast(result.Succeeded ? $"Maximum weekly workload set to {maxWeeklyPeriods} periods." : result.Errors[0].Value, result.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Index));
    }

    private async Task<SettingsViewModel> BuildAsync(SchoolProfileForm profile, CancellationToken ct) => new()
    {
        Profile = profile,
        System = await settings.GetSystemInfoAsync(ct),
        ArrivalDeadline = await settings.GetArrivalDeadlineAsync(ct),
        LateCutoff = await settings.GetLateCutoffAsync(ct),
        MaxWeeklyPeriods = await settings.GetMaxWeeklyPeriodsAsync(ct),
        Staff = await duties.StaffOptionsAsync(ct),
        OperatorStaffId = currentUser.StaffId,
        OperatorVerified = currentUser.IsVerified,
    };
}
