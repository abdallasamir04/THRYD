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
    private static readonly Regex Card = new("<article class=\"dir-row\" aria-labelledby=\"sd-(\\d+)\"");

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

    /// <summary>The ids of the rows inside one category section (dg-management, dg-academic, dg-support).</summary>
    private static List<int> Section(string html, string slug)
    {
        var start = html.IndexOf($"id=\"dg-{slug}\"", StringComparison.Ordinal);
        if (start < 0) return [];
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return Cards(html[start..end]);
    }

    [Fact]
    public async Task Staff_are_grouped_by_category_in_full_width_rows_with_a_category_bar()
    {
        var (teacher, teacherName) = await fx.NewTeacherAsync("Math Teacher");
        var (librarian, librarianName) = await fx.NewTeacherAsync("Librarian");
        int manager;
        var stamp = DbFixture.Unique();
        await using (var db = fx.NewContext())
            manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Grouped Leader " + stamp })).EntityId!.Value;
        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync("/staff/all");

        // The old card grid is gone; three sections in order, each with its header and count.
        Assert.DoesNotContain("dir-card", page);
        Assert.DoesNotContain("leader-grid", page);
        int At(string slug) => page.IndexOf($"id=\"dg-{slug}\"", StringComparison.Ordinal);
        Assert.True(At("management") > 0 && At("management") < At("academic") && At("academic") < At("support"));
        foreach (var title in new[] { ">Management Team</h2>", ">Academic Staff</h2>", ">Administrative &amp; Support Staff</h2>" }) Assert.Contains(title, page);

        await using (var db = fx.NewContext())
        {
            var managers = await db.ManagementMembers.Select(m => m.Id).ToListAsync();
            var management = Section(page, "management");
            var academic = Section(page, "academic");
            var support = Section(page, "support");
            // Every staff record exactly once across the sections; no Academic / Management mixing.
            Assert.Equal(managers.Order(), management.Order());
            Assert.Empty(academic.Intersect(managers));
            Assert.Empty(support.Intersect(managers));
            var all = management.Concat(academic).Concat(support).ToList();
            Assert.Equal(await db.StaffMembers.CountAsync(), all.Count);
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.Contains(teacher, academic);
            Assert.Contains(librarian, support);
            Assert.Contains($">{Web.Common.Ui.Plural(management.Count, "member", "members")}</span>", page);
            Assert.Contains($">{Web.Common.Ui.Plural(academic.Count, "member", "members")}</span>", page);

            // The category bar: All Staff and each category with its count.
            Assert.Matches($"aria-label=\"Staff category\">.*?All Staff <span class=\"count num\">{all.Count}</span>", Regex.Replace(page, "\\s+", " "));
            Assert.Contains($"Management <span class=\"count num\">{management.Count}</span>", page);
            Assert.Contains($"Academic <span class=\"count num\">{academic.Count}</span>", page);
        }

        // One row: name (profile link) on the left, then job title, department, contact and the existing actions.
        var row = page[page.IndexOf($"aria-labelledby=\"sd-{teacher}\"", StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</article>", StringComparison.Ordinal)];
        string[] order = [$"href=\"/staff/{teacher}\"", ">Job title<", ">Math Teacher<", ">Department<", ">Contact<", $"href=\"/staff/edit/{teacher}\""];
        var positions = order.Select(x => row.IndexOf(x, StringComparison.Ordinal)).ToList();
        Assert.True(positions.All(i => i > 0) && positions.SequenceEqual(positions.Order()), string.Join(",", positions));
        var managerRow = page[page.IndexOf($"aria-labelledby=\"sd-{manager}\"", StringComparison.Ordinal)..];
        Assert.Contains(">Position<", managerRow[..managerRow.IndexOf("</article>", StringComparison.Ordinal)]);
        Assert.Contains($"href=\"/management/members/{manager}/edit\" data-modal", managerRow);

        // A category keeps the search; empty sections are hidden; the bar counts follow the search.
        var searched = await client.GetStringAsync($"/staff/all?category=academic&search={Uri.EscapeDataString(teacherName)}");
        Assert.Equal([teacher], Cards(searched));
        Assert.Equal(-1, searched.IndexOf("id=\"dg-management\"", StringComparison.Ordinal));
        Assert.Equal(-1, searched.IndexOf("id=\"dg-support\"", StringComparison.Ordinal));
        Assert.Contains($"href=\"/staff/all?category=management&amp;search={Uri.EscapeDataString(teacherName)}\"", searched);
        Assert.Contains("Academic <span class=\"count num\">1</span>", searched);
        Assert.Contains("Management <span class=\"count num\">0</span>", searched);
        var none = await client.GetStringAsync($"/staff/all?category=management&search={Uri.EscapeDataString(librarianName)}");
        Assert.Empty(Cards(none));
        Assert.Contains("No staff match", none);
    }
}
