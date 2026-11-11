using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.Common;

/// <summary>
/// Rules shared by the timetable and the workload: school days, which timetable sessions count in a
/// week, time-overlap conflicts and the per-teacher lock that serialises every workload change.
/// </summary>
public static class ScheduleRules
{
    public static readonly DayOfWeek[] Days = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday];

    public static bool IsSchoolDay(DayOfWeek d) => d is >= DayOfWeek.Sunday and <= DayOfWeek.Thursday;

    public static string DayName(DayOfWeek d) => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(d);
    public static string DayShort(DayOfWeek d) => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d);

    /// <summary>The calendar date of a school day in a school week (weeks start on Sunday).</summary>
    public static DateOnly DateOf(DateOnly weekStart, DayOfWeek day) => weekStart.AddDays((int)day - (int)SchoolWeek.FirstDay);

    /// <summary>Sessions taught in the given week (started on or before it, not yet ended).</summary>
    public static IQueryable<TimetableAssignment> ActiveIn(this IQueryable<TimetableAssignment> q, DateOnly week) =>
        q.Where(t => t.StartsOn <= week && (t.EndedOn == null || t.EndedOn > week));

    public static Task<int> SessionsAsync(SchoolDbContext db, int teacherId, DateOnly week, CancellationToken ct) =>
        db.TimetableAssignments.Where(t => t.TeacherId == teacherId).ActiveIn(week).CountAsync(ct);

    /// <summary>Two periods clash when they are the same slot, or when both have times and the times overlap.</summary>
    public static bool Overlaps(int slotA, TimeOnly? startA, TimeOnly? endA, int slotB, TimeOnly? startB, TimeOnly? endB) =>
        slotA == slotB || (startA is { } sa && endA is { } ea && startB is { } sb && endB is { } eb && sa < eb && sb < ea);

    public static string Times(TimeOnly? start, TimeOnly? end) =>
        start is { } s && end is { } e ? $"{s.ToString("HH:mm", CultureInfo.InvariantCulture)}–{e.ToString("HH:mm", CultureInfo.InvariantCulture)}" : "time not set";

    /// <summary>
    /// The first thing that keeps a teacher busy in this day + slot, or null. <paramref name="week"/> = a single
    /// week (substitution, office hours); null = a weekly session from the current week on (checks every
    /// future week's slot periods and dated duties on that weekday too). Times are compared, not only slots.
    /// </summary>
    public static async Task<string?> TeacherConflictAsync(SchoolDbContext db, int teacherId, string teacherName, DayOfWeek day, TimeSlot slot,
        DateOnly? week, int? excludeSessionId, int? excludeWorkloadId, CancellationToken ct)
    {
        var slots = await db.TimeSlots.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        var current = SchoolWeek.Current;
        bool Clash(int otherSlot) => slots.TryGetValue(otherSlot, out var o) && Overlaps(slot.Id, slot.StartTime, slot.EndTime, o.Id, o.StartTime, o.EndTime);
        string Where(int otherSlot) => slots.TryGetValue(otherSlot, out var o) ? $"period {o.Number} ({Times(o.StartTime, o.EndTime)})" : "another period";

        // 1. Weekly teaching sessions.
        var sessions = db.TimetableAssignments.AsNoTracking().Where(t => t.TeacherId == teacherId && t.Day == day && t.Id != (excludeSessionId ?? 0));
        sessions = week is { } w ? sessions.ActiveIn(w) : sessions.Where(t => t.EndedOn == null);
        foreach (var s in await sessions.Select(t => new { t.TimeSlotId, ClassName = t.SchoolClass.Name, t.Subject }).ToListAsync(ct))
            if (Clash(s.TimeSlotId))
                return $"{teacherName} already teaches {s.ClassName} ({s.Subject}) on {DayName(day)}, {Where(s.TimeSlotId)}.";

        // 2. Single-week periods placed in a slot (substitutions, office hours).
        var periods = db.WorkloadAssignments.AsNoTracking().Where(x => x.TeacherId == teacherId && x.Day == day && x.TimeSlotId != null && x.Id != (excludeWorkloadId ?? 0));
        periods = week is { } pw ? periods.Where(x => x.WeekStart == pw) : periods.Where(x => x.WeekStart >= current);
        foreach (var p in await periods.Select(x => new { x.TimeSlotId, x.Category, x.WeekStart, ClassName = x.SchoolClass != null ? x.SchoolClass.Name : null }).ToListAsync(ct))
            if (Clash(p.TimeSlotId!.Value))
                return $"{teacherName} already has {(p.Category == WorkloadCategory.Substitution ? "a substitution" : "office hours")}{(p.ClassName is null ? "" : " in " + p.ClassName)} on {DayName(day)}, {Where(p.TimeSlotId.Value)}, in the week of {SchoolWeek.Label(p.WeekStart)}.";

        // 3. Duties (they have their own times): only comparable when the slot has times.
        if (slot.StartTime is { } start && slot.EndTime is { } end)
        {
            var from = week is { } dw ? DateOf(dw, day) : DateOf(current, day);
            var to = week is not null ? from : DateOnly.MaxValue;
            var duties = await db.DutyAssignments.AsNoTracking()
                .Where(d => d.DutyDate >= from && d.DutyDate <= to)
                .Where(d => d.PrimaryStaffId == teacherId || (d.BackupStaffId == teacherId && d.BackupStatus == BackupRequestStatus.Accepted))
                .Select(d => new { d.DutyDate, d.Component, d.StartTime, d.EndTime }).ToListAsync(ct);
            var clash = duties.FirstOrDefault(d => d.DutyDate.DayOfWeek == day && d.StartTime < end && start < d.EndTime);
            if (clash is not null)
                return $"{teacherName} has {DutyComponents.Name(clash.Component)} duty on {clash.DutyDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)}, {Times(clash.StartTime, clash.EndTime)}, which overlaps period {slot.Number} ({Times(start, end)}).";
        }
        return null;
    }

    /// <summary>
    /// Exclusive SQL Server application lock for one teacher's workload, held until the transaction ends.
    /// Workload changes and timetable changes take the same lock, so two requests cannot both pass the limit.
    /// </summary>
    public static Task LockTeacherAsync(SchoolDbContext db, int teacherId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; " +
            "IF @r < 0 THROW 51001, 'Workload lock timeout', 1;", [$"workload:{teacherId}"], ct);
}
