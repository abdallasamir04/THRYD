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
    public async Task Management_Team_shows_School_Leadership_and_Operation_Team_cards_and_the_old_pages_lead_to_them()
    {
        var client = NoRedirects();
        var landing = await client.GetStringAsync("/management");
        Assert.Matches("<a class=\"hub-card hub-card--\\w+\" href=\"/management/leadership\">", landing);
        Assert.Matches("<a class=\"hub-card hub-card--\\w+\" href=\"/management/operation-team\">", landing);
        Assert.Contains("School Leadership", landing);
        Assert.Contains("Operation Team", landing);
        Assert.Contains("Manage school leadership members, positions, responsibilities, and appointment history in one place.", landing);
        Assert.DoesNotContain("Team Members", landing);
        Assert.DoesNotContain("Management Positions", landing);
        // Two section cards; tasks are reached from the sections (All Tasks / Add Task).
        Assert.Equal(2, Regex.Matches(landing, "class=\"hub-card ").Count);
        Assert.Contains("class=\"hub hub--large \"", landing);
        Assert.DoesNotContain("href=\"/management/tasks\"", landing);

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
    public async Task Leadership_header_has_All_Tasks_and_Add_Task_together_and_Add_Task_creates_a_normal_task()
    {
        var client = NoRedirects();
        var page = await client.GetStringAsync("/management/leadership");
        var head = page[page.IndexOf("class=\"page-head__actions\"", StringComparison.Ordinal)..];
        head = head[..head.IndexOf("<ul class=\"kpis", StringComparison.Ordinal)];

        // Order: All Tasks, Add Task (together in one group), Download Report, Add Member.
        int At(string s) => head.IndexOf(s, StringComparison.Ordinal);
        var allTasks = At("href=\"/tasks\">");
        var addTask = At("href=\"/management/tasks/create\" data-modal>");
        var report = At("/management/leadership/pdf");
        var addMember = At("href=\"/management/members/create\" data-modal>");
        Assert.True(allTasks > 0 && allTasks < addTask && addTask < report && report < addMember, head);
        var group = head[At("class=\"page-head__group\"")..report];
        Assert.Contains("All Tasks", group);
        Assert.Contains("Add Task", group);
        Assert.Contains("Add Member", head);

        // All Tasks opens the existing global Tasks page; Add Task opens the Tasks module's own form.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/tasks")).StatusCode);
        var form = await client.GetStringAsync("/management/tasks/create");
        foreach (var field in new[] { "name=\"Title\"", "name=\"AssignedToId\"", "name=\"Priority\"", "name=\"DueDate\"", "name=\"Description\"" })
            Assert.Contains(field, form);
        Assert.DoesNotContain("name=\"Status\"", form);   // a new task always starts as To Do (set on the server)

        // Saving from School Leadership (the modal posts the same form): one normal task, listed on the global Tasks page once.
        var (teacher, _) = await fx.NewTeacherAsync();
        int tasksBefore, openBefore;
        await using (var db = fx.NewContext())
        {
            tasksBefore = await db.ManagementTasks.CountAsync();
            openBefore = (await new ManagementTaskService(db, new User()).CountsAsync(null)).Open;
        }
        var title = "Leadership task " + DbFixture.Unique();
        var created = await PostFormAsync(client, "/management/tasks/create", "/management/tasks/create",
            ("Title", title), ("Description", "From School Leadership."), ("Priority", "High"), ("Status", "ToDo"),
            ("AssignedToId", teacher.ToString()), ("DueDate", SchoolWeek.Iso(SchoolWeek.Today.AddDays(2))));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        await using (var db = fx.NewContext())
        {
            Assert.Equal(tasksBefore + 1, await db.ManagementTasks.CountAsync());
            var task = await db.ManagementTasks.SingleAsync(t => t.Title == title);
            Assert.Equal(teacher, task.AssignedToId);
            Assert.Equal((await new ManagementTaskService(db, new User()).CountsAsync(null)).Open, openBefore + 1);
        }
        var list = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/tasks?search=" + Uri.EscapeDataString(title)));
        Assert.Single(Regex.Matches(list, Regex.Escape(title) + "</a>"));

        // The modal save answers with the normal success message.
        var ajax = fx.Factory.CreateClient();
        ajax.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        var json = await (await PostFormAsync(ajax, "/management/tasks/create", "/management/tasks/create",
            ("Title", title + " (2)"), ("Priority", "Low"), ("Status", "ToDo"), ("AssignedToId", teacher.ToString()))).Content.ReadAsStringAsync();
        Assert.Contains("\"success\":true", json);
        Assert.Contains("was created.", json);
    }

    [Fact]
    public async Task Leadership_page_shows_every_member_without_positions_table_history_or_filters()
    {
        await using var db = fx.NewContext();
        var mgmt = DbFixture.Management(db);
        await mgmt.CreateAsync(new ManagementMemberInput { FullName = "Leader " + DbFixture.Unique() });
        // School Leadership's members: everyone but the Operation Team (members holding a school-added position).
        var memberIds = await db.ManagementMembers.Where(m => !(m.Position == null && m.CustomPositionId != null)).Select(m => m.Id).ToListAsync();
        var appointments = await db.PositionAssignments.CountAsync();
        var client = fx.Factory.CreateClient();

        // School Leadership shows its organizational structure only: no member-card section (no tabs).
        var page = await client.GetStringAsync("/management/leadership");
        Assert.Empty(Regex.Matches(page, "class=\"card leadership\""));
        Assert.DoesNotContain("?tab=", page);
        Assert.DoesNotContain("Other Leadership Members", page);
        Assert.DoesNotContain("<article class=\"leader-card ", page);
        Assert.Contains("data-download=\"/management/leadership/pdf\"", page);
        // The members are still recorded and still listed in the Staff Directory with their profiles.
        Assert.Equal(memberIds.Count, await db.ManagementMembers.CountAsync(m => memberIds.Contains(m.Id)));
        var directory = await client.GetStringAsync("/staff/all?category=management");
        foreach (var id in memberIds) Assert.Contains($"href=\"/management/members/{id}\"", directory);

        // Removed: Current Leadership Positions (table, Replace / Remove), Appointment History, and the search / position filters.
        foreach (var gone in new[] { "id=\"positions\"", "leadership-table", "Leadership Positions", "Current holder", "leadership__history", "Appointment history",
                     "leadership__filters", "Search members", "All positions", "name=\"search\"", "name=\"position\"" })
            Assert.DoesNotContain(gone, page);
        foreach (var p in ManagementPositions.All) Assert.DoesNotContain($"/management/positions/{p}/assign", page);
        // A stale search link still opens the page (it has no filter any more).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/management/leadership?search=zzzz&position=none")).StatusCode);

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
            Assert.Contains("<a href=\"/management/leadership\">School Leadership</a>", profile);   // a leadership member's section
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
