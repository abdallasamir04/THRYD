using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Staff → Management Team → School Leadership: the former Team Members and Management Positions pages in one module,
/// reading and writing the same members, positions, appointments and tasks.
/// </summary>
[Collection("db")]
public class LeadershipTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private sealed class User : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string formPage, string action, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(formPage);
        var body = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", Token.Match(page).Groups[1].Value) };
        body.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return await client.PostAsync(action, new FormUrlEncodedContent(body));
    }

    private HttpClient NoRedirects() => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Management_Team_shows_one_School_Leadership_card_and_the_old_pages_lead_to_it()
    {
        var client = NoRedirects();
        var landing = await client.GetStringAsync("/management");
        Assert.Matches("<a class=\"hub-card hub-card--\\w+\" href=\"/management/leadership\">", landing);
        Assert.Contains("School Leadership", landing);
        Assert.Contains("Manage school leadership members, positions, responsibilities, and appointment history in one place.", landing);
        Assert.DoesNotContain("Team Members", landing);
        Assert.DoesNotContain("Management Positions", landing);
        Assert.Contains("href=\"/management/tasks\"", landing); // Tasks stays

        // The navigation path is unchanged: still Staff → Management Team, with Staff highlighted in the sidebar.
        var page = await client.GetStringAsync("/management/leadership");
        Assert.Contains("<a href=\"/staff\">Staff</a></li><li><a href=\"/management\">Management Team</a></li>", page);
        var sidebar = page[..page.IndexOf("</aside>", StringComparison.Ordinal)];
        Assert.DoesNotContain("/management/leadership", sidebar);

        // Old addresses redirect into the unified module, keeping the filters.
        var members = await client.GetAsync("/management/members?search=ab&position=none");
        Assert.Equal(HttpStatusCode.Redirect, members.StatusCode);
        Assert.Equal("/management/leadership?search=ab&position=none#members", members.Headers.Location!.OriginalString);
        var positions = await client.GetAsync("/management/positions");
        Assert.Equal(HttpStatusCode.Redirect, positions.StatusCode);
        Assert.Equal("/management/leadership#positions", positions.Headers.Location!.OriginalString);
        // Links of the former tabs land on the matching part of the one section.
        Assert.Equal("/management/leadership?status=vacant#positions", (await client.GetAsync("/management/leadership?tab=positions&status=vacant")).Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Leadership_page_shows_every_member_without_positions_table_history_or_filters()
    {
        await using var db = fx.NewContext();
        var mgmt = DbFixture.Management(db);
        await mgmt.CreateAsync(new ManagementMemberInput { FullName = "Leader " + DbFixture.Unique() });
        var memberIds = await db.ManagementMembers.Select(m => m.Id).ToListAsync();
        var appointments = await db.PositionAssignments.CountAsync();
        var client = fx.Factory.CreateClient();

        // One School Leadership section with every member's card (no tabs).
        var page = await client.GetStringAsync("/management/leadership");
        Assert.Equal(1, Regex.Matches(page, "class=\"card leadership\"").Count);
        Assert.DoesNotContain("?tab=", page);
        foreach (var id in memberIds) Assert.Contains($"href=\"/management/members/{id}\"", page);
        Assert.Equal(memberIds.Count, Regex.Matches(page, "<article class=\"leader-card ").Count);
        Assert.Contains("data-download=\"/management/members/pdf\"", page);

        // Removed: Current Leadership Positions (table, Replace / Remove), Appointment History, and the search / position filters.
        foreach (var gone in new[] { "id=\"positions\"", "leadership-table", "Leadership Positions", "Current holder", "leadership__history", "Appointment history",
                     "leadership__filters", "Search members", "All positions", "name=\"search\"", "name=\"position\"" })
            Assert.DoesNotContain(gone, page);
        foreach (var p in ManagementPositions.All) Assert.DoesNotContain($"/management/positions/{p}/assign", page);
        // A stale search link still shows every member (the page has no filter any more).
        Assert.Equal(memberIds.Count, Regex.Matches(await client.GetStringAsync("/management/leadership?search=zzzz&position=none"), "<article class=\"leader-card ").Count);

        // Nothing was deleted: positions keep their own pages, with holder actions and the appointment history.
        Assert.Equal(appointments, await db.PositionAssignments.CountAsync());
        foreach (var p in ManagementPositions.All)
        {
            var position = await client.GetAsync(ManagementPositions.Href(p));
            Assert.Equal(HttpStatusCode.OK, position.StatusCode);
            Assert.Contains($"/management/positions/{p}/assign", await position.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/management/leadership/positions/Janitor")).StatusCode);
    }

    [Fact]
    public async Task Members_are_added_edited_assigned_and_replaced_from_the_unified_module_with_history_kept()
    {
        const ManagementPosition position = ManagementPosition.UpperElementaryCoordinator;
        int? original;
        await using (var db = fx.NewContext())
            original = (await DbFixture.Management(db).PositionsAsync()).Single(s => s.Position == position).Holder?.Id;

        var client = NoRedirects();
        string first = "Lead A " + DbFixture.Unique(), second = "Lead B " + DbFixture.Unique();
        try
        {
            // Add (from the School Leadership page's "Add member").
            var added = await PostFormAsync(client, "/management/members/create", "/management/members/create", ("FullName", first), ("ContactNumber", "0100 111 2222"));
            Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
            var a = int.Parse(added.Headers.Location!.OriginalString.Split('/').Last());
            var b = int.Parse((await PostFormAsync(client, "/management/members/create", "/management/members/create", ("FullName", second))).Headers.Location!.OriginalString.Split('/').Last());

            // Edit.
            var edited = await PostFormAsync(client, $"/management/members/{a}/edit", $"/management/members/{a}/edit", ("FullName", first), ("Email", "lead.a@example.org"));
            Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
            Assert.Equal("/management/leadership", edited.Headers.Location!.OriginalString);

            // Assign, then replace — both land on the position's page.
            var assign = $"/management/positions/{position}/assign";
            var today = SchoolWeek.Iso(SchoolWeek.Today);
            var r1 = await PostFormAsync(client, assign, assign, ("MemberId", a.ToString()), ("StartDate", today));
            Assert.Equal(ManagementPositions.Href(position), r1.Headers.Location!.OriginalString);
            var r2 = await PostFormAsync(client, assign, assign, ("MemberId", b.ToString()), ("StartDate", today));
            Assert.Equal(ManagementPositions.Href(position), r2.Headers.Location!.OriginalString);

            await using (var db = fx.NewContext())
            {
                var tasks = new ManagementTaskService(db, new User());
                Assert.True((await tasks.CreateAsync(new TaskInput { AssignedToId = b, Title = "Review reading levels " + DbFixture.Unique() })).Succeeded);
                Assert.Equal("lead.a@example.org", (await db.ManagementMembers.SingleAsync(m => m.Id == a)).Email);
                Assert.Equal(1, await db.PositionAssignments.CountAsync(p => p.Position == position && p.IsCurrent));
            }

            // The position page: holder, the holder's tasks, and the previous holder in the appointment history.
            var page = await client.GetStringAsync(ManagementPositions.Href(position));
            Assert.Contains(second, page);
            Assert.Contains("Review reading levels", page);
            Assert.Contains("Replace holder", page);
            var history = page[page.IndexOf("poshist-title", StringComparison.Ordinal)..];
            Assert.Contains(first, history);
            Assert.Contains("Replaced by " + second, history);
            Assert.Contains("id=\"task-list\"", page);

            // The member profile links back to the position and keeps the appointment history.
            var profile = await client.GetStringAsync($"/management/members/{a}");
            Assert.Contains("<a href=\"/management/leadership\">School Leadership</a>", profile);
            Assert.Contains($"href=\"{ManagementPositions.Href(position)}\"", profile);
            Assert.Contains("Replaced by " + second, profile);
        }
        finally
        {
            await using var db = fx.NewContext();
            Assert.True((await DbFixture.Management(db).ReassignPositionAsync(position, original, SchoolWeek.Today)).Succeeded); // restore
        }
    }
}
