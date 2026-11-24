using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using SchoolManagement.Web.Common;

namespace SchoolManagement.Tests;

/// <summary>
/// Duties: the landing page (/duties) shows two cards — Duties (the duty board, /duties/board: every Main Duty as a card,
/// the selected one's full board below the cards) and Duty Audit (/duties/audit: Duty Audit and Staff Duty Records, which
/// keep their tabs, filters, tables, report and AJAX lists; authorized users only). The former addresses lead to the
/// right card of the board; the duty management pages are unchanged and open from the board.
/// </summary>
[Collection("db")]
public class DutiesAuditPageTests(DbFixture fx)
{
    private HttpClient Client(bool follow = true) => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = follow });

    [Theory]
    [InlineData("/duties?date=2026-11-10", "/duties/board?date=2026-11-10")]
    [InlineData("/duties?type=morning", "/duties/board?duty=morning#duty-details")]
    [InlineData("/duties?type=Midday&date=2026-11-10", "/duties/board?duty=midday&date=2026-11-10#duty-details")]
    [InlineData("/duties?type=unknown", "/duties/board")]
    [InlineData("/duties/overview?duty=dismissal", "/duties/board?duty=dismissal#duty-details")]
    [InlineData("/duties/audit?duty=morning&date=2026-11-10", "/duties/board?duty=morning&date=2026-11-10#duty-details")]
    public async Task The_former_Duties_addresses_lead_to_the_right_card(string from, string to)
    {
        var r = await Client(follow: false).GetAsync(from);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(to, r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_board_has_the_duty_cards_and_the_audit_has_its_complete_list()
    {
        var client = Client();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/duties/board"));
        Assert.Contains("<h1 class=\"page-title\">Duties</h1>", html);
        // Every Main Duty (and Activities) as a card, none selected yet.
        Assert.Equal(4, Regex.Matches(html, "<article class=\"dmain").Count);
        foreach (var t in new[] { "Morning Duty", "Midday Duty", "Dismissal Duty", "Activities" }) Assert.Contains($">{t}</a></h2>", html);
        Assert.DoesNotContain("Select a duty above", html);                                  // no empty instruction panel
        Assert.DoesNotContain("class=\"duty-details", html);                                    // no details panel until a duty is selected
        Assert.DoesNotContain("class=\"dhero", html);
        Assert.Contains("data-duty-live=\"/duties/overview?date=", html);
        // The header's actions: Add Duty and Duty Audit (the former Morning Duty button is gone; its card stays).
        var actions = html[html.IndexOf("class=\"page-head__actions\"", StringComparison.Ordinal)..html.IndexOf("id=\"duties-section\"", StringComparison.Ordinal)];
        Assert.Contains("href=\"/duties/main/new\" data-modal>", actions);
        Assert.Contains("href=\"/duties/audit\">", actions);
        Assert.DoesNotContain("Morning Duty", actions);
        Assert.DoesNotContain("id=\"duty-audit-title\"", html);   // the audit is its own page
        var (ordinary, _) = await fx.NewTeacherAsync();          // working as an ordinary staff member: not authorized
        Assert.DoesNotContain("Audit this duty", WebUtility.HtmlDecode(await fx.ClientAs(fx.Factory, ordinary).GetStringAsync("/duties/board?duty=midday")));

        // The audit (authorized user): header actions, tabs, every filter and the table.
        var audit = WebUtility.HtmlDecode(await (await fx.DutyAuditClientAsync()).GetStringAsync("/duties/audit"));
        Assert.Contains("<h1 class=\"page-title\">Duty Audit</h1>", audit);
        Assert.DoesNotContain("<article class=\"dmain", audit);
        Assert.Contains("data-download=\"/duties/audit/pdf\"", audit);
        Assert.Contains("href=\"/duties/audit/staff-records\"", audit);
        foreach (var f in new[] { "name=\"from\"", "name=\"to\"", "name=\"type\"", "name=\"component\"", "name=\"location\"", "name=\"staffId\"", "name=\"status\"", "name=\"followUp\"" })
            Assert.Contains(f, audit);
        Assert.Contains("id=\"audit-list\" data-ajax-list", audit);

        // A card opens its full board (status, coverage, every Sub-Duty with its management page) below the cards.
        var date = SchoolWeek.Iso(SchoolWeek.Today);
        var midday = WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/board?duty=midday&date={date}"));
        Assert.Matches("<article class=\"dmain[^\"]*is-selected\" aria-labelledby=\"dm-Midday\" aria-current=\"true\">", midday);
        Assert.Contains("Selected duty · Midday Duty", midday);
        Assert.Contains("class=\"dhero", midday);
        Assert.Equal(5, Regex.Matches(midday, "<li class=\"dsub dsub--").Count);
        Assert.Contains("href=\"/duties/midday/upper-elementary?date=", midday);               // the Sub-Duty's management page
        Assert.Contains("href=\"/duties/midday/schedule\"", midday);
        Assert.Contains($"href=\"/duties/midday/report.pdf?date={date}\"", midday);
        Assert.Contains($"data-duty-live=\"/duties/overview?date={date}&duty=midday\"", midday);
        // An authorized user can audit the selected duty from its board.
        var authorized = WebUtility.HtmlDecode(await (await fx.DutyAuditClientAsync()).GetStringAsync($"/duties/board?duty=midday&date={date}"));
        Assert.Contains($"href=\"/duties/audit?from={date}&to={date}&type=Midday#duty-audit\"", authorized);
    }

    [Fact]
    public async Task The_live_board_and_the_audit_list_reload_separately()
    {
        var client = Client();
        var board = new HttpRequestMessage(HttpMethod.Get, "/duties/overview?duty=morning");
        board.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var boardHtml = await (await client.SendAsync(board)).Content.ReadAsStringAsync();
        Assert.Contains("<article class=\"dmain", boardHtml);
        Assert.Contains("class=\"dhero", boardHtml);
        Assert.DoesNotContain("<html", boardHtml);

        var list = new HttpRequestMessage(HttpMethod.Get, "/duties/audit?status=covered&duty=morning");
        list.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var listHtml = await (await (await fx.DutyAuditClientAsync()).SendAsync(list)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<article class=\"dmain", listHtml);   // only the audit table
        Assert.DoesNotContain("<html", listHtml);

        // The old board still answers its own reload (a page opened before the change).
        var old = new HttpRequestMessage(HttpMethod.Get, "/duties?type=dismissal");
        old.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.Contains("class=\"dhero", await (await client.SendAsync(old)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Staff_Duty_Records_keeps_its_tab_in_Duty_Audit()
    {
        var html = WebUtility.HtmlDecode(await (await fx.DutyAuditClientAsync()).GetStringAsync("/duties/audit/staff-records"));
        Assert.Contains("<h1 class=\"page-title\">Duty Audit</h1>", html);
        Assert.DoesNotContain("<article class=\"dmain", html);
        Assert.Contains("href=\"/duties/audit/staff-records\" aria-current=\"page\"", html);
        Assert.Contains("data-download=\"/staff/duty-records/pdf\"", html);
        Assert.Contains("id=\"staff-list\" data-ajax-list", html);
    }

    [Fact]
    public async Task Duty_pages_lead_back_to_Duties()
    {
        var client = Client();
        foreach (var url in new[] { "/duties/morning/students-arrival", "/duties/morning/schedule", "/duties/departments", "/duties/activities" })
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(url));
            Assert.Matches("<li><a href=\"/duties(/board)?(\\?date=[0-9-]+)?\">Duties</a></li>", html);
            Assert.DoesNotContain("Duties & Audit", html);
            Assert.Contains("href=\"/duties\" aria-current=\"page\"", html[..html.IndexOf("</aside>", StringComparison.Ordinal)]);
        }
    }
}
