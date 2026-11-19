using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>One lesson of the day as the audit schedule needs it (read from the timetable, never copied).</summary>
public sealed record AuditLesson(int SlotId, int SlotNumber, string? SlotName, TimeOnly? Start, TimeOnly? End, bool SchoolWide, int ClassId, string ClassName, bool Cancelled);

/// <summary>
/// A timetable session of one date: a period with lessons. Its three rounds come from its own start time. A session
/// whose every lesson is cancelled is <see cref="Cancelled"/> and has no rounds to inspect.
/// </summary>
public sealed record AuditSession(
    int SlotId, int Number, string Label, TimeOnly Start, TimeOnly? End, bool SchoolWide, bool Cancelled,
    IReadOnlySet<int> ClassIds, IReadOnlySet<int> CancelledClassIds)
{
    public IReadOnlyList<TimeOnly> RoundTimes => FloorAuditRounds.TimesFor(Start);
    public string Times => ScheduleRules.Times12(Start, End);
    public TimeOnly RoundTime(int round) => FloorAuditRounds.TimeOf(Start, round);
    public TimeOnly WindowEnd(int round) => FloorAuditRounds.WindowEnd(Start, End, round);

    /// <summary>The time the session is over for the audit (its end, or round 3's window).</summary>
    public TimeOnly Over => WindowEnd(FloorAuditRounds.Count);
}

/// <summary>An area as the schedule needs it.</summary>
public sealed record AuditAreaRef(int Id, int FloorId, FloorAreaKind Kind, int? ClassId, bool IsActive);

/// <summary>The audit schedule of one date, built from that date's timetable.</summary>
public sealed class AuditDaySchedule
{
    public required DateOnly Date { get; init; }
    public bool IsSchoolDay { get; init; } = true;
    public string? Holiday { get; init; }

    /// <summary>Sessions with a start time, in time order (cancelled ones included, flagged).</summary>
    public IReadOnlyList<AuditSession> Sessions { get; init; } = [];

    /// <summary>Periods with lessons that day but no start time: no rounds can be calculated for them.</summary>
    public IReadOnlyList<string> UntimedPeriods { get; init; } = [];

    public int Lessons { get; init; }

    /// <summary>Sessions that take place (not cancelled).</summary>
    public IEnumerable<AuditSession> Active => Sessions.Where(s => !s.Cancelled);

    /// <summary>True when rounds can be scheduled (at least one session with a start time takes place).</summary>
    public bool HasSchedule => Active.Any();

    public AuditSession? Get(int slotId) => Sessions.FirstOrDefault(s => s.SlotId == slotId);

    /// <summary>
    /// The session running at <paramref name="now"/> (the latest one started and not over), else the next one, else the
    /// last one of the day. Null without sessions.
    /// </summary>
    public AuditSession? CurrentAt(TimeOnly now)
    {
        var active = Active.ToList();
        if (active.Count == 0) return null;
        return active.LastOrDefault(s => s.Start <= now && now < s.Over)
               ?? active.FirstOrDefault(s => s.Start > now)
               ?? active[^1];
    }

    /// <summary>The round to show for a session: the latest one whose time has come (round 1 before that).</summary>
    public static int CurrentRound(AuditSession s, DateOnly date, DateOnly today, TimeOnly now)
    {
        if (date < today) return FloorAuditRounds.Count;
        if (date > today) return 1;
        var round = 1;
        for (var r = 1; r <= FloorAuditRounds.Count; r++) if (s.RoundTime(r) <= now) round = r;
        return round;
    }
}

/// <summary>Builds the schedule and decides statuses. Pure functions: no database, no clock of their own.</summary>
public static class FloorAuditSchedule
{
    /// <summary>
    /// Groups the day's lessons by period: each period with a start time is one session (rounds at +6, +25 and +40
    /// minutes from that start); periods without a start time are listed as untimed. A session is cancelled when every
    /// one of its lessons is cancelled.
    /// </summary>
    public static AuditDaySchedule Build(DateOnly date, IEnumerable<AuditLesson> lessons, bool isSchoolDay = true, string? holiday = null)
    {
        var all = lessons.ToList();
        if (!isSchoolDay) return new AuditDaySchedule { Date = date, IsSchoolDay = false, Holiday = holiday };
        var sessions = new List<AuditSession>();
        var untimed = new List<string>();
        foreach (var g in all.GroupBy(l => l.SlotId))
        {
            var first = g.First();
            var label = string.IsNullOrWhiteSpace(first.SlotName) ? $"Period {first.SlotNumber}" : first.SlotName!.Trim();
            if (!first.SchoolWide)
            {
                var classes = g.Select(l => l.ClassName).Distinct().ToList();
                label += $" ({string.Join(", ", classes)} only)";
            }
            if (first.Start is not { } start)
            {
                untimed.Add(label);
                continue;
            }
            var running = g.Where(l => !l.Cancelled).Select(l => l.ClassId).ToHashSet();
            var cancelled = g.Where(l => l.Cancelled).Select(l => l.ClassId).Where(c => !running.Contains(c)).ToHashSet();
            sessions.Add(new AuditSession(g.Key, first.SlotNumber, label, start, first.End, first.SchoolWide, running.Count == 0, running, cancelled));
        }
        return new AuditDaySchedule
        {
            Date = date,
            Sessions = sessions.OrderBy(s => s.Start).ThenBy(s => s.Number).ThenBy(s => s.SlotId).ToList(),
            UntimedPeriods = untimed,
            Lessons = all.Count,
        };
    }

    /// <summary>
    /// Whether an area must be inspected in a session: active, the session takes place, and — a classroom: its class
    /// has a lesson in it that is not cancelled; a facility: the session is a school-wide period (a period of one class
    /// only concerns that classroom).
    /// </summary>
    public static bool IsRequired(AuditAreaRef area, AuditSession session) =>
        area.IsActive && !session.Cancelled && (area.Kind == FloorAreaKind.Facility
            ? session.SchoolWide
            : area.ClassId is { } c && session.ClassIds.Contains(c));

    /// <summary>
    /// A round's status from its required areas and their inspections. Completed only when every required area has a
    /// completed inspection; Missed when its time is over without that; never completed just because time passed.
    /// </summary>
    public static AuditRoundStatus RoundStatus(AuditSession s, int round, DateOnly date, DateOnly today, TimeOnly now, int required, int completed, int started)
    {
        if (required > 0 && completed >= required) return AuditRoundStatus.Completed;
        var at = s.RoundTime(round);
        if (date > today || (date == today && now < at)) return started > 0 ? AuditRoundStatus.InProgress : AuditRoundStatus.Upcoming;
        var over = date < today || now >= s.WindowEnd(round);
        if (over) return AuditRoundStatus.Missed;
        return started > 0 ? AuditRoundStatus.InProgress : AuditRoundStatus.Due;
    }

    /// <summary>True when the round's scheduled time has arrived (an inspection may be recorded).</summary>
    public static bool HasStarted(AuditSession s, int round, DateOnly date, DateOnly today, TimeOnly now) =>
        date < today || (date == today && now >= s.RoundTime(round));

    /// <summary>An area's status in a round.</summary>
    public static AreaAuditStatus AreaStatus(bool required, FloorInspectionStatus? inspection, int issues, bool roundStarted) => inspection switch
    {
        FloorInspectionStatus.Completed => issues > 0 ? AreaAuditStatus.IssuesFound : AreaAuditStatus.Completed,
        FloorInspectionStatus.InProgress => AreaAuditStatus.InProgress,
        _ when !required => AreaAuditStatus.NotScheduled,
        _ => roundStarted ? AreaAuditStatus.Pending : AreaAuditStatus.NotStarted,
    };

    /// <summary>Loads a date's schedule from the timetable: its real lessons (holidays, cancellations and period times included).</summary>
    public static async Task<AuditDaySchedule> LoadAsync(SchoolDbContext db, DateOnly date, ISchoolTimeService clock, CancellationToken ct)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        if (!days.IsSchoolDay(date)) return Build(date, [], false, days.HolidayOn(date)?.Name);
        var lessons = await Lessons.OnAsync(db, date, days, null, ct, clock);
        if (lessons.Count == 0) return Build(date, []);
        var slotIds = lessons.Select(l => l.SlotId).Distinct().ToList();
        var schoolWide = (await db.TimeSlots.AsNoTracking().Where(s => slotIds.Contains(s.Id) && s.SchoolClassId == null)
            .Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        return Build(date, lessons.Select(l => new AuditLesson(l.SlotId, l.SlotNumber, l.SlotName, l.Start, l.End, schoolWide.Contains(l.SlotId),
            l.ClassId, l.ClassName, l.Cancelled)));
    }
}
