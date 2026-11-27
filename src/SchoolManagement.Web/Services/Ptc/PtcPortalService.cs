using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>Limits of the public parent booking portal ("PtcPortal" configuration section).</summary>
public sealed class PtcPortalOptions
{
    public const string Section = "PtcPortal";

    /// <summary>Identification and booking submissions per client address per window.</summary>
    public int SubmitPermitLimit { get; set; } = 20;

    /// <summary>All portal requests per client address per window.</summary>
    public int RequestPermitLimit { get; set; } = 120;
    public int WindowMinutes { get; set; } = 5;

    /// <summary>Failed identifications for one parent number (any address) before it is paused.</summary>
    public int MaxFailedAttemptsPerNumber { get; set; } = 5;
    public int FailedAttemptWindowMinutes { get; set; } = 15;

    /// <summary>How long an identification stays valid for choosing a Round (minutes).</summary>
    public int IdentityLifetimeMinutes { get; set; } = 30;

    /// <summary>The site address used in shared booking links (e.g. https://school.example); empty: the address of the current request.</summary>
    public string? PublicBaseUrl { get; set; }
}

public sealed record PortalEvent(int Id, string Token, string Title, string? Description, DateOnly StartDate, DateOnly EndDate, bool BookingsOpen);

/// <summary>A child matched to the parent: only what is needed to tell siblings apart. <see cref="Token"/> is opaque.</summary>
public sealed record PortalChild(string Token, string FirstName, string ClassName, bool AlreadyBooked);

public enum PortalIdentifyStatus
{
    Found = 1,
    NotFound = 2,
    Throttled = 3,
}

public sealed record PortalIdentifyResult(PortalIdentifyStatus Status, IReadOnlyList<PortalChild> Children);

public sealed record PortalRound(int Number, DateOnly Date, TimeOnly Start, TimeOnly End, int Remaining)
{
    public bool Full => Remaining <= 0;
}

/// <summary>A subject the parent can choose; <see cref="Code"/> is opaque (no database id).</summary>
public sealed record PortalSubject(string Code, string Subject, string Teacher);

public sealed class PortalBookingPage
{
    public required PortalEvent Event { get; init; }
    public required string ChildToken { get; init; }
    public required string FirstName { get; init; }
    public required string ClassName { get; init; }
    public DateOnly? Date { get; init; }
    public IReadOnlyList<PortalRound> Rounds { get; init; } = [];
    public IReadOnlyList<PortalSubject> Subjects { get; init; } = [];

    /// <summary>The child already has a booking for this class's PTC (its confirmation token).</summary>
    public string? ExistingBooking { get; init; }

    /// <summary>Why booking is not possible for this child now (parent-friendly), or null.</summary>
    public string? Unavailable { get; init; }
    public bool CanBook => ExistingBooking is null && Unavailable is null && Rounds.Any(r => !r.Full) && Subjects.Count > 0;
}

public sealed record PortalBookingResult(bool Succeeded, string? ConfirmationToken, string? Error, bool AlreadyBooked = false, bool Restart = false);

public sealed class PortalConfirmation
{
    public required string EventTitle { get; init; }
    public required string StudentName { get; init; }
    public required string ClassName { get; init; }
    public required DateOnly Date { get; init; }
    public required int RoundNumber { get; init; }
    public required TimeOnly RoundStart { get; init; }
    public required TimeOnly RoundEnd { get; init; }
    public required TimeOnly MeetingStart { get; init; }
    public required TimeOnly MeetingEnd { get; init; }
    public required IReadOnlyList<(string Subject, string Teacher)> Subjects { get; init; }
    public required string Reference { get; init; }
    public required PtcBookingStatus Status { get; init; }
}

public interface IPtcPortalService
{
    /// <summary>A Published event by its public link code; null for any other code or status (never says which).</summary>
    Task<PortalEvent?> EventAsync(string? token, CancellationToken ct = default);

    /// <summary>
    /// Finds the parent's children taking part in the event from the registered parent number and one child's first name.
    /// Every failure looks the same (no hint whether a number, a name or a class exists); repeated failures for a number pause it.
    /// </summary>
    Task<PortalIdentifyResult> IdentifyAsync(PortalEvent ev, string? phone, string? firstName, CancellationToken ct = default);

    /// <summary>
    /// The student behind a child code from <see cref="IdentifyAsync"/> for event <paramref name="eventId"/> (re-checked: the
    /// registered number must still match), or null. Used by the feedback pages, which identify parents the same way.
    /// </summary>
    Task<int?> ChildStudentAsync(int eventId, string? childToken, CancellationToken ct = default);

    /// <summary>The booking step for an identified child; null when the identification is invalid, expired or no longer matches.</summary>
    Task<PortalBookingPage?> BookingPageAsync(PortalEvent ev, string? childToken, CancellationToken ct = default);

    /// <summary>Books one Round with the chosen subjects for the identified child (all rules in <see cref="IPtcBookingService"/>).</summary>
    Task<PortalBookingResult> BookAsync(PortalEvent ev, string? childToken, int roundNumber, IReadOnlyList<string> subjectCodes, CancellationToken ct = default);

    /// <summary>The confirmation behind a confirmation token; null for any other value.</summary>
    Task<PortalConfirmation?> ConfirmationAsync(PortalEvent ev, string? bookingToken, CancellationToken ct = default);
}

/// <summary>
/// The public parent booking portal. A parent is identified by the parent contact number recorded on the student
/// (<see cref="Student.ParentContactNumber"/>, matched normalized; never changed) plus the child's first name as recorded.
/// This is knowledge-based: it does not prove the parent owns the phone (there is no OTP). It exposes only the matched
/// children's first names and classes, carries the identification in a tamper-proof, time-limited token (no ids in the
/// browser), resolves every Round and subject on the server and books through <see cref="IPtcBookingService"/>.
/// </summary>
public sealed class PtcPortalService(SchoolDbContext db, IPtcBookingService booking, IDataProtectionProvider protection, IMemoryCache cache,
    IOptions<PtcPortalOptions> options, ISchoolTimeService? schoolTime = null) : IPtcPortalService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;
    private readonly PtcPortalOptions _o = options.Value;
    private readonly ITimeLimitedDataProtector _child = protection.CreateProtector("SchoolManagement.Ptc.Portal.Child").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector _booking = protection.CreateProtector("SchoolManagement.Ptc.Portal.Booking").ToTimeLimitedDataProtector();

    /// <summary>How long a confirmation link keeps working.</summary>
    public static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromDays(60);

    public const string NotFoundMessage =
        "We could not match these details to a child taking part in this conference. Please check the mobile number registered with the school " +
        "and your child's first name as the school records it. If your number has changed or was never registered, please contact the school to update it.";
    public const string ThrottledMessage = "Too many attempts with this number. Please wait a few minutes and try again, or contact the school.";

    // ------------------------------------------------------------------ event

    public async Task<PortalEvent?> EventAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > PtcEvent.PublicTokenMaxLength) return null;
        var ev = await db.PtcEvents.AsNoTracking().Where(e => e.PublicToken == token && e.Status == PtcEventStatus.Published)
            .Select(e => new { e.Id, e.Title, e.Description, e.StartDate, e.EndDate, e.PublicToken }).FirstOrDefaultAsync(ct);
        // The code is case-sensitive (the database comparison is not): only the exact code opens the event.
        if (ev is null || !string.Equals(ev.PublicToken, token, StringComparison.Ordinal)) return null;
        var open = (await PtcAvailability.BookableRoundsAsync(db, _time.Now, ct, eventId: ev.Id)).Count > 0;
        return new PortalEvent(ev.Id, token, ev.Title, ev.Description, ev.StartDate, ev.EndDate, open);
    }

    // ------------------------------------------------------------------ identification

    private string FailKey(int eventId, string parentKey) => $"ptc:portal:fail:{eventId}:{parentKey}";

    private static string FirstNameKey(string? name) => TextNormalizer.NameKey(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    private static string FirstName(string fullName) => TextNormalizer.CleanText(fullName).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fullName;

    public async Task<PortalIdentifyResult> IdentifyAsync(PortalEvent ev, string? phone, string? firstName, CancellationToken ct = default)
    {
        var parentKey = PtcRules.ParentKey(phone);
        var nameKey = FirstNameKey(firstName);
        if (parentKey is null || nameKey.Length == 0) return new(PortalIdentifyStatus.NotFound, []);
        var failKey = FailKey(ev.Id, parentKey);
        if (cache.TryGetValue(failKey, out int failures) && failures >= _o.MaxFailedAttemptsPerNumber) return new(PortalIdentifyStatus.Throttled, []);

        // Children in the event's classes whose recorded parent number is this number (compared normalized, in memory: the
        // stored value is kept exactly as entered). Only students with a number can match.
        var classIds = await db.PtcClassSchedules.AsNoTracking().Where(s => s.PtcEventId == ev.Id && s.Status == PtcScheduleStatus.Scheduled)
            .Select(s => s.SchoolClassId).ToListAsync(ct);
        var candidates = await db.Students.AsNoTracking()
            .Where(s => classIds.Contains(s.SchoolClassId) && s.ParentContactNumber != null && s.SchoolClass.IsActive)
            .Select(s => new { s.Id, s.FullName, s.ParentContactNumber, ClassName = s.SchoolClass.Name, s.SchoolClass.SortOrder })
            .ToListAsync(ct);
        var mine = candidates.Where(s => PtcRules.ParentKey(s.ParentContactNumber) == parentKey).OrderBy(s => s.SortOrder).ThenBy(s => s.FullName).ToList();
        if (!mine.Any(s => FirstNameKey(s.FullName) == nameKey))
        {
            cache.Set(failKey, failures + 1, TimeSpan.FromMinutes(_o.FailedAttemptWindowMinutes));
            return new(PortalIdentifyStatus.NotFound, []);
        }

        var ids = mine.Select(s => s.Id).ToList();
        var booked = await db.PtcParentBookings.AsNoTracking()
            .Where(b => ids.Contains(b.StudentId) && b.ClassSchedule.PtcEventId == ev.Id && b.Status != PtcBookingStatus.Cancelled)
            .Select(b => b.StudentId).ToListAsync(ct);
        var lifetime = TimeSpan.FromMinutes(_o.IdentityLifetimeMinutes);
        return new(PortalIdentifyStatus.Found, mine.Select(s => new PortalChild(
            _child.Protect($"{ev.Id}|{s.Id}|{parentKey}", lifetime), FirstName(s.FullName), s.ClassName, booked.Contains(s.Id))).ToList());
    }

    private sealed record ChildContext(int StudentId, string FullName, string ParentKey, int ClassId, string ClassName, int? ScheduleId, DateOnly? Date);

    public async Task<int?> ChildStudentAsync(int eventId, string? childToken, CancellationToken ct = default) =>
        (await ChildAsync(new PortalEvent(eventId, "", "", null, default, default, false), childToken, ct))?.StudentId;

    /// <summary>The child behind a token, re-checked against the current records (the number must still match).</summary>
    private async Task<ChildContext?> ChildAsync(PortalEvent ev, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        string payload;
        try { payload = _child.Unprotect(token); }
        catch (CryptographicException) { return null; }
        var parts = payload.Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, C, out var eventId) || eventId != ev.Id
            || !int.TryParse(parts[1], NumberStyles.None, C, out var studentId)) return null;
        var parentKey = parts[2];
        var s = await db.Students.AsNoTracking().Where(x => x.Id == studentId)
            .Select(x => new { x.FullName, x.ParentContactNumber, x.SchoolClassId, ClassName = x.SchoolClass.Name, x.SchoolClass.IsActive }).FirstOrDefaultAsync(ct);
        if (s is null || !s.IsActive || PtcRules.ParentKey(s.ParentContactNumber) != parentKey) return null;
        var schedule = await db.PtcClassSchedules.AsNoTracking()
            .Where(x => x.PtcEventId == ev.Id && x.SchoolClassId == s.SchoolClassId && x.Status == PtcScheduleStatus.Scheduled)
            .Select(x => new { x.Id, x.EventDate }).FirstOrDefaultAsync(ct);
        return new ChildContext(studentId, s.FullName, parentKey, s.SchoolClassId, s.ClassName, schedule?.Id, schedule?.EventDate);
    }

    /// <summary>An opaque, stable code for a subject option in this event (no ids reach the browser).</summary>
    private static string Code(PortalEvent ev, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ev.Token + "|" + key)))[..16];

    private string ConfirmationToken(int bookingId, int studentId) => _booking.Protect($"{bookingId}|{studentId}", ConfirmationLifetime);

    private Task<int?> ActiveBookingAsync(int scheduleId, int studentId, CancellationToken ct) =>
        db.PtcParentBookings.AsNoTracking().Where(b => b.PtcClassScheduleId == scheduleId && b.StudentId == studentId && b.Status != PtcBookingStatus.Cancelled)
            .Select(b => (int?)b.Id).FirstOrDefaultAsync(ct);

    // ------------------------------------------------------------------ booking step

    public async Task<PortalBookingPage?> BookingPageAsync(PortalEvent ev, string? childToken, CancellationToken ct = default)
    {
        var child = await ChildAsync(ev, childToken, ct);
        if (child is null) return null;
        var page = new { Event = ev, ChildToken = childToken!, FirstName = FirstName(child.FullName), child.ClassName };
        if (child.ScheduleId is not { } scheduleId)
            return new PortalBookingPage { Event = ev, ChildToken = page.ChildToken, FirstName = page.FirstName, ClassName = page.ClassName,
                Unavailable = $"{child.ClassName} is not taking part in this conference." };

        if (await ActiveBookingAsync(scheduleId, child.StudentId, ct) is { } existing)
            return new PortalBookingPage { Event = ev, ChildToken = page.ChildToken, FirstName = page.FirstName, ClassName = page.ClassName, Date = child.Date,
                ExistingBooking = ConfirmationToken(existing, child.StudentId) };

        var rounds = (await PtcAvailability.BookableRoundsAsync(db, _time.Now, ct, eventId: ev.Id, scheduleId: scheduleId, includeFull: true))
            .Select(r => new PortalRound(r.Number, r.Date, r.Start, r.End, r.Remaining)).ToList();
        var subjects = (await PtcSchedulingRules.ClassSubjectsAsync(db, child.ClassId, child.Date!.Value, ct, scheduleId))
            .Select(o => new PortalSubject(Code(ev, o.Key), o.Subject, o.TeacherName)).ToList();
        string? unavailable =
            subjects.Count == 0 ? $"Booking is not available for {child.ClassName} yet: the school is still preparing its teachers for this conference. Please try again later or contact the school."
            : rounds.Count == 0 ? "There are no upcoming sessions left for this class."
            : rounds.All(r => r.Full) ? "All sessions for this class are full. Please contact the school."
            : null;
        return new PortalBookingPage
        {
            Event = ev, ChildToken = page.ChildToken, FirstName = page.FirstName, ClassName = page.ClassName, Date = child.Date,
            Rounds = rounds, Subjects = subjects, Unavailable = unavailable,
        };
    }

    public async Task<PortalBookingResult> BookAsync(PortalEvent ev, string? childToken, int roundNumber, IReadOnlyList<string> subjectCodes, CancellationToken ct = default)
    {
        var child = await ChildAsync(ev, childToken, ct);
        if (child is null) return new(false, null, "Your session has expired. Please enter your details again.", Restart: true);
        if (child.ScheduleId is not { } scheduleId) return new(false, null, $"{child.ClassName} is not taking part in this conference.");
        if (await ActiveBookingAsync(scheduleId, child.StudentId, ct) is { } existing)
            return new(false, ConfirmationToken(existing, child.StudentId), null, AlreadyBooked: true);

        var roundId = await db.PtcRounds.AsNoTracking()
            .Where(r => r.PtcClassScheduleId == scheduleId && r.RoundNumber == roundNumber && r.Status != PtcRoundStatus.Cancelled)
            .Select(r => (int?)r.Id).FirstOrDefaultAsync(ct);
        if (roundId is null) return new(false, null, "Please choose one of the sessions shown.");
        var codes = (subjectCodes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count == 0) return new(false, null, "Please choose at least one subject to discuss.");
        var options = await PtcSchedulingRules.ClassSubjectsAsync(db, child.ClassId, child.Date!.Value, ct, scheduleId);
        var keys = new List<string>();
        foreach (var code in codes)
        {
            var option = options.FirstOrDefault(o => Code(ev, o.Key) == code);
            if (option is null) return new(false, null, "One of the chosen subjects is no longer available. Please review your choice.");
            keys.Add(option.Key);
        }

        var result = await booking.BookAsParentAsync(new PtcBookingRequest(roundId.Value, child.StudentId, child.ParentKey, keys), ct);
        if (result.Succeeded) return new(true, ConfirmationToken(result.EntityId!.Value, child.StudentId), null);
        var field = result.Errors[0].Key;
        if (field == PtcBookingService.AlreadyBookedField && await ActiveBookingAsync(scheduleId, child.StudentId, ct) is { } made)
            return new(false, ConfirmationToken(made, child.StudentId), null, AlreadyBooked: true);
        // Parent-friendly messages only: no details about other classes, teachers' other meetings or records.
        return field switch
        {
            PtcBookingService.FullField => new(false, null, "Sorry, that session has just been filled. Please choose another session."),
            "Subjects" or PtcBookingService.NoCommonSlotField =>
                new(false, null, "No remaining time in that session suits all the chosen teachers together. Please choose another session, or fewer subjects."),
            "ParentContact" or "StudentId" => new(false, null, "Your details no longer match the school's records. Please enter them again.", Restart: true),
            _ => new(false, null, "That session is no longer available. Please choose another session."),
        };
    }

    // ------------------------------------------------------------------ confirmation

    public async Task<PortalConfirmation?> ConfirmationAsync(PortalEvent ev, string? bookingToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bookingToken)) return null;
        string payload;
        try { payload = _booking.Unprotect(bookingToken); }
        catch (CryptographicException) { return null; }
        var parts = payload.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, C, out var bookingId) || !int.TryParse(parts[1], NumberStyles.None, C, out var studentId))
            return null;
        var b = await db.PtcParentBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.StudentId == studentId && x.ClassSchedule.PtcEventId == ev.Id)
            .Select(x => new
            {
                EventTitle = x.ClassSchedule.PtcEvent.Title, StudentName = x.Student.FullName, ClassName = x.ClassSchedule.SchoolClass.Name,
                x.ClassSchedule.EventDate, x.Round.RoundNumber, x.Round.StartTime, x.Round.EndTime, x.BookingReference, x.Status,
                MeetingStart = x.Interviews.Select(i => (TimeOnly?)i.ScheduledStart).Min(),
                MeetingEnd = x.Interviews.Select(i => (TimeOnly?)i.ScheduledEnd).Max(),
                Subjects = x.Subjects.Where(s => s.Status == PtcBookingSubjectStatus.Active).OrderBy(s => s.SubjectName)
                    .Select(s => new { s.SubjectName, Teacher = s.AssignedTeacher.FullName }).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (b is null) return null;
        return new PortalConfirmation
        {
            EventTitle = b.EventTitle, StudentName = b.StudentName, ClassName = b.ClassName, Date = b.EventDate, RoundNumber = b.RoundNumber,
            RoundStart = b.StartTime, RoundEnd = b.EndTime, MeetingStart = b.MeetingStart ?? b.StartTime, MeetingEnd = b.MeetingEnd ?? b.StartTime,
            Subjects = b.Subjects.Select(s => (s.SubjectName, s.Teacher)).ToList(), Reference = b.BookingReference, Status = b.Status,
        };
    }
}

/// <summary>Public booking link and the WhatsApp share link (click-to-chat; the Admin sends it manually).</summary>
public static class PtcLinks
{
    /// <summary>The public booking address of an event: {base}/ptc-booking/{token}.</summary>
    public static string BookingUrl(string baseUrl, string token) => baseUrl.TrimEnd('/') + "/ptc-booking/" + Uri.EscapeDataString(token);

    /// <summary>The default invitation text (editable before sending).</summary>
    public static string InvitationMessage(string eventTitle, string url) =>
        $"Dear Parent, Parent-Teacher Conference booking is now open ({eventTitle}). Please use the following link to select your preferred session: {url}";

    /// <summary>WhatsApp's share link with the message pre-filled; the sender picks the chat and can edit the text.</summary>
    public static string WhatsAppShare(string message) => "https://wa.me/?text=" + Uri.EscapeDataString(message);
}
