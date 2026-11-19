using System.Net;
using System.Text.RegularExpressions;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// November 2026 – January 2027 of the printed 2026/2027 teachers' calendar, as confirmed by the school: the events, their
/// list colours and the coloured date cells, which differ where the print differs (13 December, 10 and 25 January).
/// </summary>
[Collection("db")]
public sealed class SchoolCalendarNovJanTests(DbFixture fx)
{
    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Calendar tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private const CalendarColor None = CalendarColor.None, Green = CalendarColor.Green, Blue = CalendarColor.Blue,
        Pink = CalendarColor.Pink, Red = CalendarColor.Red, Yellow = CalendarColor.Yellow, Gray = CalendarColor.Gray;

    private static readonly (string Date, string Title, CalendarColor Color)[] November =
    [
        ("1", "Start of Q2", Blue),
        ("12", "Q2 Project Proposal Submission", Green),
        ("16", "Career Day", None),
        ("19", "PTC", Pink),
        ("22–26", "Reading Week", None),
        ("30", "International Day", None),
    ];
    private static readonly (string Date, string Title, CalendarColor Color)[] December =
    [
        ("2", "Mock Exams Submission", Green),
        ("3", "Fire Drill", Red),
        ("6", "Q2 Exams Submission", Green),
        ("8", "Q3 Curriculum Map Submission", Green),
        ("13", "Q2 Projects Submission", Green),
        ("13–15", "Revision", None),
        ("15", "Arabic Day", None),
        ("16–22", "Q2 Exams", Yellow),
        ("22", "Xmas Bazaar", None),
        ("23", "Q3 Lesson Plans Submission", None),
        ("27–31", "Xmas Break", None),
    ];
    private static readonly (string Date, string Title, CalendarColor Color)[] January =
    [
        ("1–9", "Xmas Break", None),
        ("10", "Start of Q3", None),
        ("11–14", "Environmental Awareness", None),
        ("12", "Science Fair Proposal Submission", Green),
        ("13", "Quiz 1 Submission", Green),
        ("21", "Science Fair", None),
        ("25", "National Police Day", Gray),
        ("26", "International Day of Education (UN)", None),
        ("28", "National Police Day Off", None),
    ];

    private async Task<int> LoadedYearAsync()
    {
        await using var db = fx.NewContext();
        var year = await new BelongingsService(db, DbFixture.Timetable(db), new TestUser()).CreateYearAsync("2026–2027 " + DbFixture.Unique());
        Assert.True(year.Succeeded);
        Assert.True((await new SchoolCalendarService(db).LoadReferenceAsync(year.EntityId!.Value)).Result.Succeeded);
        return year.EntityId!.Value;
    }

    [Fact]
    public async Task November_to_January_list_exactly_the_confirmed_events_and_colour_only_the_printed_dates()
    {
        var yearId = await LoadedYearAsync();
        await using var db = fx.NewContext();
        var page = await new SchoolCalendarService(db).PageAsync(yearId);
        CalendarMonth Month(int y, int m) => page.Months.Single(x => x.Month == new DateOnly(y, m, 1));

        foreach (var (y, m, lines) in new[] { (2026, 11, November), (2026, 12, December), (2027, 1, January) })
            Assert.Equal(lines.Select(e => (e.Date, e.Title, e.Color)),
                Month(y, m).Rows.Select(r => (r.DateText, r.Event.Title, r.Event.Color)));

        // Each event once: no "continued" lines (the Reunion Break of 31 Jan – 4 Feb is listed under February only).
        Assert.DoesNotContain(page.Months.SelectMany(x => x.Rows), r => r.Continuation);
        Assert.DoesNotContain(Month(2027, 1).Rows, r => r.Event.Title == "Reunion Break");
        Assert.Contains(Month(2027, 2).Rows, r => r.Event.Title == "Reunion Break");

        // Date cells: exactly the printed ones (13 December green in the list only; 10 January blue on the date only).
        var cells = new[] { Month(2026, 11), Month(2026, 12), Month(2027, 1) }.SelectMany(x => x.Weeks).SelectMany(w => w)
            .Where(d => d.InMonth && d.Color != None).ToDictionary(d => d.Date, d => d.Color);
        var expected = new Dictionary<DateOnly, CalendarColor>
        {
            [new(2026, 11, 1)] = Blue, [new(2026, 11, 12)] = Green,
            [new(2026, 12, 2)] = Green, [new(2026, 12, 3)] = Red, [new(2026, 12, 6)] = Green, [new(2026, 12, 8)] = Green,
            [new(2026, 12, 16)] = Yellow, [new(2026, 12, 17)] = Yellow, [new(2026, 12, 20)] = Yellow, [new(2026, 12, 21)] = Yellow, [new(2026, 12, 22)] = Yellow,
            [new(2027, 1, 10)] = Blue, [new(2027, 1, 12)] = Green, [new(2027, 1, 13)] = Green,
        };
        Assert.Equal(expected.OrderBy(k => k.Key), cells.OrderBy(k => k.Key));
    }

    [Fact]
    public async Task The_page_keeps_list_and_date_colours_apart()
    {
        var yearId = await LoadedYearAsync();
        var html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync($"/school-calendar?year={yearId}"));
        string Month(string id) => Regex.Match(html, $"<article class=\"sc-month\" id=\"m-{id}\".*?</article>", RegexOptions.Singleline).Value;
        int Lines(string month) => Regex.Matches(month, "<li class=\"sc-ev").Count;
        Assert.Equal(6, Lines(Month("2026-11")));
        Assert.Equal(11, Lines(Month("2026-12")));
        Assert.Equal(9, Lines(Month("2027-01")));
        Assert.DoesNotContain("sc-day__dot", html);

        // 13 December: green line, plain date cell.
        Assert.Matches("<li class=\"sc-ev is-colored sc-c-green\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">13</span>", Month("2026-12"));
        Assert.Matches("<td class=\"sc-day has-events\">\\s*<a[^>]*aria-label=\"Sunday 13 December 2026:", Month("2026-12"));
        // 10 January: plain line, blue date cell. 25 January: grey line.
        Assert.Matches("<li class=\"sc-ev\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">10</span>", Month("2027-01"));
        Assert.Matches("<td class=\"sc-day is-marked sc-c-blue has-events\">\\s*<a[^>]*aria-label=\"Sunday 10 January 2027:", Month("2027-01"));
        Assert.Matches("<li class=\"sc-ev is-colored sc-c-gray\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">25</span>", Month("2027-01"));
    }
}
