using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

// ============================================================ readiness

/// <summary>
/// How ready something is. Not configured: nothing set up yet. Blocked: a required part is missing or invalid (an event
/// cannot be published). Incomplete: nothing required is missing, but some parents will not be able to book (e.g. no parent
/// number on record) — publishing needs the administrator to acknowledge it. Ready: nothing missing.
/// </summary>
public enum PtcReadiness
{
    NotConfigured = 0,
    Ready = 1,
    Incomplete = 2,
    Blocked = 3,
}

public enum PtcIssueArea
{
    Event = 1,
    Classes = 2,
    Rounds = 3,
    Teachers = 4,
    ParentContacts = 5,

    /// <summary>Class coverage: teachers' timetable lessons during the Rounds and their substitutes (Phase 6).</summary>
    Coverage = 6,
}

/// <summary>One readiness finding. A blocking issue prevents publishing; a warning must be acknowledged to publish.</summary>
public sealed record PtcIssue(PtcIssueArea Area, bool Blocking, string Message, string? ClassName = null, string? Href = null);

/// <summary>The readiness of one event (or one class in it), from its issues.</summary>
public sealed class PtcReadinessResult(IReadOnlyList<PtcIssue> issues, bool configured = true)
{
    public IReadOnlyList<PtcIssue> Issues { get; } = issues;
    public bool Configured { get; } = configured;
    public IReadOnlyList<PtcIssue> Blockers => Issues.Where(i => i.Blocking).ToList();
    public IReadOnlyList<PtcIssue> Warnings => Issues.Where(i => !i.Blocking).ToList();

    public PtcReadiness Status => !Configured ? PtcReadiness.NotConfigured
        : Issues.Any(i => i.Blocking) ? PtcReadiness.Blocked
        : Issues.Count > 0 ? PtcReadiness.Incomplete
        : PtcReadiness.Ready;

    /// <summary>Publishing is allowed (warnings, if any, must still be acknowledged).</summary>
    public bool CanPublish => Configured && !Issues.Any(i => i.Blocking);
}

// ============================================================ read models

public sealed record PtcRoundRow(int Id, int Number, TimeOnly Start, TimeOnly End, int Capacity, int MeetingMinutes, int TransitionMinutes,
    PtcRoundStatus Status, int Bookings)
{
    public int DurationMinutes => PtcRules.Minutes(Start, End);
    public int Remaining => Math.Max(0, Capacity - Bookings);
}

public sealed record PtcScheduleRow(int Id, DateOnly Date, TimeOnly Start, TimeOnly End, string? Notes, IReadOnlyList<PtcRoundRow> Rounds, int SubstituteAssignments)
{
    public int Capacity => Rounds.Where(r => r.Status != PtcRoundStatus.Cancelled).Sum(r => r.Capacity);
    public int Bookings => Rounds.Sum(r => r.Bookings);
}

/// <summary>A KG Homeroom Teacher / Co-Teacher assignment, shown for context only (never used as a PTC subject teacher).</summary>
public sealed record PtcTeamMember(string Name, ClassTeachingRole Role);

/// <summary>Students of a class and their parent contact numbers (usable = can identify the parent for booking).</summary>
public sealed record PtcContactCounts(int Students, int Usable, int Missing, int Unusable)
{
    public int WithoutUsable => Missing + Unusable;
}

/// <summary>One class in an event: assigned (with its schedule) or not, its subjects and teachers, and its readiness.</summary>
public sealed record PtcClassRow(int ClassId, string ClassName, bool IsKg, bool ClassActive, PtcScheduleRow? Schedule,
    IReadOnlyList<PtcSubjectOption> Subjects, IReadOnlyList<PtcTeamMember> KgTeam, PtcContactCounts Contacts, PtcReadinessResult Readiness)
{
    public bool Assigned => Schedule is not null;
}

public sealed class PtcEventDetail
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required int YearId { get; init; }
    public required string YearName { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required PtcEventStatus Status { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Every active class (and any inactive class still assigned), in class order.</summary>
    public required IReadOnlyList<PtcClassRow> Classes { get; init; }
    public required PtcReadinessResult Readiness { get; init; }

    /// <summary>Class coverage computed now (timetable lessons during the Rounds and their substitutes).</summary>
    public required PtcCoverageReport Coverage { get; init; }

    public IReadOnlyList<PtcClassRow> Assigned => Classes.Where(c => c.Assigned).ToList();
    public IReadOnlyList<PtcClassRow> Unassigned => Classes.Where(c => !c.Assigned && c.ClassActive).ToList();
    public int Days => Assigned.Select(c => c.Schedule!.Date).Distinct().Count();
    public int Rounds => Assigned.Sum(c => c.Schedule!.Rounds.Count(r => r.Status != PtcRoundStatus.Cancelled));
    public int Capacity => Assigned.Sum(c => c.Schedule!.Capacity);
    public int Bookings => Assigned.Sum(c => c.Schedule!.Bookings);
    public bool IsDraft => Status == PtcEventStatus.Draft;
    public bool IsClosed => Status is PtcEventStatus.Completed or PtcEventStatus.Cancelled;

    /// <summary>The code of the public booking link (/ptc-booking/{token}).</summary>
    public string? PublicToken { get; init; }

    /// <summary>Rounds that can take a booking now (see <see cref="PtcAvailability"/>): Published, future, open, with places left.</summary>
    public IReadOnlyList<PtcBookableRound> BookableRounds { get; init; } = [];
    public bool BookingsOpen => BookableRounds.Count > 0;

    /// <summary>The event's dates, first to last.</summary>
    public IReadOnlyList<DateOnly> Dates =>
        Enumerable.Range(0, EndDate.DayNumber - StartDate.DayNumber + 1).Select(StartDate.AddDays).ToList();
}

public sealed record PtcEventSummary(int Id, string Title, string YearName, DateOnly StartDate, DateOnly EndDate, PtcEventStatus Status,
    int Classes, int Days, int Rounds, int Capacity, int Bookings, bool BookingsOpen, PtcReadiness? Readiness, int CoverageAttention = 0);

/// <summary>A school-wide readiness indicator on the dashboard.</summary>
public sealed record PtcIndicator(string Title, PtcReadiness Status, string Value, string Detail, string? Href = null);

public sealed class PtcDashboard
{
    public required IReadOnlyList<PtcEventSummary> Events { get; init; }
    public required IReadOnlyList<PtcIndicator> Indicators { get; init; }
    public required PtcRoundDefaults Defaults { get; init; }

    public int Count(PtcEventStatus s) => Events.Count(e => e.Status == s);
    public int BookingsOpen => Events.Count(e => e.BookingsOpen);

    /// <summary>Scheduling totals over the active events (Draft and Published).</summary>
    public required int Classes { get; init; }
    public required int Days { get; init; }
    public required int Rounds { get; init; }
    public required int Capacity { get; init; }
    public required int Bookings { get; init; }
    public int Remaining => Math.Max(0, Capacity - Bookings);
}

public sealed record PtcContactStudent(int Id, string Name, int ClassId, string ClassName, string? Stored, string? Normalized);

public sealed record PtcContactClass(int ClassId, string ClassName, PtcContactCounts Counts);

public sealed class PtcContactReport
{
    public required IReadOnlyList<PtcContactClass> Classes { get; init; }
    public required IReadOnlyList<PtcContactStudent> Missing { get; init; }
    public required IReadOnlyList<PtcContactStudent> Unusable { get; init; }
    public required IReadOnlyList<PtcContactStudent> Recorded { get; init; }
    public int Students => Classes.Sum(c => c.Counts.Students);
}

/// <summary>One confirmed booking as the Admin sees it (never shown publicly).</summary>
public sealed record PtcBookingRow(string Reference, string StudentName, int Slot, TimeOnly Start, TimeOnly End,
    IReadOnlyList<(string Subject, string Teacher)> Subjects, DateTime BookedAt, string? BookedBy);

public sealed record PtcRoundBookings(int Number, TimeOnly Start, TimeOnly End, int Capacity, PtcRoundStatus Status, bool Bookable, IReadOnlyList<PtcBookingRow> Bookings)
{
    public int Remaining => Math.Max(0, Capacity - Bookings.Count);
}

public sealed record PtcScheduleBookings(int ScheduleId, string ClassName, DateOnly Date, IReadOnlyList<PtcRoundBookings> Rounds)
{
    public int Bookings => Rounds.Sum(r => r.Bookings.Count);
    public int Capacity => Rounds.Sum(r => r.Capacity);
}


// ============================================================ service

public interface IPtcAdminService
{
    Task<PtcDashboard> DashboardAsync(CancellationToken ct = default);

    /// <summary>The event with every class, schedule, Round, subject teacher, KG team, parent contact count and its readiness.</summary>
    Task<PtcEventDetail?> EventAsync(int id, CancellationToken ct = default);

    /// <summary>The event id of a class schedule.</summary>
    Task<int?> EventOfScheduleAsync(int scheduleId, CancellationToken ct = default);

    /// <summary>Students' parent contact numbers in active classes (Super Admin only: numbers are personal data).</summary>
    Task<PtcContactReport> ParentContactsAsync(CancellationToken ct = default);

    /// <summary>Academic years (existing table) with the current one flagged.</summary>
    Task<IReadOnlyList<AcademicYearOption>> YearsAsync(CancellationToken ct = default);

    /// <summary>The event's confirmed bookings by class schedule and Round (Super Admin only; never public).</summary>
    Task<IReadOnlyList<PtcScheduleBookings>> BookingsAsync(int eventId, CancellationToken ct = default);

}

/// <summary>
/// The read side of PTC administration. Subjects and teachers come only from the timetable (<see cref="PtcSchedulingRules"/>);
/// KG Homeroom / Co-Teacher assignments are shown as context and never turned into subject teachers; parent contact
/// readiness reads <see cref="Student.ParentContactNumber"/> and never changes it. Nothing here writes.
/// </summary>
public sealed class PtcAdminService(SchoolDbContext db, IPtcAccess access, ISettingsService settings, IClassTeamService years, ISchoolTimeService? schoolTime = null)
    : IPtcAdminService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    public Task<IReadOnlyList<AcademicYearOption>> YearsAsync(CancellationToken ct = default) => years.YearsAsync(ct);

    public Task<int?> EventOfScheduleAsync(int scheduleId, CancellationToken ct = default) =>
        db.PtcClassSchedules.AsNoTracking().Where(s => s.Id == scheduleId).Select(s => (int?)s.PtcEventId).FirstOrDefaultAsync(ct);

    // ------------------------------------------------------------------ event

    public async Task<PtcEventDetail?> EventAsync(int id, CancellationToken ct = default)
    {
        access.Ensure();
        var ev = await db.PtcEvents.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new
            {
                e.Id, e.Title, e.Description, e.AcademicYearId, YearName = e.AcademicYear.Name, e.StartDate, e.EndDate, e.Status,
                e.CreatedBy, e.CreatedAt, e.UpdatedBy, e.UpdatedAt, e.PublicToken,
            })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return null;

        var schedules = await SchedulesAsync(db.PtcClassSchedules.Where(s => s.PtcEventId == id && s.Status != PtcScheduleStatus.Cancelled), ct);
        var assignedIds = schedules.Select(s => s.ClassId).ToHashSet();
        var classes = await db.Classes.AsNoTracking().Where(c => c.IsActive || assignedIds.Contains(c.Id))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name, c.IsActive }).ToListAsync(ct);
        var classIds = classes.Select(c => c.Id).ToList();

        // Subjects and teachers come from the timetable for every class (KG included): an assigned class's PTC week, or the
        // event's first week for a class not assigned yet.
        var subjects = await PtcSchedulingRules.ClassSubjectsAsync(db, classes.Select(c =>
        {
            var sc = schedules.FirstOrDefault(s => s.ClassId == c.Id)?.Row;
            return (ClassId: c.Id, Date: sc?.Date ?? ev.StartDate, ScheduleId: sc?.Id);
        }).ToList(), ct);
        var contacts = await ContactCountsAsync(classIds, ct);
        var kgTeams = await KgTeamsAsync(classes.Where(c => ClassStages.IsKgStage(c.Name)).Select(c => c.Id).ToList(), ev.AcademicYearId, ct);
        var today = _time.Today;
        // Former manual KG teacher selections are kept as history but never used: reported so nothing changes silently.
        var legacy = (await db.PtcClassTeachers.AsNoTracking()
                .Where(t => t.Status == PtcClassTeacherStatus.Active && t.ClassSchedule.PtcEventId == id && t.ClassSchedule.Status != PtcScheduleStatus.Cancelled)
                .Select(t => new { t.PtcClassScheduleId, t.StaffMember.FullName, t.SubjectName }).ToListAsync(ct))
            .GroupBy(t => t.PtcClassScheduleId).ToDictionary(g => g.Key, g => g.Select(t => $"{t.FullName} ({t.SubjectName})").ToList());

        var rows = new List<PtcClassRow>();
        foreach (var c in classes)
        {
            var schedule = schedules.FirstOrDefault(s => s.ClassId == c.Id)?.Row;
            var isKg = ClassStages.IsKgStage(c.Name);
            var issues = schedule is null ? [] : ClassIssues(c.Name, c.IsActive, schedule, subjects[c.Id], contacts[c.Id], ev.StartDate, ev.EndDate,
                ev.Status == PtcEventStatus.Draft ? today : null, id);
            if (schedule is not null && legacy.TryGetValue(schedule.Id, out var old))
                issues.Add(new PtcIssue(PtcIssueArea.Teachers, false,
                    $"{c.Name} has {Ui.Plural(old.Count, "earlier manual KG teacher selection", "earlier manual KG teacher selections")} ({string.Join(", ", old)}): kept as history but no longer used. Its PTC teachers come from its timetable like every class; check that these teachers teach {c.Name} in the timetable.",
                    c.Name, $"/ptc/schedules/{schedule.Id}"));
            rows.Add(new PtcClassRow(c.Id, c.Name, isKg, c.IsActive, schedule, subjects[c.Id], kgTeams.GetValueOrDefault(c.Id, []), contacts[c.Id],
                new PtcReadinessResult(issues, schedule is not null)));
        }

        var eventIssues = new List<PtcIssue>();
        if (rows.All(r => !r.Assigned))
            eventIssues.Add(new PtcIssue(PtcIssueArea.Classes, true, "No classes are assigned to PTC dates yet.", Href: $"/ptc/events/{id}/classes"));
        eventIssues.AddRange(rows.SelectMany(r => r.Readiness.Issues));
        eventIssues.AddRange(TeacherClashes(rows.Where(r => r.Assigned).ToList()));
        // Class coverage (Phase 6): every participating teacher's lessons during the Rounds, with their substitutes, checked now.
        var coverage = await PtcCoverageEngine.ComputeAsync(db, id, rows.Where(r => r.Assigned).Select(r => new PtcCoverageInput(r.Schedule!.Id, r.ClassName,
            r.Schedule.Date, r.Schedule.Rounds, r.Subjects.Select(s => (s.TeacherId, s.TeacherName)).Distinct().ToList())).ToList(), ct);
        eventIssues.AddRange(PtcCoverageEngine.Issues(coverage));

        return new PtcEventDetail
        {
            Id = ev.Id, Title = ev.Title, Description = ev.Description, YearId = ev.AcademicYearId, YearName = ev.YearName,
            StartDate = ev.StartDate, EndDate = ev.EndDate, Status = ev.Status,
            CreatedBy = ev.CreatedBy, CreatedAt = ev.CreatedAt, UpdatedBy = ev.UpdatedBy, UpdatedAt = ev.UpdatedAt,
            Classes = rows,
            Readiness = new PtcReadinessResult(eventIssues, rows.Any(r => r.Assigned)),
            Coverage = coverage,
            PublicToken = ev.PublicToken,
            BookableRounds = await PtcAvailability.BookableRoundsAsync(db, _time.Now, ct, eventId: id),
        };
    }

    private sealed record ScheduleData(int ClassId, int EventId, PtcScheduleRow Row);

    private async Task<List<ScheduleData>> SchedulesAsync(IQueryable<PtcClassSchedule> query, CancellationToken ct)
    {
        var raw = await query.AsNoTracking()
            .Select(s => new
            {
                s.Id, s.PtcEventId, s.SchoolClassId, s.EventDate, s.StartTime, s.EndTime, s.Notes,
                Subs = s.SubstituteAssignments.Count(a => a.Status == PtcSubstituteStatus.Active),
                Rounds = s.Rounds.OrderBy(r => r.StartTime).Select(r => new
                {
                    r.Id, r.RoundNumber, r.StartTime, r.EndTime, r.Capacity, r.MeetingDurationMinutes, r.TransitionDurationMinutes, r.Status,
                    Bookings = r.Bookings.Count(b => b.Status != PtcBookingStatus.Cancelled),
                }).ToList(),
            })
            .ToListAsync(ct);
        return raw.Select(s => new ScheduleData(s.SchoolClassId, s.PtcEventId, new PtcScheduleRow(s.Id, s.EventDate, s.StartTime, s.EndTime, s.Notes,
            s.Rounds.Select(r => new PtcRoundRow(r.Id, r.RoundNumber, r.StartTime, r.EndTime, r.Capacity, r.MeetingDurationMinutes,
                r.TransitionDurationMinutes, r.Status, r.Bookings)).ToList(), s.Subs))).ToList();
    }

    /// <summary>What is missing or invalid for one assigned class (<paramref name="today"/> set: dates before it are blocking).</summary>
    private static List<PtcIssue> ClassIssues(string name, bool active, PtcScheduleRow s, IReadOnlyList<PtcSubjectOption> subjects,
        PtcContactCounts contacts, DateOnly from, DateOnly to, DateOnly? today, int eventId)
    {
        var href = $"/ptc/schedules/{s.Id}";
        var issues = new List<PtcIssue>();
        void Block(PtcIssueArea area, string message) => issues.Add(new PtcIssue(area, true, message, name, href));
        void Warn(PtcIssueArea area, string message) => issues.Add(new PtcIssue(area, false, message, name, href));

        if (!active) Block(PtcIssueArea.Classes, $"{name} is no longer an active class.");
        if (s.Date < from || s.Date > to) Block(PtcIssueArea.Classes, $"{name}'s date ({Day(s.Date)}) is outside the event dates ({Day(from)} – {Day(to)}).");
        if (today is { } t && s.Date < t) Block(PtcIssueArea.Classes, $"{name}'s date ({Day(s.Date)}) has already passed.");
        if (s.End <= s.Start) Block(PtcIssueArea.Rounds, $"{name}'s daily end time is not after its start time.");

        var rounds = s.Rounds.Where(r => r.Status != PtcRoundStatus.Cancelled).ToList();
        if (rounds.Count == 0) Block(PtcIssueArea.Rounds, $"{name} has no Rounds configured.");
        foreach (var r in rounds)
        {
            if (r.Start < s.Start || r.End > s.End)
                Block(PtcIssueArea.Rounds, $"{name} Round {r.Number} ({ScheduleRules.Times(r.Start, r.End)}) is outside the daily time ({ScheduleRules.Times(s.Start, s.End)}).");
            if (!PtcRules.FitsMeetings(r.DurationMinutes, r.Capacity, r.MeetingMinutes))
                Block(PtcIssueArea.Rounds, $"{name} Round {r.Number} is {r.DurationMinutes} minutes but needs {r.Capacity * r.MeetingMinutes} for {r.Capacity} meetings of {r.MeetingMinutes} minutes.");
            var overlap = rounds.FirstOrDefault(o => o.Number > r.Number && PtcRules.Overlaps(r.Start, r.End, o.Start, o.End));
            if (overlap is not null) Block(PtcIssueArea.Rounds, $"{name} Rounds {r.Number} and {overlap.Number} overlap.");
        }
        if (rounds.Count > 0 && rounds.All(r => r.Status != PtcRoundStatus.Open))
            Block(PtcIssueArea.Rounds, $"{name} has no Round open for booking.");

        if (subjects.Count == 0)
            Block(PtcIssueArea.Teachers, $"No timetable assignments found for {name}: its subjects and their actual teachers cannot be identified.");

        if (contacts.Students == 0) Warn(PtcIssueArea.ParentContacts, $"{name} has no students.");
        else if (contacts.WithoutUsable > 0)
            Warn(PtcIssueArea.ParentContacts,
                $"{contacts.WithoutUsable} of {contacts.Students} students in {name} have no usable parent contact number and cannot book until one is recorded.");
        return issues;
    }

    /// <summary>
    /// Teacher clashes: a teacher whose classes' Rounds overlap on the same day (a warning: some slots may be refused). A
    /// teacher's own timetable lesson during a Round is class coverage (<see cref="PtcCoverageEngine"/>).
    /// </summary>
    private static List<PtcIssue> TeacherClashes(List<PtcClassRow> rows)
    {
        var windows = rows
            .SelectMany(r => r.Subjects.Select(s => (s.TeacherId, s.TeacherName)).Distinct()
                .SelectMany(t => r.Schedule!.Rounds.Where(x => x.Status != PtcRoundStatus.Cancelled)
                    .Select(x => (t.TeacherId, t.TeacherName, r.ClassName, r.Schedule.Id, r.Schedule.Date, Round: x))))
            .ToList();
        var issues = new List<PtcIssue>();
        if (windows.Count == 0) return issues;

        foreach (var g in windows.GroupBy(w => (w.TeacherId, w.Date)))
        {
            var list = g.ToList();
            var seen = new HashSet<(string, string)>();
            foreach (var a in list)
                foreach (var b in list.Where(b => string.CompareOrdinal(b.ClassName, a.ClassName) > 0 && PtcRules.Overlaps(a.Round.Start, a.Round.End, b.Round.Start, b.Round.End)))
                    if (seen.Add((a.ClassName, b.ClassName)))
                        issues.Add(new PtcIssue(PtcIssueArea.Teachers, false,
                            $"{a.TeacherName} teaches {a.ClassName} and {b.ClassName}, whose Rounds overlap on {Day(a.Date)}: a parent of one class may be refused a slot with {a.TeacherName}.",
                            a.ClassName, $"/ptc/schedules/{a.Id}"));
        }
        return issues;
    }

    private async Task<Dictionary<int, PtcContactCounts>> ContactCountsAsync(IReadOnlyCollection<int> classIds, CancellationToken ct)
    {
        var students = await db.Students.AsNoTracking().Where(s => classIds.Contains(s.SchoolClassId))
            .Select(s => new { s.SchoolClassId, s.ParentContactNumber }).ToListAsync(ct);
        return classIds.ToDictionary(id => id, id =>
        {
            var mine = students.Where(s => s.SchoolClassId == id).ToList();
            var missing = mine.Count(s => string.IsNullOrWhiteSpace(s.ParentContactNumber));
            var usable = mine.Count(s => PtcRules.ParentKey(s.ParentContactNumber) is not null);
            return new PtcContactCounts(mine.Count, usable, missing, mine.Count - missing - usable);
        });
    }

    private async Task<Dictionary<int, IReadOnlyList<PtcTeamMember>>> KgTeamsAsync(IReadOnlyCollection<int> classIds, int yearId, CancellationToken ct)
    {
        if (classIds.Count == 0) return [];
        var team = await db.ClassTeachingAssignments.AsNoTracking()
            .Where(a => a.IsActive && a.AcademicYearId == yearId && classIds.Contains(a.SchoolClassId))
            .OrderBy(a => a.Role).Select(a => new { a.SchoolClassId, a.StaffMember.FullName, a.Role }).ToListAsync(ct);
        return team.GroupBy(a => a.SchoolClassId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PtcTeamMember>)g.Select(a => new PtcTeamMember(a.FullName, a.Role)).ToList());
    }

    // ------------------------------------------------------------------ dashboard

    public async Task<PtcDashboard> DashboardAsync(CancellationToken ct = default)
    {
        access.Ensure();
        var today = _time.Today;
        var events = await db.PtcEvents.AsNoTracking().OrderByDescending(e => e.StartDate).ThenByDescending(e => e.Id)
            .Select(e => new { e.Id, e.Title, YearName = e.AcademicYear.Name, e.StartDate, e.EndDate, e.Status }).ToListAsync(ct);
        var schedules = await SchedulesAsync(db.PtcClassSchedules.Where(s => s.Status != PtcScheduleStatus.Cancelled), ct);

        var openEvents = await PtcAvailability.OpenEventsAsync(db, _time.Now, ct);
        var summaries = new List<PtcEventSummary>();
        foreach (var e in events)
        {
            var mine = schedules.Where(s => s.EventId == e.Id).Select(s => s.Row).ToList();
            var rounds = mine.SelectMany(s => s.Rounds).Where(r => r.Status != PtcRoundStatus.Cancelled).ToList();
            var open = openEvents.Contains(e.Id);
            // Readiness is assessed for events still being prepared (an event's assessment reads only its own classes); a
            // Published event is checked for class coverage that broke after publishing (needs the Super Admin's attention).
            var detail = e.Status is PtcEventStatus.Draft or PtcEventStatus.Published ? await EventAsync(e.Id, ct) : null;
            var readiness = e.Status == PtcEventStatus.Draft ? detail?.Readiness.Status : null;
            var attention = e.Status == PtcEventStatus.Published && detail is not null && detail.Coverage.Unresolved > 0 ? detail.Coverage.Unresolved : 0;
            summaries.Add(new PtcEventSummary(e.Id, e.Title, e.YearName, e.StartDate, e.EndDate, e.Status, mine.Count, mine.Select(s => s.Date).Distinct().Count(),
                rounds.Count, rounds.Sum(r => r.Capacity), mine.Sum(s => s.Bookings), open, readiness, attention));
        }

        var activeEventIds = events.Where(e => e.Status is PtcEventStatus.Draft or PtcEventStatus.Published).Select(e => e.Id).ToHashSet();
        var active = schedules.Where(s => activeEventIds.Contains(s.EventId)).ToList();
        var activeRounds = active.SelectMany(s => s.Row.Rounds).Where(r => r.Status != PtcRoundStatus.Cancelled).ToList();

        return new PtcDashboard
        {
            Events = summaries,
            Indicators = await IndicatorsAsync(active, activeEventIds.Count, today, ct),
            Defaults = await settings.GetPtcDefaultsAsync(ct),
            Classes = active.Select(s => s.ClassId).Distinct().Count(),
            Days = active.Select(s => s.Row.Date).Distinct().Count(),
            Rounds = activeRounds.Count,
            Capacity = activeRounds.Sum(r => r.Capacity),
            Bookings = activeRounds.Sum(r => r.Bookings),
        };
    }

    private async Task<List<PtcIndicator>> IndicatorsAsync(List<ScheduleData> active, int activeEvents, DateOnly today, CancellationToken ct)
    {
        var classes = await db.Classes.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var ids = classes.Select(c => c.Id).ToList();
        var total = classes.Count;
        var list = new List<PtcIndicator>();

        // 1. Classes assigned to PTC dates (in Draft / Published events).
        var assigned = active.Select(s => s.ClassId).Where(ids.Contains).Distinct().Count();
        list.Add(new PtcIndicator("Classes assigned to PTC dates",
            activeEvents == 0 || assigned == 0 ? PtcReadiness.NotConfigured : assigned == total ? PtcReadiness.Ready : PtcReadiness.Incomplete,
            $"{assigned} of {total}",
            activeEvents == 0 ? "No Draft or Published PTC event yet." : assigned == total ? "Every active class has a PTC date." : $"{total - assigned} active classes have no PTC date in an active event."));

        // 2 / 3. Timetable subjects and their actual teachers (the classes' current lessons, this school week): every active
        //        class, KG included, follows the same rule.
        var subjects = await PtcSchedulingRules.ClassSubjectsAsync(db, classes.Select(c => (c.Id, today, (int?)null)).ToList(), ct);
        var withSubjects = subjects.Count(kv => kv.Value.Count > 0);
        list.Add(new PtcIndicator("Timetable subjects & teachers",
            withSubjects == 0 ? PtcReadiness.Blocked : withSubjects == total ? PtcReadiness.Ready : PtcReadiness.Incomplete,
            $"{withSubjects} of {Ui.Plural(total, "class", "classes")} ready",
            withSubjects == 0 ? "No class has timetable lessons with a current teacher: PTC subjects and teachers cannot be identified."
                : "Classes whose subjects and actual teachers come from their timetable lessons (every class, KG included).", "/timetables"));

        var week = SchoolWeek.StartOf(today);
        var noTeacher = await db.TimetableAssignments.AsNoTracking().ActiveIn(week).CountAsync(t => ids.Contains(t.SchoolClassId) && t.Teacher.ArchivedAt != null, ct);
        var missingClasses = classes.Where(c => subjects[c.Id].Count == 0).Select(c => c.Name).ToList();
        list.Add(new PtcIndicator("Missing timetable assignments",
            missingClasses.Count == 0 && noTeacher == 0 ? PtcReadiness.Ready : PtcReadiness.Blocked,
            missingClasses.Count == 0 ? "None" : $"{Ui.Plural(missingClasses.Count, "class", "classes")} missing",
            (missingClasses.Count == 0 ? "Every active class has timetable lessons with a current teacher." : "No timetable assignments: " + Names(missingClasses) + ".")
            + (noTeacher > 0 ? $" {Ui.Plural(noTeacher, "lesson's teacher is", "lessons' teachers are")} no longer current staff." : "")));

        // 4. Parent contact numbers.
        var contacts = await ContactCountsAsync(ids, ct);
        var students = contacts.Values.Sum(c => c.Students);
        var without = contacts.Values.Sum(c => c.WithoutUsable);
        list.Add(new PtcIndicator("Parent contact numbers",
            students == 0 ? PtcReadiness.NotConfigured : without == 0 ? PtcReadiness.Ready : without == students ? PtcReadiness.Blocked : PtcReadiness.Incomplete,
            $"{students - without} of {students}",
            without == 0 ? "Every student has a usable parent number." : $"{without} students have no usable parent number: their parents cannot book.", "/ptc/parent-contacts"));

        // 5. Classes with incomplete academic information: classes with no students.
        var empty = classes.Where(c => contacts[c.Id].Students == 0).Select(c => c.Name).ToList();
        list.Add(new PtcIndicator("Classes with incomplete academic information",
            empty.Count == 0 ? PtcReadiness.Ready : PtcReadiness.Incomplete,
            empty.Count == 0 ? "None" : Ui.Plural(empty.Count, "class", "classes"),
            empty.Count == 0 ? "Every active class has students." : $"No students: {Names(empty)}."));

        // 6. Daily schedules whose Rounds are missing or invalid.
        var badRounds = active.Count(s => !s.Row.Rounds.Any(r => r.Status == PtcRoundStatus.Open)
            || s.Row.Rounds.Any(r => r.Start < s.Row.Start || r.End > s.Row.End || !PtcRules.FitsMeetings(r.DurationMinutes, r.Capacity, r.MeetingMinutes)));
        list.Add(new PtcIndicator("Daily schedules with incomplete Rounds",
            active.Count == 0 ? PtcReadiness.NotConfigured : badRounds == 0 ? PtcReadiness.Ready : PtcReadiness.Incomplete,
            active.Count == 0 ? "No schedules" : $"{badRounds} of {active.Count}",
            active.Count == 0 ? "No class has a PTC date yet." : badRounds == 0 ? "Every class schedule has valid open Rounds." : $"{badRounds} class schedules have no open Round or an invalid one."));
        return list;
    }

    private static string Names(IReadOnlyList<string> names) => names.Count <= 40 ? string.Join(", ", names) : string.Join(", ", names.Take(40)) + $" and {names.Count - 40} more";

    // ------------------------------------------------------------------ bookings (Admin visibility)

    public async Task<IReadOnlyList<PtcScheduleBookings>> BookingsAsync(int eventId, CancellationToken ct = default)
    {
        access.Ensure();
        var now = _time.Now;
        var schedules = await db.PtcClassSchedules.AsNoTracking()
            .Where(s => s.PtcEventId == eventId && s.Status != PtcScheduleStatus.Cancelled)
            .OrderBy(s => s.EventDate).ThenBy(s => s.SchoolClass.SortOrder)
            .Select(s => new
            {
                s.Id, s.EventDate, ClassName = s.SchoolClass.Name, s.Status, EventStatus = s.PtcEvent.Status,
                Rounds = s.Rounds.Where(r => r.Status != PtcRoundStatus.Cancelled).OrderBy(r => r.StartTime).Select(r => new
                {
                    r.RoundNumber, r.StartTime, r.EndTime, r.Capacity, r.Status,
                    Bookings = r.Bookings.Where(b => b.Status != PtcBookingStatus.Cancelled).OrderBy(b => b.SlotNumber).Select(b => new
                    {
                        b.BookingReference, StudentName = b.Student.FullName, b.SlotNumber, b.BookedAt, b.BookedBy,
                        Start = b.Interviews.Where(i => i.Status != PtcInterviewStatus.Cancelled).Select(i => (TimeOnly?)i.ScheduledStart).Min(),
                        End = b.Interviews.Where(i => i.Status != PtcInterviewStatus.Cancelled).Select(i => (TimeOnly?)i.ScheduledEnd).Max(),
                        Subjects = b.Subjects.Where(x => x.Status == PtcBookingSubjectStatus.Active).OrderBy(x => x.SubjectName)
                            .Select(x => new { x.SubjectName, Teacher = x.AssignedTeacher.FullName }).ToList(),
                    }).ToList(),
                }).ToList(),
            })
            .ToListAsync(ct);
        return schedules.Select(s => new PtcScheduleBookings(s.Id, s.ClassName, s.EventDate, s.Rounds.Select(r =>
        {
            var rows = r.Bookings.Select(b => new PtcBookingRow(b.BookingReference, b.StudentName, b.SlotNumber, b.Start ?? r.StartTime, b.End ?? r.StartTime,
                b.Subjects.Select(x => (x.SubjectName, x.Teacher)).ToList(), b.BookedAt, b.BookedBy)).ToList();
            var bookable = s.EventStatus == PtcEventStatus.Published && s.Status == PtcScheduleStatus.Scheduled && r.Status == PtcRoundStatus.Open
                           && PtcAvailability.IsFuture(s.EventDate, r.StartTime, now) && rows.Count < r.Capacity;
            return new PtcRoundBookings(r.RoundNumber, r.StartTime, r.EndTime, r.Capacity, r.Status, bookable, rows);
        }).ToList())).ToList();
    }

    // ------------------------------------------------------------------ parent contacts

    public async Task<PtcContactReport> ParentContactsAsync(CancellationToken ct = default)
    {
        access.Ensure();
        var classes = await db.Classes.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var ids = classes.Select(c => c.Id).ToList();
        var students = await db.Students.AsNoTracking().Where(s => ids.Contains(s.SchoolClassId))
            .OrderBy(s => s.SchoolClass.SortOrder).ThenBy(s => s.FullName)
            .Select(s => new { s.Id, s.FullName, s.SchoolClassId, ClassName = s.SchoolClass.Name, s.ParentContactNumber }).ToListAsync(ct);
        var counts = await ContactCountsAsync(ids, ct);

        // Shown normalized (international, as used for WhatsApp) without changing the stored value.
        static string? Normal(string? phone) => WhatsApp.Number(phone) is { } n ? "+" + n : PtcRules.ParentKey(phone);
        var rows = students.Select(s => new PtcContactStudent(s.Id, s.FullName, s.SchoolClassId, s.ClassName, s.ParentContactNumber, Normal(s.ParentContactNumber))).ToList();
        return new PtcContactReport
        {
            Classes = classes.Select(c => new PtcContactClass(c.Id, c.Name, counts[c.Id])).ToList(),
            Missing = rows.Where(r => string.IsNullOrWhiteSpace(r.Stored)).ToList(),
            Unusable = rows.Where(r => !string.IsNullOrWhiteSpace(r.Stored) && PtcRules.ParentKey(r.Stored) is null).ToList(),
            Recorded = rows.Where(r => PtcRules.ParentKey(r.Stored) is not null).ToList(),
        };
    }

    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", C);
}

/// <summary>Display labels and badge tones for PTC statuses.</summary>
public static class PtcLabels
{
    public static string Label(PtcReadiness r) => r switch
    {
        PtcReadiness.Ready => "Ready",
        PtcReadiness.Incomplete => "Incomplete",
        PtcReadiness.Blocked => "Blocked",
        _ => "Not yet configured",
    };

    public static string Badge(PtcReadiness r) => r switch
    {
        PtcReadiness.Ready => "badge--success",
        PtcReadiness.Incomplete => "badge--warning",
        PtcReadiness.Blocked => "badge--error",
        _ => "badge--neutral",
    };

    public static string Icon(PtcReadiness r) => r switch
    {
        PtcReadiness.Ready => "check-circle",
        PtcReadiness.Incomplete => "alert-triangle",
        PtcReadiness.Blocked => "x-circle",
        _ => "info",
    };

    public static string Label(PtcEventStatus s) => s.ToString();

    public static string Badge(PtcEventStatus s) => s switch
    {
        PtcEventStatus.Published => "badge--success",
        PtcEventStatus.Completed => "badge--primary",
        PtcEventStatus.Cancelled => "badge--error",
        _ => "badge--neutral",
    };

    public static string Area(PtcIssueArea a) => a switch
    {
        PtcIssueArea.Event => "Event",
        PtcIssueArea.Classes => "Classes & dates",
        PtcIssueArea.Rounds => "Daily sessions & Rounds",
        PtcIssueArea.Teachers => "Subjects & teachers",
        PtcIssueArea.Coverage => "Class coverage",
        _ => "Parent contacts",
    };

    public static string Role(ClassTeachingRole r) => r == ClassTeachingRole.HomeroomTeacher ? "Homeroom Teacher" : "Co-Teacher";

    public static string Label(PtcAllocationState s) => s switch
    {
        PtcAllocationState.Available => "Available",
        PtcAllocationState.Scheduled => "Scheduled",
        PtcAllocationState.Conflict => "Conflict",
        PtcAllocationState.Unallocated => "Unallocated",
        _ => "Requires Admin action",
    };

    public static string Badge(PtcAllocationState s) => s switch
    {
        PtcAllocationState.Available => "badge--neutral",
        PtcAllocationState.Scheduled => "badge--success",
        PtcAllocationState.Conflict => "badge--error",
        _ => "badge--warning",
    };

    public static string Label(PtcConflictSeverity s) => s switch
    {
        PtcConflictSeverity.Hard => "Conflict",
        PtcConflictSeverity.AdminAction => "Requires Admin action",
        _ => "Availability restriction",
    };

    public static string Badge(PtcConflictSeverity s) => s switch
    {
        PtcConflictSeverity.Hard => "badge--error",
        PtcConflictSeverity.AdminAction => "badge--warning",
        _ => "badge--neutral",
    };
}
