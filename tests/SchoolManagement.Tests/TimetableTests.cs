using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Time slots, class/teacher timetables, conflicts, and the timetable's place in the 24-period workload.</summary>
[Collection("db")]
public class TimetableTests(DbFixture fx)
{
    private const int Math = 3;

    private async Task<int> TeacherAsync(int departmentId = Math, string? sub = null, string? subject = null)
    {
        await using var db = fx.NewContext();
        var r = await DbFixture.Teachers(db).CreateAsync("Teacher " + DbFixture.Unique(), "Teacher", null, departmentId, sub, subject);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        return r.EntityId!.Value;
    }

    private static SessionInput Session(int classId, DayOfWeek day, int slot, int teacher, int dept = Math, string subject = "Math") =>
        new(classId, day, slot, dept, subject, teacher, null);

    private static int SlotId(int number) => number; // seeded ids = numbers

    [Fact]
    public async Task Ten_slots_apply_to_Sunday_through_Thursday_and_start_without_times()
    {
        await using var db = fx.NewContext();
        var slots = await DbFixture.Timetable(db).SlotsAsync();
        Assert.Equal(Enumerable.Range(1, 10), slots.Select(s => s.Number));
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday], ScheduleRules.Days);
        Assert.False(ScheduleRules.IsSchoolDay(DayOfWeek.Friday));
    }

    [Fact]
    public async Task Slot_times_are_validated_and_changes_to_used_slots_need_confirmation()
    {
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        try
        {
            Assert.Contains((await tt.UpdateSlotAsync(SlotId(7), null, new(10, 0), new(9, 0), true, false)).Errors, e => e.Key == "EndTime");
            Assert.Contains((await tt.UpdateSlotAsync(SlotId(7), null, new(10, 0), null, true, false)).Errors, e => e.Key == "EndTime");
            Assert.True((await tt.UpdateSlotAsync(SlotId(7), "Period 7", new(12, 0), new(12, 45), true, false)).Succeeded);
            // Overlapping another active slot is refused.
            var overlap = await tt.UpdateSlotAsync(SlotId(8), null, new(12, 30), new(13, 15), true, false);
            Assert.Contains(overlap.Errors, e => e.Value.Contains("overlap"));

            // A slot with a current session: a time change needs confirmation; deactivation is refused.
            var cls = await fx.NewClassAsync();
            var teacher = await TeacherAsync();
            Assert.True((await tt.AssignAsync(Session(cls, DayOfWeek.Tuesday, SlotId(7), teacher))).Succeeded);
            Assert.Contains((await tt.UpdateSlotAsync(SlotId(7), null, new(12, 5), new(12, 50), true, false)).Errors, e => e.Key == "ConfirmAffected");
            Assert.True((await tt.UpdateSlotAsync(SlotId(7), null, new(12, 5), new(12, 50), true, true)).Succeeded);
            Assert.Contains((await tt.UpdateSlotAsync(SlotId(7), null, new(12, 5), new(12, 50), false, true)).Errors, e => e.Key == "IsActive");

            // The session stayed in its slot and shows the new times.
            var grid = await tt.ClassGridAsync(cls, SchoolWeek.Current);
            Assert.NotNull(grid.Session(DayOfWeek.Tuesday, SlotId(7)));
            Assert.Equal(new TimeOnly(12, 5), grid.Slots.Single(s => s.Id == SlotId(7)).Start);
        }
        finally
        {
            await using var reset = fx.NewContext();
            await reset.TimeSlots.Where(s => s.Id == SlotId(7)).ExecuteUpdateAsync(u => u.SetProperty(s => s.StartTime, (TimeOnly?)null).SetProperty(s => s.EndTime, (TimeOnly?)null).SetProperty(s => s.IsActive, true));
        }
    }

    [Fact]
    public async Task One_session_feeds_both_timetables_and_counts_once_in_workload()
    {
        var cls = await fx.NewClassAsync();
        var teacher = await TeacherAsync();
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        var week = SchoolWeek.Current;

        var r = await tt.AssignAsync(Session(cls, DayOfWeek.Sunday, SlotId(2), teacher));
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));

        var classView = (await tt.ClassGridAsync(cls, week)).Session(DayOfWeek.Sunday, SlotId(2));
        var teacherView = (await tt.TeacherGridAsync(teacher, week)).Session(DayOfWeek.Sunday, SlotId(2));
        Assert.NotNull(classView);
        Assert.Equal(classView!.Id, teacherView!.Id); // the same row, not a copy
        Assert.Equal(1, await db.TimetableAssignments.CountAsync(t => t.SchoolClassId == cls));

        var summary = (await DbFixture.Workload(db).SummaryAsync(teacher, week))!;
        Assert.Equal((1, 1, 1), (summary.Breakdown.Class, summary.FromTimetable, summary.Total));
        Assert.Single((await DbFixture.Workload(db).OverviewAsync(week, new WorkloadOverviewQuery { TeacherId = teacher })), o => o.Breakdown.Class == 1);
        // Weeks before the session started do not change.
        Assert.Equal(0, (await DbFixture.Workload(db).SummaryAsync(teacher, week.AddDays(-7)))!.Total);

        // One subject and one teacher per class slot.
        var other = await TeacherAsync();
        var taken = await tt.AssignAsync(Session(cls, DayOfWeek.Sunday, SlotId(2), other));
        Assert.False(taken.Succeeded);
        Assert.Contains("already has Math", taken.Errors[0].Value);

        // A manual Class row for the same class would count the sessions twice.
        var manual = await DbFixture.Workload(db).CreateAsync(new WorkloadInput(teacher, week, WorkloadCategory.Class, 1, cls, null, null));
        Assert.Contains(manual.Errors, e => e.Value.Contains("class timetable"));
    }

    [Fact]
    public async Task Teacher_conflicts_are_rejected_for_the_same_slot_and_for_overlapping_times()
    {
        var classA = await fx.NewClassAsync();
        var classB = await fx.NewClassAsync();
        var teacher = await TeacherAsync();
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        var names = await db.Classes.Where(c => c.Id == classA).Select(c => c.Name).SingleAsync();

        Assert.True((await tt.AssignAsync(Session(classA, DayOfWeek.Sunday, SlotId(3), teacher))).Succeeded);
        var clash = await tt.AssignAsync(Session(classB, DayOfWeek.Sunday, SlotId(3), teacher));
        Assert.False(clash.Succeeded);
        Assert.Contains($"already teaches {names}", clash.Errors[0].Value);
        Assert.True((await tt.AssignAsync(Session(classB, DayOfWeek.Monday, SlotId(3), teacher))).Succeeded); // another day is fine

        // Real time overlap: a duty on a Thursday 07:15–08:00 and period 9 set to 07:30–08:10.
        try
        {
            Assert.True((await tt.UpdateSlotAsync(SlotId(9), null, new(7, 30), new(8, 10), true, true)).Succeeded);
            var thursday = ScheduleRules.DateOf(SchoolWeek.Current.AddDays(7), DayOfWeek.Thursday);
            var duty = await DbFixture.Duties(db).CreateAsync(new DutyInput(DutyComponent.StudentsArrival, thursday, "Gate", new(7, 15), new(8, 0), null,
                teacher, AttendanceStatus.NotRecorded, null, null, []));
            Assert.True(duty.Succeeded, string.Join("; ", duty.Errors.Select(e => e.Value)));
            var overlap = await tt.AssignAsync(Session(classA, DayOfWeek.Thursday, SlotId(9), teacher));
            Assert.False(overlap.Succeeded);
            Assert.Contains("duty", overlap.Errors[0].Value);
        }
        finally
        {
            await using var reset = fx.NewContext();
            await reset.TimeSlots.Where(s => s.Id == SlotId(9)).ExecuteUpdateAsync(u => u.SetProperty(s => s.StartTime, (TimeOnly?)null).SetProperty(s => s.EndTime, (TimeOnly?)null));
        }

        // The overlap rule itself compares times, not only slot ids.
        Assert.True(ScheduleRules.Overlaps(1, new(8, 0), new(8, 45), 2, new(8, 30), new(9, 15)));
        Assert.False(ScheduleRules.Overlaps(1, new(8, 0), new(8, 45), 2, new(8, 45), new(9, 30)));
    }

    [Fact]
    public async Task A_teacher_with_23_periods_gets_one_more_and_never_more_than_24()
    {
        var teacher = await TeacherAsync();
        var classA = await fx.NewClassAsync();
        var classB = await fx.NewClassAsync();
        await using var db = fx.NewContext();
        var week = SchoolWeek.Current;
        var workload = DbFixture.Workload(db);
        Assert.True((await workload.CreateAsync(new WorkloadInput(teacher, week, WorkloadCategory.Office, 23, null, null, "Office"))).Succeeded);

        var tt = DbFixture.Timetable(db);
        Assert.True((await tt.AssignAsync(Session(classA, DayOfWeek.Wednesday, SlotId(4), teacher))).Succeeded); // 24/24
        var over = await tt.AssignAsync(Session(classB, DayOfWeek.Wednesday, SlotId(5), teacher));
        Assert.False(over.Succeeded);
        Assert.Contains("24/24", over.Errors[0].Value);
        Assert.False((await workload.CreateAsync(new WorkloadInput(teacher, week, WorkloadCategory.Substitution, 1, null, null, null))).Succeeded);
        Assert.Equal(24, (await workload.SummaryAsync(teacher, week))!.Total);

        // The candidate list marks the teacher as unavailable (full).
        var candidates = await tt.CandidatesAsync(Math, "Math", DayOfWeek.Wednesday, SlotId(5), null, null);
        Assert.False(candidates.Single(c => c.Id == teacher).Available);

        // The database backstop: even a direct insert cannot push the week above 24.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO TimetableAssignments (SchoolClassId, Day, TimeSlotId, DepartmentId, Subject, TeacherId, StartsOn, CreatedAt, UpdatedAt) VALUES ({classB}, 3, 6, {Math}, 'Math', {teacher}, {week}, SYSUTCDATETIME(), SYSUTCDATETIME())"));
        Assert.Contains("more than 24", ex.Message);
    }

    [Fact]
    public async Task Changing_or_removing_a_session_recalculates_workload_without_double_counting()
    {
        var cls = await fx.NewClassAsync();
        var first = await TeacherAsync();
        var second = await TeacherAsync();
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        var workload = DbFixture.Workload(db);
        var week = SchoolWeek.Current;

        var id = (await tt.AssignAsync(Session(cls, DayOfWeek.Monday, SlotId(6), first))).EntityId!.Value;
        Assert.True((await tt.UpdateSessionAsync(id, Session(cls, DayOfWeek.Monday, SlotId(6), first) with { Notes = "Lab" })).Succeeded);
        Assert.Equal(1, (await workload.SummaryAsync(first, week))!.Total); // edit in place: still one

        var moved = await tt.UpdateSessionAsync(id, Session(cls, DayOfWeek.Monday, SlotId(6), second));
        Assert.True(moved.Succeeded);
        Assert.Equal((0, 1), ((await workload.SummaryAsync(first, week))!.Total, (await workload.SummaryAsync(second, week))!.Total));
        var history = await db.TimetableAssignments.AsNoTracking().Where(t => t.SchoolClassId == cls).OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.NotNull(history[0].EndedOn); // kept as history
        Assert.Null(history[1].EndedOn);

        Assert.True((await tt.RemoveSessionAsync(moved.EntityId!.Value)).Succeeded);
        Assert.Equal(0, (await workload.SummaryAsync(second, week))!.Total);
        Assert.Null((await tt.ClassGridAsync(cls, week)).Session(DayOfWeek.Monday, SlotId(6)));
        Assert.Equal(2, await db.TimetableAssignments.CountAsync(t => t.SchoolClassId == cls));
        // A teacher with timetable history is kept.
        Assert.False((await DbFixture.Teachers(db).DeleteAsync(second)).Succeeded);
    }

    [Fact]
    public async Task Teachers_are_filtered_by_department_and_subject_and_management_is_refused()
    {
        var quran = await TeacherAsync(7, "Islamic", "Quran");
        var christian = await TeacherAsync(7, "Christian");
        var mathTeacher = await TeacherAsync();
        var cls = await fx.NewClassAsync();
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);

        Assert.Contains("Quran", await tt.SubjectsAsync(7));
        Assert.Equal(["Math"], await tt.SubjectsAsync(Math));
        var candidates = await tt.CandidatesAsync(7, "Quran", DayOfWeek.Sunday, SlotId(8), null, null);
        Assert.Contains(candidates, c => c.Id == quran);
        Assert.DoesNotContain(candidates, c => c.Id == christian || c.Id == mathTeacher);

        Assert.Contains((await tt.AssignAsync(Session(cls, DayOfWeek.Sunday, SlotId(8), christian, 7, "Quran"))).Errors, e => e.Key == "TeacherId");
        Assert.Contains((await tt.AssignAsync(Session(cls, DayOfWeek.Sunday, SlotId(8), mathTeacher, 7, "Math"))).Errors, e => e.Key == "Subject");
        var manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Manager " + DbFixture.Unique() })).EntityId!.Value;
        Assert.Contains((await tt.AssignAsync(Session(cls, DayOfWeek.Sunday, SlotId(8), manager))).Errors, e => e.Value.Contains("Management Team"));
        Assert.True((await tt.AssignAsync(Session(cls, DayOfWeek.Sunday, SlotId(8), quran, 7, "quran"))).Succeeded);
    }

    [Fact]
    public async Task Substitutions_office_hours_and_duties_update_their_own_categories()
    {
        var cls = await fx.NewClassAsync();
        var teacher = await TeacherAsync();
        var sub = await TeacherAsync();
        await using var db = fx.NewContext();
        var tt = DbFixture.Timetable(db);
        var workload = DbFixture.Workload(db);
        var week = SchoolWeek.Current.AddDays(7);

        var session = (await tt.AssignAsync(Session(cls, DayOfWeek.Tuesday, SlotId(1), teacher))).EntityId!.Value;

        // The class's own teacher is busy in that slot; another teacher can substitute for one week.
        Assert.False((await workload.CreateAsync(new WorkloadInput(teacher, week, WorkloadCategory.Substitution, 1, cls, null, null, DayOfWeek.Tuesday, SlotId(1)))).Succeeded);
        Assert.True((await workload.CreateAsync(new WorkloadInput(sub, week, WorkloadCategory.Substitution, 1, cls, null, null, DayOfWeek.Tuesday, SlotId(1)))).Succeeded);
        Assert.Single((await tt.ClassGridAsync(cls, week)).PeriodsAt(DayOfWeek.Tuesday, SlotId(1)), p => p.TeacherId == sub);
        // ...and is then busy there: no office hours in the same slot that week.
        Assert.False((await workload.CreateAsync(new WorkloadInput(sub, week, WorkloadCategory.Office, 1, null, null, null, DayOfWeek.Tuesday, SlotId(1)))).Succeeded);
        Assert.True((await workload.CreateAsync(new WorkloadInput(sub, week, WorkloadCategory.Office, 1, null, null, null, DayOfWeek.Tuesday, SlotId(2)))).Succeeded);
        // A slot period is always exactly one period.
        Assert.False((await workload.CreateAsync(new WorkloadInput(sub, week, WorkloadCategory.Office, 2, null, null, null, DayOfWeek.Wednesday, SlotId(2)))).Succeeded);

        // Duty from the existing Duties module, counted once in Duties.
        var dutyDate = ScheduleRules.DateOf(week, DayOfWeek.Wednesday);
        var duty = (await DbFixture.Duties(db).CreateAsync(new DutyInput(DutyComponent.MorningAssembly, dutyDate, "Line", new(7, 0), new(7, 20), null,
            sub, AttendanceStatus.NotRecorded, null, null, []))).EntityId!.Value;
        Assert.True((await workload.CreateAsync(new WorkloadInput(sub, week, WorkloadCategory.Duty, 1, null, duty, null))).Succeeded);
        Assert.False((await workload.CreateAsync(new WorkloadInput(sub, week, WorkloadCategory.Duty, 1, null, duty, null))).Succeeded);
        Assert.Contains((await tt.TeacherGridAsync(sub, week)).Duties, d => d.Id == duty && d.Counted);

        var s = (await workload.SummaryAsync(sub, week))!;
        Assert.Equal((0, 1, 1, 1, 3), (s.Breakdown.Class, s.Breakdown.Substitution, s.Breakdown.Office, s.Breakdown.Duty, s.Total));
        Assert.Equal(1, (await workload.SummaryAsync(teacher, week))!.Breakdown.Class); // the session still counts for its teacher
        Assert.NotEqual(0, session);
    }
}
