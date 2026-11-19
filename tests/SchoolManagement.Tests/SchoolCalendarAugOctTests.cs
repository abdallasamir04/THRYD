using System.Net;
using System.Text.RegularExpressions;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// August, September and October 2026 of the printed 2026/2027 teachers' calendar, as confirmed by the school: exactly
/// these events, dates, ranges and colours; date cells coloured only where specified; no dots under the dates.
/// </summary>
[Collection("db")]
public sealed class SchoolCalendarAugOctTests(DbFixture fx)
{
    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Calendar tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private const CalendarColor None = CalendarColor.None, Green = CalendarColor.Green, Blue = CalendarColor.Blue,
        Orange = CalendarColor.Orange, Pink = CalendarColor.Pink, Red = CalendarColor.Red, Yellow = CalendarColor.Yellow;

    // (date as listed, title, line colour, coloured part of the title or null)
    private static readonly (string Date, string Title, CalendarColor Color, string? Accent)[] August =
    [
        ("20", "Q1 Curriculum Map", Green, null),
        ("25", "Q1 Lesson Plans", Green, null),
        ("30", "Weeks 1 & 2 PPTs", Green, null),
    ];
    private static readonly (string Date, string Title, CalendarColor Color, string? Accent)[] September =
    [
        ("1", "Diagnostic Tests Submission", Green, null),
        ("3", "HW 1 & 2 + Quiz 1 Submission", Green, null),
        ("6–8", "First Day of School + Diagnostic Test", Blue, null),
        ("10", "Weeks 3 & 4 PPTs", None, null),
        ("23–24", "Aqua Day", Orange, null),
        ("27", "Quiz 2 Submission", Green, null),
        ("28", "Armed Forces Day Proposal", Pink, null),
        ("29", "Weeks 5 & 6 PPTs", Green, null),
        ("30", "Q1 Project Proposal Submission", Green, null),
    ];
    private static readonly (string Date, string Title, CalendarColor Color, string? Accent)[] October =
    [
        ("5", "World Teachers’ Day", None, null),
        ("6", "6th of October Victory", None, null),
        ("8", "Armed Forces Day", None, null),
        ("11", "Q1 Exams Submission + M.A.", Green, null),
        ("15", "Spelling Bee", None, null),
        ("15", "Q2 Curriculum Map Submission", None, null),
        ("18", "Fire Drill / Q1 Project Submission", Red, "Fire Drill"),
        ("18–22", "Revision", None, null),
        ("22", "Q2 Lesson Plans Submission", Green, null),
        ("25–29", "Q1 Exams (Submission of Grading Sheets + Graph Analysis on the same day of the exam)", Yellow, null),
    ];

    private async Task<int> LoadedYearAsync()
    {
        await using var db = fx.NewContext();
        var year = await new BelongingsService(db, DbFixture.Timetable(db), new TestUser()).CreateYearAsync("2026–2027 " + DbFixture.Unique());
        Assert.True(year.Succeeded);
        var load = await new SchoolCalendarService(db).LoadReferenceAsync(year.EntityId!.Value);
        Assert.True(load.Result.Succeeded);
        return year.EntityId!.Value;
    }

    [Fact]
    public async Task August_to_October_list_exactly_the_confirmed_events_in_date_order()
    {
        var yearId = await LoadedYearAsync();
        await using var db = fx.NewContext();
        var page = await new SchoolCalendarService(db).PageAsync(yearId);
        CalendarMonth Month(int m) => page.Months.Single(x => x.Month == new DateOnly(2026, m, 1));

        foreach (var (m, expected) in new[] { (8, August), (9, September), (10, October) })
        {
            var rows = Month(m).Rows.ToList();
            Assert.Equal(expected.Select(e => (e.Date, e.Title, e.Color, e.Accent)),
                rows.Select(r => (r.DateText, r.Event.Title, r.Event.Color, r.Event.AccentText)));
            Assert.All(rows, r => Assert.False(r.Event.NeedsConfirmation));
            Assert.All(rows, r => Assert.False(r.Continuation));
        }

        // Nothing on 1 August, and no quarter start listed there.
        Assert.DoesNotContain(Month(8).Rows, r => r.Event.StartDate == new DateOnly(2026, 8, 1));
        // The multi-day events are one line each with their whole range.
        var firstDay = Month(9).Rows.Single(r => r.Event.Title.StartsWith("First Day"));
        Assert.Equal((new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 8)), (firstDay.Event.StartDate!.Value, firstDay.Event.EndDate!.Value));
        var exams = Month(10).Rows.Single(r => r.Event.Color == Yellow);
        Assert.Equal((new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 29)), (exams.Event.StartDate!.Value, exams.Event.EndDate!.Value));

        // Date cells: coloured exactly where specified (10 September, 15 October and 1 August stay clean).
        var cells = page.Months.Where(x => x.Month.Month is 8 or 9 or 10).SelectMany(x => x.Weeks).SelectMany(w => w)
            .Where(d => d.InMonth && d.Color != None).ToDictionary(d => d.Date, d => d.Color);
        var expectedCells = new Dictionary<DateOnly, CalendarColor>();
        void Cells(int m, CalendarColor c, params int[] days) { foreach (var d in days) expectedCells[new(2026, m, d)] = c; }
        Cells(8, Green, 20, 25, 30);
        Cells(9, Green, 1, 3, 27, 29, 30); Cells(9, Blue, 6, 7, 8); Cells(9, Orange, 23, 24);
        Cells(10, Green, 11, 22); Cells(10, Red, 18); Cells(10, Yellow, 25, 26, 27, 28, 29);
        Assert.Equal(expectedCells.OrderBy(k => k.Key), cells.OrderBy(k => k.Key));
    }

    [Fact]
    public async Task The_page_shows_no_dots_and_colours_the_lines_and_the_Fire_Drill_words()
    {
        var yearId = await LoadedYearAsync();
        var html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync($"/school-calendar?year={yearId}"));

        Assert.DoesNotContain("sc-day__dot", html);
        Assert.DoesNotContain("sc-day__flag", html);
        Assert.DoesNotContain("Start of Q1", html);

        string Month(int m) => Regex.Match(html, $"<article class=\"sc-month\" id=\"m-2026-{m:00}\".*?</article>", RegexOptions.Singleline).Value;
        int Lines(string month) => Regex.Matches(month, "<li class=\"sc-ev").Count;
        Assert.Equal(3, Lines(Month(8)));
        Assert.Equal(9, Lines(Month(9)));
        Assert.Equal(10, Lines(Month(10)));

        // 10 September: no colour on the date, its line uncoloured but listed.
        Assert.Matches("<td class=\"sc-day has-events\">\\s*<a[^>]*aria-label=\"Thursday 10 September 2026: Weeks 3 & 4 PPTs\"", Month(9));
        Assert.Matches("<li class=\"sc-ev\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">10</span>", Month(9));
        // Pink line on 28 September; Fire Drill red on the words only; the exams one yellow line over 25–29.
        Assert.Matches("<li class=\"sc-ev is-colored sc-c-pink\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">28</span>", Month(9));
        Assert.Contains("<mark class=\"sc-ev__accent sc-c-red\">Fire Drill</mark> / Q1 Project Submission", Month(10));
        Assert.Matches("<li class=\"sc-ev is-colored sc-c-yellow\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">25–29</span>", Month(10));
    }
}
