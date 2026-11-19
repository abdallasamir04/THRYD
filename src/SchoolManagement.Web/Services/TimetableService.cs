using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>A period (time slot) with its scope: school-wide or one class, and the weekdays it runs.</summary>
public sealed record SlotView(int Id, int Number, string? Name, TimeOnly? Start, TimeOnly? End, bool IsActive,
    int? ClassId = null, string? ClassName = null, int DayMask = 0, int? ReplacesId = null, bool Archived = false)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"Period {Number}" : Name!;
    public string Times => ScheduleRules.Times(Start, End);
    public bool HasTimes => Start is not null;
    public bool IsSchoolWide => ClassId is null;

    /// <summary>The label with the class for class-specific periods (school-wide views, reports).</summary>
    public string FullLabel => ClassId is null ? Label : $"{Label} ({ClassName})";
    public string ScopeLabel => ClassId is null ? "All classes" : ReplacesId is null ? $"{ClassName} only" : $"{ClassName} only (own version)";
    public bool AllDays => PeriodDays.IsAll(DayMask);
    public string DaysLabel => PeriodDays.Label(DayMask);
    public bool AppliesOn(DayOfWeek day) => PeriodDays.Applies(DayMask, day);
    public int? Minutes => Start is { } s && End is { } e ? (int)(e - s).TotalMinutes : null;
}

public sealed record SessionView(int Id, int ClassId, string ClassName, DayOfWeek Day, int SlotId, int DepartmentId, string DepartmentName,
    string Subject, int TeacherId, string TeacherName, string? Notes, DateOnly StartsOn, DateOnly? EndedOn);

/// <summary>A single-week period placed in a slot (substitution or office hours), from the workload table.</summary>
public sealed record WeekPeriodView(int Id, int TeacherId, string TeacherName, DayOfWeek Day, int SlotId, WorkloadCategory Category, int? ClassId, string? ClassName, string? Description);

public sealed record DutyView(int Id, DateOnly Date, DutyComponent Component, TimeOnly Start, TimeOnly End, string Description, string? Location, string Role, bool Counted);

public sealed class TimetableGrid
{
    public required DateOnly Week { get; init; }

    /// <summary>The rows / columns shown: the timetable's periods, plus any period a shown lesson still sits in.</summary>
    public required IReadOnlyList<SlotView> Slots { get; init; }
    public required IReadOnlyList<SessionView> Sessions { get; init; }
    public IReadOnlyList<WeekPeriodView> Periods { get; init; } = [];
    public IReadOnlyList<DutyView> Duties { get; init; } = [];

    /// <summary>Periods that belong to this timetable's structure (the others are shown only for their lessons).</summary>
    public IReadOnlySet<int> Structure { get; init; } = new HashSet<int>();

    /// <summary>Lessons with a problem (period disabled / removed / not on that day, overlapping lessons): session id → reason.</summary>
    public IReadOnlyDictionary<int, string> Problems { get; init; } = new Dictionary<int, string>();

    /// <summary>Cancelled lessons of the week: (session id, date) → reason.</summary>
    public IReadOnlyDictionary<(int SessionId, DateOnly Date), string> Cancelled { get; init; } = new Dictionary<(int, DateOnly), string>();

    /// <summary>Weekly sessions can be changed from the current week on; past weeks are history.</summary>
    public bool Editable => Week >= SchoolWeek.Current;

    /// <summary>The period runs in this timetable on that day (enabled, part of the structure, the day selected).</summary>
    public bool Scheduled(DayOfWeek day, SlotView slot) => slot.IsActive && !slot.Archived && Structure.Contains(slot.Id) && slot.AppliesOn(day);

    public string? CancelReason(SessionView s) => Cancelled.GetValueOrDefault((s.Id, ScheduleRules.DateOf(Week, s.Day)));

    public SessionView? Session(DayOfWeek day, int slotId) => Sessions.FirstOrDefault(s => s.Day == day && s.SlotId == slotId);
    public IEnumerable<WeekPeriodView> PeriodsAt(DayOfWeek day, int slotId) => Periods.Where(p => p.Day == day && p.SlotId == slotId);
    public IEnumerable<DutyView> DutiesOn(DayOfWeek day) => Duties.Where(d => d.Date.DayOfWeek == day);
}

public sealed record TeacherCandidate(int Id, string Name, string? Department, int Total, int Max, WorkloadStatus Status, bool Available, string? Reason)
{
    public int Remaining => Math.Max(0, Max - Total);
}

/// <summary>A class's timetable at a glance; <paramref name="Capacity"/> = lessons its own period structure allows a week.</summary>
public sealed record ClassTimetableSummary(int Id, string Name, bool IsActive, int Sessions, int Students, int Capacity = 0);

public sealed record TeacherTimetableSummary(int Id, string Name, string? Department, int Sessions);

public sealed record SessionInput(int ClassId, DayOfWeek Day, int SlotId, int? DepartmentId, string? Subject, int? TeacherId, string? Notes);

public interface ITimetableService
{
    /// <summary>The school-wide periods (every class has them unless it replaced one), enabled and disabled.</summary>
    Task<IReadOnlyList<SlotView>> SlotsAsync(CancellationToken ct = default);

    /// <summary>Enabled school-wide periods plus the given periods (e.g. class-specific ones in use), for cross-class grids and reports.</summary>
    Task<IReadOnlyList<SlotView>> SlotsForAsync(IEnumerable<int> usedSlotIds, CancellationToken ct = default);

    Task<SlotView?> GetSlotAsync(int id, CancellationToken ct = default);
    Task<int> SessionsInSlotAsync(int slotId, CancellationToken ct = default);

    /// <summary>Current lessons per period (of one class, or of the school).</summary>
    Task<IReadOnlyDictionary<int, int>> LessonsPerSlotAsync(int? classId, CancellationToken ct = default);

    Task<IReadOnlyList<ClassTimetableSummary>> ClassesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TeacherTimetableSummary>> TeachersAsync(string? search, int? departmentId, CancellationToken ct = default);
    Task<TimetableGrid> ClassGridAsync(int classId, DateOnly week, CancellationToken ct = default);
    Task<TimetableGrid> TeacherGridAsync(int teacherId, DateOnly week, CancellationToken ct = default);

    Task<SessionView?> GetSessionAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SubjectsAsync(int departmentId, CancellationToken ct = default);

    /// <summary>The teachers who may teach a subject of a department (the same rule a lesson save checks), by name.</summary>
    Task<IReadOnlyList<(int Id, string Name)>> TeachersForAsync(int departmentId, string subject, CancellationToken ct = default);
    Task<IReadOnlyList<TeacherCandidate>> CandidatesAsync(int departmentId, string? subject, DayOfWeek day, int slotId, DateOnly? week, int? excludeSessionId, CancellationToken ct = default);
    Task<ServiceResult> AssignAsync(SessionInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateSessionAsync(int id, SessionInput input, CancellationToken ct = default);
    Task<ServiceResult> RemoveSessionAsync(int id, CancellationToken ct = default);

    /// <summary>The same, with the reason kept in the lesson's history (e.g. "Replaced by an uploaded timetable").</summary>
    Task<ServiceResult> RemoveSessionAsync(int id, string reason, CancellationToken ct = default);

    /// <summary>Moves a lesson to another day / period of its class (from this week; history kept).</summary>
    Task<ServiceResult> MoveSessionAsync(int id, DayOfWeek day, int slotId, CancellationToken ct = default);

    /// <summary>Free day + period targets a lesson can move to (its class's enabled periods without a lesson).</summary>
    Task<IReadOnlyList<(DayOfWeek Day, SlotView Slot)>> MoveTargetsAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// The weekly timetable: the school days × each class's own periods (school-wide periods it has not replaced,
/// plus class-specific ones; each period runs on its selected days — see <see cref="PeriodMap"/>). One row per
/// session feeds both the class and the teacher timetable and counts as one Teaching Session in the teacher's
/// workload. Every save: server-side checks (the period runs for that class on that day, one lesson at a time
/// per class, eligible teacher, real time overlaps with the teacher's sessions / periods / duties across all
/// classes, weekly maximum in every affected week) inside a transaction holding the period-structure lock and
/// the teacher's workload lock; unique indexes and a trigger back it up.
/// </summary>
public sealed class TimetableService(SchoolDbContext db, IWorkloadService workload, ICurrentUser currentUser) : ITimetableService
{
    // ------------------------------------------------------------------ time slots

    public async Task<IReadOnlyList<SlotView>> SlotsAsync(CancellationToken ct = default) =>
        PeriodMap.Order(await db.TimeSlots.AsNoTracking().Where(s => s.SchoolClassId == null && s.ArchivedAt == null).Select(PeriodMap.Project).ToListAsync(ct));

    public async Task<IReadOnlyList<SlotView>> SlotsForAsync(IEnumerable<int> usedSlotIds, CancellationToken ct = default)
    {
        var used = usedSlotIds.Distinct().ToList();
        return PeriodMap.Order(await db.TimeSlots.AsNoTracking()
            .Where(s => (s.SchoolClassId == null && s.ArchivedAt == null && s.IsActive) || used.Contains(s.Id))
            .Select(PeriodMap.Project).ToListAsync(ct));
    }

    public async Task<SlotView?> GetSlotAsync(int id, CancellationToken ct = default) =>
        await db.TimeSlots.AsNoTracking().Where(s => s.Id == id).Select(PeriodMap.Project).FirstOrDefaultAsync(ct);

    public async Task<int> SessionsInSlotAsync(int slotId, CancellationToken ct = default) =>
        await db.TimetableAssignments.CountAsync(t => t.TimeSlotId == slotId && t.EndedOn == null, ct)
        + await db.WorkloadAssignments.CountAsync(w => w.TimeSlotId == slotId && w.WeekStart >= SchoolWeek.Current, ct);

    public async Task<IReadOnlyDictionary<int, int>> LessonsPerSlotAsync(int? classId, CancellationToken ct = default) =>
        await db.TimetableAssignments.AsNoTracking().Where(t => t.EndedOn == null && (classId == null || t.SchoolClassId == classId))
            .GroupBy(t => t.TimeSlotId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    // ------------------------------------------------------------------ pickers

    public async Task<IReadOnlyList<ClassTimetableSummary>> ClassesAsync(CancellationToken ct = default)
    {
        var current = SchoolWeek.Current;
        var counts = await db.TimetableAssignments.AsNoTracking().ActiveIn(current).GroupBy(t => t.SchoolClassId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var classes = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.IsActive, Students = c.Students.Count }).ToListAsync(ct);
        var map = await PeriodMap.LoadAsync(db, ct);
        return classes.Select(c => new ClassTimetableSummary(c.Id, c.Name, c.IsActive, counts.GetValueOrDefault(c.Id), c.Students, map.Capacity(c.Id))).ToList();
    }

    public async Task<IReadOnlyList<TeacherTimetableSummary>> TeachersAsync(string? search, int? departmentId, CancellationToken ct = default)
    {
        var current = SchoolWeek.Current;
        var query = db.Teachers.AsNoTracking();
        var term = TextNormalizer.NameKey(search);
        if (term.Length > 0) query = query.Where(t => t.NormalizedName.Contains(term));
        if (departmentId is { } d) query = query.Where(t => t.DepartmentId == d);
        var counts = await db.TimetableAssignments.AsNoTracking().ActiveIn(current).GroupBy(t => t.TeacherId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = await query.OrderBy(t => t.FullName)
            .Select(t => new { t.Id, t.FullName, Department = t.Department != null ? t.Department.Name : null, t.Subcategory, t.Subject }).ToListAsync(ct);
        return rows.Select(t => new TeacherTimetableSummary(t.Id, t.FullName,
            t.Department is null ? null : t.Subcategory is null ? t.Department : $"{t.Department} · {DepartmentCatalog.Detail(t.Subcategory, t.Subject)}",
            counts.GetValueOrDefault(t.Id))).ToList();
    }

    // ------------------------------------------------------------------ grids (both read the same rows)

    public async Task<TimetableGrid> ClassGridAsync(int classId, DateOnly week, CancellationToken ct = default)
    {
        week = SchoolWeek.StartOf(week);
        var map = await PeriodMap.LoadAsync(db, ct);
        var sessions = await Sessions(db.TimetableAssignments.Where(t => t.SchoolClassId == classId).ActiveIn(week)).ToListAsync(ct);
        var periods = await Periods(db.WorkloadAssignments.Where(w => w.SchoolClassId == classId && w.WeekStart == week && w.TimeSlotId != null)).ToListAsync(ct);
        var structure = map.ForClass(classId);
        var problems = new Dictionary<int, string>();
        // Past weeks are history: a period changed since then is not a problem of that week.
        if (week >= SchoolWeek.Current)
        {
            foreach (var s in sessions)
            {
                var slot = map.Get(s.SlotId);
                if (slot is null || map.Schedules(classId, s.SlotId, s.Day)) continue;
                problems[s.Id] = slot.Archived ? $"{slot.Label} was deleted — move or remove this lesson."
                    : !map.InStructure(classId, s.SlotId) ? $"{slot.Label} is no longer in this class's timetable — move this lesson."
                    : !slot.IsActive ? $"{slot.Label} is disabled — move this lesson."
                    : $"{slot.Label} does not run on {ScheduleRules.DayName(s.Day)} — move this lesson.";
            }
            AddOverlaps(map, sessions, problems, s => $"{s.Subject} ({s.TeacherName})");
        }
        return new TimetableGrid
        {
            Week = week,
            Slots = WithUsed(map, structure, sessions.Select(s => s.SlotId).Concat(periods.Select(p => p.SlotId))),
            Structure = structure.Select(s => s.Id).ToHashSet(),
            Sessions = sessions,
            Periods = periods,
            Problems = problems,
            Cancelled = await CancelledAsync(sessions, week, ct),
        };
    }

    public async Task<TimetableGrid> TeacherGridAsync(int teacherId, DateOnly week, CancellationToken ct = default)
    {
        week = SchoolWeek.StartOf(week);
        var end = week.AddDays(6);
        var map = await PeriodMap.LoadAsync(db, ct);
        var counted =await db.WorkloadAssignments.Where(w => w.TeacherId == teacherId && w.DutyAssignmentId != null)
            .Select(w => w.DutyAssignmentId!.Value).ToListAsync(ct);
        var duties = await db.DutyAssignments.AsNoTracking()
            .Where(d => d.DutyDate >= week && d.DutyDate <= end)
            .Where(d => d.PrimaryStaffId == teacherId || (d.BackupStaffId == teacherId && d.BackupStatus == BackupRequestStatus.Accepted))
            .OrderBy(d => d.DutyDate).ThenBy(d => d.StartTime)
            .Select(d => new { d.Id, d.DutyDate, d.Component, d.StartTime, d.EndTime, d.Description, d.Location, Primary = d.PrimaryStaffId == teacherId })
            .ToListAsync(ct);
        var sessions = await Sessions(db.TimetableAssignments.Where(t => t.TeacherId == teacherId).ActiveIn(week)).ToListAsync(ct);
        var periods = await Periods(db.WorkloadAssignments.Where(w => w.TeacherId == teacherId && w.WeekStart == week && w.TimeSlotId != null)).ToListAsync(ct);
        // A teacher's week: the enabled school-wide periods, plus any class's own period the teacher teaches in.
        var structure = map.SchoolWide.Where(s => s.IsActive).ToList();
        var problems = new Dictionary<int, string>();
        if (week >= SchoolWeek.Current) AddOverlaps(map, sessions, problems, s => $"{s.ClassName} ({s.Subject})");
        return new TimetableGrid
        {
            Week = week,
            Slots = WithUsed(map, structure, sessions.Select(s => s.SlotId).Concat(periods.Select(p => p.SlotId))),
            Structure = structure.Select(s => s.Id).ToHashSet(),
            Sessions = sessions,
            Periods = periods,
            Problems = problems,
            Cancelled = await CancelledAsync(sessions, week, ct),
            Duties = duties.Select(d => new DutyView(d.Id, d.DutyDate, d.Component, d.StartTime, d.EndTime, d.Description, d.Location,
                d.Primary ? "Assigned" : "Backup", counted.Contains(d.Id))).ToList(),
        };
    }

    private static IReadOnlyList<SlotView> WithUsed(PeriodMap map, IEnumerable<SlotView> structure, IEnumerable<int> used)
    {
        var list = structure.ToList();
        foreach (var id in used.Distinct())
            if (list.All(s => s.Id != id) && map.Get(id) is { } extra) list.Add(extra);
        return PeriodMap.Order(list);
    }

    /// <summary>Two lessons of the same timetable on the same day whose periods overlap in time.</summary>
    private static void AddOverlaps(PeriodMap map, IReadOnlyList<SessionView> sessions, Dictionary<int, string> problems, Func<SessionView, string> describe)
    {
        foreach (var day in sessions.GroupBy(s => s.Day))
        {
            var list = day.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var (a, b) = (map.Get(list[i].SlotId), map.Get(list[j].SlotId));
                    if (a is null || b is null || !ScheduleRules.Overlaps(a.Id, a.Start, a.End, b.Id, b.Start, b.End)) continue;
                    problems.TryAdd(list[i].Id, $"Overlaps {describe(list[j])} in {b.Label} ({b.Times}).");
                    problems.TryAdd(list[j].Id, $"Overlaps {describe(list[i])} in {a.Label} ({a.Times}).");
                }
        }
    }

    private async Task<IReadOnlyDictionary<(int SessionId, DateOnly Date), string>> CancelledAsync(IReadOnlyList<SessionView> sessions, DateOnly week, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        var end = week.AddDays(6);
        return (await db.SessionCancellations.AsNoTracking()
                .Where(c => ids.Contains(c.TimetableAssignmentId) && c.SessionDate >= week && c.SessionDate <= end)
                .Select(c => new { c.TimetableAssignmentId, c.SessionDate, c.Reason }).ToListAsync(ct))
            .ToDictionary(c => (c.TimetableAssignmentId, c.SessionDate), c => c.Reason);
    }

    public Task<SessionView?> GetSessionAsync(int id, CancellationToken ct = default) =>
        Sessions(db.TimetableAssignments.Where(t => t.Id == id)).FirstOrDefaultAsync(ct);

    // ------------------------------------------------------------------ assign period

    /// <summary>What can be taught in a department: its subjects / subcategories, or the department name itself.</summary>
    public async Task<IReadOnlyList<string>> SubjectsAsync(int departmentId, CancellationToken ct = default)
    {
        var name = await db.Departments.AsNoTracking().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        if (name is null) return [];
        var rules = await DepartmentCatalog.ForAsync(db, departmentId, ct);
        return rules is null ? [name] : rules.TeachingSubjects.Distinct().ToList();
    }

    public async Task<IReadOnlyList<(int Id, string Name)>> TeachersForAsync(int departmentId, string subject, CancellationToken ct = default) =>
        (await (await EligibleTeachersAsync(departmentId, subject, ct)).OrderBy(t => t.FullName).Select(t => new { t.Id, t.FullName }).ToListAsync(ct))
            .Select(t => (t.Id, t.FullName)).ToList();

    public async Task<IReadOnlyList<TeacherCandidate>> CandidatesAsync(int departmentId, string? subject, DayOfWeek day, int slotId, DateOnly? week, int? excludeSessionId, CancellationToken ct = default)
    {
        var slot = await db.TimeSlots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == slotId, ct);
        if (slot is null) return [];
        var teachers = await (await EligibleTeachersAsync(departmentId, subject, ct))
            .OrderBy(t => t.FullName)
            .Select(t => new { t.Id, t.FullName, Department = t.Department!.Name, t.Subcategory, t.Subject }).ToListAsync(ct);
        var view = SchoolStart(week);
        var result = new List<TeacherCandidate>();
        foreach (var t in teachers)
        {
            var summary = (await workload.SummaryAsync(t.Id, view, ct))!;
            var conflict = await ScheduleRules.TeacherConflictAsync(db, t.Id, t.FullName, day, slot, week, excludeSessionId, null, ct);
            var full = summary.Total >= summary.Max;
            result.Add(new TeacherCandidate(t.Id, t.FullName,
                t.Subcategory is null ? t.Department : $"{t.Department} · {DepartmentCatalog.Detail(t.Subcategory, t.Subject)}",
                summary.Total, summary.Max, summary.Status, conflict is null && !full,
                conflict ?? (full ? $"Weekly workload is full ({summary.Total}/{summary.Max})." : null)));
        }
        return result.OrderByDescending(c => c.Available).ThenBy(c => c.Total).ThenBy(c => c.Name).ToList();
    }

    public Task<ServiceResult> AssignAsync(SessionInput input, CancellationToken ct = default) => SaveAsync(null, input, ct);

    public Task<ServiceResult> UpdateSessionAsync(int id, SessionInput input, CancellationToken ct = default) => SaveAsync(id, input, ct);

    public async Task<IReadOnlyList<(DayOfWeek Day, SlotView Slot)>> MoveTargetsAsync(int id, CancellationToken ct = default)
    {
        var s = await db.TimetableAssignments.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.EndedOn == null, ct);
        if (s is null) return [];
        var map = await PeriodMap.LoadAsync(db, ct);
        var taken = (await db.TimetableAssignments.AsNoTracking().Where(t => t.SchoolClassId == s.SchoolClassId && t.EndedOn == null && t.Id != id)
            .Select(t => new { t.Day, t.TimeSlotId }).ToListAsync(ct)).Select(t => (t.Day, t.TimeSlotId)).ToHashSet();
        return ScheduleRules.Days.SelectMany(d => map.ForClassDay(s.SchoolClassId, d).Select(slot => (Day: d, Slot: slot)))
            .Where(x => !taken.Contains((x.Day, x.Slot.Id)) && !(x.Day == s.Day && x.Slot.Id == s.TimeSlotId)).ToList();
    }

    public async Task<ServiceResult> MoveSessionAsync(int id, DayOfWeek day, int slotId, CancellationToken ct = default)
    {
        if (!ScheduleRules.IsSchoolDay(day)) return ServiceResult.Fail("Day", "Choose a school day.");
        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        try
        {
            await ScheduleRules.LockPeriodsAsync(db, exclusive: false, ct);
            var session = await db.TimetableAssignments.Include(t => t.SchoolClass).Include(t => t.Teacher).FirstOrDefaultAsync(t => t.Id == id && t.EndedOn == null, ct);
            if (session is null) return ServiceResult.Fail("", "This session is no longer on the timetable.");
            if (session.Day == day && session.TimeSlotId == slotId) return ServiceResult.Ok(id);
            var map = await PeriodMap.LoadAsync(db, ct);
            var target = await db.TimeSlots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == slotId, ct);
            if (target is null || !map.Schedules(session.SchoolClassId, slotId, day))
                return ServiceResult.Fail("SlotId", $"{(target is null ? "This period" : ScheduleRules.PeriodName(target))} does not run on {ScheduleRules.DayName(day)} in {session.SchoolClass.Name}'s timetable.");
            await ScheduleRules.LockTeacherAsync(db, session.TeacherId, ct);

            var occupied = await db.TimetableAssignments.AsNoTracking()
                .Where(t => t.SchoolClassId == session.SchoolClassId && t.Day == day && t.TimeSlotId == slotId && t.EndedOn == null)
                .Select(t => new { t.Subject, Teacher = t.Teacher.FullName }).FirstOrDefaultAsync(ct);
            if (occupied is not null)
                return ServiceResult.Fail("", $"{session.SchoolClass.Name} already has {occupied.Subject} with {occupied.Teacher} on {ScheduleRules.DayName(day)}, {ScheduleRules.PeriodName(target)}.");
            if (await ClassClashAsync(map, session.SchoolClassId, session.SchoolClass.Name, day, target, id, ct) is { } classClash)
                return ServiceResult.Fail("", classClash);
            if (await ScheduleRules.TeacherConflictAsync(db, session.TeacherId, session.Teacher.FullName, day, target, null, id, null, ct) is { } conflict)
                return ServiceResult.Fail("", conflict);
            // A substitute already placed in the lesson's current period would no longer match it.
            var current = SchoolWeek.Current;
            if (await db.WorkloadAssignments.AnyAsync(w => w.Category == WorkloadCategory.Substitution && w.SchoolClassId == session.SchoolClassId
                    && w.Day == session.Day && w.TimeSlotId == session.TimeSlotId && w.WeekStart >= current, ct))
                return ServiceResult.Fail("", "This lesson has a substitute in this or a later week. Remove the substitution before moving the lesson.");

            var from = map.Get(session.TimeSlotId)?.Label ?? "its period";
            var moved = await TimetableRows.MoveAsync(db, session, day, slotId,
                $"Moved from {ScheduleRules.DayName(session.Day)}, {from} to {ScheduleRules.DayName(day)}, {ScheduleRules.PeriodName(target)}", currentUser.Name, ct);
            await tx.CommitAsync(ct);
            return ServiceResult.Ok(moved.Id);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: WorkloadService.LimitErrorNumber })
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This teacher is above the weekly maximum. Resolve their workload before moving lessons.");
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "That period (or this teacher at that time) was just assigned by someone else. Refresh the timetable.");
        }
        catch (SqlException ex) when (ex.Number == 51001)
        {
            return ServiceResult.Fail("", "Someone else is changing the timetable right now. Try again in a moment.");
        }
    }

    /// <summary>Another current lesson of the class on that day whose period overlaps (overlapping times; same period is checked separately).</summary>
    private async Task<string?> ClassClashAsync(PeriodMap map, int classId, string className, DayOfWeek day, TimeSlot slot, int? excludeId, CancellationToken ct)
    {
        var others = await db.TimetableAssignments.AsNoTracking()
            .Where(t => t.SchoolClassId == classId && t.Day == day && t.EndedOn == null && t.Id != (excludeId ?? 0) && t.TimeSlotId != slot.Id)
            .Select(t => new { t.TimeSlotId, t.Subject, Teacher = t.Teacher.FullName }).ToListAsync(ct);
        foreach (var o in others)
            if (map.Get(o.TimeSlotId) is { } os && ScheduleRules.Overlaps(slot.Id, slot.StartTime, slot.EndTime, os.Id, os.Start, os.End))
                return $"{className} already has {o.Subject} with {o.Teacher} on {ScheduleRules.DayName(day)} in {os.Label} ({os.Times}), which overlaps {ScheduleRules.PeriodName(slot)} ({ScheduleRules.Times(slot.StartTime, slot.EndTime)}).";
        return null;
    }

    /// <summary>Ends the session from the current week (kept as history; earlier weeks still count it).</summary>
    public Task<ServiceResult> RemoveSessionAsync(int id, CancellationToken ct = default) => RemoveSessionAsync(id, "Removed from the timetable", ct);

    public async Task<ServiceResult> RemoveSessionAsync(int id, string reason, CancellationToken ct = default)
    {
        var session = await db.TimetableAssignments.FirstOrDefaultAsync(t => t.Id == id && t.EndedOn == null, ct);
        if (session is null) return ServiceResult.Fail("", "This session is no longer on the timetable.");
        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        await ScheduleRules.LockTeacherAsync(db, session.TeacherId, ct);
        End(session, reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    private async Task<ServiceResult> SaveAsync(int? id, SessionInput input, CancellationToken ct)
    {
        var current = SchoolWeek.Current;
        var result = ServiceResult.Ok();
        if (!ScheduleRules.IsSchoolDay(input.Day)) result.AddError("Day", "Choose a school day (Sunday to Thursday).");
        var cls = await db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == input.ClassId, ct);
        if (cls is null) result.AddError("", "This class no longer exists.");
        else if (!cls.IsActive) result.AddError("", $"{cls.Name} is inactive. Activate it before changing its timetable.");
        var slot = await db.TimeSlots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == input.SlotId, ct);
        if (slot is null || !slot.IsActive || slot.ArchivedAt is not null) result.AddError("SlotId", "Choose an enabled period.");
        if (input.DepartmentId is null) result.AddError("DepartmentId", "Choose the department.");
        var subject = TextNormalizer.CleanText(input.Subject);
        if (subject.Length == 0) result.AddError("Subject", "Choose the subject.");
        if (input.TeacherId is null) result.AddError("TeacherId", "Choose the teacher.");
        var notes = input.Notes?.Trim();
        if (notes?.Length > TimetableAssignment.NotesMaxLength) result.AddError("Notes", $"Use {TimetableAssignment.NotesMaxLength} characters or fewer.");
        if (!result.Succeeded) return result;

        var subjects = await SubjectsAsync(input.DepartmentId!.Value, ct);
        var canonical = subjects.FirstOrDefault(s => string.Equals(s, subject, StringComparison.OrdinalIgnoreCase));
        if (canonical is null) return ServiceResult.Fail("Subject", "Choose a subject of the selected department.");

        if (await db.ManagementMembers.AnyAsync(m => m.Id == input.TeacherId, ct))
            return ServiceResult.Fail("TeacherId", "Management Team members are not academic staff and cannot be timetabled.");
        var teacher = await (await EligibleTeachersAsync(input.DepartmentId.Value, canonical, ct)).Where(t => t.Id == input.TeacherId)
            .Select(t => new { t.Id, t.FullName }).FirstOrDefaultAsync(ct);
        if (teacher is null) return ServiceResult.Fail("TeacherId", $"Choose a teacher of the selected department who teaches {canonical}.");

        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        try
        {
            // The class's period structure cannot change while the lesson is saved.
            await ScheduleRules.LockPeriodsAsync(db, exclusive: false, ct);
            var map = await PeriodMap.LoadAsync(db, ct);
            if (!map.Schedules(input.ClassId, input.SlotId, input.Day))
            {
                var label = map.Get(input.SlotId)?.Label ?? "This period";
                return ServiceResult.Fail("SlotId", map.InStructure(input.ClassId, input.SlotId)
                    ? $"{label} does not run on {ScheduleRules.DayName(input.Day)} in {cls!.Name}'s timetable."
                    : $"{label} is not part of {cls!.Name}'s timetable.");
            }
            TimetableAssignment? existing = null;
            if (id is not null)
            {
                existing = await db.TimetableAssignments.FirstOrDefaultAsync(t => t.Id == id && t.EndedOn == null, ct);
                if (existing is null) return ServiceResult.Fail("", "This session is no longer on the timetable.");
                if (existing.SchoolClassId != input.ClassId || existing.Day != input.Day || existing.TimeSlotId != input.SlotId)
                    return ServiceResult.Fail("", "A session cannot be moved to another class or slot. Remove it and assign the new slot instead.");
            }

            foreach (var t in new SortedSet<int> { teacher.Id, existing?.TeacherId ?? teacher.Id })
                await ScheduleRules.LockTeacherAsync(db, t, ct);

            // One subject and one teacher per class slot.
            var occupied = await db.TimetableAssignments.AsNoTracking()
                .Where(t => t.SchoolClassId == input.ClassId && t.Day == input.Day && t.TimeSlotId == input.SlotId && t.EndedOn == null && t.Id != (id ?? 0))
                .Select(t => new { t.Subject, Teacher = t.Teacher.FullName }).FirstOrDefaultAsync(ct);
            if (occupied is not null)
                return ServiceResult.Fail("", $"{cls!.Name} already has {occupied.Subject} with {occupied.Teacher} on {ScheduleRules.DayName(input.Day)}, {ScheduleRules.PeriodName(slot!)}. Remove it first.");
            // A class has one lesson at a time: no other lesson of the class that day may overlap these times.
            if (await ClassClashAsync(map, input.ClassId, cls!.Name, input.Day, slot!, id, ct) is { } classClash)
                return ServiceResult.Fail("", classClash);

            var sameTeacher = existing is not null && existing.TeacherId == teacher.Id;
            if (!sameTeacher)
            {
                var conflict = await ScheduleRules.TeacherConflictAsync(db, teacher.Id, teacher.FullName, input.Day, slot!, null, id, null, ct);
                if (conflict is not null) return ServiceResult.Fail("TeacherId", conflict);
                var capacity = await CheckCapacityAsync(teacher.Id, teacher.FullName, current, ct);
                if (!capacity.Succeeded) return capacity;
            }

            if (existing is not null && sameTeacher)
            {
                // Subject / department / notes change in place: same teacher, same week count.
                (existing.DepartmentId, existing.Subject, existing.Notes, existing.UpdatedBy) = (input.DepartmentId.Value, canonical, Empty(notes), currentUser.Name);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ServiceResult.Ok(existing.Id);
            }

            // New session, or a new teacher for the slot: the old row ends this week and a new one starts,
            // so neither teacher's week counts the period twice and earlier weeks keep their history.
            if (existing is not null)
            {
                End(existing, $"Teacher changed to {teacher.FullName}");
                await db.SaveChangesAsync(ct);
            }
            var row = new TimetableAssignment
            {
                SchoolClassId = input.ClassId, Day = input.Day, TimeSlotId = input.SlotId, DepartmentId = input.DepartmentId.Value,
                Subject = canonical, TeacherId = teacher.Id, Notes = Empty(notes), StartsOn = current,
                CreatedBy = currentUser.Name, UpdatedBy = currentUser.Name,
            };
            db.TimetableAssignments.Add(row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ServiceResult.Ok(row.Id);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: WorkloadService.LimitErrorNumber })
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("TeacherId", $"Cannot assign {teacher.FullName}: this would exceed the weekly maximum of {WorkloadRules.Max} periods.");
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This slot (or this teacher at this time) was just assigned by someone else. Refresh the timetable.");
        }
        catch (SqlException ex) when (ex.Number == 51001)
        {
            return ServiceResult.Fail("", "Someone else is changing this teacher's workload right now. Try again in a moment.");
        }
    }

    /// <summary>
    /// A weekly session adds one period to every week from now on: the current week and every later week
    /// that already has workload or a starting session must stay within the configured maximum.
    /// </summary>
    private async Task<ServiceResult> CheckCapacityAsync(int teacherId, string teacherName, DateOnly current, CancellationToken ct)
    {
        var max = await workload.MaxAsync(ct);
        var weeks = new SortedSet<DateOnly> { current };
        foreach (var w in await db.WorkloadAssignments.Where(w => w.TeacherId == teacherId && w.WeekStart > current).Select(w => w.WeekStart).Distinct().ToListAsync(ct)) weeks.Add(w);
        foreach (var w in await db.TimetableAssignments.Where(t => t.TeacherId == teacherId && t.StartsOn > current).Select(t => t.StartsOn).Distinct().ToListAsync(ct)) weeks.Add(w);
        foreach (var week in weeks)
        {
            var total = (await db.WorkloadAssignments.Where(w => w.TeacherId == teacherId && w.WeekStart == week).SumAsync(w => (int?)w.Periods, ct) ?? 0)
                + await ScheduleRules.SessionsAsync(db, teacherId, week, ct);
            if (total + 1 > max)
                return ServiceResult.Fail("TeacherId", total >= max
                    ? $"Cannot assign {teacherName}: the weekly workload is already {total}/{max} periods{(week == current ? "" : $" in the week of {SchoolWeek.Label(week)}")}."
                    : $"Cannot assign {teacherName}: only {max - total} period(s) left{(week == current ? "" : $" in the week of {SchoolWeek.Label(week)}")}.");
        }
        return ServiceResult.Ok();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Teachers of the department; for a subject (Quran, French, AI…) only those whose subcategory or subject matches.</summary>
    private async Task<IQueryable<Teacher>> EligibleTeachersAsync(int departmentId, string? subject, CancellationToken ct)
    {
        var q = db.Teachers.AsNoTracking().Where(t => t.DepartmentId == departmentId);
        var s = subject?.Trim();
        var departmentName = await db.Departments.AsNoTracking().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(s) && !string.Equals(s, departmentName, StringComparison.OrdinalIgnoreCase))
            q = q.Where(t => t.Subject == s || t.Subcategory == s);
        return q;
    }

    private static DateOnly SchoolStart(DateOnly? week) => week is { } w && w > SchoolWeek.Current ? SchoolWeek.StartOf(w) : SchoolWeek.Current;

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private void End(TimetableAssignment session, string reason) => TimetableRows.End(session, reason, currentUser.Name);

    private static IQueryable<SessionView> Sessions(IQueryable<TimetableAssignment> q) =>
        q.AsNoTracking().Select(t => new SessionView(t.Id, t.SchoolClassId, t.SchoolClass.Name, t.Day, t.TimeSlotId, t.DepartmentId, t.Department.Name,
            t.Subject, t.TeacherId, t.Teacher.FullName, t.Notes, t.StartsOn, t.EndedOn));

    private static IQueryable<WeekPeriodView> Periods(IQueryable<TeacherWorkloadAssignment> q) =>
        q.AsNoTracking().Select(w => new WeekPeriodView(w.Id, w.TeacherId, w.Teacher.FullName, w.Day!.Value, w.TimeSlotId!.Value, w.Category,
            w.SchoolClassId, w.SchoolClass != null ? w.SchoolClass.Name : null, w.Description));
}
