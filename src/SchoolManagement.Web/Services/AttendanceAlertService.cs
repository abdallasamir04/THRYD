using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record AlertRow(
    int Id, AttendanceAlertType Type, AttendanceAlertStatus Status, int StudentId, string StudentName, int ClassId, string ClassName,
    DateOnly FirstDate, DateOnly LastDate, int DayCount, IReadOnlyList<DateOnly> Dates,
    int? SessionId, string? Subject, int? SlotNumber, string? SlotName, TimeOnly? Start, TimeOnly? End, string? TeacherName, string? SubstituteName,
    DateTime CreatedAt, DateTime UpdatedAt, string? ResolvedBy, DateTime? ResolvedAt, string? Resolution)
{
    public bool IsPending => Status is AttendanceAlertStatus.Open or AttendanceAlertStatus.FollowUp;
    public string Period => SlotNumber is null ? "" : string.IsNullOrWhiteSpace(SlotName) ? $"Period {SlotNumber}" : SlotName!;
    public string Times => ScheduleRules.Times(Start, End);
    public string? Teacher => TeacherName is null ? null : SubstituteName is null ? TeacherName : $"{SubstituteName} (covering {TeacherName})";
}

public sealed record AlertEventRow(AttendanceAlertEventKind Kind, string Text, DateTime At, string? By);

public sealed record AlertDetails(AlertRow Alert, IReadOnlyList<AlertEventRow> Events);

public sealed record AlertFilter(AttendanceAlertType? Type = null, string? State = null, int? ClassId = null, string? Search = null);

public sealed record AlertCounts(int ConsecutivePending, int SessionPending, int FollowUp, int Resolved, int Cleared)
{
    public int Pending => ConsecutivePending + SessionPending;
}

public interface IAttendanceAlertService
{
    /// <summary>After a school attendance record is saved or corrected.</summary>
    Task SyncStudentDayAsync(int studentId, DateOnly date, CancellationToken ct = default);

    /// <summary>After lesson attendance of these students on this date was saved.</summary>
    Task SyncLessonDayAsync(IReadOnlyCollection<int> studentIds, DateOnly date, CancellationToken ct = default);

    /// <summary>After a lesson was cancelled or restored on a date.</summary>
    Task SyncLessonAsync(int sessionId, DateOnly date, CancellationToken ct = default);

    /// <summary>Re-checks every student with absences or alerts between the dates (holidays, scheduled sweep, manual re-check).</summary>
    Task<int> RecheckAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<IReadOnlyList<AlertRow>> ListAsync(AlertFilter filter, CancellationToken ct = default);
    Task<AlertCounts> CountsAsync(CancellationToken ct = default);
    Task<AlertDetails?> GetAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> FollowUpAsync(int id, string? note, CancellationToken ct = default);
    Task<ServiceResult> ResolveAsync(int id, string? note, CancellationToken ct = default);
    Task<ServiceResult> ReopenAsync(int id, string? note, CancellationToken ct = default);
}

public static class AlertLabels
{
    public const int ConsecutiveDays = 3;

    public static string Type(AttendanceAlertType t) => t switch
    {
        AttendanceAlertType.ConsecutiveAbsence => "3 consecutive school-day absences",
        AttendanceAlertType.AbsentFromSession => "Present at school, absent from a lesson",
        _ => t.ToString(),
    };

    public static string Status(AttendanceAlertStatus s) => s switch
    {
        AttendanceAlertStatus.Open => "Open",
        AttendanceAlertStatus.FollowUp => "Following up",
        AttendanceAlertStatus.Resolved => "Resolved",
        AttendanceAlertStatus.Cleared => "Cleared",
        _ => s.ToString(),
    };

    public static string Badge(AttendanceAlertStatus s) => s switch
    {
        AttendanceAlertStatus.Open => "badge--error",
        AttendanceAlertStatus.FollowUp => "badge--warning",
        AttendanceAlertStatus.Resolved => "badge--success",
        _ => "badge--neutral",
    };

    public static string Event(AttendanceAlertEventKind k) => k switch
    {
        AttendanceAlertEventKind.Created => "Alert raised",
        AttendanceAlertEventKind.Updated => "Updated",
        AttendanceAlertEventKind.FollowUp => "Follow-up",
        AttendanceAlertEventKind.Resolved => "Resolved",
        AttendanceAlertEventKind.Reopened => "Reopened",
        AttendanceAlertEventKind.Cleared => "Cleared automatically",
        _ => k.ToString(),
    };

    public static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>
/// Attendance alerts, derived from the records every time they change (so corrections update them):
///  • Three consecutive school-day absences: school days are Sunday–Thursday outside school holidays;
///    every day of the streak needs a recorded Absent — an unrecorded day is not an absence and breaks it.
///    One alert per streak (key: student + first day); a longer streak updates the same alert.
///  • Present at school but Absent from a scheduled lesson: only when the school record says Present,
///    and only for lessons on the timetable that day that are not cancelled and have started.
///    One alert per student + lesson + date.
/// An alert that no longer applies after a correction is Cleared (with history), and comes back
/// (Reopened) if the records match again. Alerts resolved by staff stay resolved.
/// </summary>
public sealed class AttendanceAlertService(SchoolDbContext db, ICurrentUser currentUser) : IAttendanceAlertService
{
    private static string StreakKey(int studentId, DateOnly first) => $"CA:{studentId}:{first:yyyyMMdd}";
    private static string LessonKey(int studentId, int sessionId, DateOnly date) => $"SA:{studentId}:{sessionId}:{date:yyyyMMdd}";

    // ------------------------------------------------------------------ sync entry points

    public async Task SyncStudentDayAsync(int studentId, DateOnly date, CancellationToken ct = default)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        await SyncStreaksAsync(studentId, days, ct);
        await SyncLessonsAsync(studentId, date, days, ct);
    }

    public async Task SyncLessonDayAsync(IReadOnlyCollection<int> studentIds, DateOnly date, CancellationToken ct = default)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        foreach (var id in studentIds.Distinct()) await SyncLessonsAsync(id, date, days, ct);
    }

    public async Task SyncLessonAsync(int sessionId, DateOnly date, CancellationToken ct = default)
    {
        var students = await db.SessionAttendance.Where(r => r.TimetableAssignmentId == sessionId && r.SessionDate == date)
            .Select(r => r.StudentId).Distinct().ToListAsync(ct);
        await SyncLessonDayAsync(students, date, ct);
    }

    public async Task<int> RecheckAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        var streakStudents = await db.StudentArrivals.Where(a => a.ArrivalDate >= from && a.ArrivalDate <= to && a.Status == ArrivalStatus.Absent)
            .Select(a => a.StudentId)
            .Union(db.AttendanceAlerts.Where(a => a.Type == AttendanceAlertType.ConsecutiveAbsence && a.LastDate >= from && a.FirstDate <= to).Select(a => a.StudentId))
            .Distinct().ToListAsync(ct);
        var lessonDays = await db.SessionAttendance.Where(r => r.SessionDate >= from && r.SessionDate <= to && r.Status == SessionAttendanceStatus.Absent)
            .Select(r => new { r.StudentId, Date = r.SessionDate })
            .Union(db.AttendanceAlerts.Where(a => a.Type == AttendanceAlertType.AbsentFromSession && a.FirstDate >= from && a.FirstDate <= to)
                .Select(a => new { a.StudentId, Date = a.FirstDate }))
            .Distinct().ToListAsync(ct);

        var changes = 0;
        foreach (var s in streakStudents) changes += await SyncStreaksAsync(s, days, ct);
        foreach (var x in lessonDays) changes += await SyncLessonsAsync(x.StudentId, x.Date, days, ct);
        return changes;
    }

    // ------------------------------------------------------------------ rule 1: consecutive school-day absences

    private sealed record Streak(int ClassId, List<DateOnly> Dates)
    {
        public DateOnly First => Dates[0];
        public DateOnly Last => Dates[^1];
    }

    private async Task<int> SyncStreaksAsync(int studentId, SchoolDays days, CancellationToken ct)
    {
        var records = await db.StudentArrivals.AsNoTracking().Where(a => a.StudentId == studentId).OrderBy(a => a.ArrivalDate)
            .Select(a => new { a.ArrivalDate, a.Status, a.SchoolClassId }).ToListAsync(ct);

        var streaks = new List<Streak>();
        Streak? current = null;
        foreach (var r in records)
        {
            if (!days.IsSchoolDay(r.ArrivalDate)) continue; // a record on a (later declared) holiday neither counts nor breaks
            if (r.Status != ArrivalStatus.Absent) { current = null; continue; }
            // Consecutive = the very next school day; an unrecorded school day in between ends the streak.
            if (current is not null && days.Next(current.Last) == r.ArrivalDate)
            {
                current.Dates.Add(r.ArrivalDate);
                current = current with { ClassId = r.SchoolClassId };
                streaks[^1] = current;
            }
            else
            {
                current = new Streak(r.SchoolClassId, [r.ArrivalDate]);
                streaks.Add(current);
            }
        }
        var wanted = streaks.Where(s => s.Dates.Count >= AlertLabels.ConsecutiveDays).ToDictionary(s => StreakKey(studentId, s.First));

        var existing = await db.AttendanceAlerts.Where(a => a.StudentId == studentId && a.Type == AttendanceAlertType.ConsecutiveAbsence).ToListAsync(ct);
        var changes = 0;
        foreach (var (key, s) in wanted)
        {
            var dates = DatesText(s.Dates);
            var summary = $"Absent from school on {s.Dates.Count} consecutive school days: {string.Join(", ", s.Dates.Select(AlertLabels.Day))}.";
            var alert = existing.FirstOrDefault(a => a.Key == key);
            if (alert is null)
            {
                alert = new AttendanceAlert
                {
                    Type = AttendanceAlertType.ConsecutiveAbsence, Key = key, StudentId = studentId, SchoolClassId = s.ClassId,
                    FirstDate = s.First, LastDate = s.Last, DayCount = s.Dates.Count, AbsenceDates = dates,
                };
                alert.Events.Add(Event(AttendanceAlertEventKind.Created, summary + " Contact the parent or guardian."));
                db.AttendanceAlerts.Add(alert);
                changes++;
                continue;
            }
            if (alert.AbsenceDates == dates && alert.Status != AttendanceAlertStatus.Cleared) continue;

            var longer = s.Last > alert.LastDate;
            (alert.SchoolClassId, alert.FirstDate, alert.LastDate, alert.DayCount, alert.AbsenceDates) = (s.ClassId, s.First, s.Last, s.Dates.Count, dates);
            if (alert.Status == AttendanceAlertStatus.Cleared || (alert.Status == AttendanceAlertStatus.Resolved && longer))
            {
                var why = alert.Status == AttendanceAlertStatus.Cleared ? "The records show the consecutive absences again." : "Still absent after the alert was resolved.";
                Reopen(alert);
                alert.Events.Add(Event(AttendanceAlertEventKind.Reopened, $"{why} {summary}"));
            }
            else alert.Events.Add(Event(AttendanceAlertEventKind.Updated, summary));
            changes++;
        }
        foreach (var alert in existing.Where(a => !wanted.ContainsKey(a.Key) && a.Status is AttendanceAlertStatus.Open or AttendanceAlertStatus.FollowUp))
        {
            alert.Status = AttendanceAlertStatus.Cleared;
            alert.Events.Add(Event(AttendanceAlertEventKind.Cleared,
                "Cleared: after a correction or a school-calendar change the records no longer show three consecutive school-day absences."));
            changes++;
        }
        return await SaveAsync(changes, ct);
    }

    // ------------------------------------------------------------------ rule 2: present at school, absent from a lesson

    private async Task<int> SyncLessonsAsync(int studentId, DateOnly date, SchoolDays days, CancellationToken ct)
    {
        var school = await db.StudentArrivals.AsNoTracking().Where(a => a.StudentId == studentId && a.ArrivalDate == date)
            .Select(a => new { a.Status, a.ArrivalTime, a.LateStatus }).FirstOrDefaultAsync(ct);
        var absent = await db.SessionAttendance.AsNoTracking()
            .Where(r => r.StudentId == studentId && r.SessionDate == date && r.Status == SessionAttendanceStatus.Absent)
            .Select(r => new { r.TimetableAssignmentId, r.SchoolClassId }).ToListAsync(ct);

        // Only when the school record says Present (not Absent, not missing), and only scheduled lessons that took place.
        var wanted = new Dictionary<string, (LessonView Lesson, int ClassId)>();
        if (school?.Status == ArrivalStatus.Present && absent.Count > 0)
        {
            var lessons = await Lessons.OnAsync(db, date, days, new Lessons.Scope(SessionIds: absent.Select(a => a.TimetableAssignmentId).ToList()), ct);
            foreach (var l in lessons.Where(l => l.IsExpected))
                wanted[LessonKey(studentId, l.SessionId, date)] = (l, absent.First(a => a.TimetableAssignmentId == l.SessionId).SchoolClassId);
        }

        var existing = await db.AttendanceAlerts
            .Where(a => a.StudentId == studentId && a.Type == AttendanceAlertType.AbsentFromSession && a.FirstDate == date).ToListAsync(ct);
        var changes = 0;
        foreach (var (key, (l, classId)) in wanted)
        {
            var arrived = school?.ArrivalTime is { } t ? $" (arrived {t.ToString("HH:mm", CultureInfo.InvariantCulture)}{(school.LateStatus == LateStatus.Late ? ", late" : "")})" : "";
            var summary = $"Present at school{arrived} but marked Absent in {l.Subject}, {l.Period} ({l.Times}) with {l.Teacher} on {AlertLabels.Day(date)}.";
            var alert = existing.FirstOrDefault(a => a.Key == key);
            if (alert is null)
            {
                alert = new AttendanceAlert
                {
                    Type = AttendanceAlertType.AbsentFromSession, Key = key, StudentId = studentId, SchoolClassId = classId,
                    FirstDate = date, LastDate = date, DayCount = 1, TimetableAssignmentId = l.SessionId,
                };
                alert.Events.Add(Event(AttendanceAlertEventKind.Created, summary));
                db.AttendanceAlerts.Add(alert);
                changes++;
            }
            else if (alert.Status == AttendanceAlertStatus.Cleared)
            {
                Reopen(alert);
                alert.Events.Add(Event(AttendanceAlertEventKind.Reopened, "The records match again. " + summary));
                changes++;
            }
        }
        foreach (var alert in existing.Where(a => !wanted.ContainsKey(a.Key) && a.Status is AttendanceAlertStatus.Open or AttendanceAlertStatus.FollowUp))
        {
            alert.Status = AttendanceAlertStatus.Cleared;
            alert.Events.Add(Event(AttendanceAlertEventKind.Cleared, school?.Status switch
            {
                null => "Cleared: the school attendance for this day is no longer recorded.",
                ArrivalStatus.Absent => "Cleared: the school attendance was corrected to Absent.",
                _ => "Cleared: the lesson attendance was corrected, or the lesson was cancelled.",
            }));
            changes++;
        }
        return await SaveAsync(changes, ct);
    }

    // ------------------------------------------------------------------ follow-up

    public async Task<ServiceResult> FollowUpAsync(int id, string? note, CancellationToken ct = default)
    {
        var alert = await db.AttendanceAlerts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null) return ServiceResult.Fail("", "This alert no longer exists.");
        if (alert.Status is AttendanceAlertStatus.Resolved or AttendanceAlertStatus.Cleared)
            return ServiceResult.Fail("", "This alert is closed. Reopen it to add a follow-up.");
        var text = Note(note);
        if (text is null) return ServiceResult.Fail("Note", "Describe the follow-up (e.g. who was contacted and what they said).");
        if (text.Length > AttendanceAlertEvent.TextMaxLength) return ServiceResult.Fail("Note", $"Use {AttendanceAlertEvent.TextMaxLength} characters or fewer.");
        alert.Status = AttendanceAlertStatus.FollowUp;
        db.AttendanceAlertEvents.Add(Event(AttendanceAlertEventKind.FollowUp, text, alert));
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> ResolveAsync(int id, string? note, CancellationToken ct = default)
    {
        var alert = await db.AttendanceAlerts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null) return ServiceResult.Fail("", "This alert no longer exists.");
        if (alert.Status is AttendanceAlertStatus.Resolved or AttendanceAlertStatus.Cleared) return ServiceResult.Fail("", "This alert is already closed.");
        var text = Note(note);
        if (text is null) return ServiceResult.Fail("Note", "Explain how the alert was resolved.");
        if (text.Length > AttendanceAlert.ResolutionMaxLength) return ServiceResult.Fail("Note", $"Use {AttendanceAlert.ResolutionMaxLength} characters or fewer.");
        (alert.Status, alert.Resolution, alert.ResolvedAt, alert.ResolvedBy) = (AttendanceAlertStatus.Resolved, text, DateTime.UtcNow, currentUser.Name);
        db.AttendanceAlertEvents.Add(Event(AttendanceAlertEventKind.Resolved, text, alert));
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> ReopenAsync(int id, string? note, CancellationToken ct = default)
    {
        var alert = await db.AttendanceAlerts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null) return ServiceResult.Fail("", "This alert no longer exists.");
        if (alert.Status is AttendanceAlertStatus.Open or AttendanceAlertStatus.FollowUp) return ServiceResult.Fail("", "This alert is already open.");
        var text = Note(note);
        if (text?.Length > AttendanceAlertEvent.TextMaxLength) return ServiceResult.Fail("Note", $"Use {AttendanceAlertEvent.TextMaxLength} characters or fewer.");
        Reopen(alert);
        db.AttendanceAlertEvents.Add(Event(AttendanceAlertEventKind.Reopened, text ?? "Reopened by staff.", alert));
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ------------------------------------------------------------------ reading

    public async Task<IReadOnlyList<AlertRow>> ListAsync(AlertFilter filter, CancellationToken ct = default)
    {
        var q = db.AttendanceAlerts.AsNoTracking();
        if (filter.Type is { } t) q = q.Where(a => a.Type == t);
        q = filter.State switch
        {
            "resolved" => q.Where(a => a.Status == AttendanceAlertStatus.Resolved || a.Status == AttendanceAlertStatus.Cleared),
            "all" => q,
            _ => q.Where(a => a.Status == AttendanceAlertStatus.Open || a.Status == AttendanceAlertStatus.FollowUp),
        };
        if (filter.ClassId is { } c) q = q.Where(a => a.SchoolClassId == c);
        var term = TextNormalizer.NameKey(filter.Search);
        if (term.Length > 0) q = q.Where(a => a.Student.NormalizedName.Contains(term));
        return await ProjectAsync(q.OrderBy(a => a.Status == AttendanceAlertStatus.Open ? 0 : a.Status == AttendanceAlertStatus.FollowUp ? 1 : 2)
            .ThenByDescending(a => a.LastDate).ThenByDescending(a => a.UpdatedAt).Take(500), ct);
    }

    public async Task<AlertCounts> CountsAsync(CancellationToken ct = default)
    {
        var groups = await db.AttendanceAlerts.AsNoTracking().GroupBy(a => new { a.Type, a.Status })
            .Select(g => new { g.Key.Type, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        int Sum(Func<AttendanceAlertType, AttendanceAlertStatus, bool> f) => groups.Where(g => f(g.Type, g.Status)).Sum(g => g.Count);
        bool Pending(AttendanceAlertStatus s) => s is AttendanceAlertStatus.Open or AttendanceAlertStatus.FollowUp;
        return new AlertCounts(
            Sum((t, s) => t == AttendanceAlertType.ConsecutiveAbsence && Pending(s)),
            Sum((t, s) => t == AttendanceAlertType.AbsentFromSession && Pending(s)),
            Sum((_, s) => s == AttendanceAlertStatus.FollowUp),
            Sum((_, s) => s == AttendanceAlertStatus.Resolved),
            Sum((_, s) => s == AttendanceAlertStatus.Cleared));
    }

    public async Task<AlertDetails?> GetAsync(int id, CancellationToken ct = default)
    {
        var row = (await ProjectAsync(db.AttendanceAlerts.AsNoTracking().Where(a => a.Id == id), ct)).FirstOrDefault();
        if (row is null) return null;
        var events = await db.AttendanceAlertEvents.AsNoTracking().Where(e => e.AttendanceAlertId == id).OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Select(e => new AlertEventRow(e.Kind, e.Text, e.At, e.By)).ToListAsync(ct);
        return new AlertDetails(row, events);
    }

    private async Task<List<AlertRow>> ProjectAsync(IQueryable<AttendanceAlert> q, CancellationToken ct)
    {
        var rows = await q.Select(a => new
        {
            a.Id, a.Type, a.Status, a.StudentId, Student = a.Student.FullName, a.SchoolClassId, ClassName = a.SchoolClass.Name,
            a.FirstDate, a.LastDate, a.DayCount, a.AbsenceDates, a.TimetableAssignmentId,
            Subject = a.Session != null ? a.Session.Subject : null,
            LessonClassId = a.Session != null ? (int?)a.Session.SchoolClassId : null,
            Day = a.Session != null ? (DayOfWeek?)a.Session.Day : null,
            SlotId = a.Session != null ? (int?)a.Session.TimeSlotId : null,
            SlotNumber = a.Session != null ? (int?)a.Session.TimeSlot.Number : null,
            SlotName = a.Session != null ? a.Session.TimeSlot.Name : null,
            Start = a.Session != null ? a.Session.TimeSlot.StartTime : null,
            End = a.Session != null ? a.Session.TimeSlot.EndTime : null,
            Teacher = a.Session != null ? a.Session.Teacher.FullName : null,
            a.CreatedAt, a.UpdatedAt, a.ResolvedBy, a.ResolvedAt, a.Resolution,
        }).ToListAsync(ct);

        // Substitute teachers for the lesson alerts (placed in that class's slot for that week).
        var weeks = rows.Where(r => r.TimetableAssignmentId is not null).Select(r => SchoolWeek.StartOf(r.FirstDate)).Distinct().ToList();
        var subs = weeks.Count == 0 ? [] : await db.WorkloadAssignments.AsNoTracking()
            .Where(w => w.Category == WorkloadCategory.Substitution && weeks.Contains(w.WeekStart) && w.SchoolClassId != null && w.TimeSlotId != null)
            .Select(w => new { w.WeekStart, w.Day, ClassId = w.SchoolClassId!.Value, SlotId = w.TimeSlotId!.Value, w.Teacher.FullName })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var sub = r.TimetableAssignmentId is null ? null
                : subs.FirstOrDefault(s => s.WeekStart == SchoolWeek.StartOf(r.FirstDate) && s.Day == r.Day && s.ClassId == r.LessonClassId && s.SlotId == r.SlotId)?.FullName;
            var dates = (r.AbsenceDates ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
            if (dates.Count == 0) dates = [r.FirstDate];
            return new AlertRow(r.Id, r.Type, r.Status, r.StudentId, r.Student, r.SchoolClassId, r.ClassName, r.FirstDate, r.LastDate, r.DayCount, dates,
                r.TimetableAssignmentId, r.Subject, r.SlotNumber, r.SlotName, r.Start, r.End, r.Teacher, sub,
                r.CreatedAt, r.UpdatedAt, r.ResolvedBy, r.ResolvedAt, r.Resolution);
        }).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private AttendanceAlertEvent Event(AttendanceAlertEventKind kind, string text, AttendanceAlert? alert = null)
    {
        var e = new AttendanceAlertEvent { Kind = kind, Text = text.Length > AttendanceAlertEvent.TextMaxLength ? text[..AttendanceAlertEvent.TextMaxLength] : text, At = DateTime.UtcNow, By = currentUser.Name };
        if (alert is not null) e.AttendanceAlertId = alert.Id;
        return e;
    }

    private static void Reopen(AttendanceAlert alert) =>
        (alert.Status, alert.ResolvedAt, alert.ResolvedBy, alert.Resolution) = (AttendanceAlertStatus.Open, null, null, null);

    private static string? Note(string? s)
    {
        var t = TextNormalizer.CleanText(s);
        return t.Length == 0 ? null : t;
    }

    private static string DatesText(IEnumerable<DateOnly> dates)
    {
        var text = string.Join(",", dates.Select(SchoolWeek.Iso));
        if (text.Length <= AttendanceAlert.DatesMaxLength) return text;
        return text[..text.LastIndexOf(',', AttendanceAlert.DatesMaxLength)];
    }

    /// <summary>Saves; a concurrent sync that created the same alert first wins (the unique key prevents a duplicate).</summary>
    private async Task<int> SaveAsync(int changes, CancellationToken ct)
    {
        if (changes == 0) return 0;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
        }
        return changes;
    }
}

/// <summary>
/// Raises alerts for records that existed before alerts were introduced (and for days whose lessons have
/// since started): re-checks the last 120 days shortly after start-up and then every few hours.
/// Disabled with Attendance:AlertSweep = false (the tests do, to keep their data their own).
/// </summary>
public sealed class AttendanceAlertSweep(IServiceScopeFactory scopes, IConfiguration config, ILogger<AttendanceAlertSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Attendance:AlertSweep", true)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var alerts = scope.ServiceProvider.GetRequiredService<IAttendanceAlertService>();
                var changes = await alerts.RecheckAsync(SchoolWeek.Today.AddDays(-120), SchoolWeek.Today, stoppingToken);
                if (changes > 0) logger.LogInformation("Attendance alert sweep: {Changes} alert change(s).", changes);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { logger.LogError(ex, "Attendance alert sweep failed."); }
            try { await Task.Delay(TimeSpan.FromHours(3), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
