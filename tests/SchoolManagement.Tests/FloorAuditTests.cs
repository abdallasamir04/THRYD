using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Tests;

/// <summary>
/// Floor Audit &amp; Student Movement: the three rounds of every session (from its own start time), the schedule read from
/// the timetable (cancellations, untimed periods, rescheduling), area statuses, the checklist and its snapshots, floors
/// and areas, student movements (never a fabricated return, one open movement per student, attendance untouched) and
/// who may do what.
/// </summary>
[Collection("db")]
public class FloorAuditTests(DbFixture fx) : IAsyncLifetime
{
    private readonly List<int> _slots = [];
    private readonly List<int> _lessons = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// The periods and lessons a test added are retired afterwards (archived / ended, as the timetable does), so other
    /// tests' school-wide period changes never meet them. Their inspections stay, as history does.
    /// </summary>
    public async Task DisposeAsync()
    {
        await using var db = fx.NewContext();
        if (_lessons.Count > 0)
            await db.TimetableAssignments.Where(t => _lessons.Contains(t.Id) && t.EndedOn == null).ExecuteUpdateAsync(u => u.SetProperty(t => t.EndedOn, t => t.StartsOn));
        if (_slots.Count > 0)
            await db.TimeSlots.Where(t => _slots.Contains(t.Id)).ExecuteUpdateAsync(u => u.SetProperty(t => t.ArchivedAt, DateTime.UtcNow).SetProperty(t => t.IsActive, false));
    }

    // ---------------------------------------------------------------- helpers

    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class User(int? staffId, string? name) : ICurrentUser
    {
        public string? Name => name;
        public int? StaffId => staffId;
        public bool IsVerified => false;
    }

    private sealed class MemoryFiles : ITaskFileStore
    {
        public readonly Dictionary<string, byte[]> Files = [];
        public async Task SaveAsync(string storedName, Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            content.Position = 0;
            await content.CopyToAsync(ms, ct);
            Files[storedName] = ms.ToArray();
        }
        public Stream? Open(string storedName) => Files.TryGetValue(storedName, out var b) ? new MemoryStream(b) : null;
        public void Delete(string storedName) => Files.Remove(storedName);
    }

    /// <summary>A Wednesday (school day, Cairo UTC+2 in March) far from other tests' dates.</summary>
    private static readonly DateOnly Day = new(2031, 3, 12);

    private static ISchoolTimeService ClockAt(DateOnly date, int hour, int minute) =>
        new SchoolTimeService(new FixedClock(new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(2))), SchoolTimeService.DefaultTimeZoneId);

    private sealed record Services(FloorAuditService Audit, FloorConfigService Config, StudentMovementService Movements, FloorAuditAccessService Access, MemoryFiles Files);

    private static Services For(SchoolDbContext db, ICurrentUser user, ISchoolTimeService clock)
    {
        var access = new FloorAuditAccessService(db, user);
        var movements = new StudentMovementService(db, user, access, clock);
        var files = new MemoryFiles();
        var audit = new FloorAuditService(db, user, access, clock, files, movements, DbFixture.Settings(db));
        return new Services(audit, new FloorConfigService(db, user, access, audit, movements), movements, access, files);
    }

    /// <summary>A staff member with a Floor Audit role (Manager, or Inspector of a floor / every floor).</summary>
    private async Task<(int Id, string Name)> StaffWithAsync(FloorAuditRole? role, int? floorId = null, bool movements = true)
    {
        var t = await fx.NewTeacherAsync("Floor Audit tester");
        if (role is { } r)
        {
            await using var db = fx.NewContext();
            db.FloorAuditAccess.Add(new FloorAuditAccess { StaffMemberId = t.Id, Role = r, FloorId = floorId, CanRecordMovements = movements, GrantedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        return t;
    }

    /// <summary>
    /// A new class with its own period (class-specific, so other tests' timetables are untouched) starting at
    /// <paramref name="start"/> on <see cref="Day"/>'s weekday, and one lesson in it. Returns (classId, slotId, lessonId).
    /// </summary>
    private async Task<(int ClassId, int SlotId, int LessonId)> ClassWithLessonAsync(TimeOnly start, int? classId = null)
    {
        var cls = classId ?? await fx.NewClassAsync();
        var teacher = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var slot = new TimeSlot { Number = 900 + Random.Shared.Next(90), Name = "FA " + DbFixture.Unique()[..5], StartTime = start, EndTime = start.AddMinutes(45),
            SchoolClassId = cls, DayMask = 0, IsActive = true, UpdatedAt = DateTime.UtcNow };
        db.TimeSlots.Add(slot);
        await db.SaveChangesAsync();
        var dept = await db.Departments.Select(d => d.Id).FirstAsync();
        var lesson = new TimetableAssignment { SchoolClassId = cls, Day = Day.DayOfWeek, TimeSlotId = slot.Id, DepartmentId = dept, Subject = "Science",
            TeacherId = teacher.Id, StartsOn = SchoolWeek.StartOf(Day) };
        db.TimetableAssignments.Add(lesson);
        await db.SaveChangesAsync();
        _slots.Add(slot.Id);
        _lessons.Add(lesson.Id);
        return (cls, slot.Id, lesson.Id);
    }

    /// <summary>Puts a class's classroom on a floor (as a manager would) and returns the area id.</summary>
    private async Task<int> RoomAsync(int classId, int floorId)
    {
        var mgr = await StaffWithAsync(FloorAuditRole.Manager);
        await using var db = fx.NewContext();
        var s = For(db, new User(mgr.Id, mgr.Name), ClockAt(Day, 7, 0));
        var r = await s.Config.AssignClassroomsAsync(floorId, [classId]);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        return await db.FloorAreas.Where(a => a.SchoolClassId == classId).Select(a => a.Id).SingleAsync();
    }

    private static Dictionary<int, string?> AllAnswers(InspectionFormModel form) =>
        form.Questions.ToDictionary(q => q.ItemId, q => q.Type switch
        {
            AuditResponseType.Number => "21",
            AuditResponseType.Text or AuditResponseType.IssueText => null,
            _ => q.Choices[0],
        });

    private static AuditLesson L(int slot, TimeOnly? start, int classId, bool schoolWide = true, bool cancelled = false, string? name = null) =>
        new(slot, slot, name, start, start?.AddMinutes(45), schoolWide, classId, "C" + classId, cancelled);

    // ---------------------------------------------------------------- audit scheduling (pure)

    [Theory]
    [InlineData("08:00", "08:06", "08:25", "08:40")]
    [InlineData("09:00", "09:06", "09:25", "09:40")]
    [InlineData("10:35", "10:41", "11:00", "11:15")]
    [InlineData("12:50", "12:56", "13:15", "13:30")]
    public void Each_round_is_calculated_from_the_sessions_own_start(string start, string r1, string r2, string r3)
    {
        var times = FloorAuditRounds.TimesFor(TimeOnly.Parse(start));
        Assert.Equal([TimeOnly.Parse(r1), TimeOnly.Parse(r2), TimeOnly.Parse(r3)], times);
        Assert.Equal(TimeOnly.Parse(r2), FloorAuditRounds.TimeOf(TimeOnly.Parse(start), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => FloorAuditRounds.TimeOf(TimeOnly.Parse(start), 4));
    }

    [Fact]
    public void Different_sessions_get_their_own_three_rounds_and_untimed_periods_get_none()
    {
        var s = FloorAuditSchedule.Build(Day,
        [
            L(1, new TimeOnly(8, 0), 1), L(1, new TimeOnly(8, 0), 2),
            L(2, new TimeOnly(8, 50), 1), L(2, new TimeOnly(8, 50), 2, cancelled: true),
            L(3, null, 1),
        ]);
        Assert.Equal(2, s.Sessions.Count);
        Assert.Equal([new TimeOnly(8, 6), new TimeOnly(8, 25), new TimeOnly(8, 40)], s.Sessions[0].RoundTimes);
        Assert.Equal([new TimeOnly(8, 56), new TimeOnly(9, 15), new TimeOnly(9, 30)], s.Sessions[1].RoundTimes);
        // A cancelled lesson takes its class out of that session only.
        Assert.Equal([1], s.Sessions[1].ClassIds);
        Assert.Equal([2], s.Sessions[1].CancelledClassIds);
        // A period without a start time: listed, never given invented rounds.
        Assert.Equal(["Period 3"], s.UntimedPeriods);
        Assert.True(s.HasSchedule);
    }

    [Fact]
    public void A_session_whose_lessons_are_all_cancelled_has_no_rounds_and_no_timetable_means_no_schedule()
    {
        var s = FloorAuditSchedule.Build(Day, [L(1, new TimeOnly(8, 0), 1, cancelled: true), L(1, new TimeOnly(8, 0), 2, cancelled: true)]);
        Assert.True(Assert.Single(s.Sessions).Cancelled);
        Assert.False(s.HasSchedule);
        var room = new AuditAreaRef(1, 1, FloorAreaKind.Classroom, 1, true);
        var bath = new AuditAreaRef(2, 1, FloorAreaKind.Facility, null, true);
        Assert.False(FloorAuditSchedule.IsRequired(room, s.Sessions[0]));
        Assert.False(FloorAuditSchedule.IsRequired(bath, s.Sessions[0]));

        var none = FloorAuditSchedule.Build(Day, [L(1, null, 1), L(2, null, 1)]);
        Assert.False(none.HasSchedule);
        Assert.Empty(none.Sessions);
        Assert.Equal(2, none.UntimedPeriods.Count);
        Assert.False(FloorAuditSchedule.Build(Day, [], isSchoolDay: false, holiday: "Spring break").IsSchoolDay);
    }

    [Fact]
    public void Areas_are_required_by_their_class_lessons_facilities_by_school_wide_sessions_and_never_when_inactive()
    {
        var s = FloorAuditSchedule.Build(Day, [L(1, new TimeOnly(8, 0), 1), L(2, new TimeOnly(8, 5), 7, schoolWide: false)]);
        var wide = s.Get(1)!;
        var classOnly = s.Get(2)!;
        Assert.True(FloorAuditSchedule.IsRequired(new AuditAreaRef(1, 1, FloorAreaKind.Classroom, 1, true), wide));
        Assert.False(FloorAuditSchedule.IsRequired(new AuditAreaRef(2, 1, FloorAreaKind.Classroom, 9, true), wide));   // no lesson
        Assert.False(FloorAuditSchedule.IsRequired(new AuditAreaRef(1, 1, FloorAreaKind.Classroom, 1, false), wide));  // inactive
        Assert.True(FloorAuditSchedule.IsRequired(new AuditAreaRef(3, 1, FloorAreaKind.Facility, null, true), wide));
        Assert.False(FloorAuditSchedule.IsRequired(new AuditAreaRef(4, 1, FloorAreaKind.Facility, null, false), wide));
        Assert.False(FloorAuditSchedule.IsRequired(new AuditAreaRef(3, 1, FloorAreaKind.Facility, null, true), classOnly));
        Assert.True(FloorAuditSchedule.IsRequired(new AuditAreaRef(5, 1, FloorAreaKind.Classroom, 7, true), classOnly));
        Assert.Contains("(C7 only)", classOnly.Label);
    }

    [Fact]
    public void A_round_is_completed_only_when_every_required_area_is_inspected_never_because_time_passed()
    {
        var s = FloorAuditSchedule.Build(Day, [L(1, new TimeOnly(8, 0), 1)]).Sessions[0];
        AuditRoundStatus At(int h, int m, int required, int completed, int started) =>
            FloorAuditSchedule.RoundStatus(s, 1, Day, Day, new TimeOnly(h, m), required, completed, started);
        Assert.Equal(AuditRoundStatus.Upcoming, At(8, 5, 3, 0, 0));
        Assert.Equal(AuditRoundStatus.Due, At(8, 6, 3, 0, 0));
        Assert.Equal(AuditRoundStatus.InProgress, At(8, 10, 3, 1, 2));
        Assert.Equal(AuditRoundStatus.Missed, At(8, 25, 3, 0, 0));       // round 2's time: round 1 is over
        Assert.Equal(AuditRoundStatus.Missed, At(8, 30, 3, 2, 3));       // 2 of 3 is not completed
        Assert.Equal(AuditRoundStatus.Completed, At(8, 30, 3, 3, 3));
        Assert.Equal(AuditRoundStatus.Completed, At(8, 7, 2, 2, 2));
        // A past day: still not completed without the inspections; a future day: upcoming.
        Assert.Equal(AuditRoundStatus.Missed, FloorAuditSchedule.RoundStatus(s, 3, Day, Day.AddDays(1), new TimeOnly(7, 0), 3, 0, 0));
        Assert.Equal(AuditRoundStatus.Upcoming, FloorAuditSchedule.RoundStatus(s, 1, Day.AddDays(1), Day, new TimeOnly(9, 0), 3, 0, 0));
        // Round 3 stays due until the session ends.
        Assert.Equal(AuditRoundStatus.Due, FloorAuditSchedule.RoundStatus(s, 3, Day, Day, new TimeOnly(8, 44), 3, 0, 0));
        // Area statuses.
        Assert.Equal(AreaAuditStatus.NotStarted, FloorAuditSchedule.AreaStatus(true, null, 0, false));
        Assert.Equal(AreaAuditStatus.Pending, FloorAuditSchedule.AreaStatus(true, null, 0, true));
        Assert.Equal(AreaAuditStatus.InProgress, FloorAuditSchedule.AreaStatus(true, FloorInspectionStatus.InProgress, 0, true));
        Assert.Equal(AreaAuditStatus.IssuesFound, FloorAuditSchedule.AreaStatus(true, FloorInspectionStatus.Completed, 2, true));
        Assert.Equal(AreaAuditStatus.Completed, FloorAuditSchedule.AreaStatus(true, FloorInspectionStatus.Completed, 0, true));
        Assert.Equal(AreaAuditStatus.NotScheduled, FloorAuditSchedule.AreaStatus(false, null, 0, true));
    }

    // ---------------------------------------------------------------- checklist responses (pure)

    [Theory]
    [InlineData(AuditResponseType.Number, "23", "23", false, null)]
    [InlineData(AuditResponseType.Number, "٢٣", "23", false, null)]
    [InlineData(AuditResponseType.Number, "-1", null, false, "whole number")]
    [InlineData(AuditResponseType.Number, "abc", null, false, "whole number")]
    [InlineData(AuditResponseType.Number, "10000", null, false, "whole number")]
    [InlineData(AuditResponseType.PassFail, "pass", "Pass", false, null)]
    [InlineData(AuditResponseType.PassFail, "Fail", "Fail", true, null)]
    [InlineData(AuditResponseType.PassFail, "Maybe", null, false, "Pass or Fail")]
    [InlineData(AuditResponseType.YesNo, "No", "No", true, null)]
    [InlineData(AuditResponseType.GoodNeedsAttention, "needs attention", "Needs Attention", true, null)]
    [InlineData(AuditResponseType.PresentAbsent, "Absent", "Absent", true, null)]
    [InlineData(AuditResponseType.PresentAbsent, "Present", "Present", false, null)]
    [InlineData(AuditResponseType.Text, "Window open", "Window open", false, null)]
    [InlineData(AuditResponseType.IssueText, "Broken tap", "Broken tap", true, null)]
    [InlineData(AuditResponseType.IssueText, "  ", null, false, null)]
    public void Each_response_type_is_validated_in_its_own_way(AuditResponseType type, string raw, string? value, bool issue, string? error)
    {
        var (v, e) = AuditResponses.Clean(type, raw);
        Assert.Equal(value, v);
        if (error is null) Assert.Null(e); else Assert.Contains(error, e);
        Assert.Equal(issue, AuditResponses.IsIssue(type, v));
    }

    // ---------------------------------------------------------------- floors, areas, checklist (database)

    [Fact]
    public async Task Four_floors_and_the_default_checklist_exist_from_the_start()
    {
        await using var db = fx.NewContext();
        var floors = await db.Floors.Where(f => f.Id <= 4).OrderBy(f => f.SortOrder).Select(f => f.Name).ToListAsync();
        Assert.Equal(["Ground Floor", "1st Floor", "2nd Floor", "3rd Floor"], floors);
        var defaults = await db.FloorAuditItems.Where(i => i.IsDefault).ToListAsync();
        Assert.Equal(15, defaults.Count);
        Assert.Contains(defaults, i => i.Name == "Student Count" && i.ResponseType == AuditResponseType.Number && i.AppliesTo == AuditItemScope.Classroom);
        Assert.Contains(defaults, i => i.Name == "Teacher / Staff Presence" && i.ResponseType == AuditResponseType.PresentAbsent);
        Assert.Contains(defaults, i => i.Name == "Safety" && i.ResponseType == AuditResponseType.PassFail && i.AppliesTo == AuditItemScope.Facility);
        Assert.Equal(8, defaults.Count(i => i.AppliesTo == AuditItemScope.Classroom));
        Assert.Equal(7, defaults.Count(i => i.AppliesTo == AuditItemScope.Facility));
    }

    [Fact]
    public async Task Classrooms_are_existing_classes_on_one_floor_and_moving_one_keeps_its_history_on_the_old_floor()
    {
        var (cls, slot, _) = await ClassWithLessonAsync(new TimeOnly(8, 0));
        var area = await RoomAsync(cls, 2);
        await using (var db = fx.NewContext())
        {
            var a = await db.FloorAreas.Include(x => x.SchoolClass).SingleAsync(x => x.Id == area);
            Assert.Equal(FloorAreaKind.Classroom, a.Kind);
            Assert.Equal(cls, a.SchoolClassId);
            Assert.Null(a.Name);   // the class's own name is shown; nothing is copied or created
            Assert.Equal(1, await db.Classes.CountAsync(c => c.Id == cls));
        }
        // The same class cannot be put on a second floor.
        var mgr = await StaffWithAsync(FloorAuditRole.Manager);
        await using (var db = fx.NewContext())
        {
            var s = For(db, new User(mgr.Id, mgr.Name), ClockAt(Day, 8, 7));
            var dup = await s.Config.AssignClassroomsAsync(3, [cls]);
            Assert.False(dup.Succeeded);
            Assert.Contains("already on 1st Floor", dup.Errors[0].Value);
            Assert.Equal(1, await db.FloorAreas.CountAsync(a => a.SchoolClassId == cls));

            // An inspection on the 1st Floor, then the classroom moves to the 3rd Floor.
            var form = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
            form.Answers = AllAnswers(form);
            Assert.True((await s.Audit.SaveInspectionAsync(form, true, [])).Succeeded);
            Assert.True((await s.Config.MoveAreaAsync(area, 4)).Succeeded);
        }
        await using (var db = fx.NewContext())
        {
            Assert.Equal(4, await db.FloorAreas.Where(a => a.Id == area).Select(a => a.FloorId).SingleAsync());
            var old = await db.FloorInspections.SingleAsync(i => i.FloorAreaId == area);
            Assert.Equal(2, old.FloorId);   // still recorded on the floor it was made on
            var s = For(db, new User(mgr.Id, mgr.Name), ClockAt(Day, 8, 26));
            var page = (await s.Audit.FloorAsync(4, Day, slot, 2))!;
            Assert.Contains(page.Classrooms, c => c.Area.Id == area && c.Required);   // new rounds use the new floor
            Assert.DoesNotContain((await s.Audit.FloorAsync(2, Day, slot, 2))!.Classrooms, c => c.Area.Id == area);
            Assert.Single((await s.Audit.AreaAsync(area))!.History);
        }
    }

    [Fact]
    public async Task Facilities_belong_to_their_floor_and_names_are_unique_per_floor()
    {
        var mgr = await StaffWithAsync(FloorAuditRole.Manager);
        await using var db = fx.NewContext();
        var s = For(db, new User(mgr.Id, mgr.Name), ClockAt(Day, 7, 0));
        var name = "Bathroom " + DbFixture.Unique()[..6];
        var added = await s.Config.SaveFacilityAsync(new FacilityForm { FloorId = 3, Name = name, FacilityType = FacilityType.Bathroom, IsActive = true });
        Assert.True(added.Succeeded);
        var f = await db.FloorAreas.SingleAsync(a => a.Id == added.EntityId);
        Assert.Equal((3, FloorAreaKind.Facility, FacilityType.Bathroom, (int?)null), (f.FloorId, f.Kind, f.FacilityType, f.SchoolClassId));
        Assert.False((await s.Config.SaveFacilityAsync(new FacilityForm { FloorId = 3, Name = name.ToUpperInvariant(), FacilityType = FacilityType.Bathroom })).Succeeded);
        Assert.True((await s.Config.SaveFacilityAsync(new FacilityForm { FloorId = 4, Name = name, FacilityType = FacilityType.Bathroom, IsActive = true })).Succeeded);
        Assert.False((await s.Config.SaveFacilityAsync(new FacilityForm { FloorId = 3, Name = "No type " + DbFixture.Unique() })).Succeeded);
        // Deactivated, never deleted: it leaves the rounds and keeps its row.
        Assert.True((await s.Config.SetAreaActiveAsync(f.Id, false)).Succeeded);
        Assert.False(await db.FloorAreas.Where(a => a.Id == f.Id).Select(a => a.IsActive).SingleAsync());
        Assert.Contains(await db.FloorAuditLog.Where(l => l.Entity == "Area" && l.EntityId == f.Id).Select(l => l.Action).ToListAsync(), a => a == "Deactivated");
    }

    // ---------------------------------------------------------------- inspections (database)

    [Fact]
    public async Task An_inspection_is_saved_in_progress_completed_with_its_required_items_and_completes_the_round()
    {
        var (cls, slot, _) = await ClassWithLessonAsync(new TimeOnly(8, 0));
        var area = await RoomAsync(cls, 1);
        var inspector = await StaffWithAsync(FloorAuditRole.Inspector, 1);
        await using var db = fx.NewContext();
        var s = For(db, new User(inspector.Id, inspector.Name), ClockAt(Day, 8, 10));

        var schedule = await s.Audit.ScheduleAsync(Day);
        var session = Assert.Single(schedule.Sessions, x => x.SlotId == slot);
        Assert.Equal([new TimeOnly(8, 6), new TimeOnly(8, 25), new TimeOnly(8, 40)], session.RoundTimes);

        var form = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
        Assert.False(form.ReadOnly, form.ReadOnlyReason);
        Assert.Null(form.InspectionId);          // opening the form records nothing
        Assert.Equal(0, await db.FloorInspections.CountAsync(i => i.FloorAreaId == area));

        // Saved in progress with one answer.
        var count = form.Questions.First(q => q.Name == "Student Count");
        form.Answers = new() { [count.ItemId] = "19" };
        var saved = await s.Audit.SaveInspectionAsync(form, false, []);
        Assert.True(saved.Succeeded, string.Join("; ", saved.Errors.Select(e => e.Value)));
        var i1 = await db.FloorInspections.AsNoTracking().Include(i => i.Responses).SingleAsync(i => i.Id == saved.EntityId);
        Assert.Equal((FloorInspectionStatus.InProgress, (DateTime?)null, inspector.Id), (i1.Status, i1.CompletedAt, i1.InspectorStaffId));
        Assert.Equal((new TimeOnly(8, 6), new TimeOnly(8, 0), 1, 1), (i1.ScheduledTime, i1.SessionStart, i1.Round, i1.FloorId));
        Assert.Equal(DateTime.SpecifyKind(new DateTime(2031, 3, 12, 6, 10, 0), DateTimeKind.Unspecified), i1.StartedAt);   // 08:10 Cairo, stored UTC
        Assert.Equal("19", i1.Responses.Single(r => r.ItemId == count.ItemId).Value);
        var page = (await s.Audit.FloorAsync(1, Day, slot, 1))!;
        Assert.Equal(AreaAuditStatus.InProgress, page.Classrooms.Single(c => c.Area.Id == area).Status);

        // Completing needs every required item; a wrong type is refused.
        form = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
        form.Answers = new() { [count.ItemId] = "nineteen" };
        Assert.False((await s.Audit.SaveInspectionAsync(form, true, [])).Succeeded);
        form.Answers = new() { [count.ItemId] = "19" };
        var missing = await s.Audit.SaveInspectionAsync(form, true, []);
        Assert.False(missing.Succeeded);
        Assert.Contains(missing.Errors, e => e.Value.Contains("is required to complete"));

        // Complete, with an issue (cleanliness needs attention).
        form.Answers = AllAnswers(form);
        form.Answers[form.Questions.First(q => q.Name == "Classroom Cleanliness").ItemId] = "Needs Attention";
        form.Observations = "Paper on the floor.";
        var done = await s.Audit.SaveInspectionAsync(form, true, []);
        Assert.True(done.Succeeded, string.Join("; ", done.Errors.Select(e => e.Value)));
        var i2 = await db.FloorInspections.AsNoTracking().SingleAsync(i => i.Id == done.EntityId);
        Assert.Equal(FloorInspectionStatus.Completed, i2.Status);
        Assert.NotNull(i2.CompletedAt);
        Assert.Equal(1, i2.IssueCount);
        Assert.Equal("Classroom Cleanliness: Needs Attention", i2.IssueSummary);

        page = (await s.Audit.FloorAsync(1, Day, slot, 1))!;
        Assert.Equal(AreaAuditStatus.IssuesFound, page.Classrooms.Single(c => c.Area.Id == area).Status);
        var round = page.Rounds.Single(r => r.Round == 1);
        Assert.Equal(round.Required, round.Completed + round.Pending);
        Assert.Contains(await db.FloorAuditLog.Where(l => l.Entity == "Inspection" && l.EntityId == i2.Id).Select(l => l.Action).ToListAsync(), a => a == "Completed");

        // Only a manager corrects a completed inspection, with a reason.
        form = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
        Assert.True(form.ReadOnly);
        Assert.False((await s.Audit.SaveInspectionAsync(form, true, [])).Succeeded);
    }

    [Fact]
    public async Task Rounds_cannot_be_recorded_before_their_time_nor_by_people_without_access()
    {
        var (cls, slot, _) = await ClassWithLessonAsync(new TimeOnly(8, 0));
        var area = await RoomAsync(cls, 3);
        var other = await StaffWithAsync(FloorAuditRole.Inspector, 1);
        var nobody = await StaffWithAsync(null);
        var everyFloor = await StaffWithAsync(FloorAuditRole.Inspector);
        await using var db = fx.NewContext();

        async Task<string?> Attempt(ICurrentUser user, int h, int m, int round)
        {
            var s = For(db, user, ClockAt(Day, h, m));
            var form = (await s.Audit.InspectionFormAsync(area, Day, slot, round))!;
            form.Answers = AllAnswers(form);
            var r = await s.Audit.SaveInspectionAsync(form, true, []);
            return r.Succeeded ? null : r.Errors[0].Value;
        }

        Assert.Contains("starts at 8:25 AM", await Attempt(new User(everyFloor.Id, everyFloor.Name), 8, 20, 2));
        Assert.Contains("Working as", await Attempt(new User(null, null), 8, 30, 1));
        Assert.Contains("not an inspector of this floor", await Attempt(new User(other.Id, other.Name), 8, 30, 1));
        Assert.Contains("not an inspector of this floor", await Attempt(new User(nobody.Id, nobody.Name), 8, 30, 1));
        Assert.Equal(0, await db.FloorInspections.CountAsync(i => i.FloorAreaId == area));
        Assert.Null(await Attempt(new User(everyFloor.Id, everyFloor.Name), 8, 30, 1));
        // One inspection per area, date, session and round.
        Assert.Equal(1, await db.FloorInspections.CountAsync(i => i.FloorAreaId == area && i.Round == 1));
    }

    [Fact]
    public async Task A_cancelled_session_has_no_rounds_and_a_rescheduled_one_keeps_completed_times()
    {
        var (cls, slot, lesson) = await ClassWithLessonAsync(new TimeOnly(8, 0));
        var area = await RoomAsync(cls, 2);
        var insp = await StaffWithAsync(FloorAuditRole.Inspector);
        await using (var db = fx.NewContext())
        {
            var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 8, 7));
            var form = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
            form.Answers = AllAnswers(form);
            Assert.True((await s.Audit.SaveInspectionAsync(form, true, [])).Succeeded);
        }

        // The period moves to 09:00: future rounds follow it; the completed one keeps 08:00 / 08:06.
        await using (var db = fx.NewContext())
        {
            var p = await db.TimeSlots.SingleAsync(x => x.Id == slot);
            p.StartTime = new TimeOnly(9, 0);
            p.EndTime = new TimeOnly(9, 45);
            await db.SaveChangesAsync();
            var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 9, 7));
            var session = (await s.Audit.ScheduleAsync(Day)).Get(slot)!;
            Assert.Equal([new TimeOnly(9, 6), new TimeOnly(9, 25), new TimeOnly(9, 40)], session.RoundTimes);
            var kept = await db.FloorInspections.AsNoTracking().SingleAsync(i => i.FloorAreaId == area && i.Round == 1);
            Assert.Equal((new TimeOnly(8, 0), new TimeOnly(8, 6)), (kept.SessionStart, kept.ScheduledTime));
            Assert.Equal(FloorInspectionStatus.Completed, kept.Status);
        }

        // The lesson is cancelled on that date: the session has no rounds; nothing new can be recorded; history stays.
        await using (var db = fx.NewContext())
        {
            db.SessionCancellations.Add(new SessionCancellation { TimetableAssignmentId = lesson, SessionDate = Day, Reason = "Trip", CancelledAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 9, 30));
            var session = (await s.Audit.ScheduleAsync(Day)).Get(slot)!;
            Assert.True(session.Cancelled);
            var form = (await s.Audit.InspectionFormAsync(area, Day, slot, 2))!;
            Assert.True(form.ReadOnly);
            Assert.Contains("cancelled", form.ReadOnlyReason);
            form.Answers = AllAnswers(form);
            Assert.False((await s.Audit.SaveInspectionAsync(form, true, [])).Succeeded);
            Assert.Equal(1, await db.FloorInspections.CountAsync(i => i.FloorAreaId == area));
        }
    }

    [Fact]
    public async Task Custom_items_join_new_inspections_and_editing_them_never_changes_recorded_ones()
    {
        var (cls, slot, _) = await ClassWithLessonAsync(new TimeOnly(8, 0));
        var area = await RoomAsync(cls, 1);
        var mgr = await StaffWithAsync(FloorAuditRole.Manager);
        await using var db = fx.NewContext();
        var s = For(db, new User(mgr.Id, mgr.Name), ClockAt(Day, 8, 8));
        var name = "Windows closed " + DbFixture.Unique()[..5];
        var created = await s.Config.SaveItemAsync(new AuditItemForm { Name = name, AppliesTo = AuditItemScope.Classroom, ResponseType = AuditResponseType.YesNo, IsRequired = true, IsActive = true });
        Assert.True(created.Succeeded);
        try
        {
            var form = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
            var q = Assert.Single(form.Questions, x => x.ItemId == created.EntityId);
            Assert.Equal((AuditResponseType.YesNo, true), (q.Type, q.Required));
            form.Answers = AllAnswers(form);
            Assert.True((await s.Audit.SaveInspectionAsync(form, true, [])).Succeeded);

            // The item is renamed, made optional and then deactivated.
            Assert.True((await s.Config.SaveItemAsync(new AuditItemForm { Id = created.EntityId, Name = name + " (edited)", AppliesTo = AuditItemScope.Classroom,
                ResponseType = AuditResponseType.PassFail, IsRequired = false, IsActive = true })).Succeeded);
            var recorded = await db.FloorInspectionResponses.AsNoTracking().SingleAsync(r => r.ItemId == created.EntityId);
            Assert.Equal((name, AuditResponseType.YesNo, true, "Yes"), (recorded.ItemName, recorded.ResponseType, recorded.IsRequired, recorded.Value));

            var s2 = For(db, new User(mgr.Id, mgr.Name), ClockAt(Day, 8, 26));
            var next = (await s2.Audit.InspectionFormAsync(area, Day, slot, 2))!;
            Assert.Equal(name + " (edited)", Assert.Single(next.Questions, x => x.ItemId == created.EntityId).Name);
            Assert.True((await s.Config.SetItemActiveAsync(created.EntityId!.Value, false)).Succeeded);
            next = (await s2.Audit.InspectionFormAsync(area, Day, slot, 2))!;
            Assert.DoesNotContain(next.Questions, x => x.ItemId == created.EntityId);
            Assert.Equal(name, (await db.FloorInspectionResponses.AsNoTracking().SingleAsync(r => r.ItemId == created.EntityId)).ItemName);
        }
        finally
        {
            await s.Config.SetItemActiveAsync(created.EntityId!.Value, false);
        }
    }

    [Fact]
    public async Task Photos_must_be_real_images_or_pdfs()
    {
        var (cls, slot, _) = await ClassWithLessonAsync(new TimeOnly(8, 0));
        var area = await RoomAsync(cls, 1);
        var insp = await StaffWithAsync(FloorAuditRole.Inspector);
        await using var db = fx.NewContext();
        var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 8, 9));
        var form = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
        form.Answers = AllAnswers(form);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var fake = new UploadedFile("photo.png", 5, () => new MemoryStream("hello"u8.ToArray()));
        Assert.False((await s.Audit.SaveInspectionAsync(form, false, [fake])).Succeeded);
        Assert.False((await s.Audit.SaveInspectionAsync(form, false, [new UploadedFile("run.exe", 3, () => new MemoryStream([1, 2, 3]))])).Succeeded);
        var ok = await s.Audit.SaveInspectionAsync(form, true, [new UploadedFile("photo.png", png.Length, () => new MemoryStream(png))]);
        Assert.True(ok.Succeeded);
        var att = await db.FloorInspectionAttachments.SingleAsync(a => a.InspectionId == ok.EntityId);
        Assert.Equal(("photo.png", "png"), (att.FileName, att.Extension));
        Assert.True(s.Files.Files.ContainsKey(att.StoredName));
    }

    // ---------------------------------------------------------------- student movements (database)

    [Fact]
    public async Task A_movement_records_the_real_student_class_destination_and_times_and_never_invents_a_return()
    {
        var cls = await fx.NewClassAsync();
        var (studentId, studentName) = await fx.NewStudentAsync(cls);
        var room = await RoomAsync(cls, 1);
        var mgr = await StaffWithAsync(FloorAuditRole.Manager);
        int nurse;
        await using (var db = fx.NewContext())
        {
            var s = For(db, new User(mgr.Id, mgr.Name), ClockAt(Day, 9, 0));
            nurse = (await s.Config.SaveFacilityAsync(new FacilityForm { FloorId = 3, Name = "Nurse " + DbFixture.Unique()[..6], FacilityType = FacilityType.NurseRoom, IsActive = true })).EntityId!.Value;
        }
        var insp = await StaffWithAsync(FloorAuditRole.Inspector, 1);
        int arrivalsBefore, sessionsBefore;
        await using (var db = fx.NewContext())
        {
            arrivalsBefore = await db.StudentArrivals.CountAsync(a => a.StudentId == studentId);
            sessionsBefore = await db.SessionAttendance.CountAsync(a => a.StudentId == studentId);
        }

        int movementId;
        await using (var db = fx.NewContext())
        {
            var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 10, 0));
            var before = await s.Movements.CountsAsync(Day, null);
            var r = await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId, ToAreaId = nurse });
            Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
            movementId = r.EntityId!.Value;
            var m = await db.StudentMovements.AsNoTracking().SingleAsync(x => x.Id == movementId);
            Assert.Equal((cls, (int?)room, (int?)1, (int?)nurse, (int?)3, Day), (m.FromClassId, m.FromAreaId, m.FromFloorId, m.ToAreaId, m.ToFloorId, m.Date));
            Assert.Equal(new DateTime(2031, 3, 12, 8, 0, 0), m.ExitAt);   // 10:00 Cairo in UTC
            Assert.Null(m.ReturnAt);
            Assert.Equal((insp.Id, insp.Name), (m.RecordedByStaffId!.Value, m.RecordedBy));
            var after = await s.Movements.CountsAsync(Day, null);
            Assert.Equal(before.Outside + 1, after.Outside);
            Assert.Equal(before.Total + 1, after.Total);

            // Already outside: a second open movement is refused and the existing one is described.
            var dup = await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "Gate" });
            Assert.False(dup.Succeeded);
            Assert.Contains($"{studentName} is already outside", dup.Errors[0].Value);
            Assert.Equal(1, await db.StudentMovements.CountAsync(x => x.StudentId == studentId));
        }

        // 12 minutes later, still out: the elapsed time is shown, nothing is stored.
        await using (var db = fx.NewContext())
        {
            var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 10, 12));
            var row = (await s.Movements.RowAsync(movementId))!;
            Assert.Equal((TimeSpan.FromMinutes(12), MovementState.Overdue, (DateTime?)null), (row.Duration, row.State, row.ReturnAt));
            Assert.Null(await db.StudentMovements.Where(x => x.Id == movementId).Select(x => x.ReturnAt).SingleAsync());
            // A return before the exit is refused.
            Assert.False((await s.Movements.ReturnAsync(movementId, "09:55")).Succeeded);
            var before = await s.Movements.CountsAsync(Day, null);
            Assert.True((await s.Movements.ReturnAsync(movementId, "10:09")).Succeeded);
            var m = await db.StudentMovements.AsNoTracking().SingleAsync(x => x.Id == movementId);
            Assert.Equal(new DateTime(2031, 3, 12, 8, 9, 0), m.ReturnAt);
            Assert.Equal(insp.Name, m.ReturnedBy);
            row = (await s.Movements.RowAsync(movementId))!;
            Assert.Equal((TimeSpan.FromMinutes(9), MovementState.Returned), (row.Duration, row.State));
            var after = await s.Movements.CountsAsync(Day, null);
            Assert.Equal(before.Outside - 1, after.Outside);
            Assert.Equal(before.Returned + 1, after.Returned);
            Assert.False((await s.Movements.ReturnAsync(movementId, null)).Succeeded);   // returned once
            // A new movement is allowed once the student is back.
            Assert.True((await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "Parent at the gate" })).Succeeded);
        }

        // Attendance is untouched.
        await using (var db = fx.NewContext())
        {
            Assert.Equal(arrivalsBefore, await db.StudentArrivals.CountAsync(a => a.StudentId == studentId));
            Assert.Equal(sessionsBefore, await db.SessionAttendance.CountAsync(a => a.StudentId == studentId));
            Assert.Equal(cls, await db.Students.Where(x => x.Id == studentId).Select(x => x.SchoolClassId).SingleAsync());
        }
    }

    [Fact]
    public async Task The_database_allows_one_open_movement_per_student_and_voiding_needs_a_manager_and_a_reason()
    {
        var cls = await fx.NewClassAsync();
        var (studentId, _) = await fx.NewStudentAsync(cls);
        var insp = await StaffWithAsync(FloorAuditRole.Inspector, movements: true);
        var mgr = await StaffWithAsync(FloorAuditRole.Manager);
        int id;
        await using (var db = fx.NewContext())
        {
            var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 11, 0));
            id = (await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "Office" })).EntityId!.Value;
            // A direct second open row breaks the unique filtered index.
            var m = await db.StudentMovements.AsNoTracking().SingleAsync(x => x.Id == id);
            db.StudentMovements.Add(new StudentMovement { StudentId = studentId, Date = Day, FromClassId = cls, ToName = "Elsewhere", ExitAt = m.ExitAt.AddMinutes(1) });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = fx.NewContext())
        {
            var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 11, 5));
            Assert.Contains("manager", (await s.Movements.VoidAsync(id, "Wrong student")).Errors[0].Value);
            var m = For(db, new User(mgr.Id, mgr.Name), ClockAt(Day, 11, 5));
            Assert.False((await m.Movements.VoidAsync(id, " ")).Succeeded);
            Assert.True((await m.Movements.VoidAsync(id, "Wrong student")).Succeeded);
            var row = (await m.Movements.RowAsync(id))!;
            Assert.Equal((MovementState.Voided, "Wrong student", mgr.Name), (row.State, row.VoidReason, row.VoidedBy));
            Assert.True(await db.StudentMovements.AnyAsync(x => x.Id == id));   // kept, never deleted
            Assert.True((await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "Office" })).Succeeded);
        }
    }

    [Fact]
    public async Task Movements_need_a_valid_student_destination_and_permission()
    {
        var cls = await fx.NewClassAsync();
        var (studentId, _) = await fx.NewStudentAsync(cls);
        await RoomAsync(cls, 2);
        var noMovements = await StaffWithAsync(FloorAuditRole.Inspector, 2, movements: false);
        var otherFloor = await StaffWithAsync(FloorAuditRole.Inspector, 4);
        var ok = await StaffWithAsync(FloorAuditRole.Inspector, 2);
        await using var db = fx.NewContext();
        var clock = ClockAt(Day, 12, 0);
        Assert.Contains("Working as", (await For(db, new User(null, null), clock).Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "X" })).Errors[0].Value);
        Assert.Contains("not allowed", (await For(db, new User(noMovements.Id, noMovements.Name), clock).Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "X" })).Errors[0].Value);
        Assert.Contains("not allowed", (await For(db, new User(otherFloor.Id, otherFloor.Name), clock).Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "X" })).Errors[0].Value);
        var s = For(db, new User(ok.Id, ok.Name), clock);
        Assert.Equal("StudentId", (await s.Movements.RecordAsync(new MovementFormModel { StudentId = -5, Custom = true, CustomDestination = "X" })).Errors[0].Key);
        Assert.Equal("ToAreaId", (await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId })).Errors[0].Key);
        Assert.Equal("CustomDestination", (await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true })).Errors[0].Key);
        Assert.Equal("ExitTime", (await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "X", ExitTime = "13:30" })).Errors[0].Key);
        Assert.Equal(0, await db.StudentMovements.CountAsync(m => m.StudentId == studentId));
        var r = await s.Movements.RecordAsync(new MovementFormModel { StudentId = studentId, Custom = true, CustomDestination = "Main gate", ExitTime = "11:52" });
        Assert.True(r.Succeeded);
        var m = await db.StudentMovements.AsNoTracking().SingleAsync(x => x.Id == r.EntityId);
        Assert.Equal((true, "Main gate", (int?)null, new DateTime(2031, 3, 12, 9, 52, 0)), (m.IsCustomDestination, m.ToName, m.ToAreaId, m.ExitAt));
    }

    // ---------------------------------------------------------------- pages and permissions (HTTP)

    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    /// <summary>A browser-like client; with a staff id it first chooses that person under "Working as" (the operator cookie).</summary>
    private async Task<HttpClient> ClientAsync(int? staffId)
    {
        var c = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (staffId is { } id)
        {
            var token = Token.Match(await c.GetStringAsync("/settings")).Groups[1].Value;
            var r = await c.PostAsync("/operator", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("staffId", id.ToString()), new("returnUrl", "/floor-audit")]));
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        return c;
    }

    [Fact]
    public async Task The_pages_render_and_the_navigation_has_Floor_Audit()
    {
        var client = await ClientAsync(null);
        var home = WebUtility.HtmlDecode(await client.GetStringAsync("/floor-audit"));
        Assert.Contains("<h1 class=\"page-title\">Floor Audit</h1>", home);
        Assert.Contains("href=\"/floor-audit\"", home);
        foreach (var f in new[] { "Ground Floor", "1st Floor", "2nd Floor", "3rd Floor" }) Assert.Contains(f, home);
        Assert.Contains("View only — nobody is chosen", home);
        Assert.Contains("action=\"/operator\"", home);   // "Working as" can be chosen right here   // no "Working as": nothing can be recorded
        Assert.DoesNotContain("href=\"/floor-audit/movements/new\"", home);
        foreach (var url in new[] { "/floor-audit/floors/1", "/floor-audit/movements", "/floor-audit/history", "/floor-audit/manage", "/floor-audit/manage?tab=checklist", "/floor-audit/manage?tab=access" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/floor-audit/floors/99999")).StatusCode);
        var floor = WebUtility.HtmlDecode(await client.GetStringAsync("/floor-audit/floors/1"));
        foreach (var t in new[] { "Student Access", "View Audit History", "Classrooms", "Facilities" }) Assert.Contains(t, floor);
        Assert.DoesNotContain("Manage Floor Areas", floor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/floor-audit/history/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/floor-audit/movements/excel")).StatusCode);
    }

    [Fact]
    public async Task Only_managers_change_the_configuration()
    {
        var inspector = await StaffWithAsync(FloorAuditRole.Inspector, 1);
        var manager = await StaffWithAsync(FloorAuditRole.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ClientAsync(null)).GetAsync("/floor-audit/manage/items/new")).StatusCode);
        var client = await ClientAsync(inspector.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/floor-audit/manage/items/new")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/floor-audit/manage/facilities/new")).StatusCode);

        // A form token from a page the inspector can open; the save itself is refused.
        var token = Token.Match(await client.GetStringAsync("/floor-audit/movements/new")).Groups[1].Value;
        var name = "Hacked " + DbFixture.Unique();
        var post = new HttpRequestMessage(HttpMethod.Post, "/floor-audit/manage/items/save")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("Name", name), new("AppliesTo", "Both"), new("ResponseType", "PassFail")]),
        };
        post.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var refused = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        await using (var db = fx.NewContext()) Assert.False(await db.FloorAuditItems.AnyAsync(i => i.Name == name));

        var mgr = await ClientAsync(manager.Id);
        Assert.Equal(HttpStatusCode.OK, (await mgr.GetAsync("/floor-audit/manage/items/new")).StatusCode);
        var page = WebUtility.HtmlDecode(await mgr.GetStringAsync("/floor-audit/manage?tab=checklist"));
        Assert.Contains("Add Audit Item", page);
    }

    [Fact]
    public async Task Additional_observations_and_issues_are_saved_with_the_inspection_and_counted_as_issues()
    {
        var (cls, slot, _) = await ClassWithLessonAsync(new TimeOnly(8, 0));
        var area = await RoomAsync(cls, 1);
        var insp = await StaffWithAsync(FloorAuditRole.Inspector, 1);
        await using var db = fx.NewContext();
        var s = For(db, new User(insp.Id, insp.Name), ClockAt(Day, 8, 9));
        var form = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
        Assert.Equal(insp.Name, form.InspectorName);
        form.Answers = AllAnswers(form);
        form.Extras =
        [
            new ExtraFinding { Kind = ExtraFinding.Observation, Text = "Window left open" },
            new ExtraFinding { Kind = ExtraFinding.Issue, About = "Classroom Cleanliness", Text = "Bin overflowing" },
            new ExtraFinding { Kind = ExtraFinding.Issue, Text = "   " },   // empty rows are ignored
        ];
        var saved = await s.Audit.SaveInspectionAsync(form, false, []);
        Assert.True(saved.Succeeded, string.Join("; ", saved.Errors.Select(e => e.Value)));

        // Reopened ("Continue"): the checklist and the findings come back separately.
        var again = (await s.Audit.InspectionFormAsync(area, Day, slot, 1))!;
        Assert.Equal(form.Questions.Count, again.Questions.Count);
        Assert.Equal(2, again.Extras.Count);
        Assert.Contains(again.Extras, e => e.IsIssue && e.About == "Classroom Cleanliness" && e.Text == "Bin overflowing");
        Assert.Equal(1, await db.FloorInspections.Where(i => i.Id == saved.EntityId).Select(i => i.IssueCount).SingleAsync());

        // A wrong kind is refused; the findings are replaced as a whole on the next save.
        again.Answers = AllAnswers(again);
        again.Extras = [new ExtraFinding { Kind = "maybe", Text = "x" }];
        Assert.False((await s.Audit.SaveInspectionAsync(again, true, [])).Succeeded);
        again.Extras = [new ExtraFinding { Kind = ExtraFinding.Issue, Text = "Broken chair" }];
        Assert.True((await s.Audit.SaveInspectionAsync(again, true, [])).Succeeded);
        var extras = await db.FloorInspectionResponses.AsNoTracking().Where(r => r.InspectionId == saved.EntityId && r.ItemId == null).ToListAsync();
        var only = Assert.Single(extras);
        Assert.Equal(("Additional issue", "Broken chair", true, AuditResponseType.IssueText), (only.ItemName, only.Value, only.IsIssue, only.ResponseType));
        var done = await db.FloorInspections.AsNoTracking().SingleAsync(i => i.Id == saved.EntityId);
        Assert.Equal((FloorInspectionStatus.Completed, 1, "Additional issue: Broken chair"), (done.Status, done.IssueCount, done.IssueSummary));
    }

    [Fact]
    public async Task The_inspections_page_and_the_student_access_board_and_history_render()
    {
        var inspector = await StaffWithAsync(FloorAuditRole.Inspector, 1);
        var client = await ClientAsync(inspector.Id);
        var inspections = WebUtility.HtmlDecode(await client.GetStringAsync("/floor-audit/inspections"));
        Assert.Contains("Classroom & Facility Inspections", inspections);
        Assert.Contains("href=\"/floor-audit/inspections?show=pending\"", inspections);   // Record Audit
        Assert.Contains("Recording as", inspections);
        var board = WebUtility.HtmlDecode(await client.GetStringAsync("/floor-audit/movements"));
        Assert.Contains("Outside now", board);
        Assert.Contains("href=\"/floor-audit/movements/new\" data-modal", board);   // Record Student Movement
        var history = WebUtility.HtmlDecode(await client.GetStringAsync("/floor-audit/movements/history"));
        Assert.Contains("Movement History", history);
        foreach (var url in new[] { "/floor-audit/movements/history/pdf", "/floor-audit/movements/history/excel", "/floor-audit/movements/pdf", "/floor-audit/movements/excel" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        // The movement form fills the class and floors in from the student; the departure time is the server's.
        var formHtml = WebUtility.HtmlDecode(await client.GetStringAsync("/floor-audit/movements/new"));
        Assert.Contains("data-fa-class", formHtml);
        Assert.Contains("Recorded by the server when you save", formHtml);
        Assert.DoesNotContain("name=\"ExitTime\"", formHtml);
    }

    [Fact]
    public async Task Mark_Returned_records_the_server_time_whatever_time_is_posted()
    {
        var cls = await fx.NewClassAsync();
        var (studentId, _) = await fx.NewStudentAsync(cls);
        var inspector = await StaffWithAsync(FloorAuditRole.Inspector);   // every floor, may record movements
        int id;
        await using (var db = fx.NewContext())
        {
            var m = new StudentMovement { StudentId = studentId, Date = SchoolWeek.Today, FromClassId = cls, ToName = "Office", IsCustomDestination = true,
                ExitAt = DateTime.UtcNow.AddMinutes(-5), RecordedBy = "Test runner" };
            db.StudentMovements.Add(m);
            await db.SaveChangesAsync();
            id = m.Id;
        }
        var client = await ClientAsync(inspector.Id);
        var token = Token.Match(await client.GetStringAsync("/floor-audit/movements")).Groups[1].Value;
        var before = DateTime.UtcNow.AddSeconds(-2);
        var post = new HttpRequestMessage(HttpMethod.Post, $"/floor-audit/movements/{id}/return")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("time", "00:01")]),
        };
        post.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(post)).StatusCode);
        await using (var db = fx.NewContext())
        {
            var m = await db.StudentMovements.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.NotNull(m.ReturnAt);
            Assert.InRange(m.ReturnAt!.Value, before, DateTime.UtcNow.AddSeconds(2));
            Assert.Equal(inspector.Name, m.ReturnedBy);
        }
    }
}
