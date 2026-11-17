using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

// ====================================================================== shared definitions

/// <summary>The five personal belongings checked every school day (the columns of <see cref="BelongingsRecord"/>).</summary>
public static class PersonalItems
{
    public sealed record Item(string Key, string Label);

    public static readonly IReadOnlyList<Item> All =
    [
        new("Jacket", "Jacket"), new("SchoolBag", "School Bag"), new("PencilCase", "Pencil Case"),
        new("WaterFlask", "Water Flask"), new("LunchBox", "Lunch Box / Bag"),
    ];

    public static string Label(string key) => All.FirstOrDefault(i => i.Key == key)?.Label ?? key;

    public static ItemStatus?[] Of(BelongingsRecord r) => [r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox];

    public static ItemStatus? Get(BelongingsRecord r, string key) => key switch
    {
        "Jacket" => r.Jacket, "SchoolBag" => r.SchoolBag, "PencilCase" => r.PencilCase, "WaterFlask" => r.WaterFlask, "LunchBox" => r.LunchBox,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    public static void Set(BelongingsRecord r, string key, ItemStatus? value)
    {
        switch (key)
        {
            case "Jacket": r.Jacket = value; break;
            case "SchoolBag": r.SchoolBag = value; break;
            case "PencilCase": r.PencilCase = value; break;
            case "WaterFlask": r.WaterFlask = value; break;
            case "LunchBox": r.LunchBox = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(key));
        }
    }

    /// <summary>A day is complete when every item has a status (Available, Missing or N/A).</summary>
    public static bool Complete(IReadOnlyList<ItemStatus?> items) => items.All(s => s is not null);

    /// <summary>The labels of the items marked Missing.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<ItemStatus?> items) =>
        All.Select((item, i) => (item, i)).Where(x => items[x.i] == ItemStatus.Red).Select(x => x.item.Label).ToList();
}

/// <summary>How a belongings status reads on screen and in reports.</summary>
public static class BelongingsStatus
{
    public static readonly IReadOnlyList<ItemStatus> Choices = [ItemStatus.Green, ItemStatus.Red, ItemStatus.NotApplicable];

    public static string Label(ItemStatus? s) => s switch
    {
        ItemStatus.Green => "Available",
        ItemStatus.Red => "Missing",
        ItemStatus.NotApplicable => "N/A",
        _ => "Not checked",
    };

    public static string Slug(ItemStatus? s) => s switch
    {
        ItemStatus.Green => "ok", ItemStatus.Red => "missing", ItemStatus.NotApplicable => "na", _ => "none",
    };

    /// <summary>Filter values: "Available", "Missing", "N/A", "None" (not checked).</summary>
    public static bool Matches(ItemStatus? s, string? filter) => filter switch
    {
        null or "" => true,
        "None" => s is null,
        "Available" => s == ItemStatus.Green,
        "Missing" => s == ItemStatus.Red,
        "NA" => s == ItemStatus.NotApplicable,
        _ => true,
    };
}

/// <summary>Daily progress of a class: Not Started, In Progress or Completed (from the students' records).</summary>
public enum DailyState { NotStarted, InProgress, Completed }

public static class DailyStates
{
    public static DailyState Of(int students, int complete, int started) =>
        students > 0 && complete >= students ? DailyState.Completed : started > 0 ? DailyState.InProgress : DailyState.NotStarted;

    public static string Label(DailyState s) => s switch { DailyState.Completed => "Completed", DailyState.InProgress => "In Progress", _ => "Not Started" };
}

// ====================================================================== Personal Belongings views

public sealed record PersonalClassCard(
    int ClassId, string Name, string Stage, string? Grade, string? Section, int Students, int Complete, int Started, ReadinessCounts Readiness)
{
    public int Pending => Math.Max(0, Students - Complete);
    public int Percent => Students == 0 ? 0 : (int)Math.Round(100.0 * Complete / Students);
    public DailyState State => DailyStates.Of(Students, Complete, Started);
}

public sealed record PersonalStudentRow(
    int StudentId, string Name, int? RecordId, IReadOnlyList<ItemStatus?> Items, string? Notes, int NoteVersions, int Corrections,
    DateTime? UpdatedAt, string? UpdatedBy)
{
    public bool Complete => PersonalItems.Complete(Items);
    public bool Started => Items.Any(s => s is not null);
    public ReadinessCounts Readiness => ReadinessCounts.Of(Items);
    public IReadOnlyList<string> Missing => PersonalItems.Missing(Items);
}

public sealed record DailyCounts(int Students, int Complete, int Started, ReadinessCounts Readiness)
{
    public int Pending => Math.Max(0, Students - Complete);
    public int Percent => Students == 0 ? 0 : (int)Math.Round(100.0 * Complete / Students);
    public DailyState State => DailyStates.Of(Students, Complete, Started);
}

public sealed record PersonalClassDay(
    int ClassId, string ClassName, string? YearName, DateOnly Date, DateOnly Today, bool IsSchoolDay, string? Holiday, bool InQuarter,
    IReadOnlyList<PersonalStudentRow> Rows)
{
    public DailyCounts Counts => new(Rows.Count, Rows.Count(r => r.Complete), Rows.Count(r => r.Started), Rows.Aggregate(ReadinessCounts.Zero, (a, r) => a + r.Readiness));

    public bool IsToday => Date == Today;

    /// <summary>Records can be made on a school day of a configured quarter, today or earlier (school clock).</summary>
    public bool CanRecord => IsSchoolDay && InQuarter && Date <= Today;
}

public sealed record PersonalDaily(DateOnly Date, bool IsToday, bool IsSchoolDay, string? Holiday, DateOnly LastSchoolDay, IReadOnlyList<PersonalClassCard> Classes);

/// <summary>Result of one click: whether it was saved, a message, and the student's row and class counts as stored.</summary>
public sealed record PersonalMarkResult(bool Saved, string Message, PersonalStudentRow? Row, DailyCounts? Counts);

// ====================================================================== Academic Belongings views

public sealed record AcademicClassCard(ClassCard Card, ClassLesson? Lesson, int Checked, bool TimesMissing)
{
    /// <summary>The lesson state shown on the card: In Progress, Upcoming (next), Finished (the day is over) or No Active Lesson.</summary>
    public string State => Lesson?.Phase switch
    {
        LessonPhase.InProgress => "In Progress",
        _ when Card.Lessons.Count == 0 => "No lessons today",
        _ when TimesMissing && Card.Current is null => "Times not set",
        _ when Card.Next is not null => "No Active Lesson",
        _ when Card.Lessons.All(l => l.Phase is LessonPhase.Finished or LessonPhase.Cancelled) => "Finished",
        _ => "No Active Lesson",
    };
}

public sealed record AcademicStudentRow(
    int StudentId, string Name, int? RowId, ItemStatus? Book, ItemStatus? CopyBook, string? Notes, int Corrections,
    DateTime? UpdatedAt, string? UpdatedBy)
{
    public bool Complete => Book is not null && CopyBook is not null;
    public bool Started => Book is not null || CopyBook is not null;
    public ReadinessCounts Readiness => ReadinessCounts.Of([Book, CopyBook]);
}

public sealed record AcademicClassDay(ClassDay Day, ClassLesson? Lesson, IReadOnlyList<AcademicStudentRow> Rows, bool InQuarter, IReadOnlyDictionary<int, int> CheckedByLesson)
{
    public DailyCounts Counts => new(Rows.Count, Rows.Count(r => r.Complete), Rows.Count(r => r.Started), Rows.Aggregate(ReadinessCounts.Zero, (a, r) => a + r.Readiness));

    /// <summary>Only a lesson of the class's timetable that has started (or has no times), on a school day of a quarter, never upcoming or cancelled.</summary>
    public bool CanRecord => Lesson is { CanRecord: true } && Day.IsSchoolDay && InQuarter && Day.Date <= DateOnly.FromDateTime(Day.Now);
}

public sealed record AcademicMarkResult(bool Saved, string Message, AcademicStudentRow? Row, DailyCounts? Counts);

public interface IBelongingsDailyService
{
    Task<PersonalDaily> PersonalClassesAsync(DateOnly date, CancellationToken ct = default);
    Task<PersonalClassDay?> PersonalClassAsync(int classId, DateOnly date, CancellationToken ct = default);
    /// <summary>One click: one item of one student on one day. A changed saved value is kept as a correction.</summary>
    Task<PersonalMarkResult> SetPersonalAsync(int studentId, DateOnly date, string item, ItemStatus status, CancellationToken ct = default);
    /// <summary>Marks every item not checked yet as Available (saved values are never overwritten).</summary>
    Task<PersonalMarkResult> PersonalAllAvailableAsync(int studentId, DateOnly date, CancellationToken ct = default);
    Task<PersonalMarkResult> SavePersonalNotesAsync(int studentId, DateOnly date, string? notes, CancellationToken ct = default);

    Task<IReadOnlyList<AcademicClassCard>> AcademicClassesAsync(DateOnly date, CancellationToken ct = default);
    /// <summary>A class's lesson: the one in progress (found from the timetable and the school clock) unless <paramref name="sessionId"/> opens another of the day.</summary>
    Task<AcademicClassDay?> AcademicClassAsync(int classId, DateOnly date, int? sessionId, CancellationToken ct = default);
    Task<AcademicMarkResult> SetAcademicAsync(int sessionId, DateOnly date, int studentId, string item, ItemStatus status, CancellationToken ct = default);
    Task<AcademicMarkResult> AcademicAllAvailableAsync(int sessionId, DateOnly date, int studentId, CancellationToken ct = default);
    Task<AcademicMarkResult> SaveAcademicNotesAsync(int sessionId, DateOnly date, int studentId, string? notes, CancellationToken ct = default);
}

/// <summary>
/// Belongings daily checks. Personal Belongings: one record per student per school day, the five items and notes;
/// no timetable. Academic Belongings: Book and Copy Book per student per lesson of the class timetable; the lesson
/// (period, subject, teacher, times) always comes from the timetable on the school clock (Africa/Cairo), never typed.
/// Every change of a saved value is kept (<see cref="BelongingsChange"/>, <see cref="BelongingsNoteHistory"/>).
/// </summary>
public sealed class BelongingsDailyService(SchoolDbContext db, ICurrentUser currentUser, ISessionAttendanceService sessions, ISchoolTimeService? time = null)
    : IBelongingsDailyService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = time ?? SchoolTime.Current;

    // ------------------------------------------------------------------ Personal: classes

    public async Task<PersonalDaily> PersonalClassesAsync(DateOnly date, CancellationToken ct = default)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        var classes = await db.Classes.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var roster = await Lessons.RosterAsync(db, classes.Select(c => c.Id).ToList(), date, ct);
        var ids = roster.Select(r => r.Id).ToList();
        var records = (await db.BelongingsRecords.AsNoTracking().Where(r => r.Date == date && ids.Contains(r.StudentId))
                .Select(r => new { r.StudentId, r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox }).ToListAsync(ct))
            .ToDictionary(r => r.StudentId, r => new ItemStatus?[] { r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox });
        var byClass = roster.GroupBy(r => r.ClassId).ToDictionary(g => g.Key, g => g.ToList());

        var cards = classes.Select(c =>
        {
            var students = byClass.GetValueOrDefault(c.Id) ?? [];
            var items = students.Select(s => records.GetValueOrDefault(s.Id)).Where(x => x is not null).Select(x => x!).ToList();
            var key = GradeCatalog.KeyOf(c.Name);
            return new PersonalClassCard(c.Id, c.Name, ClassStages.Locate(c.Name)?.Stage.Title ?? "Other classes",
                key is null ? null : GradeCatalog.Label(key), ClassNumbering.Parse(c.Name)?.Section, students.Count,
                items.Count(PersonalItems.Complete), items.Count(i => i.Any(s => s is not null)),
                items.Aggregate(ReadinessCounts.Zero, (a, i) => a + ReadinessCounts.Of(i)));
        }).ToList();
        var today = _time.Today;
        return new PersonalDaily(date, date == today, days.IsSchoolDay(date), days.HolidayOn(date)?.Name,
            days.IsSchoolDay(today) ? today : days.Previous(today), cards);
    }

    public async Task<PersonalClassDay?> PersonalClassAsync(int classId, DateOnly date, CancellationToken ct = default)
    {
        var cls = await db.Classes.AsNoTracking().Where(c => c.Id == classId).Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct);
        if (cls is null) return null;
        var days = await SchoolDays.LoadAsync(db, ct);
        var quarter = await QuarterOnAsync(date, ct);
        var roster = await Lessons.RosterAsync(db, [classId], date, ct);
        var rows = await PersonalRowsAsync(roster.Select(r => (r.Id, r.Name)).ToList(), date, ct);
        return new PersonalClassDay(cls.Id, cls.Name, quarter?.Year ?? await YearNameOnAsync(date, ct), date, _time.Today,
            days.IsSchoolDay(date), days.HolidayOn(date)?.Name, quarter is not null, rows);
    }

    private async Task<List<PersonalStudentRow>> PersonalRowsAsync(IReadOnlyList<(int Id, string Name)> students, DateOnly date, CancellationToken ct)
    {
        var ids = students.Select(s => s.Id).ToList();
        var records = (await db.BelongingsRecords.AsNoTracking().Where(r => r.Date == date && ids.Contains(r.StudentId))
                .Select(r => new
                {
                    r.Id, r.StudentId, r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox, r.Notes, r.UpdatedAt, r.UpdatedBy,
                    Versions = db.BelongingsNoteHistory.Count(h => h.BelongingsRecordId == r.Id),
                    Corrections = db.BelongingsChanges.Count(h => h.BelongingsRecordId == r.Id && h.AcademicMaterialRecordId == null),
                    Personal = r.Jacket != null || r.SchoolBag != null || r.PencilCase != null || r.WaterFlask != null || r.LunchBox != null || r.Notes != null,
                }).ToListAsync(ct))
            .ToDictionary(r => r.StudentId);
        return students.Select(s =>
        {
            records.TryGetValue(s.Id, out var r);
            // A record made only by Academic Belongings is not a personal check: no time / author shown.
            var personal = r is { Personal: true };
            return new PersonalStudentRow(s.Id, s.Name, r?.Id, r is null ? new ItemStatus?[5] : [r.Jacket, r.SchoolBag, r.PencilCase, r.WaterFlask, r.LunchBox],
                r?.Notes, r?.Versions ?? 0, r?.Corrections ?? 0, personal ? r!.UpdatedAt : null, personal ? r!.UpdatedBy : null);
        }).ToList();
    }

    // ------------------------------------------------------------------ Personal: one click

    public Task<PersonalMarkResult> SetPersonalAsync(int studentId, DateOnly date, string item, ItemStatus status, CancellationToken ct = default)
    {
        if (PersonalItems.All.All(i => i.Key != item)) return Task.FromResult(new PersonalMarkResult(false, "Choose one of the belongings in the list.", null, null));
        if (!Enum.IsDefined(status)) return Task.FromResult(new PersonalMarkResult(false, "Choose Available, Missing or N/A.", null, null));
        return PersonalWriteAsync(studentId, date, record =>
        {
            var before = PersonalItems.Get(record, item);
            if (before == status) return null;
            PersonalItems.Set(record, item, status);
            if (before is not null) Change(record, null, PersonalItems.Label(item), null, BelongingsStatus.Label(before), BelongingsStatus.Label(status));
            return $"{PersonalItems.Label(item)}: {BelongingsStatus.Label(status)}";
        }, ct);
    }

    public Task<PersonalMarkResult> PersonalAllAvailableAsync(int studentId, DateOnly date, CancellationToken ct = default) =>
        PersonalWriteAsync(studentId, date, record =>
        {
            var set = PersonalItems.All.Where(i => PersonalItems.Get(record, i.Key) is null).ToList();
            foreach (var i in set) PersonalItems.Set(record, i.Key, ItemStatus.Green);
            return set.Count == 0 ? null : $"{set.Count} item{(set.Count == 1 ? "" : "s")} marked Available";
        }, ct);

    public Task<PersonalMarkResult> SavePersonalNotesAsync(int studentId, DateOnly date, string? notes, CancellationToken ct = default)
    {
        var clean = notes?.Trim() is { Length: > 0 } n ? n : null;
        if (clean?.Length > BelongingsRecord.NotesMaxLength)
            return Task.FromResult(new PersonalMarkResult(false, $"Use {BelongingsRecord.NotesMaxLength} characters or fewer.", null, null));
        return PersonalWriteAsync(studentId, date, record =>
        {
            if (record.Notes == clean) return null;
            // Earlier notes are kept (the existing note history of Personal Belongings).
            if (record.Id != 0 && record.Notes is not null)
                db.BelongingsNoteHistory.Add(new BelongingsNoteHistory { BelongingsRecordId = record.Id, PreviousNotes = record.Notes, ChangedAt = DateTime.UtcNow, ChangedBy = currentUser.Name });
            record.Notes = clean;
            return clean is null ? "Notes removed" : "Notes saved";
        }, ct);
    }

    /// <summary>Checks the day and the student, loads (or starts) the day's record, applies the change and saves it.</summary>
    private async Task<PersonalMarkResult> PersonalWriteAsync(int studentId, DateOnly date, Func<BelongingsRecord, string?> apply, CancellationToken ct)
    {
        var (student, quarterId, error) = await DayContextAsync(studentId, date, ct);
        if (error is not null) return new(false, error, null, null);
        for (var attempt = 0; ; attempt++)
        {
            var record = await RecordAsync(studentId, date, student!.ClassId, quarterId!.Value, ct);
            var what = apply(record);
            if (what is null)
            {
                db.ChangeTracker.Clear();
                return new(true, "Already saved.", await PersonalRowAsync(student, date, ct), await PersonalCountsAsync(student.ClassId, date, ct));
            }
            record.UpdatedBy = currentUser.Name;
            try
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                return new(true, $"{student.Name} · {what}.", await PersonalRowAsync(student, date, ct), await PersonalCountsAsync(student.ClassId, date, ct));
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex) && attempt == 0)
            {
                // Someone started the same student's day at the same moment: apply the change to that record instead.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<PersonalStudentRow> PersonalRowAsync(StudentPick student, DateOnly date, CancellationToken ct) =>
        (await PersonalRowsAsync([(student.Id, student.Name)], date, ct))[0];

    private async Task<DailyCounts> PersonalCountsAsync(int classId, DateOnly date, CancellationToken ct)
    {
        var roster = await Lessons.RosterAsync(db, [classId], date, ct);
        var rows = await PersonalRowsAsync(roster.Select(r => (r.Id, r.Name)).ToList(), date, ct);
        return new(rows.Count, rows.Count(r => r.Complete), rows.Count(r => r.Started), rows.Aggregate(ReadinessCounts.Zero, (a, r) => a + r.Readiness));
    }

    // ------------------------------------------------------------------ Academic: classes

    public async Task<IReadOnlyList<AcademicClassCard>> AcademicClassesAsync(DateOnly date, CancellationToken ct = default)
    {
        var cards = (await sessions.ClassesAsync(date, ct)).Where(c => c.IsActive).ToList();
        var shown = cards.Select(c => c.Current).OfType<ClassLesson>().Select(l => l.Lesson.SessionId).ToList();
        var done = await CompleteByLessonAsync(shown, date, ct);
        return cards.Select(c => new AcademicClassCard(c, c.Current, c.Current is { } cur ? done.GetValueOrDefault(cur.Lesson.SessionId) : 0,
            c.Lessons.Any(l => l.Lesson.Start is null || l.Lesson.End is null))).ToList();
    }

    public async Task<AcademicClassDay?> AcademicClassAsync(int classId, DateOnly date, int? sessionId, CancellationToken ct = default)
    {
        var day = await sessions.ClassDayAsync(classId, date, sessionId, ct);
        if (day is null) return null;
        // An explicit lesson must be one of the class's lessons that day; otherwise nothing is opened (never a guess).
        var lesson = sessionId is null ? day.Current : day.Lessons.FirstOrDefault(l => l.Lesson.SessionId == sessionId);
        var inQuarter = await QuarterOnAsync(date, ct) is not null;
        var rows = new List<AcademicStudentRow>();
        if (lesson is not null)
        {
            var roster = await Lessons.RosterAsync(db, [classId], date, ct);
            rows = await AcademicRowsAsync(lesson.Lesson.SessionId, roster.Select(r => (r.Id, r.Name)).ToList(), date, ct);
        }
        var done = await CompleteByLessonAsync(day.Lessons.Select(l => l.Lesson.SessionId).ToList(), date, ct);
        return new AcademicClassDay(day, lesson, rows, inQuarter, done);
    }

    private async Task<Dictionary<int, int>> CompleteByLessonAsync(IReadOnlyCollection<int> sessionIds, DateOnly date, CancellationToken ct) =>
        sessionIds.Count == 0 ? [] : await db.AcademicMaterialRecords.AsNoTracking()
            .Where(m => m.TimetableAssignmentId != null && sessionIds.Contains(m.TimetableAssignmentId.Value) && m.Record.Date == date && m.Book != null && m.CopyBook != null)
            .GroupBy(m => m.TimetableAssignmentId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private async Task<List<AcademicStudentRow>> AcademicRowsAsync(int sessionId, IReadOnlyList<(int Id, string Name)> students, DateOnly date, CancellationToken ct)
    {
        var ids = students.Select(s => s.Id).ToList();
        var rows = (await db.AcademicMaterialRecords.AsNoTracking()
                .Where(m => m.TimetableAssignmentId == sessionId && m.Record.Date == date && ids.Contains(m.Record.StudentId))
                .Select(m => new
                {
                    m.Id, m.Record.StudentId, m.Book, m.CopyBook, m.Notes, m.UpdatedAt, By = m.UpdatedBy ?? m.RecordedBy,
                    Corrections = db.BelongingsChanges.Count(h => h.AcademicMaterialRecordId == m.Id),
                }).ToListAsync(ct))
            .ToDictionary(r => r.StudentId);
        return students.Select(s =>
        {
            rows.TryGetValue(s.Id, out var r);
            return new AcademicStudentRow(s.Id, s.Name, r?.Id, r?.Book, r?.CopyBook, r?.Notes, r?.Corrections ?? 0, r?.UpdatedAt, r?.By);
        }).ToList();
    }

    // ------------------------------------------------------------------ Academic: one click

    public Task<AcademicMarkResult> SetAcademicAsync(int sessionId, DateOnly date, int studentId, string item, ItemStatus status, CancellationToken ct = default)
    {
        if (item is not ("Book" or "CopyBook")) return Task.FromResult(new AcademicMarkResult(false, "Choose Book or Copy Book.", null, null));
        if (!Enum.IsDefined(status)) return Task.FromResult(new AcademicMarkResult(false, "Choose Available, Missing or N/A.", null, null));
        var label = item == "Book" ? "Book" : "Copy Book";
        return AcademicWriteAsync(sessionId, date, studentId, (row, context) =>
        {
            var before = item == "Book" ? row.Book : row.CopyBook;
            if (before == status) return null;
            if (item == "Book") row.Book = status; else row.CopyBook = status;
            if (before is not null) Change(row.Record, row, label, context, BelongingsStatus.Label(before), BelongingsStatus.Label(status));
            return $"{label}: {BelongingsStatus.Label(status)}";
        }, ct);
    }

    public Task<AcademicMarkResult> AcademicAllAvailableAsync(int sessionId, DateOnly date, int studentId, CancellationToken ct = default) =>
        AcademicWriteAsync(sessionId, date, studentId, (row, _) =>
        {
            var n = 0;
            if (row.Book is null) { row.Book = ItemStatus.Green; n++; }
            if (row.CopyBook is null) { row.CopyBook = ItemStatus.Green; n++; }
            return n == 0 ? null : n == 2 ? "Book and Copy Book Available" : "Marked Available";
        }, ct);

    public Task<AcademicMarkResult> SaveAcademicNotesAsync(int sessionId, DateOnly date, int studentId, string? notes, CancellationToken ct = default)
    {
        var clean = notes?.Trim() is { Length: > 0 } n ? n : null;
        if (clean?.Length > AcademicMaterialRecord.NotesMaxLength)
            return Task.FromResult(new AcademicMarkResult(false, $"Use {AcademicMaterialRecord.NotesMaxLength} characters or fewer.", null, null));
        return AcademicWriteAsync(sessionId, date, studentId, (row, context) =>
        {
            if (row.Notes == clean) return null;
            if (row.Notes is not null) Change(row.Record, row, "Notes", context, row.Notes, clean);
            row.Notes = clean;
            return clean is null ? "Notes removed" : "Notes saved";
        }, ct);
    }

    private async Task<AcademicMarkResult> AcademicWriteAsync(int sessionId, DateOnly date, int studentId,
        Func<AcademicMaterialRecord, string, string?> apply, CancellationToken ct)
    {
        static AcademicMarkResult Fail(string message) => new(false, message, null, null);
        if (date > _time.Today) return Fail("Belongings cannot be recorded for a future date.");
        var days = await SchoolDays.LoadAsync(db, ct);
        if (!days.IsSchoolDay(date)) return Fail($"{date.ToString("dddd d MMMM", C)} is not a school day.");
        // The lesson comes from the timetable for that date: an unscheduled lesson is never recorded.
        var lesson = (await Lessons.OnAsync(db, date, days, new Lessons.Scope(SessionIds: [sessionId]), ct, _time)).FirstOrDefault();
        if (lesson is null) return Fail("This lesson is not on the class timetable on this date. Refresh the page.");
        if (lesson.Cancelled) return Fail("This lesson is cancelled on this date: no checks are recorded.");
        if (lesson.Upcoming) return Fail($"{lesson.Subject} has not started yet ({lesson.Times12}). Checks open when the lesson starts.");

        var student = (await Lessons.RosterAsync(db, [lesson.ClassId], date, ct)).FirstOrDefault(s => s.Id == studentId);
        if (student is null) return Fail("This student is not in the class on this date. Refresh the page.");
        var quarter = await QuarterOnAsync(date, ct);
        if (quarter is null) return Fail("This date is not inside a quarter of the School Calendar. Set the quarter dates first.");
        var department = await db.TimetableAssignments.Where(t => t.Id == sessionId).Select(t => t.DepartmentId).FirstAsync(ct);
        var context = $"{lesson.Subject} · {lesson.Period}";

        for (var attempt = 0; ; attempt++)
        {
            var record = await RecordAsync(studentId, date, student.ClassId, quarter.Id, ct);
            var row = record.Materials.FirstOrDefault(m => m.TimetableAssignmentId == sessionId);
            if (row is null)
            {
                // The lesson as it is on that date, kept with the check (later timetable edits never change it).
                row = new AcademicMaterialRecord
                {
                    Record = record, TimetableAssignmentId = sessionId, DepartmentId = department, Subject = lesson.Subject,
                    PeriodNumber = lesson.SlotNumber, PeriodName = lesson.Period, LessonStart = lesson.Start, LessonEnd = lesson.End,
                    TeacherId = lesson.SubstituteId ?? lesson.TeacherId, TeacherName = lesson.Teacher,
                    RecordedBy = currentUser.Name, CreatedAt = DateTime.UtcNow,
                };
                record.Materials.Add(row);
            }
            var what = apply(row, context);
            if (what is null)
            {
                db.ChangeTracker.Clear();
                return new(true, "Already saved.", await AcademicRowAsync(sessionId, student, date, ct), await AcademicCountsAsync(sessionId, lesson.ClassId, date, ct));
            }
            row.UpdatedBy = currentUser.Name;
            row.UpdatedAt = DateTime.UtcNow;
            record.UpdatedBy ??= currentUser.Name;
            try
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                return new(true, $"{student.Name} · {lesson.Subject} · {what}.", await AcademicRowAsync(sessionId, student, date, ct),
                    await AcademicCountsAsync(sessionId, lesson.ClassId, date, ct));
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex) && attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<AcademicStudentRow> AcademicRowAsync(int sessionId, StudentPick student, DateOnly date, CancellationToken ct) =>
        (await AcademicRowsAsync(sessionId, [(student.Id, student.Name)], date, ct))[0];

    private async Task<DailyCounts> AcademicCountsAsync(int sessionId, int classId, DateOnly date, CancellationToken ct)
    {
        var roster = await Lessons.RosterAsync(db, [classId], date, ct);
        var rows = await AcademicRowsAsync(sessionId, roster.Select(r => (r.Id, r.Name)).ToList(), date, ct);
        return new(rows.Count, rows.Count(r => r.Complete), rows.Count(r => r.Started), rows.Aggregate(ReadinessCounts.Zero, (a, r) => a + r.Readiness));
    }

    // ------------------------------------------------------------------ shared

    private sealed record QuarterOn(int Id, string Year);

    /// <summary>The quarter (8 school weeks) holding the date: every record belongs to one.</summary>
    private Task<QuarterOn?> QuarterOnAsync(DateOnly date, CancellationToken ct) =>
        db.AcademicQuarters.AsNoTracking().Where(q => q.StartDate <= date && q.EndDate >= date)
            .Select(q => new QuarterOn(q.Id, q.AcademicYear.Name)).FirstOrDefaultAsync(ct);

    /// <summary>The academic year whose School Calendar (or quarters) cover the date, for the page header.</summary>
    private Task<string?> YearNameOnAsync(DateOnly date, CancellationToken ct) =>
        db.AcademicQuarters.AsNoTracking()
            .Where(q => (q.CalendarStartDate <= date && q.CalendarEndDate >= date) || (q.StartDate <= date && q.EndDate >= date))
            .Select(q => q.AcademicYear.Name).FirstOrDefaultAsync(ct);

    /// <summary>A school day of a quarter, today or earlier, for a student in a class that day.</summary>
    private async Task<(StudentPick? Student, int? QuarterId, string? Error)> DayContextAsync(int studentId, DateOnly date, CancellationToken ct)
    {
        if (date > _time.Today) return (null, null, "Belongings cannot be recorded for a future date.");
        var days = await SchoolDays.LoadAsync(db, ct);
        if (!days.IsSchoolDay(date))
            return (null, null, days.HolidayOn(date) is { } h ? $"{date.ToString("ddd d MMM", C)} is a school holiday ({h.Name})." : $"{date.ToString("dddd d MMMM", C)} is not a school day.");
        var student = await Lessons.StudentOnAsync(db, studentId, date, ct);
        if (student is null) return (null, null, "This student no longer exists. Refresh the page.");
        var quarter = await QuarterOnAsync(date, ct);
        if (quarter is null) return (null, null, "This date is not inside a quarter of the School Calendar. Set the quarter dates first.");
        return (student, quarter.Id, null);
    }

    private async Task<BelongingsRecord> RecordAsync(int studentId, DateOnly date, int classId, int quarterId, CancellationToken ct)
    {
        var record = await db.BelongingsRecords.Include(r => r.Materials).FirstOrDefaultAsync(r => r.StudentId == studentId && r.Date == date, ct);
        if (record is null)
        {
            record = new BelongingsRecord { StudentId = studentId, Date = date, SchoolClassId = classId, AcademicQuarterId = quarterId, RecordedBy = currentUser.Name };
            db.BelongingsRecords.Add(record);
        }
        return record;
    }

    private void Change(BelongingsRecord record, AcademicMaterialRecord? row, string item, string? context, string? before, string? after)
    {
        // Only a saved value can be corrected, so an academic row here always exists already (it has its id).
        db.BelongingsChanges.Add(new BelongingsChange
        {
            Record = record, AcademicMaterialRecordId = row?.Id, Item = item, Context = context, Before = Trim(before), After = Trim(after),
            ChangedAt = DateTime.UtcNow, ChangedBy = currentUser.Name,
        });
        static string? Trim(string? s) => s is null ? null : s.Length > BelongingsChange.ValueMaxLength ? s[..BelongingsChange.ValueMaxLength] : s;
    }
}
