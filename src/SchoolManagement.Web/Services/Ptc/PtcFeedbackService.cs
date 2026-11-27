using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

// ============================================================ PTC Phase 7: parent feedback
//
// Eligibility (one definition, used by the parent pages, the admin links and the response rate):
//   a booking is ELIGIBLE when it is Confirmed, its class schedule is not cancelled, the event is Published or Completed and
//   the child's class PTC is over: the event is Completed, or the class's PTC date is before today (school time).
//   Feedback is OPEN for an eligible booking until FeedbackWindowDays after the event's last date; then it is CLOSED.
//   Response rate = eligible bookings with feedback / eligible bookings × 100 (cancelled bookings are never eligible).
// Links carry a signed, time-limited code (ASP.NET Data Protection) naming the booking; nothing about it is stored, so the
// database never holds a usable link, and an expired or altered code simply does not open.

public sealed class PtcFeedbackOptions
{
    public const string Section = "PtcFeedback";

    /// <summary>Days after the event's last date during which parents can still give feedback.</summary>
    public int WindowDays { get; set; } = 30;
}

public enum PtcFeedbackAvailability
{
    /// <summary>The booking cannot give feedback (cancelled, Draft or cancelled event, cancelled class).</summary>
    NotEligible = 0,

    /// <summary>The class's PTC is not over yet.</summary>
    NotYet = 1,
    Open = 2,

    /// <summary>The feedback period has ended.</summary>
    Closed = 3,
}

public static class PtcFeedbackRules
{
    /// <summary>The single eligibility rule (see the file header).</summary>
    public static PtcFeedbackAvailability Availability(PtcEventStatus eventStatus, DateOnly eventEnd, PtcScheduleStatus scheduleStatus, DateOnly classDate,
        PtcBookingStatus bookingStatus, DateOnly today, int windowDays)
    {
        if (bookingStatus != PtcBookingStatus.Confirmed || scheduleStatus == PtcScheduleStatus.Cancelled
            || eventStatus is not (PtcEventStatus.Published or PtcEventStatus.Completed)) return PtcFeedbackAvailability.NotEligible;
        if (eventStatus != PtcEventStatus.Completed && classDate >= today) return PtcFeedbackAvailability.NotYet;
        return today > ClosesOn(eventEnd, windowDays) ? PtcFeedbackAvailability.Closed : PtcFeedbackAvailability.Open;
    }

    /// <summary>The last day feedback is accepted.</summary>
    public static DateOnly ClosesOn(DateOnly eventEnd, int windowDays) => eventEnd.AddDays(Math.Max(0, windowDays));

    /// <summary>Eligible for the response rate: the class's PTC is over (feedback open or since closed).</summary>
    public static bool CountsForResponse(PtcFeedbackAvailability a) => a is PtcFeedbackAvailability.Open or PtcFeedbackAvailability.Closed;

    /// <summary>Positive: Excellent or Good; neutral: Average; negative: Poor.</summary>
    public static bool Positive(PtcRating? r) => r is PtcRating.Excellent or PtcRating.Good;
    public static bool Negative(PtcRating? r) => r == PtcRating.Poor;

    public static string Label(PtcRating r) => r.ToString();
    public static string Label(PtcHelpful h) => h.ToString();
    public static string Label(PtcConcernCategory c) => c switch
    {
        PtcConcernCategory.Scheduling => "Scheduling",
        PtcConcernCategory.TeacherInteraction => "Teacher interaction",
        PtcConcernCategory.AcademicProgress => "Academic progress",
        PtcConcernCategory.Communication => "Communication",
        PtcConcernCategory.Organization => "Organization & facilities",
        _ => "Other",
    };
    public static string Label(PtcConcernStatus s) => s switch
    {
        PtcConcernStatus.UnderReview => "Under review",
        PtcConcernStatus.InProgress => "In progress",
        _ => s.ToString(),
    };
    public static string Badge(PtcConcernStatus s) => s switch
    {
        PtcConcernStatus.New => "badge--error",
        PtcConcernStatus.UnderReview or PtcConcernStatus.InProgress => "badge--warning",
        _ => "badge--success",
    };
    public static string Label(PtcFeedbackActionKind k) => k switch
    {
        PtcFeedbackActionKind.ConcernStatusChanged => "Concern status changed",
        PtcFeedbackActionKind.NoteAdded => "Internal note",
        _ => k.ToString(),
    };
    public static bool Open(PtcConcernStatus? s) => s is PtcConcernStatus.New or PtcConcernStatus.UnderReview or PtcConcernStatus.InProgress;

    /// <summary>Feedback text as written (Arabic or English): trimmed, line breaks kept, control characters removed, at most <paramref name="max"/>.</summary>
    public static string? Text(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Replace("\r\n", "\n").Replace('\r', '\n');
        s = new string(s.Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray());
        s = Regex.Replace(s, "[ \t]+", " ");
        s = Regex.Replace(s, "\n{3,}", "\n\n").Trim();
        return s.Length == 0 ? null : s.Length > max ? s[..max] : s;
    }
}

// ------------------------------------------------------------------ public models

public sealed record PtcFeedbackEvent(int Id, string Token, string Title, DateOnly StartDate, DateOnly EndDate, PtcEventStatus Status);

/// <summary>A child of the identified parent and whether feedback can be given for their booking (with its link code).</summary>
public sealed record PtcFeedbackChild(string FirstName, string ClassName, PtcFeedbackAvailability Availability, bool Submitted, string? FeedbackToken);

public sealed record PtcFeedbackIdentify(PortalIdentifyStatus Status, IReadOnlyList<PtcFeedbackChild> Children);

/// <summary>A teacher the parent was scheduled to meet (opaque code; no id reaches the browser).</summary>
public sealed record PtcFeedbackTeacher(string Code, string Name, string Subjects);

public sealed class PtcFeedbackForm
{
    public required string Token { get; init; }
    public required string EventTitle { get; init; }
    public required DateOnly Date { get; init; }
    public required string ChildFirstName { get; init; }
    public required string ClassName { get; init; }
    public required PtcFeedbackAvailability Availability { get; init; }
    public required DateOnly ClosesOn { get; init; }
    public required IReadOnlyList<PtcFeedbackTeacher> Teachers { get; init; }

    /// <summary>Set when feedback was already given: the reference and when (nothing else is shown again).</summary>
    public string? SubmittedReference { get; init; }
    public DateTime? SubmittedAt { get; init; }
}

public sealed record PtcTeacherFeedbackInput(string Code, string? Communication, string? Helpful, string? Comment);

public sealed record PtcFeedbackInput(string? Overall, string? Communication, string? Organization, string? Helpful, string? Comments,
    string? ConcernCategory, string? Concern, IReadOnlyList<PtcTeacherFeedbackInput> Teachers);

public sealed record PtcFeedbackSubmitResult(bool Succeeded, string? Reference, string? Error, bool AlreadySubmitted = false, bool Unavailable = false);

// ------------------------------------------------------------------ admin models

public sealed class PtcFeedbackFilter
{
    public int? EventId { get; set; }
    public DateOnly? Date { get; set; }
    public int? ClassId { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeacherId { get; set; }
    public PtcRating? Rating { get; set; }
    public PtcConcernStatus? ConcernStatus { get; set; }
    public bool? HasConcern { get; set; }
    public PtcFeedbackReview? Review { get; set; }
    public DateOnly? SubmittedFrom { get; set; }
    public DateOnly? SubmittedTo { get; set; }
    public string? Q { get; set; }
}

public sealed record PtcFeedbackRow(int Id, string Reference, int EventId, string EventTitle, DateOnly Date, int ClassId, string ClassName, string BookingReference,
    DateTime SubmittedAt, PtcRating? Overall, PtcRating? Communication, PtcRating? Organization, PtcHelpful? Helpful, PtcConcernCategory? Category,
    PtcConcernStatus? ConcernStatus, PtcFeedbackReview Review, string? AssignedTo, int TeacherComments);

public sealed class PtcFeedbackSummary
{
    public int Submissions { get; init; }
    public int Events { get; init; }
    public int RatedOverall { get; init; }
    public double? AverageOverall { get; init; }
    public int Positive { get; init; }
    public int Negative { get; init; }
    public int Concerns { get; init; }
    public int Unresolved { get; init; }
    public int Resolved { get; init; }
    public int PendingReview { get; init; }
}

public sealed class PtcFeedbackList
{
    public required IReadOnlyList<PtcFeedbackRow> Rows { get; init; }
    public required PtcFeedbackSummary Summary { get; init; }
    public required IReadOnlyList<(int Id, string Title)> Events { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Classes { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Departments { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Teachers { get; init; }
}

public sealed record PtcTeacherFeedbackRow(int TeacherId, string Teacher, string Subjects, PtcRating? Communication, PtcHelpful? Helpful, string? Comment);
public sealed record PtcFeedbackHistoryRow(DateTime At, PtcFeedbackActionKind Kind, string? OldValue, string? NewValue, string? Note, string? By);

public sealed class PtcFeedbackDetails
{
    public required PtcFeedbackRow Row { get; init; }
    public required string? Comments { get; init; }
    public required string? Concern { get; init; }
    public required string? ReviewedBy { get; init; }
    public required DateTime? ReviewedAt { get; init; }
    public required int? AssignedStaffId { get; init; }
    public required string? AssignedBy { get; init; }
    public required DateTime? AssignedAt { get; init; }
    public required string? Resolution { get; init; }
    public required DateTime? ResolvedAt { get; init; }
    public required string? ResolvedBy { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<PtcTeacherFeedbackRow> Teachers { get; init; }
    public required IReadOnlyList<PtcFeedbackHistoryRow> History { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Staff { get; init; }
}

/// <summary>An eligible (or not yet eligible) booking of an event and its feedback link state, for the Admin's manual sharing.</summary>
public sealed record PtcFeedbackLinkRow(string BookingReference, string ChildFirstName, string ClassName, DateOnly Date, PtcFeedbackAvailability Availability,
    string? FeedbackReference, string? Url);

public sealed class PtcFeedbackLinks
{
    public required int EventId { get; init; }
    public required PtcEventStatus Status { get; init; }
    public required DateOnly ClosesOn { get; init; }
    public required string? EventUrl { get; init; }
    public required IReadOnlyList<PtcFeedbackLinkRow> Bookings { get; init; }
    public int Eligible => Bookings.Count(b => PtcFeedbackRules.CountsForResponse(b.Availability));
    public int Submitted => Bookings.Count(b => PtcFeedbackRules.CountsForResponse(b.Availability) && b.FeedbackReference is not null);
    public double? ResponseRate => Eligible == 0 ? null : 100.0 * Submitted / Eligible;
}

public interface IPtcFeedbackService
{
    // public (parents)
    Task<PtcFeedbackEvent?> EventAsync(string? publicToken, CancellationToken ct = default);
    Task<PtcFeedbackIdentify> IdentifyAsync(PtcFeedbackEvent ev, string? phone, string? firstName, CancellationToken ct = default);
    Task<PtcFeedbackForm?> FormAsync(string? token, CancellationToken ct = default);
    Task<PtcFeedbackSubmitResult> SubmitAsync(string? token, PtcFeedbackInput input, CancellationToken ct = default);

    // Super Admin
    Task<PtcFeedbackLinks?> LinksAsync(int eventId, string siteBaseUrl, CancellationToken ct = default);
    Task<PtcFeedbackList> ListAsync(PtcFeedbackFilter filter, CancellationToken ct = default);
    Task<PtcFeedbackDetails?> DetailsAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> MarkReviewedAsync(int id, string? version, CancellationToken ct = default);
    Task<ServiceResult> SetConcernStatusAsync(int id, PtcConcernStatus status, string? note, string? version, CancellationToken ct = default);
    Task<ServiceResult> AddNoteAsync(int id, string? note, CancellationToken ct = default);
    Task<ServiceResult> AssignAsync(int id, int? staffId, string? version, CancellationToken ct = default);
    Task<ServiceResult> ResolveAsync(int id, string? resolution, string? version, CancellationToken ct = default);
    Task<ServiceResult> ReopenAsync(int id, string? reason, string? version, CancellationToken ct = default);
}

public sealed class PtcFeedbackService(SchoolDbContext db, IPtcAccess access, ICurrentUser currentUser, IPtcPortalService portal, IDataProtectionProvider protection,
    IOptions<PtcFeedbackOptions> options, ISchoolTimeService? schoolTime = null) : IPtcFeedbackService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("SchoolManagement.Ptc.Feedback").ToTimeLimitedDataProtector();
    private int Window => options.Value.WindowDays;

    public const string NotAvailableMessage = "This feedback link is not valid or has expired. Please use the link the school sent you, or contact the school.";
    public const string ChangedMessage = "This feedback record was changed by someone else in the meantime. Reload the page and try again.";
    public const string EmptyMessage = "Please answer at least one question or write a comment before sending.";

    public static string FeedbackUrl(string baseUrl, string token) => baseUrl.TrimEnd('/') + "/ptc-feedback/f/" + Uri.EscapeDataString(token);
    public static string EventUrl(string baseUrl, string publicToken) => baseUrl.TrimEnd('/') + "/ptc-feedback/" + Uri.EscapeDataString(publicToken);

    // ------------------------------------------------------------------ tokens

    /// <summary>A link code for the booking, valid until the end of the feedback period (null when that has passed).</summary>
    private string? NewToken(int bookingId, int studentId, DateOnly eventEnd)
    {
        var until = _time.ToUtc(PtcFeedbackRules.ClosesOn(eventEnd, Window).AddDays(1), TimeOnly.MinValue);
        var life = until - _time.UtcNow;
        return life <= TimeSpan.Zero ? null : _protector.Protect($"fb|{bookingId}|{studentId}", life);
    }

    private (int BookingId, int StudentId)? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1000) return null;
        string payload;
        try { payload = _protector.Unprotect(token); }
        catch (CryptographicException) { return null; }
        var p = payload.Split('|');
        return p.Length == 3 && p[0] == "fb" && int.TryParse(p[1], NumberStyles.None, C, out var b) && int.TryParse(p[2], NumberStyles.None, C, out var s) ? (b, s) : null;
    }

    private static string TeacherCode(int bookingId, int teacherId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"ptc-feedback|{bookingId}|{teacherId}")))[..12];

    private static string NewReference() =>
        "FB-" + RandomNumberGenerator.GetString("23456789ABCDEFGHJKLMNPQRSTUVWXYZ", 8);

    private static string FirstName(string fullName) => TextNormalizer.CleanText(fullName).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fullName;

    private sealed record BookingContext(int Id, int StudentId, string StudentName, int EventId, string EventTitle, PtcEventStatus EventStatus, DateOnly EventEnd,
        int ScheduleId, PtcScheduleStatus ScheduleStatus, DateOnly Date, string ClassName, PtcBookingStatus Status);

    private Task<BookingContext?> BookingAsync(int bookingId, CancellationToken ct) =>
        db.PtcParentBookings.AsNoTracking().Where(b => b.Id == bookingId)
            .Select(b => new BookingContext(b.Id, b.StudentId, b.Student.FullName, b.ClassSchedule.PtcEventId, b.ClassSchedule.PtcEvent.Title, b.ClassSchedule.PtcEvent.Status,
                b.ClassSchedule.PtcEvent.EndDate, b.PtcClassScheduleId, b.ClassSchedule.Status, b.ClassSchedule.EventDate, b.ClassSchedule.SchoolClass.Name, b.Status))
            .FirstOrDefaultAsync(ct);

    private PtcFeedbackAvailability Availability(BookingContext b) =>
        PtcFeedbackRules.Availability(b.EventStatus, b.EventEnd, b.ScheduleStatus, b.Date, b.Status, _time.Today, Window);

    /// <summary>The teachers the parent was scheduled to meet in the booking (interviews not cancelled).</summary>
    private async Task<List<(int Id, string Name, string Subjects)>> TeachersAsync(int bookingId, CancellationToken ct) =>
        (await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.PtcParentBookingId == bookingId && i.Status != PtcInterviewStatus.Cancelled)
            .Select(i => new { i.TeacherStaffMemberId, i.Teacher.FullName, i.BookingSubject.SubjectName }).ToListAsync(ct))
        .GroupBy(i => i.TeacherStaffMemberId)
        .Select(g => (g.Key, g.First().FullName, string.Join(", ", g.Select(x => x.SubjectName).Distinct())))
        .OrderBy(t => t.FullName).ToList();

    // ------------------------------------------------------------------ public

    public async Task<PtcFeedbackEvent?> EventAsync(string? publicToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(publicToken) || publicToken.Length > PtcEvent.PublicTokenMaxLength) return null;
        var ev = await db.PtcEvents.AsNoTracking()
            .Where(e => e.PublicToken == publicToken && (e.Status == PtcEventStatus.Published || e.Status == PtcEventStatus.Completed))
            .Select(e => new { e.Id, e.Title, e.StartDate, e.EndDate, e.Status, e.PublicToken }).FirstOrDefaultAsync(ct);
        return ev is null || !string.Equals(ev.PublicToken, publicToken, StringComparison.Ordinal) ? null
            : new PtcFeedbackEvent(ev.Id, publicToken, ev.Title, ev.StartDate, ev.EndDate, ev.Status);
    }

    public async Task<PtcFeedbackIdentify> IdentifyAsync(PtcFeedbackEvent ev, string? phone, string? firstName, CancellationToken ct = default)
    {
        // The same identification as booking (registered number + child's first name, throttled per number).
        var found = await portal.IdentifyAsync(new PortalEvent(ev.Id, ev.Token, ev.Title, null, ev.StartDate, ev.EndDate, false), phone, firstName, ct);
        if (found.Status != PortalIdentifyStatus.Found) return new(found.Status, []);
        var children = new List<PtcFeedbackChild>();
        foreach (var child in found.Children)
        {
            var studentId = await portal.ChildStudentAsync(ev.Id, child.Token, ct);
            var bookingId = studentId is null ? null : await db.PtcParentBookings.AsNoTracking()
                .Where(b => b.StudentId == studentId && b.ClassSchedule.PtcEventId == ev.Id && b.Status == PtcBookingStatus.Confirmed)
                .Select(b => (int?)b.Id).FirstOrDefaultAsync(ct);
            var b = bookingId is { } id ? await BookingAsync(id, ct) : null;
            if (b is null)
            {
                children.Add(new PtcFeedbackChild(child.FirstName, child.ClassName, PtcFeedbackAvailability.NotEligible, false, null));
                continue;
            }
            var availability = Availability(b);
            var submitted = await db.PtcFeedbacks.AnyAsync(f => f.PtcParentBookingId == b.Id, ct);
            children.Add(new PtcFeedbackChild(child.FirstName, child.ClassName, availability, submitted,
                availability == PtcFeedbackAvailability.Open ? NewToken(b.Id, b.StudentId, b.EventEnd) : null));
        }
        return new(PortalIdentifyStatus.Found, children);
    }

    public async Task<PtcFeedbackForm?> FormAsync(string? token, CancellationToken ct = default)
    {
        if (Read(token) is not { } t || await BookingAsync(t.BookingId, ct) is not { } b || b.StudentId != t.StudentId) return null;
        var availability = Availability(b);
        if (availability == PtcFeedbackAvailability.NotEligible) return null;              // a cancelled booking looks like an unknown link
        var existing = await db.PtcFeedbacks.AsNoTracking().Where(f => f.PtcParentBookingId == b.Id).Select(f => new { f.Reference, f.SubmittedAt }).FirstOrDefaultAsync(ct);
        return new PtcFeedbackForm
        {
            Token = token!, EventTitle = b.EventTitle, Date = b.Date, ChildFirstName = FirstName(b.StudentName), ClassName = b.ClassName,
            Availability = availability, ClosesOn = PtcFeedbackRules.ClosesOn(b.EventEnd, Window),
            Teachers = (await TeachersAsync(b.Id, ct)).Select(x => new PtcFeedbackTeacher(TeacherCode(b.Id, x.Id), x.Name, x.Subjects)).ToList(),
            SubmittedReference = existing?.Reference, SubmittedAt = existing?.SubmittedAt,
        };
    }

    private static bool TryEnum<T>(string? value, out T? result) where T : struct, Enum
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (Enum.TryParse<T>(value, false, out var v) && Enum.IsDefined(v) && !int.TryParse(value, out _)) { result = v; return true; }
        return false;
    }

    public async Task<PtcFeedbackSubmitResult> SubmitAsync(string? token, PtcFeedbackInput input, CancellationToken ct = default)
    {
        if (Read(token) is not { } t || await BookingAsync(t.BookingId, ct) is not { } b || b.StudentId != t.StudentId)
            return new(false, null, NotAvailableMessage, Unavailable: true);
        var availability = Availability(b);
        if (availability != PtcFeedbackAvailability.Open)
            return new(false, null, availability switch
            {
                PtcFeedbackAvailability.NotYet => "Feedback opens after your child's Parent-Teacher Conference.",
                PtcFeedbackAvailability.Closed => "The feedback period for this conference has ended. Thank you.",
                _ => NotAvailableMessage,
            }, Unavailable: true);
        if (await db.PtcFeedbacks.AsNoTracking().Where(f => f.PtcParentBookingId == b.Id).Select(f => f.Reference).FirstOrDefaultAsync(ct) is { } done)
            return new(true, done, null, AlreadySubmitted: true);                                 // idempotent: never overwritten

        const string invalid = "Some answers could not be read. Please check the form and try again.";
        if (!TryEnum<PtcRating>(input.Overall, out var overall) || !TryEnum<PtcRating>(input.Communication, out var communication)
            || !TryEnum<PtcRating>(input.Organization, out var organization) || !TryEnum<PtcHelpful>(input.Helpful, out var helpful)
            || !TryEnum<PtcConcernCategory>(input.ConcernCategory, out var category))
            return new(false, null, invalid);
        var comments = PtcFeedbackRules.Text(input.Comments, PtcFeedback.CommentsMaxLength);
        var concern = PtcFeedbackRules.Text(input.Concern, PtcFeedback.ConcernMaxLength);
        if (category is not null && concern is null) return new(false, null, "Please describe your concern, or leave the concern category empty.");

        var teachers = await TeachersAsync(b.Id, ct);
        var byCode = teachers.ToDictionary(x => TeacherCode(b.Id, x.Id), x => x.Id);
        var teacherRows = new List<PtcTeacherFeedback>();
        foreach (var x in input.Teachers ?? [])
        {
            // Only teachers of this booking: any other code is refused (no feedback about teachers the parent did not meet).
            if (!byCode.TryGetValue(x.Code ?? "", out var teacherId)) return new(false, null, invalid);
            if (!TryEnum<PtcRating>(x.Communication, out var tc) || !TryEnum<PtcHelpful>(x.Helpful, out var th)) return new(false, null, invalid);
            var comment = PtcFeedbackRules.Text(x.Comment, PtcTeacherFeedback.CommentMaxLength);
            if (tc is null && th is null && comment is null) continue;
            if (teacherRows.Any(r => r.TeacherStaffMemberId == teacherId)) return new(false, null, invalid);
            teacherRows.Add(new PtcTeacherFeedback { TeacherStaffMemberId = teacherId, CommunicationRating = tc, MeetingHelpful = th, Comment = comment });
        }
        if (overall is null && communication is null && organization is null && helpful is null && comments is null && concern is null && teacherRows.Count == 0)
            return new(false, null, EmptyMessage);

        var now = _time.UtcNow;
        var feedback = new PtcFeedback
        {
            Reference = NewReference(), PtcEventId = b.EventId, PtcClassScheduleId = b.ScheduleId, PtcParentBookingId = b.Id,
            OverallRating = overall, CommunicationRating = communication, OrganizationRating = organization, MeetingHelpful = helpful,
            Comments = comments, ConcernCategory = concern is null ? null : category ?? PtcConcernCategory.Other, ConcernText = concern,
            ConcernStatus = concern is null ? null : PtcConcernStatus.New, SubmittedAt = now, UpdatedAt = now, Teachers = teacherRows,
        };
        db.PtcFeedbacks.Add(feedback);
        try
        {
            await db.SaveChangesAsync(ct);
            return new(true, feedback.Reference, null);
        }
        catch (DbUpdateException)
        {
            // A second submission at the same moment hits the one-per-booking index: the first one is kept and shown.
            db.ChangeTracker.Clear();
            var first = await db.PtcFeedbacks.AsNoTracking().Where(f => f.PtcParentBookingId == b.Id).Select(f => f.Reference).FirstOrDefaultAsync(ct);
            if (first is null) throw;
            return new(true, first, null, AlreadySubmitted: true);
        }
    }

    // ------------------------------------------------------------------ Super Admin: links and response tracking

    public async Task<PtcFeedbackLinks?> LinksAsync(int eventId, string siteBaseUrl, CancellationToken ct = default)
    {
        access.Ensure();
        var ev = await db.PtcEvents.AsNoTracking().Where(e => e.Id == eventId).Select(e => new { e.Id, e.Status, e.EndDate, e.PublicToken }).FirstOrDefaultAsync(ct);
        if (ev is null) return null;
        var ids = await db.PtcParentBookings.AsNoTracking().Where(b => b.ClassSchedule.PtcEventId == eventId && b.Status == PtcBookingStatus.Confirmed)
            .OrderBy(b => b.ClassSchedule.EventDate).ThenBy(b => b.ClassSchedule.SchoolClass.SortOrder).ThenBy(b => b.Round.StartTime).ThenBy(b => b.SlotNumber)
            .Select(b => new { b.Id, b.BookingReference }).ToListAsync(ct);
        var feedback = await db.PtcFeedbacks.AsNoTracking().Where(f => f.PtcEventId == eventId).ToDictionaryAsync(f => f.PtcParentBookingId, f => f.Reference, ct);
        var rows = new List<PtcFeedbackLinkRow>();
        foreach (var x in ids)
        {
            var b = (await BookingAsync(x.Id, ct))!;
            var a = Availability(b);
            var url = a == PtcFeedbackAvailability.Open && NewToken(b.Id, b.StudentId, b.EventEnd) is { } token ? FeedbackUrl(siteBaseUrl, token) : null;
            rows.Add(new PtcFeedbackLinkRow(x.BookingReference, FirstName(b.StudentName), b.ClassName, b.Date, a, feedback.GetValueOrDefault(b.Id), url));
        }
        var eventOpen = ev.Status is PtcEventStatus.Published or PtcEventStatus.Completed && ev.PublicToken is not null
                        && _time.Today <= PtcFeedbackRules.ClosesOn(ev.EndDate, Window);
        return new PtcFeedbackLinks
        {
            EventId = ev.Id, Status = ev.Status, ClosesOn = PtcFeedbackRules.ClosesOn(ev.EndDate, Window),
            EventUrl = eventOpen ? EventUrl(siteBaseUrl, ev.PublicToken!) : null, Bookings = rows,
        };
    }

    // ------------------------------------------------------------------ Super Admin: feedback management

    private IQueryable<PtcFeedback> Filtered(PtcFeedbackFilter f)
    {
        var q = db.PtcFeedbacks.AsNoTracking().AsQueryable();
        if (f.EventId is { } e) q = q.Where(x => x.PtcEventId == e);
        if (f.Date is { } d) q = q.Where(x => x.ClassSchedule.EventDate == d);
        if (f.ClassId is { } c) q = q.Where(x => x.ClassSchedule.SchoolClassId == c);
        if (f.TeacherId is { } t) q = q.Where(x => x.Booking.Interviews.Any(i => i.TeacherStaffMemberId == t && i.Status != PtcInterviewStatus.Cancelled));
        if (f.DepartmentId is { } dep) q = q.Where(x => x.Booking.Subjects.Any(s => s.DepartmentId == dep && s.Status == PtcBookingSubjectStatus.Active));
        if (f.Rating is { } r) q = q.Where(x => x.OverallRating == r);
        if (f.ConcernStatus is { } cs) q = q.Where(x => x.ConcernStatus == cs);
        if (f.HasConcern is { } hc) q = hc ? q.Where(x => x.ConcernStatus != null) : q.Where(x => x.ConcernStatus == null);
        if (f.Review is { } rv) q = q.Where(x => x.ReviewStatus == rv);
        if (f.SubmittedFrom is { } from) { var u = _time.StartOfDayUtc(from); q = q.Where(x => x.SubmittedAt >= u); }
        if (f.SubmittedTo is { } to) { var u = _time.StartOfDayUtc(to.AddDays(1)); q = q.Where(x => x.SubmittedAt < u); }
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var s = f.Q.Trim();
            q = q.Where(x => x.Reference.Contains(s) || x.Booking.BookingReference.Contains(s) || x.PtcEvent.Title.Contains(s));
        }
        return q;
    }

    private static IQueryable<PtcFeedbackRow> Rows(IQueryable<PtcFeedback> q) => q.Select(x => new PtcFeedbackRow(x.Id, x.Reference, x.PtcEventId, x.PtcEvent.Title,
        x.ClassSchedule.EventDate, x.ClassSchedule.SchoolClassId, x.ClassSchedule.SchoolClass.Name, x.Booking.BookingReference, x.SubmittedAt, x.OverallRating,
        x.CommunicationRating, x.OrganizationRating, x.MeetingHelpful, x.ConcernCategory, x.ConcernStatus, x.ReviewStatus,
        x.AssignedStaffMember != null ? x.AssignedStaffMember.FullName : null, x.Teachers.Count));

    public async Task<PtcFeedbackList> ListAsync(PtcFeedbackFilter filter, CancellationToken ct = default)
    {
        access.Ensure();
        var rows = await Rows(Filtered(filter).OrderByDescending(x => x.SubmittedAt).ThenByDescending(x => x.Id)).ToListAsync(ct);
        var rated = rows.Where(r => r.Overall is not null).ToList();
        var events = await db.PtcEvents.AsNoTracking().OrderByDescending(e => e.StartDate).Select(e => new { e.Id, e.Title }).ToListAsync(ct);
        var classes = await db.Classes.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var teachers = await db.PtcTeacherInterviews.AsNoTracking().Select(i => new { i.TeacherStaffMemberId, i.Teacher.FullName }).Distinct().OrderBy(t => t.FullName).ToListAsync(ct);
        return new PtcFeedbackList
        {
            Rows = rows,
            Summary = new PtcFeedbackSummary
            {
                Submissions = rows.Count, Events = rows.Select(r => r.EventId).Distinct().Count(), RatedOverall = rated.Count,
                AverageOverall = rated.Count == 0 ? null : rated.Average(r => (int)r.Overall!.Value),
                Positive = rows.Count(r => PtcFeedbackRules.Positive(r.Overall)), Negative = rows.Count(r => PtcFeedbackRules.Negative(r.Overall)),
                Concerns = rows.Count(r => r.ConcernStatus is not null), Unresolved = rows.Count(r => PtcFeedbackRules.Open(r.ConcernStatus)),
                Resolved = rows.Count(r => r.ConcernStatus is PtcConcernStatus.Resolved or PtcConcernStatus.Closed),
                PendingReview = rows.Count(r => r.Review == PtcFeedbackReview.Pending),
            },
            Events = events.Select(e => (e.Id, e.Title)).ToList(), Classes = classes.Select(c => (c.Id, c.Name)).ToList(),
            Departments = departments.Select(d => (d.Id, d.Name)).ToList(), Teachers = teachers.Select(t => (t.TeacherStaffMemberId, t.FullName)).ToList(),
        };
    }

    public async Task<PtcFeedbackDetails?> DetailsAsync(int id, CancellationToken ct = default)
    {
        access.Ensure();
        var row = await Rows(db.PtcFeedbacks.AsNoTracking().Where(f => f.Id == id)).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var f = await db.PtcFeedbacks.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Comments, x.ConcernText, x.ReviewedBy, x.ReviewedAt, x.AssignedStaffMemberId, x.AssignedBy, x.AssignedAt, x.ResolutionNotes, x.ResolvedAt, x.ResolvedBy,
            x.Version, x.PtcParentBookingId,
        }).FirstAsync(ct);
        var subjects = (await TeachersAsync(f.PtcParentBookingId, ct)).ToDictionary(t => t.Id, t => t.Subjects);
        var teachers = await db.PtcTeacherFeedbacks.AsNoTracking().Where(t => t.PtcFeedbackId == id).OrderBy(t => t.Teacher.FullName)
            .Select(t => new { t.TeacherStaffMemberId, t.Teacher.FullName, t.CommunicationRating, t.MeetingHelpful, t.Comment }).ToListAsync(ct);
        var history = await db.PtcFeedbackHistories.AsNoTracking().Where(h => h.PtcFeedbackId == id).OrderByDescending(h => h.At).ThenByDescending(h => h.Id)
            .Select(h => new PtcFeedbackHistoryRow(h.At, h.Kind, h.OldValue, h.NewValue, h.Note, h.By)).ToListAsync(ct);
        var staff = await db.StaffMembers.AsNoTracking().Where(s => s.ArchivedAt == null).OrderBy(s => s.FullName).Select(s => new { s.Id, s.FullName }).ToListAsync(ct);
        return new PtcFeedbackDetails
        {
            Row = row, Comments = f.Comments, Concern = f.ConcernText, ReviewedBy = f.ReviewedBy, ReviewedAt = f.ReviewedAt, AssignedStaffId = f.AssignedStaffMemberId,
            AssignedBy = f.AssignedBy, AssignedAt = f.AssignedAt, Resolution = f.ResolutionNotes, ResolvedAt = f.ResolvedAt, ResolvedBy = f.ResolvedBy,
            Version = Convert.ToBase64String(f.Version),
            Teachers = teachers.Select(t => new PtcTeacherFeedbackRow(t.TeacherStaffMemberId, t.FullName, subjects.GetValueOrDefault(t.TeacherStaffMemberId, ""),
                t.CommunicationRating, t.MeetingHelpful, t.Comment)).ToList(),
            History = history, Staff = staff.Select(s => (s.Id, s.FullName)).ToList(),
        };
    }

    /// <summary>
    /// Loads the record for an administrative change, holding the version the administrator saw (a concurrent change makes
    /// the save fail instead of overwriting it). The parent's answers are never touched by any caller.
    /// </summary>
    private async Task<(PtcFeedback? F, ServiceResult? Error)> ForChangeAsync(int id, string? version, CancellationToken ct)
    {
        var f = await db.PtcFeedbacks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return (null, ServiceResult.Fail("", "This feedback record no longer exists."));
        if (version is not null)
        {
            byte[] seen;
            try { seen = Convert.FromBase64String(version); }
            catch (FormatException) { return (null, ServiceResult.Fail("", ChangedMessage)); }
            if (!seen.AsSpan().SequenceEqual(f.Version)) return (null, ServiceResult.Fail("", ChangedMessage));
            db.Entry(f).Property(x => x.Version).OriginalValue = seen;
        }
        return (f, null);
    }

    private void History(PtcFeedback f, PtcFeedbackActionKind kind, string? oldValue, string? newValue, string? note)
    {
        db.PtcFeedbackHistories.Add(new PtcFeedbackHistory
        {
            PtcFeedbackId = f.Id, Kind = kind, OldValue = oldValue, NewValue = newValue,
            Note = PtcFeedbackRules.Text(note, PtcFeedbackHistory.NoteMaxLength), By = currentUser.AccountName(), At = _time.UtcNow,
        });
        f.UpdatedAt = _time.UtcNow;
    }

    private async Task<ServiceResult> SaveAsync(PtcFeedback f, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return ServiceResult.Ok(f.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", ChangedMessage);
        }
    }

    public async Task<ServiceResult> MarkReviewedAsync(int id, string? version, CancellationToken ct = default)
    {
        access.Ensure();
        var (f, error) = await ForChangeAsync(id, version, ct);
        if (error is not null) return error;
        if (f!.ReviewStatus == PtcFeedbackReview.Reviewed) return ServiceResult.Ok(f.Id);
        f.ReviewStatus = PtcFeedbackReview.Reviewed;
        f.ReviewedAt = _time.UtcNow;
        f.ReviewedBy = currentUser.AccountName();
        History(f, PtcFeedbackActionKind.Reviewed, "Pending", "Reviewed", null);
        return await SaveAsync(f, ct);
    }

    public async Task<ServiceResult> SetConcernStatusAsync(int id, PtcConcernStatus status, string? note, string? version, CancellationToken ct = default)
    {
        access.Ensure();
        if (!Enum.IsDefined(status) || status == PtcConcernStatus.Resolved) return ServiceResult.Fail("Status", "Use Resolve (with the resolution details) to resolve a concern.");
        var (f, error) = await ForChangeAsync(id, version, ct);
        if (error is not null) return error;
        if (f!.ConcernStatus is not { } old) return ServiceResult.Fail("", "This feedback has no concern.");
        if (old == status) return ServiceResult.Ok(f.Id);
        if (old is PtcConcernStatus.Resolved or PtcConcernStatus.Closed && status != PtcConcernStatus.Closed)
            return ServiceResult.Fail("Status", "This concern is resolved: reopen it (with a reason) to work on it again.");
        if (status == PtcConcernStatus.Closed && PtcFeedbackRules.Text(note, 2000) is null)
            return ServiceResult.Fail("Note", "Give a short note on why the concern is closed.");
        f.ConcernStatus = status;
        History(f, PtcFeedbackActionKind.ConcernStatusChanged, PtcFeedbackRules.Label(old), PtcFeedbackRules.Label(status), note);
        return await SaveAsync(f, ct);
    }

    public async Task<ServiceResult> AddNoteAsync(int id, string? note, CancellationToken ct = default)
    {
        access.Ensure();
        if (PtcFeedbackRules.Text(note, PtcFeedbackHistory.NoteMaxLength) is null) return ServiceResult.Fail("Note", "Write the note first.");
        var (f, error) = await ForChangeAsync(id, null, ct);
        if (error is not null) return error;
        History(f!, PtcFeedbackActionKind.NoteAdded, null, null, note);
        return await SaveAsync(f!, ct);
    }

    public async Task<ServiceResult> AssignAsync(int id, int? staffId, string? version, CancellationToken ct = default)
    {
        access.Ensure();
        var (f, error) = await ForChangeAsync(id, version, ct);
        if (error is not null) return error;
        if (f!.ConcernStatus is null) return ServiceResult.Fail("", "Only a concern can be assigned.");
        if (f.AssignedStaffMemberId == staffId) return ServiceResult.Ok(f.Id);
        var oldName = f.AssignedStaffMemberId is { } oldId ? await db.StaffMembers.Where(s => s.Id == oldId).Select(s => s.FullName).FirstOrDefaultAsync(ct) : null;
        string? newName = null;
        if (staffId is { } sid)
        {
            newName = await db.StaffMembers.Where(s => s.Id == sid && s.ArchivedAt == null).Select(s => s.FullName).FirstOrDefaultAsync(ct);
            if (newName is null) return ServiceResult.Fail("StaffId", "Choose a current staff member.");
        }
        f.AssignedStaffMemberId = staffId;
        f.AssignedBy = staffId is null ? null : currentUser.AccountName();
        f.AssignedAt = staffId is null ? null : _time.UtcNow;
        History(f, staffId is null ? PtcFeedbackActionKind.Unassigned : PtcFeedbackActionKind.Assigned, oldName, newName, null);
        return await SaveAsync(f, ct);
    }

    public async Task<ServiceResult> ResolveAsync(int id, string? resolution, string? version, CancellationToken ct = default)
    {
        access.Ensure();
        var text = PtcFeedbackRules.Text(resolution, PtcFeedback.ResolutionMaxLength);
        if (text is null) return ServiceResult.Fail("Resolution", "Record how the concern was resolved.");
        var (f, error) = await ForChangeAsync(id, version, ct);
        if (error is not null) return error;
        if (f!.ConcernStatus is not { } old) return ServiceResult.Fail("", "This feedback has no concern.");
        if (old is PtcConcernStatus.Resolved or PtcConcernStatus.Closed) return ServiceResult.Fail("", "This concern is already resolved.");
        f.ConcernStatus = PtcConcernStatus.Resolved;
        f.ResolutionNotes = text;
        f.ResolvedAt = _time.UtcNow;
        f.ResolvedBy = currentUser.AccountName();
        History(f, PtcFeedbackActionKind.Resolved, PtcFeedbackRules.Label(old), "Resolved", text);
        return await SaveAsync(f, ct);
    }

    public async Task<ServiceResult> ReopenAsync(int id, string? reason, string? version, CancellationToken ct = default)
    {
        access.Ensure();
        if (PtcFeedbackRules.Text(reason, PtcFeedbackHistory.NoteMaxLength) is null) return ServiceResult.Fail("Reason", "Give a reason for reopening the concern.");
        var (f, error) = await ForChangeAsync(id, version, ct);
        if (error is not null) return error;
        if (f!.ConcernStatus is not (PtcConcernStatus.Resolved or PtcConcernStatus.Closed) || f.ConcernStatus is not { } old)
            return ServiceResult.Fail("", "Only a resolved or closed concern can be reopened.");
        // The earlier resolution stays in the history; the record shows it is being worked on again.
        f.ConcernStatus = PtcConcernStatus.InProgress;
        f.ResolvedAt = null;
        f.ResolvedBy = null;
        History(f, PtcFeedbackActionKind.Reopened, PtcFeedbackRules.Label(old), "In progress", reason);
        return await SaveAsync(f, ct);
    }
}
