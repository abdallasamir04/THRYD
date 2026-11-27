using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

// ============================================================ PTC Phase 7: reports and analytics
//
// Everything on the reports pages and in the export comes from ONE filtered dataset (PtcReportData), so the cards, charts
// and tables always agree. Definitions:
//  - Parent booking: one PtcParentBooking (one shared meeting window). Never counted per teacher interview.
//  - Teacher interview: one PtcTeacherInterview row (one teacher, one subject of a booking).
//  - Meeting: one teacher with one booking (all that teacher's interviews of the booking happen together); durations and a
//    teacher's meeting counts use meetings, so a teacher meeting a parent about two subjects is one meeting of one duration.
//  - Interview state: the live rule (PtcLiveRules.State, server time): Upcoming, In progress, Completed, Missed (never started
//    and its planned end has passed, or recorded No-show), Cancelled. Upcoming is never counted as missed.
//  - Completion rate = Completed / (Completed + Missed) interviews; cancellation rate = Cancelled / all interview rows.
//  - Actual duration = server-recorded End − Start (completed meetings with both stamps only); nothing is estimated.
//  - Started on time: started no later than OnTimeGraceMinutes after the planned start.
//  - Averages of ratings are shown only with at least MinSample answers; otherwise "insufficient data" with the count.
//  - Teacher satisfaction uses only teacher-specific feedback about that teacher; general event feedback is never attributed.
//  - Response rate = eligible bookings with feedback / eligible bookings (PtcFeedbackRules).

public enum PtcReportFeedback
{
    Submitted = 1,
    NotSubmitted = 2,
    WithConcern = 3,
    OpenConcern = 4,
    ResolvedConcern = 5,
}

public sealed class PtcReportFilter
{
    public int? YearId { get; set; }
    public int? EventId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? ClassId { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeacherId { get; set; }
    public PtcLiveState? State { get; set; }
    public PtcReportFeedback? Feedback { get; set; }

    public bool InterviewFilter => DepartmentId is not null || TeacherId is not null || State is not null;

    /// <summary>The filters as a query string (for links and the export).</summary>
    public string Query()
    {
        var c = CultureInfo.InvariantCulture;
        return string.Join("&", new[]
        {
            YearId is { } y ? $"year={y}" : null, EventId is { } e ? $"event={e}" : null,
            From is { } f ? $"from={f.ToString("yyyy-MM-dd", c)}" : null, To is { } t ? $"to={t.ToString("yyyy-MM-dd", c)}" : null,
            ClassId is { } cl ? $"cls={cl}" : null, DepartmentId is { } d ? $"dept={d}" : null, TeacherId is { } te ? $"teacher={te}" : null,
            State is { } s ? $"state={s}" : null, Feedback is { } fb ? $"feedback={fb}" : null,
        }.Where(x => x is not null));
    }
}

/// <summary>A value that exists only with enough answers.</summary>
public sealed record PtcSampled(double? Value, int Count)
{
    public bool Sufficient => Value is not null;
    public string Text(string format = "0.0", string unit = " / 4") =>
        Value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) + unit + $" (n={Count})" : Count == 0 ? "No data" : $"Insufficient data (n={Count})";
}

public sealed record PtcReportInterview(int Id, int BookingId, string BookingReference, int EventId, string EventTitle, int ScheduleId, int ClassId, string ClassName,
    int RoundNumber, int TeacherId, string TeacherName, int DepartmentId, string Department, string Subject, DateOnly Date, TimeOnly PlannedStart, TimeOnly PlannedEnd,
    DateTime? ActualStart, DateTime? ActualEnd, PtcLiveState State)
{
    public int PlannedMinutes => (int)(PlannedEnd - PlannedStart).TotalMinutes;
}

public sealed record PtcReportBooking(int Id, string Reference, int EventId, string EventTitle, int ScheduleId, int ClassId, string ClassName, DateOnly Date,
    int RoundNumber, int Slot, string StudentName, PtcBookingStatus Status, DateTime BookedAt, PtcFeedbackAvailability FeedbackAvailability);

public sealed record PtcReportFeedbackItem(int Id, string Reference, int BookingId, int EventId, PtcRating? Overall, PtcRating? Communication, PtcRating? Organization,
    PtcHelpful? Helpful, PtcConcernCategory? Category, PtcConcernStatus? ConcernStatus, PtcFeedbackReview Review, DateTime SubmittedAt, string? AssignedTo,
    string? Comments, string? Concern, string? Resolution, DateTime? ResolvedAt);

public sealed record PtcReportTeacherFeedback(int FeedbackId, string FeedbackReference, int BookingId, int EventId, int TeacherId, PtcRating? Communication,
    PtcHelpful? Helpful, string? Comment, DateTime SubmittedAt);

public sealed record PtcReportSubstitute(int Id, int EventId, string EventTitle, string ClassName, DateOnly Date, TimeOnly Start, TimeOnly End, int OriginalId,
    string Original, int SubstituteId, string Substitute, string? Lesson, PtcSubstituteStatus Status, string? AssignedBy, DateTime AssignedAt);

public sealed record PtcReportEvent(int Id, string Title, string Year, DateOnly Start, DateOnly End, PtcEventStatus Status, int Classes, int Rounds,
    int Bookings, int CancelledBookings, int Interviews, int Completed, int Missed, int Cancelled, int Upcoming, int InProgress, int Teachers, int ParticipatingClasses,
    int Substitutes, int? CoverageConflicts, int Feedback, int Eligible, int Responded, int OpenConcerns, int Concerns, PtcSampled AverageMinutes, PtcSampled Overall)
{
    public double? ResponseRate => Eligible == 0 ? null : 100.0 * Responded / Eligible;
    public bool HasData => Bookings > 0;
}

public sealed record PtcReportTeacher(int Id, string Name, string? Department, int Events, int Meetings, int Completed, int Missed, int Cancelled, int Upcoming,
    PtcSampled AverageMinutes, int TotalMinutes, int ParentFeedback, int TeacherFeedback, PtcSampled Communication, int Positive, int Negative, int OpenConcerns,
    int ResolvedConcerns);

public sealed record PtcReportClass(int Id, string Name, IReadOnlyList<string> Events, IReadOnlyList<string> Subjects, int Teachers, int Bookings, int Completed,
    int Missed, int Cancelled, int Feedback, PtcSampled Overall, int Concerns, int OpenConcerns);

public sealed record PtcReportDepartment(int Id, string Name, int Teachers, int Classes, int Meetings, int Completed, int Missed, int Feedback, PtcSampled Communication,
    int Concerns, int Resolved, int Unresolved);

/// <summary>One bar group of a chart: a label and one value per series.</summary>
public sealed record PtcChartRow(string Label, IReadOnlyList<double> Values, string? Note = null);

/// <summary>A server-rendered bar chart (real values only; empty when there is nothing to show).</summary>
public sealed record PtcChart(string Title, string Description, IReadOnlyList<string> Series, IReadOnlyList<PtcChartRow> Rows, string Unit = "")
{
    public bool Empty => Rows.Count == 0 || Rows.All(r => r.Values.All(v => v == 0));
    public double Max => Rows.Count == 0 ? 0 : Rows.Max(r => r.Values.DefaultIfEmpty(0).Max());
}

public sealed class PtcReportKpis
{
    public int Events { get; init; }
    public int CompletedEvents { get; init; }
    public int Bookings { get; init; }
    public int CancelledBookings { get; init; }
    public int Interviews { get; init; }
    public int Completed { get; init; }
    public int Missed { get; init; }
    public int Upcoming { get; init; }
    public int InProgress { get; init; }
    public int Cancelled { get; init; }
    public int Teachers { get; init; }
    public int Classes { get; init; }
    public int Feedback { get; init; }
    public int Eligible { get; init; }

    /// <summary>Eligible bookings with feedback (the response rate's numerator).</summary>
    public int Responded { get; init; }
    public PtcSampled Satisfaction { get; init; } = new(null, 0);
    public int OpenConcerns { get; init; }
    public int ResolvedConcerns { get; init; }
    public int Concerns { get; init; }

    public double? ResponseRate => Eligible == 0 ? null : 100.0 * Responded / Eligible;
    public double? CompletionRate => Completed + Missed == 0 ? null : 100.0 * Completed / (Completed + Missed);
    public double? CancellationRate => Interviews + Cancelled == 0 ? null : 100.0 * Cancelled / (Interviews + Cancelled);

    // attendance detail
    public PtcSampled AverageActualMinutes { get; init; } = new(null, 0);
    public PtcSampled AveragePlannedMinutes { get; init; } = new(null, 0);
    public int StartedOnTime { get; init; }
    public int Started { get; init; }
    public int EndedAfterPlanned { get; init; }
    public int Ended { get; init; }
    public int StartedNotEnded { get; init; }
}

public sealed class PtcReportData
{
    public required PtcReportFilter Filter { get; init; }
    public required DateTime GeneratedUtc { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Years { get; init; }
    public required IReadOnlyList<(int Id, string Title)> EventOptions { get; init; }
    public required IReadOnlyList<(int Id, string Name)> ClassOptions { get; init; }
    public required IReadOnlyList<(int Id, string Name)> DepartmentOptions { get; init; }
    public required IReadOnlyList<(int Id, string Name)> TeacherOptions { get; init; }

    public required PtcReportKpis Kpis { get; init; }
    public required IReadOnlyList<PtcReportEvent> Events { get; init; }
    public required IReadOnlyList<PtcReportTeacher> Teachers { get; init; }
    public required IReadOnlyList<PtcReportClass> Classes { get; init; }
    public required IReadOnlyList<PtcReportDepartment> Departments { get; init; }
    public required IReadOnlyList<PtcChart> Charts { get; init; }

    // the underlying filtered records (tables and the export)
    public required IReadOnlyList<PtcReportBooking> Bookings { get; init; }
    public required IReadOnlyList<PtcReportInterview> Interviews { get; init; }
    public required IReadOnlyList<PtcReportFeedbackItem> Feedback { get; init; }
    public required IReadOnlyList<PtcReportTeacherFeedback> TeacherFeedback { get; init; }
    public required IReadOnlyList<PtcReportSubstitute> Substitutes { get; init; }

    public IReadOnlyList<PtcReportEvent> Comparison => Events.Where(e => e.Status == PtcEventStatus.Completed).ToList();
    public bool Empty => Bookings.Count == 0;
    public string? FilterLabel(string what, int? id, IReadOnlyList<(int Id, string Name)> options) => id is { } x ? options.FirstOrDefault(o => o.Id == x).Name ?? $"#{x}" : null;
}

public sealed class PtcTeacherReport
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string? Department { get; init; }
    public required string? JobTitle { get; init; }
    public required PtcReportTeacher? Summary { get; init; }
    public required IReadOnlyList<PtcReportInterview> Interviews { get; init; }
    public required IReadOnlyList<PtcReportTeacherFeedback> Feedback { get; init; }
    public required IReadOnlyList<PtcReportFeedbackItem> Concerns { get; init; }
    public required IReadOnlyList<PtcReportSubstitute> Substitutes { get; init; }
    public required IReadOnlyList<(int Id, string Title)> Events { get; init; }
}

public interface IPtcReportService
{
    Task<PtcReportData> BuildAsync(PtcReportFilter filter, CancellationToken ct = default);
    Task<PtcTeacherReport?> TeacherAsync(int teacherId, PtcReportFilter filter, CancellationToken ct = default);
}

public sealed class PtcReportService(SchoolDbContext db, IPtcAccess access, IPtcAdminService admin, IOptions<PtcFeedbackOptions> feedbackOptions,
    ISchoolTimeService? schoolTime = null) : IPtcReportService
{
    /// <summary>Fewest answers before an average is shown.</summary>
    public const int MinSample = 3;

    /// <summary>An interview started no later than this after its planned start counts as on time.</summary>
    public const int OnTimeGraceMinutes = 2;

    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    private static PtcSampled Sample(IEnumerable<double> values, int min = MinSample)
    {
        var list = values.ToList();
        return new PtcSampled(list.Count >= min ? list.Average() : null, list.Count);
    }

    private static PtcSampled Minutes(IEnumerable<double> values) => Sample(values, 1);

    public async Task<PtcReportData> BuildAsync(PtcReportFilter f, CancellationToken ct = default)
    {
        access.Ensure();
        var now = _time.Now;
        var today = _time.Today;

        // ---- events and class schedules in scope
        var eventsQuery = db.PtcEvents.AsNoTracking().AsQueryable();
        if (f.YearId is { } y) eventsQuery = eventsQuery.Where(e => e.AcademicYearId == y);
        if (f.EventId is { } eid) eventsQuery = eventsQuery.Where(e => e.Id == eid);
        if (f.From is { } from) eventsQuery = eventsQuery.Where(e => e.EndDate >= from);
        if (f.To is { } to) eventsQuery = eventsQuery.Where(e => e.StartDate <= to);
        var events = await eventsQuery.OrderBy(e => e.StartDate).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.Title, Year = e.AcademicYear.Name, e.StartDate, e.EndDate, e.Status }).ToListAsync(ct);
        var eventIds = events.Select(e => e.Id).ToList();

        var schedulesQuery = db.PtcClassSchedules.AsNoTracking().Where(s => eventIds.Contains(s.PtcEventId) && s.Status != PtcScheduleStatus.Cancelled);
        if (f.From is { } sf) schedulesQuery = schedulesQuery.Where(s => s.EventDate >= sf);
        if (f.To is { } st) schedulesQuery = schedulesQuery.Where(s => s.EventDate <= st);
        if (f.ClassId is { } cid) schedulesQuery = schedulesQuery.Where(s => s.SchoolClassId == cid);
        var schedules = await schedulesQuery.Select(s => new
        {
            s.Id, s.PtcEventId, s.SchoolClassId, ClassName = s.SchoolClass.Name, s.EventDate, s.Status,
            Rounds = s.Rounds.Count(r => r.Status != PtcRoundStatus.Cancelled),
        }).ToListAsync(ct);
        var scheduleIds = schedules.Select(s => s.Id).ToList();
        var eventById = events.ToDictionary(e => e.Id);

        // ---- bookings, interviews, feedback of those schedules
        var rawBookings = await db.PtcParentBookings.AsNoTracking().Where(b => scheduleIds.Contains(b.PtcClassScheduleId))
            .Select(b => new
            {
                b.Id, b.BookingReference, b.PtcClassScheduleId, RoundNumber = b.Round.RoundNumber, b.SlotNumber, Student = b.Student.FullName, b.Status, b.BookedAt,
            }).ToListAsync(ct);
        var bookingIds = rawBookings.Select(b => b.Id).ToList();
        var scheduleById = schedules.ToDictionary(s => s.Id);
        var window = feedbackOptions.Value.WindowDays;
        var bookings = rawBookings.Select(b =>
        {
            var s = scheduleById[b.PtcClassScheduleId];
            var e = eventById[s.PtcEventId];
            return new PtcReportBooking(b.Id, b.BookingReference, e.Id, e.Title, s.Id, s.SchoolClassId, s.ClassName, s.EventDate, b.RoundNumber, b.SlotNumber, b.Student,
                b.Status, b.BookedAt, PtcFeedbackRules.Availability(e.Status, e.EndDate, s.Status, s.EventDate, b.Status, today, window));
        }).ToList();
        var bookingById = bookings.ToDictionary(b => b.Id);

        var rawInterviews = await db.PtcTeacherInterviews.AsNoTracking().Where(i => bookingIds.Contains(i.PtcParentBookingId))
            .Select(i => new
            {
                i.Id, i.PtcParentBookingId, i.TeacherStaffMemberId, Teacher = i.Teacher.FullName, i.BookingSubject.DepartmentId, Department = i.BookingSubject.Department.Name,
                Subject = i.BookingSubject.SubjectName, i.Date, i.ScheduledStart, i.ScheduledEnd, i.ActualStart, i.ActualEnd, i.Status,
            }).ToListAsync(ct);
        var interviews = rawInterviews.Select(i =>
        {
            var b = bookingById[i.PtcParentBookingId];
            return new PtcReportInterview(i.Id, b.Id, b.Reference, b.EventId, b.EventTitle, b.ScheduleId, b.ClassId, b.ClassName, b.RoundNumber, i.TeacherStaffMemberId,
                i.Teacher, i.DepartmentId, i.Department, i.Subject, i.Date, i.ScheduledStart, i.ScheduledEnd, i.ActualStart, i.ActualEnd,
                PtcLiveRules.State(i.Status, b.Status == PtcBookingStatus.Cancelled, i.Date, i.ScheduledEnd, now));
        }).ToList();

        var rawFeedback = await db.PtcFeedbacks.AsNoTracking().Where(x => bookingIds.Contains(x.PtcParentBookingId))
            .Select(x => new PtcReportFeedbackItem(x.Id, x.Reference, x.PtcParentBookingId, x.PtcEventId, x.OverallRating, x.CommunicationRating, x.OrganizationRating,
                x.MeetingHelpful, x.ConcernCategory, x.ConcernStatus, x.ReviewStatus, x.SubmittedAt, x.AssignedStaffMember != null ? x.AssignedStaffMember.FullName : null,
                x.Comments, x.ConcernText, x.ResolutionNotes, x.ResolvedAt)).ToListAsync(ct);
        var feedbackIds = rawFeedback.Select(x => x.Id).ToList();
        var rawTeacherFeedback = await db.PtcTeacherFeedbacks.AsNoTracking().Where(t => feedbackIds.Contains(t.PtcFeedbackId))
            .Select(t => new { t.PtcFeedbackId, t.TeacherStaffMemberId, t.CommunicationRating, t.MeetingHelpful, t.Comment }).ToListAsync(ct);

        // ---- the filters on interviews (teacher, department, state) and on feedback, applied once for everything below
        if (f.InterviewFilter)
        {
            interviews = interviews.Where(i => (f.TeacherId is null || i.TeacherId == f.TeacherId) && (f.DepartmentId is null || i.DepartmentId == f.DepartmentId)
                                               && (f.State is null || i.State == f.State)).ToList();
            var keep = interviews.Select(i => i.BookingId).ToHashSet();
            bookings = bookings.Where(b => keep.Contains(b.Id)).ToList();
        }
        var feedbackByBooking = rawFeedback.ToDictionary(x => x.BookingId);
        if (f.Feedback is { } fb)
        {
            bookings = bookings.Where(b => feedbackByBooking.TryGetValue(b.Id, out var x) ? fb switch
            {
                PtcReportFeedback.Submitted => true,
                PtcReportFeedback.WithConcern => x.ConcernStatus is not null,
                PtcReportFeedback.OpenConcern => PtcFeedbackRules.Open(x.ConcernStatus),
                PtcReportFeedback.ResolvedConcern => x.ConcernStatus is PtcConcernStatus.Resolved or PtcConcernStatus.Closed,
                _ => false,
            } : fb == PtcReportFeedback.NotSubmitted && PtcFeedbackRules.CountsForResponse(b.FeedbackAvailability)).ToList();
            var keep = bookings.Select(b => b.Id).ToHashSet();
            interviews = interviews.Where(i => keep.Contains(i.BookingId)).ToList();
        }
        var kept = bookings.Select(b => b.Id).ToHashSet();
        var feedback = rawFeedback.Where(x => kept.Contains(x.BookingId)).ToList();
        var feedbackRefs = feedback.ToDictionary(x => x.Id);
        var teacherFeedback = rawTeacherFeedback.Where(t => feedbackRefs.ContainsKey(t.PtcFeedbackId))
            .Select(t => { var x = feedbackRefs[t.PtcFeedbackId]; return new PtcReportTeacherFeedback(x.Id, x.Reference, x.BookingId, x.EventId, t.TeacherStaffMemberId, t.CommunicationRating, t.MeetingHelpful, t.Comment, x.SubmittedAt); })
            // Attributed only to teachers the parent was scheduled to meet in the filtered interviews.
            .Where(t => interviews.Any(i => i.BookingId == t.BookingId && i.TeacherId == t.TeacherId))
            .ToList();

        // ---- substitutes and live coverage (coverage is computed live only for events still being prepared or run)
        var substitutes = await db.PtcSubstituteAssignments.AsNoTracking().Where(a => scheduleIds.Contains(a.PtcClassScheduleId))
            .Select(a => new
            {
                a.Id, a.ClassSchedule.PtcEventId, ClassName = a.ClassSchedule.SchoolClass.Name, a.ClassSchedule.EventDate, a.StartTime, a.EndTime,
                a.OriginalTeacherStaffMemberId, Original = a.OriginalTeacher.FullName, a.SubstituteTeacherStaffMemberId, Substitute = a.SubstituteTeacher.FullName,
                Lesson = a.Lesson != null ? a.Lesson.SchoolClass.Name + " · " + a.Lesson.Subject : null, a.Status, a.AssignedBy, a.CreatedAt,
            }).ToListAsync(ct);
        var subs = substitutes.Where(a => f.TeacherId is null || a.OriginalTeacherStaffMemberId == f.TeacherId || a.SubstituteTeacherStaffMemberId == f.TeacherId)
            .Select(a => new PtcReportSubstitute(a.Id, a.PtcEventId, eventById[a.PtcEventId].Title, a.ClassName, a.EventDate, a.StartTime, a.EndTime,
                a.OriginalTeacherStaffMemberId, a.Original, a.SubstituteTeacherStaffMemberId, a.Substitute, a.Lesson, a.Status, a.AssignedBy, a.CreatedAt)).ToList();
        var coverage = new Dictionary<int, int>();
        foreach (var e in events.Where(e => e.Status is PtcEventStatus.Draft or PtcEventStatus.Published))
            if (await admin.EventAsync(e.Id, ct) is { } detail) coverage[e.Id] = detail.Coverage.Unresolved;

        // ---- meetings (teacher × booking)
        var meetings = interviews.GroupBy(i => (i.BookingId, i.TeacherId)).Select(g => g.First()).ToList();
        double? Duration(PtcReportInterview m) => m.State == PtcLiveState.Completed && m.ActualStart is { } s && m.ActualEnd is { } e && e >= s ? (e - s).TotalMinutes : null;
        var active = interviews.Where(i => i.State != PtcLiveState.Cancelled).ToList();
        var confirmed = bookings.Where(b => b.Status == PtcBookingStatus.Confirmed).ToList();
        var eligible = confirmed.Where(b => PtcFeedbackRules.CountsForResponse(b.FeedbackAvailability)).ToList();
        var eligibleIds = eligible.Select(b => b.Id).ToHashSet();
        var started = meetings.Where(m => m.ActualStart is not null).ToList();
        var ended = meetings.Where(m => m.ActualEnd is not null).ToList();

        var kpis = new PtcReportKpis
        {
            Events = events.Count, CompletedEvents = events.Count(e => e.Status == PtcEventStatus.Completed),
            Bookings = confirmed.Count, CancelledBookings = bookings.Count - confirmed.Count,
            Interviews = active.Count, Completed = interviews.Count(i => i.State == PtcLiveState.Completed), Missed = interviews.Count(i => i.State == PtcLiveState.Missed),
            Upcoming = interviews.Count(i => i.State == PtcLiveState.Upcoming), InProgress = interviews.Count(i => i.State == PtcLiveState.InProgress),
            Cancelled = interviews.Count(i => i.State == PtcLiveState.Cancelled),
            Teachers = active.Select(i => i.TeacherId).Distinct().Count(), Classes = confirmed.Select(b => b.ClassId).Distinct().Count(),
            Feedback = feedback.Count, Eligible = eligible.Count, Responded = feedback.Count(x => eligibleIds.Contains(x.BookingId)),
            Satisfaction = Sample(feedback.Where(x => x.Overall is not null).Select(x => (double)(int)x.Overall!.Value)),
            Concerns = feedback.Count(x => x.ConcernStatus is not null), OpenConcerns = feedback.Count(x => PtcFeedbackRules.Open(x.ConcernStatus)),
            ResolvedConcerns = feedback.Count(x => x.ConcernStatus is PtcConcernStatus.Resolved or PtcConcernStatus.Closed),
            AverageActualMinutes = Minutes(meetings.Select(Duration).OfType<double>()),
            AveragePlannedMinutes = Minutes(meetings.Where(m => m.State != PtcLiveState.Cancelled).Select(m => (double)m.PlannedMinutes)),
            Started = started.Count,
            StartedOnTime = started.Count(m => _time.ToLocal(m.ActualStart!.Value) <= m.Date.ToDateTime(m.PlannedStart).AddMinutes(OnTimeGraceMinutes)),
            Ended = ended.Count, EndedAfterPlanned = ended.Count(m => _time.ToLocal(m.ActualEnd!.Value) > m.Date.ToDateTime(m.PlannedEnd)),
            StartedNotEnded = meetings.Count(m => m.State == PtcLiveState.InProgress && m.Date.ToDateTime(m.PlannedEnd) <= now),
        };

        // ---- per event
        var eventRows = events.Select(e =>
        {
            var mySchedules = schedules.Where(s => s.PtcEventId == e.Id).ToList();
            var myBookings = bookings.Where(b => b.EventId == e.Id).ToList();
            var myConfirmed = myBookings.Where(b => b.Status == PtcBookingStatus.Confirmed).ToList();
            var myInterviews = interviews.Where(i => i.EventId == e.Id).ToList();
            var myEligible = myConfirmed.Where(b => PtcFeedbackRules.CountsForResponse(b.FeedbackAvailability)).Select(b => b.Id).ToHashSet();
            var myFeedback = feedback.Where(x => x.EventId == e.Id).ToList();
            return new PtcReportEvent(e.Id, e.Title, e.Year, e.StartDate, e.EndDate, e.Status, mySchedules.Count, mySchedules.Sum(s => s.Rounds),
                myConfirmed.Count, myBookings.Count - myConfirmed.Count, myInterviews.Count(i => i.State != PtcLiveState.Cancelled),
                myInterviews.Count(i => i.State == PtcLiveState.Completed), myInterviews.Count(i => i.State == PtcLiveState.Missed),
                myInterviews.Count(i => i.State == PtcLiveState.Cancelled), myInterviews.Count(i => i.State == PtcLiveState.Upcoming),
                myInterviews.Count(i => i.State == PtcLiveState.InProgress), myInterviews.Where(i => i.State != PtcLiveState.Cancelled).Select(i => i.TeacherId).Distinct().Count(),
                myConfirmed.Select(b => b.ClassId).Distinct().Count(), subs.Count(a => a.EventId == e.Id && a.Status == PtcSubstituteStatus.Active),
                coverage.TryGetValue(e.Id, out var c) ? c : null, myFeedback.Count, myEligible.Count, myFeedback.Count(x => myEligible.Contains(x.BookingId)),
                myFeedback.Count(x => PtcFeedbackRules.Open(x.ConcernStatus)), myFeedback.Count(x => x.ConcernStatus is not null),
                Minutes(meetings.Where(m => m.EventId == e.Id).Select(Duration).OfType<double>()),
                Sample(myFeedback.Where(x => x.Overall is not null).Select(x => (double)(int)x.Overall!.Value)));
        }).ToList();

        // ---- per teacher (operational metrics, parent satisfaction from teacher-specific feedback only, concerns of their parents)
        var teacherIds = meetings.Select(m => m.TeacherId).Distinct().ToList();
        var staff = await db.StaffMembers.AsNoTracking().Where(s => teacherIds.Contains(s.Id))
            .Select(s => new { s.Id, Department = s.Department != null ? s.Department.Name : null }).ToDictionaryAsync(s => s.Id, s => s.Department, ct);
        var teacherRows = meetings.GroupBy(m => m.TeacherId).Select(g =>
        {
            var mine = g.ToList();
            var myBookings = mine.Where(m => m.State != PtcLiveState.Cancelled).Select(m => m.BookingId).ToHashSet();
            var mySpecific = teacherFeedback.Where(t => t.TeacherId == g.Key).ToList();
            var parentFeedback = feedback.Where(x => myBookings.Contains(x.BookingId)).ToList();
            return new PtcReportTeacher(g.Key, mine[0].TeacherName, staff.GetValueOrDefault(g.Key), mine.Select(m => m.EventId).Distinct().Count(),
                mine.Count(m => m.State != PtcLiveState.Cancelled), mine.Count(m => m.State == PtcLiveState.Completed), mine.Count(m => m.State == PtcLiveState.Missed),
                mine.Count(m => m.State == PtcLiveState.Cancelled), mine.Count(m => m.State == PtcLiveState.Upcoming),
                Minutes(mine.Select(Duration).OfType<double>()), (int)Math.Round(mine.Select(Duration).OfType<double>().Sum()),
                parentFeedback.Count, mySpecific.Count, Sample(mySpecific.Where(t => t.Communication is not null).Select(t => (double)(int)t.Communication!.Value)),
                mySpecific.Count(t => PtcFeedbackRules.Positive(t.Communication)), mySpecific.Count(t => PtcFeedbackRules.Negative(t.Communication)),
                parentFeedback.Count(x => PtcFeedbackRules.Open(x.ConcernStatus)),
                parentFeedback.Count(x => x.ConcernStatus is PtcConcernStatus.Resolved or PtcConcernStatus.Closed));
        }).OrderBy(t => t.Name).ToList();

        // ---- per class: every active class (all 25, KG and PreK included), plus any other class in scope
        var scheduledClasses = schedules.Select(s => s.SchoolClassId).Distinct().ToList();
        var classList = await db.Classes.AsNoTracking().Where(c => c.IsActive || scheduledClasses.Contains(c.Id))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var classRows = classList.Where(c => f.ClassId is null || c.Id == f.ClassId).Select(c =>
        {
            var myConfirmed = confirmed.Where(b => b.ClassId == c.Id).Select(b => b.Id).ToHashSet();
            var myInterviews = interviews.Where(i => i.ClassId == c.Id).ToList();
            var myFeedback = feedback.Where(x => myConfirmed.Contains(x.BookingId)).ToList();
            return new PtcReportClass(c.Id, c.Name, bookings.Where(b => b.ClassId == c.Id).Select(b => b.EventTitle).Distinct().ToList(),
                myInterviews.Where(i => i.State != PtcLiveState.Cancelled).Select(i => i.Subject).Distinct().OrderBy(s => s).ToList(),
                myInterviews.Where(i => i.State != PtcLiveState.Cancelled).Select(i => i.TeacherId).Distinct().Count(), myConfirmed.Count,
                myInterviews.Count(i => i.State == PtcLiveState.Completed), myInterviews.Count(i => i.State == PtcLiveState.Missed),
                myInterviews.Count(i => i.State == PtcLiveState.Cancelled), myFeedback.Count,
                Sample(myFeedback.Where(x => x.Overall is not null).Select(x => (double)(int)x.Overall!.Value)),
                myFeedback.Count(x => x.ConcernStatus is not null), myFeedback.Count(x => PtcFeedbackRules.Open(x.ConcernStatus)));
        }).ToList();

        // ---- per department (the subject's department of each interview, from the timetable lesson it came from)
        var departmentRows = interviews.GroupBy(i => i.DepartmentId).Select(g =>
        {
            var mine = g.ToList();
            var myActive = mine.Where(i => i.State != PtcLiveState.Cancelled).ToList();
            var myBookings = myActive.Select(i => i.BookingId).ToHashSet();
            var pairs = myActive.Select(i => (i.BookingId, i.TeacherId)).ToHashSet();
            var myFeedback = feedback.Where(x => myBookings.Contains(x.BookingId)).ToList();
            return new PtcReportDepartment(g.Key, mine[0].Department, myActive.Select(i => i.TeacherId).Distinct().Count(), myActive.Select(i => i.ClassId).Distinct().Count(),
                pairs.Count, mine.Count(i => i.State == PtcLiveState.Completed), mine.Count(i => i.State == PtcLiveState.Missed), myFeedback.Count,
                Sample(teacherFeedback.Where(t => pairs.Contains((t.BookingId, t.TeacherId)) && t.Communication is not null).Select(t => (double)(int)t.Communication!.Value)),
                myFeedback.Count(x => x.ConcernStatus is not null), myFeedback.Count(x => x.ConcernStatus is PtcConcernStatus.Resolved or PtcConcernStatus.Closed),
                myFeedback.Count(x => PtcFeedbackRules.Open(x.ConcernStatus)));
        }).OrderBy(d => d.Name).ToList();

        // ---- charts (all from the same filtered records)
        PtcChartRow Dist(PtcRating r, Func<PtcReportFeedbackItem, PtcRating?> pick) => new(r.ToString(), [feedback.Count(x => pick(x) == r)]);
        var ratings = Enum.GetValues<PtcRating>().OrderByDescending(r => r).ToList();
        var charts = new List<PtcChart>
        {
            new("Event performance", "Parent bookings, completed and missed interviews per event.", ["Parent bookings", "Completed interviews", "Missed interviews"],
                eventRows.Select(e => new PtcChartRow(e.Title, [e.Bookings, e.Completed, e.Missed])).ToList()),
            new("Overall experience", "Parents' overall rating.", ["Answers"], ratings.Select(r => Dist(r, x => x.Overall)).ToList()),
            new("Teacher communication", "Parents' rating of teacher communication (general question).", ["Answers"], ratings.Select(r => Dist(r, x => x.Communication)).ToList()),
            new("Organization & scheduling", "Parents' rating of the organization.", ["Answers"], ratings.Select(r => Dist(r, x => x.Organization)).ToList()),
            new("Positive vs negative", "Overall rating: positive = Excellent or Good, neutral = Average, negative = Poor.", ["Answers"],
            [
                new("Positive", [feedback.Count(x => PtcFeedbackRules.Positive(x.Overall))]), new("Neutral", [feedback.Count(x => x.Overall == PtcRating.Average)]),
                new("Negative", [feedback.Count(x => PtcFeedbackRules.Negative(x.Overall))]),
            ]),
            new("Interview outcomes", "Teacher interviews by state (server-recorded).", ["Interviews"],
            [
                new("Completed", [kpis.Completed]), new("Missed", [kpis.Missed]), new("In progress", [kpis.InProgress]), new("Upcoming", [kpis.Upcoming]),
                new("Cancelled", [kpis.Cancelled]),
            ]),
            new("Planned vs actual duration", "Average minutes per completed meeting (actual from Start/End stamps).", ["Planned", "Actual"],
                kpis.AverageActualMinutes.Value is { } actual
                    ? [new("All completed meetings", [Math.Round(meetings.Where(m => Duration(m) is not null).Average(m => (double)m.PlannedMinutes), 1), Math.Round(actual, 1)])]
                    : [], " min"),
            new("Interviews by Round", "Teacher interviews (not cancelled) by Round number.", ["Interviews", "Completed"],
                active.GroupBy(i => i.RoundNumber).OrderBy(g => g.Key).Select(g => new PtcChartRow($"Round {g.Key}", [g.Count(), g.Count(i => i.State == PtcLiveState.Completed)])).ToList()),
            new("Parent bookings by class", "Confirmed parent bookings per class.", ["Bookings"],
                classRows.Where(c => c.Bookings > 0).Select(c => new PtcChartRow(c.Name, [c.Bookings])).ToList()),
            new("Interview completion by class", "Completed share of completed + missed interviews.", ["Completion %"],
                classRows.Where(c => c.Completed + c.Missed > 0).Select(c => new PtcChartRow(c.Name, [Math.Round(100.0 * c.Completed / (c.Completed + c.Missed), 1)], $"{c.Completed} of {c.Completed + c.Missed}")).ToList(), "%"),
            new("Participation by department", "Parent meetings per department (subject's department).", ["Meetings", "Teachers"],
                departmentRows.Select(d => new PtcChartRow(d.Name, [d.Meetings, d.Teachers])).ToList()),
            new("Concerns by status", "Parent concerns and where they stand.", ["Concerns"],
                Enum.GetValues<PtcConcernStatus>().Select(s => new PtcChartRow(PtcFeedbackRules.Label(s), [feedback.Count(x => x.ConcernStatus == s)])).ToList()),
            new("Concerns by category", "What parents' concerns were about.", ["Concerns"],
                Enum.GetValues<PtcConcernCategory>().Select(c => new PtcChartRow(PtcFeedbackRules.Label(c), [feedback.Count(x => x.ConcernStatus is not null && x.Category == c)])).ToList()),
        };

        // ---- filter options
        var years = await db.AcademicYears.AsNoTracking().OrderByDescending(x => x.Id).Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        var eventOptions = await db.PtcEvents.AsNoTracking().OrderByDescending(e => e.StartDate).Select(e => new { e.Id, e.Title }).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var teachers = await db.PtcTeacherInterviews.AsNoTracking().Select(i => new { i.TeacherStaffMemberId, i.Teacher.FullName }).Distinct().OrderBy(t => t.FullName).ToListAsync(ct);

        return new PtcReportData
        {
            Filter = f, GeneratedUtc = _time.UtcNow,
            Years = years.Select(x => (x.Id, x.Name)).ToList(), EventOptions = eventOptions.Select(e => (e.Id, e.Title)).ToList(),
            ClassOptions = classList.Select(c => (c.Id, c.Name)).ToList(), DepartmentOptions = departments.Select(d => (d.Id, d.Name)).ToList(),
            TeacherOptions = teachers.Select(t => (t.TeacherStaffMemberId, t.FullName)).ToList(),
            Kpis = kpis, Events = eventRows, Teachers = teacherRows, Classes = classRows, Departments = departmentRows, Charts = charts,
            Bookings = bookings, Interviews = interviews.OrderBy(i => i.Date).ThenBy(i => i.PlannedStart).ThenBy(i => i.TeacherName).ToList(),
            Feedback = feedback.OrderByDescending(x => x.SubmittedAt).ToList(), TeacherFeedback = teacherFeedback, Substitutes = subs,
        };
    }

    public async Task<PtcTeacherReport?> TeacherAsync(int teacherId, PtcReportFilter filter, CancellationToken ct = default)
    {
        access.Ensure();
        var person = await db.StaffMembers.AsNoTracking().Where(s => s.Id == teacherId)
            .Select(s => new { s.Id, s.FullName, s.JobTitle, Department = s.Department != null ? s.Department.Name : null }).FirstOrDefaultAsync(ct);
        if (person is null) return null;
        var data = await BuildAsync(new PtcReportFilter
        {
            YearId = filter.YearId, EventId = filter.EventId, From = filter.From, To = filter.To, ClassId = filter.ClassId, DepartmentId = filter.DepartmentId,
            TeacherId = teacherId, State = filter.State, Feedback = filter.Feedback,
        }, ct);
        var mine = data.Interviews.Where(i => i.TeacherId == teacherId).ToList();
        var bookings = mine.Where(i => i.State != PtcLiveState.Cancelled).Select(i => i.BookingId).ToHashSet();
        return new PtcTeacherReport
        {
            Id = person.Id, Name = person.FullName, Department = person.Department, JobTitle = person.JobTitle,
            Summary = data.Teachers.FirstOrDefault(t => t.Id == teacherId), Interviews = mine,
            Feedback = data.TeacherFeedback.Where(t => t.TeacherId == teacherId).OrderByDescending(t => t.SubmittedAt).ToList(),
            Concerns = data.Feedback.Where(x => x.ConcernStatus is not null && bookings.Contains(x.BookingId)).ToList(),
            Substitutes = data.Substitutes.Where(s => s.OriginalId == teacherId || s.SubstituteId == teacherId).ToList(),
            Events = mine.Select(i => (i.EventId, i.EventTitle)).Distinct().ToList(),
        };
    }
}
