using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>
/// Counts over student school days: Present, Late, Absent (recorded) and Not Recorded (no record — never an absence).
/// Attendance % = (Present + Late) ÷ days with a record, so days nobody recorded neither help nor hurt.
/// </summary>
public sealed record AttendanceCounts(int Present, int Late, int Absent, int NotRecorded)
{
    public static readonly AttendanceCounts Zero = new(0, 0, 0, 0);
    public int Days => Present + Late + Absent + NotRecorded;
    public int Recorded => Present + Late + Absent;
    public int Attended => Present + Late;
    public double? Percent => Recorded == 0 ? null : Math.Round(Attended * 100.0 / Recorded, 1);
    public string PercentText => Percent is { } p ? p.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%" : "—";

    public static AttendanceCounts operator +(AttendanceCounts a, AttendanceCounts b) =>
        new(a.Present + b.Present, a.Late + b.Late, a.Absent + b.Absent, a.NotRecorded + b.NotRecorded);

    public static AttendanceCounts Of(ArrivalOutcome? o) => o switch
    {
        ArrivalOutcome.Present => new(1, 0, 0, 0),
        ArrivalOutcome.Late => new(0, 1, 0, 0),
        ArrivalOutcome.Absent => new(0, 0, 1, 0),
        _ => new(0, 0, 0, 1),
    };
}

/// <summary>One school day of one student.</summary>
public sealed record StudentDayRow(DateOnly Date, ArrivalOutcome? Outcome, TimeOnly? Time, string? Notes, string? RecordedBy, int Corrections, string? LastCorrection, string? ClassName);

public sealed record StudentAttendanceReport(
    int StudentId, string Name, string ClassName, DateOnly From, DateOnly To, AttendanceCounts Counts,
    IReadOnlyList<StudentDayRow> Days, IReadOnlyList<ArrivalCorrectionRow> Corrections);

/// <summary>A student's line in a class / grade / school report. On a single date <see cref="DayOutcome"/> and <see cref="DayTime"/> are that day's.</summary>
public sealed record StudentSummaryRow(int StudentId, string Name, int ClassId, string ClassName, AttendanceCounts Counts, ArrivalOutcome? DayOutcome, TimeOnly? DayTime);

public sealed record GroupRow(string Label, int Students, AttendanceCounts Counts);

public sealed record GroupAttendanceReport(
    string Scope, string Title, DateOnly From, DateOnly To, int SchoolDays, int Students, AttendanceCounts Counts,
    IReadOnlyList<GroupRow> ByGrade, IReadOnlyList<GroupRow> ByClass, IReadOnlyList<StudentSummaryRow> StudentRows)
{
    public bool SingleDay => From == To;
}

public sealed record AttendanceStudentPick(int Id, string Name, int ClassId, string ClassName);

public interface IAttendanceReportService
{
    Task<IReadOnlyList<AttendanceStudentPick>> StudentsAsync(CancellationToken ct = default);

    /// <summary>One student over a period: every applicable school day with its status, arrival time and corrections.</summary>
    Task<StudentAttendanceReport?> StudentAsync(int studentId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// A class (<paramref name="classId"/>), a grade (<paramref name="grade"/>) or the whole school over a period: totals,
    /// breakdowns by grade and class, and one line per student (optionally only students with <paramref name="status"/>).
    /// </summary>
    Task<GroupAttendanceReport> GroupAsync(int? classId, string? grade, DateOnly from, DateOnly to, string? status, CancellationToken ct = default);
}

/// <summary>
/// Attendance History &amp; Reports, read-only over the same School Attendance records (StudentArrivalRecords). School days come
/// from the school calendar (Sunday–Thursday, minus school holidays); a student counts from the day they were added, and no
/// day after today counts. A school day without a record is Absent when the cutoff rule covers it (from the first automatic day,
/// past the 09:30 cutoff — the same rule as Daily Attendance), otherwise Not Recorded (history before the rule).
/// </summary>
public sealed class AttendanceReportService(SchoolDbContext db, ISchoolTimeService? schoolTime = null, IArrivalService? arrivals = null) : IAttendanceReportService
{
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    /// <summary>Whether a school day with no record counts as Absent (automatic absence after the cutoff).</summary>
    private async Task<Func<DateOnly, bool>> AutomaticAbsenceAsync(CancellationToken ct)
    {
        if (arrivals is null) return _ => false;
        var rules = await arrivals.RulesAsync(ct);
        if (rules.AutomaticFrom is not { } from) return _ => false;
        var now = _time.Now;
        var today = DateOnly.FromDateTime(now);
        var past = TimeOnly.FromDateTime(now) >= rules.Cutoff;
        return d => d >= from && (d < today || (d == today && past));
    }
    public const int MaxDays = 400;

    public async Task<IReadOnlyList<AttendanceStudentPick>> StudentsAsync(CancellationToken ct = default) =>
        await db.Students.AsNoTracking().OrderBy(s => s.SchoolClass.SortOrder).ThenBy(s => s.FullName)
            .Select(s => new AttendanceStudentPick(s.Id, s.FullName, s.SchoolClassId, s.SchoolClass.Name)).ToListAsync(ct);

    public async Task<StudentAttendanceReport?> StudentAsync(int studentId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var student = await db.Students.AsNoTracking().Where(s => s.Id == studentId)
            .Select(s => new { s.Id, s.FullName, ClassName = s.SchoolClass.Name, s.CreatedAt }).FirstOrDefaultAsync(ct);
        if (student is null) return null;
        (from, to) = Clamp(from, to);
        var calendar = await SchoolDays.LoadAsync(db, ct);
        var first = Max(from, _time.LocalDate(student.CreatedAt));
        var records = await db.StudentArrivals.AsNoTracking().Where(r => r.StudentId == studentId && r.ArrivalDate >= from && r.ArrivalDate <= to)
            .Select(r => new
            {
                r.Id, r.ArrivalDate, r.Status, r.LateStatus, r.ArrivalTime, r.Notes, r.RecordedBy, ClassName = r.SchoolClass.Name,
                Corrections = r.Corrections.Count, Last = r.Corrections.OrderByDescending(c => c.CorrectedAt).Select(c => c.Reason).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var byDay = records.ToDictionary(r => r.ArrivalDate);
        var automatic = await AutomaticAbsenceAsync(ct);

        // Every applicable school day, plus any recorded day outside them (e.g. a record on a day later declared a holiday).
        var days = new List<StudentDayRow>();
        for (var d = first; d <= to; d = d.AddDays(1))
        {
            if (byDay.TryGetValue(d, out var r))
                days.Add(new(d, Arrivals.Outcome(r.Status, r.LateStatus), r.ArrivalTime, r.Notes, r.RecordedBy, r.Corrections, r.Last, r.ClassName));
            else if (calendar.IsSchoolDay(d))
                days.Add(automatic(d)
                    ? new(d, ArrivalOutcome.Absent, null, "No arrival recorded by the cutoff.", Arrivals.AutomaticRecorder, 0, null, null)
                    : new(d, null, null, null, null, 0, null, null));
        }
        var counts = days.Aggregate(AttendanceCounts.Zero, (acc, x) => acc + AttendanceCounts.Of(x.Outcome));
        var ids = records.Select(r => r.Id).ToList();
        var corrections = await db.StudentArrivalCorrections.AsNoTracking().Where(c => ids.Contains(c.StudentArrivalRecordId))
            .OrderByDescending(c => c.CorrectedAt)
            .Select(c => new ArrivalCorrectionRow(c.Id, c.StudentArrivalRecordId, c.Record.ArrivalDate, c.Before, c.After, c.Reason, c.CorrectedAt, c.CorrectedBy))
            .ToListAsync(ct);
        return new StudentAttendanceReport(student.Id, student.FullName, student.ClassName, from, to, counts, days, corrections);
    }

    public async Task<GroupAttendanceReport> GroupAsync(int? classId, string? grade, DateOnly from, DateOnly to, string? status, CancellationToken ct = default)
    {
        (from, to) = Clamp(from, to);
        var calendar = await SchoolDays.LoadAsync(db, ct);
        var schoolDays = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1)) if (calendar.IsSchoolDay(d)) schoolDays.Add(d);

        var q = db.Students.AsNoTracking();
        if (classId is { } c) q = q.Where(s => s.SchoolClassId == c);
        var students = (await q.OrderBy(s => s.SchoolClass.SortOrder).ThenBy(s => s.FullName)
                .Select(s => new { s.Id, s.FullName, s.SchoolClassId, ClassName = s.SchoolClass.Name, s.CreatedAt }).ToListAsync(ct))
            .Where(s => grade is null || GradeCatalog.KeyOf(s.ClassName) == grade)
            .ToList();
        var ids = students.Select(s => s.Id).ToList();
        var records = (await db.StudentArrivals.AsNoTracking().Where(r => r.ArrivalDate >= from && r.ArrivalDate <= to && (classId == null && grade == null || ids.Contains(r.StudentId)))
                .Select(r => new { r.StudentId, r.ArrivalDate, r.Status, r.LateStatus, r.ArrivalTime }).ToListAsync(ct))
            .Where(r => ids.Contains(r.StudentId))
            .ToLookup(r => r.StudentId);

        var automatic = await AutomaticAbsenceAsync(ct);
        ArrivalOutcome? Missing(DateOnly d) => automatic(d) ? ArrivalOutcome.Absent : null;
        var rows = new List<StudentSummaryRow>();
        foreach (var s in students)
        {
            var since = _time.LocalDate(s.CreatedAt);
            var mine = records[s.Id].ToDictionary(r => r.ArrivalDate);
            var counts = AttendanceCounts.Zero;
            foreach (var d in schoolDays.Where(d => d >= since))
                counts += AttendanceCounts.Of(mine.TryGetValue(d, out var r) ? Arrivals.Outcome(r.Status, r.LateStatus) : Missing(d));
            // A record on a non-school day (rare) still counts.
            foreach (var r in mine.Values.Where(r => !calendar.IsSchoolDay(r.ArrivalDate))) counts += AttendanceCounts.Of(Arrivals.Outcome(r.Status, r.LateStatus));
            var day = from == to && mine.TryGetValue(from, out var only) ? only : null;
            if (counts.Days == 0 && !(from == to && calendar.IsSchoolDay(from) && since <= from)) continue; // not yet a student in this period
            rows.Add(new StudentSummaryRow(s.Id, s.FullName, s.SchoolClassId, s.ClassName, counts,
                day is not null ? Arrivals.Outcome(day.Status, day.LateStatus) : from == to && calendar.IsSchoolDay(from) && since <= from ? Missing(from) : null,
                day?.ArrivalTime));
        }

        var total = rows.Aggregate(AttendanceCounts.Zero, (acc, r) => acc + r.Counts);
        var byGrade = rows.GroupBy(r => GradeCatalog.KeyOf(r.ClassName) ?? r.ClassName)
            .Select(g => new GroupRow(GradeCatalog.Label(g.Key), g.Count(), g.Aggregate(AttendanceCounts.Zero, (acc, r) => acc + r.Counts)))
            .ToList();
        var byClass = rows.GroupBy(r => (r.ClassId, r.ClassName))
            .Select(g => new GroupRow(g.Key.ClassName, g.Count(), g.Aggregate(AttendanceCounts.Zero, (acc, r) => acc + r.Counts)))
            .ToList();

        var filtered = status switch
        {
            "present" => rows.Where(r => from == to ? r.DayOutcome == ArrivalOutcome.Present : r.Counts.Present > 0),
            "late" => rows.Where(r => from == to ? r.DayOutcome == ArrivalOutcome.Late : r.Counts.Late > 0),
            "absent" => rows.Where(r => from == to ? r.DayOutcome == ArrivalOutcome.Absent : r.Counts.Absent > 0),
            "notrecorded" => rows.Where(r => r.Counts.NotRecorded > 0),
            _ => rows,
        };

        var scope = classId is not null ? "class" : grade is not null ? "grade" : "school";
        var title = scope switch
        {
            "class" => students.FirstOrDefault()?.ClassName ?? await db.Classes.Where(x => x.Id == classId).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "Class",
            "grade" => GradeCatalog.Label(grade),
            _ => "Whole school",
        };
        return new GroupAttendanceReport(scope, title, from, to, schoolDays.Count, rows.Count, total, byGrade, byClass, filtered.ToList());
    }

    /// <summary>In order, not after today, at most <see cref="MaxDays"/> days.</summary>
    private (DateOnly, DateOnly) Clamp(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);
        if (to > _time.Today) to = _time.Today;
        if (from > to) from = to;
        if (to.DayNumber - from.DayNumber >= MaxDays) from = to.AddDays(-(MaxDays - 1));
        return (from, to);
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
