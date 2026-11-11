using System.Globalization;

namespace SchoolManagement.Web.Common;

/// <summary>
/// The school week used for workload: it starts on Sunday (the Egyptian school week runs
/// Sunday–Thursday). Every workload row stores the week's first day, so any date entered by a
/// user is normalised with <see cref="StartOf"/> before it is saved or compared.
/// </summary>
public static class SchoolWeek
{
    public const DayOfWeek FirstDay = DayOfWeek.Sunday;
    public const int SchoolDays = 5;

    public static DateOnly StartOf(DateOnly date)
    {
        var diff = ((int)date.DayOfWeek - (int)FirstDay + 7) % 7;
        return date.AddDays(-diff);
    }

    public static DateOnly Current => StartOf(Today);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static DateOnly LastSchoolDay(DateOnly weekStart) => weekStart.AddDays(SchoolDays - 1);

    /// <summary>"8–12 Nov 2026"</summary>
    public static string Label(DateOnly weekStart)
    {
        var end = LastSchoolDay(weekStart);
        var c = CultureInfo.InvariantCulture;
        return weekStart.Month == end.Month
            ? $"{weekStart.Day}–{end.ToString("d MMM yyyy", c)}"
            : $"{weekStart.ToString("d MMM", c)} – {end.ToString("d MMM yyyy", c)}";
    }

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ParseOrCurrent(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? StartOf(d) : Current;

    public static DateOnly ParseDayOrToday(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Today;
}
