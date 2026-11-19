using System.Globalization;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>The three audit rounds of a session: always calculated from that session's own start time.</summary>
public static class FloorAuditRounds
{
    /// <summary>Minutes after the session's start: round 1, round 2, round 3.</summary>
    public static readonly int[] OffsetMinutes = [6, 25, 40];

    public const int Count = 3;

    /// <summary>Round 3 stays due at least this long when the session ends sooner (or has no end time).</summary>
    public const int LastRoundMinutes = 10;

    public static bool IsValid(int round) => round is >= 1 and <= Count;

    /// <summary>The scheduled time of a round (1–3) of a session starting at <paramref name="sessionStart"/>.</summary>
    public static TimeOnly TimeOf(TimeOnly sessionStart, int round) =>
        IsValid(round) ? sessionStart.AddMinutes(OffsetMinutes[round - 1]) : throw new ArgumentOutOfRangeException(nameof(round));

    /// <summary>The three scheduled times of a session.</summary>
    public static IReadOnlyList<TimeOnly> TimesFor(TimeOnly sessionStart) => OffsetMinutes.Select(m => sessionStart.AddMinutes(m)).ToList();

    /// <summary>
    /// When a round stops being due: the next round's time; for round 3 the session's end (but never sooner than
    /// <see cref="LastRoundMinutes"/> after it).
    /// </summary>
    public static TimeOnly WindowEnd(TimeOnly sessionStart, TimeOnly? sessionEnd, int round)
    {
        if (round < Count) return TimeOf(sessionStart, round + 1);
        var r3 = TimeOf(sessionStart, Count);
        var min = r3.AddMinutes(LastRoundMinutes);
        return sessionEnd is { } end && end > min ? end : min;
    }

    public static string Label(int round) => $"Round {round}";
}

/// <summary>Status of an audit round (whole school, one floor). Never Completed only because time has passed.</summary>
public enum AuditRoundStatus
{
    Upcoming = 1,
    Due = 2,
    InProgress = 3,
    Completed = 4,
    Missed = 5,
}

/// <summary>Status of one area in one round.</summary>
public enum AreaAuditStatus
{
    /// <summary>The round has not started yet.</summary>
    NotStarted = 1,

    /// <summary>The round is due (or its time has passed) and the area has not been inspected.</summary>
    Pending = 2,
    InProgress = 3,
    Completed = 4,
    IssuesFound = 5,

    /// <summary>The area is not inspected in this session (no lesson for its class, cancelled, or a class-only period).</summary>
    NotScheduled = 6,
}

public static class FloorAuditLabels
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Round(AuditRoundStatus s) => s switch
    {
        AuditRoundStatus.Upcoming => "Upcoming",
        AuditRoundStatus.Due => "Due",
        AuditRoundStatus.InProgress => "In Progress",
        AuditRoundStatus.Completed => "Completed",
        AuditRoundStatus.Missed => "Missed",
        _ => s.ToString(),
    };

    /// <summary>CSS modifier shared by badges and cards.</summary>
    public static string Tone(AuditRoundStatus s) => s switch
    {
        AuditRoundStatus.Completed => "ok",
        AuditRoundStatus.Missed => "bad",
        AuditRoundStatus.InProgress => "info",
        AuditRoundStatus.Due => "warn",
        _ => "muted",
    };

    public static string Area(AreaAuditStatus s) => s switch
    {
        AreaAuditStatus.NotStarted => "Not Started",
        AreaAuditStatus.Pending => "Pending",
        AreaAuditStatus.InProgress => "In Progress",
        AreaAuditStatus.Completed => "Completed",
        AreaAuditStatus.IssuesFound => "Issues Found",
        AreaAuditStatus.NotScheduled => "Not scheduled",
        _ => s.ToString(),
    };

    public static string Tone(AreaAuditStatus s) => s switch
    {
        AreaAuditStatus.Completed => "ok",
        AreaAuditStatus.IssuesFound => "bad",
        AreaAuditStatus.InProgress => "info",
        AreaAuditStatus.Pending => "warn",
        _ => "muted",
    };

    public static string Kind(FloorAreaKind k) => k == FloorAreaKind.Classroom ? "Classroom" : "Facility";

    public static readonly FacilityType[] FacilityTypes = Enum.GetValues<FacilityType>();

    public static string Facility(FacilityType? t) => t switch
    {
        null => "Facility",
        FacilityType.NurseRoom => "Nurse Room",
        FacilityType.PrayerRoom => "Prayer Room",
        { } x => x.ToString(),
    };

    public static string FacilityIcon(FacilityType? t) => t switch
    {
        FacilityType.Bathroom => "users",
        FacilityType.Corridor => "arrow-right",
        FacilityType.Stairs => "layers",
        FacilityType.Reception or FacilityType.Administration or FacilityType.Office => "briefcase",
        FacilityType.Library => "file-text",
        FacilityType.NurseRoom => "plus",
        FacilityType.Laboratory => "sparkles",
        FacilityType.Playground => "sun",
        FacilityType.Canteen => "grid",
        _ => "school",
    };

    public static string Scope(AuditItemScope s) => s switch
    {
        AuditItemScope.Classroom => "Classrooms",
        AuditItemScope.Facility => "Facilities",
        _ => "Classrooms & facilities",
    };

    public static bool Applies(AuditItemScope scope, FloorAreaKind kind) =>
        scope == AuditItemScope.Both || (scope == AuditItemScope.Classroom) == (kind == FloorAreaKind.Classroom);

    public static string Movement(MovementState s) => s switch
    {
        MovementState.Outside => "Outside",
        MovementState.Overdue => "Overdue",
        MovementState.Returned => "Returned",
        MovementState.Voided => "Voided",
        _ => s.ToString(),
    };

    public static string Tone(MovementState s) => s switch
    {
        MovementState.Returned => "ok",
        MovementState.Overdue => "bad",
        MovementState.Outside => "warn",
        _ => "muted",
    };

    /// <summary>"12 min", "1 h 05 min".</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        var minutes = (int)Math.Floor(d.TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    /// <summary>"8:06 AM".</summary>
    public static string Time(TimeOnly t) => t.ToString("h:mm tt", C);

    /// <summary>A stored UTC instant as school time, "8:07 AM".</summary>
    public static string Time(DateTime utc) => SchoolTime.Current.ToLocal(utc).ToString("h:mm tt", C);
}

/// <summary>A student movement's state: outside (open), overdue (open beyond the configured threshold), returned or voided.</summary>
public enum MovementState
{
    Outside = 1,
    Overdue = 2,
    Returned = 3,
    Voided = 4,
}

/// <summary>Answer types of checklist items: their choices, which answer is an issue, and validation.</summary>
public static class AuditResponses
{
    public static readonly AuditResponseType[] All = Enum.GetValues<AuditResponseType>();

    /// <summary>Largest number a numeric item accepts (a class count, a headcount).</summary>
    public const int MaxNumber = 9999;

    public static string Label(AuditResponseType t) => t switch
    {
        AuditResponseType.PassFail => "Pass / Fail",
        AuditResponseType.YesNo => "Yes / No",
        AuditResponseType.GoodNeedsAttention => "Good / Needs Attention",
        AuditResponseType.PresentAbsent => "Present / Absent",
        AuditResponseType.Number => "Number",
        AuditResponseType.Text => "Observation (text)",
        AuditResponseType.IssueText => "Issue description (text = issue)",
        _ => t.ToString(),
    };

    /// <summary>The choices of a choice type, the satisfactory one first; empty for numbers and text.</summary>
    public static IReadOnlyList<string> Choices(AuditResponseType t) => t switch
    {
        AuditResponseType.PassFail => ["Pass", "Fail"],
        AuditResponseType.YesNo => ["Yes", "No"],
        AuditResponseType.GoodNeedsAttention => ["Good", "Needs Attention"],
        AuditResponseType.PresentAbsent => ["Present", "Absent"],
        _ => [],
    };

    public static bool IsChoice(AuditResponseType t) => Choices(t).Count > 0;
    public static bool IsText(AuditResponseType t) => t is AuditResponseType.Text or AuditResponseType.IssueText;

    /// <summary>True when a (valid, cleaned) answer is an issue: the second choice, or any text in an issue item.</summary>
    public static bool IsIssue(AuditResponseType t, string? value) => t switch
    {
        _ when string.IsNullOrWhiteSpace(value) => false,
        AuditResponseType.IssueText => true,
        _ when IsChoice(t) => string.Equals(value, Choices(t)[1], StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// Cleans an answer and checks it against its type: a listed choice, a whole number from 0 to <see cref="MaxNumber"/>,
    /// or text up to the maximum length. Empty is null (allowed here; required items are checked on completion).
    /// </summary>
    public static (string? Value, string? Error) Clean(AuditResponseType t, string? raw)
    {
        var text = TextNormalizer.CleanText(raw);
        if (text.Length == 0) return (null, null);
        if (IsChoice(t))
        {
            var match = Choices(t).FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
            return match is null ? (null, $"Choose {string.Join(" or ", Choices(t))}.") : (match, null);
        }
        if (t == AuditResponseType.Number)
        {
            var digits = TextNormalizer.ToLatinDigits(text);
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxNumber
                ? (n.ToString(CultureInfo.InvariantCulture), null)
                : (null, $"Enter a whole number from 0 to {MaxNumber}.");
        }
        // Text keeps its line breaks; only the ends are trimmed.
        var multi = raw!.Trim();
        return multi.Length > FloorInspectionResponse.ValueMaxLength
            ? (null, $"Use {FloorInspectionResponse.ValueMaxLength} characters or fewer.")
            : (multi, null);
    }
}
