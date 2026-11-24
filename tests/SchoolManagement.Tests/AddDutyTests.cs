using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Duties page: Add Duty (a new Main Duty from scratch, with its schedule and Sub-Duties) in place of the top Morning Duty
/// button, no introduction or empty instruction panel, and the original duties unchanged. Every duty a test adds is removed
/// again (records, Sub-Duties, schedules) and the duty catalog reloaded, so other tests keep seeing the original three.
/// </summary>
[Collection("db")]
public class AddDutyTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private HttpClient SuperAdmin() => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string formUrl, string action, IEnumerable<(string Key, string Value)> fields)
    {
        var token = Token.Match(await client.GetStringAsync(formUrl)).Groups[1].Value;
        return await client.PostAsync(action, new FormUrlEncodedContent([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]));
    }

    private static IEnumerable<(string, string)> Form(string name, string start, string end, params (string Name, string Start, string End, bool Required)[] subs)
    {
        yield return ("Name", name);
        yield return ("Summary", "Supervision of the canteen queues.");
        yield return ("Icon", "users");
        yield return ("StartTime", start);
        yield return ("EndTime", end);
        yield return ("EffectiveFrom", SchoolWeek.Iso(SchoolWeek.Today));
        yield return ("AllDays", "true");
        yield return ("AllDays", "false");
        for (var i = 0; i < subs.Length; i++)
        {
            yield return ("SubDuties.Index", $"k{i}");
            yield return ($"SubDuties[k{i}].Name", subs[i].Name);
            yield return ($"SubDuties[k{i}].StartTime", subs[i].Start);
            yield return ($"SubDuties[k{i}].EndTime", subs[i].End);
            if (subs[i].Required) yield return ($"SubDuties[k{i}].Required", "true");
            yield return ($"SubDuties[k{i}].Required", "false");
        }
    }

    /// <summary>Removes an added Main Duty and everything made for it, then reloads the catalog.</summary>
    private async Task RemoveAsync(string name)
    {
        await using var db = fx.NewContext();
        var main = await db.DutyMainDuties.FirstOrDefaultAsync(m => m.Name == name);
        if (main is not null)
        {
            var type = (DutyType)main.Id;
            var sections = await db.DutySections.Where(s => s.DutyType == type).Select(s => s.Id).ToListAsync();
            var components = sections.Select(s => (DutyComponent)s).ToList();
            foreach (var id in await db.DutyAssignments.Where(d => components.Contains(d.Component)).Select(d => d.Id).ToListAsync())
                await DbFixture.Duties(db).DeleteAsync(id);
            await db.DutySectionSchedules.Where(s => sections.Contains(s.DutySectionId)).ExecuteDeleteAsync();
            await db.DutySections.Where(s => sections.Contains(s.Id)).ExecuteDeleteAsync();
            await db.DutyMainSchedules.Where(m => m.DutyType == type).ExecuteDeleteAsync();
            await db.DutyMainDuties.Where(m => m.Id == main.Id).ExecuteDeleteAsync();
        }
        await DutyCatalog.LoadAsync(db);
    }

    private async Task<string> OriginalDutiesAsync()
    {
        await using var db = fx.NewContext();
        var mains = await db.DutyMainDuties.Where(m => m.Id <= 3).OrderBy(m => m.Id).Select(m => m.Id + m.Name + m.Slug).ToListAsync();
        var schedules = await db.DutyMainSchedules.Where(m => (int)m.DutyType <= 3).CountAsync();
        var sections = await db.DutySections.Where(s => (int)s.DutyType <= 3).OrderBy(s => s.Id).Select(s => s.Id + s.Name + s.Slug + s.DutyType).ToListAsync();
        var versions = await db.DutySectionSchedules.CountAsync();
        var points = await db.DutyAssignments.CountAsync();
        return string.Join("|", mains) + "#" + schedules + "#" + string.Join("|", sections) + "#" + versions + "#" + points;
    }

    [Fact]
    public async Task The_Duties_page_has_Add_Duty_and_Duty_Audit_no_introduction_and_no_empty_panel()
    {
        var html = WebUtility.HtmlDecode(await SuperAdmin().GetStringAsync("/duties/board"));
        var head = html[html.IndexOf("<div class=\"page-head\">", StringComparison.Ordinal)..html.IndexOf("id=\"duties-section\"", StringComparison.Ordinal)];
        Assert.Contains("<h1 class=\"page-title\">Duties</h1>", head);
        Assert.DoesNotContain("page-sub", head);                                                   // no introduction text
        Assert.Matches("<a class=\"btn btn--primary\" href=\"/duties/main/new\" data-modal><svg[^>]*><use href=\"#i-plus\"></use></svg>Add Duty</a>", head);
        Assert.Contains("href=\"/duties/audit\">", head);
        Assert.DoesNotContain("Morning Duty", head);                                               // the old top button
        Assert.DoesNotContain("Every duty with its Sub-Duties, staff and coverage", html);
        Assert.DoesNotContain("ops-section__title", html);                                        // no second "Duties" heading
        Assert.DoesNotContain("Select a duty above", html);                                       // no empty instruction panel
        Assert.DoesNotContain("duty-details__hint", html);
        Assert.Contains("class=\"ses-daynav\"", html);                                            // the date bar…
        Assert.Contains("href=\"/duties/departments?date=", html);                                // …with By department
        foreach (var t in new[] { "Morning Duty", "Midday Duty", "Dismissal Duty", "Activities" }) Assert.Contains($">{t}</a></h2>", html);
        // The date bar comes straight after the header.
        Assert.True(html.IndexOf("class=\"ses-daynav\"", StringComparison.Ordinal) < html.IndexOf("<article class=\"dmain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_duty_is_created_with_its_schedule_and_Sub_Duties_and_works_like_the_others()
    {
        var name = "Canteen Duty " + DbFixture.Unique();
        var before = await OriginalDutiesAsync();
        try
        {
            var client = SuperAdmin();
            Assert.Contains("name=\"SubDuties.Index\"", await client.GetStringAsync("/duties/main/new"));
            var created = await PostAsync(client, "/duties/main/new", "/duties/main/new",
                Form(name, "11:00", "12:30", ("Queue A", "11:00", "11:45", true), ("Queue B", "", "", false), ("", "", "", true)));
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

            DutyMainDuty main;
            List<DutySection> sections;
            await using (var db = fx.NewContext())
            {
                main = await db.DutyMainDuties.SingleAsync(m => m.Name == name);
                Assert.True(main.Id > 100);
                Assert.Equal(name.ToLowerInvariant().Replace(' ', '-'), main.Slug);
                Assert.Equal(("Supervision of the canteen queues.", "users"), (main.Summary, main.Icon));
                Assert.Equal($"/duties/board?duty={main.Slug}#duty-details", created.Headers.Location!.OriginalString);
                var schedule = await db.DutyMainSchedules.SingleAsync(m => m.DutyType == (DutyType)main.Id);
                Assert.Equal((new TimeOnly(11, 0), new TimeOnly(12, 30), 0, SchoolWeek.Today), (schedule.StartTime, schedule.EndTime, schedule.DayMask, schedule.EffectiveFrom));
                sections = await db.DutySections.Include(s => s.Schedules).Where(s => s.DutyType == (DutyType)main.Id).OrderBy(s => s.SortOrder).ToListAsync();
                Assert.Equal(["Queue A", "Queue B"], sections.Select(s => s.Name));                   // the empty row is ignored
                var a = Assert.Single(sections[0].Schedules);
                Assert.Equal((new TimeOnly(11, 0), new TimeOnly(11, 45), true), (a.StartTime, a.EndTime, a.IsRequired));
                var b = Assert.Single(sections[1].Schedules);
                Assert.Equal((new TimeOnly(11, 0), new TimeOnly(12, 30), false), (b.StartTime, b.EndTime, b.IsRequired)); // the duty's times
            }

            // It appears on the Duties page beside the original duties, and opens and is managed like them.
            var board = WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/board?duty={main.Slug}"));
            Assert.Contains($">{name}</a></h2>", board);
            Assert.Contains($"Selected duty · {name}", board);
            Assert.Contains("Queue A", board);
            foreach (var t in new[] { "Morning Duty", "Midday Duty", "Dismissal Duty", "Activities" }) Assert.Contains($">{t}</a></h2>", board);
            foreach (var url in new[] { $"/duties/{main.Slug}/schedule", $"/duties/{main.Slug}/{sections[0].Slug}", $"/duties/{main.Slug}/report.pdf",
                         $"/duties/create?component={sections[0].Slug}", $"/duties/sections/new?type={main.Slug}" })
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);

            // A coverage point (duty record with its staff) under the new Sub-Duty, through the existing workflow.
            var (staff, staffName) = await fx.NewTeacherAsync();
            await using (var db = fx.NewContext())
            {
                var point = await DbFixture.Duties(db).CreateAsync(new DutyInput((DutyComponent)sections[0].Id, SchoolWeek.Today, "Queue A gate",
                    new TimeOnly(11, 0), new TimeOnly(11, 45), "Canteen", staff, AttendanceStatus.NotRecorded, null, null, []));
                Assert.True(point.Succeeded, string.Join(" ", point.Errors.Select(e => e.Value)));
            }
            var sub = WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/{main.Slug}/{sections[0].Slug}"));
            Assert.Contains("Queue A gate", sub);
            Assert.Contains(staffName, sub);
            // A Sub-Duty added later through the existing schedule page.
            var added = await PostAsync(client, $"/duties/sections/new?type={main.Slug}", "/duties/sections/new",
                [("Duty", main.Slug), ("Name", "Queue C"), ("StartTime", "12:00"), ("EndTime", "12:30"), ("EffectiveFrom", SchoolWeek.Iso(SchoolWeek.Today)),
                 ("AllDays", "true"), ("AllDays", "false"), ("Required", "true"), ("Required", "false")]);
            Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
            Assert.Contains("Queue C", WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/{main.Slug}/schedule")));
        }
        finally
        {
            await RemoveAsync(name);
        }
        // The original duties, their schedules, Sub-Duties and records are untouched.
        Assert.Equal(before, await OriginalDutiesAsync());
    }

    [Fact]
    public async Task Invalid_or_duplicate_duties_are_refused_with_their_reasons_and_nothing_is_saved()
    {
        var client = SuperAdmin();
        async Task<string> Refused(IEnumerable<(string, string)> form)
        {
            var r = await PostAsync(client, "/duties/main/new", "/duties/main/new", form);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);                                      // the form again, not a redirect
            return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        }
        await using var db = fx.NewContext();
        var count = await db.DutyMainDuties.CountAsync();
        Assert.Contains("Name the duty.", await Refused(Form("", "10:00", "11:00")));
        Assert.Contains("The end time must be after the start time.", await Refused(Form("Late " + DbFixture.Unique(), "11:00", "10:00")));
        Assert.Contains("must fall within the duty's time", await Refused(Form("Gate " + DbFixture.Unique(), "10:00", "11:00", ("Outside", "09:00", "10:30", true))));
        Assert.Contains("is listed twice", await Refused(Form("Twice " + DbFixture.Unique(), "10:00", "11:00", ("Same", "", "", true), ("same", "", "", true))));
        Assert.Contains("A duty with this name already exists.", await Refused(Form("morning duty", "10:00", "11:00")));   // any case
        Assert.Equal(count, await db.DutyMainDuties.CountAsync());
    }

    [Fact]
    public async Task A_duty_named_like_a_fixed_page_gets_its_own_address()
    {
        var name = "Audit " + DbFixture.Unique();
        try
        {
            var client = SuperAdmin();
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "/duties/main/new", "/duties/main/new", Form("Board", "10:00", "11:00"))).StatusCode);
            await using var db = fx.NewContext();
            Assert.Equal("board-2", await db.DutyMainDuties.Where(m => m.Name == "Board").Select(m => m.Slug).SingleAsync());
            Assert.Contains("<h1 class=\"page-title\">Duties</h1>", await client.GetStringAsync("/duties/board"));      // the page still answers
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/duties/board-2/schedule")).StatusCode);
        }
        finally
        {
            await RemoveAsync("Board");
            await RemoveAsync(name);
        }
    }

    [Fact]
    public async Task Only_the_Super_Admin_can_add_a_duty()
    {
        var (staffId, _) = await fx.NewTeacherAsync();
        var staff = fx.ClientAs(fx.Factory, staffId, follow: false);
        Assert.DoesNotContain("/duties/main/new", await staff.GetStringAsync("/duties/board"));
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/duties/main/new")).StatusCode);
        var token = Token.Match(await staff.GetStringAsync("/duties/sections/new?type=morning")).Groups[1].Value;
        var name = "Sneaky " + DbFixture.Unique();
        var post = await staff.PostAsync("/duties/main/new", new FormUrlEncodedContent([new("__RequestVerificationToken", token), .. Form(name, "10:00", "11:00").Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))]));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        await using var db = fx.NewContext();
        Assert.False(await db.DutyMainDuties.AnyAsync(m => m.Name == name));
        // The service refuses too (defence in depth): a caller that is not the Super Admin.
        var refused = await DbFixture.DutyBoard(db).CreateMainDutyAsync(new NewMainDutyInput(name, null, "users", SchoolWeek.Today, new TimeOnly(10, 0), new TimeOnly(11, 0), 0, []));
        Assert.False(refused.Succeeded);
        Assert.False(await db.DutyMainDuties.AnyAsync(m => m.Name == name));
    }
}
