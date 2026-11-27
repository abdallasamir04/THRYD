using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

// ============================================================ PTC Phase 6: timetable integration & class coverage
//
// A participating teacher (a class's subject teacher from the timetable, every class alike, KG included) who has their
// own timetable lesson during a Round of a class they meet parents for must have that lesson covered by a substitute the
// Super Admin chooses. Everything here is computed from the live data on every read (timetable, Rounds, substitutes,
// duties, interviews): no coverage status is stored, so a change anywhere is reflected the next time anything is read,
// including immediately before publishing. The availability itself is PtcTeacherAvailability, the same engine the
// booking allocation and the scheduling analysis use. Nothing is ever assigned, replaced or approved automatically.

/// <summary>The coverage state of one conflict, a teacher or a whole event.</summary>
public enum PtcCoverageStatus
{
    /// <summary>Nothing to cover (no lesson during the PTC).</summary>
    NoConflict = 0,

    /// <summary>A valid substitute covers the lesson.</summary>
    Covered = 1,

    /// <summary>No substitute: blocks publishing.</summary>
    Uncovered = 2,

    /// <summary>Something cannot be checked or an assignment needs a decision (does not block on its own).</summary>
    NeedsReview = 3,

    /// <summary>A substitute is assigned but no longer counts (busy, not current staff, or the lesson changed): blocks publishing.</summary>
    Invalid = 4,
}

/// <summary>What one participating schedule brings to the coverage check.</summary>
public sealed record PtcCoverageInput(int ScheduleId, string ClassName, DateOnly Date, IReadOnlyList<PtcRoundRow> Rounds, IReadOnlyList<(int Id, string Name)> Teachers);

/// <summary>
/// One teacher's timetable lesson that overlaps a Round of a class they meet parents for (one row per Round it overlaps).
/// <see cref="Cover"/> is the substitute assignment that covers it (Covered) or that no longer counts (Invalid).
/// </summary>
public sealed record PtcCoverageConflict(int ScheduleId, string PtcClassName, DateOnly Date, int RoundId, int RoundNumber, TimeOnly RoundStart, TimeOnly RoundEnd,
    int TeacherId, string TeacherName, int LessonId, int LessonClassId, string LessonClassName, int DepartmentId, string Department, string Subject, int? Period,
    TimeOnly LessonStart, TimeOnly LessonEnd, PtcCoverageStatus Status, PtcCover? Cover, string? InvalidReason)
{
    public TimeOnly OverlapStart => LessonStart > RoundStart ? LessonStart : RoundStart;
    public TimeOnly OverlapEnd => LessonEnd < RoundEnd ? LessonEnd : RoundEnd;
    public bool Blocks => Status is PtcCoverageStatus.Uncovered or PtcCoverageStatus.Invalid;

    /// <summary>Why the teacher is unavailable, in words (and what covers it).</summary>
    public string Explanation =>
        $"{TeacherName} has a {Subject} lesson with {LessonClassName} from {LessonStart:HH\\:mm} to {LessonEnd:HH\\:mm} during {PtcClassName}'s Round {RoundNumber} ({ScheduleRules.Times(RoundStart, RoundEnd)})."
        + Status switch
        {
            PtcCoverageStatus.Covered => $" Covered by {Cover!.SubstituteName}.",
            PtcCoverageStatus.Invalid => $" The assigned substitute ({Cover!.SubstituteName}) no longer counts: {InvalidReason}",
            _ => " No valid substitute has been assigned.",
        };
}

/// <summary>Another commitment of a participating teacher during a Round (the scheduling engine avoids it; no substitute needed).</summary>
public sealed record PtcCoverageOther(int ScheduleId, string PtcClassName, DateOnly Date, int RoundNumber, TimeOnly RoundStart, TimeOnly RoundEnd, int TeacherId,
    string TeacherName, PtcConflictKind Kind, TimeOnly Start, TimeOnly End, string Explanation);

/// <summary>A lesson that cannot be checked because its period has no times.</summary>
public sealed record PtcCoverageUntimed(int TeacherId, string TeacherName, DateOnly Date, string ClassName, string Subject, int Period);

/// <summary>An active substitute assignment of the event and its state now.</summary>
public sealed record PtcCoverageAssignment(int Id, int ScheduleId, string PtcClassName, DateOnly Date, int OriginalId, string OriginalName, int SubstituteId,
    string SubstituteName, TimeOnly Start, TimeOnly End, int? LessonId, string? LessonClassName, string? Subject, string? Reason, string? Notes, string? AssignedBy,
    DateTime AssignedAt, PtcCoverageStatus Status, string StatusDetail);

/// <summary>A participating teacher and their worst coverage state.</summary>
public sealed record PtcCoverageTeacher(int Id, string Name, int Conflicts, PtcCoverageStatus Status);

/// <summary>The whole event's class coverage, computed now.</summary>
public sealed class PtcCoverageReport
{
    public required int EventId { get; init; }

    /// <summary>Timetable lessons current in the event's weeks (school-wide). Zero: coverage cannot be verified.</summary>
    public required int TimetableLessons { get; init; }
    public bool TimetableEmpty => TimetableLessons == 0;
    public bool HasSchedules { get; init; }
    public int CancelledLessons { get; init; }

    public required IReadOnlyList<PtcCoverageTeacher> Teachers { get; init; }
    public required IReadOnlyList<PtcCoverageConflict> Conflicts { get; init; }
    public required IReadOnlyList<PtcCoverageOther> Others { get; init; }
    public required IReadOnlyList<PtcCoverageUntimed> Untimed { get; init; }
    public required IReadOnlyList<PtcCoverageAssignment> Assignments { get; init; }

    /// <summary>Distinct lessons (lesson, date) that overlap the PTC.</summary>
    public int OverlappingLessons => Lessons.Count();
    private IEnumerable<IGrouping<(int, DateOnly), PtcCoverageConflict>> Lessons => Conflicts.GroupBy(c => (c.LessonId, c.Date));
    public int ConflictCount => OverlappingLessons;
    public int Covered => Lessons.Count(g => g.All(c => c.Status == PtcCoverageStatus.Covered));
    public int Uncovered => Lessons.Count(g => g.Any(c => c.Status == PtcCoverageStatus.Uncovered));
    public int Invalid => Lessons.Count(g => g.Any(c => c.Status == PtcCoverageStatus.Invalid) && g.All(c => c.Status != PtcCoverageStatus.Uncovered));
    public int Unresolved => Uncovered + Invalid;

    /// <summary>Assignments that no longer match a lesson in conflict (nothing to cover now): review and remove.</summary>
    public IReadOnlyList<PtcCoverageAssignment> Pending => Assignments.Where(a => a.Status == PtcCoverageStatus.NeedsReview).ToList();

    public PtcCoverageStatus Overall =>
        Uncovered > 0 ? PtcCoverageStatus.Uncovered
        : Invalid > 0 ? PtcCoverageStatus.Invalid
        : (HasSchedules && TimetableEmpty) || Untimed.Count > 0 || Pending.Count > 0 ? PtcCoverageStatus.NeedsReview
        : Conflicts.Count > 0 ? PtcCoverageStatus.Covered
        : PtcCoverageStatus.NoConflict;

    /// <summary>The short publishing-block summary, or null when nothing blocks.</summary>
    public string? BlockSummary => Unresolved == 0 ? null
        : $"Publishing blocked: {Ui.Plural(Unresolved, "timetable conflict requires", "timetable conflicts require")} substitute coverage.";
}

public static class PtcCoverageLabels
{
    public static string Label(PtcCoverageStatus s) => s switch
    {
        PtcCoverageStatus.Covered => "Covered",
        PtcCoverageStatus.Uncovered => "Uncovered",
        PtcCoverageStatus.Invalid => "Invalid",
        PtcCoverageStatus.NeedsReview => "Needs review",
        _ => "No conflict",
    };

    public static string Badge(PtcCoverageStatus s) => s switch
    {
        PtcCoverageStatus.Covered => "badge--success",
        PtcCoverageStatus.Uncovered or PtcCoverageStatus.Invalid => "badge--error",
        PtcCoverageStatus.NeedsReview => "badge--warning",
        _ => "badge--neutral",
    };

    public static string Icon(PtcCoverageStatus s) => s switch
    {
        PtcCoverageStatus.Covered => "check-circle",
        PtcCoverageStatus.Uncovered or PtcCoverageStatus.Invalid => "x-circle",
        PtcCoverageStatus.NeedsReview => "alert-triangle",
        _ => "check",
    };

    public static string Kind(PtcConflictKind k) => k switch
    {
        PtcConflictKind.Interview => "PTC overlap",
        PtcConflictKind.SubstituteCover => "Substitute cover",
        PtcConflictKind.Duty => "Duty",
        PtcConflictKind.WorkloadPeriod => "Scheduled period",
        _ => "Timetable",
    };

    public static string Kind(PtcSubstituteChangeKind k) => k switch
    {
        PtcSubstituteChangeKind.Assigned => "Assigned",
        PtcSubstituteChangeKind.Replaced => "Replaced",
        PtcSubstituteChangeKind.Removed => "Removed",
        _ => "Notes changed",
    };
}

/// <summary>The coverage computation (shared by the readiness assessment, publishing and the Class Coverage page).</summary>
public static class PtcCoverageEngine
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", C);

    public static async Task<PtcCoverageReport> ComputeAsync(SchoolDbContext db, int eventId, IReadOnlyList<PtcCoverageInput> schedules, CancellationToken ct)
    {
        var scheduleIds = schedules.Select(s => s.ScheduleId).ToHashSet();
        var teacherIds = schedules.SelectMany(s => s.Teachers.Select(t => t.Id)).Distinct().ToList();
        var dates = schedules.Select(s => s.Date).Distinct().ToList();
        var weeks = dates.Select(SchoolWeek.StartOf).Distinct().ToList();
        var firstWeek = weeks.Count == 0 ? default : weeks.Min();
        var lastWeek = weeks.Count == 0 ? default : weeks.Max();
        var timetableLessons = weeks.Count == 0 ? 0 : await db.TimetableAssignments.AsNoTracking()
            .CountAsync(t => t.StartsOn <= lastWeek && (t.EndedOn == null || t.EndedOn > firstWeek), ct);

        var availability = await PtcTeacherAvailability.LoadAsync(db, teacherIds, dates, ct);
        var hits = new List<(PtcCoverageInput S, PtcRoundRow R, (int Id, string Name) T, PtcBusyHit H)>();
        foreach (var s in schedules)
            foreach (var r in s.Rounds.Where(r => r.Status != PtcRoundStatus.Cancelled).OrderBy(r => r.Start))
                foreach (var t in s.Teachers.DistinctBy(t => t.Id))
                    foreach (var h in availability.Blocking(t.Id, s.Date, r.Start, r.End))
                        hits.Add((s, r, t, h));

        var lessonIds = hits.Where(x => x.H.Interval.LessonId is not null).Select(x => x.H.Interval.LessonId!.Value).Distinct().ToList();
        var lessons = lessonIds.Count == 0 ? [] : await db.TimetableAssignments.AsNoTracking().Where(l => lessonIds.Contains(l.Id))
            .Select(l => new { l.Id, l.SchoolClassId, ClassName = l.SchoolClass.Name, l.DepartmentId, Department = l.Department.Name, l.Subject, Period = l.TimeSlot.Number })
            .ToDictionaryAsync(l => l.Id, ct);

        var conflicts = new List<PtcCoverageConflict>();
        var others = new List<PtcCoverageOther>();
        foreach (var (s, r, t, h) in hits)
        {
            var b = h.Interval;
            if (b.Kind == PtcConflictKind.Lesson && b.LessonId is { } lid && lessons.TryGetValue(lid, out var l))
            {
                var status = h.Covered ? PtcCoverageStatus.Covered : h.InvalidCover is not null ? PtcCoverageStatus.Invalid : PtcCoverageStatus.Uncovered;
                conflicts.Add(new PtcCoverageConflict(s.ScheduleId, s.ClassName, s.Date, r.Id, r.Number, r.Start, r.End, t.Id, t.Name, lid, l.SchoolClassId, l.ClassName,
                    l.DepartmentId, l.Department, l.Subject, l.Period, b.Start, b.End, status, h.Cover ?? h.InvalidCover, h.InvalidReason));
            }
            // Other commitments: interviews of other events (this event's own interviews are the PTC itself), duties, periods, cover given.
            else if (b.Kind != PtcConflictKind.Lesson && !(b.Kind == PtcConflictKind.Interview && b.ScheduleId is { } sid && scheduleIds.Contains(sid)))
                others.Add(new PtcCoverageOther(s.ScheduleId, s.ClassName, s.Date, r.Number, r.Start, r.End, t.Id, t.Name, b.Kind, b.Start, b.End,
                    b.Description + $" (during {s.ClassName}'s Round {r.Number}, {ScheduleRules.Times(r.Start, r.End)})"));
        }

        // Lessons whose period has no times cannot be checked against the Rounds.
        var untimed = new List<PtcCoverageUntimed>();
        if (teacherIds.Count > 0)
        {
            var weekdays = dates.Select(d => d.DayOfWeek).Distinct().ToList();
            var raw = await db.TimetableAssignments.AsNoTracking()
                .Where(l => teacherIds.Contains(l.TeacherId) && weekdays.Contains(l.Day) && (l.TimeSlot.StartTime == null || l.TimeSlot.EndTime == null))
                .Select(l => new { l.TeacherId, l.Day, l.StartsOn, l.EndedOn, ClassName = l.SchoolClass.Name, l.Subject, l.TimeSlot.Number }).ToListAsync(ct);
            foreach (var s in schedules)
            {
                var week = SchoolWeek.StartOf(s.Date);
                foreach (var t in s.Teachers.DistinctBy(t => t.Id))
                    foreach (var l in raw.Where(l => l.TeacherId == t.Id && l.Day == s.Date.DayOfWeek && l.StartsOn <= week && (l.EndedOn == null || l.EndedOn > week)))
                        untimed.Add(new PtcCoverageUntimed(t.Id, t.Name, s.Date, l.ClassName, l.Subject, l.Number));
            }
            untimed = untimed.DistinctBy(u => (u.TeacherId, u.Date, u.ClassName, u.Subject, u.Period)).ToList();
        }

        // The event's active assignments and their state now.
        var used = conflicts.Where(c => c.Cover is not null).GroupBy(c => c.Cover!.Id).ToDictionary(g => g.Key, g => g.First());
        var validity = availability.Covers.ToDictionary(c => c.Id);
        var rawAssignments = await db.PtcSubstituteAssignments.AsNoTracking()
            .Where(a => a.Status == PtcSubstituteStatus.Active && scheduleIds.Contains(a.PtcClassScheduleId))
            .Select(a => new
            {
                a.Id, a.PtcClassScheduleId, ClassName = a.ClassSchedule.SchoolClass.Name, a.ClassSchedule.EventDate, a.OriginalTeacherStaffMemberId,
                Original = a.OriginalTeacher.FullName, a.SubstituteTeacherStaffMemberId, Substitute = a.SubstituteTeacher.FullName, a.StartTime, a.EndTime,
                a.TimetableAssignmentId, LessonClass = a.Lesson != null ? a.Lesson.SchoolClass.Name : null, Subject = a.Lesson != null ? a.Lesson.Subject : null,
                a.Reason, a.Notes, a.AssignedBy, a.CreatedAt,
            })
            .ToListAsync(ct);
        var assignments = rawAssignments.Select(a =>
        {
            var (status, detail) = used.TryGetValue(a.Id, out var c)
                ? c.Status == PtcCoverageStatus.Covered ? (PtcCoverageStatus.Covered, $"Covers {c.TeacherName}'s {c.Subject} lesson with {c.LessonClassName}.")
                : (PtcCoverageStatus.Invalid, c.InvalidReason ?? "It no longer counts.")
                : validity.TryGetValue(a.Id, out var v) && !v.Valid ? (PtcCoverageStatus.Invalid, string.Join(" ", v.Problems))
                : (PtcCoverageStatus.NeedsReview, a.TimetableAssignmentId is null
                    ? $"{a.Original} has no lesson in conflict during {ScheduleRules.Times(a.StartTime, a.EndTime)}: the cover may no longer be needed."
                    : $"The lesson no longer needs cover (it ended, changed teacher or time, or no longer overlaps a Round of {a.ClassName}). Review and remove it if it is not needed.");
            return new PtcCoverageAssignment(a.Id, a.PtcClassScheduleId, a.ClassName, a.EventDate, a.OriginalTeacherStaffMemberId, a.Original,
                a.SubstituteTeacherStaffMemberId, a.Substitute, a.StartTime, a.EndTime, a.TimetableAssignmentId, a.LessonClass, a.Subject, a.Reason, a.Notes,
                a.AssignedBy, a.CreatedAt, status, detail);
        }).OrderBy(a => a.Date).ThenBy(a => a.Start).ThenBy(a => a.OriginalName).ToList();

        var teachers = schedules.SelectMany(s => s.Teachers).DistinctBy(t => t.Id).OrderBy(t => t.Name).Select(t =>
        {
            var mine = conflicts.Where(c => c.TeacherId == t.Id).ToList();
            var status = mine.Any(c => c.Status == PtcCoverageStatus.Uncovered) ? PtcCoverageStatus.Uncovered
                : mine.Any(c => c.Status == PtcCoverageStatus.Invalid) ? PtcCoverageStatus.Invalid
                : untimed.Any(u => u.TeacherId == t.Id) ? PtcCoverageStatus.NeedsReview
                : mine.Count > 0 ? PtcCoverageStatus.Covered : PtcCoverageStatus.NoConflict;
            return new PtcCoverageTeacher(t.Id, t.Name, mine.Select(c => (c.LessonId, c.Date)).Distinct().Count(), status);
        }).ToList();

        return new PtcCoverageReport
        {
            EventId = eventId,
            TimetableLessons = timetableLessons,
            HasSchedules = schedules.Count > 0,
            CancelledLessons = availability.CancelledLessons,
            Teachers = teachers,
            Conflicts = conflicts.OrderBy(c => c.Date).ThenBy(c => c.LessonStart).ThenBy(c => c.TeacherName).ThenBy(c => c.RoundNumber).ToList(),
            Others = others.OrderBy(o => o.Date).ThenBy(o => o.Start).ThenBy(o => o.TeacherName).ToList(),
            Untimed = untimed,
            Assignments = assignments,
        };
    }

    /// <summary>The readiness findings of the coverage: unresolved lessons block publishing; what cannot be checked is a warning.</summary>
    public static IEnumerable<PtcIssue> Issues(PtcCoverageReport r)
    {
        var href = $"/ptc/events/{r.EventId}/coverage";
        foreach (var g in r.Conflicts.Where(c => c.Blocks).GroupBy(c => (c.TeacherId, c.LessonId, c.Date)))
        {
            var c = g.OrderBy(x => x.RoundStart).First();
            var rounds = string.Join(", ", g.Select(x => $"{x.PtcClassName}'s Round {x.RoundNumber}").Distinct());
            yield return new PtcIssue(PtcIssueArea.Coverage, true, c.Status == PtcCoverageStatus.Invalid
                    ? $"{c.TeacherName} teaches {c.LessonClassName} ({c.Subject}) {ScheduleRules.Times(c.LessonStart, c.LessonEnd)} on {Day(c.Date)}, during {rounds}: the assigned substitute ({c.Cover!.SubstituteName}) no longer counts ({c.InvalidReason}), so no PTC substitute covers it. Assign a valid substitute before publishing."
                    : $"{c.TeacherName} teaches {c.LessonClassName} ({c.Subject}) {ScheduleRules.Times(c.LessonStart, c.LessonEnd)} on {Day(c.Date)}, during {rounds}, and no PTC substitute covers it. Assign a substitute (Class Coverage) or change the Rounds before publishing.",
                c.PtcClassName, href);
        }
        if (r.HasSchedules && r.TimetableEmpty)
            yield return new PtcIssue(PtcIssueArea.Coverage, false,
                "The timetable has no lessons for the PTC weeks, so teachers' lessons during the Rounds cannot be checked: class coverage cannot be verified until the timetable is entered.",
                Href: href);
        foreach (var u in r.Untimed)
            yield return new PtcIssue(PtcIssueArea.Coverage, false,
                $"{u.TeacherName}'s {u.Subject} lesson with {u.ClassName} (period {u.Period}) on {Day(u.Date)} cannot be checked: the period has no start and end times.",
                u.ClassName, href);
        foreach (var a in r.Pending)
            yield return new PtcIssue(PtcIssueArea.Coverage, false, $"Substitute {a.SubstituteName} for {a.OriginalName} on {Day(a.Date)}: {a.StatusDetail}", a.PtcClassName, href);
    }
}

// ============================================================ the Super Admin workflow

/// <summary>A staff member offered as a substitute for one lesson (free for the whole lesson).</summary>
public sealed record PtcSubstituteCandidate(int Id, string Name, string? Department, bool SameDepartment);

/// <summary>A staff member who cannot cover the lesson, and why.</summary>
public sealed record PtcSubstituteUnavailable(int Id, string Name, string Reason);

/// <summary>One lesson in conflict and everything needed to assign, replace or remove its substitute.</summary>
public sealed class PtcCoverageForm
{
    public required PtcEventDetail Event { get; init; }

    /// <summary>The lesson's rows (one per Round it overlaps), first by time.</summary>
    public required IReadOnlyList<PtcCoverageConflict> Rows { get; init; }
    public PtcCoverageConflict Lesson => Rows[0];
    public PtcCoverageAssignment? Current { get; init; }
    public required IReadOnlyList<PtcSubstituteCandidate> Eligible { get; init; }
    public required IReadOnlyList<PtcSubstituteUnavailable> Unavailable { get; init; }
    public required IReadOnlyList<PtcSubstituteHistoryRow> History { get; init; }
}

/// <summary>One audited substitute change.</summary>
public sealed record PtcSubstituteHistoryRow(DateTime At, PtcSubstituteChangeKind Kind, string Original, string? OldSubstitute, string? NewSubstitute, string? Lesson,
    string? Reason, string? Notes, string? By, int AssignmentId, int ScheduleId, bool StillActive);

public interface IPtcCoverageService
{
    /// <summary>The lesson <paramref name="lessonId"/> in conflict in class schedule <paramref name="scheduleId"/> of the event, or null.</summary>
    Task<PtcCoverageForm?> FormAsync(int eventId, int scheduleId, int lessonId, CancellationToken ct = default);

    /// <summary>Every audited substitute change of the event, newest first.</summary>
    Task<IReadOnlyList<PtcSubstituteHistoryRow>> HistoryAsync(int eventId, CancellationToken ct = default);

    /// <summary>Records the Super Admin's substitute for a lesson in conflict (checked again now; never chosen automatically).</summary>
    Task<ServiceResult> AssignAsync(int eventId, int scheduleId, int lessonId, int substituteId, string? reason, string? notes, CancellationToken ct = default);

    /// <summary>Another substitute takes over an active assignment (the previous one is kept, cancelled). A reason is required.</summary>
    Task<ServiceResult> ReplaceAsync(int assignmentId, int substituteId, string? reason, string? notes, CancellationToken ct = default);

    /// <summary>Removes an active assignment (kept, cancelled). A reason is required.</summary>
    Task<ServiceResult> RemoveAsync(int assignmentId, string? reason, CancellationToken ct = default);

    /// <summary>Changes an active assignment's administrative notes.</summary>
    Task<ServiceResult> UpdateNotesAsync(int assignmentId, string? notes, CancellationToken ct = default);
}

public sealed class PtcCoverageService(SchoolDbContext db, IPtcAccess access, ICurrentUser currentUser, IPtcAdminService admin) : IPtcCoverageService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public const string ChangedMessage = "This substitute assignment was changed or removed in the meantime (for example by another administrator). Reload the page and check the coverage again.";

    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", C);

    /// <summary>The lesson as written in the history.</summary>
    internal static string Describe(PtcCoverageConflict c) =>
        Trim($"{c.LessonClassName} · {c.Subject} ({c.Department}) · {Day(c.Date)} {ScheduleRules.Times(c.LessonStart, c.LessonEnd)}", PtcSubstituteChange.LessonMaxLength)!;

    private static string? Trim(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    /// <summary>Adds one audit row (saved with the caller's changes).</summary>
    internal static void Audit(SchoolDbContext db, PtcSubstituteAssignment a, int eventId, PtcSubstituteChangeKind kind, int? oldSubstitute, int? newSubstitute,
        string? lesson, string? reason, string? notes, string? by)
    {
        db.PtcSubstituteChanges.Add(new PtcSubstituteChange
        {
            PtcEventId = eventId, PtcClassScheduleId = a.PtcClassScheduleId, Assignment = a, Kind = kind, OriginalTeacherStaffMemberId = a.OriginalTeacherStaffMemberId,
            OldSubstituteStaffMemberId = oldSubstitute, NewSubstituteStaffMemberId = newSubstitute, Lesson = Trim(lesson, PtcSubstituteChange.LessonMaxLength),
            Reason = Trim(reason, PtcSubstituteChange.TextMaxLength), Notes = Trim(notes, PtcSubstituteChange.TextMaxLength), ChangedBy = by, ChangedAt = DateTime.UtcNow,
        });
    }

    // ------------------------------------------------------------------ reads

    public async Task<PtcCoverageForm?> FormAsync(int eventId, int scheduleId, int lessonId, CancellationToken ct = default)
    {
        access.Ensure();
        if (await admin.EventAsync(eventId, ct) is not { } e) return null;
        var rows = e.Coverage.Conflicts.Where(c => c.ScheduleId == scheduleId && c.LessonId == lessonId).OrderBy(c => c.RoundStart).ToList();
        if (rows.Count == 0) return null;
        var lesson = rows[0];
        var current = lesson.Cover is { } cover ? e.Coverage.Assignments.FirstOrDefault(a => a.Id == cover.Id)
            ?? await AssignmentAsync(cover.Id, ct) : null;

        // Candidates: current staff other than the teacher and the current substitute, free for the whole lesson (same engine).
        var staff = await db.StaffMembers.AsNoTracking().Where(s => s.ArchivedAt == null && s.Id != lesson.TeacherId)
            .Select(s => new { s.Id, s.FullName, s.DepartmentId, Department = s.Department != null ? s.Department.Name : null }).OrderBy(s => s.FullName).ToListAsync(ct);
        var availability = await PtcTeacherAvailability.LoadAsync(db, staff.Select(s => s.Id).ToList(), [lesson.Date], ct);
        var eligible = new List<PtcSubstituteCandidate>();
        var unavailable = new List<PtcSubstituteUnavailable>();
        foreach (var s in staff.Where(s => s.Id != current?.SubstituteId))
        {
            var block = availability.FirstBlock([s.Id], lesson.Date, lesson.LessonStart, lesson.LessonEnd);
            if (block is null) eligible.Add(new PtcSubstituteCandidate(s.Id, s.FullName, s.Department, s.DepartmentId == lesson.DepartmentId));
            else unavailable.Add(new PtcSubstituteUnavailable(s.Id, s.FullName, block.Message));
        }
        // This lesson's cover history on this date (every assignment ever made for it, in any class schedule of the event).
        var history = await db.PtcSubstituteChanges.AsNoTracking()
            .Where(h => h.PtcEventId == eventId && h.Assignment.TimetableAssignmentId == lessonId && h.Assignment.ClassSchedule.EventDate == lesson.Date)
            .OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id)
            .Select(h => new PtcSubstituteHistoryRow(h.ChangedAt, h.Kind, h.OriginalTeacher.FullName, h.OldSubstitute != null ? h.OldSubstitute.FullName : null,
                h.NewSubstitute != null ? h.NewSubstitute.FullName : null, h.Lesson, h.Reason, h.Notes, h.ChangedBy, h.PtcSubstituteAssignmentId, h.PtcClassScheduleId,
                h.Assignment.Status == PtcSubstituteStatus.Active))
            .ToListAsync(ct);
        return new PtcCoverageForm
        {
            Event = e, Rows = rows, Current = current,
            Eligible = eligible.OrderByDescending(c => c.SameDepartment).ThenBy(c => c.Name).ToList(),
            Unavailable = unavailable, History = history,
        };
    }

    private async Task<PtcCoverageAssignment?> AssignmentAsync(int id, CancellationToken ct) =>
        await db.PtcSubstituteAssignments.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new PtcCoverageAssignment(a.Id, a.PtcClassScheduleId, a.ClassSchedule.SchoolClass.Name, a.ClassSchedule.EventDate, a.OriginalTeacherStaffMemberId,
                a.OriginalTeacher.FullName, a.SubstituteTeacherStaffMemberId, a.SubstituteTeacher.FullName, a.StartTime, a.EndTime, a.TimetableAssignmentId,
                a.Lesson != null ? a.Lesson.SchoolClass.Name : null, a.Lesson != null ? a.Lesson.Subject : null, a.Reason, a.Notes, a.AssignedBy, a.CreatedAt,
                PtcCoverageStatus.Covered, "Assigned in another class schedule on the same date."))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PtcSubstituteHistoryRow>> HistoryAsync(int eventId, CancellationToken ct = default)
    {
        access.Ensure();
        return await db.PtcSubstituteChanges.AsNoTracking().Where(h => h.PtcEventId == eventId).OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id)
            .Select(h => new PtcSubstituteHistoryRow(h.ChangedAt, h.Kind, h.OriginalTeacher.FullName, h.OldSubstitute != null ? h.OldSubstitute.FullName : null,
                h.NewSubstitute != null ? h.NewSubstitute.FullName : null, h.Lesson, h.Reason, h.Notes, h.ChangedBy, h.PtcSubstituteAssignmentId, h.PtcClassScheduleId,
                h.Assignment.Status == PtcSubstituteStatus.Active))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------ writes (under the PTC lock, checked again inside it)

    private Task<DbScope> BeginAsync(CancellationToken ct) => DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);

    /// <summary>Why <paramref name="substituteId"/> cannot cover <paramref name="originalId"/> on the date and times, or null.</summary>
    private async Task<ServiceResult?> SubstituteProblemAsync(int originalId, int substituteId, DateOnly date, TimeOnly start, TimeOnly end, CancellationToken ct)
    {
        if (substituteId == originalId) return ServiceResult.Fail("SubstituteId", "A teacher cannot be their own substitute.");
        var sub = await db.StaffMembers.AsNoTracking().Where(s => s.Id == substituteId).Select(s => new { s.FullName, s.ArchivedAt }).FirstOrDefaultAsync(ct);
        if (sub is null) return ServiceResult.Fail("SubstituteId", "Choose an existing staff member as the substitute.");
        if (sub.ArchivedAt is not null) return ServiceResult.Fail("SubstituteId", $"{sub.FullName} is not current staff.");
        // The same availability as booking and scheduling: lessons, interviews, other cover, duties and periods.
        var block = (await PtcSchedulingRules.TeacherConflictsAsync(db, substituteId, date, start, end, ct)).FirstOrDefault(c => c.Blocks);
        return block is null ? null : ServiceResult.Fail("SubstituteId", $"{sub.FullName} cannot cover this lesson: {block.Message}");
    }

    private static ServiceResult? TextProblem(string reason, string notes, bool reasonRequired)
    {
        if (reasonRequired && reason.Length == 0) return ServiceResult.Fail("Reason", "Give a reason for this change (it is kept in the history).");
        if (reason.Length > PtcSubstituteAssignment.ReasonMaxLength) return ServiceResult.Fail("Reason", $"Use {PtcSubstituteAssignment.ReasonMaxLength} characters or fewer for the reason.");
        if (notes.Length > PtcSubstituteAssignment.NotesMaxLength) return ServiceResult.Fail("Notes", $"Use {PtcSubstituteAssignment.NotesMaxLength} characters or fewer for the notes.");
        return null;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    public async Task<ServiceResult> AssignAsync(int eventId, int scheduleId, int lessonId, int substituteId, string? reason, string? notes, CancellationToken ct = default)
    {
        access.Ensure();
        var cleanReason = TextNormalizer.CleanText(reason);
        var cleanNotes = TextNormalizer.CleanText(notes);
        if (TextProblem(cleanReason, cleanNotes, false) is { } bad) return bad;

        await using var tx = await BeginAsync(ct);
        await PtcSetupService.LockAsync(db, ct);
        // Coverage as it is now, inside the lock: the lesson must still be in conflict and without a substitute.
        if (await admin.EventAsync(eventId, ct) is not { } e) return ServiceResult.Fail("", "This PTC event no longer exists.");
        if (e.IsClosed) return ServiceResult.Fail("", PtcSetupService.ClosedMessage);
        var row = e.Coverage.Conflicts.Where(c => c.ScheduleId == scheduleId && c.LessonId == lessonId).OrderBy(c => c.RoundStart).FirstOrDefault();
        if (row is null)
            return ServiceResult.Fail("", "This lesson no longer needs a substitute: it does not overlap a Round of this class's PTC any more, or the lesson changed. Revalidate the coverage.");
        if (row.Cover is { } existing)
            return ServiceResult.Fail("", $"{row.TeacherName}'s lesson already has a substitute ({existing.SubstituteName}): replace or remove that assignment instead.");

        var overlapping = await db.PtcSubstituteAssignments.AsNoTracking()
            .Where(a => a.Status == PtcSubstituteStatus.Active && a.OriginalTeacherStaffMemberId == row.TeacherId && a.ClassSchedule.EventDate == row.Date
                        && a.StartTime < row.LessonEnd && a.EndTime > row.LessonStart)
            .Select(a => a.SubstituteTeacher.FullName).FirstOrDefaultAsync(ct);
        if (overlapping is not null)
            return ServiceResult.Fail("", $"{row.TeacherName} already has a substitute ({overlapping}) at an overlapping time on {Day(row.Date)}: replace or remove it first.");
        if (await SubstituteProblemAsync(row.TeacherId, substituteId, row.Date, row.LessonStart, row.LessonEnd, ct) is { } problem) return problem;

        var by = currentUser.AccountName();
        var assignment = new PtcSubstituteAssignment
        {
            PtcClassScheduleId = scheduleId, OriginalTeacherStaffMemberId = row.TeacherId, SubstituteTeacherStaffMemberId = substituteId,
            StartTime = row.LessonStart, EndTime = row.LessonEnd, TimetableAssignmentId = lessonId,
            Reason = NullIfEmpty(cleanReason), Notes = NullIfEmpty(cleanNotes), AssignedBy = by,
        };
        db.PtcSubstituteAssignments.Add(assignment);
        Audit(db, assignment, eventId, PtcSubstituteChangeKind.Assigned, null, substituteId, Describe(row), NullIfEmpty(cleanReason), NullIfEmpty(cleanNotes), by);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(assignment.Id);
    }

    /// <summary>The tracked active assignment with its schedule's event and state, or why it cannot change.</summary>
    private async Task<(PtcSubstituteAssignment? A, int EventId, ServiceResult? Error)> ActiveAsync(int assignmentId, CancellationToken ct)
    {
        var a = await db.PtcSubstituteAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (a is null) return (null, 0, ServiceResult.Fail("", "This substitute assignment no longer exists."));
        var s = await db.PtcClassSchedules.AsNoTracking().Where(x => x.Id == a.PtcClassScheduleId)
            .Select(x => new { x.PtcEventId, x.Status, EventStatus = x.PtcEvent.Status }).FirstAsync(ct);
        if (a.Status != PtcSubstituteStatus.Active) return (null, s.PtcEventId, ServiceResult.Fail("", ChangedMessage));
        if (s.Status == PtcScheduleStatus.Cancelled || s.EventStatus is PtcEventStatus.Cancelled or PtcEventStatus.Completed)
            return (null, s.PtcEventId, ServiceResult.Fail("", PtcSetupService.ClosedMessage));
        return (a, s.PtcEventId, null);
    }

    private async Task<string> LessonTextAsync(PtcSubstituteAssignment a, CancellationToken ct)
    {
        var date = await db.PtcClassSchedules.AsNoTracking().Where(s => s.Id == a.PtcClassScheduleId).Select(s => s.EventDate).FirstAsync(ct);
        var l = a.TimetableAssignmentId is { } id
            ? await db.TimetableAssignments.AsNoTracking().Where(x => x.Id == id).Select(x => new { ClassName = x.SchoolClass.Name, x.Subject, Department = x.Department.Name }).FirstOrDefaultAsync(ct)
            : null;
        return Trim(l is null ? $"Cover {Day(date)} {ScheduleRules.Times(a.StartTime, a.EndTime)}"
            : $"{l.ClassName} · {l.Subject} ({l.Department}) · {Day(date)} {ScheduleRules.Times(a.StartTime, a.EndTime)}", PtcSubstituteChange.LessonMaxLength)!;
    }

    public async Task<ServiceResult> ReplaceAsync(int assignmentId, int substituteId, string? reason, string? notes, CancellationToken ct = default)
    {
        access.Ensure();
        var cleanReason = TextNormalizer.CleanText(reason);
        var cleanNotes = TextNormalizer.CleanText(notes);
        if (TextProblem(cleanReason, cleanNotes, true) is { } bad) return bad;

        await using var tx = await BeginAsync(ct);
        await PtcSetupService.LockAsync(db, ct);
        var (old, eventId, error) = await ActiveAsync(assignmentId, ct);
        if (error is not null) return error;
        if (old!.SubstituteTeacherStaffMemberId == substituteId)
            return ServiceResult.Fail("SubstituteId", "This staff member is already the substitute. Choose another one, or change only the notes.");
        var date = await db.PtcClassSchedules.AsNoTracking().Where(s => s.Id == old.PtcClassScheduleId).Select(s => s.EventDate).FirstAsync(ct);
        // A lesson cover follows the lesson's current times (the period may have been retimed since).
        var (start, end) = (old.StartTime, old.EndTime);
        if (old.TimetableAssignmentId is { } lessonId && await db.TimetableAssignments.AsNoTracking()
                .Where(l => l.Id == lessonId && l.TimeSlot.StartTime != null && l.TimeSlot.EndTime != null)
                .Select(l => new { Start = l.TimeSlot.StartTime!.Value, End = l.TimeSlot.EndTime!.Value }).FirstOrDefaultAsync(ct) is { } times)
            (start, end) = (times.Start, times.End);
        if (await SubstituteProblemAsync(old.OriginalTeacherStaffMemberId, substituteId, date, start, end, ct) is { } problem) return problem;

        var by = currentUser.AccountName();
        var lesson = await LessonTextAsync(old, ct);
        old.Status = PtcSubstituteStatus.Cancelled;
        old.CancelledAt = DateTime.UtcNow;
        old.CancelledBy = by;
        await db.SaveChangesAsync(ct);                                   // frees the "one active cover per lesson" index first
        var replacement = new PtcSubstituteAssignment
        {
            PtcClassScheduleId = old.PtcClassScheduleId, OriginalTeacherStaffMemberId = old.OriginalTeacherStaffMemberId, SubstituteTeacherStaffMemberId = substituteId,
            StartTime = start, EndTime = end, TimetableAssignmentId = old.TimetableAssignmentId,
            Reason = NullIfEmpty(cleanReason), Notes = NullIfEmpty(cleanNotes) ?? old.Notes, AssignedBy = by,
        };
        db.PtcSubstituteAssignments.Add(replacement);
        Audit(db, replacement, eventId, PtcSubstituteChangeKind.Replaced, old.SubstituteTeacherStaffMemberId, substituteId, lesson, cleanReason, replacement.Notes, by);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(replacement.Id);
    }

    public async Task<ServiceResult> RemoveAsync(int assignmentId, string? reason, CancellationToken ct = default)
    {
        access.Ensure();
        var cleanReason = TextNormalizer.CleanText(reason);
        if (TextProblem(cleanReason, "", true) is { } bad) return bad;

        await using var tx = await BeginAsync(ct);
        await PtcSetupService.LockAsync(db, ct);
        var (a, eventId, error) = await ActiveAsync(assignmentId, ct);
        if (error is not null) return error;
        var by = currentUser.AccountName();
        a!.Status = PtcSubstituteStatus.Cancelled;
        a.CancelledAt = DateTime.UtcNow;
        a.CancelledBy = by;
        Audit(db, a, eventId, PtcSubstituteChangeKind.Removed, a.SubstituteTeacherStaffMemberId, null, await LessonTextAsync(a, ct), cleanReason, a.Notes, by);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(eventId);
    }

    public async Task<ServiceResult> UpdateNotesAsync(int assignmentId, string? notes, CancellationToken ct = default)
    {
        access.Ensure();
        var cleanNotes = TextNormalizer.CleanText(notes);
        if (TextProblem("", cleanNotes, false) is { } bad) return bad;

        await using var tx = await BeginAsync(ct);
        await PtcSetupService.LockAsync(db, ct);
        var (a, eventId, error) = await ActiveAsync(assignmentId, ct);
        if (error is not null) return error;
        if ((a!.Notes ?? "") == cleanNotes) return ServiceResult.Ok(a.Id);
        a.Notes = NullIfEmpty(cleanNotes);
        Audit(db, a, eventId, PtcSubstituteChangeKind.NotesChanged, a.SubstituteTeacherStaffMemberId, a.SubstituteTeacherStaffMemberId,
            await LessonTextAsync(a, ct), null, a.Notes, currentUser.AccountName());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(a.Id);
    }
}
