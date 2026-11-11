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

    /// <summary>The school arrival deadline used to mark students On Time or Late.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ArrivalDeadline(string? deadline, CancellationToken ct)
    {
        var time = ArrivalForm.ParseTime(deadline);
        var result = await settings.SaveArrivalDeadlineAsync(time, ct);
        Toast(result.Succeeded
            ? $"Arrival deadline set to {time:HH\\:mm}. Arrivals recorded from now on use it; existing records keep the deadline they were recorded with."
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
        MaxWeeklyPeriods = await settings.GetMaxWeeklyPeriodsAsync(ct),
        Staff = await duties.StaffOptionsAsync(ct),
        OperatorStaffId = currentUser.StaffId,
        OperatorVerified = currentUser.IsVerified,
    };
}
