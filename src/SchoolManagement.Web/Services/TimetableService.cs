using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record SlotView(int Id, int Number, string? Name, TimeOnly? Start, TimeOnly? End, bool IsActive)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"Period {Number}" : Name!;
    public string Times => ScheduleRules.Times(Start, End);
    public bool HasTimes => Start is not null;
}

public sealed record SessionView(int Id, int ClassId, string ClassName, DayOfWeek Day, int SlotId, int DepartmentId, string DepartmentName,
    string Subject, int TeacherId, string TeacherName, string? Notes, DateOnly StartsOn, DateOnly? EndedOn);

/// <summary>A single-week period placed in a slot (substitution or office hours), from the workload table.</summary>
public sealed record WeekPeriodView(int Id, int TeacherId, string TeacherName, DayOfWeek Day, int SlotId, WorkloadCategory Category, int? ClassId, string? ClassName, string? Description);

public sealed record DutyView(int Id, DateOnly Date, DutyComponent Component, TimeOnly Start, TimeOnly End, string Description, string? Location, string Role, bool Counted);

public sealed class TimetableGrid
{
    public required DateOnly Week { get; init; }
    public required IReadOnlyList<SlotView> Slots { get; init; }
    public required IReadOnlyList<SessionView> Sessions { get; init; }
    public IReadOnlyList<WeekPeriodView> Periods { get; init; } = [];
    public IReadOnlyList<DutyView> Duties { get; init; } = [];

    /// <summary>Weekly sessions can be changed from the current week on; past weeks are history.</summary>
    public bool Editable => Week >= SchoolWeek.Current;

    public SessionView? Session(DayOfWeek day, int slotId) => Sessions.FirstOrDefault(s => s.Day == day && s.SlotId == slotId);
    public IEnumerable<WeekPeriodView> PeriodsAt(DayOfWeek day, int slotId) => Periods.Where(p => p.Day == day && p.SlotId == slotId);
    public IEnumerable<DutyView> DutiesOn(DayOfWeek day) => Duties.Where(d => d.Date.DayOfWeek == day);
}

public sealed record TeacherCandidate(int Id, string Name, string? Department, int Total, int Max, WorkloadStatus Status, bool Available, string? Reason)
{
    public int Remaining => Math.Max(0, Max - Total);
}

public sealed record ClassTimetableSummary(int Id, string Name, bool IsActive, int Sessions, int Students);

public sealed record TeacherTimetableSummary(int Id, string Name, string? Department, int Sessions);

public sealed record SessionInput(int ClassId, DayOfWeek Day, int SlotId, int? DepartmentId, string? Subject, int? TeacherId, string? Notes);

public interface ITimetableService
{
    Task<IReadOnlyList<SlotView>> SlotsAsync(CancellationToken ct = default);
    Task<SlotView?> GetSlotAsync(int id, CancellationToken ct = default);
    Task<int> SessionsInSlotAsync(int slotId, CancellationToken ct = default);
    Task<ServiceResult> UpdateSlotAsync(int id, string? name, TimeOnly? start, TimeOnly? end, bool isActive, bool confirmAffected, CancellationToken ct = default);

    Task<IReadOnlyList<ClassTimetableSummary>> ClassesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TeacherTimetableSummary>> TeachersAsync(string? search, int? departmentId, CancellationToken ct = default);
    Task<TimetableGrid> ClassGridAsync(int classId, DateOnly week, CancellationToken ct = default);
    Task<TimetableGrid> TeacherGridAsync(int teacherId, DateOnly week, CancellationToken ct = default);

    Task<SessionView?> GetSessionAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SubjectsAsync(int departmentId, CancellationToken ct = default);
    Task<IReadOnlyList<TeacherCandidate>> CandidatesAsync(int departmentId, string? subject, DayOfWeek day, int slotId, DateOnly? week, int? excludeSessionId, CancellationToken ct = default);
    Task<ServiceResult> AssignAsync(SessionInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateSessionAsync(int id, SessionInput input, CancellationToken ct = default);
    Task<ServiceResult> RemoveSessionAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// The weekly timetable (Sunday–Thursday × 10 configurable slots). One row per session feeds both the
/// class and the teacher timetable and counts as one Teaching Session in the teacher's workload.
/// Every save: server-side checks (one subject + teacher per class slot, eligible teacher, real time
/// overlaps with sessions / periods / duties, weekly maximum in every affected week) inside a
/// transaction holding the teacher's workload lock; unique indexes and a trigger back it up.
/// </summary>
public sealed class TimetableService(SchoolDbContext db, IWorkloadService workload, ICurrentUser currentUser) : ITimetableService
{
    // ------------------------------------------------------------------ time slots

    public async Task<IReadOnlyList<SlotView>> SlotsAsync(CancellationToken ct = default) =>
        await db.TimeSlots.AsNoTracking().OrderBy(s => s.Number)
            .Select(s => new SlotView(s.Id, s.Number, s.Name, s.StartTime, s.EndTime, s.IsActive)).ToListAsync(ct);

    public async Task<SlotView?> GetSlotAsync(int id, CancellationToken ct = default) =>
        await db.TimeSlots.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new SlotView(s.Id, s.Number, s.Name, s.StartTime, s.EndTime, s.IsActive)).FirstOrDefaultAsync(ct);

    public async Task<int> SessionsInSlotAsync(int slotId, CancellationToken ct = default) =>
        await db.TimetableAssignments.CountAsync(t => t.TimeSlotId == slotId && t.EndedOn == null, ct)
        + await db.WorkloadAssignments.CountAsync(w => w.TimeSlotId == slotId && w.WeekStart >= SchoolWeek.Current, ct);

    public async Task<ServiceResult> UpdateSlotAsync(int id, string? name, TimeOnly? start, TimeOnly? end, bool isActive, bool confirmAffected, CancellationToken ct = default)
    {
        var slot = await db.TimeSlots.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (slot is null) return ServiceResult.Fail("", "This time slot no longer exists.");
        var result = ServiceResult.Ok(id);
        var clean = TextNormalizer.CleanText(name);
        if (clean.Length > TimeSlot.NameMaxLength) result.AddError("Name", $"Use {TimeSlot.NameMaxLength} characters or fewer.");
        if (start is null != end is null) result.AddError(start is null ? "StartTime" : "EndTime", "Enter both the start and the end time, or leave both empty.");
        else if (start is { } s && end is { } e && e <= s) result.AddError("EndTime", "The end time must be later than the start time.");
        if (!result.Succeeded) return result;

        if (isActive && start is { } ns && end is { } ne)
        {
            var clash = (await db.TimeSlots.AsNoTracking().Where(o => o.Id != id && o.IsActive && o.StartTime != null).ToListAsync(ct))
                .FirstOrDefault(o => o.StartTime < ne && ns < o.EndTime);
            if (clash is not null)
                return ServiceResult.Fail("StartTime", $"These times overlap period {clash.Number} ({ScheduleRules.Times(clash.StartTime, clash.EndTime)}). Slots may not overlap.");
        }

        var inUse = await SessionsInSlotAsync(id, ct);
        if (!isActive && slot.IsActive && inUse > 0)
            return ServiceResult.Fail("IsActive", $"Period {slot.Number} has {inUse} current assignment(s). Move or remove them before deactivating it.");
        var timesChanged = slot.StartTime != start || slot.EndTime != end;
        if (timesChanged && inUse > 0 && !confirmAffected)
            return ServiceResult.Fail("ConfirmAffected", $"Period {slot.Number} has {inUse} current assignment(s). They stay in this slot and move to the new times — tick the confirmation to continue.");

        (slot.Name, slot.StartTime, slot.EndTime, slot.IsActive, slot.UpdatedAt) = (clean.Length == 0 ? null : clean, start, end, isActive, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return result;
    }

    // ------------------------------------------------------------------ pickers

    public async Task<IReadOnlyList<ClassTimetableSummary>> ClassesAsync(CancellationToken ct = default)
    {
        var current = SchoolWeek.Current;
        var counts = await db.TimetableAssignments.AsNoTracking().ActiveIn(current).GroupBy(t => t.SchoolClassId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var classes = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.IsActive, Students = c.Students.Count }).ToListAsync(ct);
        return classes.Select(c => new ClassTimetableSummary(c.Id, c.Name, c.IsActive, counts.GetValueOrDefault(c.Id), c.Students)).ToList();
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
        return new TimetableGrid
        {
            Week = week,
            Slots = await SlotsAsync(ct),
            Sessions = await Sessions(db.TimetableAssignments.Where(t => t.SchoolClassId == classId).ActiveIn(week)).ToListAsync(ct),
            Periods = await Periods(db.WorkloadAssignments.Where(w => w.SchoolClassId == classId && w.WeekStart == week && w.TimeSlotId != null)).ToListAsync(ct),
        };
    }

    public async Task<TimetableGrid> TeacherGridAsync(int teacherId, DateOnly week, CancellationToken ct = default)
    {
        week = SchoolWeek.StartOf(week);
        var end = week.AddDays(6);
        var counted = await db.WorkloadAssignments.Where(w => w.TeacherId == teacherId && w.DutyAssignmentId != null)
            .Select(w => w.DutyAssignmentId!.Value).ToListAsync(ct);
        var duties = await db.DutyAssignments.AsNoTracking()
            .Where(d => d.DutyDate >= week && d.DutyDate <= end)
            .Where(d => d.PrimaryStaffId == teacherId || (d.BackupStaffId == teacherId && d.BackupStatus == BackupRequestStatus.Accepted))
            .OrderBy(d => d.DutyDate).ThenBy(d => d.StartTime)
            .Select(d => new { d.Id, d.DutyDate, d.Component, d.StartTime, d.EndTime, d.Description, d.Location, Primary = d.PrimaryStaffId == teacherId })
            .ToListAsync(ct);
        return new TimetableGrid
        {
            Week = week,
            Slots = await SlotsAsync(ct),
            Sessions = await Sessions(db.TimetableAssignments.Where(t => t.TeacherId == teacherId).ActiveIn(week)).ToListAsync(ct),
            Periods = await Periods(db.WorkloadAssignments.Where(w => w.TeacherId == teacherId && w.WeekStart == week && w.TimeSlotId != null)).ToListAsync(ct),
            Duties = duties.Select(d => new DutyView(d.Id, d.DutyDate, d.Component, d.StartTime, d.EndTime, d.Description, d.Location,
                d.Primary ? "Assigned" : "Backup", counted.Contains(d.Id))).ToList(),
        };
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

    /// <summary>Ends the session from the current week (kept as history; earlier weeks still count it).</summary>
    public async Task<ServiceResult> RemoveSessionAsync(int id, CancellationToken ct = default)
    {
        var session = await db.TimetableAssignments.FirstOrDefaultAsync(t => t.Id == id && t.EndedOn == null, ct);
        if (session is null) return ServiceResult.Fail("", "This session is no longer on the timetable.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ScheduleRules.LockTeacherAsync(db, session.TeacherId, ct);
        End(session, "Removed from the timetable");
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
        if (slot is null || !slot.IsActive) result.AddError("SlotId", "Choose an active time slot.");
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

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
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
                return ServiceResult.Fail("", $"{cls!.Name} already has {occupied.Subject} with {occupied.Teacher} on {ScheduleRules.DayName(input.Day)}, period {slot!.Number}. Remove it first.");

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

    private void End(TimetableAssignment session, string reason)
    {
        var current = SchoolWeek.Current;
        session.EndedOn = current < session.StartsOn ? session.StartsOn : current;
        session.EndReason = reason.Length > 200 ? reason[..200] : reason;
        session.UpdatedBy = currentUser.Name;
    }

    private static IQueryable<SessionView> Sessions(IQueryable<TimetableAssignment> q) =>
        q.AsNoTracking().Select(t => new SessionView(t.Id, t.SchoolClassId, t.SchoolClass.Name, t.Day, t.TimeSlotId, t.DepartmentId, t.Department.Name,
            t.Subject, t.TeacherId, t.Teacher.FullName, t.Notes, t.StartsOn, t.EndedOn));

    private static IQueryable<WeekPeriodView> Periods(IQueryable<TeacherWorkloadAssignment> q) =>
        q.AsNoTracking().Select(w => new WeekPeriodView(w.Id, w.TeacherId, w.Teacher.FullName, w.Day!.Value, w.TimeSlotId!.Value, w.Category,
            w.SchoolClassId, w.SchoolClass != null ? w.SchoolClass.Name : null, w.Description));
}
