using System.Net;
using System.Text.RegularExpressions;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// School Calendar as one continuous run of months (from the quarters' dates): no quarter sections, each month listing
/// only its events in date order, no quarter milestone lines or flags and no dots under the dates.
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
    public async Task Months_run_continuously_and_list_only_their_events_in_date_order()
    {
        var y = await YearAsync();
        await using var db = fx.NewContext();
        var page = await new SchoolCalendarService(db).PageAsync(y.YearId);

        // One continuous run of whole months: August 2051 to July 2052, each once, in order.
        Assert.Equal(Enumerable.Range(0, 12).Select(i => new DateOnly(2051, 8, 1).AddMonths(i)), page.Months.Select(m => m.Month));

        // Quarter starts and ends are not event lines: August (Q1 starts on the 1st) lists nothing.
        Assert.Empty(page.Months.Single(m => m.Month == new DateOnly(2051, 8, 1)).Rows);

        // January in date order: concert (5th), sports day (20th) — not the printed order; nothing for the quarter change.
        var january = page.Months.Single(m => m.Month == new DateOnly(2052, 1, 1));
        Assert.Equal(["Winter concert", "Sports day"], january.Rows.Select(r => r.Event.Title).ToArray());

        // A printed colour on a date is kept (the 9th, also the start of Q3, is only its yellow).
        var days = january.Weeks.SelectMany(w => w).Where(d => d.InMonth).ToDictionary(d => d.Date);
        Assert.Equal(CalendarColor.Yellow, days[new(2052, 1, 9)].Color);
        Assert.Equal(CalendarColor.None, days[new(2052, 1, 8)].Color);

        // Events keep their months: a multi-month event is listed once, under its month (not repeated in the next, whose
        // dates still carry it); undated under its month; hidden ones are listed separately, never lost.
        var october = page.Months.Single(m => m.Month == new DateOnly(2051, 10, 1)).Rows.ToList();
        var novemberMonth = page.Months.Single(m => m.Month == new DateOnly(2051, 11, 1));
        Assert.Contains(october, r => r.Event.Id == y.Midterm && !r.Continuation);
        Assert.DoesNotContain(novemberMonth.Rows, r => r.Event.Id == y.Midterm);
        Assert.Contains("Midterm week", novemberMonth.Weeks.SelectMany(w => w).Single(d => d.Date == new DateOnly(2051, 11, 3)).Events);
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
        // No quarter milestone lines or flags, and no dots under the dates.
        foreach (var label in new[] { "Start of Q1", "End of Q1", "Start of Q2", "End of Q2", "Start of Q3", "End of Q3", "Start of Q4", "End of Q4" })
            Assert.DoesNotContain(label, html);
        Assert.DoesNotContain("sc-day__flag", html);
        Assert.DoesNotContain("sc-ms", html);
        Assert.DoesNotContain("sc-day__dot", html);
        Assert.DoesNotContain("sc-legend", html);                                       // no key above the months
        Assert.DoesNotContain("Start of a quarter", html);

        // No Quarters bar above the months; the quarters themselves are kept and their dates still open and save.
        Assert.DoesNotContain("sc-qlink", html);
        Assert.DoesNotContain(">Quarters<", html);
        foreach (var q in y.QuarterIds)
        {
            Assert.DoesNotContain($"/school-calendar/quarters/{q}/edit", html);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/school-calendar/quarters/{q}/edit")).StatusCode);
        }
        // Every event opens its edit form, which still loads.
        foreach (var id in new[] { y.Concert, y.Midterm, y.Sports, y.Undated })
        {
            Assert.Contains($"/school-calendar/events/{id}/edit", html);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/school-calendar/events/{id}/edit")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/school-calendar/days/2052-01-09?year={y.YearId}")).StatusCode);
    }
}
