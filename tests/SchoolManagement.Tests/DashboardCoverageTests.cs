using System.Net;
using System.Text.RegularExpressions;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Dashboard "Duties covered today": fully covered SECTIONS ÷ sections per category. Morning / Midday / Dismissal
/// sections are the duty cards (DutyComponents); Activities sections are the activities scheduled that day (P.E. once,
/// each active created activity once). A section is covered only when everything scheduled in it is Covered.
/// </summary>
[Collection("db")]
public class DashboardCoverageTests(DbFixture fx)
{
    private static void Ok(ServiceResult r) => Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));

    private static (int Covered, int Total) Segment(string html, string name)
    {
        var m = Regex.Match(html, $"<span class=\"hero-cover__name\">{name}</span>\\s*<span class=\"hero-cover__value\">(\\d+)<small> / (\\d+)</small>");
        Assert.True(m.Success, $"No numeric {name} segment");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    [Fact]
    public async Task Duty_categories_count_sections_once_and_a_section_is_covered_only_when_all_its_duties_are()
    {
        // A past school day nobody else uses: every duty has ended, so statuses come from the recorded attendance.
        var day = new DateOnly(2024, 5, 14);
        Assert.Equal(DayOfWeek.Tuesday, day.DayOfWeek);
        var (staff, _) = await fx.NewTeacherAsync();
        var (other, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        async Task<int> Add(DutyComponent c, int who, TimeOnly start, AttendanceStatus att, DateOnly? on = null)
        {
            var r = await duties.CreateAsync(new DutyInput(c, on ?? day, "Test", start, start.AddMinutes(20), null, who, att, null, null, []));
            Ok(r);
            return r.EntityId!.Value;
        }

        var empty = await duties.SectionCoverageAsync(day);
        foreach (var t in DutyComponents.Types)
        {
            var n = DutyComponents.ForType(t).Count;
            Assert.Equal(new SectionCoverage(n, 0, 0, 0, n), empty[t]);
        }

        // Two coverage points in Students Arrival Check are one section; one without recorded presence makes it Not Covered.
        var a1 = await Add(DutyComponent.StudentsArrival, staff, new(7, 0), AttendanceStatus.Present);
        var a2 = await Add(DutyComponent.StudentsArrival, other, new(7, 30), AttendanceStatus.NotRecorded);
        Assert.Equal(new SectionCoverage(3, 0, 0, 1, 2), (await duties.SectionCoverageAsync(day))[DutyType.Morning]);

        // Once every point has someone recorded present, the section counts once as covered.
        Ok(await duties.SetPrimaryAttendanceAsync(a2, AttendanceStatus.Present));
        Assert.Equal(new SectionCoverage(3, 1, 0, 0, 2), (await duties.SectionCoverageAsync(day))[DutyType.Morning]);

        // A point reported not covered makes its section Not Covered (a reason never counts as coverage); others never moved.
        var k = await Add(DutyComponent.Kg2Break, staff, new(10, 0), AttendanceStatus.Present);
        Ok(await DbFixture.DutyBoard(db).ReportUncoveredAsync(new CoverageReportInput(day, DutyComponent.Kg2Break, k, null, DutyUncoveredReason.AreaUnattended, "Left early", null)));
        var now = await duties.SectionCoverageAsync(day);
        Assert.Equal(new SectionCoverage(5, 0, 0, 1, 4), now[DutyType.Midday]);
        Assert.Equal(empty[DutyType.Dismissal], now[DutyType.Dismissal]);

        // A future duty keeps its section open (Pending), never covered.
        var future = new DateOnly(2032, 5, 11);
        await Add(DutyComponent.StudentsArrival, staff, new(7, 0), AttendanceStatus.Present, future);
        Assert.Equal(new SectionCoverage(3, 0, 1, 0, 2), (await duties.SectionCoverageAsync(future))[DutyType.Morning]);
        _ = a1;
    }

    [Fact]
    public async Task Dashboard_shows_the_section_counts_for_today()
    {
        var today = SchoolWeek.Today;
        await using var db = fx.NewContext();
        var html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/"));
        var byType = await DbFixture.Duties(db).SectionCoverageAsync(today);
        foreach (var t in DutyComponents.Types)
        {
            Assert.Equal(DutyComponents.ForType(t).Count, byType[t].Total);
            Assert.Equal((byType[t].Covered, byType[t].Total), Segment(html, DutyComponents.TypeName(t).Replace(" Duty", "")));
        }
        var activities = (await new ActivityService(db, new User()).SessionsOnAsync(today)).Sections;
        Assert.Equal((activities.Covered, activities.Total), Segment(html, "Activities"));
    }

    [Fact]
    public async Task Activities_count_each_scheduled_active_activity_once_and_cover_it_only_when_all_its_sessions_are()
    {
        // A past Tuesday (sessions have started, so coverage can be recorded) far from other tests' data.
        var tuesday = new DateOnly(2020, 3, 10);
        Assert.Equal(DayOfWeek.Tuesday, tuesday.DayOfWeek);
        await using var db = fx.NewContext();
        var activities = new ActivityService(db, new User());
        var before = (await activities.SessionsOnAsync(tuesday)).Sections;

        var chess = await activities.CreateAsync(new ActivityInput($"Chess {DbFixture.Unique()}", "Chess club.", null, null, null, null, null, true));
        var drama = await activities.CreateAsync(new ActivityInput($"Drama {DbFixture.Unique()}", "Drama club.", null, null, null, null, null, true));
        var debate = await activities.CreateAsync(new ActivityInput($"Debate {DbFixture.Unique()}", "Debate club.", null, null, null, null, null, true));
        Ok(chess); Ok(drama); Ok(debate);
        var chessId = chess.EntityId!.Value;
        var m1 = await activities.AddScheduleAsync(chessId, new(DayOfWeek.Tuesday, new(8, 0), new(9, 0), null, null));
        var m2 = await activities.AddScheduleAsync(chessId, new(DayOfWeek.Tuesday, new(14, 0), new(15, 0), null, null));
        Ok(m1); Ok(m2);
        Ok(await activities.AddScheduleAsync(chessId, new(DayOfWeek.Wednesday, new(14, 0), new(15, 0), null, null)));
        Ok(await activities.AddScheduleAsync(drama.EntityId!.Value, new(DayOfWeek.Tuesday, new(14, 0), new(15, 0), null, null)));
        Ok(await activities.SetActiveAsync(drama.EntityId.Value, false)); // inactive: not counted
        Ok(await activities.AddScheduleAsync(debate.EntityId!.Value, new(DayOfWeek.Monday, new(14, 0), new(15, 0), null, null))); // not that day

        // Chess meets twice that Tuesday but is one activity; nothing recorded yet, so it is open.
        Assert.Equal(before with { Total = before.Total + 1, Open = before.Open + 1 }, (await activities.SessionsOnAsync(tuesday)).Sections);

        // One of its two sessions covered: still not fully covered.
        Ok(await activities.SetSessionStatusAsync(ActivitySessionKind.ActivityMeeting, m1.EntityId!.Value, tuesday, ActivitySessionStatus.Covered));
        Assert.Equal(before.Covered, (await activities.SessionsOnAsync(tuesday)).Sections.Covered);

        // Both covered: the activity counts once as covered.
        Ok(await activities.SetSessionStatusAsync(ActivitySessionKind.ActivityMeeting, m2.EntityId!.Value, tuesday, ActivitySessionStatus.Covered));
        Assert.Equal(before with { Total = before.Total + 1, Covered = before.Covered + 1 }, (await activities.SessionsOnAsync(tuesday)).Sections);

        // Not counted on another date, nor once inactive; a non-school day shows 0 / 0.
        Assert.Equal(SectionCoverage.None, (await activities.SessionsOnAsync(tuesday.AddDays(3))).Sections); // Friday
        Ok(await activities.SetActiveAsync(chessId, false));
        Assert.Equal(before, (await activities.SessionsOnAsync(tuesday)).Sections);
        Ok(await activities.SetActiveAsync(chessId, true));

        // Sessions not scheduled that day, or not started yet, cannot be recorded.
        Assert.False((await activities.SetSessionStatusAsync(ActivitySessionKind.ActivityMeeting, m1.EntityId.Value, tuesday.AddDays(1), ActivitySessionStatus.Covered)).Succeeded);
        Assert.False((await activities.SetSessionStatusAsync(ActivitySessionKind.ActivityMeeting, m1.EntityId.Value, new DateOnly(2031, 3, 11), ActivitySessionStatus.Covered)).Succeeded);
        Assert.False((await activities.SetSessionStatusAsync(ActivitySessionKind.PeLesson, int.MaxValue, tuesday, ActivitySessionStatus.Covered)).Succeeded);

        // A holiday: nothing scheduled, so 0 / 0.
        var holiday = new SchoolHoliday { Name = "Coverage test " + DbFixture.Unique(), StartDate = tuesday, EndDate = tuesday, CreatedBy = "Test" };
        db.SchoolHolidays.Add(holiday);
        await db.SaveChangesAsync();
        try { Assert.Equal(SectionCoverage.None, (await activities.SessionsOnAsync(tuesday)).Sections); }
        finally { db.SchoolHolidays.Remove(holiday); await db.SaveChangesAsync(); }
    }

    private sealed class User : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
}
