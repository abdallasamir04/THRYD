using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>A Round that can take a booking now, with its places left.</summary>
public sealed record PtcBookableRound(int RoundId, int EventId, int ScheduleId, int ClassId, DateOnly Date, int Number, TimeOnly Start, TimeOnly End, int Capacity, int Booked)
{
    public int Remaining => Math.Max(0, Capacity - Booked);
}

/// <summary>
/// "Bookings open", defined once and used by the Admin dashboard, the parent portal and the booking service: a Round is
/// bookable when its event is Published (so not Cancelled or Completed), its class schedule is active (Scheduled), the Round
/// itself is Open, it starts in the future (school time) and it has a place left. An event has bookings open when at least
/// one of its Rounds is bookable. Always read from the current database state.
/// </summary>
public static class PtcAvailability
{
    /// <summary>The Round starts after <paramref name="schoolNow"/> (school-local wall time).</summary>
    public static bool IsFuture(DateOnly date, TimeOnly start, DateTime schoolNow) => date.ToDateTime(start) > schoolNow;

    /// <summary>Bookable Rounds (optionally of one event / one class schedule), in date and time order.</summary>
    public static async Task<IReadOnlyList<PtcBookableRound>> BookableRoundsAsync(SchoolDbContext db, DateTime schoolNow, CancellationToken ct,
        int? eventId = null, int? scheduleId = null, bool includeFull = false)
    {
        var today = DateOnly.FromDateTime(schoolNow);
        var time = TimeOnly.FromDateTime(schoolNow);
        var q = db.PtcRounds.AsNoTracking().Where(r =>
            r.Status == PtcRoundStatus.Open
            && r.ClassSchedule.Status == PtcScheduleStatus.Scheduled
            && r.ClassSchedule.PtcEvent.Status == PtcEventStatus.Published
            && (r.ClassSchedule.EventDate > today || (r.ClassSchedule.EventDate == today && r.StartTime > time)));
        if (eventId is { } e) q = q.Where(r => r.ClassSchedule.PtcEventId == e);
        if (scheduleId is { } s) q = q.Where(r => r.PtcClassScheduleId == s);
        var rows = await q
            .Select(r => new PtcBookableRound(r.Id, r.ClassSchedule.PtcEventId, r.PtcClassScheduleId, r.ClassSchedule.SchoolClassId, r.ClassSchedule.EventDate,
                r.RoundNumber, r.StartTime, r.EndTime, r.Capacity, r.Bookings.Count(b => b.Status != PtcBookingStatus.Cancelled)))
            .ToListAsync(ct);
        return rows.Where(r => includeFull || r.Remaining > 0).OrderBy(r => r.Date).ThenBy(r => r.Start).ToList();
    }

    /// <summary>The events (of <paramref name="eventIds"/>) that have bookings open now.</summary>
    public static async Task<HashSet<int>> OpenEventsAsync(SchoolDbContext db, DateTime schoolNow, CancellationToken ct) =>
        (await BookableRoundsAsync(db, schoolNow, ct)).Select(r => r.EventId).ToHashSet();
}
