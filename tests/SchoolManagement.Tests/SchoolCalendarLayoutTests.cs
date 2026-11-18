using System.Net;
using System.Text.RegularExpressions;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// School Calendar as one continuous run of months: no quarter sections, the start and end of every quarter highlighted
/// on its date and listed under its month, and each month's events and milestones in date order.
/// </summary>
[Collection("db")]
public sealed class SchoolCalendarLayoutTests(DbFixture fx)
{
    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Calendar tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private sealed record Setup(int YearId, int[] QuarterIds, int Concert, int Midterm, int Sports, int Hidden, int Undated);

    private static readonly DateOnly[] Starts = [new(2051, 8, 1), new(2051, 11, 1), new(2052, 1, 9), new(2052, 4, 2)];
    private static readonly DateOnly[] Ends = [new(2051, 10, 31), new(2052, 1, 8), new(2052, 4, 1), new(2052, 7, 31)];

    private async Task<Setup> YearAsync()
    {
        await using var db = fx.NewContext();
        var belongings = new BelongingsService(db, DbFixture.Timetable(db), new TestUser());
        var year = await belongings.CreateYearAsync("Calendar " + DbFixture.Unique());
        Assert.True(year.Succeeded);
        var quarters = (await belongings.YearsAsync()).Single(y => y.Id == year.EntityId).Quarters.OrderBy(q => q.Number).Select(q => q.Id).ToArray();
        var svc = new SchoolCalendarService(db);
        for (var i = 0; i < 4; i++) Assert.True((await svc.UpdateQuarterDatesAsync(quarters[i], Starts[i], Ends[i])).Succeeded);

        async Task<int> Event(string title, DateOnly? start, DateOnly? end, DateOnly listMonth, int order, bool hidden = false)
        {
            var r = await svc.SaveEventAsync(null, year.EntityId!.Value,
                new CalendarEventInput(title, start, end, null, listMonth, CalendarColor.None, null, false, null, order, hidden));
            Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
            return r.EntityId!.Value;
        }
        var concert = await Event("Winter concert", new(2052, 1, 5), null, new(2052, 1, 1), 5);
        var midterm = await Event("Midterm week", new(2051, 10, 26), new(2051, 11, 4), new(2051, 10, 1), 1);
        var sports = await Event("Sports day", new(2052, 1, 20), null, new(2052, 1, 1), 1);       // printed first, but later in the month
        var hidden = await Event("Hidden rehearsal", new(2052, 2, 10), null, new(2052, 2, 1), 1, hidden: true);
        var undated = await Event("Science fair", null, null, new(2052, 3, 1), 1);
        Assert.True((await svc.SetMarkAsync(year.EntityId!.Value, new(2052, 1, 9), CalendarColor.Yellow)).Succeeded);
        return new Setup(year.EntityId!.Value, quarters, concert, midterm, sports, hidden, undated);
    }

    [Fact]
    public async Task Months_run_continuously_with_quarter_milestones_and_items_in_date_order()
    {
        var y = await YearAsync();
        await using var db = fx.NewContext();
        var page = await new SchoolCalendarService(db).PageAsync(y.YearId);

        // One continuous run of whole months: August 2051 to July 2052, each once, in order.
        Assert.Equal(Enumerable.Range(0, 12).Select(i => new DateOnly(2051, 8, 1).AddMonths(i)), page.Months.Select(m => m.Month));

        // Every quarter's first and last day, under its own month; January and April hold an end and a start.
        string[] Labels(int year, int month) => page.Months.Single(m => m.Month == new DateOnly(year, month, 1)).Milestones.Select(x => x.Label).ToArray();
        Assert.Equal(["Start of Q1"], Labels(2051, 8));
        Assert.Equal(["End of Q1"], Labels(2051, 10));
        Assert.Equal(["Start of Q2"], Labels(2051, 11));
        Assert.Equal(["End of Q2", "Start of Q3"], Labels(2052, 1));
        Assert.Equal(["End of Q3", "Start of Q4"], Labels(2052, 4));
        Assert.Equal(["End of Q4"], Labels(2052, 7));
        Assert.Equal(8, page.Months.Sum(m => m.Milestones.Count()));
        Assert.Empty(Labels(2051, 9));

        // January in date order: concert (5th), end of Q2 (8th), start of Q3 (9th), sports day (20th) — not the printed order.
        var january = page.Months.Single(m => m.Month == new DateOnly(2052, 1, 1));
        Assert.Equal(["Winter concert", "End of Q2", "Start of Q3", "Sports day"],
            january.Items.Select(i => i.Milestone?.Label ?? i.Row!.Event.Title).ToArray());

        // The milestone dates carry the milestone in the grid; a printed colour on the same date is kept.
        var days = january.Weeks.SelectMany(w => w).Where(d => d.InMonth).ToDictionary(d => d.Date);
        Assert.Equal("End of Q2", Assert.Single(days[new(2052, 1, 8)].Milestones).Label);
        var start = days[new(2052, 1, 9)];
        Assert.Equal(("Start of Q3", CalendarColor.Yellow), (Assert.Single(start.Milestones).Label, start.Color));
        Assert.All(days.Values.Where(d => d.Date.Day is not (8 or 9)), d => Assert.Empty(d.Milestones));

        // Events keep their months: a multi-month event under its month, continued in the next; undated under its month;
        // hidden ones are listed separately, never lost.
        var october = page.Months.Single(m => m.Month == new DateOnly(2051, 10, 1)).Rows.ToList();
        var november = page.Months.Single(m => m.Month == new DateOnly(2051, 11, 1)).Rows.ToList();
        Assert.Contains(october, r => r.Event.Id == y.Midterm && !r.Continuation);
        Assert.Contains(november, r => r.Event.Id == y.Midterm && r.Continuation);
        Assert.Contains(page.Months.Single(m => m.Month == new DateOnly(2052, 3, 1)).Rows, r => r.Event.Id == y.Undated);
        Assert.DoesNotContain(page.Months.SelectMany(m => m.Rows), r => r.Event.Id == y.Hidden);
        Assert.Contains(page.Unplaced, r => r.Event.Id == y.Hidden);
        Assert.Equal(5, page.EventCount);
        Assert.Equal(1, page.MarkCount);

        // The quarters' own dates are read, never changed.
        Assert.Equal(Starts.Zip(Ends).Select(p => ((DateOnly?)p.First, (DateOnly?)p.Second)), page.Quarters.Select(q => (q.Start, q.End)));
    }

    [Fact]
    public async Task Calendar_page_shows_months_without_quarter_sections_and_editing_still_works()
    {
        var y = await YearAsync();
        var client = fx.Factory.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/school-calendar?year={y.YearId}"));

        Assert.DoesNotContain("sc-quarter", html);
        Assert.DoesNotContain("sc-qnav", html);
        Assert.Equal(12, Regex.Matches(html, "<article class=\"sc-month\"").Count);
        foreach (var label in new[] { "Start of Q1", "End of Q1", "Start of Q2", "End of Q2", "Start of Q3", "End of Q3", "Start of Q4", "End of Q4" })
            Assert.Equal(1, Regex.Matches(html, $">{label}</span>").Count);
        Assert.Equal(4, Regex.Matches(html, "sc-day__flag sc-day__flag--start\">Q").Count);
        Assert.Equal(4, Regex.Matches(html, "sc-day__flag sc-day__flag--end\">Q").Count);
        Assert.DoesNotContain("sc-legend", html);                                       // no key above the months
        Assert.DoesNotContain("Start of a quarter", html);

        // Every milestone opens its quarter's dates; every event opens its edit form, which still loads.
        foreach (var q in y.QuarterIds) Assert.Contains($"href=\"/school-calendar/quarters/{q}/edit\" data-modal title=\"Change the dates", html);
        foreach (var id in new[] { y.Concert, y.Midterm, y.Sports, y.Undated })
        {
            Assert.Contains($"/school-calendar/events/{id}/edit", html);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/school-calendar/events/{id}/edit")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/school-calendar/days/2052-01-09?year={y.YearId}")).StatusCode);
    }
}
