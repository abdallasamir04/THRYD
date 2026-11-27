using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Tests;

/// <summary>
/// PTC Phase 1 foundation: defaults, Round capacity (also under concurrent requests), duplicate bookings, parent / student /
/// class / subject-teacher validation, parallel interviews, teacher overlaps across classes and days, lesson cover by a
/// manual substitute, and the Super Admin boundary. Runs on the test database only.
/// </summary>
[Collection("db")]
public class PtcFoundationTests(DbFixture fx)
{
    /// <summary>A Tuesday (a school day). Lessons of the scenarios are on Sunday / Monday unless a test needs a clash.</summary>
    private static readonly DateOnly Day = new(2027, 3, 9);
    private static readonly DateOnly LessonsFrom = SchoolWeek.StartOf(new DateOnly(2026, 9, 6));

    private sealed class Admin : IPtcAccess { public bool CanManage => true; }
    private sealed class NotAdmin : IPtcAccess { public bool CanManage => false; }
    private sealed class User : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static PtcSetupService Setup(SchoolDbContext db, IPtcAccess? access = null) =>
        new(db, access ?? new Admin(), new SettingsService(db, new MemoryCache(new MemoryCacheOptions())), new User());

    private static PtcBookingService Booking(SchoolDbContext db, IPtcAccess? access = null) => new(db, access ?? new Admin(), new User());

    private static int Ok(ServiceResult r)
    {
        Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));
        return r.EntityId!.Value;
    }

    private static void Fails(ServiceResult r, string text)
    {
        Assert.False(r.Succeeded, "Expected a failure containing: " + text);
        Assert.Contains(r.Errors, e => e.Value.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ helpers (test database only)

    private async Task<int> NewEventAsync()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "PTC " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        return Ok(await Setup(db).SaveEventAsync(null, new PtcEventInput("Q2 PTC " + DbFixture.Unique(), null, year.Id, Day, Day.AddDays(6))));
    }

    /// <summary>
    /// Test shortcut: marks the event Published directly in the test database so these booking-rule tests can book (only a
    /// Published event is bookable). The publishing rules themselves are tested in <see cref="PtcAdminTests"/>.
    /// </summary>
    private async Task PublishedAsync(int eventId)
    {
        await using var db = fx.NewContext();
        await db.PtcEvents.Where(e => e.Id == eventId).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Published));
    }

    private async Task<int> ScheduleAsync(int eventId, int classId, DateOnly date, int fromHour = 8, int toHour = 16)
    {
        await using var db = fx.NewContext();
        return Ok(await Setup(db).ScheduleClassAsync(eventId, classId, date, new TimeOnly(fromHour, 0), new TimeOnly(toHour, 0), null));
    }

    private async Task<int> RoundAsync(int scheduleId, TimeOnly start)
    {
        await using var db = fx.NewContext();
        return Ok(await Setup(db).AddRoundAsync(scheduleId, start));
    }

    /// <summary>A student of the class with a parent contact number on record.</summary>
    private async Task<(int Id, string Phone)> StudentAsync(int classId, bool withPhone = true)
    {
        var (id, _) = await fx.NewStudentAsync(classId);
        var phone = "010" + Random.Shared.Next(10_000_000, 99_999_999);
        if (withPhone)
        {
            await using var db = fx.NewContext();
            var s = await db.Students.SingleAsync(x => x.Id == id);
            s.ParentContactNumber = phone;
            await db.SaveChangesAsync();
        }
        return (id, phone);
    }

    /// <summary>A timetable lesson (the class-subject-teacher assignment PTC resolves teachers from).</summary>
    private async Task<int> LessonAsync(int classId, int teacherId, int departmentId, string subject, DayOfWeek day, TimeOnly? start = null, TimeOnly? end = null)
    {
        await using var db = fx.NewContext();
        var slot = new TimeSlot { Number = Random.Shared.Next(100, 999), SchoolClassId = classId, StartTime = start, EndTime = end, UpdatedAt = DateTime.UtcNow };
        db.TimeSlots.Add(slot);
        await db.SaveChangesAsync();
        var lesson = new TimetableAssignment
        {
            SchoolClassId = classId, Day = day, TimeSlotId = slot.Id, DepartmentId = departmentId, Subject = subject,
            TeacherId = teacherId, StartsOn = LessonsFrom,
        };
        db.TimetableAssignments.Add(lesson);
        await db.SaveChangesAsync();
        return lesson.Id;
    }

    private async Task<ServiceResult> BookAsync(int roundId, int studentId, string phone, params int[] lessons)
    {
        await using var db = fx.NewContext();
        return await Booking(db).BookAsync(new PtcBookingRequest(roundId, studentId, phone, lessons.Select(PtcSubjectOption.LessonKey).ToList()));
    }

    // ------------------------------------------------------------------ defaults and pure rules

    [Fact]
    public async Task Defaults_are_9_parents_7_minute_meetings_7_minute_transition_70_minute_rounds()
    {
        var d = PtcRoundDefaults.Initial;
        Assert.Equal((9, 7, 7, 70), (d.Capacity, d.MeetingDurationMinutes, d.TransitionDurationMinutes, d.RoundDurationMinutes));
        Assert.Equal(70, PtcRules.PlannedRoundMinutes(9, 7, 7));

        await using var db = fx.NewContext();
        var settings = DbFixture.Settings(db);
        Assert.Equal(d, await settings.GetPtcDefaultsAsync());
        // Values are validated independently: a Round must hold every meeting (nothing is saved here).
        Fails(await settings.SavePtcDefaultsAsync(d with { RoundDurationMinutes = 60 }), "cannot hold 9 meetings");
        Fails(await settings.SavePtcDefaultsAsync(d with { Capacity = 0 }), "capacity");

        // A new Round gets the defaults: 08:00–09:10.
        var schedule = await ScheduleAsync(await NewEventAsync(), await fx.NewClassAsync(), Day);
        var roundId = await RoundAsync(schedule, new TimeOnly(8, 0));
        var round = await db.PtcRounds.AsNoTracking().SingleAsync(r => r.Id == roundId);
        Assert.Equal((9, 7, 7, 1), (round.Capacity, round.MeetingDurationMinutes, round.TransitionDurationMinutes, round.RoundNumber));
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(9, 10)), (round.StartTime, round.EndTime));
    }

    [Fact]
    public void Overlap_rule_and_slot_windows()
    {
        TimeOnly T(int h, int m) => new(h, m);
        Assert.True(PtcRules.Overlaps(T(8, 0), T(8, 7), T(8, 5), T(8, 12)));
        Assert.False(PtcRules.Overlaps(T(8, 0), T(8, 7), T(8, 7), T(8, 14)));   // touching is not overlapping
        Assert.Equal((T(8, 0), T(8, 7)), PtcRules.SlotWindow(T(8, 0), 1, 7));
        Assert.Equal((T(8, 56), T(9, 3)), PtcRules.SlotWindow(T(8, 0), 9, 7));   // slot 9 ends 07 min before the 70-minute Round ends
        Assert.True(PtcRules.IsStudentParent("01012345678", "+20 101 234 5678"));
        Assert.False(PtcRules.IsStudentParent("01012345678", "01012345679"));
        Assert.False(PtcRules.IsStudentParent(null, "01012345678"));
    }

    // ------------------------------------------------------------------ booking rules

    [Fact]
    public async Task Booking_with_several_subjects_creates_parallel_interviews_with_the_actual_teachers()
    {
        var cls = await fx.NewClassAsync();
        var (math, _) = await fx.NewTeacherAsync();
        var (science, _) = await fx.NewTeacherAsync();
        var mathLesson = await LessonAsync(cls, math, 3, "Math", DayOfWeek.Sunday);
        var scienceLesson = await LessonAsync(cls, science, 4, "Science", DayOfWeek.Monday);
        var ev = await NewEventAsync();
        var round = await RoundAsync(await ScheduleAsync(ev, cls, Day), new TimeOnly(8, 0));
        await PublishedAsync(ev);
        var (s1, p1) = await StudentAsync(cls);
        var (s2, p2) = await StudentAsync(cls);

        await using (var db = fx.NewContext())
        {
            var options = await Booking(db).SubjectOptionsAsync(round);
            Assert.Equal(new[] { (3, math), (4, science) }, options.Select(o => (o.DepartmentId, o.TeacherId)).ToList());
        }

        var b1 = Ok(await BookAsync(round, s1, p1, mathLesson, scienceLesson));
        var b2 = Ok(await BookAsync(round, s2, "+2" + p2, mathLesson));   // the same number written internationally

        await using var check = fx.NewContext();
        var booking = await check.PtcParentBookings.AsNoTracking().Include(b => b.Subjects).Include(b => b.Interviews).SingleAsync(b => b.Id == b1);
        Assert.Equal(PtcBookingStatus.Confirmed, booking.Status);                // direct booking: no approval step
        Assert.Equal(1, booking.SlotNumber);
        Assert.StartsWith("PTC-", booking.BookingReference);
        Assert.Equal(2, booking.Subjects.Count);
        Assert.Equal(new[] { math, science }.Order(), booking.Interviews.Select(i => i.TeacherStaffMemberId).Order().ToList());
        // Parallel: both teachers meet the parent in the same slot (08:00–08:07), not one after the other.
        Assert.All(booking.Interviews, i => Assert.Equal((Day, new TimeOnly(8, 0), new TimeOnly(8, 7)), (i.Date, i.ScheduledStart, i.ScheduledEnd)));
        Assert.All(booking.Interviews, i => Assert.Null(i.ActualStart));
        Assert.Equal(mathLesson, booking.Subjects.Single(s => s.AssignedTeacherStaffMemberId == math).SourceTimetableAssignmentId);

        var second = await check.PtcTeacherInterviews.AsNoTracking().SingleAsync(i => i.PtcParentBookingId == b2);
        Assert.Equal((new TimeOnly(8, 7), new TimeOnly(8, 14)), (second.ScheduledStart, second.ScheduledEnd));
    }

    [Fact]
    public async Task Round_capacity_holds_under_concurrent_requests_and_duplicates_are_rejected()
    {
        var cls = await fx.NewClassAsync();
        var (teacher, _) = await fx.NewTeacherAsync();
        var lesson = await LessonAsync(cls, teacher, 1, "Arabic", DayOfWeek.Sunday);
        var ev = await NewEventAsync();
        var round = await RoundAsync(await ScheduleAsync(ev, cls, Day), new TimeOnly(8, 0));
        await PublishedAsync(ev);
        var students = new List<(int Id, string Phone)>();
        for (var i = 0; i < 12; i++) students.Add(await StudentAsync(cls));

        // 12 parents book the 9-place Round at the same moment.
        var results = await Task.WhenAll(students.Select(s => Task.Run(() => BookAsync(round, s.Id, s.Phone, lesson))));
        Assert.Equal(9, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Fails(r, "full"));

        await using var db = fx.NewContext();
        var slots = await db.PtcParentBookings.Where(b => b.PtcRoundId == round).Select(b => b.SlotNumber).OrderBy(n => n).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 9), slots);
        var interviews = await db.PtcTeacherInterviews.Where(i => i.TeacherStaffMemberId == teacher).OrderBy(i => i.ScheduledStart).ToListAsync();
        Assert.Equal(9, interviews.Count);
        Assert.All(interviews.Zip(interviews.Skip(1)), p => Assert.False(PtcRules.Overlaps(p.Second.ScheduledStart, p.Second.ScheduledEnd, p.First.ScheduledStart, p.First.ScheduledEnd)));

        // The same student again in the Round.
        var booked = await db.PtcParentBookings.Where(b => b.PtcRoundId == round).Select(b => b.StudentId).FirstAsync();
        Fails(await BookAsync(round, booked, students.Single(s => s.Id == booked).Phone, lesson), "already has a booking");

        // Database backstop: a second active booking of a taken slot is refused by the unique index.
        db.PtcParentBookings.Add(new PtcParentBooking
        {
            PtcRoundId = round, PtcClassScheduleId = await db.PtcRounds.Where(r => r.Id == round).Select(r => r.PtcClassScheduleId).SingleAsync(), StudentId = students[^1].Id, ParentContact = "201000000000", BookingReference = PtcRules.NewReference(), SlotNumber = 1,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(DbErrors.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task Parent_student_class_and_subject_are_validated()
    {
        var cls = await fx.NewClassAsync();
        var other = await fx.NewClassAsync();
        var (teacher, _) = await fx.NewTeacherAsync();
        var (otherTeacher, _) = await fx.NewTeacherAsync();
        var lesson = await LessonAsync(cls, teacher, 2, "English", DayOfWeek.Sunday);
        var sameSubjectAgain = await LessonAsync(cls, teacher, 2, "English", DayOfWeek.Monday);
        var otherClassLesson = await LessonAsync(other, otherTeacher, 3, "Math", DayOfWeek.Sunday);
        var ev = await NewEventAsync();
        var round = await RoundAsync(await ScheduleAsync(ev, cls, Day), new TimeOnly(8, 0));
        await PublishedAsync(ev);
        var (student, phone) = await StudentAsync(cls);
        var (noPhone, _) = await StudentAsync(cls, withPhone: false);
        var (elsewhere, elsewherePhone) = await StudentAsync(other);

        Fails(await BookAsync(round, student, "01099999999", lesson), "not the student's parent");
        Fails(await BookAsync(round, noPhone, "01099999999", lesson), "no parent contact number");
        Fails(await BookAsync(round, elsewhere, elsewherePhone, lesson), "not in the class");
        Fails(await BookAsync(round, student, phone, otherClassLesson), "not offered to this class");
        Fails(await BookAsync(round, student, phone, lesson, sameSubjectAgain), "chosen twice");
        Fails(await BookAsync(round, student, phone), "at least one subject");
        Fails(await BookAsync(round, 0, phone, lesson), "does not exist");

        await using var db = fx.NewContext();
        Assert.False(await db.PtcParentBookings.AnyAsync(b => b.PtcRoundId == round));   // nothing was written
        Assert.Empty(await PtcSchedulingRules.ClassSubjectsAsync(db, await fx.NewClassAsync(), Day, default)); // no timetable: no invented subjects
    }

    [Fact]
    public async Task A_teacher_never_has_overlapping_interviews_but_can_join_other_classes_at_other_times_and_days()
    {
        var (teacher, _) = await fx.NewTeacherAsync();
        var ev = await NewEventAsync();
        var a = await fx.NewClassAsync();
        var b = await fx.NewClassAsync();
        var c = await fx.NewClassAsync();
        var la = await LessonAsync(a, teacher, 4, "Science", DayOfWeek.Sunday);
        var lb = await LessonAsync(b, teacher, 4, "Science", DayOfWeek.Monday);
        var lc = await LessonAsync(c, teacher, 4, "Science", DayOfWeek.Wednesday);
        var roundA = await RoundAsync(await ScheduleAsync(ev, a, Day), new TimeOnly(8, 0));
        var scheduleB = await ScheduleAsync(ev, b, Day);
        var roundB = await RoundAsync(scheduleB, new TimeOnly(8, 0));
        var roundBLater = await RoundAsync(scheduleB, new TimeOnly(9, 10));
        var roundC = await RoundAsync(await ScheduleAsync(ev, c, Day.AddDays(1)), new TimeOnly(8, 0));
        var sa = await StudentAsync(a);
        var sb = await StudentAsync(b);
        var sb2 = await StudentAsync(b);
        var sc = await StudentAsync(c);

        // The class is scheduled once per event; Rounds of a schedule do not overlap (checked while the event is a Draft).
        await using (var db = fx.NewContext())
        {
            Fails(await Setup(db).ScheduleClassAsync(ev, a, Day.AddDays(2), new TimeOnly(8, 0), new TimeOnly(12, 0), null), "already scheduled");
            Fails(await Setup(db).AddRoundAsync(scheduleB, new TimeOnly(8, 30)), "overlaps Round");
        }
        await PublishedAsync(ev);

        Ok(await BookAsync(roundA, sa.Id, sa.Phone, la));                              // 08:00–08:07, class A
        // Class B, same teacher, same Round time: the engine never overlaps the teacher; this parent gets slot 2 (08:07–08:14).
        var shifted = Ok(await BookAsync(roundB, sb.Id, sb.Phone, lb));
        await using (var check = fx.NewContext())
        {
            var i = await check.PtcTeacherInterviews.SingleAsync(x => x.PtcParentBookingId == shifted);
            Assert.Equal((new TimeOnly(8, 7), new TimeOnly(8, 14)), (i.ScheduledStart, i.ScheduledEnd));
        }
        Ok(await BookAsync(roundBLater, sb2.Id, sb2.Phone, lb));                       // 09:10–09:17, same day: allowed
        Ok(await BookAsync(roundC, sc.Id, sc.Phone, lc));                              // 08:00 next day: allowed
    }

    [Fact]
    public async Task A_lesson_at_the_same_time_blocks_until_a_manual_substitute_covers_it()
    {
        var cls = await fx.NewClassAsync();
        var taught = await fx.NewClassAsync();
        var (teacher, _) = await fx.NewTeacherAsync();
        var (substitute, _) = await fx.NewTeacherAsync();
        var lesson = await LessonAsync(cls, teacher, 1, "Arabic", DayOfWeek.Sunday);
        // The teacher has a lesson in another class on the PTC weekday, 13:00–14:15.
        // (The lesson spans the whole Round, so no slot of the Round avoids it.)
        await LessonAsync(taught, teacher, 1, "Arabic", Day.DayOfWeek, new TimeOnly(13, 0), new TimeOnly(14, 15));
        var ev = await NewEventAsync();
        var schedule = await ScheduleAsync(ev, cls, Day);
        var round = await RoundAsync(schedule, new TimeOnly(13, 0));
        await PublishedAsync(ev);
        var (student, phone) = await StudentAsync(cls);

        Fails(await BookAsync(round, student, phone, lesson), "no PTC substitute covers it");

        await using (var db = fx.NewContext())
        {
            var setup = Setup(db);
            Fails(await setup.AssignSubstituteAsync(schedule, teacher, teacher, new TimeOnly(13, 0), new TimeOnly(14, 15), null), "themselves");
            Fails(await setup.AssignSubstituteAsync(schedule, teacher, 0, new TimeOnly(13, 0), new TimeOnly(14, 15), null), "existing staff member");
            Ok(await setup.AssignSubstituteAsync(schedule, teacher, substitute, new TimeOnly(13, 0), new TimeOnly(14, 15), "PTC cover"));
        }
        Ok(await BookAsync(round, student, phone, lesson));

        await using var check = fx.NewContext();
        var cover = await check.PtcSubstituteAssignments.AsNoTracking().SingleAsync(x => x.PtcClassScheduleId == schedule);
        Assert.Equal((teacher, substitute, PtcSubstituteStatus.Active, "Test runner"),
            (cover.OriginalTeacherStaffMemberId, cover.SubstituteTeacherStaffMemberId, cover.Status, cover.AssignedBy));
    }

    [Fact]
    public async Task Only_the_super_admin_can_manage_ptc()
    {
        await using var db = fx.NewContext();
        await Assert.ThrowsAsync<PtcAccessDeniedException>(() => Setup(db, new NotAdmin()).SaveEventAsync(null, new PtcEventInput("PTC", null, 1, Day, Day)));
        await Assert.ThrowsAsync<PtcAccessDeniedException>(() => Booking(db, new NotAdmin()).BookAsync(new PtcBookingRequest(1, 1, "01012345678", ["L1"])));
    }
}
