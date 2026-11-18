using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Academic Departments → Add Task (choose a department) and a department's page → Add Task (that department, fixed): the
/// Tasks module's own form and save, offering and accepting only active members of the department. The server rejects
/// any other executor whatever the browser sends; the tasks are ordinary tasks on the Tasks page.
/// </summary>
[Collection("db")]
public sealed class DepartmentTasksTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private sealed class User : ICurrentUser
    {
        public string? Name => "Department tasks tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private HttpClient Client() => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>The option values of one select of the page.</summary>
    private static List<int> Options(string html, string selectId)
    {
        var start = html.IndexOf($"id=\"{selectId}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, selectId);
        var select = html[start..html.IndexOf("</select>", start, StringComparison.Ordinal)];
        return Regex.Matches(select, "<option value=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToList();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string formPage, string referer, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(formPage);
        var body = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", Token.Match(page).Groups[1].Value) };
        body.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/management/tasks/create") { Content = new FormUrlEncodedContent(body) };
        request.Headers.Referrer = new Uri(client.BaseAddress!, referer);
        return await client.SendAsync(request);
    }

    private async Task<int> MemberAsync(int departmentId, string jobTitle = "Arabic Teacher")
    {
        var (id, _) = await fx.NewTeacherAsync(jobTitle);
        await using var db = fx.NewContext();
        await db.StaffMembers.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, departmentId));
        return id;
    }

    [Fact]
    public async Task Add_Task_from_departments_offers_and_accepts_only_active_members_of_the_department()
    {
        int arabic, english, departments;
        await using (var db = fx.NewContext())
        {
            arabic = await db.Departments.Where(d => d.Name == "Arabic").Select(d => d.Id).SingleAsync();
            english = await db.Departments.Where(d => d.Name == "English").Select(d => d.Id).SingleAsync();
            departments = await db.Departments.CountAsync();
        }
        var arabicTeacher = await MemberAsync(arabic);
        var englishTeacher = await MemberAsync(english, "English Teacher");
        var former = await MemberAsync(arabic);
        int manager;
        await using (var db = fx.NewContext())
        {
            await db.StaffMembers.Where(s => s.Id == former).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContractEndDate, SchoolWeek.Today.AddDays(-10)));
            manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Dept Task Leader " + DbFixture.Unique() })).EntityId!.Value;
        }
        var client = Client();

        // ---------- The buttons: on the Departments page, and on every department's page with its own id.
        Assert.Contains("href=\"/management/tasks/create?by=department\" data-modal>", await client.GetStringAsync("/staff/departments"));
        await using (var db = fx.NewContext())
            foreach (var id in await db.Departments.Select(d => d.Id).ToListAsync())
                Assert.Contains($"href=\"/management/tasks/create?departmentId={id}\" data-modal>", await client.GetStringAsync($"/staff/departments/{id}"));

        // ---------- Departments page form: every department; nobody to assign until one is chosen; the reviewer can be anyone.
        var choose = await client.GetStringAsync("/management/tasks/create?by=department");
        Assert.Contains("data-task-department", choose);
        Assert.Equal(departments, Options(choose, "DepartmentId").Count);
        Assert.Empty(Options(choose, "AssignedToId"));
        Assert.Contains("Choose a department first…", WebUtility.HtmlDecode(choose));
        Assert.Contains(englishTeacher, Options(choose, "ReviewerId"));

        // Choosing a department loads its active members only (no other department, no Management Team, no ended contract).
        var arabicStaff = await client.GetStringAsync($"/management/tasks/department-staff?departmentId={arabic}");
        var ids = Regex.Matches(arabicStaff, "<option value=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Contains(arabicTeacher, ids);
        Assert.DoesNotContain(englishTeacher, ids);
        Assert.DoesNotContain(former, ids);
        Assert.DoesNotContain(manager, ids);
        await using (var db = fx.NewContext())
        {
            var members = await db.StaffMembers.Where(s => s.DepartmentId == arabic && (s.ContractEndDate == null || s.ContractEndDate >= SchoolWeek.Today)).Select(s => s.Id).ToListAsync();
            Assert.Equal(members.Order(), ids.Order());
        }
        // Changing the department: a choice that is not a member there is not kept; one that is, is.
        Assert.DoesNotContain("selected", await client.GetStringAsync($"/management/tasks/department-staff?departmentId={english}&selected={arabicTeacher}"));
        Assert.Contains($"value=\"{arabicTeacher}\" selected", await client.GetStringAsync($"/management/tasks/department-staff?departmentId={arabic}&selected={arabicTeacher}"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/management/tasks/department-staff?departmentId=999999")).StatusCode);

        // ---------- A department's page form: the department fixed, its members only.
        var locked = await client.GetStringAsync($"/management/tasks/create?departmentId={arabic}");
        Assert.Contains($"<input type=\"hidden\" name=\"DepartmentId\" value=\"{arabic}\" />", locked);
        Assert.Contains("name=\"DepartmentLocked\" value=\"true\"", locked);
        Assert.Matches("<select class=\"select\" id=\"DepartmentId\" disabled[^>]*>\\s*<option selected>Arabic</option>", locked);
        var lockedAssignees = Options(locked, "AssignedToId");
        Assert.Contains(arabicTeacher, lockedAssignees);
        Assert.DoesNotContain(englishTeacher, lockedAssignees);
        Assert.DoesNotContain(former, lockedAssignees);
        Assert.DoesNotContain(manager, lockedAssignees);

        // ---------- The server rejects what the lists do not offer, whatever is posted.
        int before;
        await using (var db = fx.NewContext()) before = await db.ManagementTasks.CountAsync();
        var stamp = DbFixture.Unique();
        (string, string)[] Fields(string title, int? dept, int assignee) =>
        [
            ("DepartmentMode", "true"), ("DepartmentId", dept?.ToString() ?? ""), ("Title", title), ("Priority", "High"), ("Status", "ToDo"),
            ("AssignedToId", assignee.ToString()), ("DueDate", SchoolWeek.Iso(SchoolWeek.Today.AddDays(5))),
        ];
        var cross = await PostAsync(client, $"/management/tasks/create?departmentId={arabic}", $"/staff/departments/{arabic}", Fields("Cross " + stamp, arabic, englishTeacher));
        Assert.Equal(HttpStatusCode.OK, cross.StatusCode); // the form again, unsaved
        Assert.Contains("is not a member of the Arabic department", WebUtility.HtmlDecode(await cross.Content.ReadAsStringAsync()));
        var manual = await PostAsync(client, "/management/tasks/create", "/tasks", ("DepartmentId", arabic.ToString()), ("Title", "Manual " + stamp), ("Priority", "Low"),
            ("Status", "ToDo"), ("AssignedToId", manager.ToString())); // the department sent without the form's other fields
        Assert.Contains("is not a member of the Arabic department", WebUtility.HtmlDecode(await manual.Content.ReadAsStringAsync()));
        Assert.Contains("Choose a department from the list.", await (await PostAsync(client, "/management/tasks/create?by=department", "/staff/departments",
            Fields("Unknown " + stamp, 999999, arabicTeacher))).Content.ReadAsStringAsync());
        Assert.Contains(">Choose a department.</span>", await (await PostAsync(client, "/management/tasks/create?by=department", "/staff/departments",
            Fields("None " + stamp, null, arabicTeacher))).Content.ReadAsStringAsync());
        Assert.Contains("contract ended", await (await PostAsync(client, "/management/tasks/create?by=department", "/staff/departments",
            Fields("Former " + stamp, arabic, former))).Content.ReadAsStringAsync());
        Assert.Contains(">Enter the task title.</span>", await (await PostAsync(client, "/management/tasks/create?by=department", "/staff/departments",
            Fields("", arabic, arabicTeacher))).Content.ReadAsStringAsync());
        await using (var db = fx.NewContext()) Assert.Equal(before, await db.ManagementTasks.CountAsync());

        // ---------- Two valid tasks: one from the Departments page, one from the English department's page.
        var fromList = "From departments " + stamp;
        var created = await PostAsync(client, "/management/tasks/create?by=department", "/staff/departments", Fields(fromList, arabic, arabicTeacher));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/staff/departments", created.Headers.Location!.OriginalString); // back to where it was opened
        var fromPage = "From English page " + stamp;
        var created2 = await PostAsync(client, $"/management/tasks/create?departmentId={english}", $"/staff/departments/{english}",
            [.. Fields(fromPage, english, englishTeacher), ("DepartmentLocked", "true")]);
        Assert.Equal(HttpStatusCode.Redirect, created2.StatusCode);
        Assert.Equal($"/staff/departments/{english}", created2.Headers.Location!.OriginalString);

        await using (var db = fx.NewContext())
        {
            Assert.Equal(before + 2, await db.ManagementTasks.CountAsync());
            Assert.Equal(arabicTeacher, await db.ManagementTasks.Where(t => t.Title == fromList).Select(t => t.AssignedToId).SingleAsync());
            Assert.Equal(englishTeacher, await db.ManagementTasks.Where(t => t.Title == fromPage).Select(t => t.AssignedToId).SingleAsync());
            // The staff members' own task counts (new staff: these are their only tasks).
            var tasks = new ManagementTaskService(db, new User());
            Assert.Equal(1, (await tasks.CountsAsync(arabicTeacher)).Open);
            Assert.Equal(1, (await tasks.CountsAsync(englishTeacher)).Open);
            Assert.Equal(0, (await tasks.CountsAsync(arabicTeacher)).Overdue);
            Assert.Equal(0, (await tasks.CountsAsync(arabicTeacher)).Completed);
        }

        // Both are ordinary tasks on the global Tasks page, each once, under their department.
        var all = WebUtility.HtmlDecode(await client.GetStringAsync("/tasks?search=" + stamp));
        Assert.Single(Regex.Matches(all, Regex.Escape(fromList) + "</a>"));
        Assert.Single(Regex.Matches(all, Regex.Escape(fromPage) + "</a>"));
        var arabicTasks = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?search={stamp}&department={arabic}"));
        Assert.Contains(fromList, arabicTasks);
        Assert.DoesNotContain(fromPage, arabicTasks);

        // The ordinary Add Task form is unchanged: no department step, every active staff member.
        var plain = await client.GetStringAsync("/management/tasks/create");
        Assert.DoesNotContain("name=\"DepartmentMode\"", plain);
        Assert.Contains(englishTeacher, Options(plain, "AssignedToId"));
        Assert.Contains(manager, Options(plain, "AssignedToId"));
    }
}
