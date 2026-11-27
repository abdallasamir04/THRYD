using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Tests;

/// <summary>
/// PTC Phase 5: live operations. A teacher sees and operates only their own interviews; Start / End record the server's
/// time once (repeated or concurrent requests keep the first times); the state lifecycle (Upcoming, In progress, Completed,
/// Missed, Cancelled) comes from server times; the Super Admin live view counts bookings and interviews separately; Admin
/// corrections need a reason and confirmation and keep the previous values. Planned times, bookings, subjects and teachers
/// never change. Test database only; a fixed, movable server clock.
/// </summary>
[Collection("db")]
public class PtcOperationsTests(DbFixture fx)
{
    private static readonly DateOnly Day = new(2027, 3, 9); // a Tuesday; Cairo is UTC+2 in March
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private sealed class Clock(DateTimeOffset utc) : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    /// <summary>A server clock at a Cairo local time on <see cref="Day"/> (or another date).</summary>
    private static Clock At(int h, int m, int s = 0, DateOnly? date = null) =>
        new(new DateTimeOffset((date ?? Day).ToDateTime(new TimeOnly(h, m, s)), TimeSpan.FromHours(2)).ToUniversalTime());

    private sealed class Admin : IPtcAccess { public bool CanManage => true; }
    private sealed class NotAdmin : IPtcAccess { public bool CanManage => false; }
    private sealed class As(int? staffId, bool admin, string name) : ICurrentUser
    {
        public string? Name => name;
        public int? StaffId => staffId;
        public bool IsVerified => false;
        public bool IsSuperAdmin => admin;
    }

    private static SettingsService Settings(SchoolDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));
    private static PtcSetupService Setup(SchoolDbContext db) =>
        new(db, new Admin(), Settings(db), new As(null, true, "Test runner"), new PtcAdminService(db, new Admin(), Settings(db), new ClassTeamService(db, new As(null, true, "Test runner"))));

    private static PtcOperationsService Ops(SchoolDbContext db, ICurrentUser user, Clock clock) =>
        new(db, user.IsSuperAdmin ? new Admin() : new NotAdmin(), user, Protection, Options.Create(new PtcOperationsOptions { StuckAfterMinutes = 30 }),
            new SchoolTimeService(clock, SchoolTimeService.DefaultTimeZoneId));

    private static ICurrentUser Teacher(int id) => new As(id, false, "Teacher " + id);
    private static readonly ICurrentUser SuperAdmin = new As(null, true, "Super Admin");

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

    private sealed record Scenario(int Event, int Class, int T1, int T2, int BookingA, int BookingB, string StudentA, string StudentB, int Round);

    /// <summary>
    /// A Published event: one class, Round 08:00; parent A chose both teachers (08:00–08:07, parallel), parent B teacher 1
    /// only (08:07–08:14).
    /// </summary>
    private async Task<Scenario> ScenarioAsync()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "PTC5 " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        var ev = Ok(await Setup(db).SaveEventAsync(null, new PtcEventInput("PTC " + DbFixture.Unique(), null, year.Id, Day, Day.AddDays(4))));
        var cls = await fx.NewClassAsync();
        var (t1, _) = await fx.NewTeacherAsync();
        var (t2, _) = await fx.NewTeacherAsync();
        var lessons = new List<int>();
        foreach (var (t, dept, subject, day) in new[] { (t1, 3, "Math", DayOfWeek.Sunday), (t2, 2, "English", DayOfWeek.Monday) })
        {
            var slot = new TimeSlot { Number = Random.Shared.Next(100, 999), SchoolClassId = cls, UpdatedAt = DateTime.UtcNow };
            db.TimeSlots.Add(slot);
            await db.SaveChangesAsync();
            var lesson = new TimetableAssignment { SchoolClassId = cls, Day = day, TimeSlotId = slot.Id, DepartmentId = dept, Subject = subject, TeacherId = t, StartsOn = SchoolWeek.StartOf(new DateOnly(2026, 9, 6)) };
            db.TimetableAssignments.Add(lesson);
            await db.SaveChangesAsync();
            lessons.Add(lesson.Id);
        }
        var schedule = Ok(await Setup(db).ScheduleClassAsync(ev, cls, Day, new TimeOnly(8, 0), new TimeOnly(14, 0), null));
        var round = Ok(await Setup(db).AddRoundAsync(schedule, new TimeOnly(8, 0)));
        await db.PtcEvents.Where(e => e.Id == ev).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Published));

        async Task<(int Booking, string Name)> Book(params int[] chosen)
        {
            var (student, name) = await fx.NewStudentAsync(cls);
            var phone = "010" + Random.Shared.Next(10_000_000, 99_999_999);
            await db.Students.Where(s => s.Id == student).ExecuteUpdateAsync(u => u.SetProperty(s => s.ParentContactNumber, phone));
            var r = await new PtcBookingService(db, new Admin(), new As(null, true, "x")).BookAsParentAsync(new PtcBookingRequest(round, student, phone, chosen.Select(PtcSubjectOption.LessonKey).ToList()));
            return (Ok(r), name);
        }
        var a = await Book(lessons[0], lessons[1]);
        var b = await Book(lessons[0]);
        return new Scenario(ev, cls, t1, t2, a.Booking, b.Booking, a.Name, b.Name, round);
    }

    private async Task<(PtcMeeting A, PtcMeeting B)> MeetingsAsync(Scenario s, int teacher, Clock clock)
    {
        await using var db = fx.NewContext();
        var d = (await Ops(db, Teacher(teacher), clock).TeacherDashboardAsync(Day))!;
        return (d.Meetings.Single(m => m.StudentName == s.StudentA), d.Meetings.SingleOrDefault(m => m.StudentName == s.StudentB)!);
    }

    private async Task<object> SnapshotAsync(Scenario s)
    {
        await using var db = fx.NewContext();
        var rows = await db.PtcTeacherInterviews.AsNoTracking().Where(i => i.Booking.PtcRoundId == s.Round).OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.PtcParentBookingId, i.PtcBookingSubjectId, i.TeacherStaffMemberId, i.Date, i.ScheduledStart, i.ScheduledEnd }).ToListAsync();
        var bookings = await db.PtcParentBookings.AsNoTracking().Where(b => b.PtcRoundId == s.Round).OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.BookingReference, b.SlotNumber, b.PtcRoundId, b.Status, Subjects = b.Subjects.Count }).ToListAsync();
        return string.Join(";", rows.Select(r => r.ToString())) + "|" + string.Join(";", bookings.Select(b => b.ToString()));
    }

    // ------------------------------------------------------------------ identity and ownership

    [Fact]
    public async Task A_teacher_sees_and_operates_only_their_own_interviews()
    {
        var s = await ScenarioAsync();
        var clock = At(8, 0, 30);
        await using var db = fx.NewContext();

        var mine = (await Ops(db, Teacher(s.T1), clock).TeacherDashboardAsync(null))!;
        Assert.Equal((Day, 2), (mine.Date!.Value, mine.Meetings.Count));
        var a1 = mine.Meetings.Single(m => m.StudentName == s.StudentA);
        Assert.Single(a1.Joint);                                                        // the other teacher meets the same parent at the same time
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(8, 7)), (a1.PlannedStart, a1.PlannedEnd));
        var theirs = (await Ops(db, Teacher(s.T2), clock).TeacherDashboardAsync(null))!;
        Assert.Equal(new[] { s.StudentA }, theirs.Meetings.Select(m => m.StudentName));   // teacher 2 never sees parent B
        Assert.DoesNotContain(a1.Key, theirs.Meetings.Select(m => m.Key));

        // Teacher 2 cannot start or end teacher 1's interview; a forged key opens nothing.
        Assert.Equal(PtcOperationsService.NotYoursMessage, (await Ops(db, Teacher(s.T2), clock).StartAsync(a1.Key)).Error);
        Assert.Equal(PtcOperationsService.NotYoursMessage, (await Ops(db, Teacher(s.T2), clock).EndAsync(a1.Key)).Error);
        Assert.False((await Ops(db, Teacher(s.T1), clock).StartAsync(a1.Key[..^4] + "AAAA")).Succeeded);
        Assert.Null(await Ops(db, new As(null, false, "Nobody"), clock).TeacherDashboardAsync(null));   // no staff identity: nothing
        await Assert.ThrowsAsync<PtcAccessDeniedException>(() => Ops(db, Teacher(s.T1), clock).LiveAsync(s.Event, null, null, null, null));

        // The Super Admin may operate any teacher's interview.
        var byAdmin = await Ops(db, SuperAdmin, clock).StartAsync(a1.Key);
        Assert.True(byAdmin.Succeeded);
        Assert.Equal("Super Admin", (await db.PtcInterviewEvents.AsNoTracking().SingleAsync(e => e.PtcParentBookingId == s.BookingA && e.TeacherStaffMemberId == s.T1)).By);
    }

    // ------------------------------------------------------------------ start / end

    [Fact]
    public async Task Start_and_end_record_the_server_time_once_and_never_touch_the_plan()
    {
        var s = await ScenarioAsync();
        var before = await SnapshotAsync(s);
        var clock = At(8, 1, 10);
        var (a1, b1) = await MeetingsAsync(s, s.T1, clock);
        await using var db = fx.NewContext();
        var t1 = Ops(db, Teacher(s.T1), clock);

        Assert.Contains("not been started", (await t1.EndAsync(b1.Key)).Error);                // end without start
        var started = await t1.StartAsync(a1.Key);
        Assert.Equal((true, false, clock.Utc.UtcDateTime), (started.Succeeded, started.AlreadyDone, started.ActualStartUtc!.Value));
        clock.Utc = clock.Utc.AddMinutes(1);
        var again = await t1.StartAsync(a1.Key);                                               // duplicate: the original time stands
        Assert.Equal((true, true, started.ActualStartUtc), (again.Succeeded, again.AlreadyDone, again.ActualStartUtc));

        clock.Utc = clock.Utc.AddSeconds(375);                                                  // ends 7:15 after it started
        var ended = await t1.EndAsync(a1.Key);
        Assert.Equal(TimeSpan.FromSeconds(435), ended.ActualEndUtc - ended.ActualStartUtc);
        clock.Utc = clock.Utc.AddMinutes(2);
        var endAgain = await t1.EndAsync(a1.Key);
        Assert.Equal((true, true, ended.ActualEndUtc), (endAgain.Succeeded, endAgain.AlreadyDone, endAgain.ActualEndUtc));
        Assert.Contains("already ended", (await t1.StartAsync(a1.Key)).Error);                 // a completed interview cannot start again

        var row = await db.PtcTeacherInterviews.AsNoTracking().SingleAsync(i => i.PtcParentBookingId == s.BookingA && i.TeacherStaffMemberId == s.T1);
        Assert.Equal((PtcInterviewStatus.Completed, started.ActualStartUtc, ended.ActualEndUtc), (row.Status, row.ActualStart, row.ActualEnd));
        Assert.Equal(before, await SnapshotAsync(s));                                          // plan, bookings, subjects, teachers: unchanged; no new rows
        var log = await db.PtcInterviewEvents.AsNoTracking().Where(e => e.PtcParentBookingId == s.BookingA && e.TeacherStaffMemberId == s.T1).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(new[] { PtcInterviewEventKind.Started, PtcInterviewEventKind.Ended }, log.Select(e => e.Kind));
        Assert.All(log, e => Assert.Equal(("Teacher " + s.T1, s.T1), (e.By, e.ByStaffId!.Value)));
    }

    [Fact]
    public async Task Concurrent_starts_and_ends_keep_one_time_and_one_record()
    {
        var s = await ScenarioAsync();
        var clock = At(8, 2);
        var (a2, _) = await MeetingsAsync(s, s.T2, clock);
        async Task<PtcTimerResult> Run(Func<PtcOperationsService, Task<PtcTimerResult>> f)
        {
            await using var db = fx.NewContext();
            return await f(Ops(db, Teacher(s.T2), clock));
        }
        var starts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Run(o => o.StartAsync(a2.Key)))));
        Assert.All(starts, r => Assert.True(r.Succeeded));
        Assert.Single(starts.Select(r => r.ActualStartUtc).Distinct());
        Assert.Equal(1, starts.Count(r => !r.AlreadyDone));
        clock.Utc = clock.Utc.AddMinutes(6);
        var ends = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Run(o => o.EndAsync(a2.Key)))));
        Assert.All(ends, r => Assert.True(r.Succeeded));
        Assert.Single(ends.Select(r => r.ActualEndUtc).Distinct());
        await using var db = fx.NewContext();
        Assert.Equal(2, await db.PtcInterviewEvents.CountAsync(e => e.PtcParentBookingId == s.BookingA && e.TeacherStaffMemberId == s.T2));
    }

    [Fact]
    public async Task The_status_lifecycle_comes_from_server_times()
    {
        var s = await ScenarioAsync();
        // 07:50: both Upcoming; startable only on the day (not the day before).
        var (a, b) = await MeetingsAsync(s, s.T1, At(7, 50));
        Assert.Equal((PtcLiveState.Upcoming, PtcLiveState.Upcoming, true), (a.State, b.State, a.CanStart));
        await using (var db = fx.NewContext())
            Assert.Contains("only be started on that day", (await Ops(db, Teacher(s.T1), At(8, 0, 0, Day.AddDays(-1))).StartAsync(a.Key)).Error);

        // 08:20: B (08:07–08:14) was never started: Missed, though still startable late on the day.
        var late = At(8, 20);
        await using (var db = fx.NewContext()) Assert.True((await Ops(db, Teacher(s.T1), late).StartAsync(a.Key)).Succeeded);
        (a, b) = await MeetingsAsync(s, s.T1, late);
        Assert.Equal((PtcLiveState.InProgress, PtcLiveState.Missed, true), (a.State, b.State, b.CanStart));
        Assert.False(a.Overrunning);                                                             // just started: not running over

        // Ended: Completed. A cancelled booking: Cancelled and not startable.
        await using (var db = fx.NewContext()) Assert.True((await Ops(db, Teacher(s.T1), At(8, 26)).EndAsync(a.Key)).Succeeded);
        await using (var db = fx.NewContext())
            await db.PtcParentBookings.Where(x => x.Id == s.BookingB).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PtcBookingStatus.Cancelled).SetProperty(x => x.CancelledAt, DateTime.UtcNow));
        (a, b) = await MeetingsAsync(s, s.T1, At(8, 30));
        Assert.Equal((PtcLiveState.Completed, PtcLiveState.Cancelled, false), (a.State, b.State, b.CanStart));
        await using (var db = fx.NewContext()) Assert.Contains("cancelled", (await Ops(db, Teacher(s.T1), At(8, 30)).StartAsync(b.Key)).Error);
    }

    [Fact]
    public async Task The_timer_recovers_from_the_recorded_start_whatever_the_page_or_browser_does()
    {
        var s = await ScenarioAsync();
        var clock = At(8, 3);
        var (a, _) = await MeetingsAsync(s, s.T1, clock);
        await using var db = fx.NewContext();
        var start = (await Ops(db, Teacher(s.T1), clock).StartAsync(a.Key)).ActualStartUtc;
        // Reloading later (refresh, coming back): the same recorded start, and the server's current time to count from.
        clock.Utc = clock.Utc.AddMinutes(4);
        var first = (await Ops(db, Teacher(s.T1), clock).TeacherDashboardAsync(Day))!;
        clock.Utc = clock.Utc.AddMinutes(4);
        var second = (await Ops(db, Teacher(s.T1), clock).TeacherDashboardAsync(Day))!;
        Assert.Equal(start, first.Current!.ActualStartUtc);
        Assert.Equal(start, second.Current!.ActualStartUtc);
        Assert.Equal(TimeSpan.FromMinutes(4), second.ServerNowUtc - first.ServerNowUtc);
        Assert.False(first.Current.Overrunning);                                                 // 4 of 7 planned minutes
        Assert.True(second.Current.Overrunning);                                                 // 8 of 7: running over
        Assert.Equal(TimeSpan.FromMinutes(8), second.ServerNowUtc - start!.Value);
    }

    // ------------------------------------------------------------------ Super Admin live view and corrections

    [Fact]
    public async Task The_live_view_counts_bookings_and_interviews_separately_and_flags_problems()
    {
        var s = await ScenarioAsync();
        var (a1, b1) = await MeetingsAsync(s, s.T1, At(8, 0));
        var (a2, _) = await MeetingsAsync(s, s.T2, At(8, 0));
        await using var db = fx.NewContext();
        Assert.True((await Ops(db, Teacher(s.T1), At(8, 0, 20)).StartAsync(a1.Key)).Succeeded);
        Assert.True((await Ops(db, Teacher(s.T2), At(8, 0, 40)).StartAsync(a2.Key)).Succeeded);
        Assert.True((await Ops(db, Teacher(s.T2), At(8, 6)).EndAsync(a2.Key)).Succeeded);

        // 08:45: teacher 1 never ended A (stuck after 30 min) and never started B (missed).
        var live = (await Ops(db, SuperAdmin, At(8, 45)).LiveAsync(s.Event, null, null, null, null))!;
        Assert.Equal((Day, 2, 3), (live.Date!.Value, live.Bookings, live.Counts.Total));     // 2 parents, 3 teacher interviews
        Assert.Equal((1, 1, 1, 0), (live.Counts.InProgress, live.Counts.Completed, live.Counts.Missed, live.Counts.Upcoming));
        Assert.Equal(s.StudentA, Assert.Single(live.Stuck).StudentName);
        Assert.Equal(s.StudentB, Assert.Single(live.NotStarted).StudentName);
        var round = Assert.Single(live.Rounds);
        Assert.Equal((2, 3), (round.Bookings, round.Interviews.Total));
        Assert.Equal(2, live.Teachers.Single(t => t.TeacherId == s.T1).Interviews.Total);
        Assert.NotNull(live.AverageActual);

        // Filters.
        Assert.Equal(2, (await Ops(db, SuperAdmin, At(8, 45)).LiveAsync(s.Event, Day, null, s.T1, null))!.Meetings.Count);
        Assert.Equal(s.StudentB, Assert.Single((await Ops(db, SuperAdmin, At(8, 45)).LiveAsync(s.Event, Day, s.Class, null, PtcLiveState.Missed))!.Meetings).StudentName);
    }

    [Fact]
    public async Task Admin_corrections_need_a_reason_and_confirmation_and_keep_the_previous_values()
    {
        var s = await ScenarioAsync();
        var before = await SnapshotAsync(s);
        var (a1, _) = await MeetingsAsync(s, s.T1, At(8, 0));
        await using var db = fx.NewContext();
        Assert.True((await Ops(db, Teacher(s.T1), At(8, 0, 30)).StartAsync(a1.Key)).Succeeded);
        var admin = Ops(db, SuperAdmin, At(9, 0));
        var stuck = (await admin.LiveAsync(s.Event, Day, null, s.T1, PtcLiveState.InProgress))!.Meetings.Single();
        var version = admin.Version(stuck);

        Fails(await admin.CorrectAsync(stuck.Key, PtcCorrectionTarget.Completed, new TimeOnly(8, 0, 30), new TimeOnly(8, 8), "", true, version), "reason");
        Fails(await admin.CorrectAsync(stuck.Key, PtcCorrectionTarget.Completed, new TimeOnly(8, 0, 30), new TimeOnly(8, 8), "Forgot to end", false, version), "Confirm");
        Fails(await admin.CorrectAsync(stuck.Key, PtcCorrectionTarget.Completed, new TimeOnly(8, 9), new TimeOnly(8, 8), "Forgot to end", true, version), "before the start");
        Fails(await admin.CorrectAsync(stuck.Key, PtcCorrectionTarget.Completed, new TimeOnly(8, 0, 30), new TimeOnly(9, 30), "Forgot to end", true, version), "future");
        Fails(await admin.CorrectAsync(stuck.Key, PtcCorrectionTarget.Completed, new TimeOnly(8, 0, 30), new TimeOnly(8, 8), "Forgot to end", true, "stale"), "changed since");
        await Assert.ThrowsAsync<PtcAccessDeniedException>(() => Ops(db, Teacher(s.T1), At(9, 0)).CorrectAsync(stuck.Key, PtcCorrectionTarget.Completed, null, null, "x", true, version));

        Ok(await admin.CorrectAsync(stuck.Key, PtcCorrectionTarget.Completed, new TimeOnly(8, 0, 30), new TimeOnly(8, 8), "Teacher forgot to press End", true, version));
        var row = await db.PtcTeacherInterviews.AsNoTracking().SingleAsync(i => i.PtcParentBookingId == s.BookingA && i.TeacherStaffMemberId == s.T1);
        Assert.Equal((PtcInterviewStatus.Completed, new DateTime(2027, 3, 9, 6, 8, 0, DateTimeKind.Utc)), (row.Status, row.ActualEnd!.Value));
        var correction = await db.PtcInterviewEvents.AsNoTracking().SingleAsync(e => e.PtcParentBookingId == s.BookingA && e.TeacherStaffMemberId == s.T1 && e.Kind == PtcInterviewEventKind.Corrected);
        Assert.Equal((PtcInterviewStatus.InProgress, (DateTime?)null, PtcInterviewStatus.Completed, "Teacher forgot to press End", "Super Admin"),
            (correction.OldStatus!.Value, correction.OldActualEnd, correction.NewStatus, correction.Reason, correction.By));
        Assert.Equal(row.ActualStart, correction.OldActualStart);
        Assert.Equal(before, await SnapshotAsync(s));                                             // planned times and bookings untouched
    }

    // ------------------------------------------------------------------ pages (HTTP)

    [Fact]
    public async Task The_teacher_dashboard_shows_only_their_interviews_and_the_live_view_is_super_admin_only()
    {
        var s = await ScenarioAsync();
        var asT1 = fx.ClientAs(fx.Factory, s.T1, follow: false);
        var asT2 = fx.ClientAs(fx.Factory, s.T2, follow: false);
        var t1 = await (await asT1.GetAsync($"/ptc/my?date={Day:yyyy-MM-dd}")).Content.ReadAsStringAsync();
        var t2 = await (await asT2.GetAsync($"/ptc/my?date={Day:yyyy-MM-dd}")).Content.ReadAsStringAsync();
        Assert.Contains(s.StudentA, t1);
        Assert.Contains(s.StudentB, t1);
        Assert.Contains(s.StudentA, t2);
        Assert.DoesNotContain(s.StudentB, t2);
        Assert.Contains("href=\"/ptc/my\"", t1);                                                 // "My PTC interviews" in the navigation
        Assert.DoesNotContain("href=\"/ptc\"", t1);

        Assert.Equal(HttpStatusCode.Forbidden, (await asT1.GetAsync($"/ptc/events/{s.Event}/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asT1.GetAsync($"/ptc/events/{s.Event}/live?partial=true")).StatusCode);
        var admin = fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/ptc/events/{s.Event}/live?date={Day:yyyy-MM-dd}")).StatusCode);
        var partial = await (await admin.GetAsync($"/ptc/events/{s.Event}/live?date={Day:yyyy-MM-dd}&partial=true")).Content.ReadAsStringAsync();
        Assert.Contains("data-server-now", partial);
        Assert.DoesNotContain("<html", partial);                                                 // the refresh returns only the live part
    }
}
