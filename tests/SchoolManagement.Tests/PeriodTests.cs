using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Dynamic periods: class-only / all-classes scope, weekday selection, validation, safe edit / delete, lesson moves,
/// conflicts across different period structures, workload and session attendance. Each test uses its own new classes
/// and late-afternoon times (the ten original periods have no times in the test database); school-wide periods a test
/// adds are deleted again, so other tests keep seeing the original ten.
/// </summary>
[Collection("db")]
public class PeriodTests(DbFixture fx)
{
    private const int Math = 3;
    private const int PeDepartment = 8;

    private static readonly DayOfWeek Sun = DayOfWeek.Sunday, Mon = DayOfWeek.Monday, Tue = DayOfWeek.Tuesday, Wed = DayOfWeek.Wednesday;

    private static PeriodInput P(string? name, (int H, int M) start, (int H, int M) end, params DayOfWeek[] days) =>
        new(name, new TimeOnly(start.H, start.M), new TimeOnly(end.H, end.M), null, true, days.Length == 0, days);

    private static async Task<PeriodOutcome> Confirmed(Func<string?, Task<PeriodOutcome>> op)
    {
        var first = await op(null);
        return first.NeedsConfirmation ? await op(first.Plan!.Fingerprint) : first;
    }

    private static void Ok(PeriodOutcome o) => Assert.True(o.Result.Succeeded, string.Join(" | ", o.Result.Errors.Select(e => e.Value)));
    private static void Ok(ServiceResult r) => Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));

    private async Task<int> TeacherAsync(int departmentId = Math)
    {
        await using var db = fx.NewContext();
        var r = await DbFixture.Teachers(db).CreateAsync("Teacher " + DbFixture.Unique(), "Teacher", null, departmentId, null, null);
        Ok(r);
        return r.EntityId!.Value;
    }

    private static SessionInput Lesson(int classId, DayOfWeek day, int slot, int teacher, int dept = Math, string subject = "Math") =>
        new(classId, day, slot, dept, subject, teacher, null);

    private async Task<int> AddForClassAsync(int classId, PeriodInput input)
    {
        await using var db = fx.NewContext();
        var o = await DbFixture.Periods(db).AddAsync(input, PeriodScope.ThisClass, classId, null);
        Ok(o);
        return o.Result.EntityId!.Value;
    }

    private async Task DeleteSchoolWideAsync(int id)
    {
        await using var db = fx.NewContext();
        await db.TimetableAssignments.Where(t => t.TimeSlotId == id && t.EndedOn == null).ExecuteUpdateAsync(u => u.SetProperty(t => t.EndedOn, SchoolWeek.Current));
        var periods = DbFixture.Periods(db);
        if (await db.TimeSlots.AnyAsync(s => s.Id == id && s.ArchivedAt == null))
            Ok(await Confirmed(c => periods.DeleteAsync(id, PeriodScope.AllClasses, null, true, c)));
    }

    // ------------------------------------------------------------------ 1, 2, 19 — one class only

    [Fact]
    public async Task A_period_added_to_one_class_only_leaves_other_classes_unchanged()
    {
        var (a, b) = (await fx.NewClassAsync(), await fx.NewClassAsync());
        await using var db = fx.NewContext();
        var before = (await DbFixture.Periods(db).MapAsync()).Capacity(b);

        var outcome = await DbFixture.Periods(db).AddAsync(P("Period 11", (15, 0), (15, 45)), PeriodScope.ThisClass, a, null);
        Ok(outcome); // one class: no confirmation step needed
        var map = await DbFixture.Periods(db).MapAsync();
        Assert.Contains(map.ForClass(a), s => s.Label == "Period 11" && s.ClassId == a);
        Assert.DoesNotContain(map.ForClass(b), s => s.Label == "Period 11");
        Assert.Equal(before, map.Capacity(b));
        Assert.Equal(map.Capacity(b) + 5, map.Capacity(a)); // different period counts per class
        Assert.Equal(Enumerable.Range(1, 10), (await DbFixture.Timetable(db).SlotsAsync()).Select(s => s.Number)); // school-wide set unchanged
    }

    // ------------------------------------------------------------------ 3, 4, 7, 14 — all classes

    [Fact]
    public async Task A_period_added_to_all_classes_needs_confirmation_and_keeps_existing_lessons()
    {
        var a = await fx.NewClassAsync();
        var teacher = await TeacherAsync();
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        var periods = DbFixture.Periods(db);
        Ok(await tt.AssignAsync(Lesson(a, Sun, 2, teacher)));
        var lessonsBefore = await db.TimetableAssignments.CountAsync();
        var slotsBefore = await db.TimeSlots.CountAsync();
        int? id = null;
        try
        {
            var preview = await periods.AddAsync(P("Club", (18, 0), (18, 30)), PeriodScope.AllClasses, null, null);
            Assert.True(preview.NeedsConfirmation);
            Assert.Equal(await db.Classes.CountAsync(), preview.Plan!.Classes.Count); // the plan names every affected class
            Assert.Equal(slotsBefore, await db.TimeSlots.CountAsync()); // nothing saved before confirming
            Assert.False((await periods.AddAsync(P("Club", (18, 0), (18, 30)), PeriodScope.AllClasses, null, "stale")).Result.Succeeded);

            var applied = await periods.AddAsync(P("Club", (18, 0), (18, 30)), PeriodScope.AllClasses, null, preview.Plan.Fingerprint);
            Ok(applied);
            id = applied.Result.EntityId;
            var map = await periods.MapAsync();
            Assert.All(map.Classes, c => Assert.Contains(map.ForClass(c.Id), s => s.Id == id));
            Assert.True(map.Get(id!.Value)!.AllDays); // all school days
            Assert.All(ScheduleRules.Days, d => Assert.True(map.Get(id.Value)!.AppliesOn(d)));
            Assert.Equal(lessonsBefore, await db.TimetableAssignments.CountAsync());
            Assert.NotNull((await tt.ClassGridAsync(a, SchoolWeek.Current)).Session(Sun, 2));

            // Editing it for all classes: confirmed plan, applied in place everywhere.
            Ok(await Confirmed(c => periods.EditAsync(id.Value, P("Club time", (18, 5), (18, 35)), PeriodScope.AllClasses, null, c)));
            map = await periods.MapAsync();
            Assert.All(map.Classes, c => Assert.Contains(map.ForClass(c.Id), s => s.Id == id && s.Label == "Club time" && s.Start == new TimeOnly(18, 5)));
        }
        finally
        {
            if (id is { } x) await DeleteSchoolWideAsync(x);
        }
    }

    // ------------------------------------------------------------------ 5, 6, 20 — selected days

    [Fact]
    public async Task Periods_run_only_on_their_selected_days()
    {
        var cls = await fx.NewClassAsync();
        var teacher = await TeacherAsync();
        var sundayOnly = await AddForClassAsync(cls, P("Sunday extra", (15, 0), (15, 30), Sun));
        var sunTue = await AddForClassAsync(cls, P("Reading", (15, 30), (16, 0), Sun, Tue));
        await using var db = fx.NewContext();
        var map = await DbFixture.Periods(db).MapAsync();
        Assert.Equal("Sun", map.Get(sundayOnly)!.DaysLabel);
        Assert.Equal("Sun, Tue", map.Get(sunTue)!.DaysLabel);
        Assert.Equal(map.ForClassDay(cls, Mon).Count + 2, map.ForClassDay(cls, Sun).Count); // different days, different periods
        Assert.Equal(map.ForClassDay(cls, Mon).Count + 1, map.ForClassDay(cls, Tue).Count);

        var tt = DbFixture.Timetable(db);
        var monday = await tt.AssignAsync(Lesson(cls, Mon, sundayOnly, teacher));
        Assert.False(monday.Succeeded);
        Assert.Contains("does not run on Monday", monday.Errors[0].Value);
        Ok(await tt.AssignAsync(Lesson(cls, Sun, sundayOnly, teacher)));

        // A day can be added only while the period stays valid; a day with a lesson cannot be dropped.
        var drop = await DbFixture.Periods(db).EditAsync(sundayOnly, P("Sunday extra", (15, 0), (15, 30), Mon), PeriodScope.ThisClass, cls, null);
        Assert.False(drop.Result.Succeeded);
        Assert.Contains(drop.Result.Errors, e => e.Value.Contains("keep that day"));
    }

    // ------------------------------------------------------------------ 8, 9, 10, 11 — rename, retime, overlaps, unused delete

    [Fact]
    public async Task Periods_can_be_renamed_and_retimed_but_never_overlap_and_unused_ones_are_removed()
    {
        var cls = await fx.NewClassAsync();
        var id = await AddForClassAsync(cls, P("Period 11", (15, 0), (15, 45)));
        await using var db = fx.NewContext();
        var periods = DbFixture.Periods(db);

        Ok(await periods.EditAsync(id, P("Study hall", (15, 10), (15, 50)), PeriodScope.ThisClass, cls, null));
        var p = (await periods.MapAsync()).Get(id)!;
        Assert.Equal(("Study hall", new TimeOnly(15, 10), new TimeOnly(15, 50)), (p.Label, p.Start!.Value, p.End!.Value));

        // End before start, overlap, duplicate name: all refused, nothing saved.
        Assert.Contains((await periods.AddAsync(P("Bad", (17, 0), (16, 0)), PeriodScope.ThisClass, cls, null)).Result.Errors, e => e.Key == "EndTime");
        var overlap = await periods.AddAsync(P("Clash", (15, 30), (16, 15)), PeriodScope.ThisClass, cls, null);
        Assert.Contains(overlap.Result.Errors, e => e.Value.Contains("overlaps Study hall"));
        Assert.Contains((await periods.AddAsync(P("Study hall", (16, 0), (16, 30)), PeriodScope.ThisClass, cls, null)).Result.Errors, e => e.Value.Contains("already has a period called"));
        // Display order must follow the start times.
        var order = await periods.AddAsync(new PeriodInput("Early", new(14, 0), new(14, 30), 50, true, true, null), PeriodScope.ThisClass, cls, null);
        Assert.Contains(order.Result.Errors, e => e.Value.Contains("display order"));
        // The same times on another day only are fine (overlap is per day).
        var monOnly = await AddForClassAsync(cls, P("Club", (16, 0), (16, 45), Mon));
        Assert.Contains((await periods.AddAsync(P("Choir", (16, 30), (17, 0), Mon), PeriodScope.ThisClass, cls, null)).Result.Errors, e => e.Value.Contains("overlaps Club"));
        Ok(await periods.AddAsync(P("Choir", (16, 30), (17, 0), Tue), PeriodScope.ThisClass, cls, null));

        // Never used: deleted outright.
        Ok(await periods.DeleteAsync(monOnly, PeriodScope.ThisClass, cls, false, null));
        Assert.False(await db.TimeSlots.AnyAsync(s => s.Id == monOnly));
    }

    // ------------------------------------------------------------------ 12, 23 — safe delete, history kept

    [Fact]
    public async Task A_period_with_lessons_is_only_deleted_by_explicitly_ending_them_and_history_is_kept()
    {
        var cls = await fx.NewClassAsync();
        var student = await fx.NewStudentAsync(cls);
        var teacher = await TeacherAsync();
        var id = await AddForClassAsync(cls, P("Period 11", (15, 0), (15, 45)));
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        var periods = DbFixture.Periods(db);
        var lesson = await tt.AssignAsync(Lesson(cls, Sun, id, teacher));
        Ok(lesson);
        // An attendance record of this lesson (history).
        var past = SchoolWeek.Current.AddDays(-7);
        db.SessionAttendance.Add(new SessionAttendanceRecord { StudentId = student.Id, TimetableAssignmentId = lesson.EntityId!.Value, SessionDate = past, SchoolClassId = cls, Status = SessionAttendanceStatus.Present, RecordedBy = "Test" });
        await db.SaveChangesAsync();

        var refused = await periods.DeleteAsync(id, PeriodScope.ThisClass, cls, endLessons: false, null);
        Assert.False(refused.Result.Succeeded);
        Assert.Contains(refused.Result.Errors, e => e.Value.Contains("End these lessons"));
        Assert.Null((await db.TimetableAssignments.AsNoTracking().SingleAsync(t => t.Id == lesson.EntityId)).EndedOn); // untouched

        Ok(await periods.DeleteAsync(id, PeriodScope.ThisClass, cls, endLessons: true, null));
        var row = await db.TimetableAssignments.AsNoTracking().SingleAsync(t => t.Id == lesson.EntityId);
        Assert.Equal(SchoolWeek.Current, row.EndedOn); // ended from this week, not deleted
        var slot = await db.TimeSlots.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.NotNull(slot.ArchivedAt); // archived, kept for history
        Assert.True(await db.SessionAttendance.AnyAsync(r => r.TimetableAssignmentId == lesson.EntityId && r.SessionDate == past && r.Status == SessionAttendanceStatus.Present));
        Assert.DoesNotContain((await periods.MapAsync()).ForClass(cls), s => s.Id == id);
        // The recorded lesson still shows on its date.
        var days = await SchoolDays.LoadAsync(db, default);
        if (days.IsSchoolDay(ScheduleRules.DateOf(past, Sun)) && ScheduleRules.DateOf(past, Sun) == past)
            Assert.Contains(await Lessons.OnAsync(db, past, days, new Lessons.Scope(ClassId: cls), default), l => l.SessionId == lesson.EntityId);
    }

    // ------------------------------------------------------------------ 13 — edit for one class only

    [Fact]
    public async Task Editing_a_school_period_for_one_class_gives_it_its_own_version_and_moves_its_lessons()
    {
        var (a, b) = (await fx.NewClassAsync(), await fx.NewClassAsync());
        var teacher = await TeacherAsync();
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        var periods = DbFixture.Periods(db);
        var add = await Confirmed(c => periods.AddAsync(P("Late club", (19, 0), (19, 30)), PeriodScope.AllClasses, null, c));
        Ok(add);
        var school = add.Result.EntityId!.Value;
        try
        {
            var lesson = await tt.AssignAsync(Lesson(a, Wed, school, teacher));
            Ok(lesson);
            var before = (await DbFixture.Workload(db).SummaryAsync(teacher, SchoolWeek.Current))!.Total;

            var plan = await periods.EditAsync(school, P("Late club", (19, 5), (19, 35)), PeriodScope.ThisClass, a, null);
            Assert.True(plan.NeedsConfirmation); // a lesson moves to new times
            Assert.Single(plan.Plan!.Lessons);
            Ok(await periods.EditAsync(school, P("Late club", (19, 5), (19, 35)), PeriodScope.ThisClass, a, plan.Plan.Fingerprint));

            var map = await periods.MapAsync();
            var own = map.ForClass(a).Single(s => s.Label == "Late club");
            Assert.Equal((a, school, new TimeOnly(19, 5)), (own.ClassId!.Value, own.ReplacesId!.Value, own.Start!.Value));
            Assert.Contains(map.ForClass(b), s => s.Id == school && s.Start == new TimeOnly(19, 0)); // other classes unchanged
            var grid = await tt.ClassGridAsync(a, SchoolWeek.Current);
            Assert.NotNull(grid.Session(Wed, own.Id)); // the lesson moved with the class
            Assert.Empty(grid.Problems);
            Assert.Equal(SchoolWeek.Current, (await db.TimetableAssignments.AsNoTracking().SingleAsync(t => t.Id == lesson.EntityId)).EndedOn); // old row kept as history
            Assert.Equal(before, (await DbFixture.Workload(db).SummaryAsync(teacher, SchoolWeek.Current))!.Total); // same weekly count

            // The class can go back to the school period once its own version has no lessons.
            Ok(await tt.RemoveSessionAsync(grid.Session(Wed, own.Id)!.Id));
            Ok(await periods.RestoreAsync(own.Id));
            Assert.Contains((await periods.MapAsync()).ForClass(a), s => s.Id == school);
        }
        finally
        {
            await DeleteSchoolWideAsync(school);
        }
    }

    // ------------------------------------------------------------------ 15, 16 — conflicts across different structures

    [Fact]
    public async Task Teacher_and_class_conflicts_compare_real_times_across_different_period_structures()
    {
        var (a, b) = (await fx.NewClassAsync(), await fx.NewClassAsync());
        var teacher = await TeacherAsync();
        var a1 = await AddForClassAsync(a, P("A late", (15, 0), (15, 45), Sun));
        var b1 = await AddForClassAsync(b, P("B late", (15, 30), (16, 15), Sun));
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        Ok(await tt.AssignAsync(Lesson(a, Sun, a1, teacher)));
        var clash = await tt.AssignAsync(Lesson(b, Sun, b1, teacher)); // different periods, overlapping times
        Assert.False(clash.Succeeded);
        Assert.Contains("already teaches", clash.Errors[0].Value);

        // A class never has two overlapping lessons, even if overlapping periods exist in old data.
        var legacy = new TimeSlot { Number = 40, Name = "Legacy", StartTime = new(15, 20), EndTime = new(15, 50), SchoolClassId = a, DayMask = PeriodDays.Bit(Sun), UpdatedAt = DateTime.UtcNow };
        db.TimeSlots.Add(legacy);
        await db.SaveChangesAsync();
        var other = await TeacherAsync();
        var twice = await tt.AssignAsync(Lesson(a, Sun, legacy.Id, other));
        Assert.False(twice.Succeeded);
        Assert.Contains("overlaps", twice.Errors[0].Value);
    }

    // ------------------------------------------------------------------ move

    [Fact]
    public async Task Lessons_move_between_periods_only_into_free_periods_that_run_that_day()
    {
        var cls = await fx.NewClassAsync();
        var teacher = await TeacherAsync();
        var sundayOnly = await AddForClassAsync(cls, P("Sunday extra", (15, 0), (15, 30), Sun));
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        var lesson = await tt.AssignAsync(Lesson(cls, Mon, 3, teacher));
        Ok(lesson);
        var before = (await DbFixture.Workload(db).SummaryAsync(teacher, SchoolWeek.Current))!.Total;

        Assert.Contains("does not run on Monday", (await tt.MoveSessionAsync(lesson.EntityId!.Value, Mon, sundayOnly)).Errors[0].Value);
        Assert.Contains((await tt.MoveTargetsAsync(lesson.EntityId.Value)), t => t.Day == Sun && t.Slot.Id == sundayOnly);
        var moved = await tt.MoveSessionAsync(lesson.EntityId.Value, Sun, sundayOnly);
        Ok(moved);
        var grid = await tt.ClassGridAsync(cls, SchoolWeek.Current);
        Assert.NotNull(grid.Session(Sun, sundayOnly));
        Assert.Null(grid.Session(Mon, 3));
        Assert.Equal(before, (await DbFixture.Workload(db).SummaryAsync(teacher, SchoolWeek.Current))!.Total);
    }

    // ------------------------------------------------------------------ 17, 18 — workload

    [Fact]
    public async Task Workload_limits_apply_in_class_periods_and_PE_lessons_count()
    {
        var (a, b) = (await fx.NewClassAsync(), await fx.NewClassAsync());
        var teacher = await TeacherAsync();
        var a1 = await AddForClassAsync(a, P("Period 11", (15, 0), (15, 45)));
        var b1 = await AddForClassAsync(b, P("Period 11", (15, 0), (15, 45)));
        await using var db = fx.NewContext();
        var week = SchoolWeek.Current;
        var max = await DbFixture.Workload(db).MaxAsync();
        Ok(await DbFixture.Workload(db).CreateAsync(new WorkloadInput(teacher, week, WorkloadCategory.Office, max - 1, null, null, "Office")));
        var tt = DbFixture.Timetable(db);
        Ok(await tt.AssignAsync(Lesson(a, Sun, a1, teacher))); // max/max
        var over = await tt.AssignAsync(Lesson(b, Mon, b1, teacher));
        Assert.False(over.Succeeded);
        Assert.Contains($"{max}/{max}", over.Errors[0].Value);

        // P.E. lessons are ordinary timetable sessions of the P.E. department: they count in the same total.
        var pe = await TeacherAsync(PeDepartment);
        var subject = (await tt.SubjectsAsync(PeDepartment))[0];
        Ok(await tt.AssignAsync(Lesson(b, Tue, b1, pe, PeDepartment, subject)));
        var summary = (await DbFixture.Workload(db).SummaryAsync(pe, week))!;
        Assert.Equal((1, 1), (summary.Breakdown.Class, summary.Total));
    }

    // ------------------------------------------------------------------ 21, 22 — session attendance

    [Fact]
    public async Task Session_attendance_follows_the_scheduled_periods_and_skips_holidays_and_cancellations()
    {
        var cls = await fx.NewClassAsync();
        var teacher = await TeacherAsync();
        var sundayOnly = await AddForClassAsync(cls, P("Sunday extra", (15, 0), (15, 30), Sun));
        await using var db = fx.NewContext();
        Ok(await DbFixture.Timetable(db).AssignAsync(Lesson(cls, Sun, sundayOnly, teacher)));
        // Far-future weeks, so no other test's holidays or data interfere.
        var week = SchoolWeek.Current.AddDays(7 * 40);
        var sunday = ScheduleRules.DateOf(week, Sun);
        var days = await SchoolDays.LoadAsync(db, default);
        var lessons = await Lessons.OnAsync(db, sunday, days, new Lessons.Scope(ClassId: cls), default);
        Assert.Contains(lessons, l => l.SlotId == sundayOnly && l.Period == "Sunday extra" && l.Start == new TimeOnly(15, 0));
        Assert.DoesNotContain(await Lessons.OnAsync(db, ScheduleRules.DateOf(week, Mon), days, new Lessons.Scope(ClassId: cls), default), l => l.SlotId == sundayOnly);

        // Cancelled on that date: listed, but not expected.
        var id = lessons.Single(l => l.SlotId == sundayOnly).SessionId;
        db.SessionCancellations.Add(new SessionCancellation { TimetableAssignmentId = id, SessionDate = sunday, Reason = "Trip", CancelledBy = "Test", CancelledAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var cancelled = (await Lessons.OnAsync(db, sunday, days, new Lessons.Scope(ClassId: cls), default)).Single(l => l.SessionId == id);
        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.IsExpected);

        // A holiday: no lessons at all.
        var holiday = new SchoolHoliday { Name = "Test holiday " + DbFixture.Unique(), StartDate = sunday, EndDate = sunday, CreatedBy = "Test" };
        db.SchoolHolidays.Add(holiday);
        await db.SaveChangesAsync();
        try
        {
            Assert.Empty(await Lessons.OnAsync(db, sunday, await SchoolDays.LoadAsync(db, default), new Lessons.Scope(ClassId: cls), default));
        }
        finally
        {
            db.SchoolHolidays.Remove(holiday);
            await db.SaveChangesAsync();
        }

        // A period that no longer runs on the day (e.g. changed in old data) produces no lessons from this week on.
        await db.TimeSlots.Where(s => s.Id == sundayOnly).ExecuteUpdateAsync(u => u.SetProperty(s => s.DayMask, PeriodDays.Bit(Mon)));
        Assert.DoesNotContain(await Lessons.OnAsync(db, ScheduleRules.DateOf(week.AddDays(7), Sun), days, new Lessons.Scope(ClassId: cls), default), l => l.SessionId == id);
    }

    // ------------------------------------------------------------------ 24 — migrated data

    [Fact]
    public async Task The_original_periods_are_school_wide_every_day_periods_after_the_migration()
    {
        await using var db = fx.NewContext();
        var originals = await db.TimeSlots.AsNoTracking().Where(s => s.Id <= TimeSlot.SeededCount).OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 10), originals.Select(s => s.Number));
        Assert.All(originals, s => Assert.True(s.SchoolClassId is null && s.ReplacesTimeSlotId is null && s.DayMask == 0 && s.ArchivedAt is null));
        // Every class that has not changed a school period still has all ten (unchanged structure for existing timetables).
        var map = await DbFixture.Periods(db).MapAsync();
        Assert.All(map.Classes.Where(c => map.ReplacedFor(c.Id).Count == 0), c => Assert.All(Enumerable.Range(1, 10), id => Assert.Contains(map.ForClass(c.Id), s => s.Id == id)));
    }
}
