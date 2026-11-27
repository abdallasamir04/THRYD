using System.Security.Cryptography;
using SchoolManagement.Web.Common;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>
/// The Round settings new PTC Rounds are created with. <see cref="Initial"/> is the one authoritative source of the
/// approved starting values (9 parents, 7-minute meetings, 7-minute transition, 70-minute Rounds); the school's own values
/// are stored in Settings (<see cref="ISettingsService.GetPtcDefaultsAsync"/>) and fall back to these. Each value can be
/// changed on its own: the Round duration is stored separately and only has to leave room for every meeting.
/// </summary>
public sealed record PtcRoundDefaults(int Capacity, int MeetingDurationMinutes, int TransitionDurationMinutes, int RoundDurationMinutes)
{
    public const int InitialCapacity = 9;
    public const int InitialMeetingDurationMinutes = 7;
    public const int InitialTransitionDurationMinutes = 7;

    /// <summary>(9 × 7) + 7 = 70.</summary>
    public static readonly int InitialRoundDurationMinutes =
        PtcRules.PlannedRoundMinutes(InitialCapacity, InitialMeetingDurationMinutes, InitialTransitionDurationMinutes);

    public static readonly PtcRoundDefaults Initial =
        new(InitialCapacity, InitialMeetingDurationMinutes, InitialTransitionDurationMinutes, InitialRoundDurationMinutes);

    // Sanity limits for the values an administrator may set (not business rules of the school).
    public const int MaxCapacity = 60;
    public const int MaxMeetingMinutes = 60;
    public const int MaxTransitionMinutes = 120;
    public const int MaxRoundMinutes = 12 * 60;

    /// <summary>Why these values cannot be used (field, message), or null when they are valid.</summary>
    public (string Field, string Message)? Problem()
    {
        if (Capacity < 1 || Capacity > MaxCapacity) return ("Capacity", $"Enter a Round capacity between 1 and {MaxCapacity} parents.");
        if (MeetingDurationMinutes < 1 || MeetingDurationMinutes > MaxMeetingMinutes)
            return ("MeetingDurationMinutes", $"Enter a meeting duration between 1 and {MaxMeetingMinutes} minutes.");
        if (TransitionDurationMinutes < 0 || TransitionDurationMinutes > MaxTransitionMinutes)
            return ("TransitionDurationMinutes", $"Enter a transition time between 0 and {MaxTransitionMinutes} minutes.");
        if (RoundDurationMinutes < 1 || RoundDurationMinutes > MaxRoundMinutes)
            return ("RoundDurationMinutes", $"Enter a Round duration between 1 and {MaxRoundMinutes} minutes.");
        if (!PtcRules.FitsMeetings(RoundDurationMinutes, Capacity, MeetingDurationMinutes))
            return ("RoundDurationMinutes",
                $"A Round of {RoundDurationMinutes} minutes cannot hold {Capacity} meetings of {MeetingDurationMinutes} minutes ({Capacity * MeetingDurationMinutes} minutes).");
        return null;
    }
}

/// <summary>Pure PTC rules (no database): durations, slot times, overlaps, parent identity and booking references.</summary>
public static class PtcRules
{
    /// <summary>The planned Round length: (Capacity × MeetingDurationMinutes) + TransitionDurationMinutes.</summary>
    public static int PlannedRoundMinutes(int capacity, int meetingMinutes, int transitionMinutes) =>
        capacity * meetingMinutes + transitionMinutes;

    /// <summary>A Round of this length leaves room for every meeting (the transition time may be shorter or longer).</summary>
    public static bool FitsMeetings(int roundMinutes, int capacity, int meetingMinutes) => capacity * meetingMinutes <= roundMinutes;

    /// <summary>Minutes from <paramref name="start"/> to <paramref name="end"/> on the same day (negative when end is earlier).</summary>
    public static int Minutes(TimeOnly start, TimeOnly end) => (int)(end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;

    /// <summary>Two time ranges on the same date overlap when NewStart &lt; ExistingEnd and NewEnd &gt; ExistingStart (touching is not overlapping).</summary>
    public static bool Overlaps(TimeOnly newStart, TimeOnly newEnd, TimeOnly existingStart, TimeOnly existingEnd) =>
        newStart < existingEnd && newEnd > existingStart;

    /// <summary>
    /// The planned meeting time of a booking's slot (1 … Capacity): slot n starts (n − 1) × MeetingDuration after the Round
    /// starts. Every interview of the booking uses this same window — the parent's selected teachers meet them in parallel.
    /// </summary>
    public static (TimeOnly Start, TimeOnly End) SlotWindow(TimeOnly roundStart, int slotNumber, int meetingMinutes)
    {
        if (slotNumber < 1) throw new ArgumentOutOfRangeException(nameof(slotNumber));
        var start = roundStart.AddMinutes((slotNumber - 1) * meetingMinutes);
        return (start, start.AddMinutes(meetingMinutes));
    }

    /// <summary>
    /// The parent identity kept on a booking: the number as international digits (the WhatsApp number, e.g. 010… →
    /// 2010…), else its digits when the country cannot be determined; null when there is no usable number.
    /// </summary>
    public static string? ParentKey(string? phone)
    {
        var key = WhatsApp.Number(phone) ?? TextNormalizer.PhoneKey(phone);
        return key.Length is >= 8 and <= Entities.PtcParentBooking.ParentContactMaxLength ? key : null;
    }

    /// <summary>The parent's number is the student's recorded parent contact number (written in any equivalent way).</summary>
    public static bool IsStudentParent(string? studentParentNumber, string? parentNumber) =>
        ParentKey(studentParentNumber) is { } recorded && ParentKey(parentNumber) is { } given && recorded == given;

    private const string ReferenceAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"; // no 0/O, 1/I

    /// <summary>A new unguessable code for an event's public booking link: 128 random bits, URL-safe (22 characters).</summary>
    public static string NewPublicToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A new random booking reference, "PTC-" + 8 characters (unique index in the database).</summary>
    public static string NewReference() =>
        "PTC-" + new string(Enumerable.Range(0, 8).Select(_ => ReferenceAlphabet[RandomNumberGenerator.GetInt32(ReferenceAlphabet.Length)]).ToArray());
}
