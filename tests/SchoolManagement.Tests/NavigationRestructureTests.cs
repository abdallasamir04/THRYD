using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Navigation restructure: the Students landing page, Staff Duty Records inside Duty Audit, and Classes
/// organised into four stages (KG Stages, Elementary, Middle School, High School) with Add Class per stage.
/// </summary>
public partial class HttpTests
{
    private static string Sidebar(string html) => html[..html.IndexOf("</aside>", StringComparison.Ordinal)];

    private static string Code(string name) => $"<span class=\"class-card__code\">{name}</span>";

    // ---------- sidebar ----------

    [Fact]
    public async Task Sidebar_has_the_new_structure()
    {
        var sidebar = Sidebar(await fx.Factory.CreateClient().GetStringAsync("/"));
        string[] ordered = ["href=\"/staff\"", "href=\"/students/hub\"", "href=\"/classes\"", "href=\"/duties\""];
        var at = ordered.Select(h => sidebar.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.True(at.All(i => i > 0) && at.SequenceEqual(at.Order()), string.Join(",", at));
        // Duties and Duty Audit are one entry, Duties: its landing page has the two cards.
        Assert.DoesNotContain("href=\"/duties/audit\"", sidebar);
        Assert.Contains("<span>Duties</span>", sidebar);
        Assert.Contains("<span>Students</span>", sidebar);
        // P.E. opens from Duties and the PE department page only.
        foreach (var gone in new[] { "href=\"/pe\"", "<span>P.E.</span>", "href=\"/belongings\"", "href=\"/arrivals\"", "Staff Duty Records", "view=duties", "<span>Belongings</span>", "Student Attendance" })
            Assert.DoesNotContain(gone, sidebar);
        // Unrelated items are kept.
        foreach (var kept in new[] { "href=\"/import-export\"", "href=\"/settings\"", "School QA" })
            Assert.Contains(kept, sidebar);
    }

    // ---------- Students ----------

    [Fact]
    public async Task Students_landing_page_has_three_cards_to_the_existing_modules()
    {
        var client = fx.Factory.CreateClient();
        var hub = await client.GetStringAsync("/students/hub");
        Assert.Equal(3, Regex.Matches(hub, "class=\"hub-card ").Count);
        foreach (var (href, title) in new[] { ("/belongings", "Belongings"), ("/attendance", "Student Attendance"), ("/students/behaviour", "Behavioural / Discipline") })
        {
            Assert.Matches($"<a class=\"hub-card hub-card--\\w+\" href=\"{Regex.Escape(href)}\">", hub);
            Assert.Contains($"<span class=\"hub-card__title\">{title}</span>", hub);
        }
        Assert.Contains("href=\"/students/hub\" aria-current=\"page\"", Sidebar(hub));

        // Each card opens its module, with Students highlighted in the sidebar.
        foreach (var url in new[] { "/belongings", "/belongings/personal", "/belongings/academic", "/attendance", "/arrivals", "/attendance/sessions", "/attendance/alerts", "/attendance/holidays", "/students/behaviour", "/students" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("href=\"/students/hub\" aria-current=\"page\"", Sidebar(await response.Content.ReadAsStringAsync()));
        }
        // School Attendance: Daily Attendance + History & Reports; no recording-session fields (method, location, receiver).
        var arrivals = await client.GetStringAsync("/arrivals");
        foreach (var part in new[] { "Daily Attendance", "Attendance History &amp; Reports", "id=\"sa-class\"" }) Assert.Contains(part, arrivals);
        foreach (var gone in new[] { "session-receiver", "session-location", "session-method", "session-confirmed", "Save entries", "Received by", "Arrival location" })
            Assert.DoesNotContain(gone, arrivals);
        // Behavioural / Discipline: exactly two section cards (Incident, Discipline).
        var behaviour = await client.GetStringAsync("/students/behaviour");
        Assert.Equal(2, Regex.Matches(behaviour, "class=\"hub-card ").Count);
        Assert.Contains("href=\"/students/behaviour/incidents\"", behaviour);
        Assert.Contains("href=\"/students/behaviour/discipline\"", behaviour);
    }

    // ---------- Duty Audit ----------

    [Fact]
    public async Task Staff_duty_records_are_a_tab_of_Duty_Audit()
    {
        var (teacher, name) = await fx.NewTeacherAsync();
        var client = await fx.DutyAuditClientAsync();   // Duty Audit is open to authorized users only

        var audit = await client.GetStringAsync("/duties/audit");
        Assert.Contains("href=\"/duties/audit/staff-records\"", audit);
        // Two separate buttons (not one segmented control); the current one is marked.
        Assert.Equal(2, Regex.Matches(audit, "class=\"btn btn--secondary audit-tab\"").Count);
        Assert.DoesNotContain("segmented audit-tabs", audit);
        Assert.Contains("href=\"/duties/audit\" aria-current=\"page\">", audit[audit.IndexOf("class=\"audit-tabs\"", StringComparison.Ordinal)..]);
        Assert.Contains("data-download=\"/duties/audit/pdf\"", audit);   // existing audit unchanged

        var records = await client.GetStringAsync("/duties/audit/staff-records?search=" + Uri.EscapeDataString(name));
        Assert.Contains($"href=\"/staff/{teacher}\"", records);
        Assert.Contains("data-download=\"/staff/duty-records/pdf\"", records);
        Assert.Contains("href=\"/duties/audit/staff-records\" aria-current=\"page\"", records);
        Assert.Contains("href=\"/duties\" aria-current=\"page\"", Sidebar(records));

        // AJAX search returns just the table.
        var ajax = new HttpRequestMessage(HttpMethod.Get, "/duties/audit/staff-records?search=" + Uri.EscapeDataString(name));
        ajax.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var table = await (await client.SendAsync(ajax)).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"/staff/{teacher}\"", table);
        Assert.DoesNotContain("<aside", table);

        // The old address redirects to the tab and keeps the search.
        var noRedirect = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var old = await noRedirect.GetAsync("/staff?view=duties&search=abc");
        Assert.Equal(HttpStatusCode.Redirect, old.StatusCode);
        Assert.Equal("/duties/audit/staff-records?search=abc", old.Headers.Location!.OriginalString);

        // The staff profile is still reached from the records, and is highlighted under Duty Audit.
        var profile = await client.GetStringAsync($"/staff/{teacher}");
        Assert.Contains("href=\"/duties\" aria-current=\"page\"", Sidebar(profile));
    }

    // ---------- Classes: stage rules ----------

    [Theory]
    [InlineData("PreK", "kg", "prek")]
    [InlineData("Pre-K", "kg", "prek")]
    [InlineData("KG1A", "kg", "kg1")]
    [InlineData("KG2B", "kg", "kg2")]
    [InlineData("G1A", "elementary", "lower")]
    [InlineData("G3B", "elementary", "lower")]
    [InlineData("G4A", "elementary", "upper")]
    [InlineData("G6B", "elementary", "upper")]
    [InlineData("G7A", "middle", "middle")]
    [InlineData("G9B", "middle", "middle")]
    [InlineData("G10A", "high", "high")]
    [InlineData("G10B", "high", "high")]
    [InlineData("G11A", "high", "high")]   // High School is open-ended: later grades join it
    public void Each_grade_belongs_to_exactly_one_stage(string name, string stage, string group)
    {
        var found = ClassStages.Locate(name);
        Assert.NotNull(found);
        Assert.Equal((stage, group), (found!.Value.Stage.Slug, found.Value.Group.Slug));
        // Exactly one group contains the grade.
        Assert.Single(ClassStages.All.SelectMany(s => s.Groups), g => g.Contains(ClassStages.GradeOf(name)!.Value));
    }

    [Fact]
    public void Stage_rules_match_the_requested_distribution()
    {
        Assert.Equal(["kg", "elementary", "middle", "high"], ClassStages.All.Select(s => s.Slug));
        Assert.Null(ClassStages.Locate("KG3A"));
        Assert.Null(ClassStages.Locate("Tabc12345"));
        // Every default class has a stage; G10 only in High School, never in Middle School.
        Assert.All(DefaultClasses.Names, n => Assert.NotNull(ClassStages.Locate(n)));
        Assert.DoesNotContain(ClassStages.Find("middle")!.Groups, g => g.Contains(new ClassGrade("G", 10)));
        Assert.Equal(["G10A", "G10B"], DefaultClasses.Names.Where(n => ClassStages.Locate(n)?.Stage.Slug == "high"));

        // Next section of a grade.
        Assert.Equal("C", ClassNumbering.NextSection(["KG1A", "KG1B", "KG2A"], "KG", 1));
        Assert.Equal("A", ClassNumbering.NextSection(["KG1A"], "G", 1));
        Assert.Equal("AA", ClassNumbering.NextSection(["G5Z"], "G", 5));
        Assert.Equal("BA", ClassNumbering.NextSection(["G5AZ"], "G", 5));
        Assert.Null(ClassNumbering.NextSection(["G5ZZZ"], "G", 5));

        // Add Class grades: bounded groups offer their range; High School offers its grades plus the next one.
        Assert.Equal([1, 2, 3], ClassStages.AddableGrades(ClassStages.FindGroup("lower")!.Value.Group, []));
        Assert.Equal([10, 11], ClassStages.AddableGrades(ClassStages.FindGroup("high")!.Value.Group, ["G10A", "G7A"]));
        // Pre-K has one "grade" (Pre-K itself); its classes are PreK + section (PreKB).
        Assert.Equal([0], ClassStages.AddableGrades(ClassStages.FindGroup("prek")!.Value.Group, ["PreK"]));
    }

    // ---------- Classes: pages ----------

    [Fact]
    public async Task Classes_page_shows_four_stage_cards_and_each_stage_its_own_classes()
    {
        var client = fx.Factory.CreateClient();
        var landing = await client.GetStringAsync("/classes");
        Assert.Equal(4, Regex.Matches(landing, "class=\"hub-card ").Count);
        foreach (var (slug, title) in new[] { ("kg", "KG Stages"), ("elementary", "Elementary"), ("middle", "Middle School"), ("high", "High School") })
        {
            Assert.Matches($"<a class=\"hub-card hub-card--\\w+\" href=\"/classes/stages/{slug}\">", landing);
            Assert.Contains($"<span class=\"hub-card__title\">{title}</span>", landing);
        }

        var kg = await client.GetStringAsync("/classes/stages/kg");
        foreach (var block in new[] { "Pre-K", "KG1", "KG2" }) Assert.Contains($"<a href=\"/classes/stages/kg/{block.ToLowerInvariant().Replace("-", "")}\">{block}</a>", kg);
        foreach (var n in new[] { "PreK", "KG1A", "KG1B", "KG2A", "KG2B" }) Assert.Contains(Code(n), kg);

        var elementary = await client.GetStringAsync("/classes/stages/elementary");
        Assert.Equal(2, Regex.Matches(elementary, "class=\"hub-card ").Count);
        Assert.Contains("href=\"/classes/stages/elementary/lower\"", elementary);
        Assert.Contains("href=\"/classes/stages/elementary/upper\"", elementary);

        var pages = new Dictionary<string, string[]>
        {
            ["/classes/stages/elementary/lower"] = ["G1A", "G1B", "G2A", "G2B", "G3A", "G3B"],
            ["/classes/stages/elementary/upper"] = ["G4A", "G4B", "G5A", "G5B", "G6A", "G6B"],
            ["/classes/stages/middle"] = ["G7A", "G7B", "G8A", "G8B", "G9A", "G9B"],
            ["/classes/stages/high"] = ["G10A", "G10B"],
        };
        var defaults = DefaultClasses.Names;
        foreach (var (url, expected) in pages)
        {
            var html = await client.GetStringAsync(url);
            foreach (var n in expected) Assert.Contains(Code(n), html);
            foreach (var n in defaults.Except(expected)) Assert.DoesNotContain(Code(n), html);
        }
        // G10 appears only under High School.
        foreach (var url in new[] { "/classes/stages/kg", "/classes/stages/elementary/lower", "/classes/stages/elementary/upper", "/classes/stages/middle" })
            Assert.DoesNotContain(Code("G10A"), await client.GetStringAsync(url));

        // Group pages under single-group stages redirect to the stage; unknown stages are 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/classes/stages/college")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/classes/stages/elementary/middle")).StatusCode);

        // The full list moved to /classes/all; old list links redirect there with their filters.
        Assert.Contains("data-download=\"/classes/pdf\"", await client.GetStringAsync("/classes/all"));
        var noRedirect = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var old = await noRedirect.GetAsync("/classes?status=inactive&view=table");
        Assert.Equal("/classes/all?status=inactive&view=table", old.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Add_class_from_a_stage_uses_the_next_section_and_keeps_existing_classes()
    {
        var client = fx.Factory.CreateClient();
        await using var db = fx.NewContext();
        var before = await db.Classes.AsNoTracking().Select(c => new { c.Id, c.Name, c.Capacity, Students = c.Students.Count }).ToListAsync();
        var names = before.Select(c => c.Name).ToList();
        var expected = $"KG1{ClassNumbering.NextSection(names, "KG", 1)}";

        // The form opens preselected for the group, offering only its grades.
        var form = await client.GetStringAsync("/classes/create?group=kg1");
        Assert.Contains("name=\"Group\" value=\"kg1\"", form);
        Assert.Contains($"next class {expected}", form);
        Assert.DoesNotContain("KG2", Regex.Match(form, "<select[^>]*GradeNumber[\\s\\S]*?</select>").Value);
        var token = TokenPattern().Match(form).Groups[1].Value;

        var ok = await PostAsync(client, "/classes/create", token, new()
        {
            ["Group"] = "kg1", ["GradeNumber"] = "1", ["GradeLevel"] = "G", ["Capacity"] = "18", ["IsActive"] = "true",
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        await using (var check = fx.NewContext())
        {
            var created = await check.Classes.AsNoTracking().SingleAsync(c => c.Name == expected);
            Assert.Equal(18, created.Capacity);
            Assert.Equal("kg1", ClassStages.Locate(created.Name)!.Value.Group.Slug);   // the tampered GradeLevel was ignored
        }
        Assert.Contains(Code(expected), await client.GetStringAsync("/classes/stages/kg/kg1"));

        // Grades outside the group and invalid capacity are refused.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/classes/create", token, new()
            { ["Group"] = "middle", ["GradeNumber"] = "10", ["Capacity"] = "25", ["IsActive"] = "true" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/classes/create", token, new()
            { ["Group"] = "upper", ["GradeNumber"] = "4", ["Capacity"] = "0", ["IsActive"] = "true" })).StatusCode);
        // The form has no Section field; a section posted anyway ("a" — G1A exists) is ignored: the next letter is used.
        var nextG1 = $"G1{ClassNumbering.NextSection(await db.Classes.AsNoTracking().Select(c => c.Name).ToListAsync(), "G", 1)}";
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/classes/create", token, new()
            { ["Group"] = "lower", ["GradeNumber"] = "1", ["Section"] = "a", ["Capacity"] = "25", ["IsActive"] = "true" })).StatusCode);
        await using (var check = fx.NewContext())
        {
            Assert.Equal(1, await check.Classes.CountAsync(c => c.Name == "G1A"));        // never a second G1A
            Assert.Equal(1, await check.Classes.CountAsync(c => c.Name == nextG1));
            await check.Classes.Where(c => c.Name == nextG1).ExecuteDeleteAsync();         // other tests check the class order
        }
        // Duplicate names stay impossible in the service too (a bare "PreK" is Pre-K section A).
        await using (var direct = fx.NewContext())
        {
            var dup = await DbFixture.Classes(direct).CreateInGradeAsync("G", 1, "A", 25, true);
            Assert.Contains("A class named \"G1A\" already exists.", dup.Errors.Select(e => e.Value));
            Assert.False((await DbFixture.Classes(direct).CreateInGradeAsync("PREK", 0, "A", 25, true)).Succeeded);
        }

        // High School can start a new grade; it appears under High School only.
        var high = await client.GetStringAsync("/classes/create?group=high");
        var nextGrade = ClassStages.AddableGrades(ClassStages.FindGroup("high")!.Value.Group, await db.Classes.AsNoTracking().Select(c => c.Name).ToListAsync()).Max();
        Assert.Contains($"G{nextGrade} (new grade)", high);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/classes/create", token, new()
            { ["Group"] = "high", ["GradeNumber"] = nextGrade.ToString(), ["Capacity"] = "30", ["IsActive"] = "true" })).StatusCode);
        Assert.Contains(Code($"G{nextGrade}A"), await client.GetStringAsync("/classes/stages/high"));
        Assert.DoesNotContain(Code($"G{nextGrade}A"), await client.GetStringAsync("/classes/stages/middle"));

        // Exactly two classes were added; every existing class kept its Id, name, capacity and students.
        await using var after = fx.NewContext();
        var now = await after.Classes.AsNoTracking().Select(c => new { c.Id, c.Name, c.Capacity, Students = c.Students.Count }).ToListAsync();
        Assert.Equal(before.Count + 2, now.Count);
        Assert.All(before, b => Assert.Contains(b, now));
    }
}
