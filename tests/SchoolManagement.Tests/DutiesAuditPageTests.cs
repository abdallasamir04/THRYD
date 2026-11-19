using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using SchoolManagement.Web.Common;

namespace SchoolManagement.Tests;

/// <summary>
/// Duties & Audit: one page with every Main Duty as a card (the selected one's full board below the cards) above the
/// Duty Audit and Staff Duty Records, which keep their tabs, filters, tables, report and AJAX lists. The former Duties
/// addresses lead to the right card; the duty management pages are unchanged and open from the board.
/// </summary>
[Collection("db")]
public class DutiesAuditPageTests(DbFixture fx)
{
    private HttpClient Client(bool follow = true) => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = follow });

    [Theory]
    [InlineData("/duties", "/duties/audit")]
    [InlineData("/duties?date=2026-11-10", "/duties/audit?date=2026-11-10")]
    [InlineData("/duties?type=morning", "/duties/audit?duty=morning#duty-details")]
    [InlineData("/duties?type=Midday&date=2026-11-10", "/duties/audit?duty=midday&date=2026-11-10#duty-details")]
    [InlineData("/duties?type=unknown", "/duties/audit")]
    [InlineData("/duties/overview?duty=dismissal", "/duties/audit?duty=dismissal#duty-details")]
    public async Task The_former_Duties_addresses_lead_to_the_right_card(string from, string to)
    {
        var r = await Client(follow: false).GetAsync(from);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(to, r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_page_has_the_duty_cards_on_top_and_the_complete_audit_below()
    {
        var client = Client();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/duties/audit"));
        Assert.Contains("<h1 class=\"page-title\">Duties & Audit</h1>", html);
        // Top: every Main Duty (and Activities) as a card, none selected yet.
        Assert.Equal(4, Regex.Matches(html, "<article class=\"dmain").Count);
        foreach (var t in new[] { "Morning Duty", "Midday Duty", "Dismissal Duty", "Activities" }) Assert.Contains($">{t}</a></h2>", html);
        Assert.Contains("Select a duty above", html);
        Assert.DoesNotContain("class=\"dhero", html);
        Assert.Contains("data-duty-live=\"/duties/overview?date=", html);
        // Below: the audit, with its header actions, tabs, every filter and the table.
        Assert.True(html.IndexOf("id=\"duties-section-title\"", StringComparison.Ordinal) < html.IndexOf("id=\"duty-audit-title\"", StringComparison.Ordinal));
        Assert.Contains("data-download=\"/duties/audit/pdf\"", html);
        Assert.Matches("href=\"/duties/audit\\?duty=morning&date=[0-9-]+#duty-details\">[^<]*<svg[^>]*><use href=\"#i-shield-check\"></use></svg>Morning Duty</a>", html);
        Assert.Contains("href=\"/duties/audit/staff-records\"", html);
        foreach (var f in new[] { "name=\"from\"", "name=\"to\"", "name=\"type\"", "name=\"component\"", "name=\"location\"", "name=\"staffId\"", "name=\"status\"", "name=\"followUp\"" })
            Assert.Contains(f, html);
        Assert.Contains("id=\"audit-list\" data-ajax-list", html);

        // A card opens its full board (status, coverage, every Sub-Duty with its management page) below the cards.
        var date = SchoolWeek.Iso(SchoolWeek.Today);
        var midday = WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/audit?duty=midday&date={date}"));
        Assert.Matches("<article class=\"dmain[^\"]*is-selected\" aria-labelledby=\"dm-Midday\" aria-current=\"true\">", midday);
        Assert.Contains("Selected duty · Midday Duty", midday);
        Assert.Contains("class=\"dhero", midday);
        Assert.Equal(5, Regex.Matches(midday, "<li class=\"dsub dsub--").Count);
        Assert.Contains("href=\"/duties/midday/upper-elementary?date=", midday);               // the Sub-Duty's management page
        Assert.Contains("href=\"/duties/midday/schedule\"", midday);
        Assert.Contains($"href=\"/duties/midday/report.pdf?date={date}\"", midday);
        Assert.Contains($"data-duty-live=\"/duties/overview?date={date}&duty=midday\"", midday);
        // The audit filters keep the day and the selected duty in the address.
        Assert.Contains("<input type=\"hidden\" name=\"duty\" value=\"midday\" />", midday);
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
        var listHtml = await (await client.SendAsync(list)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<article class=\"dmain", listHtml);   // only the audit table
        Assert.DoesNotContain("<html", listHtml);

        // The old board still answers its own reload (a page opened before the change).
        var old = new HttpRequestMessage(HttpMethod.Get, "/duties?type=dismissal");
        old.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.Contains("class=\"dhero", await (await client.SendAsync(old)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Staff_Duty_Records_keeps_its_tab_under_the_same_duty_cards()
    {
        var html = WebUtility.HtmlDecode(await Client().GetStringAsync("/duties/audit/staff-records"));
        Assert.Contains("<h1 class=\"page-title\">Duties & Audit</h1>", html);
        Assert.Equal(4, Regex.Matches(html, "<article class=\"dmain").Count);
        Assert.Contains("href=\"/duties/audit/staff-records\" aria-current=\"page\"", html);
        Assert.Contains("data-download=\"/staff/duty-records/pdf\"", html);
        Assert.Contains("id=\"staff-list\" data-ajax-list", html);
    }

    [Fact]
    public async Task Duty_pages_lead_back_to_Duties_and_Audit()
    {
        var client = Client();
        foreach (var url in new[] { "/duties/morning/students-arrival", "/duties/morning/schedule", "/duties/departments", "/duties/activities" })
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(url));
            Assert.Contains(">Duties & Audit</a></li>", html);
            Assert.Contains("href=\"/duties/audit\" aria-current=\"page\"", html[..html.IndexOf("</aside>", StringComparison.Ordinal)]);
        }
    }
}
