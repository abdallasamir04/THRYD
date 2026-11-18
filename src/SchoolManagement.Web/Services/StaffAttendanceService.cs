using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>Who counts as staff on a date: every staff member (teachers and management) inside their contract, on their working days.</summary>
public static class StaffActivity
{
    /// <summary>
    /// Active staff on a date: the contract has started (or, with no contract start recorded, the person was added by then)
    /// and has not ended (when an end is set). Part-time staff are expected only on their working weekdays.
    /// Staff added before contracts were kept have no dates: active from the day they were added.
    /// </summary>
    public static bool ExpectedOn(DateOnly? start, DateOnly? end, EmploymentType? type, IReadOnlyCollection<DayOfWeek> days, DateOnly createdLocal, DateOnly date) =>
        (start is { } s ? s <= date : createdLocal <= date) && (end is null || end >= date)
        && !(type == EmploymentType.PartTime && days.Count > 0 && !days.Contains(date.DayOfWeek));
}

public static class StaffAttendanceStatuses
{
    public static readonly IReadOnlyList<StaffAttendanceStatus> All = [StaffAttendanceStatus.Present, StaffAttendanceStatus.Late, StaffAttendanceStatus.Absent];

    public static string Label(StaffAttendanceStatus? s) => s switch
    {
        StaffAttendanceStatus.Present => "Present", StaffAttendanceStatus.Late => "Late", StaffAttendanceStatus.Absent => "Absent", _ => "Not Recorded",
    };

    public static string Slug(StaffAttendanceStatus? s) => s switch
    {
        StaffAttendanceStatus.Present => "present", StaffAttendanceStatus.Late => "late", StaffAttendanceStatus.Absent => "absent", _ => "none",
    };

    /// <summary>"Present · 7:52 AM", "Absent" — how a record reads in the correction history.</summary>
    public static string Describe(StaffAttendanceStatus s, TimeOnly? time) =>
        time is { } t ? $"{Label(s)} · {Arrivals.Time(t)}" : Label(s);
}

/// <summary>
/// Staff attendance timing (config section "StaffAttendance"): recording closes after <see cref="Cutoff"/> (09:30, school time,
/// that minute included); from the next minute, staff with no record are Absent and <see cref="AutomaticAbsences"/> stores those
/// records (StaffAttendanceFinalizer, plus the daily page and dashboard as soon as they are read). <see cref="From"/> overrides
/// the first automatic day (tests); otherwise it is stored on first use.
/// </summary>
public sealed record StaffAttendanceOptions(bool AutomaticAbsences = false, TimeOnly? Cutoff = null, DateOnly? From = null)
{
    public static readonly TimeOnly DefaultCutoff = new(9, 30);
    public TimeOnly CutoffTime => Cutoff ?? DefaultCutoff;
}

/// <summary>Expected staff, Present, Late, Absent and Not Recorded (expected staff without a record) on one date.</summary>
public sealed record StaffAttendanceCounts(int Expected, int Present, int Late, int Absent)
{
    public static readonly StaffAttendanceCounts Zero = new(0, 0, 0, 0);
    public int Recorded => Present + Late + Absent;
    public int NotRecorded => Math.Max(0, Expected - Recorded);
    public int Arrived => Present + Late;
    public static StaffAttendanceCounts operator +(StaffAttendanceCounts a, StaffAttendanceCounts b) =>
        new(a.Expected + b.Expected, a.Present + b.Present, a.Late + b.Late, a.Absent + b.Absent);
}

public sealed record StaffAttendanceRow(
    int StaffId, string Name, bool IsManagement, int? DepartmentId, string? Department, string? JobTitle, TimeOnly? ExpectedStart,
    int? RecordId, StaffAttendanceStatus? Status, TimeOnly? ArrivalTime, string? Notes, string? RecordedBy, DateTime? UpdatedAt, int Corrections)
{
    public string Group => IsManagement ? "Management Team" : Department ?? "No department";
    /// <summary>The department filter value: a department id, or "none" (no academic department, e.g. the Management Team).</summary>
    public string DepartmentKey => DepartmentId?.ToString(CultureInfo.InvariantCulture) ?? "none";
    /// <summary>Academic, Management Team or Administrative &amp; support (the Staff directory's rule).</summary>
    public StaffCategory Category => StaffCategories.Of(IsManagement, JobTitle);
}

public sealed record StaffAttendanceDay(
    DateOnly Date, DateOnly Today, bool IsSchoolDay, string? Holiday, TimeOnly? SchoolStart,
    IReadOnlyList<StaffAttendanceRow> Rows, StaffAttendanceCounts Counts, IReadOnlyList<(string Value, string Label)> Departments,
    TimeOnly Cutoff, bool RecordingOpen, bool AutomaticAbsences)
{
    public bool IsToday => Date == Today;
    /// <summary>Today after the cutoff minute: the normal buttons are gone and only Correct remains.</summary>
    public bool ClosedToday => IsSchoolDay && IsToday && !RecordingOpen;
}

/// <summary>Department: a department id or "none" (no academic department). Category: academic, management or support.</summary>
public sealed record StaffAttendanceFilter(string? Department = null, string? Status = null, string? Search = null, string? Category = null);

/// <summary>The dashboard's staff attendance: the day's counts, whether it is a school day, and the active staff on the date.</summary>
public sealed record StaffAttendanceToday(StaffAttendanceCounts Counts, bool IsSchoolDay, int ActiveStaff);

public sealed record StaffMarkResult(bool Saved, string Message, StaffAttendanceRow? Row, StaffAttendanceCounts? Counts, bool Closed = false);

public sealed record StaffCorrectionLine(int StaffId, string StaffName, DateOnly Date, string Before, string After, string? Reason, DateTime At, string? By);

// ------------------------------------------------------------------ history

public sealed class StaffAttendanceQuery
{
    /// <summary>day, range, month (default) or year.</summary>
    public string? Period { get; set; }
    public DateOnly? Date { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    /// <summary>"yyyy-MM".</summary>
    public string? Month { get; set; }
    public int? YearId { get; set; }
    public int? StaffId { get; set; }
    public string? Department { get; set; }
    /// <summary>academic, management or support (see <see cref="StaffCategories"/>).</summary>
    public string? Category { get; set; }
    /// <summary>present, late, absent, none (not recorded).</summary>
    public string? Status { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public string ToQuery()
    {
        var parts = new List<string>();
        void Add(string k, object? v) { if (v?.ToString() is { Length: > 0 } s) parts.Add($"{k}={Uri.EscapeDataString(s)}"); }
        Add("period", Period); Add("date", Date is { } d ? SchoolWeek.Iso(d) : null); Add("from", From is { } f ? SchoolWeek.Iso(f) : null);
        Add("to", To is { } t ? SchoolWeek.Iso(t) : null); Add("month", Month); Add("yearId", YearId); Add("staffId", StaffId);
        Add("department", Department); Add("category", Category); Add("status", Status);
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }
}

/// <summary>One staff member on one expected school day: a record, or Not Recorded.</summary>
public sealed record StaffAttendanceLine(DateOnly Date, int StaffId, string Name, string Group, StaffAttendanceStatus? Status, TimeOnly? ArrivalTime,
    string? Notes, string? RecordedBy, DateTime? UpdatedAt, int Corrections);

public sealed record StaffAttendanceTotals(int StaffId, string Name, string Group, StaffAttendanceCounts Counts)
{
    /// <summary>(Present + Late) ÷ recorded days; null when nothing is recorded.</summary>
    public double? Percent => Counts.Recorded == 0 ? null : Math.Round(Counts.Arrived * 100.0 / Counts.Recorded, 1);
}

public sealed record StaffAttendanceHistory(
    string PeriodLabel, DateOnly From, DateOnly To, int SchoolDays, StaffAttendanceCounts Totals,
    IReadOnlyList<StaffAttendanceTotals> ByStaff, IReadOnlyList<StaffAttendanceLine> Lines, PagedResult<StaffAttendanceLine> Page,
    IReadOnlyList<StaffCorrectionLine> Corrections, IReadOnlyList<YearView> Years, IReadOnlyList<(int Id, string Name, string Group)> Staff,
    IReadOnlyList<(string Value, string Label)> Departments, IReadOnlyList<(string Value, string Label)> Months);

public interface IStaffAttendanceService
{
    Task<StaffAttendanceDay> DayAsync(DateOnly date, StaffAttendanceFilter filter, CancellationToken ct = default);
    Task<StaffAttendanceCounts> CountsAsync(DateOnly date, CancellationToken ct = default);
    Task<StaffAttendanceToday> TodayAsync(DateOnly date, CancellationToken ct = default);
    Task<StaffAttendanceRow?> RowAsync(int staffId, DateOnly date, CancellationToken ct = default);
    /// <summary>
    /// One click, today, until the cutoff (09:30 included): Present / Late with the server's school time as the arrival time,
    /// or Absent. Only for a staff member without a record that day; refused on the server after the cutoff.
    /// </summary>
    Task<StaffMarkResult> MarkAsync(int staffId, DateOnly date, StaffAttendanceStatus status, CancellationToken ct = default);
    /// <summary>
    /// Corrects a saved record (any status; Present / Late with the actual arrival time). A reason is required and the
    /// record before the change is kept in the correction history. Records are never created here.
    /// </summary>
    Task<StaffMarkResult> CorrectAsync(int staffId, DateOnly date, StaffAttendanceStatus status, TimeOnly? arrival, string? reason, CancellationToken ct = default);
    Task<StaffAttendanceHistory> HistoryAsync(StaffAttendanceQuery query, CancellationToken ct = default);
    /// <summary>Stores Absent for every expected staff member with no record on a closed school day. Idempotent; never overwrites.</summary>
    Task<ServiceResult> FinalizeAsync(DateOnly date, CancellationToken ct = default);
    /// <summary>Finalizes every closed school day from the first automatic day (at most 60 days back) up to today.</summary>
    Task<int> FinalizeDueAsync(CancellationToken ct = default);
}

/// <summary>
/// Staff Attendance: every active staff member (academic, management, administrative &amp; support), one record per person per
/// school day, on the school clock (Africa/Cairo, from the server, never the browser).
///  - Until the cutoff (09:30, that minute included) one click records Present / Late (with the server's time) or Absent.
///    Late applies only after the person's start time (Working Hours) or, when none is set, the school start time.
///  - From the next minute recording is closed on the server, and staff with no record are Absent: those records are stored
///    automatically (never over an existing record). Before the cutoff, no record means Not Recorded, never Absent.
///  - Saved records change only through Correct (reason required, history kept).
/// </summary>
public sealed class StaffAttendanceService(SchoolDbContext db, ICurrentUser currentUser, ISettingsService settings, IBelongingsService calendar,
    ISchoolTimeService? time = null, StaffAttendanceOptions? options = null) : IStaffAttendanceService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = time ?? SchoolTime.Current;
    private readonly StaffAttendanceOptions _options = options ?? new StaffAttendanceOptions();

    /// <summary>Who stored an automatic absence.</summary>
    public const string AutomaticRecorder = "Automatic (attendance cutoff)";
    public const string AutoFinalizeFromKey = "StaffAttendance.AutoFinalizeFrom";

    public TimeOnly Cutoff => _options.CutoffTime;

    /// <summary>The current school time to the minute: 09:30:45 is still 09:30.</summary>
    private TimeOnly NowMinute => new(_time.Now.Hour, _time.Now.Minute);

    /// <summary>Recording is open today until the end of the cutoff minute.</summary>
    private bool IsOpen(DateOnly date) => date == _time.Today && NowMinute <= Cutoff;

    /// <summary>A day whose recording window has passed: an earlier day, or today after the cutoff minute.</summary>
    private bool IsClosed(DateOnly date) => date < _time.Today || (date == _time.Today && NowMinute > Cutoff);

    /// <summary>
    /// The first day with automatic absences (null when they are off). Stored on first use: today when that is before the
    /// cutoff, otherwise the next day, so switching the feature on in the afternoon never marks a whole day Absent.
    /// </summary>
    private async Task<DateOnly?> AutomaticFromAsync(CancellationToken ct)
    {
        if (!_options.AutomaticAbsences) return null;
        if (_options.From is { } given) return given;
        var stored = await db.Settings.AsNoTracking().Where(x => x.Key == AutoFinalizeFromKey).Select(x => x.Value).FirstOrDefaultAsync(ct);
        if (DateOnly.TryParseExact(stored, "yyyy-MM-dd", C, DateTimeStyles.None, out var d)) return d;
        var from = NowMinute <= Cutoff ? _time.Today : _time.Today.AddDays(1);
        db.Settings.Add(new AppSetting { Key = AutoFinalizeFromKey, Value = from.ToString("yyyy-MM-dd", C) });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return await AutomaticFromAsync(ct); } // stored by another request
        db.ChangeTracker.Clear();
        return from;
    }

    /// <summary>Lazy finalization of the day being read (daily page, dashboard), so it never waits for the background run.</summary>
    private async Task FinalizeIfDueAsync(DateOnly date, CancellationToken ct)
    {
        if (!IsClosed(date) || await AutomaticFromAsync(ct) is not { } from || date < from) return;
        await FinalizeAsync(date, ct);
    }

    public async Task<ServiceResult> FinalizeAsync(DateOnly date, CancellationToken ct = default)
    {
        if (date > _time.Today) return ServiceResult.Fail("", "A future day is never marked absent.");
        if (!IsClosed(date)) return ServiceResult.Fail("", $"Staff attendance is open until {Arrivals.Time(Cutoff)}; nobody is absent before then.");
        if (!(await SchoolDays.LoadAsync(db, ct)).IsSchoolDay(date)) return ServiceResult.Ok(0); // weekend or holiday: nobody expected

        var expected = (await StaffAsync(ct)).Where(x => x.ExpectedOn(date)).Select(x => x.Id).ToList();
        var recorded = (await db.StaffAttendance.Where(r => r.Date == date).Select(r => r.StaffMemberId).ToListAsync(ct)).ToHashSet();
        var missing = expected.Where(id => !recorded.Contains(id)).ToList();
        if (missing.Count == 0) return ServiceResult.Ok(0);

        var note = $"No attendance recorded by {Arrivals.Time(Cutoff)}.";
        StaffAttendanceRecord Absent(int id) => new()
        {
            StaffMemberId = id, Date = date, Status = StaffAttendanceStatus.Absent, Notes = note, RecordedBy = AutomaticRecorder, UpdatedBy = AutomaticRecorder,
        };
        var created = 0;
        db.StaffAttendance.AddRange(missing.Select(Absent));
        try
        {
            await db.SaveChangesAsync(ct);
            created = missing.Count;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Recorded a moment ago by someone else (or another finalizer run): add the rest one by one, keep what exists.
            db.ChangeTracker.Clear();
            foreach (var id in missing)
            {
                db.StaffAttendance.Add(Absent(id));
                try { await db.SaveChangesAsync(ct); created++; }
                catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e)) { db.ChangeTracker.Clear(); }
            }
        }
        db.ChangeTracker.Clear();
        return ServiceResult.Ok(created);
    }

    public async Task<int> FinalizeDueAsync(CancellationToken ct = default)
    {
        if (await AutomaticFromAsync(ct) is not { } from) return 0;
        var today = _time.Today;
        var created = 0;
        for (var d = from > today.AddDays(-60) ? from : today.AddDays(-60); d <= today; d = d.AddDays(1))
        {
            if (!IsClosed(d)) continue;
            var r = await FinalizeAsync(d, ct);
            if (r.Succeeded) created += r.EntityId ?? 0;
        }
        return created;
    }

    private sealed record StaffInfo(int Id, string Name, bool IsManagement, int? DepartmentId, string? Department, string? JobTitle,
        TimeOnly? WorkStart, DateOnly? Start, DateOnly? End, EmploymentType? Type, List<DayOfWeek> Days, DateTime CreatedAt)
    {
        public string Group => IsManagement ? "Management Team" : Department ?? "No department";
        public StaffCategory Category => StaffCategories.Of(IsManagement, JobTitle);
        public bool ExpectedOn(DateOnly date) =>
            StaffActivity.ExpectedOn(Start, End, Type, Days, SchoolTime.Current.LocalDate(CreatedAt), date);
    }

    private async Task<List<StaffInfo>> StaffAsync(CancellationToken ct) =>
        (await db.StaffMembers.AsNoTracking().OrderBy(s => s.FullName).Select(s => new
            {
                s.Id, s.FullName, IsManagement = s is ManagementMember, s.DepartmentId, Department = s.Department != null ? s.Department.Name : null,
                s.JobTitle, s.WorkStart, s.ContractStartDate, s.ContractEndDate, s.EmploymentType,
                Days = s.WorkingDays.Select(d => d.Day).ToList(), s.CreatedAt,
            }).ToListAsync(ct))
        .Select(s => new StaffInfo(s.Id, s.FullName, s.IsManagement, s.DepartmentId, s.Department, s.JobTitle, s.WorkStart,
            s.ContractStartDate, s.ContractEndDate, s.EmploymentType, s.Days, s.CreatedAt)).ToList();

    private static bool InDepartment(StaffInfo s, string? department) => department switch
    {
        null or "" => true,
        "none" => s.DepartmentId is null,
        _ => int.TryParse(department, out var id) && s.DepartmentId == id,
    };

    private static bool InCategory(StaffInfo s, string? category) =>
        StaffCategories.Parse(category) is not { } c || s.Category == c;

    private static IReadOnlyList<(string, string)> Departments(List<StaffInfo> staff) =>
        staff.Where(s => s.DepartmentId is not null).GroupBy(s => s.DepartmentId!.Value).OrderBy(g => g.First().Department)
            .Select(g => (g.Key.ToString(C), g.First().Department!))
            .Concat(staff.Any(s => s.DepartmentId is null) ? [("none", "No academic department")] : [])
            .ToList();

    // ------------------------------------------------------------------ one day

    public async Task<StaffAttendanceDay> DayAsync(DateOnly date, StaffAttendanceFilter filter, CancellationToken ct = default)
    {
        await FinalizeIfDueAsync(date, ct);
        var days = await SchoolDays.LoadAsync(db, ct);
        var school = days.IsSchoolDay(date);
        var staff = await StaffAsync(ct);
        var expected = school ? staff.Where(s => s.ExpectedOn(date)).ToList() : [];
        var rows = await RowsAsync(expected, date, ct);
        var counts = Count(rows);
        var term = filter.Search?.Trim();
        var shown = rows.Where(r => InDepartment(expected.First(s => s.Id == r.StaffId), filter.Department))
            .Where(r => InCategory(expected.First(s => s.Id == r.StaffId), filter.Category))
            .Where(r => filter.Status is null or "" || StaffAttendanceStatuses.Slug(r.Status) == filter.Status)
            .Where(r => string.IsNullOrEmpty(term) || r.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var from = await AutomaticFromAsync(ct);
        return new StaffAttendanceDay(date, _time.Today, school, days.HolidayOn(date)?.Name, await settings.GetArrivalDeadlineAsync(ct),
            shown, counts, Departments(staff), Cutoff, school && IsOpen(date), from is { } f && date >= f);
    }

    public async Task<StaffAttendanceCounts> CountsAsync(DateOnly date, CancellationToken ct = default)
    {
        if (!(await SchoolDays.LoadAsync(db, ct)).IsSchoolDay(date)) return StaffAttendanceCounts.Zero;
        var expected = (await StaffAsync(ct)).Where(s => s.ExpectedOn(date)).Select(s => s.Id).ToHashSet();
        var statuses = await db.StaffAttendance.AsNoTracking().Where(r => r.Date == date).Select(r => new { r.StaffMemberId, r.Status }).ToListAsync(ct);
        // Only expected staff count, each once (the unique index already allows one record per person and day).
        var mine = statuses.Where(r => expected.Contains(r.StaffMemberId)).ToList();
        return new StaffAttendanceCounts(expected.Count, mine.Count(r => r.Status == StaffAttendanceStatus.Present),
            mine.Count(r => r.Status == StaffAttendanceStatus.Late), mine.Count(r => r.Status == StaffAttendanceStatus.Absent));
    }

    public async Task<StaffAttendanceToday> TodayAsync(DateOnly date, CancellationToken ct = default)
    {
        await FinalizeIfDueAsync(date, ct);
        var school = (await SchoolDays.LoadAsync(db, ct)).IsSchoolDay(date);
        // Active = inside the contract on the date (whatever the weekday); expected = active and working that day.
        var active = (await StaffAsync(ct)).Count(s => StaffActivity.ExpectedOn(s.Start, s.End, null, [], SchoolTime.Current.LocalDate(s.CreatedAt), date));
        return new StaffAttendanceToday(school ? await CountsAsync(date, ct) : StaffAttendanceCounts.Zero, school, active);
    }

    public async Task<StaffAttendanceRow?> RowAsync(int staffId, DateOnly date, CancellationToken ct = default)
    {
        var s = (await StaffAsync(ct)).FirstOrDefault(x => x.Id == staffId);
        return s is null ? null : (await RowsAsync([s], date, ct))[0];
    }

    private async Task<List<StaffAttendanceRow>> RowsAsync(IReadOnlyList<StaffInfo> staff, DateOnly date, CancellationToken ct)
    {
        var ids = staff.Select(s => s.Id).ToList();
        var records = (await db.StaffAttendance.AsNoTracking().Where(r => r.Date == date && ids.Contains(r.StaffMemberId))
                .Select(r => new { r.Id, r.StaffMemberId, r.Status, r.ArrivalTime, r.Notes, By = r.UpdatedBy ?? r.RecordedBy, r.UpdatedAt, Corrections = r.Corrections.Count })
                .ToListAsync(ct))
            .ToDictionary(r => r.StaffMemberId);
        return staff.Select(s =>
        {
            records.TryGetValue(s.Id, out var r);
            return new StaffAttendanceRow(s.Id, s.Name, s.IsManagement, s.DepartmentId, s.Department, s.JobTitle, s.WorkStart,
                r?.Id, r?.Status, r?.ArrivalTime, r?.Notes, r?.By, r?.UpdatedAt, r?.Corrections ?? 0);
        }).ToList();
    }

    private static StaffAttendanceCounts Count(IReadOnlyCollection<StaffAttendanceRow> rows) =>
        new(rows.Count, rows.Count(r => r.Status == StaffAttendanceStatus.Present), rows.Count(r => r.Status == StaffAttendanceStatus.Late),
            rows.Count(r => r.Status == StaffAttendanceStatus.Absent));

    // ------------------------------------------------------------------ recording

    public async Task<StaffMarkResult> MarkAsync(int staffId, DateOnly date, StaffAttendanceStatus status, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status)) return Fail("Choose Present, Late or Absent.");
        // The server's moment of the click, in school time (Africa/Cairo, daylight saving included).
        var now = _time.Now;
        if (date != _time.Today) return Fail($"Staff attendance is recorded on the day itself, until {Arrivals.Time(Cutoff)}.");
        if (!IsOpen(date))
            return new(false, $"Staff attendance closed at {Arrivals.Time(Cutoff)}. Staff with no attendance are Absent; a saved record can be changed with Correct.", null, null, Closed: true);
        var (staff, error) = await CheckAsync(staffId, date, ct);
        if (error is not null) return Fail(error);
        if (await db.StaffAttendance.AnyAsync(r => r.StaffMemberId == staffId && r.Date == date, ct))
            return Fail($"{staff!.Name} is already recorded today. Use Correct to change it (the earlier record is kept).");
        TimeOnly? arrival = status == StaffAttendanceStatus.Absent ? null : new TimeOnly(now.Hour, now.Minute, now.Second);
        if (status == StaffAttendanceStatus.Late && (staff!.WorkStart ?? await settings.GetArrivalDeadlineAsync(ct)) is { } start && NowMinute <= start)
            return Fail($"{staff.Name} is not late: it is not past the {Arrivals.Time(start)} start time yet. Record Present.");

        db.StaffAttendance.Add(new StaffAttendanceRecord
        {
            StaffMemberId = staffId, Date = date, Status = status, ArrivalTime = arrival, RecordedBy = currentUser.Name, UpdatedBy = currentUser.Name,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Fail($"{staff!.Name} was recorded by someone else at the same moment. Refresh the page.");
        }
        db.ChangeTracker.Clear();
        return await DoneAsync(staff!, date, $"{staff!.Name}: {StaffAttendanceStatuses.Describe(status, arrival)}.", ct);
    }

    public async Task<StaffMarkResult> CorrectAsync(int staffId, DateOnly date, StaffAttendanceStatus status, TimeOnly? arrival, string? reason, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status)) return Fail("Choose Present, Late or Absent.");
        var (staff, error) = await CheckAsync(staffId, date, ct);
        if (error is not null) return Fail(error);
        if (status == StaffAttendanceStatus.Absent) arrival = null;
        else if (arrival is null) return Fail("Enter the time the staff member actually arrived.");
        else if (date == _time.Today && arrival > TimeOnly.FromDateTime(_time.Now)) return Fail("The arrival time cannot be later than now.");
        var why = reason?.Trim() is { Length: > 0 } r ? r : null;
        if (why?.Length > StaffAttendanceCorrection.ReasonMaxLength) return Fail($"Use {StaffAttendanceCorrection.ReasonMaxLength} characters or fewer for the reason.");

        var record = await db.StaffAttendance.FirstOrDefaultAsync(x => x.StaffMemberId == staffId && x.Date == date, ct);
        if (record is null)
            return Fail(IsOpen(date)
                ? $"{staff!.Name} has no record yet: use Present, Late or Absent (until {Arrivals.Time(Cutoff)})."
                : $"{staff!.Name} has no saved record on this day, so there is nothing to correct.");
        if (record.Status == status && record.ArrivalTime == arrival) return await DoneAsync(staff!, date, "Nothing changed.", ct);
        if (why is null) return Fail("Give a reason for the correction: the earlier record is kept with it.");
        db.StaffAttendanceCorrections.Add(new StaffAttendanceCorrection
        {
            StaffAttendanceRecordId = record.Id,
            Before = StaffAttendanceStatuses.Describe(record.Status, record.ArrivalTime),
            After = StaffAttendanceStatuses.Describe(status, arrival),
            Reason = why, CorrectedAt = DateTime.UtcNow, CorrectedBy = currentUser.Name,
        });
        (record.Status, record.ArrivalTime, record.UpdatedBy) = (status, arrival, currentUser.Name);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return await DoneAsync(staff!, date, $"{staff!.Name}: {StaffAttendanceStatuses.Describe(status, arrival)} (corrected).", ct);
    }

    private static StaffMarkResult Fail(string message) => new(false, message, null, null);

    private async Task<StaffMarkResult> DoneAsync(StaffInfo staff, DateOnly date, string message, CancellationToken ct) =>
        new(true, message, (await RowsAsync([staff], date, ct))[0], await CountsAsync(date, ct));

    /// <summary>A school day, today or earlier, for an existing staff member expected that day.</summary>
    private async Task<(StaffInfo?, string?)> CheckAsync(int staffId, DateOnly date, CancellationToken ct)
    {
        if (date > _time.Today) return (null, "Attendance cannot be recorded for a future date.");
        var days = await SchoolDays.LoadAsync(db, ct);
        if (!days.IsSchoolDay(date))
            return (null, days.HolidayOn(date) is { } h ? $"{date.ToString("ddd d MMM", C)} is a school holiday ({h.Name})." : $"{date.ToString("dddd d MMMM", C)} is not a school day.");
        var staff = (await StaffAsync(ct)).FirstOrDefault(s => s.Id == staffId);
        if (staff is null) return (null, "This staff member no longer exists. Refresh the page.");
        if (!staff.ExpectedOn(date)) return (null, $"{staff.Name} is not expected on {date.ToString("ddd d MMM", C)} (outside the contract dates or not a working day).");
        return (staff, null);
    }

    // ------------------------------------------------------------------ history

    public async Task<StaffAttendanceHistory> HistoryAsync(StaffAttendanceQuery q, CancellationToken ct = default)
    {
        var today = _time.Today;
        var years = await calendar.YearsAsync(ct);
        var year = years.FirstOrDefault(y => y.Id == q.YearId) ?? years.FirstOrDefault(y => y.Quarters.Any(x => x.StartDate <= today && x.EndDate >= today)) ?? years.FirstOrDefault();
        DateOnly from, to;
        string label;
        switch (q.Period)
        {
            case "day":
                from = to = q.Date ?? today; label = from.ToString("dddd d MMMM yyyy", C); break;
            case "range":
                from = q.From ?? q.To ?? today; to = q.To ?? q.From ?? today;
                if (to < from) (from, to) = (to, from);
                label = "Custom dates"; break;
            case "year" when year?.Quarters.Where(x => x.IsConfigured).ToList() is { Count: > 0 } qs:
                from = qs.Min(x => x.StartDate!.Value); to = qs.Max(x => x.EndDate!.Value); label = $"{year.Name} academic year"; break;
            default:
                q.Period = "month";
                var m = DateOnly.TryParseExact((q.Month ?? "") + "-01", "yyyy-MM-dd", out var mm) ? mm : new DateOnly(today.Year, today.Month, 1);
                q.Month = m.ToString("yyyy-MM", C);
                from = m; to = m.AddMonths(1).AddDays(-1); label = m.ToString("MMMM yyyy", C); break;
        }
        // History stops at today: future days have nothing to record.
        var last = to > today ? today : to;

        var days = await SchoolDays.LoadAsync(db, ct);
        var staff = await StaffAsync(ct);
        var scope = staff.Where(s => (q.StaffId is null || s.Id == q.StaffId) && InDepartment(s, q.Department) && InCategory(s, q.Category)).ToList();
        var ids = scope.Select(s => s.Id).ToList();
        var records = (await db.StaffAttendance.AsNoTracking().Where(r => r.Date >= from && r.Date <= last && ids.Contains(r.StaffMemberId))
                .Select(r => new { r.StaffMemberId, r.Date, r.Status, r.ArrivalTime, r.Notes, By = r.UpdatedBy ?? r.RecordedBy, r.UpdatedAt, Corrections = r.Corrections.Count })
                .ToListAsync(ct))
            .ToDictionary(r => (r.StaffMemberId, r.Date));

        var lines = new List<StaffAttendanceLine>();
        var schoolDays = 0;
        for (var d = from; d <= last; d = d.AddDays(1))
        {
            if (!days.IsSchoolDay(d)) continue;
            schoolDays++;
            foreach (var s in scope)
            {
                var has = records.TryGetValue((s.Id, d), out var r);
                // Not Recorded lines only for days the person was expected; a record always shows.
                if (!has && !s.ExpectedOn(d)) continue;
                lines.Add(new StaffAttendanceLine(d, s.Id, s.Name, s.Group, r?.Status, r?.ArrivalTime, r?.Notes, r?.By, r?.UpdatedAt, r?.Corrections ?? 0));
            }
        }
        var status = q.Status;
        var shown = lines.Where(l => status is null or "" || StaffAttendanceStatuses.Slug(l.Status) == status)
            .OrderByDescending(l => l.Date).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        StaffAttendanceCounts Of(IEnumerable<StaffAttendanceLine> ls)
        {
            var list = ls.ToList();
            return new(list.Count, list.Count(l => l.Status == StaffAttendanceStatus.Present), list.Count(l => l.Status == StaffAttendanceStatus.Late),
                list.Count(l => l.Status == StaffAttendanceStatus.Absent));
        }
        var byStaff = lines.GroupBy(l => l.StaffId).Select(g => new StaffAttendanceTotals(g.Key, g.First().Name, g.First().Group, Of(g)))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var corrections = await db.StaffAttendanceCorrections.AsNoTracking()
            .Where(c => c.Record.Date >= from && c.Record.Date <= last && ids.Contains(c.Record.StaffMemberId))
            .OrderByDescending(c => c.CorrectedAt)
            .Select(c => new StaffCorrectionLine(c.Record.StaffMemberId, c.Record.StaffMember.FullName, c.Record.Date, c.Before, c.After, c.Reason, c.CorrectedAt, c.CorrectedBy))
            .ToListAsync(ct);

        var size = Paging.NormalizePageSize(q.PageSize);
        var page = Paging.ClampPage(q.Page, shown.Count, size);
        var months = new List<(string, string)>();
        for (var m = new DateOnly(today.Year, today.Month, 1).AddMonths(-23); m <= new DateOnly(today.Year, today.Month, 1); m = m.AddMonths(1))
            months.Insert(0, (m.ToString("yyyy-MM", C), m.ToString("MMMM yyyy", C)));
        return new StaffAttendanceHistory(label, from, to, schoolDays, Of(lines), byStaff, shown,
            new PagedResult<StaffAttendanceLine> { Items = shown.Skip((page - 1) * size).Take(size).ToList(), Page = page, PageSize = size, TotalCount = shown.Count },
            corrections, years, staff.Select(s => (s.Id, s.Name, s.Group)).ToList(), Departments(staff), months);
    }
}
