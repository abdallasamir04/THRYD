using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>
/// One lesson on one date, read from the class timetable (no copy): the session row active in that week
/// for that weekday, its slot times, subject and teacher, a substitute placed in that slot for that week
/// (Assign Period → Substitution) and a cancellation for that date if there is one.
/// </summary>
public sealed record LessonView(
    int SessionId, DateOnly Date, int ClassId, string ClassName, int ClassOrder, int SlotId, int SlotNumber, string? SlotName,
    TimeOnly? Start, TimeOnly? End, string DepartmentName, string Subject, int TeacherId, string TeacherName,
    int? SubstituteId, string? SubstituteName, string? CancelReason, bool Upcoming)
{
    public string Period => string.IsNullOrWhiteSpace(SlotName) ? $"Period {SlotNumber}" : SlotName!;
    public string Times => ScheduleRules.Times(Start, End);
    public bool Cancelled => CancelReason is not null;

    /// <summary>Who teaches the lesson on that date: the substitute when there is one.</summary>
    public string Teacher => SubstituteName is null ? TeacherName : $"{SubstituteName} (covering {TeacherName})";

    /// <summary>Cancelled lessons and lessons that have not started yet are not expected (and never alert).</summary>
    public bool IsExpected => !Cancelled && !Upcoming;
}

/// <summary>Student-lesson counts. A lesson without a record is pending (not recorded), never absent.</summary>
public sealed record LessonCounts(int Expected, int Recorded, int Present, int Absent, int Late, int Excused)
{
    public static readonly LessonCounts Zero = new(0, 0, 0, 0, 0, 0);

    public int Pending => Math.Max(0, Expected - Recorded);

    /// <summary>(Present + Late) ÷ (Present + Late + Absent). Excused and not-recorded lessons are left out; null when nothing counts.</summary>
    public double? Percent => Present + Late + Absent == 0 ? null : Math.Round((Present + Late) * 100.0 / (Present + Late + Absent), 1);

    public LessonCounts Add(LessonCounts o) =>
        new(Expected + o.Expected, Recorded + o.Recorded, Present + o.Present, Absent + o.Absent, Late + o.Late, Excused + o.Excused);

    public static LessonCounts Of(int expected, IEnumerable<SessionAttendanceStatus> statuses)
    {
        var list = statuses.ToList();
        return new(expected, list.Count, list.Count(s => s == SessionAttendanceStatus.Present), list.Count(s => s == SessionAttendanceStatus.Absent),
            list.Count(s => s == SessionAttendanceStatus.Late), list.Count(s => s == SessionAttendanceStatus.Excused));
    }
}

public sealed record LessonRow(LessonView Lesson, LessonCounts Counts, SessionAttendanceStatus? StudentStatus);

public sealed record SessionFilter(DateOnly Date, int? ClassId = null, int? SlotId = null, string? Subject = null, int? TeacherId = null, int? StudentId = null);

public sealed record StudentPick(int Id, string Name, int ClassId, string ClassName);

public sealed record SessionDay(SessionFilter Filter, HolidayView? Holiday, StudentPick? Student, IReadOnlyList<LessonRow> Lessons, LessonCounts Totals)
{
    public bool IsWeekend => !ScheduleRules.IsSchoolDay(Filter.Date.DayOfWeek);
}

public sealed record RegisterRow(
    int StudentId, string Name, int? RecordId, SessionAttendanceStatus? Status, string? Notes, string? RecordedBy, DateTime? UpdatedAt,
    int Corrections, ArrivalStatus? School, LateStatus? SchoolLate);

public sealed record SessionCorrectionRow(string StudentName, string Before, string After, string? Reason, DateTime At, string? By);

public sealed record SessionRegister(LessonView Lesson, IReadOnlyList<RegisterRow> Rows, LessonCounts Counts, IReadOnlyList<SessionCorrectionRow> History);

public sealed record SessionEntry(int StudentId, SessionAttendanceStatus? Status, string? Notes);

public sealed record SessionFilterOptions(
    IReadOnlyList<(int Id, string Name)> Classes, IReadOnlyList<SlotView> Slots, IReadOnlyList<string> Subjects,
    IReadOnlyList<(int Id, string Name)> Teachers, IReadOnlyList<StudentPick> Students);

public interface ISessionAttendanceService
{
    Task<SessionDay> DayAsync(SessionFilter filter, CancellationToken ct = default);
    Task<SessionFilterOptions> OptionsAsync(DateOnly date, int? classId, CancellationToken ct = default);
    Task<SessionRegister?> RegisterAsync(int sessionId, DateOnly date, CancellationToken ct = default);
    Task<ServiceResult> SaveAsync(int sessionId, DateOnly date, IReadOnlyList<SessionEntry> entries, string? reason, CancellationToken ct = default);
    Task<ServiceResult> CancelAsync(int sessionId, DateOnly date, string? reason, CancellationToken ct = default);
    Task<ServiceResult> RestoreAsync(int sessionId, DateOnly date, CancellationToken ct = default);
}

public static class SessionStatuses
{
    public static readonly SessionAttendanceStatus[] All =
        [SessionAttendanceStatus.Present, SessionAttendanceStatus.Late, SessionAttendanceStatus.Absent, SessionAttendanceStatus.Excused];

    public static string Label(SessionAttendanceStatus? s) => s switch
    {
        SessionAttendanceStatus.Present => "Present",
        SessionAttendanceStatus.Absent => "Absent",
        SessionAttendanceStatus.Late => "Late",
        SessionAttendanceStatus.Excused => "Excused",
        _ => "Not recorded",
    };

    public static string Badge(SessionAttendanceStatus? s) => s switch
    {
        SessionAttendanceStatus.Present => "badge--success",
        SessionAttendanceStatus.Absent => "badge--error",
        SessionAttendanceStatus.Late => "badge--warning",
        SessionAttendanceStatus.Excused => "badge--primary",
        _ => "badge--neutral",
    };
}

/// <summary>
/// Reads lessons from the timetable for a date. Shared by session attendance and the alerts, so both
/// agree on what was scheduled, cancelled or not started yet.
/// </summary>
public static class Lessons
{
    public sealed record Scope(int? ClassId = null, int? SlotId = null, string? Subject = null, IReadOnlyCollection<int>? SessionIds = null);

    public static async Task<List<LessonView>> OnAsync(SchoolDbContext db, DateOnly date, SchoolDays days, Scope? scope, CancellationToken ct)
    {
        if (!days.IsSchoolDay(date)) return [];
        var week = SchoolWeek.StartOf(date);
        var day = date.DayOfWeek;
        // Lessons recorded on that date stay visible even if the session was replaced later that week.
        var recorded = db.SessionAttendance.Where(r => r.SessionDate == date).Select(r => r.TimetableAssignmentId);
        var q = db.TimetableAssignments.AsNoTracking()
            .Where(t => t.Day == day && ((t.StartsOn <= week && (t.EndedOn == null || t.EndedOn > week)) || recorded.Contains(t.Id)));
        if (scope?.ClassId is { } c) q = q.Where(t => t.SchoolClassId == c);
        if (scope?.SlotId is { } sl) q = q.Where(t => t.TimeSlotId == sl);
        if (!string.IsNullOrWhiteSpace(scope?.Subject)) q = q.Where(t => t.Subject == scope!.Subject);
        if (scope?.SessionIds is { } ids) q = q.Where(t => ids.Contains(t.Id));
        var rows = await q.Select(t => new
        {
            t.Id, t.SchoolClassId, ClassName = t.SchoolClass.Name, ClassOrder = t.SchoolClass.SortOrder, t.TimeSlotId,
            SlotNumber = t.TimeSlot.Number, SlotName = t.TimeSlot.Name, t.TimeSlot.StartTime, t.TimeSlot.EndTime,
            Department = t.Department.Name, t.Subject, t.TeacherId, Teacher = t.Teacher.FullName,
            Active = t.StartsOn <= week && (t.EndedOn == null || t.EndedOn > week),
        }).ToListAsync(ct);
        if (rows.Count == 0) return [];

        var withRecords = (await recorded.Distinct().ToListAsync(ct)).ToHashSet();
        // One lesson per class and slot: the row that was recorded, otherwise the active one.
        rows = rows.GroupBy(r => (r.SchoolClassId, r.TimeSlotId))
            .Select(g => g.OrderByDescending(r => withRecords.Contains(r.Id)).ThenByDescending(r => r.Active).First()).ToList();

        var subs = (await db.WorkloadAssignments.AsNoTracking()
                .Where(w => w.Category == WorkloadCategory.Substitution && w.WeekStart == week && w.Day == day && w.SchoolClassId != null && w.TimeSlotId != null)
                .Select(w => new { ClassId = w.SchoolClassId!.Value, SlotId = w.TimeSlotId!.Value, w.TeacherId, w.Teacher.FullName })
                .ToListAsync(ct))
            .GroupBy(w => (w.ClassId, w.SlotId)).ToDictionary(g => g.Key, g => g.First());
        var lessonIds = rows.Select(r => r.Id).ToList();
        var cancelled = await db.SessionCancellations.AsNoTracking().Where(x => x.SessionDate == date && lessonIds.Contains(x.TimetableAssignmentId))
            .ToDictionaryAsync(x => x.TimetableAssignmentId, x => x.Reason, ct);

        var today = SchoolWeek.Today;
        var now = TimeOnly.FromDateTime(DateTime.Now);
        return rows.Select(r =>
            {
                subs.TryGetValue((r.SchoolClassId, r.TimeSlotId), out var sub);
                var upcoming = date > today || (date == today && r.StartTime is { } s && s > now);
                return new LessonView(r.Id, date, r.SchoolClassId, r.ClassName, r.ClassOrder, r.TimeSlotId, r.SlotNumber, r.SlotName, r.StartTime, r.EndTime,
                    r.Department, r.Subject, r.TeacherId, r.Teacher, sub?.TeacherId, sub?.FullName, cancelled.GetValueOrDefault(r.Id), upcoming);
            })
            .OrderBy(l => l.SlotNumber).ThenBy(l => l.ClassOrder).ThenBy(l => l.ClassName).ToList();
    }

    /// <summary>
    /// Students expected in a class on a date: enrolled on or before it, and in that class that day
    /// (a later transfer is undone using the transfer history).
    /// </summary>
    public static async Task<List<StudentPick>> RosterAsync(SchoolDbContext db, IReadOnlyCollection<int> classIds, DateOnly date, CancellationToken ct)
    {
        if (classIds.Count == 0) return [];
        var dayEndUtc = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var movedLater = db.StudentTransfers.Where(t => t.TransferredAt >= dayEndUtc);
        var candidates = await db.Students.AsNoTracking()
            .Where(s => classIds.Contains(s.SchoolClassId) || movedLater.Any(t => t.StudentId == s.Id && classIds.Contains(t.FromClassId)))
            .Select(s => new
            {
                s.Id, s.FullName, s.SchoolClassId, s.CreatedAt,
                ThatDay = movedLater.Where(t => t.StudentId == s.Id).OrderBy(t => t.TransferredAt).Select(t => (int?)t.FromClassId).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var names = await db.Classes.AsNoTracking().Where(c => classIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return candidates.Where(s => DateOnly.FromDateTime(s.CreatedAt.ToLocalTime()) <= date)
            .Select(s => new { s.Id, s.FullName, ClassId = s.ThatDay ?? s.SchoolClassId })
            .Where(s => classIds.Contains(s.ClassId))
            .OrderBy(s => s.FullName, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new StudentPick(s.Id, s.FullName, s.ClassId, names.GetValueOrDefault(s.ClassId, ""))).ToList();
    }

    /// <summary>The student's class on a date (current class unless a later transfer moved them).</summary>
    public static async Task<StudentPick?> StudentOnAsync(SchoolDbContext db, int studentId, DateOnly date, CancellationToken ct)
    {
        var dayEndUtc = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var s = await db.Students.AsNoTracking().Where(x => x.Id == studentId).Select(x => new
        {
            x.Id, x.FullName, x.SchoolClassId,
            ThatDay = db.StudentTransfers.Where(t => t.StudentId == x.Id && t.TransferredAt >= dayEndUtc).OrderBy(t => t.TransferredAt).Select(t => (int?)t.FromClassId).FirstOrDefault(),
        }).FirstOrDefaultAsync(ct);
        if (s is null) return null;
        var classId = s.ThatDay ?? s.SchoolClassId;
        var name = await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        return new StudentPick(s.Id, s.FullName, classId, name);
    }
}

/// <summary>
/// Session Attendance: per-lesson registers built from the class timetable. Statuses Present, Late,
/// Absent and Excused; a student without a record stays "Not recorded". Every change to a saved record
/// is kept as a correction (before, after, optional reason, who). Alerts are re-checked after each save.
/// </summary>
public sealed class SessionAttendanceService(SchoolDbContext db, ICurrentUser currentUser, IAttendanceAlertService alerts) : ISessionAttendanceService
{
    public async Task<SessionDay> DayAsync(SessionFilter filter, CancellationToken ct = default)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        var student = filter.StudentId is { } sid ? await Lessons.StudentOnAsync(db, sid, filter.Date, ct) : null;
        var classId = student?.ClassId ?? filter.ClassId;
        if (student is not null && filter.ClassId is { } fc && fc != student.ClassId) classId = -1; // contradicting filters: nothing
        var lessons = await Lessons.OnAsync(db, filter.Date, days, new Lessons.Scope(classId, filter.SlotId, filter.Subject), ct);
        if (filter.TeacherId is { } tid) lessons = lessons.Where(l => l.TeacherId == tid || l.SubstituteId == tid).ToList();

        var classIds = lessons.Select(l => l.ClassId).Distinct().ToList();
        var roster = await Lessons.RosterAsync(db, classIds, filter.Date, ct);
        var expected = roster.GroupBy(r => r.ClassId).ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToHashSet());
        var ids = lessons.Select(l => l.SessionId).ToList();
        var records = await db.SessionAttendance.AsNoTracking().Where(r => r.SessionDate == filter.Date && ids.Contains(r.TimetableAssignmentId))
            .Select(r => new { r.TimetableAssignmentId, r.StudentId, r.Status }).ToListAsync(ct);
        var byLesson = records.ToLookup(r => r.TimetableAssignmentId);

        var rows = lessons.Select(l =>
        {
            var members = expected.GetValueOrDefault(l.ClassId) ?? [];
            var counts = LessonCounts.Of(members.Count, byLesson[l.SessionId].Where(r => members.Contains(r.StudentId)).Select(r => r.Status));
            var mine = student is null ? null : byLesson[l.SessionId].Where(r => r.StudentId == student.Id).Select(r => (SessionAttendanceStatus?)r.Status).FirstOrDefault();
            if (student is not null) counts = LessonCounts.Of(1, mine is { } m ? [m] : []);
            return new LessonRow(l, counts, mine);
        }).ToList();
        var totals = rows.Where(r => r.Lesson.IsExpected).Aggregate(LessonCounts.Zero, (acc, r) => acc.Add(r.Counts));
        return new SessionDay(filter, days.HolidayOn(filter.Date), student, rows, totals);
    }

    public async Task<SessionFilterOptions> OptionsAsync(DateOnly date, int? classId, CancellationToken ct = default)
    {
        var classes = (await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Select(c => (c.Id, c.Name)).ToList();
        var slots = await db.TimeSlots.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Number)
            .Select(s => new SlotView(s.Id, s.Number, s.Name, s.StartTime, s.EndTime, s.IsActive)).ToListAsync(ct);
        var week = SchoolWeek.StartOf(date);
        var active = db.TimetableAssignments.AsNoTracking().ActiveIn(week);
        var subjects = await active.Select(t => t.Subject).Distinct().OrderBy(s => s).ToListAsync(ct);
        var teacherIds = active.Select(t => t.TeacherId);
        var teachers = (await db.Teachers.AsNoTracking().Where(t => teacherIds.Contains(t.Id)).OrderBy(t => t.FullName)
            .Select(t => new { t.Id, t.FullName }).ToListAsync(ct)).Select(t => (t.Id, t.FullName)).ToList();
        IReadOnlyList<StudentPick> students = classId is { } c
            ? await Lessons.RosterAsync(db, [c], date, ct)
            : await db.Students.AsNoTracking().OrderBy(s => s.FullName)
                .Select(s => new StudentPick(s.Id, s.FullName, s.SchoolClassId, s.SchoolClass.Name)).ToListAsync(ct);
        return new SessionFilterOptions(classes, slots, subjects, teachers, students);
    }

    public async Task<SessionRegister?> RegisterAsync(int sessionId, DateOnly date, CancellationToken ct = default)
    {
        var lesson = await LessonAsync(sessionId, date, ct);
        if (lesson is null) return null;
        var roster = await Lessons.RosterAsync(db, [lesson.ClassId], date, ct);
        var ids = roster.Select(r => r.Id).ToList();
        var records = (await db.SessionAttendance.AsNoTracking().Where(r => r.TimetableAssignmentId == sessionId && r.SessionDate == date)
                .Select(r => new { r.Id, r.StudentId, r.Status, r.Notes, r.RecordedBy, r.UpdatedBy, r.UpdatedAt, Corrections = r.Corrections.Count })
                .ToListAsync(ct))
            .ToDictionary(r => r.StudentId);
        var school = await db.StudentArrivals.AsNoTracking().Where(a => a.ArrivalDate == date && ids.Contains(a.StudentId))
            .ToDictionaryAsync(a => a.StudentId, a => new { a.Status, a.LateStatus }, ct);

        var rows = roster.Select(s =>
        {
            records.TryGetValue(s.Id, out var r);
            school.TryGetValue(s.Id, out var a);
            return new RegisterRow(s.Id, s.Name, r?.Id, r?.Status, r?.Notes, r is null ? null : r.UpdatedBy ?? r.RecordedBy, r?.UpdatedAt,
                r?.Corrections ?? 0, a?.Status, a?.LateStatus);
        }).ToList();

        var recordIds = records.Values.Select(r => r.Id).ToList();
        var history = await db.SessionAttendanceCorrections.AsNoTracking().Where(c => recordIds.Contains(c.SessionAttendanceRecordId))
            .OrderByDescending(c => c.CorrectedAt)
            .Select(c => new SessionCorrectionRow(c.Record.Student.FullName, c.Before, c.After, c.Reason, c.CorrectedAt, c.CorrectedBy))
            .ToListAsync(ct);
        var counts = LessonCounts.Of(roster.Count, rows.Where(r => r.Status is not null).Select(r => r.Status!.Value));
        return new SessionRegister(lesson, rows, counts, history);
    }

    public async Task<ServiceResult> SaveAsync(int sessionId, DateOnly date, IReadOnlyList<SessionEntry> entries, string? reason, CancellationToken ct = default)
    {
        var lesson = await LessonAsync(sessionId, date, ct);
        if (lesson is null) return ServiceResult.Fail("", NotScheduled(date));
        if (lesson.Cancelled) return ServiceResult.Fail("", "This lesson is cancelled on this date. Restore it to record attendance.");
        if (lesson.Upcoming)
            return ServiceResult.Fail("", date > SchoolWeek.Today
                ? "Attendance cannot be recorded for a future date."
                : $"This lesson has not started yet ({lesson.Times}). Record attendance once it has started.");

        var why = TextNormalizer.CleanText(reason);
        if (why.Length > SessionAttendanceCorrection.ReasonMaxLength)
            return ServiceResult.Fail("Reason", $"Use {SessionAttendanceCorrection.ReasonMaxLength} characters or fewer.");

        var roster = (await Lessons.RosterAsync(db, [lesson.ClassId], date, ct)).ToDictionary(r => r.Id);
        var chosen = entries.Where(e => e.Status is not null).DistinctBy(e => e.StudentId).ToList();
        foreach (var e in chosen)
        {
            if (!roster.TryGetValue(e.StudentId, out var who))
                return ServiceResult.Fail("", "A student in the list is not in this class on this date. Refresh the page.");
            if (!Enum.IsDefined(e.Status!.Value)) return ServiceResult.Fail("", $"Choose Present, Late, Absent or Excused for {who.Name}.");
            if (TextNormalizer.CleanText(e.Notes).Length > SessionAttendanceRecord.NotesMaxLength)
                return ServiceResult.Fail("", $"The note for {who.Name} is too long (at most {SessionAttendanceRecord.NotesMaxLength} characters).");
        }

        var studentIds = chosen.Select(e => e.StudentId).ToList();
        var existing = await db.SessionAttendance.Where(r => r.TimetableAssignmentId == sessionId && r.SessionDate == date && studentIds.Contains(r.StudentId))
            .ToDictionaryAsync(r => r.StudentId, ct);
        var changed = new List<int>();
        foreach (var e in chosen)
        {
            var notes = TextNormalizer.CleanText(e.Notes);
            var cleanNotes = notes.Length == 0 ? null : notes;
            if (existing.TryGetValue(e.StudentId, out var record))
            {
                if (record.Status == e.Status && record.Notes == cleanNotes) continue;
                var before = Describe(record.Status, record.Notes);
                (record.Status, record.Notes, record.UpdatedBy) = (e.Status!.Value, cleanNotes, currentUser.Name);
                db.SessionAttendanceCorrections.Add(new SessionAttendanceCorrection
                {
                    Record = record, Before = before, After = Describe(record.Status, record.Notes),
                    Reason = why.Length == 0 ? null : why, CorrectedAt = DateTime.UtcNow, CorrectedBy = currentUser.Name,
                });
            }
            else
            {
                db.SessionAttendance.Add(new SessionAttendanceRecord
                {
                    StudentId = e.StudentId, TimetableAssignmentId = sessionId, SessionDate = date, SchoolClassId = lesson.ClassId,
                    Status = e.Status!.Value, Notes = cleanNotes, RecordedBy = currentUser.Name,
                });
            }
            changed.Add(e.StudentId);
        }
        if (changed.Count == 0) return ServiceResult.Fail("", chosen.Count == 0 ? "Nothing to save: choose a status for at least one student." : "Nothing was changed.");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "Someone else recorded this lesson a moment ago. Refresh the page to see their entries.");
        }
        await alerts.SyncLessonDayAsync(changed, date, ct);
        return ServiceResult.Ok(changed.Count);
    }

    public async Task<ServiceResult> CancelAsync(int sessionId, DateOnly date, string? reason, CancellationToken ct = default)
    {
        var lesson = await LessonAsync(sessionId, date, ct);
        if (lesson is null) return ServiceResult.Fail("", NotScheduled(date));
        if (lesson.Cancelled) return ServiceResult.Fail("", "This lesson is already cancelled on this date.");
        var why = TextNormalizer.CleanText(reason);
        if (why.Length == 0) return ServiceResult.Fail("Reason", "Explain why the lesson does not take place.");
        if (why.Length > SessionCancellation.ReasonMaxLength) return ServiceResult.Fail("Reason", $"Use {SessionCancellation.ReasonMaxLength} characters or fewer.");
        db.SessionCancellations.Add(new SessionCancellation
        {
            TimetableAssignmentId = sessionId, SessionDate = date, Reason = why, CancelledBy = currentUser.Name, CancelledAt = DateTime.UtcNow,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This lesson was just cancelled by someone else.");
        }
        await alerts.SyncLessonAsync(sessionId, date, ct);
        return ServiceResult.Ok(sessionId);
    }

    public async Task<ServiceResult> RestoreAsync(int sessionId, DateOnly date, CancellationToken ct = default)
    {
        var removed = await db.SessionCancellations.Where(x => x.TimetableAssignmentId == sessionId && x.SessionDate == date).ExecuteDeleteAsync(ct);
        if (removed == 0) return ServiceResult.Fail("", "This lesson is not cancelled on this date.");
        await alerts.SyncLessonAsync(sessionId, date, ct);
        return ServiceResult.Ok(sessionId);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<LessonView?> LessonAsync(int sessionId, DateOnly date, CancellationToken ct)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        var lessons = await Lessons.OnAsync(db, date, days, new Lessons.Scope(SessionIds: [sessionId]), ct);
        return lessons.FirstOrDefault(l => l.SessionId == sessionId);
    }

    private static string NotScheduled(DateOnly date) =>
        $"This lesson is not on the timetable on {date.ToString("dddd d MMM yyyy", CultureInfo.InvariantCulture)}.";

    private static string Describe(SessionAttendanceStatus status, string? notes) =>
        Trim(SessionStatuses.Label(status) + (notes is null ? "" : " · notes: " + notes));

    private static string Trim(string s) => s.Length > SessionAttendanceCorrection.TextMaxLength ? s[..SessionAttendanceCorrection.TextMaxLength] : s;
}
