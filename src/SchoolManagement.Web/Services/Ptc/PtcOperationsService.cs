using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>Live PTC operations settings ("PtcOperations" configuration section).</summary>
public sealed class PtcOperationsOptions
{
    public const string Section = "PtcOperations";

    /// <summary>How often the Super Admin live view refreshes (seconds).</summary>
    public int LiveRefreshSeconds { get; set; } = 15;

    /// <summary>An interview still in progress this long after it started is flagged as stuck (minutes).</summary>
    public int StuckAfterMinutes { get; set; } = 30;
}

/// <summary>
/// The operational state of an interview on the day. Upcoming: not started and its planned end has not passed. In progress:
/// started, not ended. Completed: ended. Missed: never started and its planned end has passed (server time). Cancelled: the
/// booking or the interview is no longer active.
/// </summary>
public enum PtcLiveState
{
    Upcoming = 1,
    InProgress = 2,
    Completed = 3,
    Missed = 4,
    Cancelled = 5,
}

/// <summary>The live rules, from server-side times only.</summary>
public static class PtcLiveRules
{
    public static PtcLiveState State(PtcInterviewStatus status, bool bookingCancelled, DateOnly date, TimeOnly plannedEnd, DateTime schoolNow) =>
        bookingCancelled || status == PtcInterviewStatus.Cancelled ? PtcLiveState.Cancelled
        : status == PtcInterviewStatus.Completed ? PtcLiveState.Completed
        : status == PtcInterviewStatus.InProgress ? PtcLiveState.InProgress
        : status == PtcInterviewStatus.NoShow || date.ToDateTime(plannedEnd) <= schoolNow ? PtcLiveState.Missed
        : PtcLiveState.Upcoming;

    public static string Label(PtcLiveState s) => s switch
    {
        PtcLiveState.Upcoming => "Upcoming",
        PtcLiveState.InProgress => "In progress",
        PtcLiveState.Completed => "Completed",
        PtcLiveState.Missed => "Missed",
        _ => "Cancelled",
    };

    public static string Badge(PtcLiveState s) => s switch
    {
        PtcLiveState.Upcoming => "badge--neutral",
        PtcLiveState.InProgress => "badge--primary",
        PtcLiveState.Completed => "badge--success",
        PtcLiveState.Missed => "badge--error",
        _ => "badge--warning",
    };
}

/// <summary>
/// One teacher's meeting with one parent booking: all that teacher's interviews of the booking (one per chosen subject they
/// teach), which run together at the booking's slot. Other teachers of the booking meet the same parent at the same time
/// (<see cref="Joint"/>). <see cref="Key"/> is an opaque, tamper-proof reference (no ids reach the browser).
/// </summary>
public sealed record PtcMeeting(string Key, int TeacherId, string TeacherName, int EventId, string EventTitle, DateOnly Date,
    TimeOnly PlannedStart, TimeOnly PlannedEnd, DateTime? ActualStartUtc, DateTime? ActualEndUtc, PtcLiveState State, bool CanStart,
    bool Overrunning, bool Stuck, string StudentName, int ClassId, string ClassName, int RoundNumber, string Reference,
    IReadOnlyList<string> Subjects, IReadOnlyList<string> Joint)
{
    public int PlannedMinutes => PtcRules.Minutes(PlannedStart, PlannedEnd);
    public TimeSpan? ActualDuration => ActualStartUtc is { } s && ActualEndUtc is { } e ? e - s : null;
    public bool CanEnd => State == PtcLiveState.InProgress;
    internal int BookingId { get; init; }
}

public sealed record PtcStateCounts(int Total, int Upcoming, int InProgress, int Completed, int Missed, int Cancelled)
{
    public static PtcStateCounts Of(IEnumerable<PtcMeeting> meetings)
    {
        var list = meetings.ToList();
        return new(list.Count(m => m.State != PtcLiveState.Cancelled), list.Count(m => m.State == PtcLiveState.Upcoming), list.Count(m => m.State == PtcLiveState.InProgress),
            list.Count(m => m.State == PtcLiveState.Completed), list.Count(m => m.State == PtcLiveState.Missed), list.Count(m => m.State == PtcLiveState.Cancelled));
    }
}

public sealed class PtcTeacherDashboard
{
    public required int TeacherId { get; init; }
    public required string TeacherName { get; init; }

    /// <summary>Every date the teacher has PTC interviews (Published or Completed events), with the event.</summary>
    public required IReadOnlyList<(DateOnly Date, string Event)> Dates { get; init; }
    public DateOnly? Date { get; init; }
    public required IReadOnlyList<PtcMeeting> Meetings { get; init; }
    public required DateTime ServerNowUtc { get; init; }
    public required DateOnly Today { get; init; }

    public PtcStateCounts Counts => PtcStateCounts.Of(Meetings);
    public int PlannedMinutes => Meetings.Where(m => m.State != PtcLiveState.Cancelled).Sum(m => m.PlannedMinutes);
    public TimeSpan ActualTime => Meetings.Select(m => m.ActualDuration ?? TimeSpan.Zero).Aggregate(TimeSpan.Zero, (a, b) => a + b);
    public PtcMeeting? Current => Meetings.FirstOrDefault(m => m.State == PtcLiveState.InProgress);
    public PtcMeeting? Next => Meetings.Where(m => m.State == PtcLiveState.Upcoming).OrderBy(m => m.PlannedStart).FirstOrDefault();
}

public sealed record PtcLiveRoundRow(DateOnly Date, int ClassId, string ClassName, int RoundNumber, int Bookings, PtcStateCounts Interviews);

public sealed record PtcLiveTeacherRow(int TeacherId, string Name, IReadOnlyList<string> Classes, PtcStateCounts Interviews, TimeSpan ActualTime);

public sealed record PtcInterviewEventView(DateTime At, string? By, PtcInterviewEventKind Kind, string TeacherName, string Reference, PtcInterviewStatus? OldStatus,
    PtcInterviewStatus NewStatus, DateTime? OldStart, DateTime? OldEnd, DateTime? NewStart, DateTime? NewEnd, string? Reason);

public sealed class PtcLiveOverview
{
    public required int EventId { get; init; }
    public required string Title { get; init; }
    public required PtcEventStatus Status { get; init; }
    public required IReadOnlyList<DateOnly> Dates { get; init; }
    public DateOnly? Date { get; init; }

    /// <summary>All meetings of the shown date (before filters), for the summaries.</summary>
    public required IReadOnlyList<PtcMeeting> DayMeetings { get; init; }

    /// <summary>The meetings matching the filters.</summary>
    public required IReadOnlyList<PtcMeeting> Meetings { get; init; }
    public required IReadOnlyList<PtcLiveRoundRow> Rounds { get; init; }
    public required IReadOnlyList<PtcLiveTeacherRow> Teachers { get; init; }
    public required IReadOnlyList<PtcInterviewEventView> Log { get; init; }
    public required DateTime ServerNowUtc { get; init; }
    public required int RefreshSeconds { get; init; }
    public int? ClassId { get; init; }
    public int? TeacherId { get; init; }
    public PtcLiveState? State { get; init; }

    public PtcStateCounts Counts => PtcStateCounts.Of(DayMeetings);
    public int Bookings => DayMeetings.Where(m => m.State != PtcLiveState.Cancelled).Select(m => m.BookingId).Distinct().Count();
    public int Classes => DayMeetings.Select(m => m.ClassId).Distinct().Count();
    public int ActiveRounds => DayMeetings.Where(m => m.State != PtcLiveState.Cancelled).Select(m => (m.ClassId, m.RoundNumber)).Distinct().Count();
    public TimeSpan? AverageActual
    {
        get
        {
            var done = DayMeetings.Where(m => m.ActualDuration is not null && m.State == PtcLiveState.Completed).ToList();
            return done.Count == 0 ? null : TimeSpan.FromSeconds(done.Average(m => m.ActualDuration!.Value.TotalSeconds));
        }
    }
    public IReadOnlyList<PtcMeeting> Overrunning => DayMeetings.Where(m => m.Overrunning && !m.Stuck).ToList();
    public IReadOnlyList<PtcMeeting> NotStarted => DayMeetings.Where(m => m.State == PtcLiveState.Missed).ToList();
    public IReadOnlyList<PtcMeeting> Stuck => DayMeetings.Where(m => m.Stuck).ToList();
}

/// <summary>The result of Start / End: the authoritative server timestamps (UTC).</summary>
public sealed record PtcTimerResult(bool Succeeded, string? Error, bool AlreadyDone, DateTime? ActualStartUtc, DateTime? ActualEndUtc, PtcLiveState? State)
{
    public static PtcTimerResult Fail(string error) => new(false, error, false, null, null, null);
}

/// <summary>An Admin correction: back to not started, in progress (from a start time), or completed (start and end times).</summary>
public enum PtcCorrectionTarget
{
    NotStarted = 1,
    InProgress = 2,
    Completed = 3,
}

public interface IPtcOperationsService
{
    /// <summary>
    /// The current staff member's own PTC interviews (from the server's "Working as" identity, never a parameter) for a date:
    /// today when they have interviews today, else their next date, else their last one. Null when there is no staff identity.
    /// </summary>
    Task<PtcTeacherDashboard?> TeacherDashboardAsync(DateOnly? date, CancellationToken ct = default);

    /// <summary>Whether the current staff member has any PTC interviews (Published or Completed events).</summary>
    Task<bool> HasInterviewsAsync(CancellationToken ct = default);

    /// <summary>Starts a meeting at the server's time: its assigned teacher, or the Super Admin. A repeated Start returns the original time.</summary>
    Task<PtcTimerResult> StartAsync(string? meetingKey, CancellationToken ct = default);

    /// <summary>Ends a started meeting at the server's time: its assigned teacher, or the Super Admin. A repeated End returns the original times.</summary>
    Task<PtcTimerResult> EndAsync(string? meetingKey, CancellationToken ct = default);

    /// <summary>The Super Admin live view of an event for a date (default today, else the nearest PTC date), with filters.</summary>
    Task<PtcLiveOverview?> LiveAsync(int eventId, DateOnly? date, int? classId, int? teacherId, PtcLiveState? state, CancellationToken ct = default);

    /// <summary>
    /// Super Admin correction of a meeting's recorded status / actual times (school-local times on the interview date): needs a
    /// reason and confirmation, refuses a stale view (<paramref name="expectedVersion"/>), never changes planned times, and
    /// records the previous and new values.
    /// </summary>
    Task<ServiceResult> CorrectAsync(string? meetingKey, PtcCorrectionTarget target, TimeOnly? start, TimeOnly? end, string? reason, bool confirmed,
        string? expectedVersion, CancellationToken ct = default);

    /// <summary>The version a correction form was shown with (status and actual times).</summary>
    string Version(PtcMeeting meeting);
}

/// <summary>
/// Live PTC operations: the teacher dashboard and interview timer, and the Super Admin live view. Times are recorded by the
/// server (UTC, shown in school time); planned times, bookings, subjects and teacher assignments are never changed here.
/// Start and End are conditional updates under the PTC lock, so concurrent or repeated requests keep the first timestamps.
/// </summary>
public sealed class PtcOperationsService(SchoolDbContext db, IPtcAccess access, ICurrentUser currentUser, IDataProtectionProvider protection,
    IOptions<PtcOperationsOptions> options, ISchoolTimeService? schoolTime = null) : IPtcOperationsService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;
    private readonly IDataProtector _keys = protection.CreateProtector("SchoolManagement.Ptc.Meeting");

    public const string NotYoursMessage = "This interview is not assigned to you.";
    public const string OneMeetingMessage = "Another parent meeting is still in progress: end it before starting the next one.";

    // ------------------------------------------------------------------ meeting keys

    private string Key(int bookingId, int teacherId) => _keys.Protect($"{bookingId}|{teacherId}");

    private (int BookingId, int TeacherId)? Unkey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        try
        {
            var parts = _keys.Unprotect(key).Split('|');
            return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, C, out var b) && int.TryParse(parts[1], NumberStyles.None, C, out var t) ? (b, t) : null;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>The meeting's teacher must be the current staff member (or the current user is the Super Admin).</summary>
    private bool MayOperate(int teacherId) => currentUser.IsSuperAdmin || (currentUser.StaffId is { } me && me == teacherId);

    // ------------------------------------------------------------------ loading

    private sealed record Row(int Id, int BookingId, int TeacherId, string TeacherName, DateOnly Date, TimeOnly Start, TimeOnly End, DateTime? ActualStart, DateTime? ActualEnd,
        PtcInterviewStatus Status, PtcBookingStatus BookingStatus, string Subject, int EventId, string EventTitle, PtcEventStatus EventStatus, string StudentName,
        int ClassId, string ClassName, int RoundNumber, string Reference);

    private Task<List<Row>> RowsAsync(IQueryable<PtcTeacherInterview> q, CancellationToken ct) =>
        q.AsNoTracking().Select(i => new Row(i.Id, i.PtcParentBookingId, i.TeacherStaffMemberId, i.Teacher.FullName, i.Date, i.ScheduledStart, i.ScheduledEnd,
                i.ActualStart, i.ActualEnd, i.Status, i.Booking.Status, i.BookingSubject.SubjectName, i.Booking.ClassSchedule.PtcEventId, i.Booking.ClassSchedule.PtcEvent.Title,
                i.Booking.ClassSchedule.PtcEvent.Status, i.Booking.Student.FullName, i.Booking.ClassSchedule.SchoolClassId, i.Booking.ClassSchedule.SchoolClass.Name,
                i.Booking.Round.RoundNumber, i.Booking.BookingReference))
            .ToListAsync(ct);

    /// <summary>Groups interview rows into meetings (teacher × booking), with the other teachers meeting the same parent.</summary>
    private List<PtcMeeting> Meetings(List<Row> rows, ILookup<int, (int TeacherId, string Teacher, string Subject)> joint)
    {
        var now = _time.Now;
        var today = _time.Today;
        var stuckAfter = TimeSpan.FromMinutes(Math.Max(1, options.Value.StuckAfterMinutes));
        return rows.GroupBy(r => (r.BookingId, r.TeacherId)).Select(g =>
        {
            var live = g.Where(r => r.Status != PtcInterviewStatus.Cancelled).ToList();
            var f = g.First();
            var status = live.Count == 0 ? PtcInterviewStatus.Cancelled
                : live.Any(r => r.Status == PtcInterviewStatus.InProgress) ? PtcInterviewStatus.InProgress
                : live.Any(r => r.Status == PtcInterviewStatus.Scheduled) ? PtcInterviewStatus.Scheduled
                : live.All(r => r.Status == PtcInterviewStatus.Completed) ? PtcInterviewStatus.Completed
                : PtcInterviewStatus.NoShow;
            var start = live.Min(r => r.ActualStart);
            var end = live.Max(r => r.ActualEnd);
            var state = PtcLiveRules.State(status, f.BookingStatus == PtcBookingStatus.Cancelled, f.Date, f.End, now);
            var elapsed = start is { } s ? _time.UtcNow - s : TimeSpan.Zero;
            var overrunning = state == PtcLiveState.InProgress && elapsed > TimeSpan.FromMinutes(PtcRules.Minutes(f.Start, f.End));
            var stuck = state == PtcLiveState.InProgress && (elapsed > stuckAfter || f.Date < today);
            var canStart = status == PtcInterviewStatus.Scheduled && state is PtcLiveState.Upcoming or PtcLiveState.Missed
                           && f.Date == today && f.EventStatus == PtcEventStatus.Published;
            return new PtcMeeting(Key(f.BookingId, f.TeacherId), f.TeacherId, f.TeacherName, f.EventId, f.EventTitle, f.Date, f.Start, f.End, start, end, state, canStart,
                overrunning, stuck, f.StudentName, f.ClassId, f.ClassName, f.RoundNumber, f.Reference,
                live.Count > 0 ? live.Select(r => r.Subject).Distinct().ToList() : g.Select(r => r.Subject).Distinct().ToList(),
                joint[f.BookingId].Where(j => j.TeacherId != f.TeacherId).Select(j => $"{j.Teacher} ({j.Subject})").Distinct().ToList())
            { BookingId = f.BookingId };
        }).OrderBy(m => m.Date).ThenBy(m => m.PlannedStart).ThenBy(m => m.ClassName).ThenBy(m => m.TeacherName).ToList();
    }

    private async Task<ILookup<int, (int TeacherId, string Teacher, string Subject)>> JointAsync(IReadOnlyCollection<int> bookingIds, CancellationToken ct)
    {
        var rows = await db.PtcTeacherInterviews.AsNoTracking().Where(i => bookingIds.Contains(i.PtcParentBookingId) && i.Status != PtcInterviewStatus.Cancelled)
            .Select(i => new { i.PtcParentBookingId, i.TeacherStaffMemberId, i.Teacher.FullName, i.BookingSubject.SubjectName }).ToListAsync(ct);
        return rows.ToLookup(r => r.PtcParentBookingId, r => (r.TeacherStaffMemberId, r.FullName, r.SubjectName));
    }

    // ------------------------------------------------------------------ teacher

    private IQueryable<PtcTeacherInterview> Mine(int staffId) =>
        db.PtcTeacherInterviews.Where(i => i.TeacherStaffMemberId == staffId
            && (i.Booking.ClassSchedule.PtcEvent.Status == PtcEventStatus.Published || i.Booking.ClassSchedule.PtcEvent.Status == PtcEventStatus.Completed));

    public async Task<bool> HasInterviewsAsync(CancellationToken ct = default) =>
        currentUser.StaffId is { } me && await Mine(me).AnyAsync(ct);

    public async Task<PtcTeacherDashboard?> TeacherDashboardAsync(DateOnly? date, CancellationToken ct = default)
    {
        if (currentUser.StaffId is not { } me) return null;
        var name = await db.StaffMembers.AsNoTracking().Where(s => s.Id == me).Select(s => s.FullName).FirstOrDefaultAsync(ct);
        if (name is null) return null;
        var dates = await Mine(me).Select(i => new { i.Date, i.Booking.ClassSchedule.PtcEvent.Title }).Distinct().OrderBy(x => x.Date).ToListAsync(ct);
        var today = _time.Today;
        var all = dates.Select(d => d.Date).Distinct().ToList();
        var chosen = date is { } d0 && all.Contains(d0) ? d0
            : all.Contains(today) ? today
            : all.Where(x => x > today).Cast<DateOnly?>().FirstOrDefault() ?? all.Cast<DateOnly?>().LastOrDefault();
        var rows = chosen is { } c ? await RowsAsync(Mine(me).Where(i => i.Date == c), ct) : [];
        return new PtcTeacherDashboard
        {
            TeacherId = me, TeacherName = name, Date = chosen, Today = today, ServerNowUtc = _time.UtcNow,
            Dates = dates.Select(x => (x.Date, x.Title)).ToList(),
            Meetings = Meetings(rows, await JointAsync(rows.Select(r => r.BookingId).Distinct().ToList(), ct)),
        };
    }

    // ------------------------------------------------------------------ start / end

    private async Task<(List<PtcTeacherInterview> Rows, PtcEventStatus EventStatus, PtcBookingStatus BookingStatus)?> MeetingAsync(int bookingId, int teacherId, CancellationToken ct)
    {
        var rows = await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.PtcParentBookingId == bookingId && i.TeacherStaffMemberId == teacherId).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var info = await db.PtcParentBookings.AsNoTracking().Where(b => b.Id == bookingId).Select(b => new { b.Status, EventStatus = b.ClassSchedule.PtcEvent.Status }).SingleAsync(ct);
        return (rows, info.EventStatus, info.Status);
    }

    private string? Who() => currentUser.AccountName();

    public async Task<PtcTimerResult> StartAsync(string? meetingKey, CancellationToken ct = default)
    {
        if (Unkey(meetingKey) is not { } k) return PtcTimerResult.Fail("This interview could not be found.");
        if (!MayOperate(k.TeacherId)) return PtcTimerResult.Fail(NotYoursMessage);
        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        await PtcSetupService.LockAsync(db, ct);
        if (await MeetingAsync(k.BookingId, k.TeacherId, ct) is not { } m) return PtcTimerResult.Fail("This interview could not be found.");
        var live = m.Rows.Where(r => r.Status != PtcInterviewStatus.Cancelled).ToList();
        if (m.BookingStatus == PtcBookingStatus.Cancelled || live.Count == 0) return PtcTimerResult.Fail("This booking is cancelled.");
        // Already started or ended (a repeated or concurrent Start): the original times stand.
        if (live.Any(r => r.Status is PtcInterviewStatus.InProgress or PtcInterviewStatus.Completed))
        {
            if (live.Any(r => r.Status == PtcInterviewStatus.Completed) && !live.Any(r => r.Status == PtcInterviewStatus.InProgress))
                return new PtcTimerResult(false, "This interview has already ended.", true, live.Min(r => r.ActualStart), live.Max(r => r.ActualEnd), PtcLiveState.Completed);
            return new PtcTimerResult(true, null, true, live.Min(r => r.ActualStart), null, PtcLiveState.InProgress);
        }
        if (m.EventStatus != PtcEventStatus.Published) return PtcTimerResult.Fail("This PTC event is not running.");
        if (live[0].Date != _time.Today) return PtcTimerResult.Fail($"This interview is on {live[0].Date.ToString("ddd d MMM yyyy", C)}: it can only be started on that day.");

        // One parent meeting in progress per teacher (checked under the PTC lock, so two Starts cannot both pass). All the
        // subject interviews of one booking are one meeting and never block each other; another booking's meeting does.
        var running = await db.PtcTeacherInterviews.AsNoTracking()
            .Where(i => i.TeacherStaffMemberId == k.TeacherId && i.Status == PtcInterviewStatus.InProgress && i.PtcParentBookingId != k.BookingId
                        && i.Booking.Status != PtcBookingStatus.Cancelled)
            .Select(i => new { Class = i.Booking.ClassSchedule.SchoolClass.Name, i.ScheduledStart, i.ScheduledEnd })
            .FirstOrDefaultAsync(ct);
        if (running is not null)
            return PtcTimerResult.Fail($"{OneMeetingMessage} The meeting in progress: {running.Class}, planned {ScheduleRules.Times(running.ScheduledStart, running.ScheduledEnd)}.");

        var now = _time.UtcNow;
        var changed = await db.PtcTeacherInterviews
            .Where(i => i.PtcParentBookingId == k.BookingId && i.TeacherStaffMemberId == k.TeacherId && i.Status == PtcInterviewStatus.Scheduled && i.ActualStart == null)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, PtcInterviewStatus.InProgress).SetProperty(i => i.ActualStart, now).SetProperty(i => i.UpdatedAt, now), ct);
        if (changed == 0) return PtcTimerResult.Fail("This interview could not be started. Refresh the page.");
        db.PtcInterviewEvents.Add(new PtcInterviewEvent
        {
            PtcParentBookingId = k.BookingId, TeacherStaffMemberId = k.TeacherId, Kind = PtcInterviewEventKind.Started, OldStatus = PtcInterviewStatus.Scheduled,
            NewStatus = PtcInterviewStatus.InProgress, NewActualStart = now, By = Who(), ByStaffId = currentUser.StaffId, At = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PtcTimerResult(true, null, false, now, null, PtcLiveState.InProgress);
    }

    public async Task<PtcTimerResult> EndAsync(string? meetingKey, CancellationToken ct = default)
    {
        if (Unkey(meetingKey) is not { } k) return PtcTimerResult.Fail("This interview could not be found.");
        if (!MayOperate(k.TeacherId)) return PtcTimerResult.Fail(NotYoursMessage);
        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        await PtcSetupService.LockAsync(db, ct);
        if (await MeetingAsync(k.BookingId, k.TeacherId, ct) is not { } m) return PtcTimerResult.Fail("This interview could not be found.");
        var live = m.Rows.Where(r => r.Status != PtcInterviewStatus.Cancelled).ToList();
        if (live.Count == 0) return PtcTimerResult.Fail("This interview is cancelled.");
        if (live.All(r => r.Status == PtcInterviewStatus.Completed))
            return new PtcTimerResult(true, null, true, live.Min(r => r.ActualStart), live.Max(r => r.ActualEnd), PtcLiveState.Completed);
        if (!live.Any(r => r.Status == PtcInterviewStatus.InProgress && r.ActualStart is not null))
            return PtcTimerResult.Fail("This interview has not been started.");

        var now = _time.UtcNow;
        var started = live.Where(r => r.ActualStart is not null).Min(r => r.ActualStart)!.Value;
        if (now < started) now = started; // never an end before the recorded start
        var changed = await db.PtcTeacherInterviews
            .Where(i => i.PtcParentBookingId == k.BookingId && i.TeacherStaffMemberId == k.TeacherId && i.Status == PtcInterviewStatus.InProgress && i.ActualStart != null && i.ActualEnd == null)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, PtcInterviewStatus.Completed).SetProperty(i => i.ActualEnd, now).SetProperty(i => i.UpdatedAt, now), ct);
        if (changed == 0) return PtcTimerResult.Fail("This interview could not be ended. Refresh the page.");
        db.PtcInterviewEvents.Add(new PtcInterviewEvent
        {
            PtcParentBookingId = k.BookingId, TeacherStaffMemberId = k.TeacherId, Kind = PtcInterviewEventKind.Ended, OldStatus = PtcInterviewStatus.InProgress,
            NewStatus = PtcInterviewStatus.Completed, OldActualStart = started, NewActualStart = started, NewActualEnd = now, By = Who(), ByStaffId = currentUser.StaffId, At = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PtcTimerResult(true, null, false, started, now, PtcLiveState.Completed);
    }

    // ------------------------------------------------------------------ Super Admin live view

    public async Task<PtcLiveOverview?> LiveAsync(int eventId, DateOnly? date, int? classId, int? teacherId, PtcLiveState? state, CancellationToken ct = default)
    {
        access.Ensure();
        var ev = await db.PtcEvents.AsNoTracking().Where(e => e.Id == eventId).Select(e => new { e.Title, e.Status }).FirstOrDefaultAsync(ct);
        if (ev is null) return null;
        var eventInterviews = db.PtcTeacherInterviews.Where(i => i.Booking.ClassSchedule.PtcEventId == eventId);
        var dates = await eventInterviews.Select(i => i.Date).Distinct().OrderBy(d => d).ToListAsync(ct);
        var today = _time.Today;
        var chosen = date is { } d0 && dates.Contains(d0) ? d0
            : dates.Contains(today) ? today
            : dates.Where(x => x > today).Cast<DateOnly?>().FirstOrDefault() ?? dates.Cast<DateOnly?>().LastOrDefault();
        var rows = chosen is { } c ? await RowsAsync(eventInterviews.Where(i => i.Date == c), ct) : [];
        var joint = rows.ToLookup(r => r.BookingId, r => (r.TeacherId, r.TeacherName, r.Subject));
        var day = Meetings(rows, joint);
        var filtered = day.Where(m => (classId is null || m.ClassId == classId) && (teacherId is null || m.TeacherId == teacherId) && (state is null || m.State == state)).ToList();

        var rounds = day.GroupBy(m => (m.Date, m.ClassId, m.ClassName, m.RoundNumber))
            .Select(g => new PtcLiveRoundRow(g.Key.Date, g.Key.ClassId, g.Key.ClassName, g.Key.RoundNumber,
                g.Where(m => m.State != PtcLiveState.Cancelled).Select(m => m.BookingId).Distinct().Count(), PtcStateCounts.Of(g)))
            .OrderBy(r => r.ClassName).ThenBy(r => r.RoundNumber).ToList();
        var teachers = day.GroupBy(m => (m.TeacherId, m.TeacherName))
            .Select(g => new PtcLiveTeacherRow(g.Key.TeacherId, g.Key.TeacherName, g.Select(m => m.ClassName).Distinct().Order().ToList(), PtcStateCounts.Of(g),
                g.Select(m => m.ActualDuration ?? TimeSpan.Zero).Aggregate(TimeSpan.Zero, (a, b) => a + b)))
            .OrderBy(t => t.Name).ToList();
        var log = await db.PtcInterviewEvents.AsNoTracking().Where(x => x.Booking.ClassSchedule.PtcEventId == eventId)
            .OrderByDescending(x => x.At).ThenByDescending(x => x.Id).Take(50)
            .Select(x => new PtcInterviewEventView(x.At, x.By, x.Kind, x.Teacher.FullName, x.Booking.BookingReference, x.OldStatus, x.NewStatus,
                x.OldActualStart, x.OldActualEnd, x.NewActualStart, x.NewActualEnd, x.Reason))
            .ToListAsync(ct);
        return new PtcLiveOverview
        {
            EventId = eventId, Title = ev.Title, Status = ev.Status, Dates = dates, Date = chosen, DayMeetings = day, Meetings = filtered, Rounds = rounds,
            Teachers = teachers, Log = log, ServerNowUtc = _time.UtcNow, RefreshSeconds = Math.Clamp(options.Value.LiveRefreshSeconds, 5, 300),
            ClassId = classId, TeacherId = teacherId, State = state,
        };
    }

    // ------------------------------------------------------------------ Super Admin corrections

    public string Version(PtcMeeting m) => VersionOf(m.State, m.ActualStartUtc, m.ActualEndUtc);

    /// <summary>What was recorded (Upcoming and Missed are the same record: only time passed between them).</summary>
    private static string VersionOf(PtcLiveState state, DateTime? start, DateTime? end) =>
        string.Join("|", state is PtcLiveState.Upcoming or PtcLiveState.Missed ? 0 : (int)state, start?.Ticks.ToString(C) ?? "-", end?.Ticks.ToString(C) ?? "-");

    /// <summary>A school-local time on a date as UTC (the school's time zone, daylight saving included).</summary>
    private DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.FindSystemTimeZoneById(_time.TimeZoneId)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return DateTime.SpecifyKind(local - _time.OffsetAt(), DateTimeKind.Utc);
        }
    }

    public async Task<ServiceResult> CorrectAsync(string? meetingKey, PtcCorrectionTarget target, TimeOnly? start, TimeOnly? end, string? reason, bool confirmed,
        string? expectedVersion, CancellationToken ct = default)
    {
        access.Ensure();
        var why = TextNormalizer.CleanText(reason);
        if (why.Length == 0) return ServiceResult.Fail("Reason", "Give the reason for this correction (it is kept in the history).");
        if (why.Length > PtcInterviewEvent.ReasonMaxLength) return ServiceResult.Fail("Reason", $"Use {PtcInterviewEvent.ReasonMaxLength} characters or fewer.");
        if (!confirmed) return ServiceResult.Fail("Confirmed", "Confirm the correction.");
        if (Unkey(meetingKey) is not { } k) return ServiceResult.Fail("", "This interview could not be found.");

        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        await PtcSetupService.LockAsync(db, ct);
        var rows = await db.PtcTeacherInterviews.Where(i => i.PtcParentBookingId == k.BookingId && i.TeacherStaffMemberId == k.TeacherId && i.Status != PtcInterviewStatus.Cancelled).ToListAsync(ct);
        if (rows.Count == 0) return ServiceResult.Fail("", "This interview is cancelled or could not be found.");
        var bookingStatus = await db.PtcParentBookings.Where(b => b.Id == k.BookingId).Select(b => b.Status).SingleAsync(ct);
        if (bookingStatus == PtcBookingStatus.Cancelled) return ServiceResult.Fail("", "This booking is cancelled.");

        var oldStatus = rows.Any(r => r.Status == PtcInterviewStatus.InProgress) ? PtcInterviewStatus.InProgress
            : rows.All(r => r.Status == PtcInterviewStatus.Completed) ? PtcInterviewStatus.Completed : PtcInterviewStatus.Scheduled;
        var oldStart = rows.Min(r => r.ActualStart);
        var oldEnd = rows.Max(r => r.ActualEnd);
        var oldState = PtcLiveRules.State(oldStatus, false, rows[0].Date, rows[0].ScheduledEnd, _time.Now);
        if (expectedVersion != VersionOf(oldState, oldStart, oldEnd)) return ServiceResult.Fail("", "This interview changed since it was shown. Review it again.");

        var date = rows[0].Date;
        var now = _time.UtcNow;
        DateTime? newStart = start is { } s ? ToUtc(date, s) : null;
        DateTime? newEnd = end is { } e ? ToUtc(date, e) : null;
        PtcInterviewStatus newStatus;
        switch (target)
        {
            case PtcCorrectionTarget.NotStarted:
                newStatus = PtcInterviewStatus.Scheduled; newStart = null; newEnd = null;
                break;
            case PtcCorrectionTarget.InProgress:
                if (newStart is null) return ServiceResult.Fail("Start", "Enter the actual start time.");
                newStatus = PtcInterviewStatus.InProgress; newEnd = null;
                break;
            case PtcCorrectionTarget.Completed:
                if (newStart is null || newEnd is null) return ServiceResult.Fail("Start", "Enter the actual start and end times.");
                if (newEnd < newStart) return ServiceResult.Fail("End", "The end time cannot be before the start time.");
                newStatus = PtcInterviewStatus.Completed;
                break;
            default:
                return ServiceResult.Fail("Target", "Choose the corrected status.");
        }
        if (newStart > now || newEnd > now) return ServiceResult.Fail("Start", "Actual times cannot be in the future.");
        if (newStatus == oldStatus && newStart == oldStart && newEnd == oldEnd) return ServiceResult.Fail("", "Nothing would change.");

        foreach (var r in rows)
        {
            r.Status = newStatus;
            r.ActualStart = newStart;
            r.ActualEnd = newEnd;   // planned times are not touched
        }
        db.PtcInterviewEvents.Add(new PtcInterviewEvent
        {
            PtcParentBookingId = k.BookingId, TeacherStaffMemberId = k.TeacherId, Kind = PtcInterviewEventKind.Corrected, OldStatus = oldStatus, NewStatus = newStatus,
            OldActualStart = oldStart, OldActualEnd = oldEnd, NewActualStart = newStart, NewActualEnd = newEnd, Reason = why, By = Who(), ByStaffId = currentUser.StaffId, At = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok();
    }
}
