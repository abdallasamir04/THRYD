using System.Globalization;

namespace SchoolManagement.Web.Common;

/// <summary>
/// The school's single time reference: Egyptian local time (IANA <c>Africa/Cairo</c>, Windows <c>Egypt Standard Time</c>),
/// whatever time zone the server or the browser runs in. Daylight saving (UTC+02:00 in winter, UTC+03:00 in summer) comes
/// from the operating system's time zone database through <see cref="TimeZoneInfo"/> — no offset or transition date is
/// written in code.
/// <para>
/// Storage rules used across the application: real-world instants (CreatedAt, CorrectedAt, audit and incident "At"
/// fields) are stored in UTC and converted with <see cref="ToLocal"/> for display; school dates are <see cref="DateOnly"/>
/// and period / duty / due times are <see cref="TimeOnly"/> wall-clock values in school time, never converted.
/// </para>
/// </summary>
public interface ISchoolTimeService
{
    TimeZoneInfo TimeZone { get; }

    /// <summary>The configured IANA id (Africa/Cairo); browsers use it to display the same time.</summary>
    string TimeZoneId { get; }

    /// <summary>"Cairo, Egypt".</summary>
    string Location { get; }

    /// <summary>True when the school time zone could not be found and the server's own zone is used (logged as an error).</summary>
    bool IsFallback { get; }

    /// <summary>Current instant, <see cref="DateTimeKind.Utc"/>.</summary>
    DateTime UtcNow { get; }

    /// <summary>Current school-local wall time (<see cref="DateTimeKind.Unspecified"/>).</summary>
    DateTime Now { get; }

    DateOnly Today { get; }
    TimeOnly TimeOfDay { get; }

    /// <summary>The school's UTC offset at the given instant (now when null), e.g. +02:00 in winter, +03:00 in summer.</summary>
    TimeSpan OffsetAt(DateTime? utc = null);

    /// <summary>"UTC+02:00".</summary>
    string OffsetLabel(DateTime? utc = null);

    /// <summary>A stored UTC instant as school-local wall time (an Unspecified kind is read as UTC, as EF returns it).</summary>
    DateTime ToLocal(DateTime utc);

    /// <summary>A school-local wall time as UTC. A time skipped by the spring-forward change moves to the first valid time.</summary>
    DateTime ToUtc(DateTime local);

    DateTime ToUtc(DateOnly date, TimeOnly time);

    /// <summary>The school date of a stored UTC instant.</summary>
    DateOnly LocalDate(DateTime utc);

    /// <summary>The first instant (UTC) of a school date.</summary>
    DateTime StartOfDayUtc(DateOnly date);

    /// <summary>A weekday the school works (Sunday–Thursday; holidays are checked separately with <c>SchoolDays</c>).</summary>
    bool IsSchoolWeekday(DateOnly date);

    /// <summary>Today is a school weekday and the current school time is within [start, end].</summary>
    bool IsWithinSchoolDay(TimeOnly start, TimeOnly end);

    /// <summary>The school-local moment (date + optional time; a date alone ends at midnight) has passed.</summary>
    bool HasPassed(DateOnly date, TimeOnly? time = null);
}

public sealed class SchoolTimeService : ISchoolTimeService
{
    public const string DefaultTimeZoneId = "Africa/Cairo";
    public const string WindowsTimeZoneId = "Egypt Standard Time";
    public const string DefaultLocation = "Cairo, Egypt";

    private readonly TimeProvider _clock;

    public SchoolTimeService(TimeProvider clock, string timeZoneId = DefaultTimeZoneId, string location = DefaultLocation, Action<string>? logError = null)
    {
        _clock = clock;
        TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim();
        Location = string.IsNullOrWhiteSpace(location) ? DefaultLocation : location.Trim();
        (TimeZone, IsFallback) = Resolve(TimeZoneId, logError);
    }

    /// <summary>From configuration (School:TimeZone, School:Location), defaulting to Africa/Cairo and "Cairo, Egypt".</summary>
    public static SchoolTimeService FromConfiguration(IConfiguration config, TimeProvider clock, Action<string>? logError = null) =>
        new(clock, config["School:TimeZone"] ?? DefaultTimeZoneId, config["School:Location"] ?? DefaultLocation, logError);

    public TimeZoneInfo TimeZone { get; }
    public string TimeZoneId { get; }
    public string Location { get; }
    public bool IsFallback { get; }

    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    public DateTime Now => ToLocal(UtcNow);
    public DateOnly Today => DateOnly.FromDateTime(Now);
    public TimeOnly TimeOfDay => TimeOnly.FromDateTime(Now);

    public TimeSpan OffsetAt(DateTime? utc = null) => TimeZone.GetUtcOffset(AsUtc(utc ?? UtcNow));

    public string OffsetLabel(DateTime? utc = null)
    {
        var o = OffsetAt(utc);
        return $"UTC{(o < TimeSpan.Zero ? "-" : "+")}{o.Duration():hh\\:mm}";
    }

    public DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), TimeZone), DateTimeKind.Unspecified);

    public DateTime ToUtc(DateTime local)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Spring forward: the skipped hour does not exist; use the first valid minute after it.
        for (var i = 0; i < 180 && TimeZone.IsInvalidTime(wall); i++) wall = wall.AddMinutes(1);
        // Fall back: an hour happens twice; take its first occurrence (the larger offset).
        if (TimeZone.IsAmbiguousTime(wall))
            return DateTime.SpecifyKind(wall - TimeZone.GetAmbiguousTimeOffsets(wall).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(wall, TimeZone);
    }

    public DateTime ToUtc(DateOnly date, TimeOnly time) => ToUtc(date.ToDateTime(time));

    public DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    public DateTime StartOfDayUtc(DateOnly date) => ToUtc(date, TimeOnly.MinValue);

    public bool IsSchoolWeekday(DateOnly date) => ScheduleRules.IsSchoolDay(date.DayOfWeek);

    public bool IsWithinSchoolDay(TimeOnly start, TimeOnly end)
    {
        var now = TimeOfDay;
        return IsSchoolWeekday(Today) && now >= start && now <= end;
    }

    public bool HasPassed(DateOnly date, TimeOnly? time = null)
    {
        var today = Today;
        if (date != today) return date < today;
        return time is { } t && t < TimeOfDay;
    }

    /// <summary>EF returns stored instants as Unspecified: they are UTC. A Local value (rare) is converted first.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// The configured zone from the OS database: IANA ids work on Linux and on Windows with ICU; otherwise the Windows id
    /// is tried (Egypt Standard Time). If nothing is found the server's zone is used and an error is logged — never a fixed offset.
    /// </summary>
    private static (TimeZoneInfo Zone, bool Fallback) Resolve(string id, Action<string>? logError)
    {
        var candidates = new List<string> { id };
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows)) candidates.Add(windows);
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) candidates.Add(iana);
        if (id == DefaultTimeZoneId) candidates.Add(WindowsTimeZoneId);
        foreach (var candidate in candidates.Distinct())
        {
            try { return (TimeZoneInfo.FindSystemTimeZoneById(candidate), false); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        logError?.Invoke($"School time zone \"{id}\" was not found on this server (tried {string.Join(", ", candidates.Distinct())}). " +
                         $"Using the server's time zone \"{TimeZoneInfo.Local.Id}\" until the time zone data is installed.");
        return (TimeZoneInfo.Local, true);
    }
}

/// <summary>
/// Static access to the same <see cref="ISchoolTimeService"/> instance for static helpers and views (SchoolWeek.Today,
/// Ui.DateTime…). Program.cs sets it to the DI singleton at startup; services take <see cref="ISchoolTimeService"/>
/// by injection.
/// </summary>
public static class SchoolTime
{
    private static ISchoolTimeService? _current;

    public static ISchoolTimeService Current
    {
        get => _current ??= new SchoolTimeService(TimeProvider.System);
        set => _current = value;
    }

    public static DateTime Now => Current.Now;
    public static DateOnly Today => Current.Today;

    /// <summary>"Sunday, 15 November 2026".</summary>
    public static string LongDate(DateTime local) => local.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"07:27:45 PM".</summary>
    public static string Clock(DateTime local) => local.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture);
}
