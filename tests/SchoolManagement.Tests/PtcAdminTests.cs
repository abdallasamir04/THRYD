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
/// PTC Phase 2 (admin): Super Admin authorization on every page, events, classes &amp; dates, Rounds, Draft / Published
/// rules, empty-timetable and KG readiness, parent contact reporting, publishing validation and the School Calendar.
/// Test database only.
/// </summary>
[Collection("db")]
public class PtcAdminTests(DbFixture fx)
{
    private static readonly DateOnly Day = new(2027, 3, 9); // a Tuesday
    private static readonly DateOnly LessonsFrom = SchoolWeek.StartOf(new DateOnly(2026, 9, 6));
    private static readonly TimeOnly T8 = new(8, 0), T14 = new(14, 0);

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

    // ------------------------------------------------------------------ helpers

    private async Task<int> YearAsync()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "PTC2 " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        return year.Id;
    }

    private async Task<(int Event, int Year)> EventAsync(DateOnly? from = null, DateOnly? to = null)
    {
        var year = await YearAsync();
        await using var db = fx.NewContext();
        return (Ok(await Setup(db).SaveEventAsync(null, new PtcEventInput("PTC " + DbFixture.Unique(), "notes", year, from ?? Day, to ?? Day.AddDays(4)))), year);
    }

    private async Task<int> ClassAsync(string prefix = "P")
    {
        await using var db = fx.NewContext();
        for (var i = 0; i < 50; i++)
        {
            // KG / grade names take at most three section letters (KG1ABC); other test classes use four.
            var r = await DbFixture.Classes(db).CreateAsync(prefix + new string(Enumerable.Range(0, prefix.StartsWith("KG") ? 3 : 4).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray()), null, true);
            if (r.Succeeded) return r.EntityId!.Value;
        }
        throw new InvalidOperationException("No free class name.");
    }

    private async Task LessonAsync(int classId, int teacherId)
    {
        await using var db = fx.NewContext();
        var slot = new TimeSlot { Number = Random.Shared.Next(100, 999), SchoolClassId = classId, UpdatedAt = DateTime.UtcNow };
        db.TimeSlots.Add(slot);
        await db.SaveChangesAsync();
        db.TimetableAssignments.Add(new TimetableAssignment
        {
            SchoolClassId = classId, Day = DayOfWeek.Sunday, TimeSlotId = slot.Id, DepartmentId = 3, Subject = "Math", TeacherId = teacherId, StartsOn = LessonsFrom,
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> StudentAsync(int classId, string? phone)
    {
        var (id, _) = await fx.NewStudentAsync(classId);
        await using var db = fx.NewContext();
        await db.Students.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.ParentContactNumber, phone));
        return id;
    }

    private static string Phone() => "010" + Random.Shared.Next(10_000_000, 99_999_999);

    /// <summary>An event with one fully ready class (timetable lesson, Round, students with numbers).</summary>
    private async Task<(int Event, int Year, int Class, int Schedule, int Student, string Phone)> ReadyEventAsync()
    {
        var (ev, year) = await EventAsync();
        var cls = await ClassAsync();
        var (teacher, _) = await fx.NewTeacherAsync();
        await LessonAsync(cls, teacher);
        var phone = Phone();
        var student = await StudentAsync(cls, phone);
        await using var db = fx.NewContext();
        var schedule = Ok(await Setup(db).ScheduleClassAsync(ev, cls, Day, T8, T14, null));
        Ok(await Setup(db).AddRoundAsync(schedule, T8));
        return (ev, year, cls, schedule, student, phone);
    }

    // ------------------------------------------------------------------ authorization (HTTP)

    [Fact]
    public async Task Every_ptc_page_is_super_admin_only_and_renders_for_the_super_admin()
    {
        var (ev, _, _, schedule, _, _) = await ReadyEventAsync();
        var (staff, _) = await fx.NewTeacherAsync();
        var asStaff = fx.ClientAs(fx.Factory, staff, follow: false);
        var asAdmin = fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string[] pages = ["/ptc", "/ptc/parent-contacts", "/ptc/settings", "/ptc/events/new", $"/ptc/events/{ev}", $"/ptc/events/{ev}/edit",
            $"/ptc/events/{ev}/classes", $"/ptc/events/{ev}/preview", $"/ptc/schedules/{schedule}"];

        foreach (var page in pages)
        {
            var denied = await asStaff.GetAsync(page);
            Assert.True(denied.StatusCode == HttpStatusCode.Forbidden, $"{page} as staff: {denied.StatusCode}");
            Assert.DoesNotContain("Parent contact numbers", await denied.Content.ReadAsStringAsync());
            var ok = await asAdmin.GetAsync(page);
            Assert.True(ok.StatusCode == HttpStatusCode.OK, $"{page} as Super Admin: {ok.StatusCode}");
        }
        // A staff POST never runs (no event is created or published).
        var before = await CountEventsAsync();
        var post = await asStaff.PostAsync($"/ptc/events/{ev}/publish", new FormUrlEncodedContent([]));
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.True((int)post.StatusCode >= 400);
        Assert.Equal(before, await CountEventsAsync());

        // Navigation: the PTC entry is a link for the Super Admin only.
        Assert.Contains("href=\"/ptc\"", await (await asAdmin.GetAsync("/")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("href=\"/ptc\"", await (await asStaff.GetAsync("/")).Content.ReadAsStringAsync());
    }

    private async Task<int> CountEventsAsync()
    {
        await using var db = fx.NewContext();
        return await db.PtcEvents.CountAsync();
    }

    // ------------------------------------------------------------------ events

    [Fact]
    public async Task Events_validate_dates_titles_and_protect_published_dates()
    {
        var year = await YearAsync();
        await using var db = fx.NewContext();
        var setup = Setup(db);
        Fails(await setup.SaveEventAsync(null, new PtcEventInput("PTC", null, year, Day, Day.AddDays(-1))), "cannot be before");
        Fails(await setup.SaveEventAsync(null, new PtcEventInput("PTC", null, year, Day, Day.AddDays(40))), "at most 31 days");
        Fails(await setup.SaveEventAsync(null, new PtcEventInput("", null, year, Day, Day)), "Enter the PTC title");
        Fails(await setup.SaveEventAsync(null, new PtcEventInput("PTC", null, 0, Day, Day)), "existing academic year");

        var title = "Q2 PTC " + DbFixture.Unique();
        var ev = Ok(await setup.SaveEventAsync(null, new PtcEventInput(title, null, year, Day, Day.AddDays(4))));
        Assert.Equal(PtcEventStatus.Draft, (await db.PtcEvents.AsNoTracking().SingleAsync(e => e.Id == ev)).Status);
        Fails(await setup.SaveEventAsync(null, new PtcEventInput(title, null, year, Day, Day)), "already has this title");

        // A Draft's dates can change, but never leave a class outside them.
        var cls = await ClassAsync();
        Ok(await setup.ScheduleClassAsync(ev, cls, Day.AddDays(3), T8, T14, null));
        Fails(await setup.SaveEventAsync(ev, new PtcEventInput(title, null, year, Day, Day.AddDays(1))), "outside the new dates");
        Ok(await setup.SaveEventAsync(ev, new PtcEventInput(title + " edited", "New notes", year, Day.AddDays(-1), Day.AddDays(4))));

        // Published: title and description may change, the year and dates may not.
        await db.PtcEvents.Where(e => e.Id == ev).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Published));
        db.ChangeTracker.Clear(); // the update above bypassed this context (each request has its own context in the app)
        Fails(await setup.SaveEventAsync(ev, new PtcEventInput(title, null, year, Day, Day.AddDays(4))), "cannot change");
        Ok(await setup.SaveEventAsync(ev, new PtcEventInput(title + " final", "Final notes", year, Day.AddDays(-1), Day.AddDays(4))));
    }

    // ------------------------------------------------------------------ classes & dates

    [Fact]
    public async Task Classes_get_their_own_dates_and_can_be_reassigned_while_draft()
    {
        var (ev, _) = await EventAsync();
        var a = await ClassAsync();
        var b = await ClassAsync();
        await using var db = fx.NewContext();
        var setup = Setup(db);

        // Nothing is saved when one class is invalid (date outside the event).
        Fails(await setup.AssignClassesAsync(ev, [new(a, Day, T8, T14), new(b, Day.AddDays(10), T8, T14)]), "outside the event dates");
        Assert.False(await db.PtcClassSchedules.AnyAsync(s => s.PtcEventId == ev));
        Fails(await setup.AssignClassesAsync(ev, [new(a, null, T8, T14)]), "Choose the PTC date");

        // Different classes on different days.
        Ok(await setup.AssignClassesAsync(ev, [new(a, Day, T8, T14), new(b, Day.AddDays(1), T8, T14)]));
        var scheduleA = await db.PtcClassSchedules.AsNoTracking().SingleAsync(s => s.PtcEventId == ev && s.SchoolClassId == a);
        Ok(await setup.AddRoundAsync(scheduleA.Id, T8));

        // Reassign A to another day (its Round is kept) and take B out.
        Ok(await setup.AssignClassesAsync(ev, [new(a, Day.AddDays(2), T8, T14)]));
        var schedules = await db.PtcClassSchedules.AsNoTracking().Where(s => s.PtcEventId == ev).ToListAsync();
        Assert.Equal((a, Day.AddDays(2)), (schedules.Single().SchoolClassId, schedules.Single().EventDate));
        Assert.Equal(1, await db.PtcRounds.CountAsync(r => r.PtcClassScheduleId == scheduleA.Id));
        // A daily time that would leave the Round outside is refused.
        Fails(await setup.UpdateClassScheduleAsync(scheduleA.Id, Day.AddDays(2), new TimeOnly(9, 0), T14, null), "outside");

        // The same class on the same date in another active event is refused.
        var (other, _) = await EventAsync();
        Fails(await Setup(db).ScheduleClassAsync(other, a, Day.AddDays(2), T8, T14, null), "already has PTC");
        Ok(await Setup(db).ScheduleClassAsync(other, a, Day.AddDays(3), T8, T14, null));
    }

    // ------------------------------------------------------------------ rounds

    [Fact]
    public async Task Rounds_are_configured_validated_and_renumbered()
    {
        var (ev, _) = await EventAsync();
        var a = await ClassAsync();
        var b = await ClassAsync();
        await using var db = fx.NewContext();
        var setup = Setup(db);
        Ok(await setup.AssignClassesAsync(ev, [new(a, Day, T8, T14), new(b, Day, T8, new TimeOnly(12, 0))]));
        var sa = (await db.PtcClassSchedules.AsNoTracking().SingleAsync(s => s.PtcEventId == ev && s.SchoolClassId == a)).Id;
        var sb = (await db.PtcClassSchedules.AsNoTracking().SingleAsync(s => s.PtcEventId == ev && s.SchoolClassId == b)).Id;

        // A plan that does not hold its meetings, or leaves the daily time, is refused.
        Fails(await setup.AddRoundsAsync(sa, new PtcRoundPlan(T8, 1, 70, 9, 8, 7)), "cannot hold 9 meetings");
        Fails(await setup.AddRoundsAsync(sa, new PtcRoundPlan(new TimeOnly(13, 30), 1, 70, 9, 7, 7)), "outside the daily time");

        // Three default Rounds from 08:00, also given to the other class of the day (it has none yet).
        Ok(await setup.AddRoundsAsync(sa, new PtcRoundPlan(T8, 3, 70, 9, 7, 7), sameDayClasses: true));
        var rounds = await db.PtcRounds.AsNoTracking().Where(r => r.PtcClassScheduleId == sa).OrderBy(r => r.RoundNumber).ToListAsync();
        Assert.Equal(new[] { (1, "08:00", "09:10"), (2, "09:10", "10:20"), (3, "10:20", "11:30") },
            rounds.Select(r => (r.RoundNumber, r.StartTime.ToString("HH:mm"), r.EndTime.ToString("HH:mm"))));
        Assert.All(rounds, r => Assert.Equal((9, 7, 7), (r.Capacity, r.MeetingDurationMinutes, r.TransitionDurationMinutes)));
        Assert.Equal(3, await db.PtcRounds.CountAsync(r => r.PtcClassScheduleId == sb));

        // Overlap with an existing Round is refused; an earlier custom Round (6 parents × 10 min) is accepted and numbered first.
        Fails(await setup.AddRoundsAsync(sa, new PtcRoundPlan(new TimeOnly(9, 0), 1, 70, 9, 7, 7)), "overlaps Round");
        Ok(await setup.AddRoundsAsync(sa, new PtcRoundPlan(new TimeOnly(11, 30), 1, 60, 6, 10, 0)));
        Ok(await setup.RemoveRoundAsync(rounds[0].Id));
        var after = await db.PtcRounds.AsNoTracking().Where(r => r.PtcClassScheduleId == sa).OrderBy(r => r.RoundNumber).ToListAsync();
        Assert.Equal(new[] { (1, "09:10", 9), (2, "10:20", 9), (3, "11:30", 6) }, after.Select(r => (r.RoundNumber, r.StartTime.ToString("HH:mm"), r.Capacity)));
    }

    // ------------------------------------------------------------------ readiness and publishing

    [Fact]
    public async Task An_empty_timetable_or_kg_class_blocks_publishing_but_the_draft_can_exist()
    {
        var (ev, _) = await EventAsync();
        var plain = await ClassAsync();
        var kg = await ClassAsync("KG1");
        await StudentAsync(plain, Phone());
        await using var db = fx.NewContext();
        var setup = Setup(db);

        // Nothing assigned: not yet configured, cannot publish.
        var empty = (await AdminService(db).EventAsync(ev))!;
        Assert.Equal(PtcReadiness.NotConfigured, empty.Readiness.Status);
        Fails(await setup.PublishAsync(ev, true), "No classes are assigned");

        Ok(await setup.AssignClassesAsync(ev, [new(plain, Day, T8, T14), new(kg, Day, T8, T14)]));
        var detail = (await AdminService(db).EventAsync(ev))!;
        Assert.Equal(PtcReadiness.Blocked, detail.Readiness.Status);
        var issues = detail.Readiness.Blockers.Select(i => i.Message).ToList();
        Assert.Contains(issues, m => m.Contains("No timetable assignments found for") && m.Contains("cannot be identified"));
        // The KG class follows the same rule as every class: no timetable lessons, the same blocking issue.
        var kgName = detail.Classes.Single(c => c.ClassId == kg).ClassName;
        Assert.Contains(issues, m => m.Contains($"No timetable assignments found for {kgName}"));
        Assert.DoesNotContain(issues, m => m.Contains("selected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, m => m.Contains("has no Rounds configured"));
        Assert.Empty(detail.Classes.Single(c => c.ClassId == plain).Subjects);   // nothing invented

        var result = await setup.PublishAsync(ev, true);
        Fails(result, "No timetable assignments found");
        Assert.Equal(PtcEventStatus.Draft, (await db.PtcEvents.AsNoTracking().SingleAsync(e => e.Id == ev)).Status);
        Assert.False(await db.TimetableAssignments.AnyAsync(t => t.SchoolClassId == plain || t.SchoolClassId == kg));
    }

    [Fact]
    public async Task Publishing_needs_a_ready_configuration_and_acknowledged_warnings_and_only_published_events_are_bookable()
    {
        var (ev, _, cls, schedule, student, phone) = await ReadyEventAsync();
        await using var db = fx.NewContext();
        var setup = Setup(db);
        var roundId = await db.PtcRounds.Where(r => r.PtcClassScheduleId == schedule).Select(r => r.Id).SingleAsync();
        var lessonId = await db.TimetableAssignments.Where(t => t.SchoolClassId == cls).Select(t => t.Id).SingleAsync();
        var booking = new PtcBookingService(db, new Admin(), new User());

        // A Draft is never bookable.
        Fails(await booking.BookAsync(new PtcBookingRequest(roundId, student, phone, [PtcSubjectOption.LessonKey(lessonId)])), "not published");

        // A student without a parent number: a warning (Incomplete), publishable only when acknowledged.
        await StudentAsync(cls, null);
        var detail = (await AdminService(db).EventAsync(ev))!;
        Assert.Equal(PtcReadiness.Incomplete, detail.Readiness.Status);
        Assert.Contains(detail.Readiness.Warnings, w => w.Message.Contains("1 of 2 students") && w.Area == PtcIssueArea.ParentContacts);
        Fails(await setup.PublishAsync(ev, false), "Confirm that you have reviewed");
        Ok(await setup.PublishAsync(ev, true));

        // Published: bookable, structure locked.
        Ok(await booking.BookAsync(new PtcBookingRequest(roundId, student, phone, [PtcSubjectOption.LessonKey(lessonId)])));
        Fails(await setup.AddRoundAsync(schedule, new TimeOnly(10, 0)), "return it to Draft");
        Fails(await setup.AssignClassesAsync(ev, []), "return it to Draft");
        Fails(await setup.RemoveClassScheduleAsync(schedule), "return it to Draft");
        // Bookings are protected: no return to Draft, no cancelling.
        Fails(await setup.ReturnToDraftAsync(ev), "confirmed booking");
        Fails(await setup.CancelEventAsync(ev), "confirmed booking");
        Fails(await setup.CompleteEventAsync(ev), "from its last date");
        Assert.Equal(1, await db.PtcParentBookings.CountAsync(b => b.Round.ClassSchedule.PtcEventId == ev));
    }

    [Fact]
    public async Task A_fully_ready_event_publishes_without_warnings_and_returns_to_draft_when_unbooked()
    {
        var (ev, _, _, _, _, _) = await ReadyEventAsync();
        await using var db = fx.NewContext();
        var setup = Setup(db);
        var detail = (await AdminService(db).EventAsync(ev))!;
        Assert.Equal(PtcReadiness.Ready, detail.Readiness.Status);
        Assert.Equal((1, 1, 9), (detail.Assigned.Count, detail.Rounds, detail.Capacity));
        Ok(await setup.PublishAsync(ev, false));
        Ok(await setup.ReturnToDraftAsync(ev));
        Ok(await setup.CancelEventAsync(ev));
        Ok(await setup.ReopenEventAsync(ev));
        Assert.Equal(PtcEventStatus.Draft, (await db.PtcEvents.AsNoTracking().SingleAsync(e => e.Id == ev)).Status);
    }

    // ------------------------------------------------------------------ parent contacts

    [Fact]
    public async Task Missing_and_unusable_parent_numbers_are_reported_without_changing_them()
    {
        var cls = await ClassAsync();
        var good = await StudentAsync(cls, "0100 123 4567");
        var missing = await StudentAsync(cls, null);
        var unusable = await StudentAsync(cls, "12");
        await using var db = fx.NewContext();
        var report = await AdminService(db).ParentContactsAsync();

        Assert.Equal(new PtcContactCounts(3, 1, 1, 1), report.Classes.Single(c => c.ClassId == cls).Counts);
        Assert.Contains(report.Missing, s => s.Id == missing);
        Assert.Contains(report.Unusable, s => s.Id == unusable && s.Stored == "12");
        var recorded = report.Recorded.Single(s => s.Id == good);
        Assert.Equal(("0100 123 4567", "+201001234567"), (recorded.Stored, recorded.Normalized));
        Assert.Equal("0100 123 4567", await db.Students.Where(s => s.Id == good).Select(s => s.ParentContactNumber).SingleAsync());

        var dashboard = await AdminService(db).DashboardAsync();
        Assert.Contains(dashboard.Indicators, i => i.Title == "Parent contact numbers" && i.Status != PtcReadiness.Ready);
    }

    // ------------------------------------------------------------------ School Calendar

    [Fact]
    public async Task Only_published_events_appear_on_the_school_calendar_and_follow_their_edits()
    {
        var (ev, year, _, _, _, _) = await ReadyEventAsync();
        await using var db = fx.NewContext();
        var calendar = new SchoolCalendarService(db);
        async Task<List<CalendarEventView>> PtcLines()
        {
            var page = await calendar.PageAsync(year);
            return page.Months.SelectMany(m => m.Rows).Concat(page.Unplaced).Select(r => r.Event).Where(e => e.IsPtc).ToList();
        }

        Assert.Empty(await PtcLines());                                  // Draft: internal only
        Ok(await Setup(db).PublishAsync(ev, true));
        var line = Assert.Single(await PtcLines());
        Assert.Equal((ev, -ev), (line.PtcEventId!.Value, line.Id));
        Assert.Contains("Parent-Teacher Conference", line.Notes);
        Assert.Contains((await calendar.DayAsync(year, Day))!.Events, e => e.PtcEventId == ev);
        Assert.Equal(0, (await calendar.PageAsync(year)).EventCount);   // nothing copied into the calendar's own events
        Assert.False(await db.SchoolCalendarEvents.AnyAsync(e => e.AcademicYearId == year));

        // Edited: the calendar shows the new title; back to Draft / cancelled: gone.
        var current = await db.PtcEvents.AsNoTracking().SingleAsync(e => e.Id == ev);
        Ok(await Setup(db).SaveEventAsync(ev, new PtcEventInput("Renamed PTC " + DbFixture.Unique(), null, year, current.StartDate, current.EndDate)));
        Assert.StartsWith("Renamed PTC", Assert.Single(await PtcLines()).Title);
        Ok(await Setup(db).CancelEventAsync(ev));
        Assert.Empty(await PtcLines());
    }
}
