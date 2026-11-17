using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

// ====================================================================== filters

/// <summary>
/// History & Reports filters (query string). Period: year (default), quarter, month, week, day or range.
/// Scope: the whole school, a grade, a class or a student. The academic filters are ignored by Personal Belongings.
/// </summary>
public sealed class BelongingsQuery
{
    public int? YearId { get; set; }
    public string? Period { get; set; }
    public int? QuarterId { get; set; }
    /// <summary>"yyyy-MM".</summary>
    public string? Month { get; set; }
    /// <summary>Any date of the school week (Sunday–Thursday).</summary>
    public DateOnly? Week { get; set; }
    public DateOnly? Date { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Grade { get; set; }
    public int? ClassId { get; set; }
    public int? StudentId { get; set; }
    /// <summary>Personal: complete, incomplete, missing, missing:&lt;item key&gt;. Academic: complete, incomplete.</summary>
    public string? Status { get; set; }
    public string? Subject { get; set; }
    public int? TeacherId { get; set; }
    public int? PeriodNumber { get; set; }
    /// <summary>Available, Missing, NA, None.</summary>
    public string? Book { get; set; }
    public string? CopyBook { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    /// <summary>A copy with some filters changed (page back to 1), e.g. one student's records in the same period.</summary>
    public BelongingsQuery With(Action<BelongingsQuery> change)
    {
        var copy = (BelongingsQuery)MemberwiseClone();
        copy.Page = 1;
        change(copy);
        return copy;
    }

    /// <summary>The filters as a query string (without the page), for links and downloads.</summary>
    public string ToQuery(bool withPage = false)
    {
        var parts = new List<string>();
        void Add(string key, object? value) { if (value is not null && value.ToString() is { Length: > 0 } v) parts.Add($"{key}={Uri.EscapeDataString(v)}"); }
        Add("yearId", YearId); Add("period", Period); Add("quarterId", QuarterId); Add("month", Month);
        Add("week", Week is { } w ? SchoolWeek.Iso(w) : null); Add("date", Date is { } d ? SchoolWeek.Iso(d) : null);
        Add("from", From is { } f ? SchoolWeek.Iso(f) : null); Add("to", To is { } t ? SchoolWeek.Iso(t) : null);
        Add("grade", Grade); Add("classId", ClassId); Add("studentId", StudentId); Add("status", Status);
        Add("subject", Subject); Add("teacherId", TeacherId); Add("periodNumber", PeriodNumber); Add("book", Book); Add("copyBook", CopyBook);
        if (withPage) { Add("page", Page > 1 ? Page : null); Add("pageSize", PageSize != 25 ? PageSize : null); }
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }
}

/// <summary>What a history view or report covers: the academic year, the dates and who (school, grade, class, student).</summary>
public sealed record BelongingsScope(string? YearName, string? QuarterName, DateOnly From, DateOnly To, string PeriodLabel, string Audience, bool Bounded)
{
    public string DatesLabel => Bounded ? (From == To ? From.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)
        : $"{From.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} – {To.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}") : "All dates";
}

public sealed record HistoryClassOption(int Id, string Name, string? GradeKey);
public sealed record HistoryWeekOption(DateOnly Start, string Label);

public sealed record HistoryOptions(
    IReadOnlyList<YearView> Years, YearView? Year, IReadOnlyList<(string Value, string Label)> Months, IReadOnlyList<HistoryWeekOption> Weeks,
    IReadOnlyList<HistoryClassOption> Classes, IReadOnlyList<StudentOption> Students,
    IReadOnlyList<string> Subjects, IReadOnlyList<(int Id, string Name)> Teachers, IReadOnlyList<(int Number, string Label)> Periods);

// ====================================================================== results

public sealed record GroupStat(string Label, int Records, int Complete, ReadinessCounts Readiness)
{
    public int CompletePercent => Records == 0 ? 0 : (int)Math.Round(100.0 * Complete / Records);
}

public sealed record TrendPoint(DateOnly WeekStart, int Records, int Complete, ReadinessCounts Readiness)
{
    public int CompletePercent => Records == 0 ? 0 : (int)Math.Round(100.0 * Complete / Records);
    public string Label => $"{WeekStart.ToString("d MMM", CultureInfo.InvariantCulture)} – {WeekStart.AddDays(4).ToString("d MMM", CultureInfo.InvariantCulture)}";
}

public sealed record RepeatedMissing(int StudentId, string Name, string ClassName, int Days, string Items);

public sealed record PersonalHistoryRow(
    int RecordId, DateOnly Date, string ClassName, int ClassOrder, int StudentId, string StudentName, IReadOnlyList<ItemStatus?> Items,
    string? Notes, string? RecordedBy, string? UpdatedBy, DateTime UpdatedAt, int Corrections)
{
    public bool Complete => PersonalItems.Complete(Items);
    public IReadOnlyList<string> Missing => PersonalItems.Missing(Items);
    public ReadinessCounts Readiness => ReadinessCounts.Of(Items);
}

public sealed record PersonalHistory(
    BelongingsScope Scope, int Total, int Complete, int WithMissing, ReadinessCounts Readiness,
    IReadOnlyList<GroupStat> ByClass, IReadOnlyList<RepeatedMissing> Repeated, IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<PersonalHistoryRow> Rows, PagedResult<PersonalHistoryRow> Page)
{
    public int Incomplete => Total - Complete;
}

public sealed record AcademicHistoryRow(
    int Id, int RecordId, DateOnly Date, string ClassName, int ClassOrder, int StudentId, string StudentName, int QuarterId,
    int? SessionId, int? PeriodNumber, string? PeriodName, TimeOnly? Start, TimeOnly? End, string Subject, int? TeacherId, string? TeacherName,
    ItemStatus? Book, ItemStatus? CopyBook, string? Notes, string? RecordedBy, string? UpdatedBy, DateTime UpdatedAt, int Corrections)
{
    public bool Complete => Book is not null && CopyBook is not null;
    /// <summary>False for day-based rows recorded before lesson-based checks (no lesson).</summary>
    public bool IsLesson => SessionId is not null || PeriodNumber is not null;
    public string Period => IsLesson ? PeriodName ?? $"Period {PeriodNumber}" : "Daily record";
    public string Times => IsLesson ? ScheduleRules.Times12(Start, End) : "—";
    public ReadinessCounts Readiness => ReadinessCounts.Of([Book, CopyBook]);
    /// <summary>One lesson on one date (a day-based row is its own group per subject).</summary>
    public string LessonKey => SessionId is { } s ? $"{Date:yyyyMMdd}-{s}" : $"{Date:yyyyMMdd}-{ClassName}-{Subject}";
}

public sealed record AcademicHistory(
    BelongingsScope Scope, int Lessons, int Checks, int Complete, int MissingBook, int MissingCopyBook, ReadinessCounts Readiness,
    IReadOnlyList<GroupStat> ByClass, IReadOnlyList<GroupStat> BySubject, IReadOnlyList<GroupStat> ByTeacher, IReadOnlyList<GroupStat> ByQuarter,
    IReadOnlyList<TrendPoint> Trend, IReadOnlyList<GroupStat> ByMonth, IReadOnlyList<AcademicHistoryRow> Rows, PagedResult<AcademicHistoryRow> Page)
{
    public int Incomplete => Checks - Complete;
}

public sealed record ChangeLine(string Item, string? Context, string? Before, string? After, DateTime At, string? By);

public sealed record PersonalRecordDetail(PersonalHistoryRow Row, int ClassId, IReadOnlyList<NoteVersion> NoteHistory, IReadOnlyList<ChangeLine> Changes);

/// <summary>One lesson on one date as recorded: its snapshot and every student's result, with the corrections.</summary>
public sealed record AcademicLessonDetail(AcademicHistoryRow Lesson, int ClassId, IReadOnlyList<AcademicHistoryRow> Students, IReadOnlyList<ChangeLine> Changes);

public interface IBelongingsHistoryService
{
    Task<HistoryOptions> OptionsAsync(BelongingsQuery query, bool academic, CancellationToken ct = default);
    Task<PersonalHistory> PersonalAsync(BelongingsQuery query, CancellationToken ct = default);
    Task<AcademicHistory> AcademicAsync(BelongingsQuery query, CancellationToken ct = default);
    Task<PersonalRecordDetail?> PersonalRecordAsync(int recordId, CancellationToken ct = default);
    Task<AcademicLessonDetail?> AcademicRecordAsync(int rowId, CancellationToken ct = default);
}

/// <summary>
/// Belongings History & Reports: every class, every stored record, filtered by period and scope. Counts are added
/// (never averages of percentages); readiness = Available ÷ (Available + Missing), N/A never counted.
/// </summary>
public sealed class BelongingsHistoryService(SchoolDbContext db, IBelongingsService calendar, ISchoolTimeService? time = null) : IBelongingsHistoryService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = time ?? SchoolTime.Current;

    // ------------------------------------------------------------------ scope

    private sealed record Resolved(BelongingsScope Scope, YearView? Year, IReadOnlyList<YearView> Years, List<int>? ClassIds);

    private async Task<Resolved> ResolveAsync(BelongingsQuery q, CancellationToken ct)
    {
        var years = await calendar.YearsAsync(ct);
        var today = _time.Today;
        var quarterDates = await db.AcademicQuarters.AsNoTracking()
            .Select(x => new { x.AcademicYearId, x.StartDate, x.EndDate, x.CalendarStartDate, x.CalendarEndDate }).ToListAsync(ct);
        (DateOnly, DateOnly)? RangeOf(int yearId)
        {
            var dates = quarterDates.Where(x => x.AcademicYearId == yearId)
                .SelectMany(x => new[] { x.StartDate, x.EndDate, x.CalendarStartDate, x.CalendarEndDate }).OfType<DateOnly>().ToList();
            return dates.Count == 0 ? null : (dates.Min(), dates.Max());
        }
        var year = years.FirstOrDefault(y => y.Id == q.YearId)
                   ?? years.FirstOrDefault(y => RangeOf(y.Id) is { } r && r.Item1 <= today && today <= r.Item2)
                   ?? years.FirstOrDefault(y => RangeOf(y.Id) is not null) ?? years.FirstOrDefault();
        var yearRange = year is null ? null : RangeOf(year.Id);

        DateOnly from = DateOnly.MinValue, to = DateOnly.MaxValue;
        var bounded = false;
        string label;
        QuarterView? quarter = null;
        switch (q.Period)
        {
            case "quarter" when year?.Quarters.FirstOrDefault(x => x.Id == q.QuarterId && x.IsConfigured) is { } qu:
                (from, to, bounded, quarter) = (qu.StartDate!.Value, qu.EndDate!.Value, true, qu);
                label = $"{qu.Name} ({year.Name})";
                break;
            case "month" when DateOnly.TryParseExact((q.Month ?? "") + "-01", "yyyy-MM-dd", out var m):
                (from, to, bounded) = (m, m.AddMonths(1).AddDays(-1), true);
                label = m.ToString("MMMM yyyy", C);
                break;
            case "week" when q.Week is { } w:
                from = SchoolWeek.StartOf(w); to = from.AddDays(4); bounded = true;
                label = $"Week of {from.ToString("d MMM yyyy", C)}";
                break;
            case "day" when q.Date is { } d:
                (from, to, bounded) = (d, d, true);
                label = d.ToString("dddd d MMMM yyyy", C);
                break;
            case "range" when q.From is not null || q.To is not null:
                from = q.From ?? q.To!.Value; to = q.To ?? q.From!.Value;
                if (to < from) (from, to) = (to, from);
                bounded = true;
                label = "Custom dates";
                break;
            default:
                q.Period = "year";
                if (yearRange is { } yr) { (from, to, bounded) = (yr.Item1, yr.Item2, true); label = $"{year!.Name} academic year"; }
                else label = "All dates";
                break;
        }

        List<int>? classIds = null;
        string audience = "Entire school";
        if (q.StudentId is { } sid)
        {
            var s = await db.Students.AsNoTracking().Where(x => x.Id == sid).Select(x => new { x.FullName, Class = x.SchoolClass.Name }).FirstOrDefaultAsync(ct);
            audience = s is null ? "Student" : $"Student: {s.FullName} ({s.Class})";
        }
        else if (q.ClassId is { } cid)
        {
            classIds = [cid];
            audience = "Class: " + (await db.Classes.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "—");
        }
        else if (GradeCatalog.All.Any(g => g.Key == q.Grade))
        {
            classIds = (await db.Classes.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct))
                .Where(c => GradeCatalog.KeyOf(c.Name) == q.Grade).Select(c => c.Id).ToList();
            audience = "Grade: " + GradeCatalog.Label(q.Grade);
        }
        else q.Grade = null;

        return new Resolved(new BelongingsScope(year?.Name, quarter?.Name, from, to, label, audience, bounded), year, years, classIds);
    }

    public async Task<HistoryOptions> OptionsAsync(BelongingsQuery query, bool academic, CancellationToken ct = default)
    {
        var r = await ResolveAsync(query, ct);
        var months = new List<(string, string)>();
        var weeks = new List<HistoryWeekOption>();
        if (r.Year is { } y)
        {
            var configured = y.Quarters.Where(q => q.IsConfigured).ToList();
            var dates = configured.SelectMany(q => new[] { q.StartDate!.Value, q.EndDate!.Value }).ToList();
            if (dates.Count > 0)
                for (var m = new DateOnly(dates.Min().Year, dates.Min().Month, 1); m <= dates.Max(); m = m.AddMonths(1))
                    months.Add((m.ToString("yyyy-MM", C), m.ToString("MMMM yyyy", C)));
            foreach (var q in configured)
                foreach (var w in q.Weeks)
                    weeks.Add(new(w.WeekStart, $"Q{q.Number} · Week {w.Number} · {w.WeekStart.ToString("d MMM", C)} – {w.WeekEnd.ToString("d MMM yyyy", C)}"));
        }
        var classes = (await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Select(c => new HistoryClassOption(c.Id, c.Name, GradeCatalog.KeyOf(c.Name))).ToList();
        var students = await db.Students.AsNoTracking().OrderBy(s => s.FullName).Select(s => new StudentOption(s.Id, s.FullName, s.SchoolClass.Name)).ToListAsync(ct);
        IReadOnlyList<string> subjects = [];
        IReadOnlyList<(int, string)> teachers = [];
        IReadOnlyList<(int, string)> periods = [];
        if (academic)
        {
            // Subjects, teachers and periods come from the lessons actually recorded (the snapshot), never invented.
            subjects = await db.AcademicMaterialRecords.AsNoTracking().Select(m => m.Subject).Distinct().OrderBy(s => s).ToListAsync(ct);
            teachers = (await db.AcademicMaterialRecords.AsNoTracking().Where(m => m.TeacherId != null && m.TeacherName != null)
                    .Select(m => new { Id = m.TeacherId!.Value, m.TeacherName }).Distinct().ToListAsync(ct))
                .GroupBy(t => t.Id).Select(g => (g.Key, g.First().TeacherName!)).OrderBy(t => t.Item2).ToList();
            periods = (await db.AcademicMaterialRecords.AsNoTracking().Where(m => m.PeriodNumber != null)
                    .Select(m => new { N = m.PeriodNumber!.Value, m.PeriodName }).Distinct().ToListAsync(ct))
                .GroupBy(p => p.N).OrderBy(g => g.Key).Select(g => (g.Key, g.First().PeriodName ?? $"Period {g.Key}")).ToList();
        }
        return new HistoryOptions(r.Years, r.Year, months, weeks, classes, students, subjects, teachers, periods);
    }

    // ------------------------------------------------------------------ Personal Belongings

    public async Task<PersonalHistory> PersonalAsync(BelongingsQuery query, CancellationToken ct = default)
    {
        var r = await ResolveAsync(query, ct);
        var (from, to) = (r.Scope.From, r.Scope.To);
        var q = db.BelongingsRecords.AsNoTracking().Where(x => x.Date >= from && x.Date <= to
            && (x.Jacket != null || x.SchoolBag != null || x.PencilCase != null || x.WaterFlask != null || x.LunchBox != null || x.Notes != null));
        if (query.StudentId is { } sid) q = q.Where(x => x.StudentId == sid);
        else if (r.ClassIds is { } ids) q = q.Where(x => ids.Contains(x.SchoolClassId));
        var rows = (await q.Select(x => new
            {
                x.Id, x.Date, Class = x.SchoolClass.Name, Order = x.SchoolClass.SortOrder, x.StudentId, Student = x.Student.FullName,
                x.Jacket, x.SchoolBag, x.PencilCase, x.WaterFlask, x.LunchBox, x.Notes, x.RecordedBy, x.UpdatedBy, x.UpdatedAt,
                Corrections = db.BelongingsChanges.Count(h => h.BelongingsRecordId == x.Id && h.AcademicMaterialRecordId == null)
                              + db.BelongingsNoteHistory.Count(h => h.BelongingsRecordId == x.Id),
            }).ToListAsync(ct))
            .Select(x => new PersonalHistoryRow(x.Id, x.Date, x.Class, x.Order, x.StudentId, x.Student,
                [x.Jacket, x.SchoolBag, x.PencilCase, x.WaterFlask, x.LunchBox], x.Notes, x.RecordedBy, x.UpdatedBy, x.UpdatedAt, x.Corrections))
            .Where(x => query.Status switch
            {
                "complete" => x.Complete,
                "incomplete" => !x.Complete,
                "missing" => x.Missing.Count > 0,
                { } s when s.StartsWith("missing:", StringComparison.Ordinal) && PersonalItems.All.FirstOrDefault(i => i.Key == s[8..]) is { } item
                    => PersonalItems.Missing(x.Items).Contains(item.Label),
                _ => true,
            })
            .OrderByDescending(x => x.Date).ThenBy(x => x.ClassOrder).ThenBy(x => x.StudentName, StringComparer.CurrentCultureIgnoreCase).ToList();

        var byClass = rows.GroupBy(x => (x.ClassName, x.ClassOrder)).OrderBy(g => g.Key.ClassOrder).ThenBy(g => g.Key.ClassName)
            .Select(g => new GroupStat(g.Key.ClassName, g.Count(), g.Count(x => x.Complete), g.Aggregate(ReadinessCounts.Zero, (a, x) => a + x.Readiness))).ToList();
        var repeated = rows.Where(x => x.Missing.Count > 0).GroupBy(x => x.StudentId).Where(g => g.Count() >= 2)
            .Select(g => new RepeatedMissing(g.Key, g.First().StudentName, g.First().ClassName, g.Count(),
                string.Join(", ", g.SelectMany(x => x.Missing).GroupBy(i => i).OrderByDescending(i => i.Count()).Select(i => $"{i.Key} ×{i.Count()}"))))
            .OrderByDescending(x => x.Days).ThenBy(x => x.Name).Take(20).ToList();
        return new PersonalHistory(r.Scope, rows.Count, rows.Count(x => x.Complete), rows.Count(x => x.Missing.Count > 0),
            rows.Aggregate(ReadinessCounts.Zero, (a, x) => a + x.Readiness), byClass, repeated,
            Trend(rows, x => x.Date, x => x.Complete, x => x.Readiness), rows, Page(rows, query));
    }

    public async Task<PersonalRecordDetail?> PersonalRecordAsync(int recordId, CancellationToken ct = default)
    {
        var x = await db.BelongingsRecords.AsNoTracking().Where(r => r.Id == recordId).Select(r => new
        {
            r.Id, r.Date, r.SchoolClassId, Class = r.SchoolClass.Name, Order = r.SchoolClass.SortOrder, r.StudentId, Student = r.Student.FullName,
            r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox, r.Notes, r.RecordedBy, r.UpdatedBy, r.UpdatedAt,
        }).FirstOrDefaultAsync(ct);
        if (x is null) return null;
        var notes = await db.BelongingsNoteHistory.AsNoTracking().Where(h => h.BelongingsRecordId == recordId).OrderByDescending(h => h.ChangedAt)
            .Select(h => new NoteVersion(h.PreviousNotes, h.ChangedAt, h.ChangedBy)).ToListAsync(ct);
        var changes = await Changes(db.BelongingsChanges.Where(h => h.BelongingsRecordId == recordId && h.AcademicMaterialRecordId == null), ct);
        var row = new PersonalHistoryRow(x.Id, x.Date, x.Class, x.Order, x.StudentId, x.Student, [x.Jacket, x.SchoolBag, x.PencilCase, x.WaterFlask, x.LunchBox],
            x.Notes, x.RecordedBy, x.UpdatedBy, x.UpdatedAt, notes.Count + changes.Count);
        return new PersonalRecordDetail(row, x.SchoolClassId, notes, changes);
    }

    // ------------------------------------------------------------------ Academic Belongings

    public async Task<AcademicHistory> AcademicAsync(BelongingsQuery query, CancellationToken ct = default)
    {
        var r = await ResolveAsync(query, ct);
        var (from, to) = (r.Scope.From, r.Scope.To);
        var q = db.AcademicMaterialRecords.AsNoTracking().Where(m => m.Record.Date >= from && m.Record.Date <= to);
        if (query.StudentId is { } sid) q = q.Where(m => m.Record.StudentId == sid);
        else if (r.ClassIds is { } ids) q = q.Where(m => ids.Contains(m.Record.SchoolClassId));
        if (!string.IsNullOrWhiteSpace(query.Subject)) q = q.Where(m => m.Subject == query.Subject);
        if (query.TeacherId is { } tid) q = q.Where(m => m.TeacherId == tid);
        if (query.PeriodNumber is { } pn) q = q.Where(m => m.PeriodNumber == pn);
        var rows = (await Rows(q, ct))
            .Where(x => BelongingsStatus.Matches(x.Book, query.Book) && BelongingsStatus.Matches(x.CopyBook, query.CopyBook))
            .Where(x => query.Status switch { "complete" => x.Complete, "incomplete" => !x.Complete, _ => true })
            .OrderByDescending(x => x.Date).ThenBy(x => x.PeriodNumber ?? int.MaxValue).ThenBy(x => x.ClassOrder)
            .ThenBy(x => x.StudentName, StringComparer.CurrentCultureIgnoreCase).ToList();

        GroupStat Group(IGrouping<string, AcademicHistoryRow> g) =>
            new(g.Key, g.Count(), g.Count(x => x.Complete), g.Aggregate(ReadinessCounts.Zero, (a, x) => a + x.Readiness));
        var quarterNames = r.Years.SelectMany(y => y.Quarters.Select(q => (q.Id, Name: $"{q.Name} · {y.Name}"))).ToDictionary(x => x.Id, x => x.Name);
        return new AcademicHistory(r.Scope,
            rows.Select(x => x.LessonKey).Distinct().Count(), rows.Count, rows.Count(x => x.Complete),
            rows.Count(x => x.Book == ItemStatus.Red), rows.Count(x => x.CopyBook == ItemStatus.Red),
            rows.Aggregate(ReadinessCounts.Zero, (a, x) => a + x.Readiness),
            rows.GroupBy(x => x.ClassName).OrderBy(g => g.Min(x => x.ClassOrder)).ThenBy(g => g.Key).Select(Group).ToList(),
            rows.GroupBy(x => x.Subject).OrderBy(g => g.Key).Select(Group).ToList(),
            rows.GroupBy(x => x.TeacherName ?? "Not recorded (daily records)").OrderBy(g => g.Key).Select(Group).ToList(),
            rows.GroupBy(x => quarterNames.GetValueOrDefault(x.QuarterId, "—")).OrderBy(g => g.Key).Select(Group).ToList(),
            Trend(rows, x => x.Date, x => x.Complete, x => x.Readiness),
            rows.GroupBy(x => new DateOnly(x.Date.Year, x.Date.Month, 1)).OrderBy(g => g.Key)
                .Select(g => new GroupStat(g.Key.ToString("MMMM yyyy", C), g.Count(), g.Count(x => x.Complete), g.Aggregate(ReadinessCounts.Zero, (a, x) => a + x.Readiness))).ToList(),
            rows, Page(rows, query));
    }

    public async Task<AcademicLessonDetail?> AcademicRecordAsync(int rowId, CancellationToken ct = default)
    {
        var one = (await Rows(db.AcademicMaterialRecords.Where(m => m.Id == rowId), ct)).FirstOrDefault();
        if (one is null) return null;
        var classId = await db.BelongingsRecords.Where(b => b.Id == one.RecordId).Select(b => b.SchoolClassId).FirstAsync(ct);
        // The whole lesson as recorded on that date (or, for a day-based row, the row itself).
        var all = one.SessionId is { } sid
            ? (await Rows(db.AcademicMaterialRecords.Where(m => m.TimetableAssignmentId == sid && m.Record.Date == one.Date), ct))
                .OrderBy(x => x.StudentName, StringComparer.CurrentCultureIgnoreCase).ToList()
            : [one];
        var ids = all.Select(x => x.Id).Cast<int?>().ToList();
        var changes = await Changes(db.BelongingsChanges.Where(h => ids.Contains(h.AcademicMaterialRecordId)), ct);
        return new AcademicLessonDetail(one, classId, all, changes);
    }

    private async Task<List<AcademicHistoryRow>> Rows(IQueryable<AcademicMaterialRecord> q, CancellationToken ct) =>
        await q.AsNoTracking().Select(m => new AcademicHistoryRow(
            m.Id, m.BelongingsRecordId, m.Record.Date, m.Record.SchoolClass.Name, m.Record.SchoolClass.SortOrder, m.Record.StudentId, m.Record.Student.FullName,
            m.Record.AcademicQuarterId, m.TimetableAssignmentId, m.PeriodNumber, m.PeriodName, m.LessonStart, m.LessonEnd, m.Subject, m.TeacherId, m.TeacherName,
            m.Book, m.CopyBook, m.Notes, m.RecordedBy, m.UpdatedBy ?? m.Record.UpdatedBy, m.UpdatedAt,
            db.BelongingsChanges.Count(h => h.AcademicMaterialRecordId == m.Id))).ToListAsync(ct);

    // ------------------------------------------------------------------ helpers

    private static async Task<List<ChangeLine>> Changes(IQueryable<BelongingsChange> q, CancellationToken ct) =>
        await q.AsNoTracking().OrderByDescending(h => h.ChangedAt).Select(h => new ChangeLine(h.Item, h.Context, h.Before, h.After, h.ChangedAt, h.ChangedBy)).ToListAsync(ct);

    private static IReadOnlyList<TrendPoint> Trend<T>(IReadOnlyList<T> rows, Func<T, DateOnly> date, Func<T, bool> complete, Func<T, ReadinessCounts> readiness) =>
        rows.GroupBy(x => SchoolWeek.StartOf(date(x))).OrderBy(g => g.Key)
            .Select(g => new TrendPoint(g.Key, g.Count(), g.Count(complete), g.Aggregate(ReadinessCounts.Zero, (a, x) => a + readiness(x)))).ToList();

    private static PagedResult<T> Page<T>(IReadOnlyList<T> rows, BelongingsQuery q)
    {
        var size = Paging.NormalizePageSize(q.PageSize);
        var page = Paging.ClampPage(q.Page, rows.Count, size);
        return new PagedResult<T> { Items = rows.Skip((page - 1) * size).Take(size).ToList(), Page = page, PageSize = size, TotalCount = rows.Count };
    }
}
