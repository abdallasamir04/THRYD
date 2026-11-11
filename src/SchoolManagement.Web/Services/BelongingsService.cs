using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>The 13 grades, derived from existing class names (PreK, KG1A → KG1, G7B → Grade 7). No grade table is duplicated.</summary>
public static class GradeCatalog
{
    public static readonly IReadOnlyList<(string Key, string Label)> All =
        [("PREK", "Pre-K"), ("KG1", "KG1"), ("KG2", "KG2"), .. Enumerable.Range(1, 10).Select(n => ($"G{n}", $"Grade {n}"))];

    public static string? KeyOf(string? className)
    {
        if (ClassNumbering.Parse(className) is { } p) return p.Stage == "KG" ? $"KG{p.Number}" : $"G{p.Number}";
        // "PreK", "Pre-K", "PreK A" → PREK (ClassKey drops spaces and separators).
        return TextNormalizer.ClassKey(className).StartsWith("PREK", StringComparison.Ordinal) ? "PREK" : null;
    }

    public static string Label(string? key) => All.FirstOrDefault(g => g.Key == key).Label ?? key ?? "—";
}

/// <summary>Green / Red / N/A counts. Readiness = Green ÷ (Green + Red); N/A never counts; nothing applicable → null (shown as N/A).</summary>
public readonly record struct ReadinessCounts(int Green, int Red, int NotApplicable)
{
    public static readonly ReadinessCounts Zero = new(0, 0, 0);
    public double? Percent => Green + Red == 0 ? null : Math.Round(100.0 * Green / (Green + Red), 2);
    public int Recorded => Green + Red + NotApplicable;
    public static ReadinessCounts operator +(ReadinessCounts a, ReadinessCounts b) => new(a.Green + b.Green, a.Red + b.Red, a.NotApplicable + b.NotApplicable);

    public static ReadinessCounts Of(IEnumerable<ItemStatus?> statuses)
    {
        int g = 0, r = 0, n = 0;
        foreach (var s in statuses)
        {
            if (s == ItemStatus.Green) g++;
            else if (s == ItemStatus.Red) r++;
            else if (s == ItemStatus.NotApplicable) n++;
        }
        return new(g, r, n);
    }

    public static string Text(double? percent) => percent is { } p ? p.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "N/A";
}

/// <summary>Personal and academic counts; Overall adds them (so it is never an average of the two percentages).</summary>
public readonly record struct Readiness(ReadinessCounts Personal, ReadinessCounts Academic)
{
    public static readonly Readiness Zero = new(ReadinessCounts.Zero, ReadinessCounts.Zero);
    public ReadinessCounts Overall => Personal + Academic;
    public static Readiness operator +(Readiness a, Readiness b) => new(a.Personal + b.Personal, a.Academic + b.Academic);
    public bool IsRecorded => Overall.Recorded > 0;
}

public sealed record WeekInfo(int Number, DateOnly WeekStart, DateOnly WeekEnd, IReadOnlyList<DateOnly> Days)
{
    public string Label => $"Week {Number} · {WeekStart.ToString("d MMM", CultureInfo.InvariantCulture)} – {WeekEnd.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
}

/// <summary>Quarter calendar rules: 8 school weeks (Sunday–Thursday) from the configured dates.</summary>
public static class QuarterCalendar
{
    /// <summary>Week 1 is the school week holding the start date; days outside start..end are left out (partial weeks).</summary>
    public static IReadOnlyList<WeekInfo> Weeks(DateOnly start, DateOnly end)
    {
        var first = SchoolWeek.StartOf(start);
        return Enumerable.Range(0, AcademicQuarter.Weeks).Select(i =>
        {
            var ws = first.AddDays(7 * i);
            var days = ScheduleRules.Days.Select(d => ScheduleRules.DateOf(ws, d)).Where(d => d >= start && d <= end).ToList();
            return new WeekInfo(i + 1, ws, ws.AddDays(4), days);
        }).ToList();
    }

    public static IReadOnlyList<WeekInfo> Weeks(AcademicQuarter q) => q.StartDate is { } s && q.EndDate is { } e ? Weeks(s, e) : [];

    public static IEnumerable<KeyValuePair<string, string>> Validate(DateOnly? start, DateOnly? end)
    {
        if (start is null || end is null) { yield return new("StartDate", "Enter both the start and the end date."); yield break; }
        if (!ScheduleRules.IsSchoolDay(start.Value.DayOfWeek)) yield return new("StartDate", "The quarter must start on a school day (Sunday–Thursday).");
        if (!ScheduleRules.IsSchoolDay(end.Value.DayOfWeek)) yield return new("EndDate", "The quarter must end on a school day (Sunday–Thursday).");
        if (end < start) { yield return new("EndDate", "The end date must be after the start date."); yield break; }
        var weeks = (SchoolWeek.StartOf(end.Value).DayNumber - SchoolWeek.StartOf(start.Value).DayNumber) / 7 + 1;
        if (weeks != AcademicQuarter.Weeks)
            yield return new("EndDate", $"These dates cover {weeks} school week(s). A quarter has exactly {AcademicQuarter.Weeks}: the end date must fall in the 8th week (" +
                $"{SchoolWeek.StartOf(start.Value).AddDays(49).ToString("d MMM", CultureInfo.InvariantCulture)} – {SchoolWeek.StartOf(start.Value).AddDays(53).ToString("d MMM yyyy", CultureInfo.InvariantCulture)}).");
    }
}

// ------------------------------------------------------------------ view records

public sealed record QuarterView(int Id, int YearId, string YearName, int Number, string Name, DateOnly? StartDate, DateOnly? EndDate, int Records)
{
    public bool IsConfigured => StartDate is not null;
    public IReadOnlyList<WeekInfo> Weeks => StartDate is { } s && EndDate is { } e ? QuarterCalendar.Weeks(s, e) : [];
}

public sealed record YearView(int Id, string Name, IReadOnlyList<QuarterView> Quarters);

public sealed record StudentOption(int Id, string Name, string ClassName);

public sealed record MaterialView(int Id, int DepartmentId, string DepartmentName, string Subject, ItemStatus Book, ItemStatus CopyBook);

public sealed record NoteVersion(string? Notes, DateTime ChangedAt, string? ChangedBy);

public sealed class DayView
{
    public int? RecordId { get; init; }
    public ItemStatus? Jacket { get; init; }
    public ItemStatus? SchoolBag { get; init; }
    public ItemStatus? PencilCase { get; init; }
    public ItemStatus? WaterFlask { get; init; }
    public ItemStatus? LunchBox { get; init; }
    public string? Notes { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public IReadOnlyList<MaterialView> Materials { get; init; } = [];
    public IReadOnlyList<NoteVersion> NoteHistory { get; init; } = [];
    public Readiness Readiness { get; init; }
}

public sealed record DayPoint(DateOnly Date, Readiness Readiness, bool Recorded, bool Due);

public sealed record WeekPoint(WeekInfo Week, Readiness Readiness, int RecordedDays, int MissingDays);

public sealed class PeriodAnalytics<TPoint>
{
    public required IReadOnlyList<TPoint> Points { get; init; }
    public required Readiness Total { get; init; }
    public required int RecordedDays { get; init; }
    public required int MissingDays { get; init; }
    public required int SchoolDays { get; init; }
}

public sealed record StudentWeekRow(int StudentId, string Name, string ClassName, Readiness Readiness, int RecordedDays, int MissingDays);

public sealed record PersonalInput(int StudentId, DateOnly Date, string? Grade,
    ItemStatus? Jacket, ItemStatus? SchoolBag, ItemStatus? PencilCase, ItemStatus? WaterFlask, ItemStatus? LunchBox, string? Notes);

public sealed record MaterialInput(int StudentId, DateOnly Date, string? Grade, int? DepartmentId, string? Subject, ItemStatus? Book, ItemStatus? CopyBook);

public interface IBelongingsService
{
    Task<IReadOnlyList<YearView>> YearsAsync(CancellationToken ct = default);
    Task<QuarterView?> QuarterAsync(int id, CancellationToken ct = default);
    Task<QuarterView?> QuarterForDateAsync(DateOnly date, CancellationToken ct = default);
    Task<ServiceResult> CreateYearAsync(string? name, CancellationToken ct = default);
    Task<ServiceResult> UpdateQuarterAsync(int id, string? name, DateOnly? start, DateOnly? end, CancellationToken ct = default);

    Task<IReadOnlyList<StudentOption>> StudentsAsync(string? grade, CancellationToken ct = default);
    Task<DayView> DayAsync(int studentId, DateOnly date, CancellationToken ct = default);
    Task<ServiceResult> SavePersonalAsync(PersonalInput input, CancellationToken ct = default);
    Task<ServiceResult> SaveMaterialAsync(MaterialInput input, CancellationToken ct = default);
    Task<ServiceResult> DeleteMaterialAsync(int id, CancellationToken ct = default);

    Task<PeriodAnalytics<DayPoint>> WeekAsync(int studentId, QuarterView quarter, int week, CancellationToken ct = default);
    Task<PeriodAnalytics<WeekPoint>> QuarterAnalyticsAsync(int studentId, QuarterView quarter, CancellationToken ct = default);
    Task<IReadOnlyList<StudentWeekRow>> GradeWeekAsync(string grade, QuarterView quarter, int week, CancellationToken ct = default);
}

/// <summary>
/// Belongings (administration only): daily personal items, academic materials per subject, notes, and
/// readiness. Every percentage comes from <see cref="ReadinessCounts"/>: Green ÷ (Green + Red), N/A and
/// missing records excluded; weekly / quarterly figures add the counts, never average percentages.
/// </summary>
public sealed class BelongingsService(SchoolDbContext db, ITimetableService timetable, ICurrentUser currentUser) : IBelongingsService
{
    // ------------------------------------------------------------------ calendar

    public async Task<IReadOnlyList<YearView>> YearsAsync(CancellationToken ct = default)
    {
        var quarters = await Quarters(db.AcademicQuarters).ToListAsync(ct);
        var years = await db.AcademicYears.AsNoTracking().OrderByDescending(y => y.Name).Select(y => new { y.Id, y.Name }).ToListAsync(ct);
        return years.Select(y => new YearView(y.Id, y.Name, quarters.Where(q => q.YearId == y.Id).OrderBy(q => q.Number).ToList())).ToList();
    }

    public Task<QuarterView?> QuarterAsync(int id, CancellationToken ct = default) =>
        Quarters(db.AcademicQuarters.Where(q => q.Id == id)).FirstOrDefaultAsync(ct);

    public Task<QuarterView?> QuarterForDateAsync(DateOnly date, CancellationToken ct = default) =>
        Quarters(db.AcademicQuarters.Where(q => q.StartDate <= date && q.EndDate >= date)).FirstOrDefaultAsync(ct);

    public async Task<ServiceResult> CreateYearAsync(string? name, CancellationToken ct = default)
    {
        var clean = TextNormalizer.CleanText(name);
        if (clean.Length == 0) return ServiceResult.Fail("Name", "Enter the academic year, e.g. 2026–2027.");
        if (clean.Length > AcademicYear.NameMaxLength) return ServiceResult.Fail("Name", $"Use {AcademicYear.NameMaxLength} characters or fewer.");
        if (await db.AcademicYears.AnyAsync(y => y.Name == clean, ct)) return ServiceResult.Fail("Name", $"{clean} already exists.");
        var year = new AcademicYear { Name = clean };
        for (var n = 1; n <= 4; n++) year.Quarters.Add(new AcademicQuarter { Number = n, Name = $"Quarter {n}" });
        db.AcademicYears.Add(year);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return ServiceResult.Fail("Name", $"{clean} already exists."); }
        return ServiceResult.Ok(year.Id);
    }

    public async Task<ServiceResult> UpdateQuarterAsync(int id, string? name, DateOnly? start, DateOnly? end, CancellationToken ct = default)
    {
        var q = await db.AcademicQuarters.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (q is null) return ServiceResult.Fail("", "This quarter no longer exists.");
        var result = ServiceResult.Ok(id);
        var clean = TextNormalizer.CleanText(name);
        if (clean.Length == 0) result.AddError("Name", "Enter the quarter name.");
        else if (clean.Length > AcademicQuarter.NameMaxLength) result.AddError("Name", $"Use {AcademicQuarter.NameMaxLength} characters or fewer.");
        foreach (var (k, v) in QuarterCalendar.Validate(start, end)) result.AddError(k, v);
        if (!result.Succeeded) return result;

        // Quarters of a year are in order, and no two quarters (any year) overlap.
        var others = await db.AcademicQuarters.AsNoTracking().Where(x => x.Id != id && x.StartDate != null)
            .Select(x => new { x.AcademicYearId, x.Number, x.Name, x.StartDate, x.EndDate, Year = x.AcademicYear.Name }).ToListAsync(ct);
        var overlap = others.FirstOrDefault(o => o.StartDate <= end && o.EndDate >= start);
        if (overlap is not null)
            return ServiceResult.Fail("StartDate", $"These dates overlap {overlap.Name} of {overlap.Year} ({Fmt(overlap.StartDate!.Value)} – {Fmt(overlap.EndDate!.Value)}).");
        var before = others.FirstOrDefault(o => o.AcademicYearId == q.AcademicYearId && o.Number < q.Number && o.StartDate > start);
        var after = others.FirstOrDefault(o => o.AcademicYearId == q.AcademicYearId && o.Number > q.Number && o.StartDate < start);
        if (before is not null || after is not null)
            return ServiceResult.Fail("StartDate", $"Quarter {q.Number} must come {(before is not null ? "after" : "before")} {(before ?? after)!.Name} of the same year.");

        // Saved records must stay inside the quarter.
        var outside = await db.BelongingsRecords.CountAsync(r => r.AcademicQuarterId == id && (r.Date < start || r.Date > end), ct);
        if (outside > 0) return ServiceResult.Fail("StartDate", $"{outside} saved record(s) of this quarter fall outside these dates. Keep the dates around them.");

        (q.Name, q.StartDate, q.EndDate) = (clean, start, end);
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static IQueryable<QuarterView> Quarters(IQueryable<AcademicQuarter> q) =>
        q.AsNoTracking().OrderBy(x => x.Number).Select(x => new QuarterView(x.Id, x.AcademicYearId, x.AcademicYear.Name, x.Number, x.Name, x.StartDate, x.EndDate, 0));

    // ------------------------------------------------------------------ students

    /// <summary>Students of the classes whose name belongs to the grade (existing classes and students).</summary>
    public async Task<IReadOnlyList<StudentOption>> StudentsAsync(string? grade, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(grade)) return [];
        var classIds = (await db.Classes.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Where(c => GradeCatalog.KeyOf(c.Name) == grade).Select(c => c.Id).ToList();
        return await db.Students.AsNoTracking().Where(s => classIds.Contains(s.SchoolClassId))
            .OrderBy(s => s.FullName).Select(s => new StudentOption(s.Id, s.FullName, s.SchoolClass.Name)).ToListAsync(ct);
    }

    // ------------------------------------------------------------------ daily record

    public async Task<DayView> DayAsync(int studentId, DateOnly date, CancellationToken ct = default)
    {
        var r = await db.BelongingsRecords.AsNoTracking().Where(x => x.StudentId == studentId && x.Date == date)
            .Select(x => new
            {
                x.Id, x.Jacket, x.SchoolBag, x.PencilCase, x.WaterFlask, x.LunchBox, x.Notes, x.UpdatedAt, x.UpdatedBy,
                Materials = x.Materials.OrderBy(m => m.Department.SortOrder).ThenBy(m => m.Subject)
                    .Select(m => new MaterialView(m.Id, m.DepartmentId, m.Department.Name, m.Subject, m.Book, m.CopyBook)).ToList(),
            }).FirstOrDefaultAsync(ct);
        if (r is null) return new DayView { Readiness = Readiness.Zero };
        var history = await db.BelongingsNoteHistory.AsNoTracking().Where(h => h.BelongingsRecordId == r.Id)
            .OrderByDescending(h => h.ChangedAt).Select(h => new NoteVersion(h.PreviousNotes, h.ChangedAt, h.ChangedBy)).ToListAsync(ct);
        return new DayView
        {
            RecordId = r.Id, Jacket = r.Jacket, SchoolBag = r.SchoolBag, PencilCase = r.PencilCase, WaterFlask = r.WaterFlask, LunchBox = r.LunchBox,
            Notes = r.Notes, UpdatedAt = r.UpdatedAt, UpdatedBy = r.UpdatedBy, Materials = r.Materials, NoteHistory = history,
            Readiness = new Readiness(ReadinessCounts.Of([r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox]),
                ReadinessCounts.Of(r.Materials.SelectMany(m => new ItemStatus?[] { m.Book, m.CopyBook }))),
        };
    }

    public async Task<ServiceResult> SavePersonalAsync(PersonalInput input, CancellationToken ct = default)
    {
        var result = ServiceResult.Ok();
        foreach (var (field, value) in new (string, ItemStatus?)[] { ("Jacket", input.Jacket), ("SchoolBag", input.SchoolBag), ("PencilCase", input.PencilCase), ("WaterFlask", input.WaterFlask), ("LunchBox", input.LunchBox) })
            if (value is not { } v || !Enum.IsDefined(v)) result.AddError(field, "Choose Green, Red or N/A.");
        var notes = input.Notes?.Trim() ?? "";
        if (notes.Length > BelongingsRecord.NotesMaxLength) result.AddError("Notes", $"Use {BelongingsRecord.NotesMaxLength} characters or fewer.");
        if (!result.Succeeded) return result;

        var (context, error) = await ContextAsync(input.StudentId, input.Date, input.Grade, ct);
        if (error is not null) return error;
        var record = await RecordAsync(input.StudentId, input.Date, context!.Value, ct);
        (record.Jacket, record.SchoolBag, record.PencilCase, record.WaterFlask, record.LunchBox) = (input.Jacket, input.SchoolBag, input.PencilCase, input.WaterFlask, input.LunchBox);
        var newNotes = notes.Length == 0 ? null : notes;
        if (record.Id != 0 && record.Notes != newNotes)
            db.BelongingsNoteHistory.Add(new BelongingsNoteHistory { BelongingsRecordId = record.Id, PreviousNotes = record.Notes, ChangedAt = DateTime.UtcNow, ChangedBy = currentUser.Name });
        record.Notes = newNotes;
        record.UpdatedBy = currentUser.Name;
        return await SaveAsync(record, ct);
    }

    public async Task<ServiceResult> SaveMaterialAsync(MaterialInput input, CancellationToken ct = default)
    {
        var result = ServiceResult.Ok();
        if (input.DepartmentId is null) result.AddError("DepartmentId", "Choose the department.");
        if (string.IsNullOrWhiteSpace(input.Subject)) result.AddError("Subject", "Choose the subject.");
        if (input.Book is not { } b || !Enum.IsDefined(b)) result.AddError("Book", "Choose Green, Red or N/A for the book.");
        if (input.CopyBook is not { } c || !Enum.IsDefined(c)) result.AddError("CopyBook", "Choose Green, Red or N/A for the copy book.");
        if (!result.Succeeded) return result;

        // The subject must belong to the department (same department → subject list as the timetable).
        var subject = (await timetable.SubjectsAsync(input.DepartmentId!.Value, ct))
            .FirstOrDefault(s => string.Equals(s, input.Subject!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (subject is null) return ServiceResult.Fail("Subject", "Choose a subject of the selected department.");

        var (context, error) = await ContextAsync(input.StudentId, input.Date, input.Grade, ct);
        if (error is not null) return error;
        var record = await RecordAsync(input.StudentId, input.Date, context!.Value, ct);
        var row = record.Materials.FirstOrDefault(m => m.DepartmentId == input.DepartmentId && m.Subject == subject);
        if (row is null) record.Materials.Add(row = new AcademicMaterialRecord { DepartmentId = input.DepartmentId!.Value, Subject = subject });
        (row.Book, row.CopyBook, row.UpdatedAt) = (input.Book!.Value, input.CopyBook!.Value, DateTime.UtcNow);
        record.UpdatedBy = currentUser.Name;
        return await SaveAsync(record, ct);
    }

    public async Task<ServiceResult> DeleteMaterialAsync(int id, CancellationToken ct = default)
    {
        var deleted = await db.AcademicMaterialRecords.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? ServiceResult.Fail("", "This subject record no longer exists.") : ServiceResult.Ok(id);
    }

    /// <summary>Student in the selected grade, date a school day of a configured quarter.</summary>
    private async Task<((int ClassId, int QuarterId)?, ServiceResult?)> ContextAsync(int studentId, DateOnly date, string? grade, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().Where(s => s.Id == studentId).Select(s => new { s.SchoolClassId, ClassName = s.SchoolClass.Name }).FirstOrDefaultAsync(ct);
        if (student is null) return (null, ServiceResult.Fail("StudentId", "Choose a student from the list."));
        if (string.IsNullOrEmpty(grade) || GradeCatalog.KeyOf(student.ClassName) != grade)
            return (null, ServiceResult.Fail("StudentId", $"This student is in {student.ClassName}, not in {GradeCatalog.Label(grade)}. Choose a student of the selected grade."));
        if (!ScheduleRules.IsSchoolDay(date.DayOfWeek)) return (null, ServiceResult.Fail("Date", "Choose a school day (Sunday–Thursday)."));
        var quarter = await QuarterForDateAsync(date, ct);
        if (quarter is null) return (null, ServiceResult.Fail("Date", "This date is not inside a configured quarter. Set the quarter dates first."));
        return ((student.SchoolClassId, quarter.Id), null);
    }

    private async Task<BelongingsRecord> RecordAsync(int studentId, DateOnly date, (int ClassId, int QuarterId) context, CancellationToken ct)
    {
        var record = await db.BelongingsRecords.Include(r => r.Materials).FirstOrDefaultAsync(r => r.StudentId == studentId && r.Date == date, ct);
        if (record is null)
        {
            record = new BelongingsRecord { StudentId = studentId, Date = date, SchoolClassId = context.ClassId, AcademicQuarterId = context.QuarterId };
            db.BelongingsRecords.Add(record);
        }
        return record;
    }

    private async Task<ServiceResult> SaveAsync(BelongingsRecord record, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return ServiceResult.Ok(record.Id);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This record was saved by someone else at the same moment. Reload and try again.");
        }
    }

    // ------------------------------------------------------------------ analytics (counts are added, never percentages averaged)

    private async Task<Dictionary<DateOnly, Readiness>> DaysAsync(IQueryable<BelongingsRecord> records, CancellationToken ct)
    {
        var rows = await records.AsNoTracking().Select(r => new
        {
            r.Date, r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox,
            Materials = r.Materials.Select(m => new { m.Book, m.CopyBook }).ToList(),
        }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Date, r => new Readiness(
            ReadinessCounts.Of([r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox]),
            ReadinessCounts.Of(r.Materials.SelectMany(m => new ItemStatus?[] { m.Book, m.CopyBook }))));
    }

    public async Task<PeriodAnalytics<DayPoint>> WeekAsync(int studentId, QuarterView quarter, int week, CancellationToken ct = default)
    {
        var info = quarter.Weeks.FirstOrDefault(w => w.Number == week);
        var days = info?.Days ?? [];
        var from = days.FirstOrDefault();
        var to = days.LastOrDefault();
        var data = days.Count == 0 ? [] : await DaysAsync(db.BelongingsRecords.Where(r => r.StudentId == studentId && r.Date >= from && r.Date <= to), ct);
        var today = SchoolWeek.Today;
        var points = days.Select(d =>
        {
            var has = data.TryGetValue(d, out var r) && r.IsRecorded;
            return new DayPoint(d, has ? r : Readiness.Zero, has, d <= today);
        }).ToList();
        return Summarise(points, p => p.Readiness, p => p.Recorded, p => p.Due);
    }

    public async Task<PeriodAnalytics<WeekPoint>> QuarterAnalyticsAsync(int studentId, QuarterView quarter, CancellationToken ct = default)
    {
        var weeks = quarter.Weeks;
        if (weeks.Count == 0) return new() { Points = [], Total = Readiness.Zero, RecordedDays = 0, MissingDays = 0, SchoolDays = 0 };
        var data = await DaysAsync(db.BelongingsRecords.Where(r => r.StudentId == studentId && r.AcademicQuarterId == quarter.Id), ct);
        var today = SchoolWeek.Today;
        var points = weeks.Select(w =>
        {
            var total = Readiness.Zero;
            int recorded = 0, missing = 0;
            foreach (var d in w.Days)
            {
                if (data.TryGetValue(d, out var r) && r.IsRecorded) { total += r; recorded++; }
                else if (d <= today) missing++;
            }
            return new WeekPoint(w, total, recorded, missing);
        }).ToList();
        return new()
        {
            Points = points,
            Total = points.Aggregate(Readiness.Zero, (a, p) => a + p.Readiness),
            RecordedDays = points.Sum(p => p.RecordedDays),
            MissingDays = points.Sum(p => p.MissingDays),
            SchoolDays = weeks.Sum(w => w.Days.Count),
        };
    }

    public async Task<IReadOnlyList<StudentWeekRow>> GradeWeekAsync(string grade, QuarterView quarter, int week, CancellationToken ct = default)
    {
        var students = await StudentsAsync(grade, ct);
        var days = quarter.Weeks.FirstOrDefault(w => w.Number == week)?.Days ?? [];
        if (students.Count == 0 || days.Count == 0) return [];
        var ids = students.Select(s => s.Id).ToList();
        var (from, to) = (days[0], days[^1]);
        var rows = await db.BelongingsRecords.AsNoTracking().Where(r => ids.Contains(r.StudentId) && r.Date >= from && r.Date <= to)
            .Select(r => new
            {
                r.StudentId, r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox,
                Materials = r.Materials.Select(m => new { m.Book, m.CopyBook }).ToList(),
            }).ToListAsync(ct);
        var due = days.Count(d => d <= SchoolWeek.Today);
        return students.Select(s =>
        {
            var mine = rows.Where(r => r.StudentId == s.Id)
                .Select(r => new Readiness(ReadinessCounts.Of([r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox]),
                    ReadinessCounts.Of(r.Materials.SelectMany(m => new ItemStatus?[] { m.Book, m.CopyBook }))))
                .Where(r => r.IsRecorded).ToList();
            return new StudentWeekRow(s.Id, s.Name, s.ClassName, mine.Aggregate(Readiness.Zero, (a, b) => a + b), mine.Count, Math.Max(0, due - mine.Count));
        }).ToList();
    }

    private static PeriodAnalytics<T> Summarise<T>(IReadOnlyList<T> points, Func<T, Readiness> readiness, Func<T, bool> recorded, Func<T, bool> due) => new()
    {
        Points = points,
        Total = points.Where(recorded).Aggregate(Readiness.Zero, (a, p) => a + readiness(p)),
        RecordedDays = points.Count(recorded),
        MissingDays = points.Count(p => !recorded(p) && due(p)),
        SchoolDays = points.Count,
    };

    private static string Fmt(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
