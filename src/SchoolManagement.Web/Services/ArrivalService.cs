using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record ArrivalEntry(
    int StudentId, ArrivalStatus? Status, TimeOnly? Time, ArrivalMethod? Method, string? Location,
    int? ReceivedByStaffId, bool ReceivingConfirmed, string? Notes);

public sealed record ArrivalRow(
    int? RecordId, int StudentId, string StudentName, int ClassId, string ClassName, DateOnly Date,
    ArrivalStatus? Status, TimeOnly? Time, ArrivalMethod? Method, string? Location, LateStatus? Late, TimeOnly? DeadlineUsed,
    int? ReceivedById, string? ReceivedByName, bool Confirmed, string? Notes, string? RecordedBy, DateTime? RecordedAt, int Corrections,
    TimeOnly? CutoffUsed = null)
{
    public bool IsRecorded => RecordId is not null;

    /// <summary>The final outcome of a record: Present, Late or Absent (null = no record yet).</summary>
    public ArrivalOutcome? Outcome => Status is { } s ? Arrivals.Outcome(s, Late) : null;

    /// <summary>Absent because the student arrived after the late cutoff (the arrival time is kept).</summary>
    public bool AfterCutoff => Status == ArrivalStatus.Absent && Time is not null;
}

/// <summary>
/// A day's outcomes: Present (arrived by the school start time), Late (after it, before the 09:30 cutoff), Absent (marked, or
/// no arrival by the cutoff). Not Recorded = no record on a day the automatic rule does not cover (history, or before the cutoff).
/// </summary>
public sealed record ArrivalSummary(int Students, int Recorded, int Present, int Late, int Absent)
{
    /// <summary>Students with no record (and not absent by the cutoff rule).</summary>
    public int NotRecorded => Math.Max(0, Students - Recorded);

    /// <summary>Former name of <see cref="NotRecorded"/>.</summary>
    public int Pending => NotRecorded;

    /// <summary>At school: present or late.</summary>
    public int Attended => Present + Late;

    /// <summary>Present ÷ (present + late); null when nobody arrived.</summary>
    public double? OnTimePercent => Attended == 0 ? null : Math.Round(Present * 100.0 / Attended, 1);
}

/// <summary>
/// The day shown. <see cref="RegistrationOpen"/>: today, a school day, before the cutoff (school time). <see cref="AutoAbsent"/>:
/// students without an arrival count as Absent (a school day from the start of automatic absences, past the cutoff).
/// </summary>
public sealed record ArrivalDay(bool IsSchoolDay, string? Holiday, bool IsFuture, bool IsToday, bool RegistrationOpen = false, bool AutoAbsent = false)
{
    /// <summary>Today's registration has closed (the cutoff has passed).</summary>
    public bool ClosedToday => IsToday && IsSchoolDay && !RegistrationOpen;
}

/// <summary>
/// The arrival rules in force (Settings): Present up to and including <see cref="Start"/> (none = every arrival before the cutoff
/// is Present), Late after it; arrival registration closes at <see cref="Cutoff"/> (09:30 by default, Africa/Cairo).
/// From <see cref="AutomaticFrom"/>, students with no arrival by the cutoff are Absent.
/// </summary>
public sealed record ArrivalRules(TimeOnly? Start, TimeOnly Cutoff, DateOnly? AutomaticFrom)
{
    public string ClosedMessage => $"Arrival registration closed at {Arrivals.Time(Cutoff)}.";
}

/// <summary>
/// Automatic absences (config "Attendance:AutoFinalize", on by default): after the cutoff, students with no arrival are Absent.
/// <see cref="From"/> overrides the first day it applies to (tests); otherwise it is the stored start date (set on first use).
/// </summary>
public sealed record AttendanceOptions(bool AutomaticAbsences, DateOnly? From = null);

/// <summary>Result of a one-click arrival or an explicit absence: the student's row as it now stands and the class's counts.</summary>
public sealed record ArrivalActionResult(bool Created, string Message, ArrivalRow? Row, ArrivalSummary? Summary, bool Closed = false);

public sealed record ArrivalChecklist(DateOnly Date, int? ClassId, TimeOnly? Deadline, IReadOnlyList<ArrivalRow> Rows, ArrivalSummary Summary,
    TimeOnly Cutoff = default, ArrivalDay? Day = null);

public sealed record ArrivalCorrectionRow(int Id, int RecordId, DateOnly Date, string Before, string After, string Reason, DateTime CorrectedAt, string? CorrectedBy);

public sealed record StudentArrivalHistory(
    IReadOnlyList<ArrivalRow> Records, ArrivalSummary Summary,
    IReadOnlyList<(string Label, int Count)> Methods, IReadOnlyList<(string Name, int Count)> Receivers,
    IReadOnlyList<ArrivalCorrectionRow> Corrections);

public sealed record ArrivalSaveResult(int StudentId, bool Saved, string? Error);

public interface IArrivalService
{
    Task<ArrivalChecklist> ChecklistAsync(DateOnly date, int? classId, CancellationToken ct = default);
    Task<ArrivalSummary> DaySummaryAsync(DateOnly date, CancellationToken ct = default);
    Task<IReadOnlyList<ArrivalSaveResult>> RecordAsync(DateOnly date, IReadOnlyList<ArrivalEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// One click on Daily Attendance: records the student's arrival today at the server's current school time (Africa/Cairo):
    /// Present by the school start time, Late after it. Refused at and after the cutoff (09:30) — registration is closed.
    /// A student already recorded today is never changed. <paramref name="pageDate"/> is the day the page shows.
    /// </summary>
    Task<ArrivalActionResult> RegisterArrivalAsync(int studentId, DateOnly pageDate, int? classId, CancellationToken ct = default);

    /// <summary>Explicitly records a student Absent on a school day. A student already recorded is never changed.</summary>
    Task<ArrivalActionResult> MarkAbsentAsync(int studentId, DateOnly date, int? classId, CancellationToken ct = default);

    Task<ArrivalRow?> GetAsync(int recordId, CancellationToken ct = default);

    /// <summary>Explicit correction: the new outcome (Present, Late or Absent), arrival time and notes, with a reason. Before / after are kept.</summary>
    Task<ServiceResult> CorrectAsync(int recordId, ArrivalOutcome outcome, TimeOnly? time, string? notes, string? reason, CancellationToken ct = default);
    Task<StudentArrivalHistory> StudentHistoryAsync(int studentId, DateOnly? from, DateOnly? to, bool newestFirst, CancellationToken ct = default);

    Task<ArrivalDay> DayAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>The school start time (later = Late) and the cutoff (registration closes; no arrival by then = Absent).</summary>
    Task<ArrivalRules> RulesAsync(CancellationToken ct = default);

    /// <summary>
    /// Records Absent ("no arrival by the cutoff") for every expected student without a record on a school day that is over its
    /// cutoff. Returns the number of records created (EntityId). Existing records are never changed; future days are refused.
    /// </summary>
    Task<ServiceResult> FinalizeAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>Finalizes every due school day from the start of automatic absences up to today (after its cutoff).</summary>
    Task<int> FinalizeDueAsync(CancellationToken ct = default);
}

public static class Arrivals
{
    public static string Method(ArrivalMethod? m) => m switch
    {
        ArrivalMethod.SchoolBus => "School bus",
        ArrivalMethod.PrivateCar => "Private car",
        ArrivalMethod.ParentDropOff => "Parent drop-off",
        _ => "Not specified",
    };

    /// <summary>Late = arrived after the deadline (an arrival exactly at the deadline is on time).</summary>
    public static LateStatus? LateOf(TimeOnly? time, TimeOnly? deadline) =>
        time is { } t && deadline is { } d ? (t > d ? LateStatus.Late : LateStatus.OnTime) : null;

    /// <summary>
    /// The outcome of an arrival time: at or after the cutoff (09:30) = Absent (registration closed); after the school start time
    /// = Late; otherwise Present. Without a start time every arrival before the cutoff is Present.
    /// </summary>
    public static ArrivalOutcome Evaluate(TimeOnly time, TimeOnly? start, TimeOnly cutoff) =>
        time >= cutoff ? ArrivalOutcome.Absent : start is { } s && time > s ? ArrivalOutcome.Late : ArrivalOutcome.Present;

    /// <summary>"7:42 AM" — the 12-hour display of a stored 24-hour time.</summary>
    public static string Time(TimeOnly? t) => t is { } v ? v.ToString("h:mm tt", CultureInfo.InvariantCulture) : "";

    public static string Label(ArrivalOutcome? o) => o is { } v ? Label(v) : "Not Recorded";

    /// <summary>The outcome of a stored record (kept as it was recorded — never re-evaluated with later settings).</summary>
    public static ArrivalOutcome Outcome(ArrivalStatus status, LateStatus? late) =>
        status == ArrivalStatus.Absent ? ArrivalOutcome.Absent : late == LateStatus.Late ? ArrivalOutcome.Late : ArrivalOutcome.Present;

    public static string Label(ArrivalOutcome o) => o switch { ArrivalOutcome.Late => "Late", ArrivalOutcome.Absent => "Absent", _ => "Present" };

    public static string Badge(ArrivalOutcome o) => o switch { ArrivalOutcome.Late => "badge--warning", ArrivalOutcome.Absent => "badge--error", _ => "badge--success" };

    /// <summary>Recorder name of the absences created automatically after the cutoff.</summary>
    public const string AutomaticRecorder = "Automatic finalization";

    public static string AutomaticNote(TimeOnly cutoff) => $"No arrival recorded by the {Time(cutoff)} cutoff.";
}

/// <summary>The final attendance outcome of a school day (derived from the stored status and late flag).</summary>
public enum ArrivalOutcome
{
    Present = 1,
    Late = 2,
    Absent = 3,
}

/// <remarks>
/// School Attendance. Arrivals are one click before the cutoff (the server's school-local time decides Present / Late); at and
/// after the cutoff registration is closed and students with no arrival are Absent (stored by <see cref="ArrivalFinalizer"/>;
/// shown as Absent at once). After every new record or correction the attendance alerts of the student are re-checked
/// (three consecutive school-day absences; present at school but absent from a lesson).
/// </remarks>
public sealed class ArrivalService(SchoolDbContext db, ISettingsService settings, ICurrentUser currentUser, IAttendanceAlertService? alerts = null,
    ISchoolTimeService? schoolTime = null, AttendanceOptions? options = null) : IArrivalService
{
    private readonly bool _automatic = options?.AutomaticAbsences ?? false;

    /// <summary>The school clock (Africa/Cairo): the cutoff and "today" never follow the server's or the browser's zone.</summary>
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    public async Task<ArrivalChecklist> ChecklistAsync(DateOnly date, int? classId, CancellationToken ct = default)
    {
        var students = await ExpectedStudentsAsync(date, classId, ct);
        var ids = students.Select(s => s.Id).ToList();
        var records = (await Project(db.StudentArrivals.Where(r => r.ArrivalDate == date && ids.Contains(r.StudentId))).ToListAsync(ct))
            .ToDictionary(r => r.StudentId);
        var rules = await RulesAsync(ct);
        var day = await DayAsync(date, ct);
        // Past the cutoff a student with no arrival is Absent — shown at once, stored by the finalizer within a minute.
        var rows = students.Select(s => records.TryGetValue(s.Id, out var r) ? r
                : day.AutoAbsent
                    ? new ArrivalRow(null, s.Id, s.FullName, s.ClassId, s.ClassName, date, ArrivalStatus.Absent, null, null, null, null, null, null, null, false,
                        Arrivals.AutomaticNote(rules.Cutoff), Arrivals.AutomaticRecorder, null, 0, rules.Cutoff)
                    : new ArrivalRow(null, s.Id, s.FullName, s.ClassId, s.ClassName, date, null, null, null, null, null, null, null, null, false, null, null, null, 0))
            .ToList();
        return new ArrivalChecklist(date, classId, rules.Start, rows, Summarise(rows.Count, rows), rules.Cutoff, day);
    }

    public async Task<ArrivalDay> DayAsync(DateOnly date, CancellationToken ct = default)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        var rules = await RulesAsync(ct);
        var now = _time.Now;
        var today = DateOnly.FromDateTime(now);
        var schoolDay = days.IsSchoolDay(date);
        var pastCutoff = date < today || (date == today && TimeOnly.FromDateTime(now) >= rules.Cutoff);
        return new ArrivalDay(schoolDay, days.HolidayOn(date)?.Name, date > today, date == today,
            RegistrationOpen: schoolDay && date == today && !pastCutoff,
            AutoAbsent: schoolDay && pastCutoff && rules.AutomaticFrom is { } from && date >= from);
    }

    public async Task<ArrivalRules> RulesAsync(CancellationToken ct = default) =>
        new(await settings.GetArrivalDeadlineAsync(ct), await settings.GetLateCutoffAsync(ct),
            _automatic ? options!.From ?? await settings.GetAutoFinalizeFromAsync(ct) : null);

    public async Task<ArrivalActionResult> RegisterArrivalAsync(int studentId, DateOnly pageDate, int? classId, CancellationToken ct = default)
    {
        // The moment of the click, on the server, in school time (Africa/Cairo, daylight saving from the time zone database).
        var now = _time.Now;
        var today = DateOnly.FromDateTime(now);
        if (pageDate != today)
            return new(false, $"This page shows {pageDate:ddd d MMM yyyy}. Arrivals are recorded for today ({today:ddd d MMM}) — reload the page.", null, null);
        var days = await SchoolDays.LoadAsync(db, ct);
        if (!days.IsSchoolDay(today))
            return new(false, days.HolidayOn(today) is { } h ? $"Today is a school holiday ({h.Name}). No attendance is taken." : "Today is not a school day. No attendance is taken.", null, null);
        var time = TimeOnly.FromDateTime(now);
        var rules = await RulesAsync(ct);
        // The cutoff is enforced here, on the server's school clock: 09:29:59 is accepted, 09:30:00 and later are refused.
        if (Arrivals.Evaluate(time, rules.Start, rules.Cutoff) == ArrivalOutcome.Absent)
        {
            var existing = await Project(db.StudentArrivals.Where(r => r.StudentId == studentId && r.ArrivalDate == today)).FirstOrDefaultAsync(ct);
            return new(false, rules.ClosedMessage + (existing is null ? " Students with no arrival are Absent; use Correct (with a reason) if needed." : ""),
                existing, (await ChecklistAsync(today, classId, ct)).Summary, Closed: true);
        }
        var stored = new TimeOnly(time.Hour, time.Minute, time.Second);
        var result = await AddOneAsync(studentId, today, record =>
        {
            record.Status = ArrivalStatus.Present;
            record.ArrivalTime = stored;
            record.LateStatus = Arrivals.Evaluate(stored, rules.Start, rules.Cutoff) == ArrivalOutcome.Late ? LateStatus.Late : LateStatus.OnTime;
            record.DeadlineUsed = rules.Start;
            record.CutoffUsed = rules.Cutoff;
        }, ct);
        return await ResultAsync(result, studentId, today, classId, ct);
    }

    public async Task<ArrivalActionResult> MarkAbsentAsync(int studentId, DateOnly date, int? classId, CancellationToken ct = default)
    {
        if (date > _time.Today) return new(false, "Absences cannot be recorded for a future date.", null, null);
        var days = await SchoolDays.LoadAsync(db, ct);
        if (!days.IsSchoolDay(date)) return new(false, $"{date:ddd d MMM yyyy} is not a school day (weekend or holiday).", null, null);
        var result = await AddOneAsync(studentId, date, record => record.Status = ArrivalStatus.Absent, ct);
        return await ResultAsync(result, studentId, date, classId, ct);
    }

    /// <summary>Adds one record for a student and day; an existing record is reported, never changed (one record per student per day).</summary>
    private async Task<(bool Created, string? Error)> AddOneAsync(int studentId, DateOnly date, Action<StudentArrivalRecord> fill, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().Where(s => s.Id == studentId).Select(s => new { s.Id, s.SchoolClassId }).FirstOrDefaultAsync(ct);
        if (student is null) return (false, "This student no longer exists.");
        if (await db.StudentArrivals.AnyAsync(r => r.StudentId == studentId && r.ArrivalDate == date, ct)) return (false, null);
        var record = new StudentArrivalRecord { StudentId = studentId, SchoolClassId = student.SchoolClassId, ArrivalDate = date, RecordedBy = currentUser.Name };
        fill(record);
        db.StudentArrivals.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear(); // recorded a moment ago (another click or device): keep that record
            return (false, null);
        }
        if (alerts is not null) await alerts.SyncStudentDayAsync(studentId, date, ct);
        return (true, null);
    }

    private async Task<ArrivalActionResult> ResultAsync((bool Created, string? Error) result, int studentId, DateOnly date, int? classId, CancellationToken ct)
    {
        if (result.Error is not null) return new(false, result.Error, null, null);
        var row = await Project(db.StudentArrivals.Where(r => r.StudentId == studentId && r.ArrivalDate == date)).FirstOrDefaultAsync(ct);
        var summary = (await ChecklistAsync(date, classId, ct)).Summary;
        var what = row is null ? "" : row.Outcome == ArrivalOutcome.Absent ? "Absent" : $"{Arrivals.Label(row.Outcome!.Value)} at {Arrivals.Time(row.Time)}";
        var message = result.Created
            ? $"{row?.StudentName}: {what}."
            : $"{row?.StudentName} is already recorded ({what}{(row?.RecordedBy is { } by ? " by " + by : "")}). Use Correct to change it.";
        return new(result.Created, message, row, summary);
    }

    public async Task<ArrivalSummary> DaySummaryAsync(DateOnly date, CancellationToken ct = default)
    {
        var expected = await ExpectedStudentsAsync(date, null, ct);
        var ids = expected.Select(s => s.Id).ToHashSet();
        var records = (await Project(db.StudentArrivals.Where(r => r.ArrivalDate == date)).ToListAsync(ct)).Where(r => ids.Contains(r.StudentId)).ToList();
        var s = Summarise(expected.Count, records);
        // Past the cutoff the students without an arrival are Absent (before the finalizer has stored them too).
        return (await DayAsync(date, ct)).AutoAbsent ? s with { Recorded = s.Students, Absent = s.Absent + s.NotRecorded } : s;
    }

    public async Task<ServiceResult> FinalizeAsync(DateOnly date, CancellationToken ct = default)
    {
        var rules = await RulesAsync(ct);
        var now = _time.Now;
        var today = DateOnly.FromDateTime(now);
        if (date > today) return ServiceResult.Fail("", "A future day is never marked absent.");
        if (date == today && TimeOnly.FromDateTime(now) < rules.Cutoff)
            return ServiceResult.Fail("", $"Arrival registration is open until {Arrivals.Time(rules.Cutoff)}; students are not absent before then.");
        if (!(await SchoolDays.LoadAsync(db, ct)).IsSchoolDay(date)) return ServiceResult.Ok(0); // weekend or holiday: nothing expected

        var expected = await ExpectedStudentsAsync(date, null, ct);
        var recorded = (await db.StudentArrivals.Where(r => r.ArrivalDate == date).Select(r => r.StudentId).ToListAsync(ct)).ToHashSet();
        var missing = expected.Where(s => !recorded.Contains(s.Id)).ToList();
        if (missing.Count == 0) return ServiceResult.Ok(0);

        StudentArrivalRecord Absent((int Id, string FullName, int ClassId, string ClassName) s) => new()
        {
            StudentId = s.Id, SchoolClassId = s.ClassId, ArrivalDate = date, Status = ArrivalStatus.Absent, CutoffUsed = rules.Cutoff,
            Notes = Arrivals.AutomaticNote(rules.Cutoff), RecordedBy = Arrivals.AutomaticRecorder,
        };
        var created = 0;
        db.StudentArrivals.AddRange(missing.Select(Absent));
        try
        {
            await db.SaveChangesAsync(ct);
            created = missing.Count;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Someone recorded a student a moment ago: add the rest one by one and keep what was recorded.
            db.ChangeTracker.Clear();
            foreach (var s in missing)
            {
                db.StudentArrivals.Add(Absent(s));
                try { await db.SaveChangesAsync(ct); created++; }
                catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e)) { db.ChangeTracker.Clear(); }
            }
        }
        if (alerts is not null && created > 0) await alerts.RecheckAsync(date, date, ct);
        return ServiceResult.Ok(created);
    }

    public async Task<int> FinalizeDueAsync(CancellationToken ct = default)
    {
        var rules = await RulesAsync(ct);
        if (rules.AutomaticFrom is not { } from) return 0;
        var today = _time.Today;
        var created = 0;
        // From the first automatic day (at most the last 60 days, e.g. after the server was off) up to today once past the cutoff.
        for (var d = from > today.AddDays(-60) ? from : today.AddDays(-60); d <= today; d = d.AddDays(1))
        {
            var r = await FinalizeAsync(d, ct);
            if (r.Succeeded) created += r.EntityId ?? 0;
        }
        return created;
    }

    public async Task<IReadOnlyList<ArrivalSaveResult>> RecordAsync(DateOnly date, IReadOnlyList<ArrivalEntry> entries, CancellationToken ct = default)
    {
        var results = new List<ArrivalSaveResult>();
        if (date > _time.Today) return entries.Select(e => new ArrivalSaveResult(e.StudentId, false, "Arrivals cannot be recorded for a future date.")).ToList();
        var rules = await RulesAsync(ct);

        foreach (var entry in entries.DistinctBy(e => e.StudentId))
        {
            var student = await db.Students.AsNoTracking().Where(s => s.Id == entry.StudentId)
                .Select(s => new { s.Id, s.SchoolClassId }).FirstOrDefaultAsync(ct);
            if (student is null) { results.Add(new(entry.StudentId, false, "This student no longer exists.")); continue; }

            var existing = await db.StudentArrivals.AsNoTracking().Where(r => r.StudentId == entry.StudentId && r.ArrivalDate == date)
                .Select(r => new { r.Status, r.ArrivalTime, r.RecordedBy }).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                results.Add(new(entry.StudentId, false,
                    $"Already recorded ({existing.Status}{(existing.ArrivalTime is { } t ? " " + t.ToString("HH:mm") : "")}{(existing.RecordedBy is null ? "" : " by " + existing.RecordedBy)}). Use Correct to change it."));
                continue;
            }

            var error = await ValidateAsync(entry, ct);
            if (error is null && entry.Status == ArrivalStatus.Present && entry.Time >= rules.Cutoff)
                error = $"Arrival registration closes at {Arrivals.Time(rules.Cutoff)}: an arrival at or after it is not accepted (the student is Absent; use Correct with a reason).";
            if (error is not null) { results.Add(new(entry.StudentId, false, error)); continue; }

            var record = new StudentArrivalRecord
            {
                StudentId = entry.StudentId,
                SchoolClassId = student.SchoolClassId,
                ArrivalDate = date,
                RecordedBy = currentUser.Name,
            };
            Apply(record, entry, rules);
            db.StudentArrivals.Add(record);
            try
            {
                await db.SaveChangesAsync(ct);
                results.Add(new(entry.StudentId, true, null));
                if (alerts is not null) await alerts.SyncStudentDayAsync(entry.StudentId, date, ct);
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                results.Add(new(entry.StudentId, false, "Someone else recorded this student a moment ago. Refresh to see it."));
            }
        }
        return results;
    }

    public Task<ArrivalRow?> GetAsync(int recordId, CancellationToken ct = default) =>
        Project(db.StudentArrivals.Where(r => r.Id == recordId)).FirstOrDefaultAsync(ct);

    public async Task<ServiceResult> CorrectAsync(int recordId, ArrivalOutcome outcome, TimeOnly? time, string? notes, string? reason, CancellationToken ct = default)
    {
        var record = await db.StudentArrivals.Include(r => r.ReceivedBy).FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return ServiceResult.Fail("", "This arrival record no longer exists.");
        var why = TextNormalizer.CleanText(reason);
        if (why.Length == 0) return ServiceResult.Fail("Reason", "Explain why the record is being corrected.");
        if (why.Length > StudentArrivalCorrection.ReasonMaxLength) return ServiceResult.Fail("Reason", $"Use {StudentArrivalCorrection.ReasonMaxLength} characters or fewer.");
        if (!Enum.IsDefined(outcome)) return ServiceResult.Fail("Outcome", "Choose Present, Late or Absent.");
        if (outcome != ArrivalOutcome.Absent && time is null) return ServiceResult.Fail("Time", "Enter the arrival time.");
        var cleanNotes = TextNormalizer.CleanText(notes);
        if (cleanNotes.Length > StudentArrivalRecord.NotesMaxLength) return ServiceResult.Fail("Notes", "Notes are too long.");

        var before = Describe(record, record.ReceivedBy?.FullName);
        // The outcome is the one chosen in the correction (e.g. a late arrival excused as Present); the original values
        // stay in the correction history.
        record.Notes = cleanNotes.Length == 0 ? null : cleanNotes;
        if (outcome == ArrivalOutcome.Absent)
        {
            (record.Status, record.ArrivalTime, record.LateStatus, record.DeadlineUsed) = (ArrivalStatus.Absent, null, null, null);
            (record.Method, record.ArrivalLocation, record.ReceivedByStaffId, record.ReceivingConfirmed) = (null, null, null, false);
        }
        else
        {
            (record.Status, record.ArrivalTime) = (ArrivalStatus.Present, time);
            record.LateStatus = outcome == ArrivalOutcome.Late ? LateStatus.Late : LateStatus.OnTime;
            record.DeadlineUsed = (await RulesAsync(ct)).Start;
        }
        record.CutoffUsed = null;
        var receiver = record.ReceivedByStaffId is { } rid ? await db.StaffMembers.Where(s => s.Id == rid).Select(s => s.FullName).FirstOrDefaultAsync(ct) : null;
        var after = Describe(record, receiver);
        if (before == after) return ServiceResult.Fail("", "Nothing was changed.");

        db.StudentArrivalCorrections.Add(new StudentArrivalCorrection
        {
            StudentArrivalRecordId = record.Id,
            Before = Trim(before),
            After = Trim(after),
            Reason = why,
            CorrectedAt = _time.UtcNow,
            CorrectedBy = currentUser.Name,
        });
        await db.SaveChangesAsync(ct);
        if (alerts is not null) await alerts.SyncStudentDayAsync(record.StudentId, record.ArrivalDate, ct);
        return ServiceResult.Ok(record.Id);
    }

    public async Task<StudentArrivalHistory> StudentHistoryAsync(int studentId, DateOnly? from, DateOnly? to, bool newestFirst, CancellationToken ct = default)
    {
        var q = db.StudentArrivals.Where(r => r.StudentId == studentId);
        if (from is { } f) q = q.Where(r => r.ArrivalDate >= f);
        if (to is { } t) q = q.Where(r => r.ArrivalDate <= t);
        var records = await Project(q).ToListAsync(ct);
        records = (newestFirst ? records.OrderByDescending(r => r.Date) : records.OrderBy(r => r.Date)).ToList();

        var methods = records.Where(r => r.Time is not null)
            .GroupBy(r => Arrivals.Method(r.Method)).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        var receivers = records.Where(r => r.ReceivedByName is not null)
            .GroupBy(r => r.ReceivedByName!).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        var recordIds = records.Select(r => r.RecordId!.Value).ToList();
        var corrections = await db.StudentArrivalCorrections.AsNoTracking().Where(c => recordIds.Contains(c.StudentArrivalRecordId))
            .OrderByDescending(c => c.CorrectedAt)
            .Select(c => new ArrivalCorrectionRow(c.Id, c.StudentArrivalRecordId, c.Record.ArrivalDate, c.Before, c.After, c.Reason, c.CorrectedAt, c.CorrectedBy))
            .ToListAsync(ct);

        return new StudentArrivalHistory(records, Summarise(records.Count, records), methods, receivers, corrections);
    }

    // ------------------------------------------------------------------ rules

    private async Task<string?> ValidateAsync(ArrivalEntry e, CancellationToken ct)
    {
        if (e.Status is null || !Enum.IsDefined(e.Status.Value)) return "Choose Arrived (with the time) or Absent.";
        if (e.Status == ArrivalStatus.Absent) return TextNormalizer.CleanText(e.Notes).Length > StudentArrivalRecord.NotesMaxLength ? "Notes are too long." : null;

        if (e.Time is null) return "Enter the arrival time.";
        if (e.Method is { } m && !Enum.IsDefined(m)) return "Choose School bus, Private car or Parent drop-off.";
        if (TextNormalizer.CleanText(e.Location).Length > StudentArrivalRecord.LocationMaxLength) return "The arrival location is too long.";
        if (TextNormalizer.CleanText(e.Notes).Length > StudentArrivalRecord.NotesMaxLength) return "Notes are too long.";
        if (e.ReceivedByStaffId is { } staffId && !await db.StaffMembers.AnyAsync(s => s.Id == staffId, ct)) return "Choose the receiving staff member from the list.";
        if (e.ReceivingConfirmed && e.ReceivedByStaffId is null) return "Choose who received the student before confirming.";
        return null;
    }

    /// <summary>
    /// The server decides the outcome from the arrival time (never the browser): Present by the school start time, Late after
    /// it (times at or after the cutoff are refused before this). An explicit Absent has no arrival details.
    /// </summary>
    private static void Apply(StudentArrivalRecord r, ArrivalEntry e, ArrivalRules rules)
    {
        var notes = TextNormalizer.CleanText(e.Notes);
        r.Notes = notes.Length == 0 ? null : notes;
        r.CutoffUsed = null;
        if (e.Status == ArrivalStatus.Absent)
        {
            r.Status = ArrivalStatus.Absent;
            r.ArrivalTime = null;
            r.Method = null;
            r.ArrivalLocation = null;
            r.LateStatus = null;
            r.DeadlineUsed = null;
            r.ReceivedByStaffId = null;
            r.ReceivingConfirmed = false;
            return;
        }
        var location = TextNormalizer.CleanText(e.Location);
        r.Status = ArrivalStatus.Present;
        r.ArrivalTime = e.Time;
        r.Method = e.Method;
        r.ArrivalLocation = location.Length == 0 ? null : location;
        r.DeadlineUsed = rules.Start;
        r.CutoffUsed = rules.Cutoff;
        r.LateStatus = Arrivals.Evaluate(e.Time!.Value, rules.Start, rules.Cutoff) == ArrivalOutcome.Late ? LateStatus.Late : LateStatus.OnTime;
        r.ReceivedByStaffId = e.ReceivedByStaffId;
        r.ReceivingConfirmed = e.ReceivingConfirmed && e.ReceivedByStaffId is not null;
    }

    /// <summary>One line for the correction history; older records also keep their method, location and receiver there.</summary>
    private static string Describe(StudentArrivalRecord r, string? receiver)
    {
        var time = r.ArrivalTime is { } t ? Arrivals.Time(t) : null;
        if (r.Status == ArrivalStatus.Absent && r.ArrivalTime is null) return "Absent" + (r.Notes is null ? "" : $" · notes: {r.Notes}");
        var parts = new List<string>
        {
            r.Status == ArrivalStatus.Absent
                ? $"Absent: arrived {time}, after the {r.CutoffUsed:HH\\:mm} cutoff"
                : $"{(r.LateStatus == LateStatus.Late ? "Late" : "Present")} at {time}",
        };
        if (r.Method is not null) parts.Add(Arrivals.Method(r.Method));
        if (r.ArrivalLocation is not null) parts.Add(r.ArrivalLocation);
        if (receiver is not null) parts.Add($"received by {receiver}{(r.ReceivingConfirmed ? " (confirmed)" : "")}");
        if (r.Notes is not null) parts.Add($"notes: {r.Notes}");
        return string.Join(" · ", parts);
    }

    private static string Trim(string s) => s.Length > StudentArrivalCorrection.TextMaxLength ? s[..StudentArrivalCorrection.TextMaxLength] : s;

    private static ArrivalSummary Summarise(int students, IEnumerable<ArrivalRow> recorded)
    {
        var list = recorded.Where(r => r.Status is not null).ToList();
        return new ArrivalSummary(students, list.Count,
            list.Count(r => r.Outcome == ArrivalOutcome.Present),
            list.Count(r => r.Outcome == ArrivalOutcome.Late),
            list.Count(r => r.Outcome == ArrivalOutcome.Absent));
    }

    /// <summary>Students in the directory on that date (added on or before it), optionally of one class.</summary>
    private async Task<List<(int Id, string FullName, int ClassId, string ClassName)>> ExpectedStudentsAsync(DateOnly date, int? classId, CancellationToken ct)
    {
        var q = db.Students.AsNoTracking();
        if (classId is { } c) q = q.Where(s => s.SchoolClassId == c);
        var list = await q.OrderBy(s => s.SchoolClass.SortOrder).ThenBy(s => s.FullName)
            .Select(s => new { s.Id, s.FullName, s.SchoolClassId, ClassName = s.SchoolClass.Name, s.CreatedAt })
            .ToListAsync(ct);
        return list.Where(s => _time.LocalDate(s.CreatedAt) <= date)
            .Select(s => (s.Id, s.FullName, s.SchoolClassId, s.ClassName)).ToList();
    }

    private static IQueryable<ArrivalRow> Project(IQueryable<StudentArrivalRecord> q) =>
        q.AsNoTracking().Select(r => new ArrivalRow(r.Id, r.StudentId, r.Student.FullName, r.SchoolClassId, r.SchoolClass.Name, r.ArrivalDate,
            r.Status, r.ArrivalTime, r.Method, r.ArrivalLocation, r.LateStatus, r.DeadlineUsed,
            r.ReceivedByStaffId, r.ReceivedBy != null ? r.ReceivedBy.FullName : null, r.ReceivingConfirmed, r.Notes, r.RecordedBy, r.CreatedAt,
            r.Corrections.Count, r.CutoffUsed));
}

/// <summary>
/// Stores the automatic absences: every minute, each school day past its cutoff (from the first automatic day) gets an Absent
/// record for every student with no arrival. Idempotent; existing records are never changed. Off with Attendance:AutoFinalize=false.
/// </summary>
public sealed class ArrivalFinalizer(IServiceScopeFactory scopes, ILogger<ArrivalFinalizer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var created = await scope.ServiceProvider.GetRequiredService<IArrivalService>().FinalizeDueAsync(stoppingToken);
                if (created > 0) logger.LogInformation("Recorded {Count} automatic absences (no arrival by the cutoff).", created);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Automatic absences could not be recorded; retrying in a minute."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
