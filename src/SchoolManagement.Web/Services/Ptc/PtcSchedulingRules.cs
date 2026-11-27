using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>
/// A subject a parent can choose for a class's PTC and its teacher: a timetable lesson of the class
/// (<see cref="SourceLessonId"/>, the lowest id of its lessons), for every class, KG included. <see cref="ClassTeacherId"/>
/// is only ever set on history (the former manual KG selections, no longer offered). <see cref="Key"/> identifies the
/// option; it is resolved again on the server when booking.
/// </summary>
public sealed record PtcSubjectOption(string Key, int? SourceLessonId, int? ClassTeacherId, int DepartmentId, string DepartmentName, string Subject,
    int TeacherId, string TeacherName)
{
    public bool IsKgSelection => ClassTeacherId is not null;
    public static string LessonKey(int lessonId) => "L" + lessonId.ToString(CultureInfo.InvariantCulture);
    public static string TeacherKey(int classTeacherId) => "K" + classTeacherId.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What keeps a teacher busy at a time.</summary>
public enum PtcConflictKind
{
    /// <summary>Another parent's PTC interview.</summary>
    Interview = 1,

    /// <summary>The teacher is a PTC substitute covering a class then.</summary>
    SubstituteCover = 2,

    /// <summary>The teacher's own timetable lesson that day (no conflict when an active PTC substitute covers it).</summary>
    Lesson = 3,

    /// <summary>A dated duty.</summary>
    Duty = 4,

    /// <summary>A single-week period placed in a slot (substitution, office hours).</summary>
    WorkloadPeriod = 5,
}

/// <summary>One conflict; <see cref="Covered"/> lessons are handled by a PTC substitute and do not block.</summary>
public sealed record PtcConflict(PtcConflictKind Kind, string Message, bool Covered = false)
{
    public bool Blocks => !Covered;
}

/// <summary>
/// The PTC rules that read existing THRYD data. Every class's subjects and teachers — all classes, KG and PreK included,
/// with no exception — come only from its timetable lessons (<see cref="TimetableAssignment"/>: class, department, subject,
/// teacher) current in the week of the PTC date. A Homeroom Teacher, Co-Teacher or department head meets parents only
/// when they teach a lesson of the class; former manual KG selections (<see cref="PtcClassTeacher"/>) are history and
/// never used. Nothing is invented.
/// </summary>
public static class PtcSchedulingRules
{
    /// <summary>
    /// The subjects a parent can choose for a class on a date: its timetable lessons that week (<paramref name="scheduleId"/>
    /// is accepted for callers' convenience and does not change the result). Empty when the class has no timetable lessons.
    /// </summary>
    public static async Task<IReadOnlyList<PtcSubjectOption>> ClassSubjectsAsync(SchoolDbContext db, int classId, DateOnly date, CancellationToken ct,
        int? scheduleId = null) =>
        (await ClassSubjectsAsync(db, [(classId, date, scheduleId)], ct))[classId];

    /// <summary>
    /// The subjects of many classes at once (one query): every requested class is in the result, with no options when it has
    /// no timetable lessons that week. The same rule for every class (KG included). Teachers must be current staff.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<PtcSubjectOption>>> ClassSubjectsAsync(SchoolDbContext db,
        IReadOnlyCollection<(int ClassId, DateOnly Date, int? ScheduleId)> classes, CancellationToken ct)
    {
        var requested = classes.GroupBy(c => c.ClassId).ToDictionary(g => g.Key, g => g.First());
        var result = requested.Keys.ToDictionary(id => id, _ => (IReadOnlyList<PtcSubjectOption>)[]);
        if (requested.Count == 0) return result;
        // Every class (KG and PreK included): its timetable lessons current that school week.
        var weeks = requested.Values.ToDictionary(c => c.ClassId, c => SchoolWeek.StartOf(c.Date));
        var lessonClasses = weeks.Keys.ToList();
        var first = weeks.Values.Min();
        var last = weeks.Values.Max();
        var lessons = await db.TimetableAssignments.AsNoTracking()
            .Where(t => lessonClasses.Contains(t.SchoolClassId) && t.Teacher.ArchivedAt == null && t.StartsOn <= last && (t.EndedOn == null || t.EndedOn > first))
            .Select(t => new { t.Id, t.SchoolClassId, t.StartsOn, t.EndedOn, t.DepartmentId, DepartmentName = t.Department.Name, t.Subject, t.TeacherId, TeacherName = t.Teacher.FullName })
            .ToListAsync(ct);
        foreach (var byClass in lessons.GroupBy(l => l.SchoolClassId))
        {
            var week = weeks[byClass.Key];
            result[byClass.Key] = byClass
                .Where(l => l.StartsOn <= week && (l.EndedOn == null || l.EndedOn > week))
                .GroupBy(l => (l.DepartmentId, Subject: l.Subject.Trim().ToUpperInvariant(), l.TeacherId))
                .Select(g => g.OrderBy(l => l.Id).First())
                .Select(l => new PtcSubjectOption(PtcSubjectOption.LessonKey(l.Id), l.Id, null, l.DepartmentId, l.DepartmentName, l.Subject, l.TeacherId, l.TeacherName))
                .OrderBy(o => o.DepartmentName).ThenBy(o => o.Subject).ThenBy(o => o.TeacherName)
                .ToList();
        }
        return result;
    }

    /// <summary>
    /// Everything that keeps <paramref name="staffId"/> busy on <paramref name="date"/> between <paramref name="start"/> and
    /// <paramref name="end"/> (ranges overlap when NewStart &lt; ExistingEnd and NewEnd &gt; ExistingStart). Only that date is
    /// read, so the same teacher at another class's PTC on another day is never a conflict. Interviews of
    /// <paramref name="excludeBookingId"/> (the same parent's booking) and the substitute assignment
    /// <paramref name="excludeSubstituteId"/> are ignored.
    /// </summary>
    public static async Task<IReadOnlyList<PtcConflict>> TeacherConflictsAsync(SchoolDbContext db, int staffId, DateOnly date, TimeOnly start, TimeOnly end,
        CancellationToken ct, int? excludeBookingId = null, int? excludeSubstituteId = null)
    {
        // One source of truth: the same availability the scheduling engine uses.
        var availability = await PtcTeacherAvailability.LoadAsync(db, [staffId], [date], ct);
        return availability.Blocking(staffId, date, start, end,
                b => (excludeBookingId is { } bk && b.BookingId == bk) || (excludeSubstituteId is { } sub && b.SubstituteId == sub))
            .Select(h => new PtcConflict(h.Interval.Kind, h.Message, h.Covered))
            .ToList();
    }

    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
}
