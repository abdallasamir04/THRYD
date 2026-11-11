using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record SchoolProfile(string SchoolName, string? AcademicYear);

public sealed record SystemInfo(
    bool CanConnect,
    string? Server,
    string? Database,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PendingMigrations,
    int Students,
    int Teachers,
    int Classes);

public interface ISettingsService
{
    Task<SchoolProfile> GetProfileAsync(CancellationToken ct = default);
    Task<ServiceResult> SaveProfileAsync(string schoolName, string? academicYear, CancellationToken ct = default);
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);

    /// <summary>The school arrival deadline: arriving after it is Late. Null until set in Settings.</summary>
    Task<TimeOnly?> GetArrivalDeadlineAsync(CancellationToken ct = default);
    Task<ServiceResult> SaveArrivalDeadlineAsync(TimeOnly? deadline, CancellationToken ct = default);

    Task<int> GetMaxWeeklyPeriodsAsync(CancellationToken ct = default);
    Task<ServiceResult> SaveMaxWeeklyPeriodsAsync(int max, CancellationToken ct = default);
}

public sealed class SettingsService(SchoolDbContext db, IMemoryCache cache) : ISettingsService
{
    public const string DefaultSchoolName = "School Management";
    private const string SchoolNameKey = "School.Name";
    private const string AcademicYearKey = "School.AcademicYear";
    private const string CacheKey = "settings:profile";

    public async Task<SchoolProfile> GetProfileAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out SchoolProfile? cached) && cached is not null) return cached;

        var values = await db.Settings.AsNoTracking()
            .Where(s => s.Key == SchoolNameKey || s.Key == AcademicYearKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var name = values.GetValueOrDefault(SchoolNameKey);
        var profile = new SchoolProfile(
            string.IsNullOrWhiteSpace(name) ? DefaultSchoolName : name,
            values.GetValueOrDefault(AcademicYearKey));
        cache.Set(CacheKey, profile, TimeSpan.FromMinutes(10));
        return profile;
    }

    public async Task<ServiceResult> SaveProfileAsync(string schoolName, string? academicYear, CancellationToken ct = default)
    {
        var name = TextNormalizer.CleanText(schoolName);
        var year = TextNormalizer.CleanText(academicYear);
        if (name.Length == 0) return ServiceResult.Fail("SchoolName", "Enter the school name.");
        if (name.Length > 120) return ServiceResult.Fail("SchoolName", "Use 120 characters or fewer.");
        if (year.Length > 20) return ServiceResult.Fail("AcademicYear", "Use 20 characters or fewer.");

        await UpsertAsync(SchoolNameKey, name, ct);
        await UpsertAsync(AcademicYearKey, year.Length == 0 ? null : year, ct);
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        return ServiceResult.Ok();
    }

    public const int DefaultMaxWeeklyPeriods = 24;
    public const string MaxWeeklyPeriodsKey = "Workload.MaxWeeklyPeriods";
    private const string MaxPeriodsCacheKey = "settings:max_workload";

    public async Task<int> GetMaxWeeklyPeriodsAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(MaxPeriodsCacheKey, out int cached) && cached > 0) return cached;
        var val = await db.Settings.AsNoTracking().Where(s => s.Key == MaxWeeklyPeriodsKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var max = int.TryParse(val, out var parsed) && parsed > 0 ? parsed : DefaultMaxWeeklyPeriods;
        cache.Set(MaxPeriodsCacheKey, max, TimeSpan.FromMinutes(10));
        return max;
    }

    public async Task<ServiceResult> SaveMaxWeeklyPeriodsAsync(int max, CancellationToken ct = default)
    {
        // 24 is a hard ceiling (also enforced by a database trigger); the setting can only lower it.
        if (max < 1 || max > TeacherWorkloadAssignment.MaxWeeklyPeriods)
            return ServiceResult.Fail("MaxWeeklyPeriods", $"Enter a workload maximum between 1 and {TeacherWorkloadAssignment.MaxWeeklyPeriods}.");
        await UpsertAsync(MaxWeeklyPeriodsKey, max.ToString(), ct);
        await db.SaveChangesAsync(ct);
        cache.Remove(MaxPeriodsCacheKey);
        return ServiceResult.Ok();
    }

    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        var canConnect = await db.Database.CanConnectAsync(ct);
        if (!canConnect)
            return new SystemInfo(false, connection.DataSource, connection.Database, [], [], 0, 0, 0);

        return new SystemInfo(
            true,
            connection.DataSource,
            connection.Database,
            (await db.Database.GetAppliedMigrationsAsync(ct)).ToList(),
            (await db.Database.GetPendingMigrationsAsync(ct)).ToList(),
            await db.Students.CountAsync(ct),
            await db.Teachers.CountAsync(ct),
            await db.Classes.CountAsync(ct));
    }

    private const string ArrivalDeadlineKey = "Arrival.Deadline";

    public async Task<TimeOnly?> GetArrivalDeadlineAsync(CancellationToken ct = default)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == ArrivalDeadlineKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
    }

    public async Task<ServiceResult> SaveArrivalDeadlineAsync(TimeOnly? deadline, CancellationToken ct = default)
    {
        if (deadline is null) return ServiceResult.Fail("ArrivalDeadline", "Enter the arrival deadline.");
        await UpsertAsync(ArrivalDeadlineKey, deadline.Value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    private async Task UpsertAsync(string key, string? value, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }
}
