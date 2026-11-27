using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Tests;

/// <summary>
/// PTC Phase 6: timetable integration and class coverage. Participating teachers' real timetable lessons during the Rounds
/// are conflicts; only a valid substitute the Super Admin chose covers one; substitutes are validated (not the teacher, not
/// busy, current staff, not covering two lessons at once); removing, replacing, timetable and Round changes are reflected
/// at once; publishing is refused on the server while anything is uncovered; a Published event whose coverage breaks keeps
/// its confirmed bookings and is flagged for attention. Test database only (isolated fixtures; nothing invented in the dev DB).
/// </summary>
[Collection("db")]
public class PtcCoverageTests(DbFixture fx)
{
    private static readonly DateOnly Day = new(2027, 3, 9); // a Tuesday
    private static readonly DateOnly LessonsFrom = SchoolWeek.StartOf(new DateOnly(2026, 9, 6));
    private static readonly TimeOnly T8 = new(8, 0), T14 = new(14, 0);
    private static TimeOnly At(int h, int m) => new(h, m);
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private sealed class Admin : IPtcAccess { public bool CanManage => true; }
    private sealed class NoAccess : IPtcAccess { public bool CanManage => false; }
    private sealed class User : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static SettingsService Settings(SchoolDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));
    private static PtcAdminService AdminService(SchoolDbContext db) => new(db, new Admin(), Settings(db), new ClassTeamService(db, new User()));
    private static PtcSetupService Setup(SchoolDbContext db) => new(db, new Admin(), Settings(db), new User(), AdminService(db));
    private static PtcCoverageService Coverage(SchoolDbContext db, IPtcAccess? access = null) => new(db, access ?? new Admin(), new User(), AdminService(db));

    private static async Task<PtcCoverageReport> ReportAsync(SchoolDbContext db, int ev) => (await AdminService(db).EventAsync(ev))!.Coverage;

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

    private async Task<int> ClassAsync()
    {
        await using var db = fx.NewContext();
        for (var i = 0; i < 50; i++)
        {
            var r = await DbFixture.Classes(db).CreateAsync("C" + new string(Enumerable.Range(0, 4).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray()), null, true);
            if (r.Succeeded) return r.EntityId!.Value;
        }
        throw new InvalidOperationException("No free class name.");
    }

    private async Task<int> LessonAsync(int classId, int teacherId, int departmentId, string subject, TimeOnly start, TimeOnly end)
    {
        await using var db = fx.NewContext();
        var slot = new TimeSlot { Number = Random.Shared.Next(100, 999), SchoolClassId = classId, StartTime = start, EndTime = end, UpdatedAt = DateTime.UtcNow };
        db.TimeSlots.Add(slot);
        await db.SaveChangesAsync();
        var lesson = new TimetableAssignment { SchoolClassId = classId, Day = Day.DayOfWeek, TimeSlotId = slot.Id, DepartmentId = departmentId, Subject = subject, TeacherId = teacherId, StartsOn = LessonsFrom };
        db.TimetableAssignments.Add(lesson);
        await db.SaveChangesAsync();
        return lesson.Id;
    }

    /// <summary>
    /// A Draft event with one class (Round 08:00–09:10). The teacher teaches that class Maths 09:00–09:45 (overlaps the Round)
    /// and another class Science 10:00–10:45 (does not).
    /// </summary>
    private async Task<(int Event, int Class, int Schedule, int Round, int Teacher, int Lesson, int Other)> ScenarioAsync()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "PTC6 " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        var ev = Ok(await Setup(db).SaveEventAsync(null, new PtcEventInput("PTC " + DbFixture.Unique(), null, year.Id, Day, Day.AddDays(2))));
        var cls = await ClassAsync();
        var other = await ClassAsync();
        var (teacher, _) = await fx.NewTeacherAsync();
        var lesson = await LessonAsync(cls, teacher, 3, "Maths", At(9, 0), At(9, 45));
        var otherLesson = await LessonAsync(other, teacher, 4, "Science", At(10, 0), At(10, 45));
        var schedule = Ok(await Setup(db).ScheduleClassAsync(ev, cls, Day, T8, T14, null));
        var round = Ok(await Setup(db).AddRoundsAsync(schedule, new PtcRoundPlan(T8, 1, 70, 9, 7, 7)));
        return (ev, cls, schedule, round, teacher, lesson, otherLesson);
    }

    // ------------------------------------------------------------------ timetable integration

    [Fact]
    public async Task Real_timetable_lessons_during_a_Round_are_conflicts_and_others_are_not()
    {
        var s = await ScenarioAsync();
        await using var db = fx.NewContext();
        var r = await ReportAsync(db, s.Event);
        var c = Assert.Single(r.Conflicts);                                                  // the 10:00 lesson does not overlap 08:00–09:10
        Assert.Equal((s.Teacher, s.Lesson, "Maths", PtcCoverageStatus.Uncovered), (c.TeacherId, c.LessonId, c.Subject, c.Status));
        Assert.Equal((At(9, 0), At(9, 45), At(9, 0), At(9, 10)), (c.LessonStart, c.LessonEnd, c.OverlapStart, c.OverlapEnd));
        Assert.Contains("Maths lesson with", c.Explanation);
        Assert.Contains("No valid substitute", c.Explanation);
        Assert.False(r.TimetableEmpty);
        Assert.Equal(PtcCoverageStatus.Uncovered, r.Overall);
        Assert.Equal(PtcCoverageStatus.Uncovered, Assert.Single(r.Teachers).Status);

        // Publishing is refused on the server with the coverage summary (no UI involved).
        var publish = await Setup(db).PublishAsync(s.Event, true);
        Fails(publish, "Publishing blocked: 1 timetable conflict requires substitute coverage.");
        Fails(publish, "no PTC substitute covers it");
        Assert.Equal(PtcEventStatus.Draft, await db.PtcEvents.Where(e => e.Id == s.Event).Select(e => e.Status).SingleAsync());
    }

    [Fact]
    public async Task Back_to_back_lessons_do_not_conflict_and_a_Round_move_is_reflected_at_once()
    {
        var s = await ScenarioAsync();
        await using var db = fx.NewContext();
        // Move the Round to 09:45–10:55: the Maths lesson ends exactly as it starts (no conflict); Science 10:00 now overlaps.
        Ok(await Setup(db).RemoveRoundAsync(s.Round));
        Ok(await Setup(db).AddRoundAsync(s.Schedule, At(9, 45)));
        var c = Assert.Single((await ReportAsync(db, s.Event)).Conflicts);
        Assert.Equal(s.Other, c.LessonId);
        Assert.Equal("Science", c.Subject);
    }

    // ------------------------------------------------------------------ substitutes

    [Fact]
    public async Task Substitutes_are_validated_and_a_valid_one_resolves_the_conflict_and_allows_publishing()
    {
        var s = await ScenarioAsync();
        var (busy, _) = await fx.NewTeacherAsync();
        var (archived, _) = await fx.NewTeacherAsync();
        var (sub, subName) = await fx.NewTeacherAsync();
        await LessonAsync(await ClassAsync(), busy, 3, "Maths", At(9, 30), At(10, 15));          // overlaps the lesson to cover
        await using var db = fx.NewContext();
        await db.StaffMembers.Where(x => x.Id == archived).ExecuteUpdateAsync(u => u.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));

        Fails(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, s.Teacher, null, null), "own substitute");
        Fails(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, busy, null, null), "cannot cover this lesson");
        Fails(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, archived, null, null), "not current staff");
        Fails(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, 0, null, null), "existing staff member");
        Fails(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Other, sub, null, null), "no longer needs a substitute");   // not in conflict
        await Assert.ThrowsAsync<PtcAccessDeniedException>(() => Coverage(db, new NoAccess()).AssignAsync(s.Event, s.Schedule, s.Lesson, sub, null, null));

        // The form offers only staff free for the whole lesson.
        var form = (await Coverage(db).FormAsync(s.Event, s.Schedule, s.Lesson))!;
        Assert.Contains(form.Eligible, x => x.Id == sub);
        Assert.DoesNotContain(form.Eligible, x => x.Id == busy || x.Id == s.Teacher || x.Id == archived);
        Assert.Contains(form.Unavailable, x => x.Id == busy);

        var id = Ok(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, sub, "PTC cover", "Room 12"));
        var a = await db.PtcSubstituteAssignments.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal((s.Lesson, At(9, 0), At(9, 45), "Room 12"), (a.TimetableAssignmentId, a.StartTime, a.EndTime, a.Notes));
        Assert.Equal(1, await db.TimetableAssignments.CountAsync(l => l.Id == s.Lesson && l.TeacherId == s.Teacher));   // the lesson itself is unchanged
        Fails(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, sub, null, null), "already has a substitute");  // no duplicates

        var r = await ReportAsync(db, s.Event);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(PtcCoverageStatus.Covered, c.Status);
        Assert.Equal(subName, c.Cover!.SubstituteName);
        Assert.Equal(PtcCoverageStatus.Covered, r.Overall);
        // The covered teacher is available for PTC during the lesson; the substitute is not.
        Assert.DoesNotContain(await PtcSchedulingRules.TeacherConflictsAsync(db, s.Teacher, Day, At(9, 0), At(9, 7), default), x => x.Blocks);
        Assert.Contains(await PtcSchedulingRules.TeacherConflictsAsync(db, sub, Day, At(9, 0), At(9, 7), default), x => x.Kind == PtcConflictKind.SubstituteCover);

        Ok(await Setup(db).PublishAsync(s.Event, true));
        Assert.Equal(PtcSubstituteChangeKind.Assigned, (await db.PtcSubstituteChanges.SingleAsync(h => h.PtcSubstituteAssignmentId == id)).Kind);
    }

    [Fact]
    public async Task A_substitute_cannot_cover_two_overlapping_lessons()
    {
        var s = await ScenarioAsync();
        var (second, _) = await fx.NewTeacherAsync();
        var secondLesson = await LessonAsync(s.Class, second, 4, "English", At(8, 30), At(9, 15));  // the class's English teacher, also during the Round
        var (sub, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        Assert.Equal(2, (await ReportAsync(db, s.Event)).Conflicts.Count);
        Ok(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, sub, null, null));
        Fails(await Coverage(db).AssignAsync(s.Event, s.Schedule, secondLesson, sub, null, null), "PTC substitute for");
        Assert.Equal(1, (await ReportAsync(db, s.Event)).Uncovered);
    }

    [Fact]
    public async Task Removing_and_replacing_are_audited_and_removal_brings_the_block_back()
    {
        var s = await ScenarioAsync();
        var (sub, subName) = await fx.NewTeacherAsync();
        var (other, otherName) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var first = Ok(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, sub, null, null));

        Fails(await Coverage(db).ReplaceAsync(first, other, "", null), "reason");
        Fails(await Coverage(db).ReplaceAsync(first, sub, "Same", null), "already the substitute");
        var second = Ok(await Coverage(db).ReplaceAsync(first, other, "Sub is ill", "Call first"));
        Fails(await Coverage(db).ReplaceAsync(first, sub, "Stale form", null), "changed or removed");                 // concurrent edit of an old page
        Assert.Equal(PtcSubstituteStatus.Cancelled, await db.PtcSubstituteAssignments.Where(a => a.Id == first).Select(a => a.Status).SingleAsync());
        Assert.Equal(otherName, Assert.Single((await ReportAsync(db, s.Event)).Conflicts).Cover!.SubstituteName);

        Ok(await Coverage(db).UpdateNotesAsync(second, "Call the office"));
        Fails(await Coverage(db).RemoveAsync(second, " "), "reason");
        Ok(await Coverage(db).RemoveAsync(second, "Not needed"));
        var r = await ReportAsync(db, s.Event);
        Assert.Equal(PtcCoverageStatus.Uncovered, Assert.Single(r.Conflicts).Status);
        Fails(await Setup(db).PublishAsync(s.Event, true), "Publishing blocked");

        var history = await Coverage(db).HistoryAsync(s.Event);
        Assert.Equal(new[] { PtcSubstituteChangeKind.Removed, PtcSubstituteChangeKind.NotesChanged, PtcSubstituteChangeKind.Replaced, PtcSubstituteChangeKind.Assigned },
            history.Select(h => h.Kind));
        var replaced = history.Single(h => h.Kind == PtcSubstituteChangeKind.Replaced);
        Assert.Equal((subName, otherName, "Sub is ill"), (replaced.OldSubstitute, replaced.NewSubstitute, replaced.Reason));
        Assert.All(history, h => Assert.Contains("Maths", h.Lesson));
    }

    // ------------------------------------------------------------------ revalidation

    [Fact]
    public async Task Timetable_changes_after_coverage_are_revalidated()
    {
        var s = await ScenarioAsync();
        var (sub, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var id = Ok(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, sub, null, null));

        // The substitute gets a new lesson at the same time: the cover no longer counts and publishing is blocked again.
        var clash = await LessonAsync(await ClassAsync(), sub, 3, "Maths", At(9, 15), At(10, 0));
        var r = await ReportAsync(db, s.Event);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(PtcCoverageStatus.Invalid, c.Status);
        Assert.Contains("teaches", c.InvalidReason);
        Assert.Equal(1, r.Invalid);
        Fails(await Setup(db).PublishAsync(s.Event, true), "Publishing blocked");
        Assert.Contains(await PtcSchedulingRules.TeacherConflictsAsync(db, s.Teacher, Day, At(9, 0), At(9, 7), default), x => x.Blocks);   // the teacher is busy again

        // The original teacher's lesson ends (timetable edit): nothing to cover; the old assignment needs review, nothing blocks.
        await db.TimetableAssignments.Where(l => l.Id == clash).ExecuteDeleteAsync();
        await db.TimetableAssignments.Where(l => l.Id == s.Lesson).ExecuteUpdateAsync(u => u.SetProperty(l => l.EndedOn, LessonsFrom.AddDays(7)));
        r = await ReportAsync(db, s.Event);
        Assert.Empty(r.Conflicts);
        Assert.Equal(PtcCoverageStatus.NeedsReview, Assert.Single(r.Assignments, a => a.Id == id).Status);
        Assert.Equal(PtcCoverageStatus.NeedsReview, r.Overall);
        Assert.Contains((await AdminService(db).EventAsync(s.Event))!.Readiness.Warnings, w => w.Area == PtcIssueArea.Coverage);

        // A substitute who leaves (archived) no longer counts either.
        await db.TimetableAssignments.Where(l => l.Id == s.Lesson).ExecuteUpdateAsync(u => u.SetProperty(l => l.EndedOn, (DateOnly?)null));
        await db.StaffMembers.Where(x => x.Id == sub).ExecuteUpdateAsync(u => u.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        Assert.Equal(PtcCoverageStatus.Invalid, Assert.Single((await ReportAsync(db, s.Event)).Conflicts).Status);
    }

    [Fact]
    public async Task A_Published_event_whose_coverage_breaks_keeps_its_bookings_and_is_flagged()
    {
        var s = await ScenarioAsync();
        var (sub, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var assignment = Ok(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, sub, null, null));
        Ok(await Setup(db).PublishAsync(s.Event, true));
        var (student, _) = await fx.NewStudentAsync(s.Class);
        var phone = "010" + Random.Shared.Next(10_000_000, 99_999_999);
        await db.Students.Where(x => x.Id == student).ExecuteUpdateAsync(u => u.SetProperty(x => x.ParentContactNumber, phone));
        var booking = Ok(await new PtcBookingService(db, new Admin(), new User()).BookAsParentAsync(
            new PtcBookingRequest(s.Round, student, phone, [PtcSubjectOption.LessonKey(s.Lesson)])));
        var before = await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.PtcParentBookingId == booking)
            .Select(i => new { i.Id, i.Status, i.ScheduledStart, i.ScheduledEnd, i.TeacherStaffMemberId }).ToListAsync();

        Ok(await Coverage(db).RemoveAsync(assignment, "Substitute unavailable"));
        var e = (await AdminService(db).EventAsync(s.Event))!;
        Assert.Equal(PtcEventStatus.Published, e.Status);
        Assert.Equal(1, e.Coverage.Unresolved);
        Assert.Equal(1, (await AdminService(db).DashboardAsync()).Events.Single(x => x.Id == s.Event).CoverageAttention);
        Assert.Equal(PtcBookingStatus.Confirmed, await db.PtcParentBookings.Where(b => b.Id == booking).Select(b => b.Status).SingleAsync());
        Assert.Equal(before, await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.PtcParentBookingId == booking)
            .Select(i => new { i.Id, i.Status, i.ScheduledStart, i.ScheduledEnd, i.TeacherStaffMemberId }).ToListAsync());

        // The Super Admin can cover it again on the Published event.
        Ok(await Coverage(db).AssignAsync(s.Event, s.Schedule, s.Lesson, sub, "Back", null));
        Assert.Equal(0, (await ReportAsync(db, s.Event)).Unresolved);
    }

    // ------------------------------------------------------------------ every class (KG included) uses the timetable

    [Fact]
    public async Task Dashboard_readiness_counts_every_active_class_KG_included()
    {
        await using var db = fx.NewContext();
        int kgWithout;
        for (var i = 0; ; i++)
        {
            var r = await DbFixture.Classes(db).CreateAsync("KG1" + new string(Enumerable.Range(0, 3).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray()), null, true);
            if (r.Succeeded) { kgWithout = r.EntityId!.Value; break; }
            if (i > 50) throw new InvalidOperationException("No free class name.");
        }
        var kgName = await db.Classes.Where(c => c.Id == kgWithout).Select(c => c.Name).SingleAsync();
        Assert.True(ClassStages.IsKgStage(kgName));

        var dashboard = await AdminService(db).DashboardAsync();
        var today = SchoolTime.Current.Today;
        var active = await db.Classes.AsNoTracking().Where(c => c.IsActive).Select(c => c.Id).ToListAsync();
        var subjects = await PtcSchedulingRules.ClassSubjectsAsync(db, active.Select(id => (id, today, (int?)null)).ToList(), default);
        var ready = subjects.Count(kv => kv.Value.Count > 0);
        var missing = active.Count - ready;

        var cards = dashboard.Indicators.ToDictionary(x => x.Title);
        Assert.Equal($"{ready} of {Ui.Plural(active.Count, "class", "classes")} ready", cards["Timetable subjects & teachers"].Value);   // all active classes, KG included
        Assert.Equal($"{Ui.Plural(missing, "class", "classes")} missing", cards["Missing timetable assignments"].Value);
        Assert.DoesNotContain(dashboard.Indicators, x => x.Detail.Contains("select", StringComparison.OrdinalIgnoreCase) || x.Detail.Contains("KG excluded"));   // no manual-selection labels
        Assert.Empty(subjects[kgWithout]);                                                       // a KG class without lessons is missing, like any class
    }

    // ------------------------------------------------------------------ empty timetable

    [Fact]
    public void An_empty_timetable_is_never_reported_as_covered()
    {
        var r = new PtcCoverageReport { EventId = 1, TimetableLessons = 0, HasSchedules = true, Teachers = [], Conflicts = [], Others = [], Untimed = [], Assignments = [] };
        Assert.True(r.TimetableEmpty);
        Assert.Equal(PtcCoverageStatus.NeedsReview, r.Overall);
        var issue = Assert.Single(PtcCoverageEngine.Issues(r));
        Assert.False(issue.Blocking);                                                             // acknowledged explicitly at publishing
        Assert.Contains("cannot be verified", issue.Message);
        Assert.Null(r.BlockSummary);
    }

    // ------------------------------------------------------------------ pages and direct requests

    [Fact]
    public async Task The_pages_show_the_coverage_and_a_direct_publish_request_is_refused()
    {
        var s = await ScenarioAsync();
        var client = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });   // Super Admin (sign-in off)
        var page = await client.GetAsync($"/ptc/events/{s.Event}/coverage");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Maths", html);
        Assert.Contains("Assign substitute", html);
        Assert.Contains("Publishing blocked: 1 timetable conflict requires substitute coverage.", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/ptc/events/{s.Event}/coverage/lesson?schedule={s.Schedule}&lesson={s.Lesson}")).StatusCode);
        Assert.Contains("Class coverage", await (await client.GetAsync($"/ptc/events/{s.Event}")).Content.ReadAsStringAsync());

        var preview = await (await client.GetAsync($"/ptc/events/{s.Event}/preview")).Content.ReadAsStringAsync();
        var post = await client.PostAsync($"/ptc/events/{s.Event}/publish", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token.Match(preview).Groups[1].Value, ["acknowledgeWarnings"] = "true",
        }));
        Assert.Contains("Publishing blocked", await post.Content.ReadAsStringAsync());
        await using var db = fx.NewContext();
        Assert.Equal(PtcEventStatus.Draft, await db.PtcEvents.Where(e => e.Id == s.Event).Select(e => e.Status).SingleAsync());
    }
}
