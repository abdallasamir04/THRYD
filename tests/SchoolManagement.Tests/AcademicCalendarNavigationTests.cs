using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace SchoolManagement.Tests;

/// <summary>School Calendar is its own sidebar item (Academics), no longer inside Belongings; same data and operations.</summary>
[Collection("db")]
public class AcademicCalendarNavigationTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
    private static string Sidebar(string html) => html[..html.IndexOf("</aside>", StringComparison.Ordinal)];

    [Fact]
    public async Task The_calendar_has_its_own_sidebar_item_and_left_Belongings()
    {
        var client = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Its own item under Academics (after Timetables), active on its page.
        var page = await client.GetStringAsync("/school-calendar");
        var sidebar = Sidebar(page);
        Assert.Contains("href=\"/school-calendar\" aria-current=\"page\"", sidebar);
        Assert.Contains("#i-calendar-days", sidebar);
        Assert.True(sidebar.IndexOf("href=\"/timetables\"", StringComparison.Ordinal) < sidebar.IndexOf("href=\"/school-calendar\"", StringComparison.Ordinal));
        Assert.Contains("School Calendar", page);
        // Every other page shows the item, not active.
        Assert.Contains("<a class=\"nav__link\" href=\"/school-calendar\">", Sidebar(await client.GetStringAsync("/")));

        // Belongings no longer leads to it.
        foreach (var url in new[] { "/belongings", "/belongings/personal", "/belongings/academic" })
        {
            var html = await client.GetStringAsync(url);
            Assert.DoesNotContain("/belongings/calendar", html);
            Assert.DoesNotContain("/school-calendar\" ", html[html.IndexOf("</aside>", StringComparison.Ordinal)..]); // not in the page body
        }

        // Old addresses still work: they lead to the new page (forms keep their method).
        var old = await client.GetAsync("/belongings/calendar");
        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal("/school-calendar", old.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.PermanentRedirect, (await client.PostAsync("/belongings/years", new FormUrlEncodedContent([]))).StatusCode);
    }

    [Fact]
    public async Task Years_and_quarters_are_created_and_edited_on_the_new_page_with_the_same_data()
    {
        var client = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = Token.Match(await client.GetStringAsync("/school-calendar")).Groups[1].Value;
        var name = "Year " + DbFixture.Unique();
        var created = await client.PostAsync("/school-calendar/years", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("name", name)]));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/school-calendar", created.Headers.Location!.OriginalString);

        int quarterId;
        await using (var db = fx.NewContext())
        {
            var year = await db.AcademicYears.Include(y => y.Quarters).SingleAsync(y => y.Name == name);
            Assert.Equal(4, year.Quarters.Count);
            quarterId = year.Quarters.OrderBy(q => q.Number).First().Id;
        }
        Assert.Contains(name, await client.GetStringAsync("/school-calendar"));
        Assert.Contains($"/school-calendar/quarters/{quarterId}/edit", await client.GetStringAsync("/school-calendar"));

        var form = await client.GetStringAsync($"/school-calendar/quarters/{quarterId}/edit");
        var saved = await client.PostAsync($"/school-calendar/quarters/{quarterId}/edit", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token.Match(form).Groups[1].Value), new("Name", "Quarter 1"),
            new("StartDate", "2035-01-07"), new("EndDate", "2035-03-01"), // Sunday 7 Jan → Thursday 1 Mar 2035: 8 school weeks
        ]));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await using (var db = fx.NewContext())
            Assert.Equal(new DateOnly(2035, 1, 7), await db.AcademicQuarters.Where(q => q.Id == quarterId).Select(q => q.StartDate).SingleAsync());
    }
}
