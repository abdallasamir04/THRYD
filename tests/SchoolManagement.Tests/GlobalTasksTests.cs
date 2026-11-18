using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Daily Operations → Tasks: one central page over the existing task table (the same tasks as Management Team → Tasks),
/// with school-wide totals, exclusive tabs, staff category / due-range filters, oldest-first sorting, and the existing
/// Add Task form offering every staff member (grouped by category) but never students or staff whose contract ended.
/// </summary>
[Collection("db")]
public sealed class GlobalTasksTests(DbFixture fx)
{
    private sealed class Runner : ICurrentUser
    {
        public string? Name => "Tasks tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static string Sidebar(string html) => html[..html.IndexOf("</aside>", StringComparison.Ordinal)];

    /// <summary>The number on a tab of the Tasks page ("Overdue 3").</summary>
    private static int TabCount(string html, string label)
    {
        var m = Regex.Match(html, $"class=\"task-tabs\".*?>{Regex.Escape(label)} <span class=\"count num\">(\\d+)</span>", RegexOptions.Singleline);
        Assert.True(m.Success, label);
        return int.Parse(m.Groups[1].Value);
    }

    [Fact]
    public async Task Tasks_is_one_sidebar_item_under_Daily_Operations_and_highlights_its_page()
    {
        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync("/tasks");
        var sidebar = Sidebar(page);
        Assert.Equal(1, Regex.Matches(sidebar, "href=\"/tasks\"").Count);
        Assert.Contains("href=\"/tasks\" aria-current=\"page\"", sidebar);
        Assert.Contains("#i-list-checks", sidebar);
        Assert.True(sidebar.IndexOf("href=\"/duties/audit\"", StringComparison.Ordinal) < sidebar.IndexOf("href=\"/tasks\"", StringComparison.Ordinal));
        Assert.True(sidebar.IndexOf("Daily Operations", StringComparison.Ordinal) < sidebar.IndexOf("href=\"/tasks\"", StringComparison.Ordinal));
        // Add Task opens the existing task form; Management Team → Tasks is still there.
        Assert.Contains("<a class=\"btn btn--primary\" href=\"/management/tasks/create\" data-modal>", page);
        Assert.Contains("href=\"/management/tasks\"", page);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/management/tasks")).StatusCode);
    }

    [Fact]
    public async Task Every_task_is_listed_with_exclusive_tabs_filters_and_sorting()
    {
        var stamp = DbFixture.Unique();
        var (teacher, _) = await fx.NewTeacherAsync();
        int manager;
        var ids = new List<int>();
        await using (var db = fx.NewContext())
            manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Task Manager " + stamp })).EntityId!.Value;
        var today = SchoolWeek.Today;
        string T(string name) => $"{name} {stamp}";
        try
        {
            await using (var db = fx.NewContext())
            {
                var tasks = new ManagementTaskService(db, new Runner());
                async Task<int> Add(string title, int who, DateOnly? due, ManagementTaskStatus status = ManagementTaskStatus.ToDo)
                {
                    var r = await tasks.CreateAsync(new TaskInput { Title = T(title), AssignedToId = who, DueDate = due, Status = status });
                    Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
                    return r.EntityId!.Value;
                }
                ids.Add(await Add("Alpha today", teacher, today));
                ids.Add(await Add("Bravo upcoming", manager, today.AddDays(1)));
                ids.Add(await Add("Charlie done", teacher, today.AddDays(3), ManagementTaskStatus.Completed));
                ids.Add(await Add("Delta overdue", teacher, today.AddDays(2)));
                ids.Add(await Add("Echo undated", manager, null));
                // A task that became overdue (a past due date cannot be chosen for an open task, so it is set directly).
                await db.ManagementTasks.Where(t => t.Id == ids[3]).ExecuteUpdateAsync(s => s.SetProperty(t => t.DueDate, today.AddDays(-2)));
            }

            var client = fx.Factory.CreateClient();
            var page = await client.GetStringAsync("/tasks");

            // Totals and tab counts come from the stored tasks, by the school's (Cairo) date and time.
            await using (var db = fx.NewContext())
            {
                var all = await db.ManagementTasks.AsNoTracking().Select(t => new { t.Status, t.DueDate, t.DueTime }).ToListAsync();
                var now = SchoolTime.Now;
                var (day, time) = (DateOnly.FromDateTime(now), TimeOnly.FromDateTime(now));
                bool Open(ManagementTaskStatus s) => s is not (ManagementTaskStatus.Completed or ManagementTaskStatus.Cancelled);
                var overdue = all.Count(t => TaskLabels.IsOverdue(t.Status, t.DueDate, t.DueTime, now));
                var dueToday = all.Count(t => Open(t.Status) && t.DueDate == day && !(t.DueTime is { } dt && dt < time));
                var upcoming = all.Count(t => Open(t.Status) && t.DueDate > day);
                var completed = all.Count(t => t.Status == ManagementTaskStatus.Completed);
                Assert.Equal((all.Count, overdue, dueToday, upcoming, completed),
                    (TabCount(page, "All Tasks"), TabCount(page, "Overdue"), TabCount(page, "Due Today"), TabCount(page, "Upcoming"), TabCount(page, "Completed")));
                Assert.Contains($"<span class=\"summary-tile__value num\">{all.Count}</span>", page);
            }

            // Each test task sits in exactly the tab it belongs to (and the undated open one only under All).
            var expected = new Dictionary<string, string[]>
            {
                ["overdue"] = ["Delta overdue"], ["today"] = ["Alpha today"], ["upcoming"] = ["Bravo upcoming"], ["completed"] = ["Charlie done"],
            };
            string[] names = ["Alpha today", "Bravo upcoming", "Charlie done", "Delta overdue", "Echo undated"];
            foreach (var (tab, inTab) in expected)
            {
                var list = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?tab={tab}&search={stamp}"));
                foreach (var n in names) Assert.Equal(inTab.Contains(n), list.Contains(T(n)));
                Assert.Equal(inTab.Length, TabCount(list, TaskTabs.All.Single(t => t.Key == tab).Label));
            }
            var everything = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?search={stamp}"));
            foreach (var n in names) Assert.Contains(T(n), everything);
            Assert.Equal(5, TabCount(everything, "All Tasks"));

            // Staff category: the Management Team member's tasks, or the teacher's.
            var mgmt = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?category=management&search={stamp}"));
            Assert.Equal([false, true, false, false, true], names.Select(n => mgmt.Contains(T(n))));
            var academic = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?category=academic&search={stamp}"));
            Assert.Equal([true, false, true, true, false], names.Select(n => academic.Contains(T(n))));

            // Due date range (inclusive), with its chip.
            var tomorrow = SchoolWeek.Iso(today.AddDays(1));
            var ranged = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?dueFrom={SchoolWeek.Iso(today)}&dueTo={tomorrow}&search={stamp}"));
            Assert.Equal([true, true, false, false, false], names.Select(n => ranged.Contains(T(n))));
            Assert.Contains("filter-chip\">Due ", ranged);

            // Oldest first and newest first.
            var oldest = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?sort=oldest&search={stamp}"));
            var at = names.Select(n => oldest.IndexOf(T(n), StringComparison.Ordinal)).ToList();
            Assert.Equal(at.Order(), at);
            var newest = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?sort=created&search={stamp}"));
            at = names.Select(n => newest.IndexOf(T(n), StringComparison.Ordinal)).ToList();
            Assert.Equal(at.OrderDescending(), at);

            // The same tasks on Management Team → Tasks; the cards show who created them and when.
            var management = WebUtility.HtmlDecode(await client.GetStringAsync($"/management/tasks?search={stamp}"));
            foreach (var n in names) Assert.Contains(T(n), management);
            Assert.Contains("Created ", management);
            Assert.Contains("· Tasks tester", management);

            // A task opens from the central page: its details lead back to Tasks.
            var details = await client.GetStringAsync($"/management/tasks/{ids[0]}");
            Assert.Contains("<li><a href=\"/tasks\">Tasks</a></li>", details);
            Assert.Contains("href=\"/tasks\" aria-current=\"page\"", Sidebar(details));
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.ManagementTaskHistory.Where(h => ids.Contains(h.TaskId)).ExecuteDeleteAsync();
            await db.ManagementTasks.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Add_Task_offers_every_current_staff_member_once_and_never_students()
    {
        var (teacher, teacherName) = await fx.NewTeacherAsync();
        var (support, supportName) = await fx.NewTeacherAsync("Librarian");
        var (left, leftName) = await fx.NewTeacherAsync();
        int manager, classId;
        var stamp = DbFixture.Unique();
        await using (var db = fx.NewContext())
        {
            manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Form Manager " + stamp })).EntityId!.Value;
            classId = await db.Classes.Select(c => c.Id).FirstAsync();
            await db.StaffMembers.Where(s => s.Id == left).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContractEndDate, SchoolWeek.Today.AddDays(-1)));
        }
        var (student, studentName) = await fx.NewStudentAsync(classId);
        try
        {
            var form = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/management/tasks/create"));
            // Grouped by staff category: Management Team, academic, administrative & support.
            var assigned = form[form.IndexOf("id=\"AssignedToId\"", StringComparison.Ordinal)..form.IndexOf("id=\"ReviewerId\"", StringComparison.Ordinal)];
            int Group(string label) => assigned.IndexOf($"<optgroup label=\"{label}\">", StringComparison.Ordinal);
            Assert.True(Group("Academic staff") > 0 && Group("Management Team") > Group("Academic staff") && Group("Administrative & support") > Group("Management Team"));
            Assert.True(assigned.IndexOf($">{teacherName}<", StringComparison.Ordinal) is var a && a > Group("Academic staff") && a < Group("Management Team"));
            Assert.True(assigned.IndexOf($"<option value=\"{manager}\"", StringComparison.Ordinal) > Group("Management Team"));
            Assert.True(assigned.IndexOf($">{supportName}<", StringComparison.Ordinal) > Group("Administrative & support"));

            // Every current staff member exactly once per picker; never a student; not someone whose contract ended.
            await using (var db = fx.NewContext())
            {
                var staff = await db.StaffMembers.Where(s => s.ContractEndDate == null || s.ContractEndDate >= SchoolWeek.Today).Select(s => s.Id).ToListAsync();
                foreach (var id in staff) Assert.Equal(1, Regex.Matches(assigned, $"<option value=\"{id}\"").Count);
                Assert.Equal(staff.Count, Regex.Matches(assigned, "<option value=\"\\d+\"").Count);
            }
            Assert.DoesNotContain(studentName, form);
            Assert.DoesNotContain(leftName, form);

            // The server refuses a new task for someone whose contract ended; current staff of any category are fine.
            await using (var db = fx.NewContext())
            {
                var tasks = new ManagementTaskService(db, new Runner());
                var refused = await tasks.CreateAsync(new TaskInput { Title = "Left " + stamp, AssignedToId = left });
                Assert.Contains(refused.Errors, e => e.Key == "AssignedToId" && e.Value.Contains("contract ended"));
                foreach (var who in new[] { teacher, support, manager })
                {
                    var ok = await tasks.CreateAsync(new TaskInput { Title = $"Anyone {who} {stamp}", AssignedToId = who });
                    Assert.True(ok.Succeeded);
                }
                Assert.Equal(3, await db.ManagementTasks.CountAsync(t => t.Title.EndsWith(stamp)));
            }
        }
        finally
        {
            await using var db = fx.NewContext();
            var mine = await db.ManagementTasks.Where(t => t.Title.EndsWith(stamp)).Select(t => t.Id).ToListAsync();
            await db.ManagementTaskHistory.Where(h => mine.Contains(h.TaskId)).ExecuteDeleteAsync();
            await db.ManagementTasks.Where(t => mine.Contains(t.Id)).ExecuteDeleteAsync();
            await db.Students.Where(s => s.Id == student).ExecuteDeleteAsync();
        }
    }
}
