using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>
/// One interval when a teacher is not free on a date. <see cref="PtcConflictKind.Lesson"/> intervals are the teacher's own
/// timetable lessons: whether one blocks depends on the PTC substitute cover for the exact overlap (see
/// <see cref="PtcTeacherAvailability.Blocking"/>). <see cref="Planned"/> marks an interval reserved by a schedule plan that
/// is not saved yet.
/// </summary>
public sealed record PtcBusyInterval(int TeacherId, DateOnly Date, TimeOnly Start, TimeOnly End, PtcConflictKind Kind, string Description,
    int? BookingId = null, int? ScheduleId = null, int? SubstituteId = null, bool Planned = false, int? LessonId = null);

/// <summary>
/// An active PTC substitute assignment as the availability sees it: the original teacher's cover on a date, for one lesson
/// (<see cref="LessonId"/>) or a free time range, and why it does not count now (<see cref="Problems"/>, empty when valid):
/// the substitute is no longer current staff, or is busy then (a lesson, interview, duty, other cover or period).
/// </summary>
public sealed record PtcCover(int Id, int ScheduleId, int OriginalId, string OriginalName, int SubstituteId, string SubstituteName, DateOnly Date,
    TimeOnly Start, TimeOnly End, int? LessonId, IReadOnlyList<string> Problems)
{
    public bool Valid => Problems.Count == 0;
}

/// <summary>
/// A busy interval that overlaps a requested window, and whether it blocks. A lesson does not block when a valid PTC
/// substitute (<see cref="Cover"/>) covers the overlap; <see cref="InvalidCover"/> is an assignment for it that no longer
/// counts, with <see cref="InvalidReason"/>.
/// </summary>
public sealed record PtcBusyHit(PtcBusyInterval Interval, bool Covered, PtcCover? Cover = null, PtcCover? InvalidCover = null, string? InvalidReason = null)
{
    public bool Blocks => !Covered;

    /// <summary>Why the teacher is busy (a lesson says whether a PTC substitute covers it).</summary>
    public string Message => Interval.Kind != PtcConflictKind.Lesson ? Interval.Description
        : Covered ? Interval.Description + $" (covered by a PTC substitute{(Cover is null ? "" : ": " + Cover.SubstituteName)})."
        : InvalidCover is not null ? Interval.Description + $": its PTC substitute ({InvalidCover.SubstituteName}) no longer counts ({InvalidReason}), so no PTC substitute covers it."
        : Interval.Description + " and no PTC substitute covers it.";
}

/// <summary>
/// Teacher availability across the whole PTC picture: for a set of teachers and dates, everything that keeps them busy —
/// PTC interviews of active bookings (any class, any event), PTC substitute cover they give, their own timetable lessons
/// (blocking unless an active PTC substitute covers the overlap), dated duties (primary, or accepted backup) and single-week
/// periods placed in a slot (substitutions, office hours). Loaded in a few queries, then queried in memory; the same teacher
/// on another date is never busy. Ranges overlap when NewStart &lt; ExistingEnd and NewEnd &gt; ExistingStart: an interval
/// ending exactly when another starts does not overlap.
/// </summary>
public sealed class PtcTeacherAvailability
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly Dictionary<(int, DateOnly), List<PtcBusyInterval>> _busy = [];
    private readonly Dictionary<(int, DateOnly), List<PtcCover>> _covers = [];

    /// <summary>Lessons left out because they are cancelled on that date (Attendance → session cancellations).</summary>
    public int CancelledLessons { get; private set; }

    /// <summary>Every active cover of the loaded teachers' lessons on the loaded dates (valid or not).</summary>
    public IEnumerable<PtcCover> Covers => _covers.Values.SelectMany(c => c);

    public IReadOnlyDictionary<int, string> Names { get; private init; } = new Dictionary<int, string>();

    public string Name(int teacherId) => Names.TryGetValue(teacherId, out var n) ? n : "The teacher";

    /// <summary>Everything recorded for the teacher on the date (including covered lessons), in time order.</summary>
    public IReadOnlyList<PtcBusyInterval> Busy(int teacherId, DateOnly date) =>
        _busy.TryGetValue((teacherId, date), out var list) ? list.OrderBy(b => b.Start).ToList() : [];

    /// <summary>
    /// What overlaps <paramref name="start"/>–<paramref name="end"/> for the teacher on the date; <paramref name="ignore"/>
    /// leaves out intervals (e.g. the same booking's own interviews). Lessons come back with whether a cover spans the overlap.
    /// </summary>
    public IReadOnlyList<PtcBusyHit> Blocking(int teacherId, DateOnly date, TimeOnly start, TimeOnly end, Func<PtcBusyInterval, bool>? ignore = null)
    {
        if (!_busy.TryGetValue((teacherId, date), out var list)) return [];
        var hits = new List<PtcBusyHit>();
        foreach (var b in list)
        {
            if (!PtcRules.Overlaps(start, end, b.Start, b.End) || (ignore?.Invoke(b) ?? false)) continue;
            if (b.Kind != PtcConflictKind.Lesson || !_covers.TryGetValue((teacherId, date), out var covers))
            {
                hits.Add(new PtcBusyHit(b, false));
                continue;
            }
            var from = b.Start > start ? b.Start : start;
            var to = b.End < end ? b.End : end;
            // A cover counts for this lesson when it is for this lesson (or a free range spanning the overlap) and is valid.
            var mine = covers.Where(c => c.LessonId is null ? c.Start <= from && c.End >= to : c.LessonId == b.LessonId).ToList();
            var valid = mine.FirstOrDefault(c => c.Valid && c.Start <= from && c.End >= to);
            if (valid is not null)
            {
                hits.Add(new PtcBusyHit(b, true, valid));
                continue;
            }
            var invalid = mine.FirstOrDefault();
            var why = invalid is null ? null
                : !invalid.Valid ? string.Join(" ", invalid.Problems)
                : $"the lesson is now {ScheduleRules.Times(b.Start, b.End)} but the cover is {ScheduleRules.Times(invalid.Start, invalid.End)}";
            hits.Add(new PtcBusyHit(b, false, null, invalid, why));
        }
        return hits;
    }

    /// <summary>All the teachers are free together in the window (nothing blocking).</summary>
    public bool AllFree(IEnumerable<int> teacherIds, DateOnly date, TimeOnly start, TimeOnly end, Func<PtcBusyInterval, bool>? ignore = null) =>
        teacherIds.All(t => !Blocking(t, date, start, end, ignore).Any(h => h.Blocks));

    /// <summary>The first blocking reason among the teachers in the window, or null.</summary>
    public PtcBusyHit? FirstBlock(IEnumerable<int> teacherIds, DateOnly date, TimeOnly start, TimeOnly end, Func<PtcBusyInterval, bool>? ignore = null) =>
        teacherIds.SelectMany(t => Blocking(t, date, start, end, ignore)).Where(h => h.Blocks).OrderBy(h => h.Interval.Start).FirstOrDefault();

    /// <summary>Reserves an interval in memory (a schedule plan being built); never saved by this class.</summary>
    public void Reserve(PtcBusyInterval interval)
    {
        if (!_busy.TryGetValue((interval.TeacherId, interval.Date), out var list)) _busy[(interval.TeacherId, interval.Date)] = list = [];
        list.Add(interval with { Planned = true });
    }

    private void Add(PtcBusyInterval b)
    {
        if (!_busy.TryGetValue((b.TeacherId, b.Date), out var list)) _busy[(b.TeacherId, b.Date)] = list = [];
        list.Add(b);
    }

    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", C);

    /// <summary>Loads the teachers' busy intervals on the dates (a handful of queries, whatever the number of teachers).</summary>
    public static Task<PtcTeacherAvailability> LoadAsync(SchoolDbContext db, IReadOnlyCollection<int> teacherIds, IReadOnlyCollection<DateOnly> dates, CancellationToken ct) =>
        LoadAsync(db, teacherIds, dates, true, ct);

    /// <param name="checkCovers">
    /// Also check each cover of the teachers' lessons: the substitute must be current staff and free for the whole cover
    /// (the substitutes' own commitments are loaded once more, without this check, so it never recurses).
    /// </param>
    private static async Task<PtcTeacherAvailability> LoadAsync(SchoolDbContext db, IReadOnlyCollection<int> teacherIds, IReadOnlyCollection<DateOnly> dates,
        bool checkCovers, CancellationToken ct)
    {
        var ids = teacherIds.Distinct().ToList();
        var days = dates.Distinct().ToList();
        var names = await db.StaffMembers.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FullName, ct);
        var map = new PtcTeacherAvailability { Names = names };
        if (ids.Count == 0 || days.Count == 0) return map;
        string N(int id) => names.TryGetValue(id, out var n) ? n : "The teacher";

        // 1. PTC interviews of active bookings (every class and event): a cancelled booking never holds a teacher.
        var interviews = await db.PtcTeacherInterviews.AsNoTracking()
            .Where(i => ids.Contains(i.TeacherStaffMemberId) && days.Contains(i.Date) && i.Status != PtcInterviewStatus.Cancelled
                        && i.Booking.Status != PtcBookingStatus.Cancelled)
            .Select(i => new { i.TeacherStaffMemberId, i.Date, i.ScheduledStart, i.ScheduledEnd, i.PtcParentBookingId, i.Booking.PtcClassScheduleId,
                ClassName = i.Booking.ClassSchedule.SchoolClass.Name })
            .ToListAsync(ct);
        foreach (var i in interviews)
            map.Add(new PtcBusyInterval(i.TeacherStaffMemberId, i.Date, i.ScheduledStart, i.ScheduledEnd, PtcConflictKind.Interview,
                $"{N(i.TeacherStaffMemberId)} already has a PTC interview ({i.ClassName}) on {Day(i.Date)}, {ScheduleRules.Times(i.ScheduledStart, i.ScheduledEnd)}.",
                BookingId: i.PtcParentBookingId, ScheduleId: i.PtcClassScheduleId));

        // 2. PTC substitute assignments on those dates: cover the teacher gives (busy), and cover for the teacher's lessons.
        var covers = await db.PtcSubstituteAssignments.AsNoTracking()
            .Where(a => a.Status == PtcSubstituteStatus.Active && days.Contains(a.ClassSchedule.EventDate) && a.ClassSchedule.Status != PtcScheduleStatus.Cancelled
                        && (ids.Contains(a.SubstituteTeacherStaffMemberId) || ids.Contains(a.OriginalTeacherStaffMemberId)))
            .Select(a => new
            {
                a.Id, a.PtcClassScheduleId, a.ClassSchedule.EventDate, a.StartTime, a.EndTime, a.SubstituteTeacherStaffMemberId, a.OriginalTeacherStaffMemberId,
                Original = a.OriginalTeacher.FullName, Substitute = a.SubstituteTeacher.FullName, SubstituteArchived = a.SubstituteTeacher.ArchivedAt != null,
                a.TimetableAssignmentId,
            })
            .ToListAsync(ct);
        var forLessons = covers.Where(a => ids.Contains(a.OriginalTeacherStaffMemberId)).ToList();
        var subs = checkCovers && forLessons.Count > 0
            ? await LoadAsync(db, forLessons.Select(a => a.SubstituteTeacherStaffMemberId).Distinct().ToList(), forLessons.Select(a => a.EventDate).Distinct().ToList(), false, ct)
            : null;
        foreach (var a in covers)
        {
            if (ids.Contains(a.SubstituteTeacherStaffMemberId))
                map.Add(new PtcBusyInterval(a.SubstituteTeacherStaffMemberId, a.EventDate, a.StartTime, a.EndTime, PtcConflictKind.SubstituteCover,
                    $"{N(a.SubstituteTeacherStaffMemberId)} is the PTC substitute for {a.Original} on {Day(a.EventDate)}, {ScheduleRules.Times(a.StartTime, a.EndTime)}.",
                    SubstituteId: a.Id));
            if (ids.Contains(a.OriginalTeacherStaffMemberId))
            {
                var problems = new List<string>();
                if (a.SubstituteArchived) problems.Add($"{a.Substitute} is no longer current staff.");
                if (subs is not null)
                    problems.AddRange(subs.Blocking(a.SubstituteTeacherStaffMemberId, a.EventDate, a.StartTime, a.EndTime, b => b.SubstituteId == a.Id)
                        .Where(h => h.Blocks).Select(h => h.Message).Distinct());
                var key = (a.OriginalTeacherStaffMemberId, a.EventDate);
                if (!map._covers.TryGetValue(key, out var list)) map._covers[key] = list = [];
                list.Add(new PtcCover(a.Id, a.PtcClassScheduleId, a.OriginalTeacherStaffMemberId, a.Original, a.SubstituteTeacherStaffMemberId, a.Substitute,
                    a.EventDate, a.StartTime, a.EndTime, a.TimetableAssignmentId, problems));
            }
        }

        // 3. Timetable lessons on those weekdays (periods with times), current in each date's school week.
        var weekdays = days.Where(d => ScheduleRules.IsSchoolDay(d.DayOfWeek)).Select(d => d.DayOfWeek).Distinct().ToList();
        if (weekdays.Count > 0)
        {
            var lessons = await db.TimetableAssignments.AsNoTracking()
                .Where(t => ids.Contains(t.TeacherId) && weekdays.Contains(t.Day) && t.TimeSlot.StartTime != null && t.TimeSlot.EndTime != null)
                .Select(t => new { t.Id, t.TeacherId, t.Day, t.StartsOn, t.EndedOn, Start = t.TimeSlot.StartTime!.Value, End = t.TimeSlot.EndTime!.Value, ClassName = t.SchoolClass.Name, t.Subject })
                .ToListAsync(ct);
            // A lesson cancelled on that date (Attendance → session cancellations) does not keep the teacher busy.
            var lessonIds = lessons.Select(l => l.Id).ToList();
            var cancelled = lessonIds.Count == 0 ? [] : (await db.SessionCancellations.AsNoTracking()
                .Where(x => lessonIds.Contains(x.TimetableAssignmentId) && days.Contains(x.SessionDate))
                .Select(x => new { x.TimetableAssignmentId, x.SessionDate }).ToListAsync(ct))
                .Select(x => (x.TimetableAssignmentId, x.SessionDate)).ToHashSet();
            foreach (var d in days)
            {
                var week = SchoolWeek.StartOf(d);
                foreach (var l in lessons.Where(l => l.Day == d.DayOfWeek && l.StartsOn <= week && (l.EndedOn == null || l.EndedOn > week)))
                {
                    if (cancelled.Contains((l.Id, d)))
                    {
                        map.CancelledLessons++;
                        continue;
                    }
                    map.Add(new PtcBusyInterval(l.TeacherId, d, l.Start, l.End, PtcConflictKind.Lesson,
                        $"{N(l.TeacherId)} teaches {l.ClassName} ({l.Subject}) on {Day(d)}, {ScheduleRules.Times(l.Start, l.End)}", LessonId: l.Id));
                }
            }
        }

        // 4. Dated duties (same rule as the timetable's own conflict check).
        var duties = await db.DutyAssignments.AsNoTracking()
            .Where(d => days.Contains(d.DutyDate) && (ids.Contains(d.PrimaryStaffId) || (d.BackupStaffId != null && ids.Contains(d.BackupStaffId.Value) && d.BackupStatus == BackupRequestStatus.Accepted)))
            .Select(d => new { d.DutyDate, d.Component, d.StartTime, d.EndTime, d.PrimaryStaffId, d.BackupStaffId, d.BackupStatus })
            .ToListAsync(ct);
        foreach (var d in duties)
            foreach (var t in new[] { d.PrimaryStaffId, d.BackupStatus == BackupRequestStatus.Accepted ? d.BackupStaffId ?? 0 : 0 }.Where(ids.Contains).Distinct())
                map.Add(new PtcBusyInterval(t, d.DutyDate, d.StartTime, d.EndTime, PtcConflictKind.Duty,
                    $"{N(t)} has {DutyComponents.Name(d.Component)} duty on {Day(d.DutyDate)}, {ScheduleRules.Times(d.StartTime, d.EndTime)}."));

        // 5. Single-week workload periods placed in a slot (substitutions, office hours) that week and weekday.
        var weeks = days.Select(SchoolWeek.StartOf).Distinct().ToList();
        var periods = await db.WorkloadAssignments.AsNoTracking()
            .Where(w => ids.Contains(w.TeacherId) && weeks.Contains(w.WeekStart) && w.Day != null && w.TimeSlot != null && w.TimeSlot.StartTime != null && w.TimeSlot.EndTime != null)
            .Select(w => new { w.TeacherId, w.WeekStart, w.Day, w.Category, Start = w.TimeSlot!.StartTime!.Value, End = w.TimeSlot.EndTime!.Value })
            .ToListAsync(ct);
        foreach (var d in days)
            foreach (var p in periods.Where(p => p.WeekStart == SchoolWeek.StartOf(d) && p.Day == d.DayOfWeek))
                map.Add(new PtcBusyInterval(p.TeacherId, d, p.Start, p.End, PtcConflictKind.WorkloadPeriod,
                    $"{N(p.TeacherId)} has {(p.Category == WorkloadCategory.Substitution ? "a substitution" : "a scheduled period")} on {Day(d)}, {ScheduleRules.Times(p.Start, p.End)}."));
        return map;
    }
}

/// <summary>A parent slot in a Round: its number and planned time.</summary>
public sealed record PtcSlotCandidate(int Slot, TimeOnly Start, TimeOnly End);

/// <summary>
/// Interview slot allocation for one parent booking (deterministic greedy). The parent keeps the chosen Round; the
/// candidates are its free slots (1 … Capacity, not held by an active booking), in time order; the first slot where every
/// chosen teacher is free at the same time is taken, so all the parent's interviews run in parallel in that slot. Earliest
/// first also packs teachers' interviews together. Never exceeds the capacity and never creates a teacher overlap; when no
/// slot suits every teacher the booking is refused (nothing is partly allocated).
/// </summary>
public static class PtcSlotAllocator
{
    /// <summary>The Round's slots in time order (only those that end within the Round).</summary>
    public static IEnumerable<PtcSlotCandidate> Slots(TimeOnly roundStart, TimeOnly roundEnd, int capacity, int meetingMinutes)
    {
        for (var n = 1; n <= capacity; n++)
        {
            var (s, e) = PtcRules.SlotWindow(roundStart, n, meetingMinutes);
            if (e > roundEnd) yield break;
            yield return new PtcSlotCandidate(n, s, e);
        }
    }

    /// <summary>
    /// The earliest slot not in <paramref name="taken"/> where all <paramref name="teacherIds"/> are free on
    /// <paramref name="date"/>, or null (with the first reason a slot was refused: null when every slot is taken).
    /// </summary>
    public static (PtcSlotCandidate? Slot, PtcBusyHit? Reason) EarliestCommonSlot(TimeOnly roundStart, TimeOnly roundEnd, int capacity, int meetingMinutes,
        IReadOnlySet<int> taken, IReadOnlyCollection<int> teacherIds, DateOnly date, PtcTeacherAvailability availability, Func<PtcBusyInterval, bool>? ignore = null)
    {
        PtcBusyHit? reason = null;
        foreach (var slot in Slots(roundStart, roundEnd, capacity, meetingMinutes).Where(s => !taken.Contains(s.Slot)))
        {
            var block = availability.FirstBlock(teacherIds, date, slot.Start, slot.End, ignore);
            if (block is null) return (slot, null);
            reason ??= block;
        }
        return (null, reason);
    }
}
