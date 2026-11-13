using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Timetables;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Academics → Timetables: one place to find and download every timetable of the school (all registered
/// <see cref="ITimetableSource"/>s), one at a time, by group, by category or the whole school as a ZIP.
/// The existing timetable pages are unchanged; this center only reads their data.
/// </summary>
[Route("timetables")]
public class TimetablesController(ITimetableCenter center, IBelongingsService calendar) : AppController
{
    /// <summary>At most this many weeks in one date range.</summary>
    public const int MaxWeeks = 20;

    [HttpGet("")]
    public async Task<IActionResult> Index(string? category, int? yearId, string? from, string? to, string? day,
        int? departmentId, string? subject, int? teacherId, int? classId, CancellationToken ct)
    {
        var (request, years, year, note) = await RequestAsync(category, yearId, from, to, day, departmentId, subject, teacherId, classId, ct);
        var source = center.Source(request.Category) ?? center.Sources[0];
        request = request with { Category = source.Key };
        var counts = new Dictionary<string, int>();
        foreach (var s in center.Sources) counts[s.Key] = (await s.ItemsAsync(new TimetableScope(request.FirstWeek), ct)).Count;
        return View(new TimetableCenterViewModel
        {
            Sources = center.Sources, Source = source, Counts = counts, Request = request,
            Items = await center.ItemsAsync(source, request, ct),
            Options = await center.OptionsAsync(request, ct),
            Years = years, Year = year, RangeNote = note,
            From = Iso(from) ?? SchoolWeek.Iso(request.FirstWeek), To = Iso(to) ?? SchoolWeek.Iso(SchoolWeek.LastSchoolDay(request.Weeks[^1])),
        });
    }

    /// <summary>Download Selected: the ticked timetables of the category (all shown ones when none is given) — a PDF, or a ZIP for several.</summary>
    [HttpGet("download")]
    public async Task<IActionResult> Download(string? category, int? yearId, string? from, string? to, string? day,
        int? departmentId, string? subject, int? teacherId, int? classId, [FromQuery] string[]? ids, CancellationToken ct)
    {
        var (request, _, _, _) = await RequestAsync(category, yearId, from, to, day, departmentId, subject, teacherId, classId, ct);
        var source = center.Source(request.Category);
        if (source is null) return Back("Choose a timetable category.", request);
        var items = await center.ItemsAsync(source, request, ct);
        if (ids is { Length: > 0 }) items = items.Where(i => ids.Contains(i.Id)).ToList();
        if (items.Count == 0) return Back("Tick at least one timetable to download.", request);
        var dept = request.DepartmentId is { } d ? (await center.OptionsAsync(request, ct)).Departments.FirstOrDefault(x => x.Id == d).Name : null;
        // e.g. "Arabic Teacher Timetables", "Arabic Arabic Subject Timetables" → readable ZIP names per selection.
        var name = items.Count == 1 ? $"{source.Title} {items[0].Name}" : string.Join(" ", new[] { dept, request.Subject, source.Title }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        return await FileAsync(items.Select(i => (source, i)).ToList(), request, name, flat: false, zip: false, ct);
    }

    /// <summary>Download All: every timetable of one category (filters other than week and day ignored), or of the whole school (category=all).</summary>
    [HttpGet("download-all")]
    public async Task<IActionResult> DownloadAll(string? category, int? yearId, string? from, string? to, string? day, CancellationToken ct)
    {
        var (request, _, _, _) = await RequestAsync(category, yearId, from, to, day, null, null, null, null, ct);
        var sources = category == "all" ? center.Sources : center.Source(category) is { } one ? [one] : [];
        if (sources.Count == 0) return Back("Choose a timetable category.", request);
        var items = new List<(ITimetableSource, TimetableItem)>();
        foreach (var s in sources) items.AddRange((await s.ItemsAsync(new TimetableScope(request.FirstWeek), ct)).Select(i => (s, i)));
        return await FileAsync(items, request, category == "all" ? "School Timetables" : sources[0].Title, flat: false, zip: true, ct);
    }

    /// <summary>All schedules of one department: its department timetable, subject timetables and teacher timetables (and P.E. for the PE department).</summary>
    [HttpGet("department")]
    public async Task<IActionResult> Department(int departmentId, int? yearId, string? from, string? to, string? day, CancellationToken ct)
    {
        var (request, _, _, _) = await RequestAsync("department", yearId, from, to, day, departmentId, null, null, null, ct);
        var items = new List<(ITimetableSource, TimetableItem)>();
        foreach (var s in center.Sources.Where(s => s.Filters.HasFlag(TimetableFilters.Department)))
            items.AddRange((await s.ItemsAsync(new TimetableScope(request.FirstWeek, DepartmentId: departmentId), ct)).Select(i => (s, i)));
        var dept = (await center.OptionsAsync(request, ct)).Departments.FirstOrDefault(d => d.Id == departmentId).Name;
        if (dept is null) return Back("This department no longer exists.", request);
        return await FileAsync(items, request, $"{dept} Department Timetables", flat: true, zip: true, ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<IActionResult> FileAsync(List<(ITimetableSource, TimetableItem)> items, TimetableRequest request, string name, bool flat, bool zip, CancellationToken ct)
    {
        if (items.Count == 0) return Back("There is no timetable to download for this selection.", request);
        if (items.Count * request.Weeks.Count > TimetableCenter.MaxDocuments)
            return Back($"That would be {items.Count * request.Weeks.Count:N0} PDFs. Choose fewer weeks or a smaller group (at most {TimetableCenter.MaxDocuments:N0} at a time).", request);
        var file = await center.DownloadAsync(items, request, name, flat, zip, ct);
        if (file is null) return Back("There is no timetable to download for this selection.", request);
        return File(file.Bytes, file.ContentType, file.FileName);
    }

    private IActionResult Back(string message, TimetableRequest request)
    {
        Toast(message, "warning");
        return LocalRedirect("/timetables" + (request.Category is null ? "" : $"?category={Uri.EscapeDataString(request.Category)}"));
    }

    private static string? Iso(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? SchoolWeek.Iso(d) : null;

    /// <summary>
    /// Academic year (from the academic calendar; its quarter dates bound the weeks), date range (one PDF per
    /// school week in it, at most <see cref="MaxWeeks"/>), day and filters. Defaults: the current week.
    /// </summary>
    private async Task<(TimetableRequest, IReadOnlyList<YearView>, YearView?, string?)> RequestAsync(string? category, int? yearId, string? from, string? to, string? day,
        int? departmentId, string? subject, int? teacherId, int? classId, CancellationToken ct)
    {
        var years = (await calendar.YearsAsync(ct)).Where(y => y.Quarters.Any(q => q.IsConfigured)).ToList();
        (DateOnly Start, DateOnly End)? Range(YearView y) =>
            (y.Quarters.Where(q => q.IsConfigured).Min(q => q.StartDate!.Value), y.Quarters.Where(q => q.IsConfigured).Max(q => q.EndDate!.Value));
        var today = SchoolWeek.Today;
        var year = years.FirstOrDefault(y => y.Id == yearId)
                   ?? years.FirstOrDefault(y => Range(y) is { } r && r.Start <= today && today <= r.End)
                   ?? (yearId is null ? null : years.FirstOrDefault());
        string? note = null;

        var start = Iso(from) is { } f ? DateOnly.ParseExact(f, "yyyy-MM-dd") : (DateOnly?)null;
        var end = Iso(to) is { } t ? DateOnly.ParseExact(t, "yyyy-MM-dd") : (DateOnly?)null;
        if (year is not null && Range(year) is { } yr)
        {
            var fallback = today >= yr.Start && today <= yr.End ? today : yr.Start;
            start ??= fallback;
            if (start < yr.Start || start > yr.End) { start = fallback; note = $"Dates were kept inside {year.Name} ({yr.Start.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} – {yr.End.ToString("d MMM yyyy", CultureInfo.InvariantCulture)})."; }
            end ??= start;
            if (end > yr.End) { end = yr.End; note ??= $"Dates were kept inside {year.Name}."; }
        }
        start ??= today;
        end ??= start;
        if (end < start) end = start;

        var weeks = new List<DateOnly>();
        for (var w = SchoolWeek.StartOf(start.Value); w <= SchoolWeek.StartOf(end.Value) && weeks.Count < MaxWeeks; w = w.AddDays(7)) weeks.Add(w);
        if (SchoolWeek.StartOf(end.Value) > weeks[^1]) note = $"A download covers at most {MaxWeeks} weeks: {SchoolWeek.Label(weeks[0])} to {SchoolWeek.Label(weeks[^1])}.";

        DayOfWeek? dayOfWeek = Enum.TryParse<DayOfWeek>(day, true, out var dw) && ScheduleRules.IsSchoolDay(dw) ? dw : null;
        var request = new TimetableRequest(category, year?.Id, year?.Name, weeks, dayOfWeek, departmentId,
            string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(), teacherId, classId);
        return (request, years, year, note);
    }
}
