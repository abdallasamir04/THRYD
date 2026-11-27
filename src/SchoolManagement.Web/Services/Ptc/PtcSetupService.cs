using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>An event's details as entered.</summary>
public sealed record PtcEventInput(string? Title, string? Description, int AcademicYearId, DateOnly? StartDate, DateOnly? EndDate);

/// <summary>One participating class in "Classes &amp; dates": its PTC date and daily time window.</summary>
public sealed record PtcClassDateInput(int ClassId, DateOnly? Date, TimeOnly? Start, TimeOnly? End);

/// <summary>
/// Rounds to add to a class schedule: <see cref="Count"/> Rounds from <see cref="FirstStart"/>, each
/// <see cref="DurationMinutes"/> long, <see cref="GapMinutes"/> apart, with their own capacity and meeting / transition times.
/// </summary>
public sealed record PtcRoundPlan(TimeOnly FirstStart, int Count, int DurationMinutes, int Capacity, int MeetingMinutes, int TransitionMinutes, int GapMinutes = 0)
{
    public const int MaxCount = 12;
    public const int MaxGapMinutes = 120;

    public PtcRoundDefaults Values => new(Capacity, MeetingMinutes, TransitionMinutes, DurationMinutes);

    /// <summary>The planned Rounds' times (null when one would run past midnight).</summary>
    public IReadOnlyList<(TimeOnly Start, TimeOnly End)>? Windows()
    {
        var list = new List<(TimeOnly, TimeOnly)>();
        var start = FirstStart.ToTimeSpan();
        for (var i = 0; i < Count; i++)
        {
            var end = start + TimeSpan.FromMinutes(DurationMinutes);
            if (end >= TimeSpan.FromDays(1)) return null;
            list.Add((TimeOnly.FromTimeSpan(start), TimeOnly.FromTimeSpan(end)));
            start = end + TimeSpan.FromMinutes(GapMinutes);
        }
        return list;
    }
}

/// <summary>
/// PTC set-up operations with their server-side rules. Every operation is for the Super Admin only (<see cref="IPtcAccess"/>)
/// and uses existing classes, staff and academic years. Classes, dates and Rounds change only while the event is a Draft;
/// a Published event returns to Draft only while it has no confirmed booking, so bookings are never changed by set-up. Rows
/// with bookings or substitute history are never deleted (they are cancelled and kept).
/// </summary>
public interface IPtcSetupService
{
    /// <summary>Creates (<paramref name="id"/> null) or edits an event. A Published event's academic year and dates are fixed.</summary>
    Task<ServiceResult> SaveEventAsync(int? id, PtcEventInput input, CancellationToken ct = default);

    /// <summary>Schedules an existing class in a Draft event on its own date and time window (one active schedule per class and event).</summary>
    Task<ServiceResult> ScheduleClassAsync(int eventId, int classId, DateOnly date, TimeOnly start, TimeOnly end, string? notes, CancellationToken ct = default);

    /// <summary>Changes a class schedule's date, daily times and notes (Draft only; its Rounds must still fit).</summary>
    Task<ServiceResult> UpdateClassScheduleAsync(int scheduleId, DateOnly date, TimeOnly start, TimeOnly end, string? notes, CancellationToken ct = default);

    /// <summary>Takes a class out of a Draft event (with its Rounds).</summary>
    Task<ServiceResult> RemoveClassScheduleAsync(int scheduleId, CancellationToken ct = default);

    /// <summary>
    /// "Classes &amp; dates": makes the event's participating classes exactly <paramref name="classes"/> — new ones are added,
    /// changed dates / times are updated (Rounds kept), classes left out are removed — as one unit: nothing is saved when any
    /// class is invalid.
    /// </summary>
    Task<ServiceResult> AssignClassesAsync(int eventId, IReadOnlyList<PtcClassDateInput> classes, CancellationToken ct = default);

    /// <summary>Adds one Round with the PTC defaults, starting at <paramref name="start"/> (Draft only).</summary>
    Task<ServiceResult> AddRoundAsync(int scheduleId, TimeOnly start, CancellationToken ct = default);

    /// <summary>
    /// Adds the planned Rounds to the class schedule — and, with <paramref name="sameDayClasses"/>, to every other class of the
    /// event on the same date that has no Rounds yet. All fit their daily time and overlap no other Round, or nothing is saved.
    /// </summary>
    Task<ServiceResult> AddRoundsAsync(int scheduleId, PtcRoundPlan plan, bool sameDayClasses = false, CancellationToken ct = default);

    /// <summary>Removes a Round from a Draft event (the others are renumbered in time order).</summary>
    Task<ServiceResult> RemoveRoundAsync(int roundId, CancellationToken ct = default);

    /// <summary>Publishes a Draft event when nothing blocks it; warnings must be acknowledged.</summary>
    Task<ServiceResult> PublishAsync(int eventId, bool acknowledgeWarnings, CancellationToken ct = default);

    /// <summary>Returns a Published event to Draft (only while it has no confirmed booking).</summary>
    Task<ServiceResult> ReturnToDraftAsync(int eventId, CancellationToken ct = default);

    /// <summary>Cancels a Draft or Published event (only while it has no confirmed booking). Everything is kept as history.</summary>
    Task<ServiceResult> CancelEventAsync(int eventId, CancellationToken ct = default);

    /// <summary>Marks a Published event Completed (from its last date).</summary>
    Task<ServiceResult> CompleteEventAsync(int eventId, CancellationToken ct = default);

    /// <summary>Reopens a Cancelled event as a Draft.</summary>
    Task<ServiceResult> ReopenEventAsync(int eventId, CancellationToken ct = default);


    /// <summary>Records a substitute chosen by an administrator (never automatic) on the class schedule's date.</summary>
    Task<ServiceResult> AssignSubstituteAsync(int scheduleId, int originalTeacherId, int substituteTeacherId, TimeOnly start, TimeOnly end,
        string? reason, CancellationToken ct = default);

    /// <summary>Cancels a substitute assignment (kept as history with who and when).</summary>
    Task<ServiceResult> CancelSubstituteAsync(int assignmentId, CancellationToken ct = default);
}

public sealed class PtcSetupService(SchoolDbContext db, IPtcAccess access, ISettingsService settings, ICurrentUser currentUser,
    IPtcAdminService? admin = null, ISchoolTimeService? schoolTime = null) : IPtcSetupService
{
    /// <summary>The longest event (days, first to last date).</summary>
    public const int MaxEventDays = 31;

    public const string DraftOnlyMessage =
        "This event is Published: return it to Draft to change its classes, dates or Rounds (possible while it has no confirmed bookings).";
    public const string ClosedMessage = "This event is closed (Completed or Cancelled).";

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    /// <summary>Serialises PTC writes that check other PTC rows (shared with bookings): same lock everywhere, no deadlocks.</summary>
    internal static Task LockAsync(SchoolDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; " +
            "IF @r < 0 THROW 51002, 'PTC lock timeout', 1;", ["ptc:schedule"], ct);

    private Task<DbScope> BeginAsync(CancellationToken ct) => DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);

    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", C);

    private Task<int> ActiveBookingsAsync(int eventId, CancellationToken ct) =>
        db.PtcParentBookings.CountAsync(b => b.Round.ClassSchedule.PtcEventId == eventId && b.Status != PtcBookingStatus.Cancelled, ct);

    /// <summary>The academic year's span (its quarters' school and calendar dates), when known.</summary>
    private async Task<(DateOnly? First, DateOnly? Last)> YearSpanAsync(int yearId, CancellationToken ct)
    {
        var span = await db.AcademicQuarters.AsNoTracking().Where(q => q.AcademicYearId == yearId)
            .Select(q => new { q.StartDate, q.EndDate, q.CalendarStartDate, q.CalendarEndDate }).ToListAsync(ct);
        return (span.SelectMany(q => new[] { q.StartDate, q.CalendarStartDate }).Where(d => d is not null).Min(),
            span.SelectMany(q => new[] { q.EndDate, q.CalendarEndDate }).Where(d => d is not null).Max());
    }

    private sealed record EventRow(int Id, PtcEventStatus Status, DateOnly StartDate, DateOnly EndDate);

    private Task<EventRow?> EventRowAsync(int eventId, CancellationToken ct) =>
        db.PtcEvents.AsNoTracking().Where(e => e.Id == eventId).Select(e => new EventRow(e.Id, e.Status, e.StartDate, e.EndDate)).FirstOrDefaultAsync(ct);

    /// <summary>Why the event's classes, dates and Rounds cannot change now, or null when they can (Draft).</summary>
    private static ServiceResult? NotEditable(EventRow? ev) =>
        ev is null ? ServiceResult.Fail("", "This PTC event no longer exists.")
        : ev.Status == PtcEventStatus.Published ? ServiceResult.Fail("", DraftOnlyMessage)
        : ev.Status != PtcEventStatus.Draft ? ServiceResult.Fail("", ClosedMessage)
        : null;

    private void Touch(PtcEvent e) => e.UpdatedBy = currentUser.AccountName();

    private async Task TouchEventAsync(int eventId, CancellationToken ct)
    {
        var e = await db.PtcEvents.FirstAsync(x => x.Id == eventId, ct);
        Touch(e);
        db.Entry(e).Property(x => x.UpdatedAt).IsModified = true; // stamped on save even when nothing else changed
    }

    // ------------------------------------------------------------------ events

    public async Task<ServiceResult> SaveEventAsync(int? id, PtcEventInput input, CancellationToken ct = default)
    {
        access.Ensure();
        var title = TextNormalizer.CleanText(input.Title);
        var description = TextNormalizer.CleanText(input.Description);
        if (title.Length == 0) return ServiceResult.Fail("Title", "Enter the PTC title.");
        if (title.Length > PtcEvent.TitleMaxLength) return ServiceResult.Fail("Title", $"Use {PtcEvent.TitleMaxLength} characters or fewer.");
        if (description.Length > PtcEvent.DescriptionMaxLength) return ServiceResult.Fail("Description", $"Use {PtcEvent.DescriptionMaxLength} characters or fewer.");
        if (input.StartDate is not { } start) return ServiceResult.Fail("StartDate", "Enter the first date of the PTC.");
        if (input.EndDate is not { } end) return ServiceResult.Fail("EndDate", "Enter the last date of the PTC.");
        if (end < start) return ServiceResult.Fail("EndDate", "The last date cannot be before the first date.");
        if (end.DayNumber - start.DayNumber + 1 > MaxEventDays) return ServiceResult.Fail("EndDate", $"A PTC event can cover at most {MaxEventDays} days.");
        if (!await db.AcademicYears.AnyAsync(y => y.Id == input.AcademicYearId, ct)) return ServiceResult.Fail("AcademicYearId", "Choose an existing academic year.");
        var (first, last) = await YearSpanAsync(input.AcademicYearId, ct);
        if ((first is { } f && start < f) || (last is { } l && end > l))
            return ServiceResult.Fail("StartDate", $"The dates must be within the academic year ({Day(first ?? start)} – {Day(last ?? end)}).");

        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        if (await db.PtcEvents.AnyAsync(e => e.Id != (id ?? 0) && e.AcademicYearId == input.AcademicYearId && e.Status != PtcEventStatus.Cancelled && e.Title == title, ct))
            return ServiceResult.Fail("Title", "Another PTC event of this academic year already has this title.");

        PtcEvent ev;
        if (id is null)
        {
            ev = new PtcEvent { CreatedBy = currentUser.AccountName(), PublicToken = PtcRules.NewPublicToken() };
            db.PtcEvents.Add(ev);
        }
        else
        {
            var found = await db.PtcEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (found is null) return ServiceResult.Fail("", "This PTC event no longer exists.");
            ev = found;
            if (ev.Status is PtcEventStatus.Completed or PtcEventStatus.Cancelled) return ServiceResult.Fail("", ClosedMessage);
            var structural = ev.AcademicYearId != input.AcademicYearId || ev.StartDate != start || ev.EndDate != end;
            if (structural && ev.Status == PtcEventStatus.Published)
                return ServiceResult.Fail("StartDate", "The academic year and dates of a Published event cannot change: return it to Draft first (possible while it has no confirmed bookings).");
            if (structural)
            {
                var outside = await db.PtcClassSchedules.AsNoTracking()
                    .Where(s => s.PtcEventId == ev.Id && s.Status != PtcScheduleStatus.Cancelled && (s.EventDate < start || s.EventDate > end))
                    .Select(s => s.SchoolClass.Name).ToListAsync(ct);
                if (outside.Count > 0)
                    return ServiceResult.Fail("StartDate", $"These classes have PTC dates outside the new dates: {string.Join(", ", outside)}. Move them first.");
            }
        }
        ev.Title = title;
        ev.Description = description.Length == 0 ? null : description;
        ev.AcademicYearId = input.AcademicYearId;
        ev.StartDate = start;
        ev.EndDate = end;
        Touch(ev);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(ev.Id);
    }

    // ------------------------------------------------------------------ status

    public async Task<ServiceResult> PublishAsync(int eventId, bool acknowledgeWarnings, CancellationToken ct = default)
    {
        access.Ensure();
        if (admin is null) throw new InvalidOperationException("Publishing needs the PTC readiness assessment.");
        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var ev = await db.PtcEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return ServiceResult.Fail("", "This PTC event no longer exists.");
        if (ev.Status != PtcEventStatus.Draft) return ServiceResult.Fail("", "Only a Draft event can be published.");

        // The same server-side assessment the preview shows, read inside the lock so nothing changes in between. It includes
        // class coverage, computed again now from the timetable and substitutes (never from a stored status).
        var detail = (await admin.EventAsync(eventId, ct))!;
        var readiness = detail.Readiness;
        if (!readiness.CanPublish)
        {
            var result = ServiceResult.Fail("", detail.Coverage.BlockSummary is { } blocked
                ? $"{blocked} Assign substitutes on the Class Coverage page (/ptc/events/{eventId}/coverage). {Ui.Plural(readiness.Blockers.Count, "issue blocks", "issues block")} publishing in all."
                : $"The event cannot be published: {Ui.Plural(readiness.Blockers.Count, "issue blocks", "issues block")} it.");
            foreach (var b in readiness.Blockers.Take(20)) result.AddError("", b.Message);
            return result;
        }
        if (readiness.Warnings.Count > 0 && !acknowledgeWarnings)
            return ServiceResult.Fail("AcknowledgeWarnings",
                $"Confirm that you have reviewed the {Ui.Plural(readiness.Warnings.Count, "warning", "warnings")} (for example parents who cannot book yet) to publish.");

        ev.Status = PtcEventStatus.Published;
        ev.PublicToken ??= PtcRules.NewPublicToken();
        Touch(ev);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(ev.Id);
    }

    private async Task<ServiceResult> ChangeStatusAsync(int eventId, Func<PtcEvent, Task<ServiceResult?>> check, PtcEventStatus to, CancellationToken ct)
    {
        access.Ensure();
        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var ev = await db.PtcEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return ServiceResult.Fail("", "This PTC event no longer exists.");
        if (await check(ev) is { } problem) return problem;
        ev.Status = to;
        Touch(ev);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(ev.Id);
    }

    public Task<ServiceResult> ReturnToDraftAsync(int eventId, CancellationToken ct = default) =>
        ChangeStatusAsync(eventId, async ev =>
            ev.Status != PtcEventStatus.Published ? ServiceResult.Fail("", "Only a Published event can return to Draft.")
            : await ActiveBookingsAsync(eventId, ct) is var n and > 0
                ? ServiceResult.Fail("", $"This event has {Ui.Plural(n, "confirmed booking", "confirmed bookings")}: it stays Published so they are not changed.")
            : null, PtcEventStatus.Draft, ct);

    public Task<ServiceResult> CancelEventAsync(int eventId, CancellationToken ct = default) =>
        ChangeStatusAsync(eventId, async ev =>
            ev.Status is not (PtcEventStatus.Draft or PtcEventStatus.Published) ? ServiceResult.Fail("", "Only a Draft or Published event can be cancelled.")
            : await ActiveBookingsAsync(eventId, ct) is var n and > 0
                ? ServiceResult.Fail("", $"This event has {Ui.Plural(n, "confirmed booking", "confirmed bookings")}: it cannot be cancelled until they are handled.")
            : null, PtcEventStatus.Cancelled, ct);

    public Task<ServiceResult> CompleteEventAsync(int eventId, CancellationToken ct = default) =>
        ChangeStatusAsync(eventId, ev => Task.FromResult(
            ev.Status != PtcEventStatus.Published ? ServiceResult.Fail("", "Only a Published event can be completed.")
            : _time.Today < ev.EndDate ? ServiceResult.Fail("", $"The event can be completed from its last date ({Day(ev.EndDate)}).")
            : null), PtcEventStatus.Completed, ct);

    public Task<ServiceResult> ReopenEventAsync(int eventId, CancellationToken ct = default) =>
        ChangeStatusAsync(eventId, async ev =>
        {
            if (ev.Status != PtcEventStatus.Cancelled) return ServiceResult.Fail("", "Only a Cancelled event can be reopened.");
            // Its classes may have been given the same date in another event meanwhile.
            var clash = await db.PtcClassSchedules.AsNoTracking()
                .Where(s => s.PtcEventId == eventId && s.Status != PtcScheduleStatus.Cancelled)
                .Where(s => db.PtcClassSchedules.Any(o => o.PtcEventId != eventId && o.SchoolClassId == s.SchoolClassId && o.EventDate == s.EventDate
                    && o.Status != PtcScheduleStatus.Cancelled && (o.PtcEvent.Status == PtcEventStatus.Draft || o.PtcEvent.Status == PtcEventStatus.Published)))
                .Select(s => s.SchoolClass.Name).ToListAsync(ct);
            return clash.Count > 0
                ? ServiceResult.Fail("", $"These classes already have PTC on the same date in another event: {string.Join(", ", clash)}.")
                : null;
        }, PtcEventStatus.Draft, ct);

    // ------------------------------------------------------------------ classes and dates

    /// <summary>Why a class cannot have PTC on this date and time in the event, or null.</summary>
    private async Task<(string Field, string Message)?> ScheduleProblemAsync(EventRow ev, int classId, DateOnly date, TimeOnly start, TimeOnly end,
        int? scheduleId, CancellationToken ct)
    {
        var cls = await db.Classes.AsNoTracking().Where(c => c.Id == classId).Select(c => new { c.Name, c.IsActive }).FirstOrDefaultAsync(ct);
        if (cls is null) return ("SchoolClassId", "Choose an existing class.");
        if (!cls.IsActive) return ("SchoolClassId", $"{cls.Name} is not an active class.");
        if (end <= start) return ("EndTime", $"{cls.Name}: the end time must be after the start time.");
        if (date < ev.StartDate || date > ev.EndDate)
            return ("EventDate", $"{cls.Name}: {Day(date)} is outside the event dates ({Day(ev.StartDate)} – {Day(ev.EndDate)}).");
        if (await db.PtcClassSchedules.AnyAsync(s => s.PtcEventId == ev.Id && s.SchoolClassId == classId && s.Status != PtcScheduleStatus.Cancelled && s.Id != (scheduleId ?? 0), ct))
            return ("SchoolClassId", $"{cls.Name} is already scheduled in this PTC event.");
        var other = await db.PtcClassSchedules.AsNoTracking()
            .Where(s => s.PtcEventId != ev.Id && s.SchoolClassId == classId && s.EventDate == date && s.Status != PtcScheduleStatus.Cancelled
                        && (s.PtcEvent.Status == PtcEventStatus.Draft || s.PtcEvent.Status == PtcEventStatus.Published))
            .Select(s => s.PtcEvent.Title).FirstOrDefaultAsync(ct);
        if (other is not null) return ("EventDate", $"{cls.Name} already has PTC on {Day(date)} in \"{other}\".");
        if (scheduleId is { } id)
        {
            var outside = await db.PtcRounds.AsNoTracking()
                .Where(r => r.PtcClassScheduleId == id && r.Status != PtcRoundStatus.Cancelled && (r.StartTime < start || r.EndTime > end))
                .Select(r => r.RoundNumber).ToListAsync(ct);
            if (outside.Count > 0)
                return ("StartTime", $"{cls.Name}: Round {string.Join(", ", outside)} would fall outside {ScheduleRules.Times(start, end)}. Change or remove the Rounds first.");
        }
        return null;
    }

    public async Task<ServiceResult> ScheduleClassAsync(int eventId, int classId, DateOnly date, TimeOnly start, TimeOnly end, string? notes, CancellationToken ct = default)
    {
        access.Ensure();
        var cleanNotes = TextNormalizer.CleanText(notes);
        if (cleanNotes.Length > PtcClassSchedule.NotesMaxLength) return ServiceResult.Fail("Notes", $"Use {PtcClassSchedule.NotesMaxLength} characters or fewer.");
        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var ev = await EventRowAsync(eventId, ct);
        if (NotEditable(ev) is { } denied) return denied;
        if (await ScheduleProblemAsync(ev!, classId, date, start, end, null, ct) is { } p) return ServiceResult.Fail(p.Field, p.Message);

        var schedule = new PtcClassSchedule
        {
            PtcEventId = eventId, SchoolClassId = classId, EventDate = date, StartTime = start, EndTime = end,
            Notes = cleanNotes.Length == 0 ? null : cleanNotes,
        };
        db.PtcClassSchedules.Add(schedule);
        await TouchEventAsync(eventId, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return ServiceResult.Fail("SchoolClassId", "This class is already scheduled in this PTC event.");
        }
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(schedule.Id);
    }

    public async Task<ServiceResult> UpdateClassScheduleAsync(int scheduleId, DateOnly date, TimeOnly start, TimeOnly end, string? notes, CancellationToken ct = default)
    {
        access.Ensure();
        var cleanNotes = TextNormalizer.CleanText(notes);
        if (cleanNotes.Length > PtcClassSchedule.NotesMaxLength) return ServiceResult.Fail("Notes", $"Use {PtcClassSchedule.NotesMaxLength} characters or fewer.");
        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var schedule = await db.PtcClassSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId && s.Status != PtcScheduleStatus.Cancelled, ct);
        if (schedule is null) return ServiceResult.Fail("", "This class schedule no longer exists.");
        var ev = await EventRowAsync(schedule.PtcEventId, ct);
        if (NotEditable(ev) is { } denied) return denied;
        if (await ScheduleProblemAsync(ev!, schedule.SchoolClassId, date, start, end, scheduleId, ct) is { } p) return ServiceResult.Fail(p.Field, p.Message);

        schedule.EventDate = date;
        schedule.StartTime = start;
        schedule.EndTime = end;
        schedule.Notes = cleanNotes.Length == 0 ? null : cleanNotes;
        await TouchEventAsync(schedule.PtcEventId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(schedule.Id);
    }

    public async Task<ServiceResult> RemoveClassScheduleAsync(int scheduleId, CancellationToken ct = default)
    {
        access.Ensure();
        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var schedule = await db.PtcClassSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId && s.Status != PtcScheduleStatus.Cancelled, ct);
        if (schedule is null) return ServiceResult.Fail("", "This class schedule no longer exists.");
        if (NotEditable(await EventRowAsync(schedule.PtcEventId, ct)) is { } denied) return denied;
        if (await RemoveScheduleAsync(schedule, ct) is { } problem) return ServiceResult.Fail("", problem);
        await TouchEventAsync(schedule.PtcEventId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(schedule.PtcEventId);
    }

    /// <summary>
    /// Takes a schedule out of its event (tracked changes, not saved): deleted with its Rounds when nothing refers to them,
    /// otherwise cancelled and kept as history. Refused while it has a confirmed booking.
    /// </summary>
    private async Task<string?> RemoveScheduleAsync(PtcClassSchedule schedule, CancellationToken ct)
    {
        var bookings = await db.PtcParentBookings.Where(b => b.Round.PtcClassScheduleId == schedule.Id).Select(b => b.Status).ToListAsync(ct);
        if (bookings.Any(s => s != PtcBookingStatus.Cancelled)) return "This class has confirmed bookings and cannot be removed.";
        var rounds = await db.PtcRounds.Where(r => r.PtcClassScheduleId == schedule.Id).ToListAsync(ct);
        var teachers = await db.PtcClassTeachers.Where(t => t.PtcClassScheduleId == schedule.Id).ToListAsync(ct);
        // Former manual KG teacher selections are history too: never deleted silently.
        var hasHistory = bookings.Count > 0 || teachers.Count > 0 || await db.PtcSubstituteAssignments.AnyAsync(a => a.PtcClassScheduleId == schedule.Id, ct)
                         || await db.PtcScheduleChanges.AnyAsync(c => c.PtcClassScheduleId == schedule.Id, ct);
        if (hasHistory)
        {
            schedule.Status = PtcScheduleStatus.Cancelled;
            foreach (var r in rounds) r.Status = PtcRoundStatus.Cancelled;
        }
        else
        {
            // Nothing refers to the configuration: it goes with the schedule.
            db.PtcRounds.RemoveRange(rounds);
            db.PtcClassSchedules.Remove(schedule);
        }
        return null;
    }

    public async Task<ServiceResult> AssignClassesAsync(int eventId, IReadOnlyList<PtcClassDateInput> classes, CancellationToken ct = default)
    {
        access.Ensure();
        if (classes.GroupBy(c => c.ClassId).Any(g => g.Count() > 1)) return ServiceResult.Fail("", "A class was listed twice.");
        var result = new ServiceResult();
        foreach (var c in classes)
        {
            if (c.Date is null) result.AddError($"Date_{c.ClassId}", "Choose the PTC date of every selected class.");
            if (c.Start is null || c.End is null) result.AddError($"Start_{c.ClassId}", "Enter the daily start and end time of every selected class.");
        }
        if (!result.Succeeded) return Distinct(result);

        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var ev = await EventRowAsync(eventId, ct);
        if (NotEditable(ev) is { } denied) return denied;
        var existing = await db.PtcClassSchedules.Where(s => s.PtcEventId == eventId && s.Status != PtcScheduleStatus.Cancelled).ToListAsync(ct);

        // Validate every class against the saved state, then apply all changes together.
        var adds = new List<PtcClassDateInput>();
        foreach (var c in classes)
        {
            var current = existing.FirstOrDefault(s => s.SchoolClassId == c.ClassId);
            if (current is not null && current.EventDate == c.Date && current.StartTime == c.Start && current.EndTime == c.End) continue;
            if (await ScheduleProblemAsync(ev!, c.ClassId, c.Date!.Value, c.Start!.Value, c.End!.Value, current?.Id, ct) is { } p)
            {
                result.AddError($"Date_{c.ClassId}", p.Message);
                continue;
            }
            if (current is null) adds.Add(c);
            else
            {
                current.EventDate = c.Date!.Value;
                current.StartTime = c.Start!.Value;
                current.EndTime = c.End!.Value;
            }
        }
        foreach (var removed in existing.Where(s => classes.All(c => c.ClassId != s.SchoolClassId)))
            if (await RemoveScheduleAsync(removed, ct) is { } problem)
            {
                var name = await db.Classes.Where(x => x.Id == removed.SchoolClassId).Select(x => x.Name).FirstAsync(ct);
                result.AddError("", $"{name}: {problem}");
            }
        if (!result.Succeeded)
        {
            db.ChangeTracker.Clear();
            return result;
        }

        foreach (var c in adds)
            db.PtcClassSchedules.Add(new PtcClassSchedule { PtcEventId = eventId, SchoolClassId = c.ClassId, EventDate = c.Date!.Value, StartTime = c.Start!.Value, EndTime = c.End!.Value });
        await TouchEventAsync(eventId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(eventId);
    }

    private static ServiceResult Distinct(ServiceResult r)
    {
        var d = new ServiceResult();
        foreach (var (k, v) in r.Errors.DistinctBy(e => e.Value)) d.AddError(k, v);
        return d;
    }

    // ------------------------------------------------------------------ rounds

    public async Task<ServiceResult> AddRoundAsync(int scheduleId, TimeOnly start, CancellationToken ct = default)
    {
        access.Ensure();
        var d = await settings.GetPtcDefaultsAsync(ct);
        return await AddRoundsAsync(scheduleId, new PtcRoundPlan(start, 1, d.RoundDurationMinutes, d.Capacity, d.MeetingDurationMinutes, d.TransitionDurationMinutes), false, ct);
    }

    public async Task<ServiceResult> AddRoundsAsync(int scheduleId, PtcRoundPlan plan, bool sameDayClasses = false, CancellationToken ct = default)
    {
        access.Ensure();
        if (plan.Count < 1 || plan.Count > PtcRoundPlan.MaxCount) return ServiceResult.Fail("Count", $"Add between 1 and {PtcRoundPlan.MaxCount} Rounds at a time.");
        if (plan.GapMinutes < 0 || plan.GapMinutes > PtcRoundPlan.MaxGapMinutes) return ServiceResult.Fail("GapMinutes", $"Enter a gap between 0 and {PtcRoundPlan.MaxGapMinutes} minutes.");
        if (plan.Values.Problem() is { } bad) return ServiceResult.Fail(bad.Field, bad.Message);
        var windows = plan.Windows();
        if (windows is null) return ServiceResult.Fail("FirstStart", "The Rounds would run past midnight.");

        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var schedule = await db.PtcClassSchedules.AsNoTracking().Where(s => s.Id == scheduleId && s.Status != PtcScheduleStatus.Cancelled)
            .Select(s => new { s.Id, s.PtcEventId, s.EventDate }).FirstOrDefaultAsync(ct);
        if (schedule is null) return ServiceResult.Fail("", "This class schedule no longer exists.");
        if (NotEditable(await EventRowAsync(schedule.PtcEventId, ct)) is { } denied) return denied;

        var targets = new List<int> { scheduleId };
        if (sameDayClasses)
            targets.AddRange(await db.PtcClassSchedules.AsNoTracking()
                .Where(s => s.PtcEventId == schedule.PtcEventId && s.EventDate == schedule.EventDate && s.Id != scheduleId && s.Status != PtcScheduleStatus.Cancelled
                            && !s.Rounds.Any(r => r.Status != PtcRoundStatus.Cancelled))
                .Select(s => s.Id).ToListAsync(ct));

        var data = await db.PtcClassSchedules.AsNoTracking().Where(s => targets.Contains(s.Id))
            .Select(s => new
            {
                s.Id, ClassName = s.SchoolClass.Name, s.StartTime, s.EndTime,
                Rounds = s.Rounds.Where(r => r.Status != PtcRoundStatus.Cancelled).Select(r => new { r.RoundNumber, r.StartTime, r.EndTime }).ToList(),
            })
            .ToListAsync(ct);
        var result = new ServiceResult();
        foreach (var s in data)
            foreach (var (start, end) in windows)
            {
                if (start < s.StartTime || end > s.EndTime)
                    result.AddError("FirstStart", $"{s.ClassName}: a Round at {ScheduleRules.Times(start, end)} is outside the daily time ({ScheduleRules.Times(s.StartTime, s.EndTime)}).");
                else if (s.Rounds.FirstOrDefault(r => PtcRules.Overlaps(start, end, r.StartTime, r.EndTime)) is { } o)
                    result.AddError("FirstStart", $"{s.ClassName}: a Round at {ScheduleRules.Times(start, end)} overlaps Round {o.RoundNumber} ({ScheduleRules.Times(o.StartTime, o.EndTime)}).");
            }
        if (!result.Succeeded) return Distinct(result);

        var added = new List<PtcRound>();
        foreach (var s in data)
            foreach (var (start, end) in windows)
            {
                var round = new PtcRound
                {
                    PtcClassScheduleId = s.Id, RoundNumber = 900 + added.Count(r => r.PtcClassScheduleId == s.Id) + s.Rounds.Count,
                    StartTime = start, EndTime = end,
                    Capacity = plan.Capacity, MeetingDurationMinutes = plan.MeetingMinutes, TransitionDurationMinutes = plan.TransitionMinutes,
                };
                added.Add(round);
                db.PtcRounds.Add(round);
            }
        await db.SaveChangesAsync(ct);
        foreach (var id in targets) await RenumberAsync(id, ct);
        await TouchEventAsync(schedule.PtcEventId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(added.First(r => r.PtcClassScheduleId == scheduleId).Id);
    }

    /// <summary>Numbers a schedule's Rounds 1, 2, 3… in time order (two steps: the number is unique per schedule).</summary>
    private async Task RenumberAsync(int scheduleId, CancellationToken ct)
    {
        var rounds = await db.PtcRounds.Where(r => r.PtcClassScheduleId == scheduleId).OrderBy(r => r.Status == PtcRoundStatus.Cancelled).ThenBy(r => r.StartTime).ToListAsync(ct);
        if (rounds.Select((r, i) => r.RoundNumber == i + 1).All(x => x)) return;
        for (var i = 0; i < rounds.Count; i++) rounds[i].RoundNumber = 5000 + i;
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < rounds.Count; i++) rounds[i].RoundNumber = i + 1;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ServiceResult> RemoveRoundAsync(int roundId, CancellationToken ct = default)
    {
        access.Ensure();
        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var round = await db.PtcRounds.Include(r => r.ClassSchedule).FirstOrDefaultAsync(r => r.Id == roundId, ct);
        if (round is null || round.Status == PtcRoundStatus.Cancelled) return ServiceResult.Fail("", "This Round no longer exists.");
        if (NotEditable(await EventRowAsync(round.ClassSchedule.PtcEventId, ct)) is { } denied) return denied;
        var bookings = await db.PtcParentBookings.Where(b => b.PtcRoundId == roundId).Select(b => b.Status).ToListAsync(ct);
        if (bookings.Any(s => s != PtcBookingStatus.Cancelled)) return ServiceResult.Fail("", "This Round has confirmed bookings and cannot be removed.");
        // Keep the Round when bookings or schedule changes refer to it (history); otherwise it is removed.
        if (bookings.Count > 0 || await db.PtcScheduleChanges.AnyAsync(c => c.PtcRoundId == roundId, ct)) round.Status = PtcRoundStatus.Cancelled;
        else db.PtcRounds.Remove(round);
        await db.SaveChangesAsync(ct);
        await RenumberAsync(round.PtcClassScheduleId, ct);
        await TouchEventAsync(round.ClassSchedule.PtcEventId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(round.PtcClassScheduleId);
    }

    // ------------------------------------------------------------------ substitutes (manual; screens come in a later phase)

    public async Task<ServiceResult> AssignSubstituteAsync(int scheduleId, int originalTeacherId, int substituteTeacherId, TimeOnly start, TimeOnly end,
        string? reason, CancellationToken ct = default)
    {
        access.Ensure();
        var cleanReason = TextNormalizer.CleanText(reason);
        if (cleanReason.Length > PtcSubstituteAssignment.ReasonMaxLength) return ServiceResult.Fail("Reason", $"Use {PtcSubstituteAssignment.ReasonMaxLength} characters or fewer.");
        if (originalTeacherId == substituteTeacherId) return ServiceResult.Fail("SubstituteTeacherId", "A teacher cannot substitute for themselves.");
        if (end <= start) return ServiceResult.Fail("EndTime", "The end time must be after the start time.");

        var staff = await db.StaffMembers.AsNoTracking().Where(s => s.Id == originalTeacherId || s.Id == substituteTeacherId)
            .Select(s => new { s.Id, s.ArchivedAt }).ToListAsync(ct);
        if (staff.All(s => s.Id != originalTeacherId)) return ServiceResult.Fail("OriginalTeacherId", "Choose an existing staff member as the original teacher.");
        if (staff.FirstOrDefault(s => s.Id == substituteTeacherId) is not { } sub) return ServiceResult.Fail("SubstituteTeacherId", "Choose an existing staff member as the substitute.");
        if (sub.ArchivedAt is not null) return ServiceResult.Fail("SubstituteTeacherId", "The substitute is not current staff.");

        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var schedule = await db.PtcClassSchedules.AsNoTracking().Where(s => s.Id == scheduleId)
            .Select(s => new { s.PtcEventId, s.EventDate, s.StartTime, s.EndTime, s.Status, EventStatus = s.PtcEvent.Status }).FirstOrDefaultAsync(ct);
        if (schedule is null) return ServiceResult.Fail("", "This class schedule no longer exists.");
        if (schedule.Status != PtcScheduleStatus.Scheduled || schedule.EventStatus is PtcEventStatus.Cancelled or PtcEventStatus.Completed)
            return ServiceResult.Fail("", "This class schedule is closed.");
        if (start < schedule.StartTime || end > schedule.EndTime)
            return ServiceResult.Fail("StartTime", $"Substitute cover must be within the class's PTC time ({ScheduleRules.Times(schedule.StartTime, schedule.EndTime)}).");

        // The same original teacher is not covered twice at the same time on that date.
        var doubled = await db.PtcSubstituteAssignments.AnyAsync(a => a.Status == PtcSubstituteStatus.Active && a.OriginalTeacherStaffMemberId == originalTeacherId
            && a.ClassSchedule.EventDate == schedule.EventDate && a.StartTime < end && a.EndTime > start, ct);
        if (doubled) return ServiceResult.Fail("OriginalTeacherId", "This teacher already has a substitute at that time.");

        // The substitute must be free: no interview, other cover, uncovered lesson, duty or scheduled period then.
        var blocking = (await PtcSchedulingRules.TeacherConflictsAsync(db, substituteTeacherId, schedule.EventDate, start, end, ct)).FirstOrDefault(c => c.Blocks);
        if (blocking is not null) return ServiceResult.Fail("SubstituteTeacherId", blocking.Message);

        var assignment = new PtcSubstituteAssignment
        {
            PtcClassScheduleId = scheduleId,
            OriginalTeacherStaffMemberId = originalTeacherId,
            SubstituteTeacherStaffMemberId = substituteTeacherId,
            StartTime = start,
            EndTime = end,
            Reason = cleanReason.Length == 0 ? null : cleanReason,
            AssignedBy = currentUser.AccountName(),
        };
        db.PtcSubstituteAssignments.Add(assignment);
        PtcCoverageService.Audit(db, assignment, schedule.PtcEventId, PtcSubstituteChangeKind.Assigned, null, substituteTeacherId,
            $"Cover {Day(schedule.EventDate)} {ScheduleRules.Times(start, end)}", assignment.Reason, null, assignment.AssignedBy);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(assignment.Id);
    }

    public async Task<ServiceResult> CancelSubstituteAsync(int assignmentId, CancellationToken ct = default)
    {
        access.Ensure();
        await using var tx = await BeginAsync(ct);
        await LockAsync(db, ct);
        var assignment = await db.PtcSubstituteAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment is null) return ServiceResult.Fail("", "This substitute assignment no longer exists.");
        if (assignment.Status == PtcSubstituteStatus.Cancelled) return ServiceResult.Ok(assignment.Id);
        assignment.Status = PtcSubstituteStatus.Cancelled;
        assignment.CancelledAt = DateTime.UtcNow;
        assignment.CancelledBy = currentUser.AccountName();
        var schedule = await db.PtcClassSchedules.AsNoTracking().Where(s => s.Id == assignment.PtcClassScheduleId).Select(s => new { s.PtcEventId, s.EventDate }).FirstAsync(ct);
        PtcCoverageService.Audit(db, assignment, schedule.PtcEventId, PtcSubstituteChangeKind.Removed, assignment.SubstituteTeacherStaffMemberId, null,
            $"Cover {Day(schedule.EventDate)} {ScheduleRules.Times(assignment.StartTime, assignment.EndTime)}", null, assignment.Notes, assignment.CancelledBy);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(assignment.Id);
    }
}
