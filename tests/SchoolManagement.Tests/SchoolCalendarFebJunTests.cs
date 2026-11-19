using System.Net;
using System.Text.RegularExpressions;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// February – June 2027 of the printed 2026/2027 teachers' calendar, as confirmed by the school and checked against the
/// print: the events, their list colours (green and dark green kept apart) and the coloured date cells.
/// </summary>
[Collection("db")]
public sealed class SchoolCalendarFebJunTests(DbFixture fx)
{
    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Calendar tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private const CalendarColor None = CalendarColor.None, Green = CalendarColor.Green, Dark = CalendarColor.DarkTeal, Blue = CalendarColor.Blue,
        Pink = CalendarColor.Pink, Red = CalendarColor.Red, Yellow = CalendarColor.Yellow;

    private static readonly Dictionary<int, (string Date, string Title, CalendarColor Color)[]> Lists = new()
    {
        [2] =
        [
            ("31 Jan – 4 Feb", "Reunion Break", None), ("4", "World Cancer Day", None), ("8", "1st Day of Ramadan", None),
            ("9", "100 Days of School Proposal Submission", Green), ("10", "Quiz 2 Submission", Green), ("11", "PTC", Pink),
            ("14", "Valentine’s Day", None), ("17", "Q3 Project Proposal Submission", None), ("18", "100 Days of School", None),
            ("25", "Fire Drill", Red), ("28", "Drama Day", None),
        ],
        [3] =
        [
            ("7", "Q3 Exams Submission", Dark), ("9", "Eid El Fitr Celebration", None), ("10–13", "Eid El Fitr Holiday", None),
            ("14", "International Math Day", None), ("15", "Mock Exams Submission", Dark),
            ("21", "Q4 Curriculum Map Submission", Dark), ("21–25", "Revision", None), ("23", "Q4 Project Submission", Dark),
            ("25", "Q4 Lesson Plans Submission", Dark),
            ("28 Mar – 1 Apr", "Q3 Exams", Yellow),
        ],
        [4] =
        [
            ("4", "Start of Q4", Blue), ("5", "Social Studies Gallery Proposal", Green), ("6", "Quiz 1 Submission", Green),
            ("11", "Spirit Week Proposal Submission", Green), ("13", "Social Studies Gallery", None),
            ("14", "Production Day Proposal Submission", Green), ("18–22", "Spirit Week + Art Gallery", None), ("25–29", "Easter Break", None),
        ],
        [5] =
        [
            ("2", "Coptic Easter", None), ("3", "Sham El Nesim", None), ("6", "Labor Day Off", None), ("9", "Quiz 2 Submission", Green),
            ("10", "Q4 Project Proposal Submission", Green), ("13", "Eid Al-Adha Celebration", None), ("16–20", "Eid Al-Adha Holiday", None),
            ("21", "Q4 Exams Submission", Dark), ("26", "Mock Exams Submission", Dark), ("27", "Production Day", None),
            ("30", "Q4 Projects Submission", None),
        ],
        [6] =
        [
            // June keeps the printed order exactly (Fire Drill before Revision, the Day Off before the Q4 Exams).
            ("3", "Fire Drill", Red), ("1–3", "Revision", None), ("6", "Hijri New Year", None), ("10", "Hijri New Year Day Off", None),
            ("6–15", "Q4 Exams", Yellow), ("20", "All Grading Sheets Submission", None),
            ("22:27/28", "Pacing Guide Submission", Dark), ("27", "Q1 Lesson Plans Submission", Dark), ("29", "Summer Pack Submission", Dark),
        ],
    };

    private async Task<int> LoadedYearAsync()
    {
        await using var db = fx.NewContext();
        var year = await new BelongingsService(db, DbFixture.Timetable(db), new TestUser()).CreateYearAsync("2026–2027 " + DbFixture.Unique());
        Assert.True(year.Succeeded);
        Assert.True((await new SchoolCalendarService(db).LoadReferenceAsync(year.EntityId!.Value)).Result.Succeeded);
        return year.EntityId!.Value;
    }

    [Fact]
    public async Task February_to_June_list_the_confirmed_events_and_colour_only_the_printed_dates()
    {
        var yearId = await LoadedYearAsync();
        await using var db = fx.NewContext();
        var page = await new SchoolCalendarService(db).PageAsync(yearId);
        CalendarMonth Month(int m) => page.Months.Single(x => x.Month == new DateOnly(2027, m, 1));

        foreach (var (m, lines) in Lists)
            Assert.Equal(lines, Month(m).Rows.Select(r => (r.DateText, r.Event.Title, r.Event.Color)).ToArray());

        // Cross-month events are single records listed once: the Reunion Break (31 Jan – 4 Feb) under February, the Q3
        // Exams (28 Mar – 1 Apr) under March; their dates in the other month still carry them.
        Assert.Single(page.Months.SelectMany(x => x.Rows), r => r.Event.Title == "Q3 Exams");
        Assert.Contains("Q3 Exams", Month(4).Weeks.SelectMany(w => w).Single(d => d.Date == new DateOnly(2027, 4, 1)).Events);
        // Every line here is confirmed and dated (7 March Q3 Exams Submission confirmed by the school).
        Assert.DoesNotContain(Lists.Keys.SelectMany(m => Month(m).Rows), r => r.Event.NeedsConfirmation);
        Assert.All(Lists.Keys.SelectMany(m => Month(m).Rows), r => Assert.NotNull(r.Event.StartDate));

        var cells = Lists.Keys.Select(Month).SelectMany(x => x.Weeks).SelectMany(w => w)
            .Where(d => d.InMonth && d.Color != None).ToDictionary(d => d.Date, d => d.Color);
        var expected = new Dictionary<DateOnly, CalendarColor>();
        void Cells(int m, CalendarColor c, params int[] days) { foreach (var d in days) expected[new(2027, m, d)] = c; }
        Cells(2, Green, 9, 10); Cells(2, Red, 25);
        Cells(3, Dark, 7, 8, 15, 21, 25); Cells(3, Yellow, 28, 29, 30, 31);
        Cells(4, Yellow, 1); Cells(4, Blue, 4); Cells(4, Green, 5, 6);
        Cells(5, Green, 9, 10); Cells(5, Dark, 26);
        Cells(6, Red, 3); Cells(6, Yellow, 6, 7, 8, 9, 13, 14, 15); Cells(6, Dark, 22, 27, 29);
        Assert.Equal(expected.OrderBy(k => k.Key), cells.OrderBy(k => k.Key));
    }

    [Fact]
    public async Task Every_month_heading_is_one_centred_line_and_the_dark_green_lines_render_dark()
    {
        var yearId = await LoadedYearAsync();
        var client = fx.Factory.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/school-calendar?year={yearId}"));
        Assert.Equal(12, Regex.Matches(html, "<header class=\"sc-month__head\">\\s*<h3 class=\"sc-month__title\"[^>]*>\\w+ <span>\\d{4}</span></h3>\\s*</header>").Count);
        Assert.Matches("<li class=\"sc-ev is-colored sc-c-dark-teal\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">7</span>", html);
        Assert.Matches("<li class=\"sc-ev is-colored sc-c-dark-teal\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">23</span>", html);
        Assert.Matches("<li class=\"sc-ev is-colored sc-c-dark-teal\">\\s*<a[^>]*>\\s*<span class=\"sc-ev__date\">29</span>", html);

        var css = await client.GetStringAsync("/css/school-calendar.css");
        Assert.Matches(@"\.sc-month__head \{[^}]*justify-content: center;[^}]*text-align: center;", css);
        Assert.Matches(@"\.sc-month__title \{[^}]*white-space: nowrap;", css);
        Assert.DoesNotMatch(@"\.sc-month__head \{[^}]*space-between", css);
    }
}
