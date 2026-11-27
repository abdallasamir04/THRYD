using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Tests;

/// <summary>
/// PTC Phase 4: the scheduling engine. New bookings get the earliest slot where every chosen teacher is free together, across
/// all classes (timetable and KG); teacher availability includes other interviews, lessons (and their cover), duties and
/// workload periods; the event-wide analysis finds conflicts without changing anything; Admin changes (slot reassignment,
/// applying proposals, moving a Round) are explicit, confirmed, re-validated, atomic and audited. Test database only.
/// </summary>
[Collection("db")]
public class PtcSchedulingTests(DbFixture fx)
{
    private static readonly DateOnly Day = new(2027, 3, 9); // a Tuesday
    private static readonly DateOnly LessonsFrom = SchoolWeek.StartOf(new DateOnly(2026, 9, 6));
    private static readonly TimeOnly T8 = new(8, 0), T14 = new(14, 0);
    private static TimeOnly At(int h, int m) => new(h, m);

    private sealed class Admin : IPtcAccess { public bool CanManage => true; }
    private sealed class User : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static SettingsService Settings(SchoolDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));
    private static PtcAdminService AdminService(SchoolDbContext db) => new(db, new Admin(), Settings(db), new ClassTeamService(db, new User()));
    private static PtcSetupService Setup(SchoolDbContext db) => new(db, new Admin(), Settings(db), new User(), AdminService(db));
    private static PtcBookingService Booking(SchoolDbContext db) => new(db, new Admin(), new User());
    private static PtcScheduleService Engine(SchoolDbContext db) => new(db, new Admin(), new User());

    private static int Ok(ServiceResult r)
    {
        Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));
        return r.EntityId ?? 0;
    }

    private static void Fails(ServiceResult r, string text)
    {
        Assert.False(r.Succeeded, "Expected a failure containing: " + text);
        Assert.Contains(r.Errors, e => e.Value.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ fixtures (test database only)

    private async Task<int> EventAsync()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "PTC4 " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        return Ok(await Setup(db).SaveEventAsync(null, new PtcEventInput("PTC " + DbFixture.Unique(), null, year.Id, Day, Day.AddDays(4))));
    }

    private async Task<int> ClassAsync(string prefix = "S")
    {
        await using var db = fx.NewContext();
        for (var i = 0; i < 50; i++)
        {
            var r = await DbFixture.Classes(db).CreateAsync(prefix + new string(Enumerable.Range(0, prefix.StartsWith("KG") ? 3 : 4).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray()), null, true);
            if (r.Succeeded) return r.EntityId!.Value;
        }
        throw new InvalidOperationException("No free class name.");
    }

    private async Task<int> SlotAsync(int classId, TimeOnly? start = null, TimeOnly? end = null)
    {
        await using var db = fx.NewContext();
        var slot = new TimeSlot { Number = Random.Shared.Next(100, 999), SchoolClassId = classId, StartTime = start, EndTime = end, UpdatedAt = DateTime.UtcNow };
        db.TimeSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private async Task<int> LessonAsync(int classId, int teacherId, int departmentId, string subject, DayOfWeek day = DayOfWeek.Sunday, TimeOnly? start = null, TimeOnly? end = null)
    {
        var slot = await SlotAsync(classId, start, end);
        await using var db = fx.NewContext();
        var lesson = new TimetableAssignment { SchoolClassId = classId, Day = day, TimeSlotId = slot, DepartmentId = departmentId, Subject = subject, TeacherId = teacherId, StartsOn = LessonsFrom };
        db.TimetableAssignments.Add(lesson);
        await db.SaveChangesAsync();
        return lesson.Id;
    }

    private async Task<(int Id, string Phone)> StudentAsync(int classId)
    {
        var (id, _) = await fx.NewStudentAsync(classId);
        var phone = "010" + Random.Shared.Next(10_000_000, 99_999_999);
        await using var db = fx.NewContext();
        await db.Students.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.ParentContactNumber, phone));
        return (id, phone);
    }

    private async Task<(int Schedule, int Round)> ScheduleAsync(int ev, int classId, DateOnly date, TimeOnly roundStart, int capacity = 9)
    {
        await using var db = fx.NewContext();
        var schedule = Ok(await Setup(db).ScheduleClassAsync(ev, classId, date, T8, T14, null));
        var round = Ok(await Setup(db).AddRoundsAsync(schedule, new PtcRoundPlan(roundStart, 1, Math.Max(70, capacity * 7 + 7), capacity, 7, 7)));
        return (schedule, round);
    }

    private async Task PublishedAsync(int ev)
    {
        await using var db = fx.NewContext();
        await db.PtcEvents.Where(e => e.Id == ev).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Published));
    }

    /// <summary>Books through the engine (the same core the parent portal uses) and returns the booking id.</summary>
    private async Task<ServiceResult> BookAsync(int round, (int Id, string Phone) student, params int[] lessons)
    {
        await using var db = fx.NewContext();
        return await Booking(db).BookAsParentAsync(new PtcBookingRequest(round, student.Id, student.Phone, lessons.Select(PtcSubjectOption.LessonKey).ToList()));
    }

    private async Task<List<PtcTeacherInterview>> InterviewsAsync(int teacherId)
    {
        await using var db = fx.NewContext();
        return await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.TeacherStaffMemberId == teacherId && i.Status != PtcInterviewStatus.Cancelled).OrderBy(i => i.ScheduledStart).ToListAsync();
    }

    private static void NoOverlaps(IEnumerable<PtcTeacherInterview> interviews)
    {
        var byBooking = interviews.GroupBy(i => (i.Date, i.ScheduledStart, i.ScheduledEnd, i.PtcParentBookingId)).Select(g => g.First()).OrderBy(i => i.Date).ThenBy(i => i.ScheduledStart).ToList();
        for (var i = 1; i < byBooking.Count; i++)
            if (byBooking[i].Date == byBooking[i - 1].Date)
                Assert.False(PtcRules.Overlaps(byBooking[i].ScheduledStart, byBooking[i].ScheduledEnd, byBooking[i - 1].ScheduledStart, byBooking[i - 1].ScheduledEnd),
                    $"Overlap {byBooking[i - 1].ScheduledStart}-{byBooking[i - 1].ScheduledEnd} / {byBooking[i].ScheduledStart}-{byBooking[i].ScheduledEnd}");
    }

    // ------------------------------------------------------------------ allocation

    [Fact]
    public async Task Each_parent_gets_the_earliest_slot_where_all_chosen_teachers_are_free_together_across_classes()
    {
        var ev = await EventAsync();
        var a = await ClassAsync();
        var b = await ClassAsync();
        var (math, _) = await fx.NewTeacherAsync();
        var (english, _) = await fx.NewTeacherAsync();
        var mathA = await LessonAsync(a, math, 3, "Math");
        var engA = await LessonAsync(a, english, 2, "English", DayOfWeek.Monday);
        var mathB = await LessonAsync(b, math, 3, "Math", DayOfWeek.Monday);
        var (_, roundA) = await ScheduleAsync(ev, a, Day, T8);
        var (_, roundB) = await ScheduleAsync(ev, b, Day, T8);   // same time: Math teacher shared
        var (_, roundOtherDay) = await ScheduleAsync(ev, await ClassWithLessonAsync(math), Day.AddDays(1), T8);
        await PublishedAsync(ev);

        var b1 = Ok(await BookAsync(roundB, await StudentAsync(b), mathB));                      // B slot 1: Math 08:00–08:07
        var a1 = Ok(await BookAsync(roundA, await StudentAsync(a), mathA, engA));                // A: Math busy at 08:00 → slot 2 for both
        var a2 = Ok(await BookAsync(roundA, await StudentAsync(a), engA));                       // A: English only → earliest free slot 1
        var b2 = Ok(await BookAsync(roundB, await StudentAsync(b), mathB));                      // B: slot 2 has Math busy (A) → slot 3
        await using var db = fx.NewContext();
        async Task<(int Slot, TimeOnly Start, TimeOnly End, int Count)> Of(int id)
        {
            var bk = await db.PtcParentBookings.Include(x => x.Interviews).SingleAsync(x => x.Id == id);
            Assert.Single(bk.Interviews.Select(i => (i.ScheduledStart, i.ScheduledEnd)).Distinct());   // parallel: one shared time
            var first = bk.Interviews.First();
            return (bk.SlotNumber, first.ScheduledStart, first.ScheduledEnd, bk.Interviews.Count);
        }
        Assert.Equal((1, At(8, 0), At(8, 7), 1), await Of(b1));
        Assert.Equal((2, At(8, 7), At(8, 14), 2), await Of(a1));                                 // adjacent to b1 for Math: touching is not overlapping
        Assert.Equal((1, At(8, 0), At(8, 7), 1), await Of(a2));
        Assert.Equal((3, At(8, 14), At(8, 21), 1), await Of(b2));
        NoOverlaps(await InterviewsAsync(math));

        // The same teacher on another day, at the same time: allowed.
        var other = await db.PtcRounds.Where(r => r.Id == roundOtherDay).Select(r => r.ClassSchedule.SchoolClassId).SingleAsync();
        var lesson = await db.TimetableAssignments.Where(t => t.SchoolClassId == other).Select(t => t.Id).SingleAsync();
        var d1 = Ok(await BookAsync(roundOtherDay, await StudentAsync(other), lesson));
        Assert.Equal((1, At(8, 0), At(8, 7), 1), await Of(d1));
    }

    private async Task<int> ClassWithLessonAsync(int teacher)
    {
        var cls = await ClassAsync();
        await LessonAsync(cls, teacher, 3, "Math", DayOfWeek.Wednesday);
        return cls;
    }

    [Fact]
    public async Task Duties_workload_periods_and_uncovered_lessons_make_a_teacher_unavailable_but_a_covered_lesson_does_not()
    {
        var ev = await EventAsync();
        var cls = await ClassAsync();
        var (onDuty, _) = await fx.NewTeacherAsync();
        var (withPeriod, _) = await fx.NewTeacherAsync();
        var (teaching, _) = await fx.NewTeacherAsync();
        var (substitute, _) = await fx.NewTeacherAsync();
        var dutyLesson = await LessonAsync(cls, onDuty, 3, "Math");
        var periodLesson = await LessonAsync(cls, withPeriod, 4, "Science");
        var teachingLesson = await LessonAsync(cls, teaching, 2, "English");
        var (schedule, round) = await ScheduleAsync(ev, cls, Day, T8);
        await PublishedAsync(ev);
        await using (var db = fx.NewContext())
        {
            // A dated duty 08:00–08:30; a substitution period 08:00–08:45 placed in that week's Tuesday; a lesson 08:00–08:45.
            db.DutyAssignments.Add(new DutyAssignment { Component = DutyComponent.StudentsArrival, DutyDate = Day, Description = "Gate", StartTime = At(8, 0), EndTime = At(8, 30), PrimaryStaffId = onDuty });
            var periodSlot = new TimeSlot { Number = 950, SchoolClassId = cls, StartTime = At(8, 0), EndTime = At(8, 45), UpdatedAt = DateTime.UtcNow };
            db.TimeSlots.Add(periodSlot);
            await db.SaveChangesAsync();
            db.WorkloadAssignments.Add(new TeacherWorkloadAssignment { TeacherId = withPeriod, WeekStart = SchoolWeek.StartOf(Day), Category = WorkloadCategory.Substitution, Periods = 1, Day = Day.DayOfWeek, TimeSlotId = periodSlot.Id });
            await db.SaveChangesAsync();
        }
        await LessonAsync(await ClassAsync(), teaching, 2, "English", Day.DayOfWeek, At(8, 0), At(8, 45));

        async Task<TimeOnly> FirstStart(int lesson)
        {
            var id = Ok(await BookAsync(round, await StudentAsync(cls), lesson));
            await using var db = fx.NewContext();
            return await db.PtcTeacherInterviews.Where(i => i.PtcParentBookingId == id).Select(i => i.ScheduledStart).SingleAsync();
        }
        Assert.Equal(At(8, 35), await FirstStart(dutyLesson));      // slots 08:00…08:28 overlap the duty (ends 08:30)
        Assert.Equal(At(8, 49), await FirstStart(periodLesson));    // 08:42–08:49 overlaps the period (ends 08:45); 08:35 is taken
        // The English teacher's own lesson 08:00–08:45 (no cover): 08:42 overlaps it, 08:35 and 08:49 are taken → 08:56.
        Assert.Equal(At(8, 56), await FirstStart(teachingLesson));
        await using var check = fx.NewContext();

        // With a PTC substitute covering the lesson, the teacher is available during it.
        Ok(await Setup(check).AssignSubstituteAsync(schedule, teaching, substitute, At(8, 0), At(8, 45), "PTC"));
        var conflicts = await PtcSchedulingRules.TeacherConflictsAsync(check, teaching, Day, At(8, 0), At(8, 7), default);
        Assert.All(conflicts, c => Assert.False(c.Blocks));
    }

    [Fact]
    public async Task Kg_timetable_teachers_are_allocated_together_and_never_overlap_their_other_classes()
    {
        var ev = await EventAsync();
        var kg = await ClassAsync("KG2");
        var other = await ClassAsync();
        var (shared, _) = await fx.NewTeacherAsync();
        var (second, _) = await fx.NewTeacherAsync();
        var lesson = await LessonAsync(other, shared, 2, "English");
        var kgEnglish = await LessonAsync(kg, shared, 2, "English");                               // the same engine as any class: KG teachers from its timetable
        var kgArabic = await LessonAsync(kg, second, 1, "Arabic");
        var (_, kgRound) = await ScheduleAsync(ev, kg, Day, T8);
        var (_, otherRound) = await ScheduleAsync(ev, other, Day, T8);
        await using var db = fx.NewContext();
        await PublishedAsync(ev);

        Ok(await BookAsync(otherRound, await StudentAsync(other), lesson));                      // shared teacher 08:00–08:07 (other class)
        var kgBooking = Ok(await BookAsync(kgRound, await StudentAsync(kg), kgEnglish, kgArabic));
        var interviews = await db.PtcTeacherInterviews.Where(i => i.PtcParentBookingId == kgBooking).ToListAsync();
        Assert.Equal(new[] { shared, second }.Order(), interviews.Select(i => i.TeacherStaffMemberId).Order());
        Assert.All(interviews, i => Assert.Equal((At(8, 7), At(8, 14)), (i.ScheduledStart, i.ScheduledEnd)));   // together, after the shared teacher's 08:00
        NoOverlaps(await InterviewsAsync(shared));
    }

    [Fact]
    public async Task Concurrent_bookings_across_classes_never_overlap_a_shared_teacher_or_exceed_capacity()
    {
        var ev = await EventAsync();
        var a = await ClassAsync();
        var b = await ClassAsync();
        var (math, _) = await fx.NewTeacherAsync();
        var la = await LessonAsync(a, math, 3, "Math");
        var lb = await LessonAsync(b, math, 3, "Math", DayOfWeek.Monday);
        var (_, ra) = await ScheduleAsync(ev, a, Day, T8);
        var (_, rb) = await ScheduleAsync(ev, b, Day, T8);
        await PublishedAsync(ev);
        var parents = new List<(int Round, int Lesson, (int, string) Student)>();
        for (var i = 0; i < 9; i++) { parents.Add((ra, la, await StudentAsync(a))); parents.Add((rb, lb, await StudentAsync(b))); }

        // 18 parents at once, one shared teacher with 9 possible times: exactly 9 succeed, none overlap.
        var results = await Task.WhenAll(parents.Select(p => Task.Run(() => BookAsync(p.Round, p.Student, p.Lesson))));
        Assert.Equal(9, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.Errors, e => e.Key is PtcBookingService.NoCommonSlotField or PtcBookingService.FullField));
        var interviews = await InterviewsAsync(math);
        Assert.Equal(9, interviews.Count);
        NoOverlaps(interviews);
        await using var db = fx.NewContext();
        Assert.True(await db.PtcParentBookings.Where(x => x.PtcRoundId == ra || x.PtcRoundId == rb).GroupBy(x => x.PtcRoundId).AllAsync(g => g.Count() <= 9));
    }

    [Fact]
    public async Task A_failed_allocation_saves_nothing_and_a_cancelled_booking_releases_its_slot_and_teachers()
    {
        var ev = await EventAsync();
        var cls = await ClassAsync();
        var (math, _) = await fx.NewTeacherAsync();
        var (busy, _) = await fx.NewTeacherAsync();
        var mathLesson = await LessonAsync(cls, math, 3, "Math");
        var busyLesson = await LessonAsync(cls, busy, 4, "Science", DayOfWeek.Monday);
        var (_, round) = await ScheduleAsync(ev, cls, Day, T8);
        await PublishedAsync(ev);
        await using var db = fx.NewContext();
        db.DutyAssignments.Add(new DutyAssignment { Component = DutyComponent.StudentsArrival, DutyDate = Day, Description = "All morning", StartTime = At(7, 30), EndTime = At(10, 0), PrimaryStaffId = busy });
        await db.SaveChangesAsync();

        // Math is free but Science's teacher is on duty for the whole Round: no common slot, nothing saved.
        var parent = await StudentAsync(cls);
        var refused = await BookAsync(round, parent, mathLesson, busyLesson);
        Assert.Equal(PtcBookingService.NoCommonSlotField, refused.Errors.Single().Key);
        Assert.Contains("duty", refused.Errors.Single().Value);
        Assert.False(await db.PtcParentBookings.AnyAsync(b => b.StudentId == parent.Id));
        Assert.Empty(await InterviewsAsync(math));

        // A cancelled booking no longer holds its slot or its teacher's time.
        var first = Ok(await BookAsync(round, parent, mathLesson));
        await db.PtcParentBookings.Where(b => b.Id == first).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, PtcBookingStatus.Cancelled).SetProperty(b => b.CancelledAt, DateTime.UtcNow));
        var next = Ok(await BookAsync(round, await StudentAsync(cls), mathLesson));
        var nb = await db.PtcParentBookings.Include(b => b.Interviews).AsNoTracking().SingleAsync(b => b.Id == next);
        Assert.Equal((1, At(8, 0)), (nb.SlotNumber, nb.Interviews.Single().ScheduledStart));
    }

    // ------------------------------------------------------------------ analysis and Admin changes

    [Fact]
    public async Task The_analysis_finds_conflicts_without_changing_anything_and_admin_changes_are_confirmed_audited_and_stale_safe()
    {
        var ev = await EventAsync();
        var cls = await ClassAsync();
        var (math, mathName) = await fx.NewTeacherAsync();
        var (sci, _) = await fx.NewTeacherAsync();
        var mathLesson = await LessonAsync(cls, math, 3, "Math");
        var sciLesson = await LessonAsync(cls, sci, 4, "Science", DayOfWeek.Monday);
        var (_, round) = await ScheduleAsync(ev, cls, Day, T8);
        await PublishedAsync(ev);
        var conflicted = Ok(await BookAsync(round, await StudentAsync(cls), mathLesson));     // slot 1, 08:00
        var unallocated = Ok(await BookAsync(round, await StudentAsync(cls), sciLesson));     // slot 1 for Science? no: slot 2 (slot 1 taken)
        await using var db = fx.NewContext();
        // After booking: Math gets a duty at 08:00–08:05 (a conflict), and the Science interview is lost (unallocated).
        db.DutyAssignments.Add(new DutyAssignment { Component = DutyComponent.StudentsArrival, DutyDate = Day, Description = "Gate", StartTime = At(8, 0), EndTime = At(8, 5), PrimaryStaffId = math });
        await db.SaveChangesAsync();
        await db.PtcTeacherInterviews.Where(i => i.PtcParentBookingId == unallocated).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, PtcInterviewStatus.Cancelled));
        var before = await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.Booking.PtcRoundId == round).Select(i => new { i.Id, i.ScheduledStart, i.Status }).OrderBy(i => i.Id).ToListAsync();

        var analysis = (await Engine(db).AnalyzeAsync(ev))!;
        var hard = Assert.Single(analysis.Conflicts, c => c.Severity == PtcConflictSeverity.Hard);
        Assert.Equal((math, "Teacher busy at interview time"), (hard.TeacherId!.Value, hard.Kind));
        Assert.Contains("duty", hard.Source);
        Assert.Contains(analysis.Conflicts, c => c.Severity == PtcConflictSeverity.AdminAction && c.Kind == "Unallocated interview");
        Assert.Equal(2, analysis.Proposals.Count);
        var move = analysis.Proposals.Single(p => p.BookingId == conflicted);
        Assert.Equal((1, 3, At(8, 14)), (move.OldSlot, move.NewSlot, move.NewStart));        // slot 2 is held by the other booking
        var realign = analysis.Proposals.Single(p => p.BookingId == unallocated);
        Assert.Equal(realign.OldSlot, realign.NewSlot);                                       // allocated at its own time
        // The analysis changed nothing.
        Assert.Equal(before.Select(x => (x.Id, x.ScheduledStart, x.Status)), (await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.Booking.PtcRoundId == round)
            .Select(i => new { i.Id, i.ScheduledStart, i.Status }).OrderBy(i => i.Id).ToListAsync()).Select(x => (x.Id, x.ScheduledStart, x.Status)));

        // Changes need a reason and confirmation; a stale view is refused; a busy slot is refused.
        var engine = Engine(db);
        Fails(await engine.ReassignSlotAsync(conflicted, 3, 1, "Duty", confirmed: false), "Confirm the change");
        Fails(await engine.ReassignSlotAsync(conflicted, 3, 1, "", confirmed: true), "reason");
        Fails(await engine.ReassignSlotAsync(conflicted, 3, 2, "Duty", confirmed: true), "changed since it was shown");
        Fails(await engine.ReassignSlotAsync(conflicted, 2, 1, "Duty", confirmed: true), "taken");
        Fails(await engine.ApplyProposalsAsync(ev, "stale", "Duty", confirmed: true), "changed since it was shown");

        Ok(await engine.ApplyProposalsAsync(ev, analysis.Fingerprint, "Gate duty added", confirmed: true));
        var moved = await db.PtcParentBookings.AsNoTracking().Include(b => b.Interviews).Include(b => b.Subjects).SingleAsync(b => b.Id == conflicted);
        Assert.Equal((3, At(8, 14)), (moved.SlotNumber, moved.Interviews.Single().ScheduledStart));
        Assert.Equal(before.Single(x => x.Status != PtcInterviewStatus.Cancelled && moved.Interviews.Any(i => i.Id == x.Id)).Id, moved.Interviews.Single().Id);   // same row, moved
        var restored = await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.PtcParentBookingId == unallocated && i.Status == PtcInterviewStatus.Scheduled).ToListAsync();
        Assert.Single(restored);                                                              // allocated once, no duplicate

        var history = await engine.HistoryAsync(ev);
        Assert.Equal(2, history.Count);
        var h = history.Single(x => x.OldSlot == 1 && x.NewSlot == 3);
        Assert.Equal((At(8, 0), At(8, 14), "Gate duty added", "Test runner", mathName), (h.OldStart, h.NewStart, h.Reason, h.By, h.Teachers));
        Assert.Empty((await engine.AnalyzeAsync(ev))!.Conflicts.Where(c => c.Severity != PtcConflictSeverity.Restriction));
    }

    [Fact]
    public async Task A_conflict_with_no_safe_slot_is_reported_and_leaves_the_schedule_unchanged()
    {
        var ev = await EventAsync();
        var cls = await ClassAsync();
        var (math, _) = await fx.NewTeacherAsync();
        var lesson = await LessonAsync(cls, math, 3, "Math");
        var (_, round) = await ScheduleAsync(ev, cls, Day, T8);
        await PublishedAsync(ev);
        var booking = Ok(await BookAsync(round, await StudentAsync(cls), lesson));
        await using var db = fx.NewContext();
        db.DutyAssignments.Add(new DutyAssignment { Component = DutyComponent.StudentsArrival, DutyDate = Day, Description = "All day", StartTime = At(7, 0), EndTime = At(15, 0), PrimaryStaffId = math });
        await db.SaveChangesAsync();

        var analysis = (await Engine(db).AnalyzeAsync(ev))!;
        var conflict = Assert.Single(analysis.Conflicts, c => c.Severity == PtcConflictSeverity.Hard);
        Assert.Null(conflict.SlotMove);
        Assert.Contains("left unchanged", conflict.Suggestion);
        Assert.Empty(analysis.Proposals);
        Fails(await Engine(db).ApplyProposalsAsync(ev, analysis.Fingerprint, "Try", confirmed: true), "nothing to apply");
        Fails(await Engine(db).ReassignSlotAsync(booking, 4, 1, "Try", confirmed: true), "not free");
        var b = await db.PtcParentBookings.AsNoTracking().Include(x => x.Interviews).SingleAsync(x => x.Id == booking);
        Assert.Equal((1, At(8, 0)), (b.SlotNumber, b.Interviews.Single().ScheduledStart));
        Assert.Empty(await Engine(db).HistoryAsync(ev));
    }

    [Fact]
    public async Task Moving_a_round_needs_confirmation_shifts_its_bookings_safely_and_is_audited()
    {
        var ev = await EventAsync();
        var cls = await ClassAsync();
        var (math, _) = await fx.NewTeacherAsync();
        var lesson = await LessonAsync(cls, math, 3, "Math");
        var (schedule, round) = await ScheduleAsync(ev, cls, Day, T8);
        await using var db = fx.NewContext();
        Ok(await Setup(db).AddRoundsAsync(schedule, new PtcRoundPlan(At(10, 0), 1, 70, 9, 7, 7)));
        await PublishedAsync(ev);
        db.ChangeTracker.Clear();   // published through another context (each request has its own context in the app)
        var booking = Ok(await BookAsync(round, await StudentAsync(cls), lesson));
        db.DutyAssignments.Add(new DutyAssignment { Component = DutyComponent.StudentsArrival, DutyDate = Day, Description = "Break", StartTime = At(11, 30), EndTime = At(11, 45), PrimaryStaffId = math });
        await db.SaveChangesAsync();
        var engine = Engine(db);

        Fails(await engine.MoveRoundAsync(round, At(8, 30), T8, "Later start", confirmed: false), "Confirm the change");
        Fails(await engine.MoveRoundAsync(round, At(9, 30), T8, "Later start", confirmed: true), "overlap Round");
        Fails(await engine.MoveRoundAsync(round, At(11, 30), T8, "Later start", confirmed: true), "duty");          // the booked teacher is on duty then
        Fails(await engine.MoveRoundAsync(round, At(8, 30), At(9, 0), "Later start", confirmed: true), "changed since it was shown");
        Assert.Equal(At(8, 0), (await db.PtcTeacherInterviews.AsNoTracking().SingleAsync(i => i.PtcParentBookingId == booking)).ScheduledStart);

        // A Published event never gets an uncovered lesson during a Round.
        await LessonAsync(await ClassAsync(), math, 3, "Math", Day.DayOfWeek, At(12, 0), At(12, 45));
        Fails(await engine.MoveRoundAsync(round, At(11, 50), T8, "Later start", confirmed: true), "uncovered lesson");

        Ok(await engine.MoveRoundAsync(round, At(8, 30), T8, "Hall not ready at 08:00", confirmed: true));
        var r = await db.PtcRounds.AsNoTracking().SingleAsync(x => x.Id == round);
        Assert.Equal((At(8, 30), At(9, 40)), (r.StartTime, r.EndTime));
        var shifted = await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.PtcParentBookingId == booking).Select(i => new { i.ScheduledStart, i.ScheduledEnd }).SingleAsync();
        Assert.Equal((At(8, 30), At(8, 37)), (shifted.ScheduledStart, shifted.ScheduledEnd));
        var history = await engine.HistoryAsync(ev);
        Assert.Equal(2, history.Count);                                                            // the Round and its booking
        Assert.All(history, h => Assert.Equal((PtcScheduleChangeKind.RoundMoved, "Hall not ready at 08:00"), (h.Kind, h.Reason)));
        Assert.Contains(history, h => h.Reference is not null && h.OldStart == At(8, 0) && h.NewStart == At(8, 30));
    }

    [Fact]
    public async Task Shared_teachers_in_overlapping_rounds_and_uncovered_lessons_are_reported_with_conflict_free_suggestions()
    {
        var ev = await EventAsync();
        var a = await ClassAsync();
        var b = await ClassAsync();
        var empty = await ClassAsync();
        var (math, _) = await fx.NewTeacherAsync();
        var (eng, _) = await fx.NewTeacherAsync();
        await LessonAsync(a, math, 3, "Math");
        await LessonAsync(b, math, 3, "Math", DayOfWeek.Monday);
        await LessonAsync(b, eng, 2, "English", DayOfWeek.Wednesday);
        await LessonAsync(await ClassAsync(), eng, 2, "English", Day.DayOfWeek, At(8, 0), At(8, 45));   // English teaches elsewhere 08:00–08:45
        await ScheduleAsync(ev, a, Day, T8);
        var (_, roundB) = await ScheduleAsync(ev, b, Day, T8);
        await ScheduleAsync(ev, empty, Day, T8);
        await using var db = fx.NewContext();
        var analysis = (await Engine(db).AnalyzeAsync(ev))!;

        var shared = Assert.Single(analysis.Conflicts, c => c.Kind == "Shared teacher in overlapping Rounds");
        Assert.Equal((PtcConflictSeverity.Restriction, math), (shared.Severity, shared.TeacherId!.Value));
        Assert.NotNull(shared.RoundMove);
        var lesson = Assert.Single(analysis.Conflicts, c => c.Kind == "Uncovered lesson during a Round");
        Assert.Equal((PtcConflictSeverity.Hard, eng, roundB), (lesson.Severity, lesson.TeacherId!.Value, lesson.RoundId!.Value));
        var suggestion = lesson.RoundMove!;
        Assert.True(suggestion.NewStart >= At(8, 45), $"Suggested {suggestion.NewStart} still overlaps the lesson");
        Assert.True(suggestion.NewStart >= At(9, 10), "The suggestion must not overlap class A's Round with the shared Math teacher");
        Assert.Contains(analysis.Teachers, t => t.TeacherId == math && t.MultiClass);
        // A class without a timetable has no invented teachers.
        Assert.Empty(analysis.Classes.Single(c => c.ClassId == empty).Teachers);
        Fails(await Setup(db).PublishAsync(ev, true), "no PTC substitute covers it");
    }

    [Fact]
    public async Task Scheduling_pages_are_super_admin_only()
    {
        var ev = await EventAsync();
        var cls = await ClassAsync();
        var (math, _) = await fx.NewTeacherAsync();
        await LessonAsync(cls, math, 3, "Math");
        await ScheduleAsync(ev, cls, Day, T8);
        var (staff, _) = await fx.NewTeacherAsync();
        var asStaff = fx.ClientAs(fx.Factory, staff, follow: false);
        var asAdmin = fx.Factory.CreateClient();
        foreach (var page in new[] { $"/ptc/events/{ev}/scheduling", $"/ptc/events/{ev}/scheduling?view=teachers", $"/ptc/events/{ev}/scheduling?view=conflicts",
                     $"/ptc/events/{ev}/scheduling?view=history", $"/ptc/events/{ev}/scheduling/teachers/{math}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await asStaff.GetAsync(page)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await asAdmin.GetAsync(page)).StatusCode);
        }
        Assert.NotEqual(HttpStatusCode.OK, (await asStaff.PostAsync($"/ptc/rounds/1/move", new FormUrlEncodedContent([]))).StatusCode);
    }
}
