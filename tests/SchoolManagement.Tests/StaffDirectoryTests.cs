using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Staff → All School Staff: every staff record once, with roles, filters, counts and profile links.</summary>
[Collection("db")]
public class StaffDirectoryTests(DbFixture fx)
{
    private static readonly Regex Card = new("<article class=\"leader-card leader-card--\\w+ dir-card\" aria-labelledby=\"sd-(\\d+)\"");

    private static List<int> Cards(string html) => Card.Matches(html).Select(m => int.Parse(m.Groups[1].Value)).ToList();

    [Fact]
    public async Task The_directory_lists_every_staff_member_once_with_counts_from_the_database()
    {
        // A teacher, a librarian (support), a Management Team member, and a teacher who is also an HOD.
        var (teacher, teacherName) = await fx.NewTeacherAsync("Math Teacher");
        var (librarian, _) = await fx.NewTeacherAsync("Librarian");
        int manager, head;
        await using (var db = fx.NewContext())
        {
            manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Leader " + DbFixture.Unique(), ContactNumber = "0100 555 1234" })).EntityId!.Value;
        }
        (head, _) = await fx.NewTeacherAsync("Senior Art Teacher");
        int art;
        int? previousHead;
        await using (var db = fx.NewContext())
        {
            art = await db.Departments.Where(d => d.Name == "Art").Select(d => d.Id).SingleAsync();
            previousHead = await db.DepartmentHeads.Where(h => h.DepartmentId == art && h.IsCurrent).Select(h => (int?)h.TeacherId).FirstOrDefaultAsync();
            await db.Teachers.Where(t => t.Id == head).ExecuteUpdateAsync(s => s.SetProperty(t => t.DepartmentId, art));
            var assigned = await DbFixture.Departments(db).AssignHeadAsync(art, head, SchoolManagement.Web.Common.SchoolWeek.Today, null);
            Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(e => e.Value)));
        }
        try
        {

        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync("/staff/all");
        Assert.Contains("<a href=\"/staff\">Staff</a></li><li><span aria-current=\"page\">All School Staff</span>", page);
        var ids = Cards(page);
        await using (var db = fx.NewContext())
        {
            var all = await db.StaffMembers.Select(s => s.Id).ToListAsync();
            Assert.Equal(all.Count, ids.Count);                // everyone…
            Assert.Equal(ids.Count, ids.Distinct().Count());   // …once
            Assert.Equal(all.OrderBy(i => i), ids.OrderBy(i => i));
            var managers = await db.ManagementMembers.CountAsync();
            Assert.Contains($"<span class=\"summary-tile__value num\">{all.Count}</span>", page);
            Assert.Contains($">{managers}</span><span class=\"summary-tile__label\">", page);
        }

        // Profiles: the Management Team profile for managers, the staff profile for everyone else.
        Assert.Contains($"href=\"/management/members/{manager}\"", page);
        Assert.Contains($"href=\"/staff/{teacher}\"", page);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/staff/{teacher}")).StatusCode);

        // Filters.
        Assert.Equal([teacher], Cards(await client.GetStringAsync("/staff/all?search=" + Uri.EscapeDataString(teacherName))));
        var managementOnly = Cards(await client.GetStringAsync("/staff/all?category=management"));
        Assert.Contains(manager, managementOnly);
        Assert.DoesNotContain(teacher, managementOnly);
        var support = Cards(await client.GetStringAsync("/staff/all?category=support"));
        Assert.Contains(librarian, support);
        Assert.DoesNotContain(teacher, support);
        Assert.Contains(head, Cards(await client.GetStringAsync("/staff/all?role=hod")));
        Assert.DoesNotContain(teacher, Cards(await client.GetStringAsync("/staff/all?role=hod")));
        Assert.Contains(librarian, Cards(await client.GetStringAsync("/staff/all?jobTitle=Librarian")));
        var noDept = Cards(await client.GetStringAsync("/staff/all?department=none"));
        Assert.Contains(teacher, noDept);
        Assert.Contains(manager, noDept);
        Assert.DoesNotContain(head, noDept);

        // The HOD's card names the department it heads.
        var headPage = await client.GetStringAsync("/staff/all?role=hod");
        Assert.Contains("Head of Art", headPage);
        }
        finally
        {
            await using var db = fx.NewContext();
            await DbFixture.Departments(db).AssignHeadAsync(art, previousHead, SchoolManagement.Web.Common.SchoolWeek.Today, null); // as it was
        }
    }

    [Fact]
    public async Task All_staff_members_on_Academic_Departments_opens_the_directory_and_reads_nothing_else()
    {
        var client = fx.Factory.CreateClient();
        Assert.Contains("<a class=\"btn btn--secondary\" href=\"/staff/all\">", await client.GetStringAsync("/staff/departments"));
        Assert.Contains("href=\"/staff/all\"", await client.GetStringAsync("/staff"));

        await using var db = fx.NewContext();
        var before = (await db.StaffMembers.CountAsync(), await db.WorkloadAssignments.CountAsync(), await db.TimetableAssignments.CountAsync(), await db.DepartmentHeads.CountAsync());
        foreach (var url in new[] { "/staff/all", "/staff/all?category=academic&department=none", "/staff/all?role=SchoolDirector", "/staff/all?role=kg-homeroom", "/staff/all?jobTitle=none" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        var after = (await db.StaffMembers.CountAsync(), await db.WorkloadAssignments.CountAsync(), await db.TimetableAssignments.CountAsync(), await db.DepartmentHeads.CountAsync());
        Assert.Equal(before, after);
    }
}
