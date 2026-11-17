using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>A student's School Attendance on a date, as Session Attendance uses it.</summary>
public enum SchoolPresence
{
    /// <summary>No school record (and not absent by the 09:30 rule): never treated as absent.</summary>
    NotRecorded = 0,
    Present = 1,
    Late = 2,
    /// <summary>Absent from school (recorded, or no arrival by the cutoff): lesson attendance is locked.</summary>
    Absent = 3,
}

public readonly record struct SchoolStatus(SchoolPresence Presence, TimeOnly? Time, bool Automatic)
{
    public static readonly SchoolStatus None = new(SchoolPresence.NotRecorded, null, false);
    public bool AtSchool => Presence is SchoolPresence.Present or SchoolPresence.Late;

    public string Label => Presence switch
    {
        SchoolPresence.Present => "At school",
        SchoolPresence.Late => "Late to school",
        SchoolPresence.Absent => "School Absent",
        _ => "School: Not Recorded",
    };
}

/// <summary>Where a lesson stands on the school clock (Africa/Cairo).</summary>
public enum LessonPhase { Upcoming, InProgress, Finished, Untimed, Cancelled }

public sealed record ClassLesson(LessonView Lesson, LessonPhase Phase, LessonCounts Counts)
{
    /// <summary>Attendance can be taken once the lesson has started (or when its period has no times), never when cancelled.</summary>
    public bool CanRecord => Phase is LessonPhase.InProgress or LessonPhase.Finished or LessonPhase.Untimed;

    public string PhaseLabel => Phase switch
    {
        LessonPhase.Upcoming => "Upcoming",
        LessonPhase.InProgress => "In Progress",
        LessonPhase.Finished => "Finished",
        LessonPhase.Untimed => "Time not set",
        _ => "Cancelled",
    };
}

/// <summary>A class on a date: its lessons from the timetable, the one in progress now, the next one and the one shown.</summary>
public sealed record ClassDay(
    int ClassId, string ClassName, DateOnly Date, DateTime Now, bool IsToday, bool IsSchoolDay, string? Holiday,
    IReadOnlyList<ClassLesson> Lessons, ClassLesson? Current, ClassLesson? Next, ClassLesson? Selected)
{
    /// <summary>Some periods have no start / end time, so the current lesson cannot be worked out from the clock.</summary>
    public bool TimesMissing => Lessons.Any(l => l.Lesson.Start is null || l.Lesson.End is null);
}

public sealed record ClassCard(
    int ClassId, string Name, string Stage, string? Grade, string? Section, bool IsActive, int Students,
    IReadOnlyList<ClassLesson> Lessons, ClassLesson? Current, ClassLesson? Next)
{
    public int Recorded => Lessons.Where(l => l.CanRecord).Sum(l => Math.Min(l.Counts.Recorded, l.Counts.Expected));
    public int Expected => Lessons.Where(l => l.CanRecord).Sum(l => l.Counts.Expected);
}

public sealed record LiveRow(
    int StudentId, string Name, SchoolStatus School, int? RecordId, SessionAttendanceStatus? Status, string? Notes,
    DateTime? RecordedAt, string? RecordedBy, int Corrections, bool Alert)
{
    /// <summary>Absent from school that day: no lesson attendance through the normal interface.</summary>
    public bool Locked => School.Presence == SchoolPresence.Absent;
}

/// <summary>A lesson's counts. School-absent students are counted apart (never as lesson absences).</summary>
public sealed record LiveCounts(int Students, int Present, int Late, int Absent, int Excused, int SchoolAbsent, int NotRecorded)
{
    public static LiveCounts Of(IReadOnlyCollection<LiveRow> rows) => new(
        rows.Count,
        rows.Count(r => !r.Locked && r.Status == SessionAttendanceStatus.Present),
        rows.Count(r => !r.Locked && r.Status == SessionAttendanceStatus.Late),
        rows.Count(r => !r.Locked && r.Status == SessionAttendanceStatus.Absent),
        rows.Count(r => !r.Locked && r.Status == SessionAttendanceStatus.Excused),
        rows.Count(r => r.Locked),
        rows.Count(r => !r.Locked && r.Status is null));
}

public sealed record LiveRegister(ClassLesson Lesson, IReadOnlyList<LiveRow> Rows, LiveCounts Counts, IReadOnlyList<SessionCorrectionRow> History);

/// <summary>Result of one click: the student's row and the lesson's counts as saved, and the alert raised (if any).</summary>
public sealed record MarkResult(bool Saved, string Message, LiveRow? Row, LiveCounts? Counts, string? AlertText);

// ------------------------------------------------------------------ reports

/// <summary>student (one student), class, lesson (one lesson on one date) or school; a date range (one date for a lesson).</summary>
public sealed record SessionReportQuery(string Mode, int? StudentId, int? ClassId, int? SessionId, DateOnly From, DateOnly To);

/// <summary>How a student-lesson counts in a report.</summary>
public enum SessionKind { Present, Late, Absent, Excused, SchoolAbsent, NotRecorded }

/// <summary>
/// Student-lesson counts: Present, Late, Absent from the lesson (of which <see cref="AbsentAtSchool"/> while at school that day),
/// Excused, School Absent (absent from school: never a lesson absence) and Not Recorded (no lesson record).
/// </summary>
public sealed record SessionTally(int Present, int Late, int Absent, int AbsentAtSchool, int Excused, int SchoolAbsent, int NotRecorded)
{
    public static readonly SessionTally Zero = new(0, 0, 0, 0, 0, 0, 0);
    public int Total => Present + Late + Absent + Excused + SchoolAbsent + NotRecorded;

    /// <summary>(Present + Late) ÷ (Present + Late + lesson Absent): school absences, excused and not recorded are left out.</summary>
    public double? Percent => Present + Late + Absent == 0 ? null : Math.Round((Present + Late) * 100.0 / (Present + Late + Absent), 1);
    public string PercentText => Percent is { } p ? p.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";

    public static SessionTally operator +(SessionTally a, SessionTally b) => new(a.Present + b.Present, a.Late + b.Late, a.Absent + b.Absent,
        a.AbsentAtSchool + b.AbsentAtSchool, a.Excused + b.Excused, a.SchoolAbsent + b.SchoolAbsent, a.NotRecorded + b.NotRecorded);

    public static SessionTally Of(SessionKind kind, bool atSchool) => kind switch
    {
        SessionKind.Present => new(1, 0, 0, 0, 0, 0, 0),
        SessionKind.Late => new(0, 1, 0, 0, 0, 0, 0),
        SessionKind.Absent => new(0, 0, 1, atSchool ? 1 : 0, 0, 0, 0),
        SessionKind.Excused => new(0, 0, 0, 0, 1, 0, 0),
        SessionKind.SchoolAbsent => new(0, 0, 0, 0, 0, 1, 0),
        _ => new(0, 0, 0, 0, 0, 0, 1),
    };
}

public sealed record SessionReportLine(LessonView Lesson, int StudentId, string StudentName, SessionKind Kind, SchoolPresence School, string? Notes);

public sealed record SessionReportStudent(int StudentId, string Name, string ClassName, SessionTally Tally);

public sealed record SessionReportGroup(string Label, int Lessons, SessionTally Tally);

public sealed record SessionReport(
    SessionReportQuery Query, string Title, string? Subtitle, int Lessons, SessionTally Tally,
    IReadOnlyList<SessionReportGroup> ByClass, IReadOnlyList<SessionReportGroup> BySubject,
    IReadOnlyList<SessionReportStudent> Students, IReadOnlyList<SessionReportLine> Lines, IReadOnlyList<SessionReportStudent> Repeated)
{
    public bool SingleDay => Query.From == Query.To;
}

public partial interface ISessionAttendanceService
{
    /// <summary>Every class on a date with its lessons from the timetable, the lesson in progress and the next one.</summary>
    Task<IReadOnlyList<ClassCard>> ClassesAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>A class on a date: today the lesson in progress is detected from the school clock; <paramref name="sessionId"/> picks another one.</summary>
    Task<ClassDay?> ClassDayAsync(int classId, DateOnly date, int? sessionId, CancellationToken ct = default);

    /// <summary>One lesson's register with each student's School Attendance (school-absent students are locked).</summary>
    Task<LiveRegister?> LiveRegisterAsync(int sessionId, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// One click: records (or changes, as an audited correction) one student's attendance in a lesson that has started.
    /// Refused for a school-absent student, an unscheduled, cancelled or not-yet-started lesson. The record and the
    /// "present at school, absent from the lesson" alert are saved together (one transaction).
    /// </summary>
    Task<MarkResult> MarkAsync(int sessionId, DateOnly date, int studentId, SessionAttendanceStatus status, CancellationToken ct = default);

    Task<SessionReport> ReportAsync(SessionReportQuery query, CancellationToken ct = default);
}

public sealed partial class SessionAttendanceService
{
    public const int MaxReportDays = 120;

    // ------------------------------------------------------------------ the school clock and School Attendance

    private LessonPhase PhaseOf(LessonView l)
    {
        if (l.Cancelled) return LessonPhase.Cancelled;
        var today = _time.Today;
        if (l.Date > today) return LessonPhase.Upcoming;
        if (l.Date < today) return LessonPhase.Finished;
        if (l.Start is not { } s || l.End is not { } e) return LessonPhase.Untimed;
        var now = _time.TimeOfDay;
        return now < s ? LessonPhase.Upcoming : now < e ? LessonPhase.InProgress : LessonPhase.Finished;
    }

    /// <summary>
    /// Whether a school day with no School Attendance record counts as Absent: the School Attendance rule (from the first
    /// automatic day, past the 09:30 cutoff). Without School Attendance wired in (some tests) a missing record stays Not Recorded.
    /// </summary>
    private async Task<Func<DateOnly, bool>> AutomaticSchoolAbsenceAsync(CancellationToken ct)
    {
        if (SchoolAttendance is null) return _ => false;
        var rules = await SchoolAttendance.RulesAsync(ct);
        if (rules.AutomaticFrom is not { } from) return _ => false;
        var today = _time.Today;
        var pastCutoff = _time.TimeOfDay >= rules.Cutoff;
        return d => d >= from && (d < today || (d == today && pastCutoff));
    }

    private static SchoolStatus StatusOf(ArrivalStatus status, LateStatus? late, TimeOnly? time, string? recordedBy) => new(
        status == ArrivalStatus.Absent ? SchoolPresence.Absent : late == LateStatus.Late ? SchoolPresence.Late : SchoolPresence.Present,
        time, recordedBy == Arrivals.AutomaticRecorder);

    /// <summary>School Attendance of these students on a date (a school day is assumed: lessons only exist on school days).</summary>
    private async Task<Dictionary<int, SchoolStatus>> SchoolPresenceAsync(IReadOnlyCollection<int> studentIds, DateOnly date, CancellationToken ct)
    {
        var records = await Db.StudentArrivals.AsNoTracking().Where(a => a.ArrivalDate == date && studentIds.Contains(a.StudentId))
            .Select(a => new { a.StudentId, a.Status, a.LateStatus, a.ArrivalTime, a.RecordedBy }).ToListAsync(ct);
        var byStudent = records.ToDictionary(r => r.StudentId);
        var automatic = (await AutomaticSchoolAbsenceAsync(ct))(date);
        return studentIds.Distinct().ToDictionary(id => id, id => byStudent.TryGetValue(id, out var r)
            ? StatusOf(r.Status, r.LateStatus, r.ArrivalTime, r.RecordedBy)
            : automatic ? new SchoolStatus(SchoolPresence.Absent, null, true) : SchoolStatus.None);
    }

    /// <summary>Counts per lesson (students in the class that day; records of those students).</summary>
    private async Task<List<ClassLesson>> WithCountsAsync(IReadOnlyList<LessonView> lessons, DateOnly date, CancellationToken ct)
    {
        var classIds = lessons.Select(l => l.ClassId).Distinct().ToList();
        var roster = await Lessons.RosterAsync(Db, classIds, date, ct);
        var members = roster.GroupBy(r => r.ClassId).ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToHashSet());
        var ids = lessons.Select(l => l.SessionId).ToList();
        var records = (await Db.SessionAttendance.AsNoTracking().Where(r => r.SessionDate == date && ids.Contains(r.TimetableAssignmentId))
            .Select(r => new { r.TimetableAssignmentId, r.StudentId, r.Status }).ToListAsync(ct)).ToLookup(r => r.TimetableAssignmentId);
        return lessons.Select(l =>
        {
            var m = members.GetValueOrDefault(l.ClassId) ?? [];
            return new ClassLesson(l, PhaseOf(l), LessonCounts.Of(m.Count, records[l.SessionId].Where(r => m.Contains(r.StudentId)).Select(r => r.Status)));
        }).ToList();
    }

    // ------------------------------------------------------------------ class picker and class page

    public async Task<IReadOnlyList<ClassCard>> ClassesAsync(DateOnly date, CancellationToken ct = default)
    {
        var days = await SchoolDays.LoadAsync(Db, ct);
        var lessons = await WithCountsAsync(await Lessons.OnAsync(Db, date, days, null, ct, _time), date, ct);
        var byClass = lessons.ToLookup(l => l.Lesson.ClassId);
        var classes = await Db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.IsActive }).ToListAsync(ct);
        var roster = (await Lessons.RosterAsync(Db, classes.Select(c => c.Id).ToList(), date, ct)).GroupBy(s => s.ClassId).ToDictionary(g => g.Key, g => g.Count());
        return classes.Where(c => c.IsActive || byClass[c.Id].Any()).Select(c =>
        {
            var mine = byClass[c.Id].OrderBy(l => l.Lesson.Start ?? TimeOnly.MaxValue).ThenBy(l => l.Lesson.SlotNumber).ToList();
            var key = GradeCatalog.KeyOf(c.Name);
            return new ClassCard(c.Id, c.Name, ClassStages.Locate(c.Name)?.Stage.Title ?? "Other classes", key is null ? null : GradeCatalog.Label(key),
                ClassNumbering.Parse(c.Name)?.Section, c.IsActive, roster.GetValueOrDefault(c.Id), mine,
                mine.FirstOrDefault(l => l.Phase == LessonPhase.InProgress),
                mine.FirstOrDefault(l => l.Phase == LessonPhase.Upcoming && l.Lesson.Start is not null));
        }).ToList();
    }

    public async Task<ClassDay?> ClassDayAsync(int classId, DateOnly date, int? sessionId, CancellationToken ct = default)
    {
        var cls = await Db.Classes.AsNoTracking().Where(c => c.Id == classId).Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct);
        if (cls is null) return null;
        var days = await SchoolDays.LoadAsync(Db, ct);
        // Only the class's own timetable: never an unscheduled lesson.
        var lessons = (await WithCountsAsync(await Lessons.OnAsync(Db, date, days, new Lessons.Scope(classId), ct, _time), date, ct))
            .OrderBy(l => l.Lesson.Start ?? TimeOnly.MaxValue).ThenBy(l => l.Lesson.SlotNumber).ToList();
        var current = lessons.FirstOrDefault(l => l.Phase == LessonPhase.InProgress);
        var next = lessons.FirstOrDefault(l => l.Phase == LessonPhase.Upcoming && l.Lesson.Start is not null);
        var selected = sessionId is { } sid ? lessons.FirstOrDefault(l => l.Lesson.SessionId == sid) : current;
        return new ClassDay(cls.Id, cls.Name, date, _time.Now, date == _time.Today, days.IsSchoolDay(date), days.HolidayOn(date)?.Name,
            lessons, current, next, selected);
    }

    public async Task<LiveRegister?> LiveRegisterAsync(int sessionId, DateOnly date, CancellationToken ct = default)
    {
        var lesson = await LessonAsync(sessionId, date, ct);
        if (lesson is null) return null;
        var roster = await Lessons.RosterAsync(Db, [lesson.ClassId], date, ct);
        var ids = roster.Select(r => r.Id).ToList();
        var records = (await Db.SessionAttendance.AsNoTracking().Where(r => r.TimetableAssignmentId == sessionId && r.SessionDate == date)
                .Select(r => new { r.Id, r.StudentId, r.Status, r.Notes, r.RecordedBy, r.UpdatedBy, r.UpdatedAt, Corrections = r.Corrections.Count })
                .ToListAsync(ct))
            .ToDictionary(r => r.StudentId);
        var school = await SchoolPresenceAsync(ids, date, ct);
        var alerted = (await Db.AttendanceAlerts.AsNoTracking()
                .Where(a => a.Type == AttendanceAlertType.AbsentFromSession && a.TimetableAssignmentId == sessionId && a.FirstDate == date
                    && (a.Status == AttendanceAlertStatus.Open || a.Status == AttendanceAlertStatus.FollowUp))
                .Select(a => a.StudentId).ToListAsync(ct))
            .ToHashSet();
        var rows = roster.Select(s =>
        {
            records.TryGetValue(s.Id, out var r);
            return new LiveRow(s.Id, s.Name, school.GetValueOrDefault(s.Id), r?.Id, r?.Status, r?.Notes, r?.UpdatedAt,
                r is null ? null : r.UpdatedBy ?? r.RecordedBy, r?.Corrections ?? 0, alerted.Contains(s.Id));
        }).ToList();
        var recordIds = records.Values.Select(r => r.Id).ToList();
        var history = await Db.SessionAttendanceCorrections.AsNoTracking().Where(c => recordIds.Contains(c.SessionAttendanceRecordId))
            .OrderByDescending(c => c.CorrectedAt)
            .Select(c => new SessionCorrectionRow(c.Record.Student.FullName, c.Before, c.After, c.Reason, c.CorrectedAt, c.CorrectedBy))
            .ToListAsync(ct);
        var counts = LessonCounts.Of(roster.Count, rows.Where(r => r.Status is not null).Select(r => r.Status!.Value));
        return new LiveRegister(new ClassLesson(lesson, PhaseOf(lesson), counts), rows, LiveCounts.Of(rows), history);
    }

    // ------------------------------------------------------------------ one click

    public async Task<MarkResult> MarkAsync(int sessionId, DateOnly date, int studentId, SessionAttendanceStatus status, CancellationToken ct = default)
    {
        static MarkResult Fail(string message) => new(false, message, null, null, null);
        if (!Enum.IsDefined(status)) return Fail("Choose Present, Late or Absent.");
        var lesson = await LessonAsync(sessionId, date, ct);
        if (lesson is null) return Fail(NotScheduled(date));
        switch (PhaseOf(lesson))
        {
            case LessonPhase.Cancelled: return Fail("This lesson is cancelled on this date: no attendance is taken.");
            case LessonPhase.Upcoming:
                return Fail(date > _time.Today ? "Attendance cannot be recorded for a future date."
                    : $"{lesson.Subject} has not started yet ({lesson.Times12}). Attendance opens when the lesson starts.");
        }
        var student = (await Lessons.RosterAsync(Db, [lesson.ClassId], date, ct)).FirstOrDefault(s => s.Id == studentId);
        if (student is null) return Fail("This student is not in the class on this date. Refresh the page.");
        var school = (await SchoolPresenceAsync([studentId], date, ct))[studentId];
        if (school.Presence == SchoolPresence.Absent)
            return Fail($"{student.Name} was absent from school on {date.ToString("ddd d MMM", CultureInfo.InvariantCulture)}: lesson attendance is locked.");

        // The record and its alert are saved together: either both are stored or neither.
        await using (var tx = await Db.Database.BeginTransactionAsync(ct))
        {
            var record = await Db.SessionAttendance.FirstOrDefaultAsync(r => r.TimetableAssignmentId == sessionId && r.SessionDate == date && r.StudentId == studentId, ct);
            if (record is not null && record.Status == status)
                return new(true, $"{student.Name} is already {SessionStatuses.Label(status)} in {lesson.Subject}.", await RowAsync(sessionId, date, studentId, ct), null, null);
            if (record is null)
            {
                Db.SessionAttendance.Add(new SessionAttendanceRecord
                {
                    StudentId = studentId, TimetableAssignmentId = sessionId, SessionDate = date, SchoolClassId = lesson.ClassId,
                    Status = status, RecordedBy = User.Name,
                });
            }
            else
            {
                // A change to a saved entry is a correction: before, after, who and when are kept.
                var before = Describe(record.Status, record.Notes);
                (record.Status, record.UpdatedBy) = (status, User.Name);
                Db.SessionAttendanceCorrections.Add(new SessionAttendanceCorrection
                {
                    Record = record, Before = before, After = Describe(record.Status, record.Notes), CorrectedAt = _time.UtcNow, CorrectedBy = User.Name,
                });
            }
            try
            {
                await Db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                Db.ChangeTracker.Clear();
                return Fail($"{student.Name} was recorded a moment ago on another device. Refresh to see it.");
            }
            await Alerts.SyncLessonDayAsync([studentId], date, ct);
            await tx.CommitAsync(ct);
        }

        string? alertText = null;
        if (status == SessionAttendanceStatus.Absent && school.AtSchool)
            alertText = await Db.AttendanceAlerts.AsNoTracking()
                .Where(a => a.StudentId == studentId && a.Type == AttendanceAlertType.AbsentFromSession && a.TimetableAssignmentId == sessionId && a.FirstDate == date)
                .SelectMany(a => a.Events).OrderByDescending(e => e.Id).Select(e => e.Text).FirstOrDefaultAsync(ct);
        var register = await LiveRegisterAsync(sessionId, date, ct);
        return new(true, $"{student.Name}: {SessionStatuses.Label(status)} in {lesson.Subject}.",
            register?.Rows.FirstOrDefault(r => r.StudentId == studentId), register?.Counts, alertText);
    }

    private async Task<LiveRow?> RowAsync(int sessionId, DateOnly date, int studentId, CancellationToken ct) =>
        (await LiveRegisterAsync(sessionId, date, ct))?.Rows.FirstOrDefault(r => r.StudentId == studentId);

    // ------------------------------------------------------------------ reports

    public async Task<SessionReport> ReportAsync(SessionReportQuery q, CancellationToken ct = default)
    {
        var today = _time.Today;
        var (from, to) = q.From <= q.To ? (q.From, q.To) : (q.To, q.From);
        if (to > today) to = today;
        if (from > to) from = to;
        if (q.Mode == "lesson") from = to = q.From > today ? today : q.From;
        if (to.DayNumber - from.DayNumber >= MaxReportDays) from = to.AddDays(-(MaxReportDays - 1));
        q = q with { From = from, To = to };

        var days = await SchoolDays.LoadAsync(Db, ct);
        var automatic = await AutomaticSchoolAbsenceAsync(ct);
        var student = q.Mode == "student" && q.StudentId is { } sid
            ? await Db.Students.AsNoTracking().Where(s => s.Id == sid).Select(s => new { s.Id, s.FullName, ClassName = s.SchoolClass.Name }).FirstOrDefaultAsync(ct)
            : null;

        // School attendance and lesson records of the period, read once.
        var arrivals = (await Db.StudentArrivals.AsNoTracking().Where(a => a.ArrivalDate >= from && a.ArrivalDate <= to && (student == null || a.StudentId == student.Id))
                .Select(a => new { a.StudentId, a.ArrivalDate, a.Status, a.LateStatus, a.ArrivalTime, a.RecordedBy }).ToListAsync(ct))
            .ToDictionary(a => (a.StudentId, a.ArrivalDate), a => StatusOf(a.Status, a.LateStatus, a.ArrivalTime, a.RecordedBy));
        var records = (await Db.SessionAttendance.AsNoTracking().Where(r => r.SessionDate >= from && r.SessionDate <= to && (student == null || r.StudentId == student.Id))
                .Select(r => new { r.StudentId, r.TimetableAssignmentId, r.SessionDate, r.Status, r.Notes }).ToListAsync(ct))
            .ToDictionary(r => (r.StudentId, r.TimetableAssignmentId, r.SessionDate));

        var lines = new List<SessionReportLine>();
        var names = new Dictionary<int, (string Name, string ClassName)>();
        var lessonCount = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (!days.IsSchoolDay(d)) continue;
            int? classId = q.Mode switch
            {
                "class" => q.ClassId,
                "student" when student is not null => (await Lessons.StudentOnAsync(Db, student.Id, d, ct))?.ClassId,
                _ => null,
            };
            if (q.Mode is "class" or "student" && classId is null) continue;
            var scope = q.Mode == "lesson" ? new Lessons.Scope(SessionIds: q.SessionId is { } ls ? [ls] : []) : new Lessons.Scope(classId);
            // Only lessons that took place: on the timetable that day, started, not cancelled.
            var lessons = (await Lessons.OnAsync(Db, d, days, scope, ct, _time)).Where(l => l.IsExpected).ToList();
            if (lessons.Count == 0) continue;
            lessonCount += lessons.Count;
            var roster = (await Lessons.RosterAsync(Db, lessons.Select(l => l.ClassId).Distinct().ToList(), d, ct))
                .Where(s => student is null || s.Id == student.Id).ToList();
            foreach (var s in roster) names[s.Id] = (s.Name, s.ClassName);
            var auto = automatic(d);
            foreach (var l in lessons)
            foreach (var s in roster.Where(s => s.ClassId == l.ClassId))
            {
                var school = arrivals.TryGetValue((s.Id, d), out var a) ? a : auto ? new SchoolStatus(SchoolPresence.Absent, null, true) : SchoolStatus.None;
                records.TryGetValue((s.Id, l.SessionId, d), out var r);
                // Absent from school that day: a school absence, never a lesson absence (whatever an older lesson record says).
                var kind = school.Presence == SchoolPresence.Absent && r?.Status is null or SessionAttendanceStatus.Absent ? SessionKind.SchoolAbsent
                    : r?.Status switch
                    {
                        SessionAttendanceStatus.Present => SessionKind.Present,
                        SessionAttendanceStatus.Late => SessionKind.Late,
                        SessionAttendanceStatus.Absent => SessionKind.Absent,
                        SessionAttendanceStatus.Excused => SessionKind.Excused,
                        _ => SessionKind.NotRecorded,
                    };
                lines.Add(new SessionReportLine(l, s.Id, s.Name, kind, school.Presence, r?.Notes));
            }
        }

        SessionTally Sum(IEnumerable<SessionReportLine> ls) => ls.Aggregate(SessionTally.Zero, (acc, x) => acc + SessionTally.Of(x.Kind, x.School is SchoolPresence.Present or SchoolPresence.Late));
        var students = lines.GroupBy(x => x.StudentId)
            .Select(g => new SessionReportStudent(g.Key, names[g.Key].Name, names[g.Key].ClassName, Sum(g)))
            .OrderBy(s => s.ClassName).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var byClass = lines.GroupBy(x => (x.Lesson.ClassId, x.Lesson.ClassName, x.Lesson.ClassOrder)).OrderBy(g => g.Key.ClassOrder).ThenBy(g => g.Key.ClassName)
            .Select(g => new SessionReportGroup(g.Key.ClassName, g.Select(x => (x.Lesson.SessionId, x.Lesson.Date)).Distinct().Count(), Sum(g))).ToList();
        var bySubject = lines.GroupBy(x => x.Lesson.Subject).OrderBy(g => g.Key)
            .Select(g => new SessionReportGroup(g.Key, g.Select(x => (x.Lesson.SessionId, x.Lesson.Date)).Distinct().Count(), Sum(g))).ToList();
        // Repeated lesson absences while at school: two or more in the period.
        var repeated = students.Where(s => s.Tally.AbsentAtSchool >= 2).OrderByDescending(s => s.Tally.AbsentAtSchool).ToList();

        var lessonView = lines.FirstOrDefault()?.Lesson;
        var (title, subtitle) = q.Mode switch
        {
            "student" => ("Student Lesson Attendance", student?.FullName),
            "class" => ("Class Lesson Attendance", await Db.Classes.Where(c => c.Id == q.ClassId).Select(c => c.Name).FirstOrDefaultAsync(ct)),
            "lesson" => ("Lesson Attendance", lessonView is null ? null : $"{lessonView.Subject} — {lessonView.ClassName}, {lessonView.Period} ({lessonView.Times12})"),
            _ => ("School Session Attendance", "Whole school"),
        };
        // Lines kept for display: every lesson of a student or of one lesson; for a class or the school, the lesson absences.
        var shown = q.Mode is "student" or "lesson" ? lines : lines.Where(x => x.Kind == SessionKind.Absent).ToList();
        return new SessionReport(q, title, subtitle, lessonCount, Sum(lines), byClass, bySubject, students,
            shown.OrderBy(x => x.Lesson.Date).ThenBy(x => x.Lesson.Start ?? TimeOnly.MaxValue).ThenBy(x => x.Lesson.SlotNumber).ThenBy(x => x.StudentName).ToList(),
            repeated);
    }
}
