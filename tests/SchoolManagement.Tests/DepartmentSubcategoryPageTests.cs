using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SchoolManagement.Tests;

/// <summary>
/// Academic Departments → Department → Subcategory → Staff: a department with subcategories opens on one card per
/// subcategory (with its staff count), each card opens a page with only that subcategory's staff (search and subject
/// filter stay inside it), staff without a subcategory have their own card, and departments without subcategories keep
/// showing their staff directly.
/// </summary>
[Collection("db")]
public class DepartmentSubcategoryPageTests(DbFixture fx)
{
    private const int Religion = 7, Math = 3;

    private async Task<string> TeacherAsync(int department, string? sub, string? subject)
    {
        await using var db = fx.NewContext();
        var name = "Dept " + DbFixture.Unique();
        var r = await DbFixture.Teachers(db).CreateAsync(name, "Teacher", null, department, sub, subject);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        return name;
    }

    private HttpClient Client() => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task A_department_with_subcategories_opens_on_their_cards_and_each_card_on_its_own_staff()
    {
        var quran = await TeacherAsync(Religion, "Islamic", "Quran");
        var christian = await TeacherAsync(Religion, "Christian", "Christian Religion");
        // Staff without a subcategory (set before the department had subcategories): the form requires one now.
        var none = await TeacherAsync(Religion, "Islamic", "Religion");
        await using (var db = fx.NewContext())
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteUpdateAsync(
                db.StaffMembers.Where(t => t.FullName == none), x => x.SetProperty(t => t.Subcategory, (string?)null).SetProperty(t => t.Subject, (string?)null));
        var client = Client();

        // The department page: one card per subcategory, a card for staff without one, and no mixed member list.
        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/staff/departments/{Religion}"));
        Assert.Contains("<h1 class=\"page-title\">Religion</h1>", page);
        foreach (var (key, name) in new[] { ("Islamic", "Islamic"), ("Christian", "Christian"), ("none", "No religion type") })
            Assert.Matches($"<a class=\"stretched\" href=\"/staff/departments/{Religion}\\?subcategory={key}\" dir=\"auto\">{name}</a>", page);
        Assert.DoesNotContain("class=\"member-grid\"", page);
        Assert.DoesNotContain(quran, page);
        Assert.Contains($"href=\"/staff/departments/{Religion}?view=all\"", page);
        Assert.Contains("Head of Department", page);                                   // the HOD card stays

        // A subcategory's own page: breadcrumb down to it, only its staff, the subject filter limited to it.
        var islamic = WebUtility.HtmlDecode(await client.GetStringAsync($"/staff/departments/{Religion}?subcategory=Islamic"));
        Assert.Contains($"<li><a href=\"/staff/departments\">Departments</a></li>", islamic);
        Assert.Matches($"<li><a href=\"/staff/departments/{Religion}\">Religion</a></li><li>Islamic</li>", islamic);
        Assert.Contains("<h1 class=\"page-title\" dir=\"auto\">Islamic</h1>", islamic);
        Assert.Contains("Religion type · Religion", islamic);                                   // the department's own label for its subcategories
        Assert.Contains(quran, islamic);
        Assert.DoesNotContain(christian, islamic);
        Assert.DoesNotContain(none, islamic);
        Assert.Contains("<option value=\"Islamic|Quran\"", islamic);
        Assert.DoesNotContain("value=\"Christian\"", islamic[islamic.IndexOf("role=\"search\"", StringComparison.Ordinal)..]);
        Assert.Contains($"href=\"/staff/departments/{Religion}\">", islamic);              // back to the department

        // Search inside the subcategory (the list's AJAX reload keeps it).
        var search = new HttpRequestMessage(HttpMethod.Get, $"/staff/departments/{Religion}?subcategory=Islamic&search={Uri.EscapeDataString(quran)}");
        search.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var found = WebUtility.HtmlDecode(await (await client.SendAsync(search)).Content.ReadAsStringAsync());
        Assert.Contains(quran, found);
        // Clearing the search stays in the subcategory.
        Assert.Contains($"href=\"/staff/departments/{Religion}?subcategory=Islamic\" data-list-link aria-label=\"Clear search\"", found);

        // Staff without a subcategory, and everyone together.
        var noneHtml = WebUtility.HtmlDecode(await client.GetStringAsync($"/staff/departments/{Religion}?subcategory=none"));
        Assert.Contains(none, noneHtml);
        Assert.DoesNotContain(quran, noneHtml);
        var all = WebUtility.HtmlDecode(await client.GetStringAsync($"/staff/departments/{Religion}?view=all"));
        Assert.Contains("class=\"member-grid\"", all);
        foreach (var n in new[] { quran, christian, none }) Assert.Contains(n, all);

        // An unknown subcategory leads back to the department.
        var unknown = await client.GetAsync($"/staff/departments/{Religion}?subcategory=Nothing");
        Assert.Equal(HttpStatusCode.Redirect, unknown.StatusCode);
        Assert.Equal($"/staff/departments/{Religion}", unknown.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_department_without_subcategories_still_shows_its_staff_directly()
    {
        var name = await TeacherAsync(Math, null, null);
        var page = WebUtility.HtmlDecode(await Client().GetStringAsync($"/staff/departments/{Math}"));
        Assert.Contains("class=\"member-grid\"", page);
        Assert.Contains(name, page);
        Assert.DoesNotContain("class=\"sub-cards\"", page);
        Assert.Equal(0, Regex.Matches(page, "\\?subcategory=").Count);
    }
}
