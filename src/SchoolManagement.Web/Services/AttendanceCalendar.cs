using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record HolidayView(int Id, string Name, DateOnly Start, DateOnly End, string? CreatedBy)
{
    public string Dates => Start == End
        ? Start.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)
        : $"{Start.ToString("d MMM", CultureInfo.InvariantCulture)} – {End.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>Sunday–Thursday days inside the range.</summary>
    public int SchoolDays
    {
        get
        {
            var n = 0;
            for (var d = Start; d <= End; d = d.AddDays(1)) if (ScheduleRules.IsSchoolDay(d.DayOfWeek)) n++;
            return n;
        }
    }
}

/// <summary>
/// Which dates are school days for attendance: Sunday–Thursday, outside every school holiday.
/// Loaded once per operation (holidays are few) and then checked in memory.
/// </summary>
public sealed class SchoolDays
{
    private readonly IReadOnlyList<HolidayView> _holidays;

    private SchoolDays(IReadOnlyList<HolidayView> holidays) => _holidays = holidays;

    public static async Task<SchoolDays> LoadAsync(SchoolDbContext db, CancellationToken ct) =>
        new(await db.SchoolHolidays.AsNoTracking().OrderBy(h => h.StartDate)
            .Select(h => new HolidayView(h.Id, h.Name, h.StartDate, h.EndDate, h.CreatedBy)).ToListAsync(ct));

    public HolidayView? HolidayOn(DateOnly date) => _holidays.FirstOrDefault(h => h.Start <= date && date <= h.End);

    public bool IsSchoolDay(DateOnly date) => ScheduleRules.IsSchoolDay(date.DayOfWeek) && HolidayOn(date) is null;

    /// <summary>The school day before <paramref name="date"/> (skips weekends and holidays).</summary>
    public DateOnly Previous(DateOnly date)
    {
        var d = date.AddDays(-1);
        for (var i = 0; i < 400 && !IsSchoolDay(d); i++) d = d.AddDays(-1);
        return d;
    }

    /// <summary>The school day after <paramref name="date"/> (skips weekends and holidays).</summary>
    public DateOnly Next(DateOnly date)
    {
        var d = date.AddDays(1);
        for (var i = 0; i < 400 && !IsSchoolDay(d); i++) d = d.AddDays(1);
        return d;
    }
}

public interface IAttendanceCalendarService
{
    Task<IReadOnlyList<HolidayView>> HolidaysAsync(CancellationToken ct = default);
    Task<ServiceResult> AddHolidayAsync(string? name, DateOnly? start, DateOnly? end, CancellationToken ct = default);
    Task<ServiceResult> RemoveHolidayAsync(int id, CancellationToken ct = default);
}

/// <summary>School holidays. Adding or removing one re-checks the absence alerts around it.</summary>
public sealed class AttendanceCalendarService(SchoolDbContext db, ICurrentUser currentUser, IAttendanceAlertService alerts) : IAttendanceCalendarService
{
    public async Task<IReadOnlyList<HolidayView>> HolidaysAsync(CancellationToken ct = default) =>
        await db.SchoolHolidays.AsNoTracking().OrderByDescending(h => h.StartDate)
            .Select(h => new HolidayView(h.Id, h.Name, h.StartDate, h.EndDate, h.CreatedBy)).ToListAsync(ct);

    public async Task<ServiceResult> AddHolidayAsync(string? name, DateOnly? start, DateOnly? end, CancellationToken ct = default)
    {
        var clean = TextNormalizer.CleanText(name);
        var result = ServiceResult.Ok();
        if (clean.Length == 0) result.AddError("Name", "Enter the holiday's name.");
        else if (clean.Length > SchoolHoliday.NameMaxLength) result.AddError("Name", $"Use {SchoolHoliday.NameMaxLength} characters or fewer.");
        if (start is null) result.AddError("StartDate", "Enter the first day of the holiday.");
        var last = end ?? start;
        if (start is { } s && last is { } e && e < s) result.AddError("EndDate", "The last day cannot be before the first day.");
        if (start is { } s2 && last is { } e2 && e2.DayNumber - s2.DayNumber > 120) result.AddError("EndDate", "A holiday can be at most 120 days long.");
        if (!result.Succeeded) return result;

        var overlap = await db.SchoolHolidays.AsNoTracking().Where(h => h.StartDate <= last!.Value && start!.Value <= h.EndDate)
            .Select(h => h.Name).FirstOrDefaultAsync(ct);
        if (overlap is not null) return ServiceResult.Fail("StartDate", $"These dates overlap the holiday “{overlap}”.");

        var holiday = new SchoolHoliday { Name = clean, StartDate = start!.Value, EndDate = last!.Value, CreatedBy = currentUser.Name };
        db.SchoolHolidays.Add(holiday);
        await db.SaveChangesAsync(ct);
        await alerts.RecheckAsync(holiday.StartDate.AddDays(-30), holiday.EndDate.AddDays(30), ct);
        return ServiceResult.Ok(holiday.Id);
    }

    public async Task<ServiceResult> RemoveHolidayAsync(int id, CancellationToken ct = default)
    {
        var holiday = await db.SchoolHolidays.FirstOrDefaultAsync(h => h.Id == id, ct);
        if (holiday is null) return ServiceResult.Fail("", "This holiday no longer exists.");
        db.SchoolHolidays.Remove(holiday);
        await db.SaveChangesAsync(ct);
        await alerts.RecheckAsync(holiday.StartDate.AddDays(-30), holiday.EndDate.AddDays(30), ct);
        return ServiceResult.Ok(id);
    }
}
