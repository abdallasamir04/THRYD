using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

// ---------------------------------------------------------------------- views

public sealed record CalendarYearOption(int Id, string Name);

public sealed record CalendarEventView(int Id, int YearId, string Title, DateOnly? StartDate, DateOnly? EndDate,
    string? DateLabel, DateOnly ListMonth, CalendarColor Color, string? AccentText, bool NeedsConfirmation, string? Notes, int SortOrder, bool IsHidden)
{
    public DateOnly? LastDate => EndDate ?? StartDate;
    public bool Covers(DateOnly d) => StartDate is { } s && d >= s && d <= (EndDate ?? s);
}

/// <summary>One cell of a month grid. <see cref="InQuarter"/> is false for the days of a transition month that belong to the other quarter.</summary>
public sealed record CalendarDay(DateOnly Date, bool InMonth, bool InQuarter, CalendarColor Color, string? QuarterStart, bool IsToday, IReadOnlyList<string> Events);

/// <summary>An event line under a month. A continuation is the part of a multi-month event listed under another month.</summary>
public sealed record CalendarRow(CalendarEventView Event, string DateText, bool Continuation, string? ContinuationText);

public sealed record CalendarMonth(DateOnly Month, DateOnly From, DateOnly To, bool Partial, string? BoundaryNote,
    IReadOnlyList<IReadOnlyList<CalendarDay>> Weeks, IReadOnlyList<CalendarRow> Rows);

public sealed record CalendarQuarter(int Id, int Number, string Name, DateOnly? Start, DateOnly? End, IReadOnlyList<CalendarMonth> Months);

public sealed record SchoolCalendarPage(
    IReadOnlyList<CalendarYearOption> Years, int? YearId, string? YearName,
    IReadOnlyList<CalendarQuarter> Quarters, IReadOnlyList<CalendarRow> Unplaced,
    int EventCount, int MarkCount, int ToConfirm, bool CanLoadReference);

public sealed record CalendarDayView(int YearId, string YearName, DateOnly Date, CalendarColor Mark, IReadOnlyList<CalendarEventView> Events);

public sealed record CalendarEventInput(string? Title, DateOnly? StartDate, DateOnly? EndDate, string? DateLabel,
    DateOnly? ListMonth, CalendarColor Color, string? AccentText, bool NeedsConfirmation, string? Notes, int SortOrder, bool IsHidden);

public sealed record CalendarLoadResult(ServiceResult Result, int Events, int Marks, int QuartersSet, IReadOnlyList<string> Kept);

public interface ISchoolCalendarService
{
    Task<SchoolCalendarPage> PageAsync(int? yearId, CancellationToken ct = default);
    Task<CalendarEventView?> EventAsync(int id, CancellationToken ct = default);
    /// <summary>The months an event can be listed under: those of the year's quarters (or of its events).</summary>
    Task<IReadOnlyList<DateOnly>> MonthsAsync(int yearId, CancellationToken ct = default);
    Task<string?> YearNameAsync(int yearId, CancellationToken ct = default);
    Task<ServiceResult> SaveEventAsync(int? id, int yearId, CalendarEventInput input, CancellationToken ct = default);
    Task<ServiceResult> DeleteEventAsync(int id, CancellationToken ct = default);
    Task<CalendarDayView?> DayAsync(int yearId, DateOnly date, CancellationToken ct = default);
    Task<ServiceResult> SetMarkAsync(int yearId, DateOnly date, CalendarColor color, CancellationToken ct = default);
    Task<ServiceResult> UpdateQuarterDatesAsync(int quarterId, DateOnly? start, DateOnly? end, CancellationToken ct = default);
    Task<(DateOnly? Start, DateOnly? End)> QuarterDatesAsync(int quarterId, CancellationToken ct = default);
    Task<CalendarLoadResult> LoadReferenceAsync(int yearId, CancellationToken ct = default);
}

/// <summary>
/// School Calendar: the teachers' calendar of an academic year, grouped by the quarters as printed (their own
/// dates, <see cref="AcademicQuarter.CalendarStartDate"/>), month by month: coloured dates in the grid and the
/// month's event lines underneath. The 8-week quarter dates used by other modules are never changed here.
/// </summary>
public sealed class SchoolCalendarService(SchoolDbContext db) : ISchoolCalendarService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ page

    public async Task<SchoolCalendarPage> PageAsync(int? yearId, CancellationToken ct = default)
    {
        var years = await db.AcademicYears.AsNoTracking().OrderByDescending(y => y.Name).Select(y => new CalendarYearOption(y.Id, y.Name)).ToListAsync(ct);
        if (years.Count == 0) return new SchoolCalendarPage(years, null, null, [], [], 0, 0, 0, false);

        var quarters = await db.AcademicQuarters.AsNoTracking().OrderBy(q => q.Number).ToListAsync(ct);
        var year = years.FirstOrDefault(y => y.Id == yearId) ?? DefaultYear(years, quarters);
        var mine = quarters.Where(q => q.AcademicYearId == year.Id).ToList();

        var events = await Events(db.SchoolCalendarEvents.Where(e => e.AcademicYearId == year.Id)).ToListAsync(ct);
        var marks = await db.SchoolCalendarMarks.AsNoTracking().Where(m => m.AcademicYearId == year.Id)
            .ToDictionaryAsync(m => m.Date, m => m.Color, ct);

        var today = SchoolWeek.Today;
        var visible = events.Where(e => !e.IsHidden).ToList();
        var starts = mine.Where(q => q.CalendarStartDate is not null && q.Number > 1).ToDictionary(q => q.CalendarStartDate!.Value, q => $"Start of Q{q.Number}");

        // Every event line goes to exactly one month card: the card of its month whose quarter part holds its anchor day.
        var placements = events.SelectMany(Placements).ToList();
        var placed = new HashSet<(int, DateOnly)>();

        var result = new List<CalendarQuarter>();
        foreach (var q in mine)
        {
            var months = new List<CalendarMonth>();
            if (q.CalendarStartDate is { } qs && q.CalendarEndDate is { } qe)
            {
                for (var m = MonthOf(qs); m <= qe; m = m.AddMonths(1))
                {
                    var last = m.AddMonths(1).AddDays(-1);
                    var from = qs > m ? qs : m;
                    var to = qe < last ? qe : last;
                    var rows = placements.Where(p => p.Month == m && p.Anchor >= from && p.Anchor <= to && !p.Event.IsHidden)
                        .OrderBy(p => p.Continuation && p.Anchor == m ? 0 : p.Continuation ? 2 : 1)
                        .ThenBy(p => p.Event.SortOrder).ThenBy(p => p.Event.StartDate)
                        .Select(p => { placed.Add((p.Event.Id, p.Month)); return p.Row; }).ToList();
                    months.Add(new CalendarMonth(m, from, to, from > m || to < last, Boundary(q, m, from, to, last, mine),
                        Weeks(m, from, to, marks, starts, visible, today), rows));
                }
            }
            result.Add(new CalendarQuarter(q.Id, q.Number, q.Name, q.CalendarStartDate, q.CalendarEndDate, months));
        }

        // Nothing is ever left out: lines outside every quarter's dates (or hidden) are listed separately.
        var unplaced = placements.Where(p => !placed.Contains((p.Event.Id, p.Month)) && !p.Continuation)
            .OrderBy(p => p.Month).ThenBy(p => p.Event.SortOrder).Select(p => p.Row).ToList();

        var canLoad = events.Count == 0 && marks.Count == 0 && SchoolCalendarReference.Fits(year.Name);
        return new SchoolCalendarPage(years, year.Id, year.Name, result, unplaced, events.Count, marks.Count,
            events.Count(e => e.NeedsConfirmation), canLoad);
    }

    private static CalendarYearOption DefaultYear(List<CalendarYearOption> years, List<AcademicQuarter> quarters)
    {
        var today = SchoolWeek.Today;
        var current = quarters.FirstOrDefault(q =>
            (q.CalendarStartDate <= today && q.CalendarEndDate >= today) || (q.StartDate <= today && q.EndDate >= today));
        return years.FirstOrDefault(y => y.Id == current?.AcademicYearId) ?? years[0];
    }

    private sealed record Placement(CalendarEventView Event, DateOnly Month, DateOnly Anchor, bool Continuation, CalendarRow Row);

    /// <summary>
    /// Where an event is listed: its own month (<see cref="SchoolCalendarEvent.ListMonth"/>), plus a "continued" line
    /// in every other month its dates cover. The anchor day decides the quarter part of a transition month.
    /// </summary>
    private static IEnumerable<Placement> Placements(CalendarEventView e)
    {
        if (e.StartDate is not { } start)
        {
            // An undated line belongs to the middle of its month (April's goes to Q4, not to 1–3 April).
            yield return new Placement(e, e.ListMonth, e.ListMonth.AddDays(14), false, new CalendarRow(e, e.DateLabel ?? "Date to confirm", false, null));
            yield break;
        }
        var end = e.LastDate!.Value;
        var primary = e.ListMonth >= MonthOf(start) && e.ListMonth <= MonthOf(end) ? e.ListMonth : MonthOf(start);
        for (var m = MonthOf(start); m <= end; m = m.AddMonths(1))
        {
            var last = m.AddMonths(1).AddDays(-1);
            var from = start > m ? start : m;
            var to = end < last ? end : last;
            if (m == primary)
                yield return new Placement(e, m, from, false, new CalendarRow(e, e.DateLabel ?? RangeText(start, end), false, null));
            else
            {
                var part = from == to ? from.Day.ToString(C) : $"{from.Day}–{to.Day}";
                var note = m < primary ? $"continues in {primary.ToString("MMMM", C)} · {RangeText(start, end, true)}"
                    : $"continued from {primary.ToString("MMMM", C)} · {RangeText(start, end, true)}";
                yield return new Placement(e, m, from, true, new CalendarRow(e, part, true, note));
            }
        }
    }

    /// <summary>"20", "25–29", or across months "28 Mar – 1 Apr".</summary>
    public static string RangeText(DateOnly start, DateOnly end, bool withMonth = false)
    {
        if (start == end) return withMonth ? start.ToString("d MMM", C) : start.Day.ToString(C);
        if (start.Month == end.Month && start.Year == end.Year)
            return withMonth ? $"{start.Day}–{end.ToString("d MMM", C)}" : $"{start.Day}–{end.Day}";
        return $"{start.ToString("d MMM", C)} – {end.ToString("d MMM", C)}";
    }

    private static string? Boundary(AcademicQuarter q, DateOnly month, DateOnly from, DateOnly to, DateOnly last, List<AcademicQuarter> all)
    {
        if (from == month && to == last) return null;
        var name = month.ToString("MMMM", C);
        var other = from > month
            ? all.FirstOrDefault(o => o.CalendarEndDate is { } oe && oe >= month && oe < from)
            : all.FirstOrDefault(o => o.CalendarStartDate is { } os && os > to && os <= last);
        var mine = $"{from.Day}–{to.Day} {name} belong to Q{q.Number}";
        if (other is null) return mine;
        return from > month
            ? $"{mine} · 1–{from.AddDays(-1).Day} belong to Q{other.Number}"
            : $"{mine} · Q{other.Number} starts {to.AddDays(1).ToString("ddd d MMM", C)}";
    }

    private static List<IReadOnlyList<CalendarDay>> Weeks(DateOnly month, DateOnly from, DateOnly to, Dictionary<DateOnly, CalendarColor> marks,
        Dictionary<DateOnly, string> starts, List<CalendarEventView> events, DateOnly today)
    {
        var last = month.AddMonths(1).AddDays(-1);
        var day = month.AddDays(-(int)month.DayOfWeek); // weeks start on Sunday, like the printed calendar
        var weeks = new List<IReadOnlyList<CalendarDay>>();
        while (day <= last)
        {
            var week = new List<CalendarDay>(7);
            for (var i = 0; i < 7; i++, day = day.AddDays(1))
            {
                var inMonth = day.Month == month.Month;
                var d = day;
                week.Add(new CalendarDay(day, inMonth, inMonth && day >= from && day <= to,
                    inMonth && marks.TryGetValue(day, out var c) ? c : CalendarColor.None,
                    inMonth && starts.TryGetValue(day, out var s) ? s : null,
                    day == today,
                    inMonth ? events.Where(e => e.Covers(d)).OrderBy(e => e.SortOrder).Select(e => e.Title).ToList() : []));
            }
            weeks.Add(week);
        }
        return weeks;
    }

    private static DateOnly MonthOf(DateOnly d) => new(d.Year, d.Month, 1);

    private static IQueryable<CalendarEventView> Events(IQueryable<SchoolCalendarEvent> q) =>
        q.AsNoTracking().OrderBy(e => e.ListMonth).ThenBy(e => e.SortOrder).ThenBy(e => e.Id)
            .Select(e => new CalendarEventView(e.Id, e.AcademicYearId, e.Title, e.StartDate, e.EndDate, e.DateLabel, e.ListMonth,
                e.Color, e.AccentText, e.NeedsConfirmation, e.Notes, e.SortOrder, e.IsHidden));

    // ------------------------------------------------------------------ events

    public Task<CalendarEventView?> EventAsync(int id, CancellationToken ct = default) =>
        Events(db.SchoolCalendarEvents.Where(e => e.Id == id)).FirstOrDefaultAsync(ct);

    public Task<string?> YearNameAsync(int yearId, CancellationToken ct = default) =>
        db.AcademicYears.Where(y => y.Id == yearId).Select(y => y.Name).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<DateOnly>> MonthsAsync(int yearId, CancellationToken ct = default)
    {
        var q = await db.AcademicQuarters.AsNoTracking().Where(x => x.AcademicYearId == yearId).ToListAsync(ct);
        var dates = q.SelectMany(x => new[] { x.CalendarStartDate, x.CalendarEndDate, x.StartDate, x.EndDate }).OfType<DateOnly>().ToList();
        dates.AddRange(await db.SchoolCalendarEvents.Where(e => e.AcademicYearId == yearId).Select(e => e.ListMonth).ToListAsync(ct));
        if (dates.Count == 0) return [];
        var months = new List<DateOnly>();
        for (var m = MonthOf(dates.Min()); m <= dates.Max(); m = m.AddMonths(1)) months.Add(m);
        return months;
    }

    public async Task<ServiceResult> SaveEventAsync(int? id, int yearId, CalendarEventInput input, CancellationToken ct = default)
    {
        if (!await db.AcademicYears.AnyAsync(y => y.Id == yearId, ct)) return ServiceResult.Fail("", "This academic year no longer exists.");
        SchoolCalendarEvent? e = null;
        if (id is not null)
        {
            e = await db.SchoolCalendarEvents.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return ServiceResult.Fail("", "This event no longer exists.");
        }

        var result = ServiceResult.Ok(id);
        var title = TextNormalizer.CleanText(input.Title);
        var label = Optional(input.DateLabel);
        var accent = Optional(input.AccentText);
        var notes = input.Notes?.Trim() is { Length: > 0 } n ? n : null;

        if (title.Length == 0) result.AddError("Title", "Enter the event name.");
        else if (title.Length > SchoolCalendarEvent.TitleMaxLength) result.AddError("Title", $"Use {SchoolCalendarEvent.TitleMaxLength} characters or fewer.");
        if (label?.Length > SchoolCalendarEvent.DateLabelMaxLength) result.AddError("DateLabel", $"Use {SchoolCalendarEvent.DateLabelMaxLength} characters or fewer.");
        if (notes?.Length > SchoolCalendarEvent.NotesMaxLength) result.AddError("Notes", $"Use {SchoolCalendarEvent.NotesMaxLength} characters or fewer.");
        if (input.EndDate is not null && input.StartDate is null) result.AddError("StartDate", "Enter the first day of the range.");
        if (input.EndDate is { } end && input.StartDate is { } st && end < st) result.AddError("EndDate", "The last day cannot be before the first day.");
        if (input.EndDate == input.StartDate) input = input with { EndDate = null };
        if (input.StartDate is null && input.ListMonth is null) result.AddError("ListMonth", "Choose the month to list an event without a date under.");
        if (!Enum.IsDefined(input.Color)) result.AddError("Color", "Choose a colour from the list.");
        if (accent is not null && title.Length > 0 && title.IndexOf(accent, StringComparison.OrdinalIgnoreCase) < 0)
            result.AddError("AccentText", "The coloured part must be words of the event name.");
        if (input.SortOrder is < 0 or > 9999) result.AddError("SortOrder", "Use a number from 0 to 9999.");
        if (!result.Succeeded) return result;

        var listMonth = input.ListMonth is { } lm ? MonthOf(lm) : MonthOf(input.StartDate!.Value);
        if (input.StartDate is { } s && (listMonth < MonthOf(s) || listMonth > MonthOf(input.EndDate ?? s)))
            return ServiceResult.Fail("ListMonth", "List the event under a month its dates cover.");

        e ??= db.SchoolCalendarEvents.Add(new SchoolCalendarEvent { AcademicYearId = yearId }).Entity;
        e.Title = title;
        e.StartDate = input.StartDate;
        e.EndDate = input.EndDate;
        e.DateLabel = label;
        e.ListMonth = listMonth;
        e.Color = input.Color;
        e.AccentText = accent;
        e.NeedsConfirmation = input.NeedsConfirmation;
        e.Notes = notes;
        e.SortOrder = input.SortOrder;
        e.IsHidden = input.IsHidden;
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(e.Id);
    }

    public async Task<ServiceResult> DeleteEventAsync(int id, CancellationToken ct = default)
    {
        var deleted = await db.SchoolCalendarEvents.Where(e => e.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? ServiceResult.Fail("", "This event no longer exists.") : ServiceResult.Ok(id);
    }

    private static string? Optional(string? s) => TextNormalizer.CleanText(s) is { Length: > 0 } c ? c : null;

    // ------------------------------------------------------------------ days and marks

    public async Task<CalendarDayView?> DayAsync(int yearId, DateOnly date, CancellationToken ct = default)
    {
        var name = await YearNameAsync(yearId, ct);
        if (name is null) return null;
        var mark = await db.SchoolCalendarMarks.Where(m => m.AcademicYearId == yearId && m.Date == date).Select(m => (CalendarColor?)m.Color).FirstOrDefaultAsync(ct);
        var events = await Events(db.SchoolCalendarEvents.Where(e => e.AcademicYearId == yearId && e.StartDate != null && e.StartDate <= date && (e.EndDate ?? e.StartDate) >= date)).ToListAsync(ct);
        return new CalendarDayView(yearId, name, date, mark ?? CalendarColor.None, events);
    }

    public async Task<ServiceResult> SetMarkAsync(int yearId, DateOnly date, CalendarColor color, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(color)) return ServiceResult.Fail("Color", "Choose a colour from the list.");
        if (!await db.AcademicYears.AnyAsync(y => y.Id == yearId, ct)) return ServiceResult.Fail("", "This academic year no longer exists.");
        var mark = await db.SchoolCalendarMarks.FirstOrDefaultAsync(m => m.AcademicYearId == yearId && m.Date == date, ct);
        if (color == CalendarColor.None) { if (mark is not null) db.SchoolCalendarMarks.Remove(mark); }
        else if (mark is null) db.SchoolCalendarMarks.Add(new SchoolCalendarMark { AcademicYearId = yearId, Date = date, Color = color });
        else mark.Color = color;
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    // ------------------------------------------------------------------ quarters as printed

    public static IEnumerable<(string Field, string Message)> ValidateQuarterDates(DateOnly? start, DateOnly? end)
    {
        if (start is null != end is null) yield return (start is null ? "CalendarStartDate" : "CalendarEndDate", "Enter both dates of the school calendar, or neither.");
        else if (end < start) yield return ("CalendarEndDate", "The school calendar's last day cannot be before its first day.");
    }

    public async Task<(DateOnly? Start, DateOnly? End)> QuarterDatesAsync(int quarterId, CancellationToken ct = default)
    {
        var q = await db.AcademicQuarters.AsNoTracking().Where(x => x.Id == quarterId)
            .Select(x => new { x.CalendarStartDate, x.CalendarEndDate }).FirstOrDefaultAsync(ct);
        return (q?.CalendarStartDate, q?.CalendarEndDate);
    }

    public async Task<ServiceResult> UpdateQuarterDatesAsync(int quarterId, DateOnly? start, DateOnly? end, CancellationToken ct = default)
    {
        var result = ServiceResult.Ok(quarterId);
        foreach (var (k, v) in ValidateQuarterDates(start, end)) result.AddError(k, v);
        if (!result.Succeeded) return result;
        var q = await db.AcademicQuarters.FirstOrDefaultAsync(x => x.Id == quarterId, ct);
        if (q is null) return ServiceResult.Fail("", "This quarter no longer exists.");
        if (start is not null)
        {
            var overlap = await db.AcademicQuarters.AsNoTracking()
                .Where(x => x.AcademicYearId == q.AcademicYearId && x.Id != quarterId && x.CalendarStartDate <= end && x.CalendarEndDate >= start)
                .Select(x => x.Name).FirstOrDefaultAsync(ct);
            if (overlap is not null) return ServiceResult.Fail("CalendarStartDate", $"These school calendar dates overlap {overlap}.");
        }
        (q.CalendarStartDate, q.CalendarEndDate) = (start, end);
        await db.SaveChangesAsync(ct);
        return result;
    }

    // ------------------------------------------------------------------ the printed 2026/2027 calendar

    public async Task<CalendarLoadResult> LoadReferenceAsync(int yearId, CancellationToken ct = default)
    {
        var year = await db.AcademicYears.Include(y => y.Quarters).FirstOrDefaultAsync(y => y.Id == yearId, ct);
        if (year is null) return new(ServiceResult.Fail("", "This academic year no longer exists."), 0, 0, 0, []);
        if (!SchoolCalendarReference.Fits(year.Name))
            return new(ServiceResult.Fail("", $"The printed calendar is for 2026/2027, not {year.Name}."), 0, 0, 0, []);
        // Never on top of the school's own entries: only into an empty calendar.
        if (await db.SchoolCalendarEvents.AnyAsync(e => e.AcademicYearId == yearId, ct) || await db.SchoolCalendarMarks.AnyAsync(m => m.AcademicYearId == yearId, ct))
            return new(ServiceResult.Fail("", $"{year.Name} already has calendar entries. Nothing was loaded, so none of them changes."), 0, 0, 0, []);

        // Quarter dates as printed: set only where the school has not set its own.
        var kept = new List<string>();
        var set = 0;
        foreach (var r in SchoolCalendarReference.Quarters)
        {
            var q = year.Quarters.FirstOrDefault(x => x.Number == r.Number);
            if (q is null) continue;
            if (q.CalendarStartDate is null) { (q.CalendarStartDate, q.CalendarEndDate) = (r.Start, r.End); set++; }
            else if (q.CalendarStartDate != r.Start || q.CalendarEndDate != r.End) kept.Add(q.Name);
        }

        var order = 0;
        DateOnly? month = null;
        foreach (var x in SchoolCalendarReference.Events)
        {
            order = month == x.ListMonth ? order + 10 : 10;
            month = x.ListMonth;
            db.SchoolCalendarEvents.Add(new SchoolCalendarEvent
            {
                AcademicYearId = yearId, Title = x.Title, StartDate = x.Start, EndDate = x.End,
                DateLabel = x.DateLabel, ListMonth = x.ListMonth, Color = x.Color, AccentText = x.Accent,
                NeedsConfirmation = x.Confirm, Notes = x.Notes, SortOrder = order,
            });
        }
        foreach (var (date, color) in SchoolCalendarReference.Marks)
            db.SchoolCalendarMarks.Add(new SchoolCalendarMark { AcademicYearId = yearId, Date = date, Color = color });

        await db.SaveChangesAsync(ct);
        return new(ServiceResult.Ok(yearId), SchoolCalendarReference.Events.Count, SchoolCalendarReference.Marks.Count, set, kept);
    }
}
