using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

// ============================================================ read models

/// <summary>
/// The state of a slot, booking or interview. Available: a free parent slot. Scheduled: allocated and conflict-free.
/// Conflict: a teacher is busy at that time. Unallocated: a chosen subject has no interview. Requires Admin action: the
/// allocation is inconsistent (e.g. an interview not at the booking's slot time).
/// </summary>
public enum PtcAllocationState
{
    Available = 1,
    Scheduled = 2,
    Conflict = 3,
    Unallocated = 4,
    RequiresAdminAction = 5,
}

/// <summary>Hard: a rule is broken (overlap, uncovered lesson). Admin action: an allocation must be fixed. Restriction: some slots are unavailable.</summary>
public enum PtcConflictSeverity
{
    Hard = 1,
    AdminAction = 2,
    Restriction = 3,
}

public sealed record PtcInterviewAllocation(int? InterviewId, int TeacherId, string TeacherName, string Subject, TimeOnly? Start, TimeOnly? End,
    PtcAllocationState State, string? Problem);

public sealed record PtcBookingAllocation(int BookingId, string Reference, string StudentName, int Slot, TimeOnly Start, TimeOnly End,
    PtcAllocationState State, IReadOnlyList<PtcInterviewAllocation> Interviews, DateTime BookedAt);

public sealed record PtcSlotView(int Slot, TimeOnly Start, TimeOnly End, PtcAllocationState State, PtcBookingAllocation? Booking);

public sealed record PtcRoundView(int RoundId, int Number, TimeOnly Start, TimeOnly End, int Capacity, int MeetingMinutes, int TransitionMinutes,
    PtcRoundStatus Status, IReadOnlyList<PtcSlotView> Slots)
{
    public int Booked => Slots.Count(s => s.Booking is not null);
    public int Utilization => Capacity == 0 ? 0 : (int)Math.Round(100.0 * Booked / Capacity);
}

public sealed record PtcClassTeacherRef(int TeacherId, string Name, string Subject);

public sealed record PtcClassScheduleView(int ScheduleId, int ClassId, string ClassName, bool IsKg, DateOnly Date, TimeOnly Start, TimeOnly End,
    IReadOnlyList<PtcClassTeacherRef> Teachers, IReadOnlyList<PtcRoundView> Rounds);

/// <summary>A proposed (or applied) move of one booking within its Round; the booking keeps its Round, subjects, teachers and reference.</summary>
public sealed record PtcSlotMove(int BookingId, string Reference, string ClassName, int RoundId, int RoundNumber, DateOnly Date,
    int OldSlot, TimeOnly OldStart, TimeOnly OldEnd, int NewSlot, TimeOnly NewStart, TimeOnly NewEnd, IReadOnlyList<string> Teachers, string Why);

/// <summary>A conflict-free start time for a Round (suggested; applied only by an explicit Admin action).</summary>
public sealed record PtcRoundMoveSuggestion(int RoundId, int RoundNumber, string ClassName, TimeOnly OldStart, TimeOnly OldEnd, TimeOnly NewStart, TimeOnly NewEnd, int Bookings);

public sealed record PtcScheduleConflict(PtcConflictSeverity Severity, string Kind, DateOnly Date, int? TeacherId, string? TeacherName,
    IReadOnlyList<string> Classes, IReadOnlyList<string> Intervals, string Source, int? RoundId, string? Round, IReadOnlyList<string> Bookings,
    string Suggestion, PtcSlotMove? SlotMove = null, PtcRoundMoveSuggestion? RoundMove = null);

/// <summary>One line of a teacher's PTC day: an interview, a Round of one of their classes, or something that keeps them busy.</summary>
public sealed record PtcTeacherItem(DateOnly Date, TimeOnly Start, TimeOnly End, string Kind, string Description, string? ClassName, string? Subject,
    string? Reference, int? RoundNumber, PtcAllocationState State);

public sealed record PtcTeacherSummary(int TeacherId, string Name, IReadOnlyList<string> Classes, IReadOnlyList<DateOnly> Dates, int Interviews, int Conflicts)
{
    public bool MultiClass => Classes.Count > 1;
}

public sealed record PtcTeacherSchedule(int TeacherId, string Name, IReadOnlyList<string> Classes, IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<PtcTeacherItem> Items, IReadOnlyList<PtcScheduleConflict> Conflicts);

public sealed record PtcScheduleChangeView(DateTime At, string? By, PtcScheduleChangeKind Kind, string ClassName, int RoundNumber, string? Reference,
    DateOnly Date, int? OldSlot, int? NewSlot, TimeOnly OldStart, TimeOnly OldEnd, TimeOnly NewStart, TimeOnly NewEnd, string? Teachers, string Reason);

public sealed class PtcScheduleAnalysis
{
    public required int EventId { get; init; }
    public required string Title { get; init; }
    public required PtcEventStatus Status { get; init; }
    public required IReadOnlyList<PtcClassScheduleView> Classes { get; init; }
    public required IReadOnlyList<PtcScheduleConflict> Conflicts { get; init; }

    /// <summary>The safe moves that resolve allocation problems (reviewed and applied explicitly by an Admin).</summary>
    public required IReadOnlyList<PtcSlotMove> Proposals { get; init; }

    /// <summary>Identifies the allocations and proposals reviewed: applying refuses when it no longer matches (stale review).</summary>
    public required string Fingerprint { get; init; }
    public required IReadOnlyList<PtcTeacherSummary> Teachers { get; init; }
    internal PtcTeacherAvailability Availability { get; init; } = null!;

    public IReadOnlyList<DateOnly> Dates => Classes.Select(c => c.Date).Distinct().Order().ToList();
    public IEnumerable<PtcBookingAllocation> Bookings => Classes.SelectMany(c => c.Rounds).SelectMany(r => r.Slots).Where(s => s.Booking is not null).Select(s => s.Booking!);
    public bool IsClosed => Status is PtcEventStatus.Completed or PtcEventStatus.Cancelled;
}

// ============================================================ service

public interface IPtcScheduleService
{
    /// <summary>
    /// The whole event as one scheduling problem: every class, date, Round and parent slot; every booking's teacher interviews
    /// and their state; teachers shared between classes; conflicts (with their source and a safe resolution where one exists)
    /// and the proposed slot moves. Reads only: nothing changes until an Admin applies a change.
    /// </summary>
    Task<PtcScheduleAnalysis?> AnalyzeAsync(int eventId, CancellationToken ct = default);

    /// <summary>One teacher's PTC across the event: classes, dates, Rounds, interviews and everything that keeps them busy.</summary>
    Task<PtcTeacherSchedule?> TeacherAsync(int eventId, int teacherId, CancellationToken ct = default);

    /// <summary>
    /// Moves a confirmed booking to <paramref name="newSlot"/> of its own Round (same subjects, teachers and reference): explicit,
    /// confirmed, with a reason; refused when the booking changed since it was shown (<paramref name="expectedSlot"/>), the slot
    /// is taken, or any of its teachers is not free then. Missing interviews are allocated. Recorded in the history.
    /// </summary>
    Task<ServiceResult> ReassignSlotAsync(int bookingId, int newSlot, int expectedSlot, string? reason, bool confirmed, CancellationToken ct = default);

    /// <summary>Applies every proposal of the analysis the Admin reviewed (<paramref name="fingerprint"/>): all or nothing.</summary>
    Task<ServiceResult> ApplyProposalsAsync(int eventId, string? fingerprint, string? reason, bool confirmed, CancellationToken ct = default);

    /// <summary>
    /// Moves a Round to <paramref name="newStart"/> (same length, capacity and slots): its bookings' interviews move by the same
    /// amount, each checked for every teacher. A Round with bookings needs confirmation; a Published event never gets an
    /// uncovered lesson clash. Recorded in the history.
    /// </summary>
    Task<ServiceResult> MoveRoundAsync(int roundId, TimeOnly newStart, TimeOnly expectedStart, string? reason, bool confirmed, CancellationToken ct = default);

    /// <summary>The event's schedule-change history, newest first.</summary>
    Task<IReadOnlyList<PtcScheduleChangeView>> HistoryAsync(int eventId, CancellationToken ct = default);
}

/// <summary>
/// The PTC scheduling engine at event level (see <see cref="PtcSlotAllocator"/> for new bookings). The analysis loads every
/// class schedule, Round, booking and interview of the event and the availability of every teacher involved
/// (<see cref="PtcTeacherAvailability"/>), so a teacher shared by several classes is checked across all of them. Plans are
/// deterministic greedy: the most recently booked parent moves first (earlier bookings keep their times), each to its own
/// slot when that becomes valid, else to the earliest free slot of its Round that suits all its teachers. Nothing is applied
/// without an explicit Admin action; each change is re-validated under the PTC lock, written atomically and audited.
/// </summary>
public sealed class PtcScheduleService(SchoolDbContext db, IPtcAccess access, ICurrentUser currentUser) : IPtcScheduleService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public const int RoundMoveStepMinutes = 5;

    public const string StaleMessage = "The schedule changed since it was shown. Review it again before changing it.";

    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", C);
    private static string T(TimeOnly a, TimeOnly b) => ScheduleRules.Times(a, b);

    // ------------------------------------------------------------------ loading

    private sealed record SubjectRow(int Id, string Subject, int TeacherId, string TeacherName);
    private sealed record InterviewRow(int Id, int SubjectId, int TeacherId, TimeOnly Start, TimeOnly End, PtcInterviewStatus Status);
    private sealed record BookingRow(int Id, string Reference, string StudentName, int Slot, DateTime BookedAt, List<SubjectRow> Subjects, List<InterviewRow> Interviews);
    private sealed record RoundRow(int Id, int Number, TimeOnly Start, TimeOnly End, int Capacity, int Meeting, int Transition, PtcRoundStatus Status, List<BookingRow> Bookings);
    private sealed record ScheduleRow(int Id, int ClassId, string ClassName, DateOnly Date, TimeOnly Start, TimeOnly End, List<RoundRow> Rounds);

    private async Task<List<ScheduleRow>> LoadAsync(int eventId, CancellationToken ct)
    {
        var raw = await db.PtcClassSchedules.AsNoTracking()
            .Where(s => s.PtcEventId == eventId && s.Status != PtcScheduleStatus.Cancelled)
            .OrderBy(s => s.EventDate).ThenBy(s => s.SchoolClass.SortOrder)
            .Select(s => new
            {
                s.Id, s.SchoolClassId, ClassName = s.SchoolClass.Name, s.EventDate, s.StartTime, s.EndTime,
                Rounds = s.Rounds.Where(r => r.Status != PtcRoundStatus.Cancelled).OrderBy(r => r.StartTime).Select(r => new
                {
                    r.Id, r.RoundNumber, r.StartTime, r.EndTime, r.Capacity, r.MeetingDurationMinutes, r.TransitionDurationMinutes, r.Status,
                    Bookings = r.Bookings.Where(b => b.Status != PtcBookingStatus.Cancelled).OrderBy(b => b.SlotNumber).Select(b => new
                    {
                        b.Id, b.BookingReference, StudentName = b.Student.FullName, b.SlotNumber, b.BookedAt,
                        Subjects = b.Subjects.Where(x => x.Status == PtcBookingSubjectStatus.Active).OrderBy(x => x.SubjectName)
                            .Select(x => new { x.Id, x.SubjectName, x.AssignedTeacherStaffMemberId, TeacherName = x.AssignedTeacher.FullName }).ToList(),
                        Interviews = b.Interviews.Where(i => i.Status != PtcInterviewStatus.Cancelled)
                            .Select(i => new { i.Id, i.PtcBookingSubjectId, i.TeacherStaffMemberId, i.ScheduledStart, i.ScheduledEnd, i.Status }).ToList(),
                    }).ToList(),
                }).ToList(),
            })
            .ToListAsync(ct);
        return raw.Select(s => new ScheduleRow(s.Id, s.SchoolClassId, s.ClassName, s.EventDate, s.StartTime, s.EndTime, s.Rounds.Select(r =>
            new RoundRow(r.Id, r.RoundNumber, r.StartTime, r.EndTime, r.Capacity, r.MeetingDurationMinutes, r.TransitionDurationMinutes, r.Status, r.Bookings.Select(b =>
                new BookingRow(b.Id, b.BookingReference, b.StudentName, b.SlotNumber, b.BookedAt,
                    b.Subjects.Select(x => new SubjectRow(x.Id, x.SubjectName, x.AssignedTeacherStaffMemberId, x.TeacherName)).ToList(),
                    b.Interviews.Select(i => new InterviewRow(i.Id, i.PtcBookingSubjectId, i.TeacherStaffMemberId, i.ScheduledStart, i.ScheduledEnd, i.Status)).ToList()))
                .ToList())).ToList())).ToList();
    }

    // ------------------------------------------------------------------ analysis

    public async Task<PtcScheduleAnalysis?> AnalyzeAsync(int eventId, CancellationToken ct = default)
    {
        access.Ensure();
        return await AnalyzeCoreAsync(eventId, ct);
    }

    private async Task<PtcScheduleAnalysis?> AnalyzeCoreAsync(int eventId, CancellationToken ct)
    {
        var ev = await db.PtcEvents.AsNoTracking().Where(e => e.Id == eventId).Select(e => new { e.Title, e.Status }).FirstOrDefaultAsync(ct);
        if (ev is null) return null;
        var schedules = await LoadAsync(eventId, ct);

        // The class's offered subjects and their teachers (timetable, every class alike), plus every booked teacher.
        var options = await PtcSchedulingRules.ClassSubjectsAsync(db, schedules.Select(s => (s.ClassId, s.Date, (int?)s.Id)).ToList(), ct);
        var classTeachers = schedules.ToDictionary(s => s.Id, s => options[s.ClassId]
            .Select(o => new PtcClassTeacherRef(o.TeacherId, o.TeacherName, o.Subject)).ToList());
        var teacherIds = classTeachers.Values.SelectMany(t => t).Select(t => t.TeacherId)
            .Concat(schedules.SelectMany(s => s.Rounds).SelectMany(r => r.Bookings).SelectMany(b => b.Subjects).Select(x => x.TeacherId)).Distinct().ToList();
        var availability = await PtcTeacherAvailability.LoadAsync(db, teacherIds, schedules.Select(s => s.Date).Distinct().ToList(), ct);
        var kgClasses = (await db.Classes.AsNoTracking().Where(c => schedules.Select(s => s.ClassId).Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Where(c => ClassStages.IsKgStage(c.Name)).Select(c => c.Id).ToHashSet();

        var conflicts = new List<PtcScheduleConflict>();
        var states = new Dictionary<int, (PtcAllocationState State, List<PtcInterviewAllocation> Interviews)>();

        // 1. Every booking: each chosen subject must have one interview, at the booking's slot time, with its teacher free.
        foreach (var s in schedules)
            foreach (var r in s.Rounds)
                foreach (var b in r.Bookings)
                {
                    var (start, end) = PtcRules.SlotWindow(r.Start, b.Slot, r.Meeting);
                    var list = new List<PtcInterviewAllocation>();
                    foreach (var x in b.Subjects)
                    {
                        var i = b.Interviews.FirstOrDefault(v => v.SubjectId == x.Id);
                        if (i is null)
                        {
                            list.Add(new PtcInterviewAllocation(null, x.TeacherId, x.TeacherName, x.Subject, null, null, PtcAllocationState.Unallocated, "No interview allocated for this subject."));
                            continue;
                        }
                        var hits = availability.Blocking(i.TeacherId, s.Date, i.Start, i.End, v => v.BookingId == b.Id).Where(h => h.Blocks).ToList();
                        if (hits.Count > 0)
                            list.Add(new PtcInterviewAllocation(i.Id, i.TeacherId, x.TeacherName, x.Subject, i.Start, i.End, PtcAllocationState.Conflict, string.Join(" ", hits.Select(h => h.Message))));
                        else if (i.Start != start || i.End != end)
                            list.Add(new PtcInterviewAllocation(i.Id, i.TeacherId, x.TeacherName, x.Subject, i.Start, i.End, PtcAllocationState.RequiresAdminAction,
                                $"The interview ({T(i.Start, i.End)}) is not at the booking's slot time ({T(start, end)})."));
                        else
                            list.Add(new PtcInterviewAllocation(i.Id, i.TeacherId, x.TeacherName, x.Subject, i.Start, i.End, PtcAllocationState.Scheduled, null));
                    }
                    var state = list.Any(v => v.State == PtcAllocationState.Conflict) ? PtcAllocationState.Conflict
                        : list.Any(v => v.State == PtcAllocationState.Unallocated) ? PtcAllocationState.Unallocated
                        : list.Any(v => v.State == PtcAllocationState.RequiresAdminAction) ? PtcAllocationState.RequiresAdminAction
                        : PtcAllocationState.Scheduled;
                    states[b.Id] = (state, list);
                }

        // 2. Plan: the most recently booked problem bookings move first, each to its own slot if valid, else the earliest valid slot.
        var proposals = new List<PtcSlotMove>();
        var unresolved = new HashSet<int>();
        var moved = new HashSet<int>();
        var taken = schedules.SelectMany(s => s.Rounds).ToDictionary(r => r.Id, r => r.Bookings.Select(b => b.Slot).ToHashSet());
        var problems = schedules.SelectMany(s => s.Rounds.SelectMany(r => r.Bookings.Select(b => (s, r, b))))
            .Where(x => states[x.b.Id].State != PtcAllocationState.Scheduled)
            .OrderByDescending(x => x.b.BookedAt).ThenByDescending(x => x.b.Id).ToList();
        foreach (var (s, r, b) in problems)
        {
            var teachers = b.Subjects.Select(x => x.TeacherId).Distinct().ToList();
            bool Ignore(PtcBusyInterval v) => !v.Planned && v.BookingId is { } id && (id == b.Id || moved.Contains(id));
            var slots = PtcSlotAllocator.Slots(r.Start, r.End, r.Capacity, r.Meeting).ToList();
            var own = slots.FirstOrDefault(c => c.Slot == b.Slot);
            var target = own is not null && availability.AllFree(teachers, s.Date, own.Start, own.End, Ignore) ? own
                : slots.Where(c => c.Slot != b.Slot && !taken[r.Id].Contains(c.Slot)).FirstOrDefault(c => availability.AllFree(teachers, s.Date, c.Start, c.End, Ignore));
            if (target is null)
            {
                unresolved.Add(b.Id);
                continue;
            }
            var (oldStart, oldEnd) = PtcRules.SlotWindow(r.Start, b.Slot, r.Meeting);
            var why = states[b.Id].State switch
            {
                PtcAllocationState.Conflict => "A teacher is busy at the current time.",
                PtcAllocationState.Unallocated => "A chosen subject has no interview yet.",
                _ => "The interviews are not at the booking's slot time.",
            };
            proposals.Add(new PtcSlotMove(b.Id, b.Reference, s.ClassName, r.Id, r.Number, s.Date, b.Slot, oldStart, oldEnd, target.Slot, target.Start, target.End,
                b.Subjects.Select(x => x.TeacherName).Distinct().ToList(), why));
            moved.Add(b.Id);
            taken[r.Id].Remove(b.Slot);
            taken[r.Id].Add(target.Slot);
            foreach (var t in teachers)
                availability.Reserve(new PtcBusyInterval(t, s.Date, target.Start, target.End, PtcConflictKind.Interview,
                    $"{availability.Name(t)} has a planned PTC interview ({s.ClassName}) at {T(target.Start, target.End)}.", BookingId: b.Id, ScheduleId: s.Id));
        }

        // 3. Booking conflicts, with their proposal or the reason none is safe.
        foreach (var (s, r, b) in problems)
        {
            var (st, list) = states[b.Id];
            var move = proposals.FirstOrDefault(p => p.BookingId == b.Id);
            foreach (var i in list.Where(v => v.State != PtcAllocationState.Scheduled))
                conflicts.Add(new PtcScheduleConflict(
                    i.State == PtcAllocationState.Conflict ? PtcConflictSeverity.Hard : PtcConflictSeverity.AdminAction,
                    i.State switch { PtcAllocationState.Conflict => "Teacher busy at interview time", PtcAllocationState.Unallocated => "Unallocated interview", _ => "Interview not at slot time" },
                    s.Date, i.TeacherId, i.TeacherName, [s.ClassName],
                    i.Start is { } a && i.End is { } e ? [T(a, e)] : [], i.Problem ?? "", r.Id, $"Round {r.Number}", [b.Reference],
                    move is null
                        ? "No free time in this Round suits all of the booking's teachers: move the Round, or record a substitute (Phase 6). The booking is left unchanged."
                        : move.NewSlot == move.OldSlot
                            ? $"Allocate the interviews at the booking's own slot ({T(move.NewStart, move.NewEnd)}); the parent's time does not change."
                            : $"Move the booking from slot {move.OldSlot} ({T(move.OldStart, move.OldEnd)}) to slot {move.NewSlot} ({T(move.NewStart, move.NewEnd)}), same Round and teachers.",
                    move));
        }

        // 4. Round-level: uncovered lessons during a Round (block publishing), other busy periods (restrict slots), and teachers
        //    shared by classes whose Rounds overlap on the same day (restrict slots).
        var roundsByDate = schedules.SelectMany(s => s.Rounds.Select(r => (s, r))).ToList();
        foreach (var s in schedules)
            foreach (var r in s.Rounds)
                foreach (var t in classTeachers[s.Id].DistinctBy(t => t.TeacherId))
                {
                    var hits = availability.Blocking(t.TeacherId, s.Date, r.Start, r.End, v => v.Kind == PtcConflictKind.Interview).Where(h => h.Blocks).ToList();
                    foreach (var lesson in hits.Where(h => h.Interval.Kind == PtcConflictKind.Lesson))
                        conflicts.Add(new PtcScheduleConflict(PtcConflictSeverity.Hard, "Uncovered lesson during a Round", s.Date, t.TeacherId, t.Name, [s.ClassName],
                            [T(r.Start, r.End), T(lesson.Interval.Start, lesson.Interval.End)], lesson.Message, r.Id, $"Round {r.Number}",
                            r.Bookings.Select(b => b.Reference).ToList(),
                            SuggestRoundMove(s, r, classTeachers, roundsByDate, availability) is { } m1
                                ? $"Move Round {r.Number} to {T(m1.NewStart, m1.NewEnd)}, or record a substitute (Phase 6). Publishing is blocked until then."
                                : "Record a substitute (Phase 6) or change the Rounds. Publishing is blocked until then.",
                            RoundMove: SuggestRoundMove(s, r, classTeachers, roundsByDate, availability)));
                    foreach (var busy in hits.Where(h => h.Interval.Kind != PtcConflictKind.Lesson))
                        conflicts.Add(new PtcScheduleConflict(PtcConflictSeverity.Restriction, "Teacher busy during part of a Round", s.Date, t.TeacherId, t.Name, [s.ClassName],
                            [T(r.Start, r.End), T(busy.Interval.Start, busy.Interval.End)], busy.Message, r.Id, $"Round {r.Number}", [],
                            "Slots at that time cannot include this teacher; parents choosing them get another slot of the Round."));
                }
        var seen = new HashSet<(int, int, int)>();
        foreach (var g in roundsByDate.SelectMany(x => classTeachers[x.s.Id].Select(t => t.TeacherId).Distinct().Select(t => (Teacher: t, x.s, x.r))).GroupBy(x => (x.Teacher, x.s.Date)))
        {
            var list = g.ToList();
            foreach (var a in list)
                foreach (var b in list.Where(b => b.s.Id != a.s.Id && b.r.Id > a.r.Id && PtcRules.Overlaps(a.r.Start, a.r.End, b.r.Start, b.r.End)))
                {
                    if (!seen.Add((g.Key.Teacher, a.r.Id, b.r.Id))) continue;
                    var later = b.r.Start >= a.r.Start ? b : a;
                    var move = SuggestRoundMove(later.s, later.r, classTeachers, roundsByDate, availability);
                    conflicts.Add(new PtcScheduleConflict(PtcConflictSeverity.Restriction, "Shared teacher in overlapping Rounds", g.Key.Date, g.Key.Teacher,
                        availability.Name(g.Key.Teacher), [a.s.ClassName, b.s.ClassName], [$"{a.s.ClassName} Round {a.r.Number} {T(a.r.Start, a.r.End)}", $"{b.s.ClassName} Round {b.r.Number} {T(b.r.Start, b.r.End)}"],
                        $"{availability.Name(g.Key.Teacher)} teaches both classes; the engine gives each interview its own time, so some slots cannot include this teacher.",
                        later.r.Id, $"{later.s.ClassName} Round {later.r.Number}", [],
                        move is null ? "Interviews are still allocated without overlap. No conflict-free time was found to move a Round."
                            : $"Optional: move {later.s.ClassName} Round {later.r.Number} to {T(move.NewStart, move.NewEnd)} so the teacher is free for both classes.",
                        RoundMove: move));
                }
        }

        // 5. Views.
        var views = schedules.Select(s => new PtcClassScheduleView(s.Id, s.ClassId, s.ClassName, kgClasses.Contains(s.ClassId), s.Date, s.Start, s.End, classTeachers[s.Id],
            s.Rounds.Select(r => new PtcRoundView(r.Id, r.Number, r.Start, r.End, r.Capacity, r.Meeting, r.Transition, r.Status,
                PtcSlotAllocator.Slots(r.Start, r.End, r.Capacity, r.Meeting).Select(c =>
                {
                    var b = r.Bookings.FirstOrDefault(x => x.Slot == c.Slot);
                    if (b is null) return new PtcSlotView(c.Slot, c.Start, c.End, PtcAllocationState.Available, null);
                    var (st, list) = states[b.Id];
                    var state = st != PtcAllocationState.Scheduled && unresolved.Contains(b.Id) && st != PtcAllocationState.Conflict ? PtcAllocationState.RequiresAdminAction : st;
                    return new PtcSlotView(c.Slot, c.Start, c.End, state, new PtcBookingAllocation(b.Id, b.Reference, b.StudentName, b.Slot, c.Start, c.End, state, list, b.BookedAt));
                }).ToList())).ToList())).ToList();

        var teachersSummary = teacherIds.Select(t =>
        {
            var classes = views.Where(v => v.Teachers.Any(x => x.TeacherId == t)).Select(v => v.ClassName).Distinct().ToList();
            var interviews = views.SelectMany(v => v.Rounds).SelectMany(r => r.Slots).Where(x => x.Booking is not null).SelectMany(x => x.Booking!.Interviews).Where(i => i.TeacherId == t).ToList();
            return new PtcTeacherSummary(t, availability.Name(t), classes, views.Where(v => v.Teachers.Any(x => x.TeacherId == t)).Select(v => v.Date).Distinct().Order().ToList(),
                interviews.Count, conflicts.Count(c => c.TeacherId == t && c.Severity != PtcConflictSeverity.Restriction));
        }).OrderBy(t => t.Name).ToList();

        var ordered = conflicts.OrderBy(c => c.Severity).ThenBy(c => c.Date).ThenBy(c => c.TeacherName).ToList();
        return new PtcScheduleAnalysis
        {
            EventId = eventId, Title = ev.Title, Status = ev.Status, Classes = views, Conflicts = ordered, Proposals = proposals,
            Fingerprint = Fingerprint(schedules, proposals), Teachers = teachersSummary, Availability = availability,
        };
    }

    /// <summary>The nearest start time (5-minute steps within the class's daily time) at which the whole Round is free for all its teachers.</summary>
    private static PtcRoundMoveSuggestion? SuggestRoundMove(ScheduleRow s, RoundRow r, Dictionary<int, List<PtcClassTeacherRef>> classTeachers,
        List<(ScheduleRow s, RoundRow r)> allRounds, PtcTeacherAvailability availability)
    {
        var minutes = PtcRules.Minutes(r.Start, r.End);
        var teachers = classTeachers[s.Id].Select(t => t.TeacherId).Distinct().ToList();
        var shared = allRounds.Where(x => x.s.Date == s.Date && x.s.Id != s.Id && classTeachers[x.s.Id].Any(t => teachers.Contains(t.TeacherId))).ToList();
        var own = r.Bookings.Select(b => b.Id).ToHashSet();
        var candidates = new List<TimeOnly>();
        for (var t = s.Start; PtcRules.Minutes(t, s.End) >= minutes && t.AddMinutes(minutes) > t; t = t.AddMinutes(RoundMoveStepMinutes))
        {
            if (t == r.Start) continue;
            var e = t.AddMinutes(minutes);
            if (s.Rounds.Any(o => o.Id != r.Id && PtcRules.Overlaps(t, e, o.Start, o.End))) continue;
            if (shared.Any(x => PtcRules.Overlaps(t, e, x.r.Start, x.r.End))) continue;
            if (!availability.AllFree(teachers, s.Date, t, e, v => v.BookingId is { } id && own.Contains(id))) continue;
            candidates.Add(t);
        }
        if (candidates.Count == 0) return null;
        var best = candidates.OrderBy(t => Math.Abs(PtcRules.Minutes(r.Start, t))).ThenBy(t => t).First();
        return new PtcRoundMoveSuggestion(r.Id, r.Number, s.ClassName, r.Start, r.End, best, best.AddMinutes(minutes), r.Bookings.Count);
    }

    private static string Fingerprint(List<ScheduleRow> schedules, List<PtcSlotMove> proposals)
    {
        var sb = new StringBuilder();
        foreach (var r in schedules.SelectMany(s => s.Rounds))
        {
            sb.Append(C, $"R{r.Id}:{r.Start:HHmm}-{r.End:HHmm};");
            foreach (var b in r.Bookings)
            {
                sb.Append(C, $"B{b.Id}:{b.Slot}:");
                foreach (var i in b.Interviews.OrderBy(i => i.Id)) sb.Append(C, $"{i.Id}@{i.Start:HHmm};");
            }
        }
        foreach (var p in proposals) sb.Append(C, $"P{p.BookingId}:{p.OldSlot}>{p.NewSlot};");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24];
    }

    // ------------------------------------------------------------------ teacher view

    public async Task<PtcTeacherSchedule?> TeacherAsync(int eventId, int teacherId, CancellationToken ct = default)
    {
        access.Ensure();
        var a = await AnalyzeCoreAsync(eventId, ct);
        if (a is null || !a.Teachers.Any(t => t.TeacherId == teacherId)) return null;
        var items = new List<PtcTeacherItem>();
        foreach (var v in a.Classes.Where(v => v.Teachers.Any(t => t.TeacherId == teacherId)))
            foreach (var r in v.Rounds)
                items.Add(new PtcTeacherItem(v.Date, r.Start, r.End, "Round", $"{v.ClassName} Round {r.Number} ({r.Booked}/{r.Capacity} booked)", v.ClassName,
                    string.Join(", ", v.Teachers.Where(t => t.TeacherId == teacherId).Select(t => t.Subject)), null, r.Number, PtcAllocationState.Available));
        foreach (var v in a.Classes)
            foreach (var r in v.Rounds)
                foreach (var b in r.Slots.Where(x => x.Booking is not null).Select(x => x.Booking!))
                    foreach (var i in b.Interviews.Where(i => i.TeacherId == teacherId))
                        items.Add(new PtcTeacherItem(v.Date, i.Start ?? b.Start, i.End ?? b.End, "Interview", $"{b.StudentName} · slot {b.Slot}", v.ClassName, i.Subject,
                            b.Reference, r.Number, i.State));
        foreach (var d in a.Dates)
            foreach (var busy in a.Availability.Busy(teacherId, d).Where(x => !x.Planned && !(x.Kind == PtcConflictKind.Interview && x.ScheduleId is { } sid && a.Classes.Any(c => c.ScheduleId == sid))))
            {
                var covered = busy.Kind == PtcConflictKind.Lesson && a.Availability.Blocking(teacherId, d, busy.Start, busy.End).Any(h => h.Interval == busy && h.Covered);
                items.Add(new PtcTeacherItem(d, busy.Start, busy.End, busy.Kind switch
                {
                    PtcConflictKind.Lesson => covered ? "Lesson (covered)" : "Lesson",
                    PtcConflictKind.Duty => "Duty",
                    PtcConflictKind.WorkloadPeriod => "Workload period",
                    PtcConflictKind.SubstituteCover => "Substitute cover",
                    _ => "Other PTC interview",
                }, busy.Description, null, null, null, null, covered ? PtcAllocationState.Available : PtcAllocationState.Conflict));
            }
        var summary = a.Teachers.Single(t => t.TeacherId == teacherId);
        return new PtcTeacherSchedule(teacherId, summary.Name, summary.Classes, summary.Dates,
            items.OrderBy(i => i.Date).ThenBy(i => i.Start).ThenBy(i => i.Kind).ToList(), a.Conflicts.Where(c => c.TeacherId == teacherId).ToList());
    }

    // ------------------------------------------------------------------ actions

    private static ServiceResult? CheckChange(string? reason, bool confirmed, bool affectsBookings)
    {
        if (string.IsNullOrWhiteSpace(reason)) return ServiceResult.Fail("Reason", "Give the reason for this schedule change (it is kept in the history).");
        if (reason.Trim().Length > PtcScheduleChange.TextMaxLength) return ServiceResult.Fail("Reason", $"Use {PtcScheduleChange.TextMaxLength} characters or fewer.");
        if (affectsBookings && !confirmed)
            return ServiceResult.Fail("Confirmed", "Confirm the change: it moves confirmed parents' meeting times.");
        return null;
    }

    public async Task<ServiceResult> ReassignSlotAsync(int bookingId, int newSlot, int expectedSlot, string? reason, bool confirmed, CancellationToken ct = default)
    {
        access.Ensure();
        if (CheckChange(reason, confirmed, true) is { } bad) return bad;
        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        await PtcSetupService.LockAsync(db, ct);
        var result = await MoveBookingAsync(bookingId, newSlot, expectedSlot, reason!.Trim(), ct);
        if (!result.Succeeded) { db.ChangeTracker.Clear(); return result; }
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Moves one booking inside the lock (caller owns the transaction): validates, updates the slot and interviews, audits.</summary>
    private async Task<ServiceResult> MoveBookingAsync(int bookingId, int newSlot, int expectedSlot, string reason, CancellationToken ct)
    {
        var b = await db.PtcParentBookings.Include(x => x.Interviews).Include(x => x.Subjects).ThenInclude(x => x.AssignedTeacher)
            .Include(x => x.Round).ThenInclude(r => r.ClassSchedule).ThenInclude(s => s.PtcEvent)
            .FirstOrDefaultAsync(x => x.Id == bookingId, ct);
        if (b is null || b.Status == PtcBookingStatus.Cancelled) return ServiceResult.Fail("", "This booking no longer exists or is cancelled.");
        var r = b.Round;
        var s = r.ClassSchedule;
        if (s.PtcEvent.Status is PtcEventStatus.Cancelled or PtcEventStatus.Completed) return ServiceResult.Fail("", PtcSetupService.ClosedMessage);
        if (b.SlotNumber != expectedSlot) return ServiceResult.Fail("", StaleMessage);
        var slot = PtcSlotAllocator.Slots(r.StartTime, r.EndTime, r.Capacity, r.MeetingDurationMinutes).FirstOrDefault(c => c.Slot == newSlot);
        if (slot is null) return ServiceResult.Fail("NewSlot", "Choose a slot of this Round.");
        if (newSlot != b.SlotNumber && await db.PtcParentBookings.AnyAsync(x => x.PtcRoundId == r.Id && x.SlotNumber == newSlot && x.Status != PtcBookingStatus.Cancelled, ct))
            return ServiceResult.Fail("NewSlot", $"Slot {newSlot} is taken by another booking.");
        var active = b.Interviews.Where(i => i.Status != PtcInterviewStatus.Cancelled).ToList();
        if (active.Any(i => i.Status != PtcInterviewStatus.Scheduled))
            return ServiceResult.Fail("", "An interview of this booking has started or finished: its time cannot change.");
        var subjects = b.Subjects.Where(x => x.Status == PtcBookingSubjectStatus.Active).ToList();
        var teachers = subjects.Select(x => x.AssignedTeacherStaffMemberId).Distinct().ToList();
        var availability = await PtcTeacherAvailability.LoadAsync(db, teachers, [s.EventDate], ct);
        var block = availability.FirstBlock(teachers, s.EventDate, slot.Start, slot.End, v => v.BookingId == b.Id);
        if (block is not null) return ServiceResult.Fail("NewSlot", $"Slot {newSlot} ({T(slot.Start, slot.End)}) is not free: {block.Message}");

        var (oldStart, oldEnd) = PtcRules.SlotWindow(r.StartTime, b.SlotNumber, r.MeetingDurationMinutes);
        db.PtcScheduleChanges.Add(new PtcScheduleChange
        {
            PtcEventId = s.PtcEventId, PtcClassScheduleId = s.Id, PtcRoundId = r.Id, PtcParentBookingId = b.Id, Kind = PtcScheduleChangeKind.SlotReassigned,
            Date = s.EventDate, OldSlot = b.SlotNumber, NewSlot = newSlot, OldStart = oldStart, OldEnd = oldEnd, NewStart = slot.Start, NewEnd = slot.End,
            Teachers = Teachers(subjects.Select(x => x.AssignedTeacher.FullName)), Reason = reason, ChangedBy = currentUser.AccountName(), ChangedAt = DateTime.UtcNow,
        });
        b.SlotNumber = newSlot;
        foreach (var x in subjects)
        {
            var i = active.FirstOrDefault(v => v.PtcBookingSubjectId == x.Id);
            if (i is null)
                b.Interviews.Add(new PtcTeacherInterview { BookingSubject = x, TeacherStaffMemberId = x.AssignedTeacherStaffMemberId, Date = s.EventDate, ScheduledStart = slot.Start, ScheduledEnd = slot.End });
            else
            {
                i.ScheduledStart = slot.Start;
                i.ScheduledEnd = slot.End;
                i.Date = s.EventDate;
            }
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return ServiceResult.Fail("NewSlot", $"Slot {newSlot} was just taken. Review the schedule again.");
        }
        return ServiceResult.Ok(b.Id);
    }

    private static string? Teachers(IEnumerable<string> names)
    {
        var text = string.Join(", ", names.Distinct());
        return text.Length == 0 ? null : text.Length > PtcScheduleChange.TextMaxLength ? text[..PtcScheduleChange.TextMaxLength] : text;
    }

    public async Task<ServiceResult> ApplyProposalsAsync(int eventId, string? fingerprint, string? reason, bool confirmed, CancellationToken ct = default)
    {
        access.Ensure();
        if (CheckChange(reason, confirmed, true) is { } bad) return bad;
        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        await PtcSetupService.LockAsync(db, ct);
        // Recomputed inside the lock: applied only when it is exactly what the Admin reviewed.
        var analysis = await AnalyzeCoreAsync(eventId, ct);
        if (analysis is null) return ServiceResult.Fail("", "This PTC event no longer exists.");
        if (analysis.Fingerprint != fingerprint) return ServiceResult.Fail("", StaleMessage);
        if (analysis.Proposals.Count == 0) return ServiceResult.Fail("", "There is nothing to apply.");
        // A move into a slot another proposal frees is applied after that one (plan order is safe; the slot index is the backstop).
        foreach (var p in Order(analysis.Proposals))
        {
            var result = await MoveBookingAsync(p.BookingId, p.NewSlot, p.OldSlot, reason!.Trim(), ct);
            if (!result.Succeeded)
            {
                db.ChangeTracker.Clear();
                return ServiceResult.Fail("", $"Nothing was changed: {p.Reference} could not move ({result.Errors[0].Value})");
            }
        }
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(analysis.Proposals.Count);
    }

    /// <summary>Moves that free a slot come before moves into it.</summary>
    private static IEnumerable<PtcSlotMove> Order(IReadOnlyList<PtcSlotMove> moves)
    {
        var pending = moves.ToList();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(m => m.NewSlot == m.OldSlot || !pending.Any(o => o != m && o.RoundId == m.RoundId && o.OldSlot == m.NewSlot)) ?? pending[0];
            pending.Remove(next);
            yield return next;
        }
    }

    public async Task<ServiceResult> MoveRoundAsync(int roundId, TimeOnly newStart, TimeOnly expectedStart, string? reason, bool confirmed, CancellationToken ct = default)
    {
        access.Ensure();
        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        await PtcSetupService.LockAsync(db, ct);
        var r = await db.PtcRounds.Include(x => x.ClassSchedule).ThenInclude(s => s.PtcEvent).Include(x => x.ClassSchedule).ThenInclude(s => s.SchoolClass)
            .FirstOrDefaultAsync(x => x.Id == roundId, ct);
        if (r is null || r.Status == PtcRoundStatus.Cancelled) return ServiceResult.Fail("", "This Round no longer exists.");
        var s = r.ClassSchedule;
        if (s.PtcEvent.Status is PtcEventStatus.Cancelled or PtcEventStatus.Completed || s.Status != PtcScheduleStatus.Scheduled)
            return ServiceResult.Fail("", PtcSetupService.ClosedMessage);
        if (r.StartTime != expectedStart) return ServiceResult.Fail("", StaleMessage);
        var minutes = PtcRules.Minutes(r.StartTime, r.EndTime);
        if (PtcRules.Minutes(newStart, TimeOnly.MaxValue) < minutes) return ServiceResult.Fail("NewStart", "The Round would end after midnight.");
        var newEnd = newStart.AddMinutes(minutes);
        if (newStart == r.StartTime) return ServiceResult.Fail("NewStart", "Choose a different start time.");
        if (newStart < s.StartTime || newEnd > s.EndTime)
            return ServiceResult.Fail("NewStart", $"The Round must stay within {s.SchoolClass.Name}'s daily time ({T(s.StartTime, s.EndTime)}).");
        var others = await db.PtcRounds.AsNoTracking().Where(x => x.PtcClassScheduleId == s.Id && x.Id != r.Id && x.Status != PtcRoundStatus.Cancelled)
            .Select(x => new { x.RoundNumber, x.StartTime, x.EndTime }).ToListAsync(ct);
        if (others.FirstOrDefault(o => PtcRules.Overlaps(newStart, newEnd, o.StartTime, o.EndTime)) is { } clash)
            return ServiceResult.Fail("NewStart", $"It would overlap Round {clash.RoundNumber} ({T(clash.StartTime, clash.EndTime)}).");

        var bookings = await db.PtcParentBookings.Include(b => b.Interviews).Include(b => b.Subjects).ThenInclude(x => x.AssignedTeacher)
            .Where(b => b.PtcRoundId == r.Id && b.Status != PtcBookingStatus.Cancelled).ToListAsync(ct);
        if (CheckChange(reason, confirmed, bookings.Count > 0) is { } bad) return bad;
        if (bookings.SelectMany(b => b.Interviews).Any(i => i.Status is PtcInterviewStatus.InProgress or PtcInterviewStatus.Completed))
            return ServiceResult.Fail("", "An interview of this Round has started or finished: the Round cannot move.");

        // Every booking keeps its slot; each teacher must be free at the new time (this Round's own interviews move together).
        var own = bookings.Select(b => b.Id).ToHashSet();
        var options = await PtcSchedulingRules.ClassSubjectsAsync(db, s.SchoolClassId, s.EventDate, ct, s.Id);
        var teachers = bookings.SelectMany(b => b.Subjects).Select(x => x.AssignedTeacherStaffMemberId).Concat(options.Select(o => o.TeacherId)).Distinct().ToList();
        var availability = await PtcTeacherAvailability.LoadAsync(db, teachers, [s.EventDate], ct);
        bool Ignore(PtcBusyInterval v) => v.BookingId is { } id && own.Contains(id);
        foreach (var b in bookings)
        {
            var (bs, be) = PtcRules.SlotWindow(newStart, b.SlotNumber, r.MeetingDurationMinutes);
            var block = availability.FirstBlock(b.Subjects.Where(x => x.Status == PtcBookingSubjectStatus.Active).Select(x => x.AssignedTeacherStaffMemberId).Distinct(), s.EventDate, bs, be, Ignore);
            if (block is not null) return ServiceResult.Fail("NewStart", $"Booking {b.BookingReference} would clash at {T(bs, be)}: {block.Message} Nothing was changed.");
        }
        // A Published event never gets an uncovered lesson during a Round (the publishing rule).
        if (s.PtcEvent.Status == PtcEventStatus.Published)
            foreach (var t in options.Select(o => o.TeacherId).Distinct())
                if (availability.Blocking(t, s.EventDate, newStart, newEnd).FirstOrDefault(h => h.Blocks && h.Interval.Kind == PtcConflictKind.Lesson) is { } lesson)
                    return ServiceResult.Fail("NewStart", $"At that time: {lesson.Message} A Published event cannot have an uncovered lesson during a Round.");

        var by = currentUser.AccountName();
        var now = DateTime.UtcNow;
        var text = reason!.Trim();
        db.PtcScheduleChanges.Add(new PtcScheduleChange
        {
            PtcEventId = s.PtcEventId, PtcClassScheduleId = s.Id, PtcRoundId = r.Id, Kind = PtcScheduleChangeKind.RoundMoved, Date = s.EventDate,
            OldStart = r.StartTime, OldEnd = r.EndTime, NewStart = newStart, NewEnd = newEnd, Reason = text, ChangedBy = by, ChangedAt = now,
        });
        foreach (var b in bookings)
        {
            var (os, oe) = PtcRules.SlotWindow(r.StartTime, b.SlotNumber, r.MeetingDurationMinutes);
            var (ns, ne) = PtcRules.SlotWindow(newStart, b.SlotNumber, r.MeetingDurationMinutes);
            db.PtcScheduleChanges.Add(new PtcScheduleChange
            {
                PtcEventId = s.PtcEventId, PtcClassScheduleId = s.Id, PtcRoundId = r.Id, PtcParentBookingId = b.Id, Kind = PtcScheduleChangeKind.RoundMoved,
                Date = s.EventDate, OldSlot = b.SlotNumber, NewSlot = b.SlotNumber, OldStart = os, OldEnd = oe, NewStart = ns, NewEnd = ne,
                Teachers = Teachers(b.Subjects.Where(x => x.Status == PtcBookingSubjectStatus.Active).Select(x => x.AssignedTeacher.FullName)),
                Reason = text, ChangedBy = by, ChangedAt = now,
            });
            foreach (var i in b.Interviews.Where(i => i.Status == PtcInterviewStatus.Scheduled))
            {
                i.ScheduledStart = ns;
                i.ScheduledEnd = ne;
            }
        }
        r.StartTime = newStart;
        r.EndTime = newEnd;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(r.Id);
    }

    public async Task<IReadOnlyList<PtcScheduleChangeView>> HistoryAsync(int eventId, CancellationToken ct = default)
    {
        access.Ensure();
        return await db.PtcScheduleChanges.AsNoTracking().Where(c => c.PtcEventId == eventId)
            .OrderByDescending(c => c.ChangedAt).ThenByDescending(c => c.Id)
            .Select(c => new PtcScheduleChangeView(c.ChangedAt, c.ChangedBy, c.Kind, c.ClassSchedule.SchoolClass.Name, c.Round.RoundNumber,
                c.Booking != null ? c.Booking.BookingReference : null, c.Date, c.OldSlot, c.NewSlot, c.OldStart, c.OldEnd, c.NewStart, c.NewEnd, c.Teachers, c.Reason))
            .ToListAsync(ct);
    }
}
