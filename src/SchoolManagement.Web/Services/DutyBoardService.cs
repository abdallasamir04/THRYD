using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

// ============================================================ Time-based duty status
//
// Every status is worked out on the server from the school clock (Africa/Cairo) and the records; nobody selects it:
//   PENDING      before the scheduled start,
//   IN PROGRESS  between start and end,
//   COVERED      after the end, when coverage was recorded (someone on duty recorded present) and nothing reports it
//                not covered — for a Sub-Duty: every coverage point covered; for a Main Duty: every required Sub-Duty,
//   NOT COVERED  after the end otherwise. Optional Sub-Duties never decide the Main Duty.

/// <summary>Who covered a point and when: from the duty's audit trail (the attendance that made it covered).</summary>
public sealed record DutyCoverageStamp(DateTime At, string? By);

/// <summary>A report that a responsibility was not covered (current version or an earlier one).</summary>
public sealed record DutyReportView(
    int Id, DateOnly Date, DutyComponent Component, int? PointId, string Area, DutyUncoveredReason Reason, string? Notes,
    DateTime RecordedAt, string? RecordedBy, bool Withdrawn, int? SupersedesId, bool Current)
{
    public string ReasonLabel => DutyReasons.Label(Reason);
}

/// <summary>One coverage point (a duty record: a gate, a building, a class group…) with its computed status.</summary>
public sealed record DutyPointView(
    int Id, DutyComponent Component, string Description, string? Location, TimeOnly Start, TimeOnly End, DutyStatus Status,
    int PrimaryStaffId, string PrimaryName, string? BackupName, CoverageState Coverage, DutyCoverageStamp? CoveredAt,
    string? Notes, IReadOnlyList<string> Classes, DutyReportView? Report, string? Reason)
{
    /// <summary>The area this point covers: its location, else its description.</summary>
    public string Area => string.IsNullOrWhiteSpace(Location) ? Description : $"{Location} — {Description}";
    public string Staff => BackupName is null ? PrimaryName : $"{PrimaryName} · backup {BackupName}";
}

/// <summary>A Sub-Duty on a date: its schedule (configured, or taken from its records on days before it was configured) and coverage.</summary>
public sealed record SubDutyView(
    DutyComponentInfo Info, bool Scheduled, bool Configured, TimeOnly Start, TimeOnly End, bool Required,
    int? DepartmentId, string? DepartmentName, DutyStatus Status, IReadOnlyList<DutyPointView> Points,
    IReadOnlyList<DutyReportView> AreaReports, string? Reason)
{
    public string Times => ScheduleRules.Times12(Start, End);
    public IReadOnlyList<string> Staff => Points.Select(p => p.PrimaryName).Concat(Points.Where(p => p.BackupName != null).Select(p => p.BackupName!)).Distinct().ToList();
    public IReadOnlyList<DutyPointView> UncoveredPoints => Points.Where(p => p.Status == DutyStatus.NotCovered).ToList();
}

/// <summary>A Main Duty on a date: its overall time range, status and Sub-Duties.</summary>
public sealed record MainDutyView(DutyType Type, bool Scheduled, bool Configured, TimeOnly? Start, TimeOnly? End, DutyStatus Status, IReadOnlyList<SubDutyView> Subs)
{
    public string Name => DutyComponents.TypeName(Type);
    public string Times => ScheduleRules.Times12(Start, End);
    public IReadOnlyList<SubDutyView> Scheduled_ => Subs.Where(s => s.Scheduled).ToList();
    public IReadOnlyList<SubDutyView> Required => Subs.Where(s => s.Scheduled && s.Required).ToList();
    public IReadOnlyList<SubDutyView> Optional => Subs.Where(s => s.Scheduled && !s.Required).ToList();
    public int RequiredCovered => Required.Count(s => s.Status == DutyStatus.Covered);
    public int RequiredNotCovered => Required.Count(s => s.Status == DutyStatus.NotCovered);
    public int RequiredOpen => Required.Count(s => s.Status is DutyStatus.Pending or DutyStatus.InProgress);

    /// <summary>Covered ÷ ended required Sub-Duties; null before any has ended.</summary>
    public double? Percent => RequiredCovered + RequiredNotCovered == 0 ? null : Math.Round(RequiredCovered * 100.0 / (RequiredCovered + RequiredNotCovered));
    public IReadOnlyList<string> Departments => Scheduled_.Where(s => s.DepartmentName != null).Select(s => s.DepartmentName!).Distinct().ToList();
    public IReadOnlyList<string> Staff => Scheduled_.SelectMany(s => s.Staff).Distinct().ToList();
}

public sealed record DutyDay(DateOnly Date, DateTime Now, bool IsToday, bool IsSchoolDay, string? Holiday, IReadOnlyList<MainDutyView> Duties)
{
    public MainDutyView Get(DutyType t) => Duties.First(d => d.Type == t);
    public IEnumerable<SubDutyView> AllSubs => Duties.SelectMany(d => d.Subs);
}

/// <summary>A department's responsibilities on a date (the Sub-Duties whose schedule names it).</summary>
public sealed record DutyDepartmentView(int? DepartmentId, string Name, IReadOnlyList<(MainDutyView Main, SubDutyView Sub)> Subs)
{
    /// <summary>From the department's required responsibilities: any not covered → Not Covered; all covered → Covered; else open.</summary>
    public DutyStatus Status
    {
        get
        {
            var req = Subs.Where(x => x.Sub.Required).Select(x => x.Sub.Status).ToList();
            if (req.Count == 0) return DutyStatus.Pending;
            if (req.Contains(DutyStatus.NotCovered)) return DutyStatus.NotCovered;
            if (req.All(s => s == DutyStatus.Covered)) return DutyStatus.Covered;
            return req.Contains(DutyStatus.InProgress) ? DutyStatus.InProgress : DutyStatus.Pending;
        }
    }
}

public static class DutyReasons
{
    public static readonly DutyUncoveredReason[] All =
    [
        DutyUncoveredReason.StaffAbsent, DutyUncoveredReason.NoReplacement, DutyUncoveredReason.NotCompleted,
        DutyUncoveredReason.AreaUnattended, DutyUncoveredReason.OperationalIssue, DutyUncoveredReason.Other,
    ];

    public static string Label(DutyUncoveredReason r) => r switch
    {
        DutyUncoveredReason.StaffAbsent => "Assigned staff member absent",
        DutyUncoveredReason.NoReplacement => "No replacement available",
        DutyUncoveredReason.NotCompleted => "Coverage not completed",
        DutyUncoveredReason.AreaUnattended => "Area left unattended",
        DutyUncoveredReason.OperationalIssue => "Operational issue",
        _ => "Other",
    };

    /// <summary>
    /// What the records say about a point that was not covered (never invented): the staffing recorded, or
    /// "Coverage was not recorded." when nothing was.
    /// </summary>
    public static string FromRecords(CoverageState c) => c switch
    {
        CoverageState.NeedsBackup => "Assigned staff member recorded absent; no backup was assigned.",
        CoverageState.AwaitingBackupResponse => "Assigned staff member recorded absent; the backup request was not answered.",
        CoverageState.BackupRejected => "Assigned staff member recorded absent; the backup declined.",
        CoverageState.AwaitingBackupAttendance => "Assigned staff member recorded absent; the backup's attendance was not recorded.",
        CoverageState.BackupAbsent => "Assigned staff member and backup both recorded absent.",
        _ => "Coverage was not recorded.",
    };
}

/// <summary>The status rules, shared by every page, dashboard and report.</summary>
public static class DutyTiming
{
    /// <summary>
    /// A coverage point: Pending before its start, In Progress until its end, then Covered when someone on duty was recorded
    /// present and it is not reported (or was not marked) not covered; otherwise Not Covered.
    /// </summary>
    public static DutyStatus Point(DateOnly date, TimeOnly start, TimeOnly end, DateOnly today, TimeOnly now, CoverageState coverage, bool reportedNotCovered)
    {
        if (date > today || (date == today && now < start)) return DutyStatus.Pending;
        if (date == today && now < end) return DutyStatus.InProgress;
        return !reportedNotCovered && Coverage.IsCovered(coverage) ? DutyStatus.Covered : DutyStatus.NotCovered;
    }

    /// <summary>Before / during a time range: Pending or In Progress; null once it has ended.</summary>
    public static DutyStatus? Running(DateOnly date, TimeOnly start, TimeOnly end, DateOnly today, TimeOnly now) =>
        date > today || (date == today && now < start) ? DutyStatus.Pending
        : date == today && now < end ? DutyStatus.InProgress
        : null;
}

public sealed record SectionInput(
    string? Name, string? Summary, DateOnly EffectiveFrom, TimeOnly? Start, TimeOnly? End, bool Required, int? DepartmentId, int DayMask);

/// <summary>A Sub-Duty created with a new Main Duty (times default to the Main Duty's).</summary>
public sealed record NewSubDutyInput(string? Name, TimeOnly? Start, TimeOnly? End, bool Required);

/// <summary>Duties → Add Duty: a new Main Duty with its schedule and, optionally, its first Sub-Duties.</summary>
public sealed record NewMainDutyInput(
    string? Name, string? Summary, string? Icon, DateOnly EffectiveFrom, TimeOnly? Start, TimeOnly? End, int DayMask, IReadOnlyList<NewSubDutyInput> SubDuties);

public sealed record CoverageReportInput(DateOnly Date, DutyComponent Component, int? PointId, string? Area, DutyUncoveredReason? Reason, string? Notes, int? SupersedesId);

/// <summary>Schedule versions of a Main Duty and its Sub-Duties, for the schedule page.</summary>
public sealed record DutyScheduleView(
    DutyType Type, DateOnly AsOf, DutyMainSchedule? Main, IReadOnlyList<DutyMainSchedule> MainHistory,
    IReadOnlyList<(DutyComponentInfo Info, DutySectionSchedule? Current, IReadOnlyList<DutySectionSchedule> History)> Sections);

public interface IDutyBoardService
{
    /// <summary>Every Main Duty on a date with its Sub-Duties, coverage points and computed statuses.</summary>
    Task<DutyDay> DayAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>Sub-Duties grouped by their responsible department on a date.</summary>
    Task<IReadOnlyList<DutyDepartmentView>> DepartmentsAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>Computed status of these coverage points (staff records, dashboards, audit lists).</summary>
    Task<IReadOnlyDictionary<int, DutyStatus>> PointStatusesAsync(IReadOnlyCollection<int> pointIds, CancellationToken ct = default);

    Task<DutyScheduleView> ScheduleAsync(DutyType type, CancellationToken ct = default);
    Task<ServiceResult> SaveMainScheduleAsync(DutyType type, DateOnly effectiveFrom, TimeOnly? start, TimeOnly? end, int dayMask, CancellationToken ct = default);
    Task<ServiceResult> SaveSectionAsync(DutyComponent component, SectionInput input, CancellationToken ct = default);
    Task<ServiceResult> AddSectionAsync(DutyType type, SectionInput input, CancellationToken ct = default);

    /// <summary>
    /// Duties → Add Duty (Super Admin only): a new Main Duty — name, description, icon, its time range and days from a date —
    /// with its first Sub-Duties, all saved together or not at all. The id of the new duty's <see cref="DutyType"/> on success.
    /// </summary>
    Task<ServiceResult> CreateMainDutyAsync(NewMainDutyInput input, CancellationToken ct = default);
    Task<ServiceResult> RetireSectionAsync(DutyComponent component, DateOnly from, CancellationToken ct = default);
    Task<ServiceResult> RestoreSectionAsync(DutyComponent component, CancellationToken ct = default);

    /// <summary>The schedule of a Sub-Duty in force on a date (null: not configured, or not on that weekday).</summary>
    Task<DutySectionSchedule?> SectionScheduleOnAsync(DutyComponent component, DateOnly date, CancellationToken ct = default);

    Task<ServiceResult> ReportUncoveredAsync(CoverageReportInput input, CancellationToken ct = default);
    Task<ServiceResult> WithdrawReportAsync(int reportId, CancellationToken ct = default);
    Task<IReadOnlyList<DutyReportView>> ReportHistoryAsync(DateOnly date, DutyComponent component, CancellationToken ct = default);
    Task<IReadOnlyList<(int Id, string Name)>> DepartmentOptionsAsync(CancellationToken ct = default);
}

/// <summary>
/// Duty scheduling, time-based status and coverage reporting over the existing duty records (DutyAssignments with their
/// staff, attendance, backups and audit trail). Coverage is what the records show — someone on duty recorded present —
/// never an assignment alone. Schedules are versioned by effective date, so earlier days keep the schedule they had.
/// </summary>
public sealed class DutyBoardService(SchoolDbContext db, ICurrentUser currentUser, ISchoolTimeService? schoolTime = null) : IDutyBoardService
{
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    // ------------------------------------------------------------------ reads

    private sealed record PointRow(
        int Id, DutyComponent Component, string Description, string? Location, TimeOnly Start, TimeOnly End, DutyStatus Stored,
        int PrimaryStaffId, string PrimaryName, AttendanceStatus PrimaryAttendance, int? BackupStaffId, string? BackupName,
        BackupRequestStatus BackupStatus, AttendanceStatus BackupAttendance, string? Notes, string? Issues, string? AuditNotes, List<string> Classes);

    public async Task<DutyDay> DayAsync(DateOnly date, CancellationToken ct = default)
    {
        var today = _time.Today;
        var now = _time.TimeOfDay;
        var days = await SchoolDays.LoadAsync(db, ct);
        var schoolDay = days.IsSchoolDay(date);

        var mains = (await db.DutyMainSchedules.AsNoTracking().Where(m => m.EffectiveFrom <= date).ToListAsync(ct))
            .GroupBy(m => m.DutyType).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.EffectiveFrom).ThenByDescending(m => m.Id).First());
        var versions = (await db.DutySectionSchedules.AsNoTracking().Include(s => s.Department).Where(s => s.EffectiveFrom <= date).ToListAsync(ct))
            .GroupBy(s => s.DutySectionId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.Id).First());

        var points = await db.DutyAssignments.AsNoTracking().Where(d => d.DutyDate == date).OrderBy(d => d.StartTime).ThenBy(d => d.Id)
            .Select(d => new PointRow(d.Id, d.Component, d.Description, d.Location, d.StartTime, d.EndTime, d.Status,
                d.PrimaryStaffId, d.PrimaryStaff.FullName, d.PrimaryAttendance, d.BackupStaffId, d.BackupStaff != null ? d.BackupStaff.FullName : null,
                d.BackupStatus, d.BackupAttendance, d.Notes, d.IssuesEncountered, d.AuditNotes,
                d.Classes.OrderBy(c => c.SchoolClass.SortOrder).Select(c => c.SchoolClass.Name).ToList()))
            .ToListAsync(ct);
        var reports = Current(await ReportsAsync(r => r.DutyDate == date, ct));
        var ids = points.Select(p => p.Id).ToList();
        var stamps = await StampsAsync(ids, ct);

        var duties = new List<MainDutyView>();
        foreach (var type in DutyComponents.Types)
        {
            var subs = new List<SubDutyView>();
            foreach (var info in DutyComponents.ForType(type))
            {
                var mine = points.Where(p => p.Component == info.Component).ToList();
                versions.TryGetValue((int)info.Component, out var v);
                var applies = v is not null && info.ActiveOn(date) && schoolDay && PeriodDays.Applies(v.DayMask, date.DayOfWeek);
                if (!applies && mine.Count == 0)
                {
                    if (info.ActiveOn(date)) subs.Add(new SubDutyView(info, false, v is not null, v?.StartTime ?? default, v?.EndTime ?? default, v?.IsRequired ?? true,
                        v?.DepartmentId, v?.Department?.Name, DutyStatus.Pending, [], [], null));
                    continue;
                }
                var start = applies ? v!.StartTime : mine.Min(p => p.Start);
                var end = applies ? v!.EndTime : mine.Max(p => p.End);
                var myReports = reports.Where(r => r.Component == info.Component).ToList();
                var pointViews = mine.Select(p =>
                {
                    var coverage = Coverage.Of(p.PrimaryAttendance, p.BackupStaffId, p.BackupStatus, p.BackupAttendance);
                    var report = myReports.FirstOrDefault(r => r.PointId == p.Id);
                    var legacy = p.Stored == DutyStatus.NotCovered; // marked Not Covered before statuses were automatic
                    var status = DutyTiming.Point(date, p.Start, p.End, today, now, coverage, report is not null || legacy);
                    string? reason = status != DutyStatus.NotCovered ? null
                        : report is not null ? null
                        : legacy ? "Marked Not Covered" + (FirstText(p.Issues, p.AuditNotes) is { } t ? $": {t}" : ".")
                        : DutyReasons.FromRecords(coverage);
                    return new DutyPointView(p.Id, p.Component, p.Description, p.Location, p.Start, p.End, status, p.PrimaryStaffId, p.PrimaryName,
                        p.BackupName, coverage, status == DutyStatus.Covered ? stamps.GetValueOrDefault(p.Id) : null, p.Notes, p.Classes, report, reason);
                }).ToList();
                var areaReports = myReports.Where(r => r.PointId is null).ToList();
                var effectiveEnd = pointViews.Count == 0 ? end : new[] { end, pointViews.Max(p => p.End) }.Max();
                var status = DutyTiming.Running(date, start, effectiveEnd, today, now)
                    ?? (pointViews.Count == 0 || areaReports.Count > 0 || pointViews.Any(p => p.Status == DutyStatus.NotCovered) ? DutyStatus.NotCovered : DutyStatus.Covered);
                string? subReason = status != DutyStatus.NotCovered ? null
                    : pointViews.Count == 0 ? "No staff were assigned to it, so coverage was not recorded."
                    : null;
                subs.Add(new SubDutyView(info, true, applies, start, end, applies ? v!.IsRequired : true, applies ? v!.DepartmentId : null,
                    applies ? v!.Department?.Name : null, status, pointViews, areaReports, subReason));
            }

            var scheduled = subs.Where(s => s.Scheduled).ToList();
            mains.TryGetValue(type, out var m);
            var mainApplies = m is not null && schoolDay && PeriodDays.Applies(m.DayMask, date.DayOfWeek);
            if (scheduled.Count == 0)
            {
                duties.Add(new MainDutyView(type, false, m is not null, m?.StartTime, m?.EndTime, DutyStatus.Pending, subs));
                continue;
            }
            var mStart = mainApplies ? m!.StartTime : scheduled.Min(s => s.Start);
            var mEnd = mainApplies ? m!.EndTime : scheduled.Max(s => s.End);
            var required = scheduled.Where(s => s.Required).ToList();
            // Never Covered before the Main Duty's own end; after it, only when every required Sub-Duty is Covered.
            var mainStatus = DutyTiming.Running(date, mStart, mEnd, today, now)
                ?? (required.Any(s => s.Status == DutyStatus.NotCovered) ? DutyStatus.NotCovered
                    : required.Any(s => s.Status is DutyStatus.Pending or DutyStatus.InProgress) ? DutyStatus.InProgress
                    : DutyStatus.Covered);
            duties.Add(new MainDutyView(type, true, mainApplies, mStart, mEnd, mainStatus, subs));
        }
        return new DutyDay(date, _time.Now, date == today, schoolDay, days.HolidayOn(date)?.Name, duties);
    }

    public async Task<IReadOnlyList<DutyDepartmentView>> DepartmentsAsync(DateOnly date, CancellationToken ct = default)
    {
        var day = await DayAsync(date, ct);
        var all = day.Duties.SelectMany(m => m.Subs.Where(s => s.Scheduled).Select(s => (Main: m, Sub: s))).ToList();
        var departments = await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var list = departments.Select(d => new DutyDepartmentView(d.Id, d.Name, all.Where(x => x.Sub.DepartmentId == d.Id).ToList()))
            .Where(d => d.Subs.Count > 0).ToList();
        var none = all.Where(x => x.Sub.DepartmentId is null).ToList();
        if (none.Count > 0) list.Add(new DutyDepartmentView(null, "No department assigned", none));
        return list;
    }

    public async Task<IReadOnlyDictionary<int, DutyStatus>> PointStatusesAsync(IReadOnlyCollection<int> pointIds, CancellationToken ct = default)
    {
        if (pointIds.Count == 0) return new Dictionary<int, DutyStatus>();
        var today = _time.Today;
        var now = _time.TimeOfDay;
        var rows = await db.DutyAssignments.AsNoTracking().Where(d => pointIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DutyDate, d.StartTime, d.EndTime, d.Status, d.PrimaryAttendance, d.BackupStaffId, d.BackupStatus, d.BackupAttendance })
            .ToListAsync(ct);
        var reported = Current(await ReportsAsync(r => r.DutyAssignmentId != null && pointIds.Contains(r.DutyAssignmentId.Value), ct))
            .Select(r => r.PointId!.Value).ToHashSet();
        return rows.ToDictionary(r => r.Id, r => DutyTiming.Point(r.DutyDate, r.StartTime, r.EndTime, today, now,
            Coverage.Of(r.PrimaryAttendance, r.BackupStaffId, r.BackupStatus, r.BackupAttendance), reported.Contains(r.Id) || r.Status == DutyStatus.NotCovered));
    }

    private async Task<List<DutyReportView>> ReportsAsync(System.Linq.Expressions.Expression<Func<DutyCoverageReport, bool>> where, CancellationToken ct)
    {
        var rows = await db.DutyCoverageReports.AsNoTracking().Where(where).OrderBy(r => r.Id).ToListAsync(ct);
        var superseded = await db.DutyCoverageReports.AsNoTracking().Where(r => r.SupersedesId != null).Select(r => r.SupersedesId!.Value).ToListAsync(ct);
        var replaced = superseded.ToHashSet();
        return rows.Select(r => new DutyReportView(r.Id, r.DutyDate, r.Component, r.DutyAssignmentId, r.Area, r.Reason, r.Notes, r.RecordedAt, r.RecordedBy,
            r.Withdrawn, r.SupersedesId, !replaced.Contains(r.Id) && !r.Withdrawn)).ToList();
    }

    private static List<DutyReportView> Current(List<DutyReportView> all) => all.Where(r => r.Current).ToList();

    /// <summary>When (and by whom) coverage was recorded: the latest attendance entry of each point's audit trail.</summary>
    private async Task<Dictionary<int, DutyCoverageStamp>> StampsAsync(List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        DutyAuditAction[] actions = [DutyAuditAction.Created, DutyAuditAction.Updated, DutyAuditAction.AttendanceRecorded, DutyAuditAction.BackupAttendanceRecorded];
        var rows = await db.DutyAudits.AsNoTracking().Where(a => ids.Contains(a.DutyAssignmentId) && actions.Contains(a.Action))
            .Select(a => new { a.DutyAssignmentId, a.ChangedAt, a.ChangedBy }).ToListAsync(ct);
        return rows.GroupBy(r => r.DutyAssignmentId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ChangedAt).Select(r => new DutyCoverageStamp(r.ChangedAt, r.ChangedBy)).First());
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    // ------------------------------------------------------------------ schedules

    public async Task<DutyScheduleView> ScheduleAsync(DutyType type, CancellationToken ct = default)
    {
        var today = _time.Today;
        var mainHistory = await db.DutyMainSchedules.AsNoTracking().Where(m => m.DutyType == type)
            .OrderByDescending(m => m.EffectiveFrom).ThenByDescending(m => m.Id).ToListAsync(ct);
        var sections = DutyComponents.ForType(type).Select(i => (int)i.Component).ToList();
        var versions = await db.DutySectionSchedules.AsNoTracking().Include(s => s.Department).Where(s => sections.Contains(s.DutySectionId))
            .OrderByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.Id).ToListAsync(ct);
        return new DutyScheduleView(type, today, InForce(mainHistory, today, m => m.EffectiveFrom), mainHistory,
            DutyComponents.ForType(type).Select(i =>
            {
                var mine = versions.Where(v => v.DutySectionId == (int)i.Component).ToList();
                return (i, InForce(mine, today, v => v.EffectiveFrom) ?? mine.LastOrDefault(), (IReadOnlyList<DutySectionSchedule>)mine);
            }).ToList());
    }

    /// <summary>The version in force on a date: the latest one effective on or before it (lists are newest first).</summary>
    private static T? InForce<T>(IReadOnlyList<T> newestFirst, DateOnly date, Func<T, DateOnly> from) where T : class =>
        newestFirst.FirstOrDefault(x => from(x) <= date);

    public async Task<DutySectionSchedule?> SectionScheduleOnAsync(DutyComponent component, DateOnly date, CancellationToken ct = default)
    {
        var v = await db.DutySectionSchedules.AsNoTracking().Where(s => s.DutySectionId == (int)component && s.EffectiveFrom <= date)
            .OrderByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        return v is not null && PeriodDays.Applies(v.DayMask, date.DayOfWeek) ? v : null;
    }

    private async Task<DutyMainSchedule?> MainOnAsync(DutyType type, DateOnly date, CancellationToken ct) =>
        await db.DutyMainSchedules.AsNoTracking().Where(m => m.DutyType == type && m.EffectiveFrom <= date)
            .OrderByDescending(m => m.EffectiveFrom).ThenByDescending(m => m.Id).FirstOrDefaultAsync(ct);

    private ServiceResult? EffectiveProblem(DateOnly from) =>
        from < _time.Today ? ServiceResult.Fail("EffectiveFrom", "Changes take effect today or later: earlier days keep the schedule they had.") : null;

    public async Task<ServiceResult> SaveMainScheduleAsync(DutyType type, DateOnly effectiveFrom, TimeOnly? start, TimeOnly? end, int dayMask, CancellationToken ct = default)
    {
        if (!DutyComponents.Types.Contains(type)) return ServiceResult.Fail("", "Unknown duty.");
        if (EffectiveProblem(effectiveFrom) is { } p) return p;
        if (start is null) return ServiceResult.Fail("StartTime", "Choose the start time.");
        if (end is null) return ServiceResult.Fail("EndTime", "Choose the end time.");
        if (end <= start) return ServiceResult.Fail("EndTime", "The end time must be after the start time.");
        // The Sub-Duties in force from that date must still fit inside the new range.
        var outside = new List<string>();
        foreach (var info in DutyComponents.ForType(type).Where(i => i.ActiveOn(effectiveFrom)))
        {
            var v = await db.DutySectionSchedules.AsNoTracking().Where(s => s.DutySectionId == (int)info.Component && s.EffectiveFrom <= effectiveFrom)
                .OrderByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.Id).FirstOrDefaultAsync(ct);
            if (v is not null && (v.StartTime < start || v.EndTime > end)) outside.Add($"{info.Name} ({ScheduleRules.Times12(v.StartTime, v.EndTime)})");
        }
        if (outside.Count > 0)
            return ServiceResult.Fail("StartTime", $"These Sub-Duties would fall outside {ScheduleRules.Times12(start, end)}: {string.Join(", ", outside)}. Change them first, or choose a wider range.");
        db.DutyMainSchedules.Add(new DutyMainSchedule
        {
            DutyType = type, EffectiveFrom = effectiveFrom, StartTime = start.Value, EndTime = end.Value, DayMask = dayMask,
            CreatedAt = _time.UtcNow, CreatedBy = currentUser.Name,
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult> CheckSectionAsync(DutyType type, SectionInput input, bool naming, int? self, CancellationToken ct)
    {
        var r = ServiceResult.Ok();
        if (EffectiveProblem(input.EffectiveFrom) is { } p) return p;
        if (naming)
        {
            var name = TextNormalizer.CleanText(input.Name);
            if (name.Length == 0) r.AddError("Name", "Name the Sub-Duty.");
            else if (name.Length > DutySection.NameMaxLength) r.AddError("Name", $"Use {DutySection.NameMaxLength} characters or fewer.");
            else if (await db.DutySections.AnyAsync(s => s.Name == name && s.DutyType == type && s.Id != (self ?? 0), ct))
                r.AddError("Name", "Another Sub-Duty of this duty already has this name.");
            if (TextNormalizer.CleanText(input.Summary).Length > DutySection.SummaryMaxLength) r.AddError("Summary", $"Use {DutySection.SummaryMaxLength} characters or fewer.");
        }
        if (input.Start is null) r.AddError("StartTime", "Choose the start time.");
        if (input.End is null) r.AddError("EndTime", "Choose the end time.");
        if (input.Start is { } s && input.End is { } e)
        {
            if (e <= s) r.AddError("EndTime", "The end time must be after the start time.");
            else if (await MainOnAsync(type, input.EffectiveFrom, ct) is { } m && (s < m.StartTime || e > m.EndTime))
                r.AddError("StartTime", $"A {DutyComponents.TypeName(type)} Sub-Duty must fall within {ScheduleRules.Times12(m.StartTime, m.EndTime)}.");
        }
        if (input.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d, ct)) r.AddError("DepartmentId", "Choose a department from the list.");
        return r;
    }

    private DutySectionSchedule Version(int sectionId, SectionInput input) => new()
    {
        DutySectionId = sectionId, EffectiveFrom = input.EffectiveFrom, StartTime = input.Start!.Value, EndTime = input.End!.Value,
        IsRequired = input.Required, DepartmentId = input.DepartmentId, DayMask = input.DayMask, CreatedAt = _time.UtcNow, CreatedBy = currentUser.Name,
    };

    public async Task<ServiceResult> SaveSectionAsync(DutyComponent component, SectionInput input, CancellationToken ct = default)
    {
        var section = await db.DutySections.FirstOrDefaultAsync(s => s.Id == (int)component, ct);
        if (section is null) return ServiceResult.Fail("", "This Sub-Duty no longer exists.");
        var check = await CheckSectionAsync(section.DutyType, input, input.Name is not null, section.Id, ct);
        if (!check.Succeeded) return check;
        if (input.Name is not null) section.Name = TextNormalizer.CleanText(input.Name);
        if (input.Summary is not null) section.Summary = TextNormalizer.CleanText(input.Summary);
        db.DutySectionSchedules.Add(Version(section.Id, input));
        await db.SaveChangesAsync(ct);
        await DutyCatalog.LoadAsync(db, ct);
        return ServiceResult.Ok(section.Id);
    }

    public async Task<ServiceResult> AddSectionAsync(DutyType type, SectionInput input, CancellationToken ct = default)
    {
        if (!DutyComponents.Types.Contains(type)) return ServiceResult.Fail("", "Unknown duty.");
        var check = await CheckSectionAsync(type, input, true, null, ct);
        if (!check.Succeeded) return check;
        var name = TextNormalizer.CleanText(input.Name);
        // A new code, never reused (the duty records store it).
        var id = Math.Max(100, await db.DutySections.MaxAsync(s => (int?)s.Id, ct) ?? 0) + 1;
        var baseSlug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (baseSlug.Length == 0) baseSlug = "section";
        if (baseSlug.Length > 50) baseSlug = baseSlug[..50].Trim('-');
        var slug = baseSlug;
        for (var n = 2; await db.DutySections.AnyAsync(s => s.Slug == slug, ct) || slug is "schedule" or "report"; n++) slug = $"{baseSlug}-{n}";
        db.DutySections.Add(new DutySection
        {
            Id = id, DutyType = type, Name = name, Slug = slug, Summary = TextNormalizer.CleanText(input.Summary), Icon = "shield-check",
            SortOrder = (await db.DutySections.Where(s => s.DutyType == type).MaxAsync(s => (int?)s.SortOrder, ct) ?? 0) + 1,
            CreatedAt = _time.UtcNow, CreatedBy = currentUser.Name,
        });
        await db.SaveChangesAsync(ct);
        db.DutySectionSchedules.Add(Version(id, input));
        await db.SaveChangesAsync(ct);
        await DutyCatalog.LoadAsync(db, ct);
        return ServiceResult.Ok(id);
    }

    // ------------------------------------------------------------------ Add Duty

    /// <summary>Icons a new Main Duty can use (all in the app's icon sprite).</summary>
    public static readonly IReadOnlyList<(string Icon, string Label)> MainDutyIcons =
    [
        ("shield-check", "Shield"), ("sun", "Sun"), ("door", "Door"), ("users", "People"), ("school", "School"),
        ("activity", "Activity"), ("clock", "Clock"), ("clipboard-check", "Clipboard"), ("calendar", "Calendar"), ("flag", "Flag"),
    ];

    /// <summary>The first segment of every fixed /duties/… address: a duty's slug may not take one.</summary>
    private static readonly HashSet<string> ReservedSlugs =
        ["audit", "board", "overview", "departments", "sections", "report-uncovered", "reports", "create", "revisions", "activities", "main", "new", "schedule", "report"];

    public const int MaxNewSubDuties = 20;

    public async Task<ServiceResult> CreateMainDutyAsync(NewMainDutyInput input, CancellationToken ct = default)
    {
        // Creating duties is administration: the Super Admin only (no staff permission for it is configured yet).
        if (!currentUser.IsSuperAdmin) return ServiceResult.Fail("", "Only the Super Admin can add a duty.");

        var r = ServiceResult.Ok();
        var name = TextNormalizer.CleanText(input.Name);
        var summary = TextNormalizer.CleanText(input.Summary);
        if (name.Length == 0) r.AddError("Name", "Name the duty.");
        else if (name.Length > DutyMainDuty.NameMaxLength) r.AddError("Name", $"Use {DutyMainDuty.NameMaxLength} characters or fewer.");
        else if ((await db.DutyMainDuties.AsNoTracking().Select(m => m.Name).ToListAsync(ct)).Any(n => TextNormalizer.NameKey(n) == TextNormalizer.NameKey(name)))
            r.AddError("Name", "A duty with this name already exists.");
        if (summary.Length > DutyMainDuty.SummaryMaxLength) r.AddError("Summary", $"Use {DutyMainDuty.SummaryMaxLength} characters or fewer.");
        var icon = string.IsNullOrWhiteSpace(input.Icon) ? "shield-check" : input.Icon.Trim();
        if (MainDutyIcons.All(i => i.Icon != icon)) r.AddError("Icon", "Choose an icon from the list.");
        if (EffectiveProblem(input.EffectiveFrom) is { } p) r.AddError("EffectiveFrom", p.Errors[0].Value);
        if (input.Start is null) r.AddError("StartTime", "Choose the start time.");
        if (input.End is null) r.AddError("EndTime", "Choose the end time.");
        if (input.Start is { } ms && input.End is { } me && me <= ms) r.AddError("EndTime", "The end time must be after the start time.");
        if (input.DayMask != 0 && (input.DayMask & PeriodDays.AllMask) == 0) r.AddError("Days", "Choose at least one school day, or every school day.");

        // Sub-Duties: rows left empty are ignored; a row needs a name, and its times must fall within the duty's.
        var subs = new List<(string Name, TimeOnly Start, TimeOnly End, bool Required)>();
        var rows = input.SubDuties.Select((s, i) => (s, i)).Where(x => !(string.IsNullOrWhiteSpace(x.s.Name) && x.s.Start is null && x.s.End is null)).ToList();
        if (rows.Count > MaxNewSubDuties) r.AddError("SubDuties", $"Add at most {MaxNewSubDuties} Sub-Duties here; more can be added from the duty's schedule.");
        foreach (var (s, i) in rows)
        {
            var key = $"SubDuties[{i}]";
            var subName = TextNormalizer.CleanText(s.Name);
            if (subName.Length == 0) { r.AddError($"{key}.Name", $"Sub-Duty {i + 1}: enter its name."); continue; }
            if (subName.Length > DutySection.NameMaxLength) { r.AddError($"{key}.Name", $"Sub-Duty {i + 1}: use {DutySection.NameMaxLength} characters or fewer."); continue; }
            if (subs.Any(x => TextNormalizer.NameKey(x.Name) == TextNormalizer.NameKey(subName))) { r.AddError($"{key}.Name", $"“{subName}” is listed twice."); continue; }
            var start = s.Start ?? input.Start;
            var end = s.End ?? input.End;
            if (start is null || end is null) continue; // the duty's own times are missing (reported above)
            if (end <= start) { r.AddError($"{key}.EndTime", $"{subName}: the end time must be after the start time."); continue; }
            if (input.Start is { } ds && input.End is { } de && (start < ds || end > de))
            {
                r.AddError($"{key}.StartTime", $"{subName} must fall within the duty's time, {ScheduleRules.Times12(ds, de)}.");
                continue;
            }
            subs.Add((subName, start.Value, end.Value, s.Required));
        }
        if (!r.Succeeded) return r;

        var slug = Slug(name, "duty");
        if (int.TryParse(slug, out _)) slug = "duty-" + slug;          // /duties/{id:int} addresses a duty record
        var existingMain = (await db.DutyMainDuties.AsNoTracking().Select(m => m.Slug).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = slug;
        for (var n = 2; existingMain.Contains(unique) || ReservedSlugs.Contains(unique); n++) unique = $"{slug}-{n}";

        try
        {
            await using var tx = await DbScope.BeginAsync(db, System.Data.IsolationLevel.Serializable, ct);
            var now = _time.UtcNow;
            // New numbers, never reused (the duty records, Sub-Duties and schedules store them).
            var typeId = Math.Max(100, await db.DutyMainDuties.MaxAsync(m => (int?)m.Id, ct) ?? 0) + 1;
            var type = (DutyType)typeId;
            db.DutyMainDuties.Add(new DutyMainDuty
            {
                Id = typeId, Name = name, Slug = unique, Summary = summary, Icon = icon,
                SortOrder = (await db.DutyMainDuties.MaxAsync(m => (int?)m.SortOrder, ct) ?? 0) + 1, CreatedAt = now, CreatedBy = currentUser.Name,
            });
            db.DutyMainSchedules.Add(new DutyMainSchedule
            {
                DutyType = type, EffectiveFrom = input.EffectiveFrom, StartTime = input.Start!.Value, EndTime = input.End!.Value, DayMask = input.DayMask,
                CreatedAt = now, CreatedBy = currentUser.Name,
            });
            var sectionId = Math.Max(100, await db.DutySections.MaxAsync(s => (int?)s.Id, ct) ?? 0);
            var sectionSlugs = (await db.DutySections.AsNoTracking().Select(s => s.Slug).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var order = 0;
            foreach (var (subName, start, end, required) in subs)
            {
                sectionId++;
                var baseSlug = Slug(subName, "section");
                var subSlug = baseSlug;
                for (var n = 2; sectionSlugs.Contains(subSlug) || subSlug is "schedule" or "report"; n++) subSlug = $"{baseSlug}-{n}";
                sectionSlugs.Add(subSlug);
                db.DutySections.Add(new DutySection
                {
                    Id = sectionId, DutyType = type, Name = subName, Slug = subSlug, Summary = "", Icon = "shield-check", SortOrder = ++order,
                    CreatedAt = now, CreatedBy = currentUser.Name,
                });
                db.DutySectionSchedules.Add(new DutySectionSchedule
                {
                    DutySectionId = sectionId, EffectiveFrom = input.EffectiveFrom, StartTime = start, EndTime = end, IsRequired = required,
                    DayMask = input.DayMask, CreatedAt = now, CreatedBy = currentUser.Name,
                });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await DutyCatalog.LoadAsync(db, ct);
            return ServiceResult.Ok(typeId);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("Name", "A duty with this name was just added. Refresh and try again.");
        }
    }

    /// <summary>An address segment from a name: lower case, letters and digits joined by hyphens (at most 50 characters).</summary>
    private static string Slug(string name, string fallback)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = fallback;
        return slug.Length > 50 ? slug[..50].Trim('-') : slug;
    }

    public async Task<ServiceResult> RetireSectionAsync(DutyComponent component, DateOnly from, CancellationToken ct = default)
    {
        var section = await db.DutySections.FirstOrDefaultAsync(s => s.Id == (int)component, ct);
        if (section is null) return ServiceResult.Fail("", "This Sub-Duty no longer exists.");
        if (EffectiveProblem(from) is { } p) return p;
        var later = await db.DutyAssignments.CountAsync(d => d.Component == component && d.DutyDate >= from, ct);
        if (later > 0) return ServiceResult.Fail("", $"{section.Name} has {Ui.Plural(later, "duty record", "duty records")} on or after {from:d MMM yyyy}. Move or delete those first; earlier records always stay.");
        section.RetiredOn = from;
        await db.SaveChangesAsync(ct);
        await DutyCatalog.LoadAsync(db, ct);
        return ServiceResult.Ok(section.Id);
    }

    public async Task<ServiceResult> RestoreSectionAsync(DutyComponent component, CancellationToken ct = default)
    {
        var section = await db.DutySections.FirstOrDefaultAsync(s => s.Id == (int)component, ct);
        if (section is null) return ServiceResult.Fail("", "This Sub-Duty no longer exists.");
        section.RetiredOn = null;
        await db.SaveChangesAsync(ct);
        await DutyCatalog.LoadAsync(db, ct);
        return ServiceResult.Ok(section.Id);
    }

    // ------------------------------------------------------------------ uncovered reports

    public async Task<ServiceResult> ReportUncoveredAsync(CoverageReportInput input, CancellationToken ct = default)
    {
        if (!DutyComponents.Exists(input.Component)) return ServiceResult.Fail("", "Unknown Sub-Duty.");
        if (input.Date > _time.Today) return ServiceResult.Fail("", "A duty that has not happened yet cannot be reported as not covered.");
        if (input.Reason is not { } reason || !Enum.IsDefined(reason)) return ServiceResult.Fail("Reason", "Choose the reason.");
        var notes = (input.Notes ?? "").Trim();
        if (notes.Length > DutyCoverageReport.NotesMaxLength) return ServiceResult.Fail("Notes", $"Use {DutyCoverageReport.NotesMaxLength} characters or fewer.");
        var area = TextNormalizer.CleanText(input.Area);
        if (input.PointId is { } pid)
        {
            var point = await db.DutyAssignments.AsNoTracking().Where(d => d.Id == pid).Select(d => new { d.Component, d.DutyDate, d.Location, d.Description }).FirstOrDefaultAsync(ct);
            if (point is null || point.Component != input.Component || point.DutyDate != input.Date)
                return ServiceResult.Fail("PointId", "Choose a coverage point of this Sub-Duty on this date.");
            if (area.Length == 0) area = string.IsNullOrWhiteSpace(point.Location) ? point.Description : point.Location!;
        }
        if (area.Length == 0) return ServiceResult.Fail("Area", "Name the area or responsibility that was not covered.");
        if (area.Length > DutyCoverageReport.AreaMaxLength) area = area[..DutyCoverageReport.AreaMaxLength];
        if (input.SupersedesId is { } sid)
        {
            var old = await db.DutyCoverageReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == sid, ct);
            if (old is null || old.Withdrawn || await db.DutyCoverageReports.AnyAsync(r => r.SupersedesId == sid, ct))
                return ServiceResult.Fail("", "That report was changed a moment ago. Refresh to see the current one.");
        }
        else if (input.PointId is { } p2 && Current(await ReportsAsync(r => r.DutyAssignmentId == p2, ct)).FirstOrDefault() is { } existing)
        {
            return ServiceResult.Fail("PointId", $"This point is already reported not covered ({existing.ReasonLabel}). Edit that report instead.");
        }
        db.DutyCoverageReports.Add(new DutyCoverageReport
        {
            DutyDate = input.Date, Component = input.Component, DutyAssignmentId = input.PointId, Area = area, Reason = reason,
            Notes = notes.Length == 0 ? null : notes, SupersedesId = input.SupersedesId, RecordedAt = _time.UtcNow, RecordedBy = currentUser.Name,
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> WithdrawReportAsync(int reportId, CancellationToken ct = default)
    {
        var old = await db.DutyCoverageReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (old is null || old.Withdrawn || await db.DutyCoverageReports.AnyAsync(r => r.SupersedesId == reportId, ct))
            return ServiceResult.Fail("", "This report is no longer current.");
        db.DutyCoverageReports.Add(new DutyCoverageReport
        {
            DutyDate = old.DutyDate, Component = old.Component, DutyAssignmentId = old.DutyAssignmentId, Area = old.Area, Reason = old.Reason,
            Notes = old.Notes, SupersedesId = old.Id, Withdrawn = true, RecordedAt = _time.UtcNow, RecordedBy = currentUser.Name,
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<IReadOnlyList<(int Id, string Name)>> DepartmentOptionsAsync(CancellationToken ct = default) =>
        (await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct)).Select(d => (d.Id, d.Name)).ToList();

    public async Task<IReadOnlyList<DutyReportView>> ReportHistoryAsync(DateOnly date, DutyComponent component, CancellationToken ct = default) =>
        (await ReportsAsync(r => r.DutyDate == date && r.Component == component, ct)).OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.Id).ToList();
}
