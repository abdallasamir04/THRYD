using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public enum WorkloadStatus
{
    Available,
    NearCapacity,
    Full,
    OverLimit,
}

public sealed record WorkloadBreakdown(int Class, int Substitution, int Duty, int Office)
{
    public static readonly WorkloadBreakdown Empty = new(0, 0, 0, 0);
    public int Total => Class + Substitution + Duty + Office;

    public int this[WorkloadCategory c] => c switch
    {
        WorkloadCategory.Class => Class,
        WorkloadCategory.Substitution => Substitution,
        WorkloadCategory.Duty => Duty,
        _ => Office,
    };

    public static WorkloadBreakdown From(IEnumerable<(WorkloadCategory Category, int Periods)> rows)
    {
        int c = 0, s = 0, d = 0, o = 0;
        foreach (var (category, periods) in rows)
        {
            switch (category)
            {
                case WorkloadCategory.Class: c += periods; break;
                case WorkloadCategory.Substitution: s += periods; break;
                case WorkloadCategory.Duty: d += periods; break;
                case WorkloadCategory.Office: o += periods; break;
            }
        }
        return new WorkloadBreakdown(c, s, d, o);
    }
}

/// <summary>The 24-period rule and its status bands, in one place.</summary>
public static class WorkloadRules
{
    public const int Max = TeacherWorkloadAssignment.MaxWeeklyPeriods;
    public const int NearFrom = TeacherWorkloadAssignment.NearCapacityFrom;

    public static readonly WorkloadCategory[] Categories =
        [WorkloadCategory.Class, WorkloadCategory.Substitution, WorkloadCategory.Duty, WorkloadCategory.Office];

    public static WorkloadStatus StatusOf(int total, int max = Max) => total switch
    {
        _ when total > max => WorkloadStatus.OverLimit,
        _ when total == max => WorkloadStatus.Full,
        _ when total >= Math.Max(0, max - 4) => WorkloadStatus.NearCapacity,
        _ => WorkloadStatus.Available,
    };

    public static string Label(WorkloadStatus s) => s switch
    {
        WorkloadStatus.Available => "Available",
        WorkloadStatus.NearCapacity => "Near capacity",
        WorkloadStatus.Full => "Full",
        _ => "Over limit",
    };

    /// <summary>CSS tone: green / orange / red.</summary>
    public static string Tone(WorkloadStatus s) => s switch
    {
        WorkloadStatus.Available => "ok",
        WorkloadStatus.NearCapacity => "near",
        _ => "full",
    };

    public static string Label(WorkloadCategory c) => c switch
    {
        WorkloadCategory.Class => "Teaching Sessions",
        WorkloadCategory.Substitution => "Substitutions",
        WorkloadCategory.Duty => "Duties",
        _ => "Office Hours",
    };

    public static string Icon(WorkloadCategory c) => c switch
    {
        WorkloadCategory.Class => "graduation-cap",
        WorkloadCategory.Substitution => "swap",
        WorkloadCategory.Duty => "shield-check",
        _ => "briefcase",
    };
}

/// <param name="FromTimetable">Teaching Sessions that come from the timetable (already included in Breakdown.Class).</param>
public sealed record WorkloadSummary(int TeacherId, string TeacherName, string? JobTitle, DateOnly WeekStart, WorkloadBreakdown Breakdown, string? DepartmentName = null, int Max = 24, int FromTimetable = 0)
{
    public int Total => Breakdown.Total;
    public int Remaining => Math.Max(0, Max - Total);
    public WorkloadStatus Status => WorkloadRules.StatusOf(Total, Max);
}

public sealed record WorkloadAssignmentRow(
    int Id, int TeacherId, string TeacherName, DateOnly WeekStart, WorkloadCategory Category, int Periods,
    int? SchoolClassId, string? ClassName, int? DutyAssignmentId, string? DutyLabel, string? Description,
    DateTime CreatedAt, DateTime UpdatedAt, DayOfWeek? Day = null, int? SlotNumber = null, int? TimeSlotId = null);

public sealed record WorkloadHistoryRow(
    long Id, int AssignmentId, DateOnly WeekStart, WorkloadChange Change, WorkloadCategory Category,
    int? PeriodsBefore, int? PeriodsAfter, int WeekTotalAfter, string Summary, DateTime ChangedAt, string? ChangedBy);

public sealed record DutyOption(int Id, string Label);

/// <param name="Day">With <paramref name="TimeSlotId"/>: the period is placed in that timetable slot (one period).</param>
public sealed record WorkloadInput(
    int TeacherId, DateOnly Week, WorkloadCategory Category, int Periods,
    int? SchoolClassId, int? DutyAssignmentId, string? Description, DayOfWeek? Day = null, int? TimeSlotId = null);

public sealed class WorkloadOverviewQuery
{
    public string? Week { get; set; }
    public string? Search { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeacherId { get; set; }
    public string? Status { get; set; }      // available | near | full
    public string? Category { get; set; }    // class | substitution | duty | office
    public string Sort { get; set; } = "load";
    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(JobTitle) || TeacherId is not null
        || !string.IsNullOrWhiteSpace(Status) || !string.IsNullOrWhiteSpace(Category) || !string.IsNullOrWhiteSpace(Department) || DepartmentId is not null;
}

public interface IWorkloadService
{
    Task<WorkloadSummary?> SummaryAsync(int teacherId, DateOnly week, CancellationToken ct = default);
    Task<int> MaxAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkloadAssignmentRow>> AssignmentsAsync(int teacherId, DateOnly week, CancellationToken ct = default);
    Task<WorkloadAssignmentRow?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkloadSummary>> OverviewAsync(DateOnly week, WorkloadOverviewQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<WorkloadAssignmentRow>> WeekAssignmentsAsync(DateOnly week, CancellationToken ct = default);
    Task<IReadOnlyList<WorkloadHistoryRow>> HistoryAsync(int teacherId, int take = 100, CancellationToken ct = default);
    Task<IReadOnlyList<DutyOption>> DutyOptionsAsync(int teacherId, DateOnly week, int? includeDutyId, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(WorkloadInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, WorkloadInput input, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Teacher workload: Class + Substitution + Duty + Office periods per school week, never more than 24.
/// Every change runs in a transaction that first takes an exclusive SQL Server application lock for
/// (teacher, week), then sums the week, so two simultaneous requests cannot both pass the check.
/// A database trigger rejects any week above 24 as a final backstop (direct SQL included).
/// </summary>
public sealed class WorkloadService(SchoolDbContext db, ICurrentUser currentUser, ILogger<WorkloadService> logger) : IWorkloadService
{
    public const int LimitErrorNumber = 51000;
    private const int LockErrorNumber = 51001;

    // ------------------------------------------------------------------ reads

    public async Task<WorkloadSummary?> SummaryAsync(int teacherId, DateOnly week, CancellationToken ct = default)
    {
        week = SchoolWeek.StartOf(week);
        var teacher = await db.Teachers.AsNoTracking().Where(t => t.Id == teacherId)
            .Select(t => new { t.Id, t.FullName, t.JobTitle, DepartmentName = t.Department != null ? t.Department.Name : null, t.Subcategory })
            .FirstOrDefaultAsync(ct);
        if (teacher is null) return null;
        var rows = await db.WorkloadAssignments.AsNoTracking()
            .Where(w => w.TeacherId == teacherId && w.WeekStart == week)
            .GroupBy(w => w.Category).Select(g => new { g.Key, Sum = g.Sum(x => x.Periods) })
            .ToListAsync(ct);
        var sessions = await ScheduleRules.SessionsAsync(db, teacherId, week, ct);
        return new WorkloadSummary(teacher.Id, teacher.FullName, teacher.JobTitle, week,
            WorkloadBreakdown.From(rows.Select(r => (r.Key, r.Sum)).Append((WorkloadCategory.Class, sessions))),
            DepartmentLabel(teacher.DepartmentName, teacher.Subcategory), await MaxAsync(ct), sessions);
    }

    /// <summary>A teacher's total for one week: workload rows + timetable sessions taught that week.</summary>
    private async Task<int> WeekTotalAsync(int teacherId, DateOnly week, int excludeId, CancellationToken ct) =>
        (await db.WorkloadAssignments.Where(w => w.TeacherId == teacherId && w.WeekStart == week && w.Id != excludeId).SumAsync(w => (int?)w.Periods, ct) ?? 0)
        + await ScheduleRules.SessionsAsync(db, teacherId, week, ct);

    /// <summary>The configured weekly maximum (Settings), never above the hard limit of 24.</summary>
    public async Task<int> MaxAsync(CancellationToken ct = default)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == SettingsService.MaxWeeklyPeriodsKey)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(value, out var max) && max >= 1 ? Math.Min(max, WorkloadRules.Max) : WorkloadRules.Max;
    }

    private static string? DepartmentLabel(string? department, string? subcategory) =>
        department is null ? null : string.IsNullOrWhiteSpace(subcategory) ? department : $"{department} ({subcategory})";

    public async Task<IReadOnlyList<WorkloadAssignmentRow>> AssignmentsAsync(int teacherId, DateOnly week, CancellationToken ct = default)
    {
        week = SchoolWeek.StartOf(week);
        var rows = await Project(db.WorkloadAssignments.AsNoTracking().Where(w => w.TeacherId == teacherId && w.WeekStart == week))
            .ToListAsync(ct);
        return rows.OrderBy(r => r.Category).ThenBy(r => r.ClassName).ThenBy(r => r.Id).Select(WithDutyLabel).ToList();
    }

    public async Task<WorkloadAssignmentRow?> GetAsync(int id, CancellationToken ct = default)
    {
        var row = await Project(db.WorkloadAssignments.AsNoTracking().Where(w => w.Id == id)).FirstOrDefaultAsync(ct);
        return row is null ? null : WithDutyLabel(row);
    }

    public async Task<IReadOnlyList<WorkloadAssignmentRow>> WeekAssignmentsAsync(DateOnly week, CancellationToken ct = default)
    {
        week = SchoolWeek.StartOf(week);
        var rows = await Project(db.WorkloadAssignments.AsNoTracking().Where(w => w.WeekStart == week)).ToListAsync(ct);
        return rows.OrderBy(r => r.TeacherName).ThenBy(r => r.Category).ThenBy(r => r.Id).Select(WithDutyLabel).ToList();
    }

    public async Task<IReadOnlyList<WorkloadSummary>> OverviewAsync(DateOnly week, WorkloadOverviewQuery q, CancellationToken ct = default)
    {
        week = SchoolWeek.StartOf(week);
        var teachers = db.Teachers.AsNoTracking();
        if (q.TeacherId is { } onlyTeacher) teachers = teachers.Where(t => t.Id == onlyTeacher);
        var term = TextNormalizer.NameKey(q.Search);
        if (term.Length > 0) teachers = teachers.Where(t => t.NormalizedName.Contains(term));
        if (q.JobTitle == TeacherListQuery.NoJobTitle) teachers = teachers.Where(t => t.JobTitle == null || t.JobTitle == "");
        else if (!string.IsNullOrWhiteSpace(q.JobTitle)) teachers = teachers.Where(t => t.JobTitle == q.JobTitle);
        if (q.DepartmentId is { } departmentId) teachers = teachers.Where(t => t.DepartmentId == departmentId);
        else if (q.Department == "__none") teachers = teachers.Where(t => t.DepartmentId == null);
        else if (!string.IsNullOrWhiteSpace(q.Department)) teachers = teachers.Where(t => t.Department != null && t.Department.Name == q.Department);

        var max = await MaxAsync(ct);
        var list = await teachers.Select(t => new { t.Id, t.FullName, t.JobTitle, DepartmentName = t.Department != null ? t.Department.Name : null, t.Subcategory })
            .ToListAsync(ct);
        var sums = await db.WorkloadAssignments.AsNoTracking().Where(w => w.WeekStart == week)
            .GroupBy(w => new { w.TeacherId, w.Category })
            .Select(g => new { g.Key.TeacherId, g.Key.Category, Sum = g.Sum(x => x.Periods) })
            .ToListAsync(ct);
        var byTeacher = sums.ToLookup(s => s.TeacherId);
        var sessions = await db.TimetableAssignments.AsNoTracking().ActiveIn(week)
            .GroupBy(t => t.TeacherId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        IEnumerable<WorkloadSummary> result = list.Select(t =>
        {
            var fromTimetable = sessions.GetValueOrDefault(t.Id);
            return new WorkloadSummary(t.Id, t.FullName, t.JobTitle, week,
                WorkloadBreakdown.From(byTeacher[t.Id].Select(s => (s.Category, s.Sum)).Append((WorkloadCategory.Class, fromTimetable))),
                DepartmentLabel(t.DepartmentName, t.Subcategory), max, fromTimetable);
        });

        result = q.Status switch
        {
            "available" => result.Where(s => s.Status == WorkloadStatus.Available),
            "near" => result.Where(s => s.Status == WorkloadStatus.NearCapacity),
            "full" => result.Where(s => s.Status is WorkloadStatus.Full or WorkloadStatus.OverLimit),
            "capacity" => result.Where(s => s.Remaining > 0),
            _ => result,
        };
        if (Enum.TryParse<WorkloadCategory>(q.Category, true, out var category))
            result = result.Where(s => s.Breakdown[category] > 0);

        return (q.Sort switch
        {
            "name" => result.OrderBy(s => s.TeacherName),
            "department" => result.OrderBy(s => s.DepartmentName == null).ThenBy(s => s.DepartmentName).ThenBy(s => s.TeacherName),
            "remaining" => result.OrderByDescending(s => s.Remaining).ThenBy(s => s.TeacherName),
            _ => result.OrderByDescending(s => s.Total).ThenBy(s => s.TeacherName),
        }).ToList();
    }

    public async Task<IReadOnlyList<WorkloadHistoryRow>> HistoryAsync(int teacherId, int take = 100, CancellationToken ct = default) =>
        await db.WorkloadHistory.AsNoTracking().Where(h => h.TeacherId == teacherId)
            .OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id).Take(take)
            .Select(h => new WorkloadHistoryRow(h.Id, h.AssignmentId, h.WeekStart, h.Change, h.Category,
                h.PeriodsBefore, h.PeriodsAfter, h.WeekTotalAfter, h.Summary, h.ChangedAt, h.ChangedBy))
            .ToListAsync(ct);

    /// <summary>Duties in the week where this teacher is the assigned staff or the accepted backup, not yet counted.</summary>
    public async Task<IReadOnlyList<DutyOption>> DutyOptionsAsync(int teacherId, DateOnly week, int? includeDutyId, CancellationToken ct = default)
    {
        week = SchoolWeek.StartOf(week);
        var end = week.AddDays(6);
        var used = db.WorkloadAssignments.Where(w => w.TeacherId == teacherId && w.DutyAssignmentId != null && w.DutyAssignmentId != includeDutyId)
            .Select(w => w.DutyAssignmentId!.Value);
        var duties = await db.DutyAssignments.AsNoTracking()
            .Where(d => d.DutyDate >= week && d.DutyDate <= end)
            .Where(d => (d.PrimaryStaffId == teacherId && d.PrimaryAttendance != AttendanceStatus.Absent)
                || (d.BackupStaffId == teacherId && d.BackupStatus == BackupRequestStatus.Accepted && d.BackupAttendance != AttendanceStatus.Absent))
            .Where(d => !used.Contains(d.Id))
            .OrderBy(d => d.DutyDate).ThenBy(d => d.StartTime)
            .Select(d => new { d.Id, d.DutyDate, d.Component, d.StartTime, d.EndTime, d.Description })
            .ToListAsync(ct);
        return duties.Select(d => new DutyOption(d.Id, DutyLabel(d.DutyDate, d.Component, d.StartTime, d.EndTime))).ToList();
    }

    // ------------------------------------------------------------------ writes

    public Task<ServiceResult> CreateAsync(WorkloadInput input, CancellationToken ct = default) => SaveAsync(null, input, ct);

    public Task<ServiceResult> UpdateAsync(int id, WorkloadInput input, CancellationToken ct = default) => SaveAsync(id, input, ct);

    private async Task<ServiceResult> SaveAsync(int? id, WorkloadInput input, CancellationToken ct)
    {
        var week = SchoolWeek.StartOf(input.Week);
        var basic = await ValidateBasicsAsync(id, input, week, ct);
        if (!basic.Result.Succeeded) return basic.Result;
        var teacherName = basic.TeacherName!;
        var max = await MaxAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            TeacherWorkloadAssignment? entity = null;
            if (id is not null)
            {
                entity = await db.WorkloadAssignments.FirstOrDefaultAsync(w => w.Id == id, ct);
                if (entity is null) return ServiceResult.Fail("", "This assignment no longer exists.");
            }

            // Lock every teacher this change touches, in a fixed order (no deadlocks). The timetable takes the same lock.
            var teachersToLock = new SortedSet<int> { input.TeacherId };
            if (entity is not null) teachersToLock.Add(entity.TeacherId);
            foreach (var teacher in teachersToLock) await ScheduleRules.LockTeacherAsync(db, teacher, ct);

            // The week's total (rows + timetable sessions) without the row being edited, so it is never counted twice.
            var others = await WeekTotalAsync(input.TeacherId, week, id ?? 0, ct);

            var capacity = CheckCapacity(teacherName, others, input.Periods, max);
            if (!capacity.Succeeded) return capacity;

            var duplicate = await CheckDuplicatesAsync(id, input, week, ct);
            if (!duplicate.Succeeded) return duplicate;

            if (basic.Slot is { } slot && input.Day is { } day)
            {
                var conflict = await ScheduleRules.TeacherConflictAsync(db, input.TeacherId, teacherName, day, slot, week, null, id, ct);
                if (conflict is not null) return ServiceResult.Fail("TimeSlotId", conflict);
            }

            var before = entity is null ? null : new { entity.Periods, entity.Category, entity.WeekStart, entity.TeacherId };
            if (entity is null)
            {
                entity = new TeacherWorkloadAssignment();
                db.WorkloadAssignments.Add(entity);
            }

            entity.TeacherId = input.TeacherId;
            entity.WeekStart = week;
            entity.Category = input.Category;
            entity.Periods = input.Periods;
            entity.SchoolClassId = input.Category is WorkloadCategory.Class or WorkloadCategory.Substitution ? input.SchoolClassId : null;
            entity.DutyAssignmentId = input.Category == WorkloadCategory.Duty ? input.DutyAssignmentId : null;
            (entity.Day, entity.TimeSlotId) = basic.Slot is null ? (null, null) : (input.Day, input.TimeSlotId);
            var description = TextNormalizer.CleanText(input.Description);
            entity.Description = description.Length == 0 ? null : description;
            await db.SaveChangesAsync(ct);

            var totalAfter = others + input.Periods;
            if (before is null)
            {
                AddHistory(entity, WorkloadChange.Created, null, entity.Periods, totalAfter,
                    $"Added {Periods(entity.Periods)} of {entity.Category}{Detail(basic.ClassName, entity.Description)}.");
            }
            else
            {
                var moved = before.WeekStart != week || before.TeacherId != input.TeacherId;
                AddHistory(entity, WorkloadChange.Updated, before.Periods, entity.Periods, totalAfter,
                    $"Changed {before.Category} {Periods(before.Periods)} → {entity.Category} {Periods(entity.Periods)}{Detail(basic.ClassName, entity.Description)}"
                    + (moved ? $" (moved from week of {SchoolWeek.Label(before.WeekStart)})." : "."));
                if (before.TeacherId != input.TeacherId)
                {
                    var oldTotal = await WeekTotalAsync(before.TeacherId, before.WeekStart, 0, ct);
                    db.WorkloadHistory.Add(NewHistory(entity.Id, before.TeacherId, before.WeekStart, WorkloadChange.Removed, before.Category,
                        before.Periods, null, oldTotal, $"Moved {Periods(before.Periods)} of {before.Category} to {teacherName}."));
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ServiceResult.Ok(entity.Id);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: LimitErrorNumber })
        {
            db.ChangeTracker.Clear();
            logger.LogWarning("Workload trigger rejected a change for teacher {TeacherId}, week {Week}", input.TeacherId, week);
            var total = await WeekTotalAsync(input.TeacherId, week, id ?? 0, ct);
            return CheckCapacity(teacherName, total, input.Periods, max) is { Succeeded: false } r ? r : LimitReached(teacherName, WorkloadRules.Max);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "A matching assignment was just added for this teacher and week. Refresh and edit that one instead.");
        }
        catch (SqlException ex) when (ex.Number == LockErrorNumber)
        {
            return ServiceResult.Fail("", "Someone else is changing this teacher's workload right now. Try again in a moment.");
        }
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var entity = await db.WorkloadAssignments.Include(w => w.SchoolClass).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This assignment no longer exists.");

        await ScheduleRules.LockTeacherAsync(db, entity.TeacherId, ct);
        db.WorkloadAssignments.Remove(entity);
        await db.SaveChangesAsync(ct);
        var totalAfter = await WeekTotalAsync(entity.TeacherId, entity.WeekStart, 0, ct);
        AddHistory(entity, WorkloadChange.Removed, entity.Periods, null, totalAfter,
            $"Removed {Periods(entity.Periods)} of {entity.Category}{Detail(entity.SchoolClass?.Name, entity.Description)}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ------------------------------------------------------------------ rules

    private static ServiceResult CheckCapacity(string teacherName, int currentTotal, int adding, int max)
    {
        if (currentTotal + adding <= max) return ServiceResult.Ok();
        if (currentTotal >= max) return LimitReached(teacherName, max);
        var remaining = max - currentTotal;
        return ServiceResult.Fail("Periods",
            $"Cannot assign {Periods(adding)}. {teacherName} has {currentTotal}/{max} periods this week, so at most {Periods(remaining)} can be added.");
    }

    private static ServiceResult LimitReached(string teacherName, int max) =>
        ServiceResult.Fail("Periods", $"Cannot assign this teacher. {teacherName}'s weekly workload is already {max}/{max} periods.");

    private async Task<(ServiceResult Result, string? TeacherName, string? ClassName, TimeSlot? Slot)> ValidateBasicsAsync(int? id, WorkloadInput input, DateOnly week, CancellationToken ct)
    {
        var result = ServiceResult.Ok();
        TimeSlot? slot = null;
        if (input.Day is not null || input.TimeSlotId is not null)
        {
            // A period placed in a slot (Assign Period): one period, a school day, an active slot; not for duties (they have their own times).
            if (input.Day is not { } d || !ScheduleRules.IsSchoolDay(d)) result.AddError("Day", "Choose a school day (Sunday to Thursday).");
            slot = input.TimeSlotId is { } sid ? await db.TimeSlots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.IsActive, ct) : null;
            if (slot is null) result.AddError("TimeSlotId", "Choose an active time slot.");
            if (input.Periods != 1) result.AddError("Periods", "A period placed in a time slot counts as exactly 1 period.");
            if (input.Category is WorkloadCategory.Class or WorkloadCategory.Duty)
                result.AddError("Category", "Teaching sessions are added through the timetable, and duties keep their own times.");
        }
        if (!Enum.IsDefined(input.Category)) result.AddError("Category", "Choose Class, Substitution, Duty or Office.");
        if (input.Periods < 1 || input.Periods > WorkloadRules.Max)
            result.AddError("Periods", $"Enter a number of periods from 1 to {WorkloadRules.Max}.");
        if (TextNormalizer.CleanText(input.Description).Length > TeacherWorkloadAssignment.DescriptionMaxLength)
            result.AddError("Description", $"Use {TeacherWorkloadAssignment.DescriptionMaxLength} characters or fewer.");

        var teacherName = await db.Teachers.Where(t => t.Id == input.TeacherId).Select(t => t.FullName).FirstOrDefaultAsync(ct);
        if (teacherName is null) result.AddError("TeacherId", "Choose a teacher from the list.");

        string? className = null;
        var existing = id is null ? null : await db.WorkloadAssignments.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (input.Category is WorkloadCategory.Class or WorkloadCategory.Substitution && input.SchoolClassId is { } classId)
        {
            var cls = await db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId, ct);
            if (cls is null) result.AddError("SchoolClassId", "Choose a class from the list.");
            else if (!cls.IsActive && existing?.SchoolClassId != classId) result.AddError("SchoolClassId", $"{cls.Name} is inactive. Choose an active class.");
            else className = cls.Name;
        }
        if (input.Category == WorkloadCategory.Class && input.SchoolClassId is null)
            result.AddError("SchoolClassId", "Class periods need the class they are taught in.");

        if (input.Category == WorkloadCategory.Duty && input.DutyAssignmentId is { } dutyId)
        {
            var duty = await db.DutyAssignments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dutyId, ct);
            if (duty is null) result.AddError("DutyAssignmentId", "This duty no longer exists.");
            else
            {
                if (SchoolWeek.StartOf(duty.DutyDate) != week)
                    result.AddError("DutyAssignmentId", "The duty is not in the selected week.");
                var asPrimary = duty.PrimaryStaffId == input.TeacherId;
                var asBackup = duty.BackupStaffId == input.TeacherId && duty.BackupStatus == BackupRequestStatus.Accepted;
                if (!asPrimary && !asBackup) result.AddError("DutyAssignmentId", "The teacher is not the assigned staff or the accepted backup for this duty.");
                // An absent teacher did not do the duty, so it is not counted in their workload.
                else if ((asPrimary && duty.PrimaryAttendance == AttendanceStatus.Absent) || (asBackup && duty.BackupAttendance == AttendanceStatus.Absent))
                    result.AddError("DutyAssignmentId", "The teacher was recorded absent for this duty, so it cannot be counted in their workload.");
            }
        }

        return (result, teacherName, className, slot);
    }

    private async Task<ServiceResult> CheckDuplicatesAsync(int? id, WorkloadInput input, DateOnly week, CancellationToken ct)
    {
        if (input.Category == WorkloadCategory.Class && input.SchoolClassId is { } classId)
        {
            var exists = await db.WorkloadAssignments.AnyAsync(w => w.Id != (id ?? 0) && w.TeacherId == input.TeacherId
                && w.WeekStart == week && w.Category == WorkloadCategory.Class && w.SchoolClassId == classId, ct);
            if (exists) return ServiceResult.Fail("SchoolClassId", "This teacher already has Class periods for this class in this week. Edit that assignment instead.");
            // The timetable already counts these sessions: a manual row would count them twice.
            var fromTimetable = await db.TimetableAssignments.Where(t => t.TeacherId == input.TeacherId && t.SchoolClassId == classId).ActiveIn(week).CountAsync(ct);
            if (fromTimetable > 0)
                return ServiceResult.Fail("SchoolClassId", $"This teacher's sessions for this class come from the class timetable ({fromTimetable} this week) and are already counted. Change them on the timetable.");
        }
        if (input.TimeSlotId is { } slotId && input.Day is { } day)
        {
            var taken = await db.WorkloadAssignments.AnyAsync(w => w.Id != (id ?? 0) && w.TeacherId == input.TeacherId && w.WeekStart == week && w.Day == day && w.TimeSlotId == slotId, ct);
            if (taken) return ServiceResult.Fail("TimeSlotId", "This teacher already has a period in this slot that week.");
        }
        if (input.Category == WorkloadCategory.Duty && input.DutyAssignmentId is { } dutyId)
        {
            var exists = await db.WorkloadAssignments.AnyAsync(w => w.Id != (id ?? 0) && w.TeacherId == input.TeacherId && w.DutyAssignmentId == dutyId, ct);
            if (exists) return ServiceResult.Fail("DutyAssignmentId", "This duty is already counted in the teacher's workload.");
        }
        return ServiceResult.Ok();
    }

    private void AddHistory(TeacherWorkloadAssignment a, WorkloadChange change, int? before, int? after, int totalAfter, string summary) =>
        db.WorkloadHistory.Add(NewHistory(a.Id, a.TeacherId, a.WeekStart, change, a.Category, before, after, totalAfter, summary));

    private WorkloadAssignmentHistory NewHistory(int assignmentId, int teacherId, DateOnly week, WorkloadChange change,
        WorkloadCategory category, int? before, int? after, int totalAfter, string summary) => new()
    {
        AssignmentId = assignmentId,
        TeacherId = teacherId,
        WeekStart = week,
        Change = change,
        Category = category,
        PeriodsBefore = before,
        PeriodsAfter = after,
        WeekTotalAfter = totalAfter,
        Summary = summary.Length > WorkloadAssignmentHistory.SummaryMaxLength ? summary[..WorkloadAssignmentHistory.SummaryMaxLength] : summary,
        ChangedAt = DateTime.UtcNow,
        ChangedBy = currentUser.Name,
    };

    private static string Periods(int n) => n == 1 ? "1 period" : $"{n} periods";

    private static string Detail(string? className, string? description)
    {
        var parts = new[] { className, description }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return parts.Count == 0 ? "" : $" ({string.Join(" · ", parts)})";
    }

    internal static string DutyLabel(DateOnly date, DutyComponent component, TimeOnly start, TimeOnly end) =>
        $"{date.ToString("ddd d MMM", CultureInfo.InvariantCulture)} · {DutyComponents.Name(component)} · {start:HH\\:mm}–{end:HH\\:mm}";

    private sealed record RawRow(
        int Id, int TeacherId, string TeacherName, DateOnly WeekStart, WorkloadCategory Category, int Periods,
        int? SchoolClassId, string? ClassName, int? DutyAssignmentId, DateOnly? DutyDate, DutyComponent? DutyComponent,
        TimeOnly? DutyStart, TimeOnly? DutyEnd, string? Description, DateTime CreatedAt, DateTime UpdatedAt, DayOfWeek? Day, int? SlotNumber, int? TimeSlotId);

    private static IQueryable<RawRow> Project(IQueryable<TeacherWorkloadAssignment> q) =>
        q.Select(w => new RawRow(w.Id, w.TeacherId, w.Teacher.FullName, w.WeekStart, w.Category, w.Periods,
            w.SchoolClassId, w.SchoolClass != null ? w.SchoolClass.Name : null,
            w.DutyAssignmentId,
            w.DutyAssignment != null ? w.DutyAssignment.DutyDate : null,
            w.DutyAssignment != null ? w.DutyAssignment.Component : null,
            w.DutyAssignment != null ? w.DutyAssignment.StartTime : null,
            w.DutyAssignment != null ? w.DutyAssignment.EndTime : null,
            w.Description, w.CreatedAt, w.UpdatedAt, w.Day, w.TimeSlot != null ? w.TimeSlot.Number : null, w.TimeSlotId));

    private static WorkloadAssignmentRow WithDutyLabel(RawRow r) =>
        new(r.Id, r.TeacherId, r.TeacherName, r.WeekStart, r.Category, r.Periods, r.SchoolClassId, r.ClassName,
            r.DutyAssignmentId,
            r.DutyDate is { } d && r.DutyComponent is { } c && r.DutyStart is { } s && r.DutyEnd is { } e ? DutyLabel(d, c, s, e) : null,
            r.Description, r.CreatedAt, r.UpdatedAt, r.Day, r.SlotNumber, r.TimeSlotId);
}
