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
    int Classes,
    int StudentsInInactiveClasses = 0);

public interface ISettingsService
{
    Task<SchoolProfile> GetProfileAsync(CancellationToken ct = default);
    Task<ServiceResult> SaveProfileAsync(string schoolName, string? academicYear, CancellationToken ct = default);
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);

    /// <summary>The school start time: arriving after it is Late. Null until set in Settings.</summary>
    Task<TimeOnly?> GetArrivalDeadlineAsync(CancellationToken ct = default);
    Task<ServiceResult> SaveArrivalDeadlineAsync(TimeOnly? deadline, CancellationToken ct = default);

    /// <summary>The arrival cutoff (default 09:30, Africa/Cairo): registration closes at it; students with no arrival by then are Absent.</summary>
    Task<TimeOnly> GetLateCutoffAsync(CancellationToken ct = default);
    Task<ServiceResult> SaveLateCutoffAsync(TimeOnly? cutoff, CancellationToken ct = default);

    /// <summary>The first day that is finalized automatically (set on first use; earlier days are left as recorded).</summary>
    Task<DateOnly> GetAutoFinalizeFromAsync(CancellationToken ct = default);

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
            await db.Classes.CountAsync(ct),
            // Data check (administrative correction, not an attendance category): every student should be in an active class.
            await db.Students.CountAsync(st => !st.SchoolClass.IsActive, ct));
    }

    private const string ArrivalDeadlineKey = "Arrival.Deadline";
    public const string LateCutoffKey = "Arrival.LateCutoff";
    private const string AutoFinalizeFromKey = "Arrival.AutoFinalizeFrom";

    /// <summary>The latest arrival that still counts as Late (later = Absent) when not configured.</summary>
    public static readonly TimeOnly DefaultLateCutoff = new(9, 30);

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private async Task<string?> ValueAsync(string key, CancellationToken ct) =>
        await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);

    private static TimeOnly? Time(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", Inv, System.Globalization.DateTimeStyles.None, out var t) ? t : null;

    public async Task<TimeOnly?> GetArrivalDeadlineAsync(CancellationToken ct = default) => Time(await ValueAsync(ArrivalDeadlineKey, ct));

    public async Task<ServiceResult> SaveArrivalDeadlineAsync(TimeOnly? deadline, CancellationToken ct = default)
    {
        if (deadline is null) return ServiceResult.Fail("ArrivalDeadline", "Enter the school start time.");
        var cutoff = await GetLateCutoffAsync(ct);
        if (deadline.Value >= cutoff)
            return ServiceResult.Fail("ArrivalDeadline", $"The school start time must be earlier than the late cutoff ({cutoff.ToString("HH:mm", Inv)}).");
        await UpsertAsync(ArrivalDeadlineKey, deadline.Value.ToString("HH:mm", Inv), ct);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<TimeOnly> GetLateCutoffAsync(CancellationToken ct = default) => Time(await ValueAsync(LateCutoffKey, ct)) ?? DefaultLateCutoff;

    public async Task<ServiceResult> SaveLateCutoffAsync(TimeOnly? cutoff, CancellationToken ct = default)
    {
        if (cutoff is null) return ServiceResult.Fail("LateCutoff", "Enter the time arrival registration closes.");
        if (await GetArrivalDeadlineAsync(ct) is { } start && cutoff.Value <= start)
            return ServiceResult.Fail("LateCutoff", $"Registration must close after the school start time ({start.ToString("HH:mm", Inv)}).");
        await UpsertAsync(LateCutoffKey, cutoff.Value.ToString("HH:mm", Inv), ct);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<DateOnly> GetAutoFinalizeFromAsync(CancellationToken ct = default)
    {
        if (DateOnly.TryParseExact(await ValueAsync(AutoFinalizeFromKey, ct), "yyyy-MM-dd", Inv, System.Globalization.DateTimeStyles.None, out var d)) return d;
        // First use: automatic finalization starts today; earlier days are history and are only finalized on request.
        var today = SchoolWeek.Today;
        await UpsertAsync(AutoFinalizeFromKey, today.ToString("yyyy-MM-dd", Inv), ct);
        await db.SaveChangesAsync(ct);
        return today;
    }

    private async Task UpsertAsync(string key, string? value, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }
}
