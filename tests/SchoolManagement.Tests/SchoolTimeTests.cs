using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using UglyToad.PdfPig;

namespace SchoolManagement.Tests;

/// <summary>
/// One school time reference: Egyptian local time (Africa/Cairo) with the OS daylight-saving rules, independent of the
/// server's and the browser's zone; the attendance cutoff, PDFs and the navbar clock all use it.
/// </summary>
[Collection("db")]
public class SchoolTimeTests(DbFixture fx)
{
    /// <summary>A clock stopped at a chosen instant.</summary>
    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private static SchoolTimeService At(string utcIso, string zone = SchoolTimeService.DefaultTimeZoneId) =>
        new(new FixedClock(DateTimeOffset.Parse(utcIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)), zone);

    private static DateTime Utc(string iso) => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ------------------------------------------------------------------ 3, 4, 5 — Egyptian time, winter and summer

    [Fact]
    public void Cairo_is_found_in_the_system_time_zone_database_and_follows_its_daylight_saving_rules()
    {
        var t = At("2026-01-15T12:00:00Z");
        Assert.False(t.IsFallback);
        Assert.Equal(TimeSpan.FromHours(2), t.OffsetAt(Utc("2026-01-15T12:00:00Z")));  // winter
        Assert.Equal(TimeSpan.FromHours(3), t.OffsetAt(Utc("2026-07-15T12:00:00Z")));  // summer
        Assert.Equal("UTC+02:00", t.OffsetLabel());
        Assert.Equal("UTC+03:00", At("2026-07-15T06:30:00Z").OffsetLabel());
        Assert.Equal(new DateTime(2026, 7, 15, 9, 30, 0), At("2026-07-15T06:30:00Z").Now);
        Assert.Equal(new DateTime(2026, 1, 15, 9, 30, 0), At("2026-01-15T07:30:00Z").Now);
        // The Windows id gives the same zone (Development on Windows, Production on Linux use the same rules).
        Assert.Equal(t.OffsetAt(Utc("2026-07-15T12:00:00Z")), new SchoolTimeService(TimeProvider.System, SchoolTimeService.WindowsTimeZoneId).OffsetAt(Utc("2026-07-15T12:00:00Z")));
    }

    [Fact]
    public void Daylight_saving_changes_never_break_conversions()
    {
        var t = At("2026-04-24T12:00:00Z");
        // Spring forward (Friday 24 April 2026): midnight does not exist; the day starts at 01:00 (+03:00).
        Assert.Equal(Utc("2026-04-23T22:00:00Z"), t.StartOfDayUtc(new DateOnly(2026, 4, 24)));
        Assert.Equal(new DateTime(2026, 4, 24, 1, 0, 0), t.ToLocal(t.StartOfDayUtc(new DateOnly(2026, 4, 24))));
        // Fall back (Thursday 29 October 2026, midnight → 23:00): 23:30 happens twice; the first one is used.
        Assert.Equal(Utc("2026-10-29T20:30:00Z"), t.ToUtc(new DateOnly(2026, 10, 29), new TimeOnly(23, 30)));
        Assert.Equal(new DateOnly(2026, 10, 29), t.LocalDate(Utc("2026-10-29T21:30:00Z"))); // second 23:30 is still the 29th
    }

    [Fact]
    public void School_time_does_not_depend_on_the_servers_own_time_zone()
    {
        // 22:30 UTC on 15 Nov: already 16 Nov in Auckland, still 16 Nov 00:30 in Cairo, but 15 Nov in UTC.
        var cairo = At("2026-11-15T22:30:00Z");
        var other = At("2026-11-15T22:30:00Z", "America/New_York");
        Assert.Equal(new DateOnly(2026, 11, 16), cairo.Today);
        Assert.Equal(new DateOnly(2026, 11, 15), other.Today);
        Assert.True(cairo.IsSchoolWeekday(cairo.Today)); // Monday
        // Stored UTC instants (EF returns Unspecified) are read as UTC.
        Assert.Equal(new DateTime(2026, 11, 16, 0, 30, 0), cairo.ToLocal(DateTime.SpecifyKind(Utc("2026-11-15T22:30:00Z"), DateTimeKind.Unspecified)));
    }

    // ------------------------------------------------------------------ 7 — the 09:30 cutoff in Cairo time

    [Theory]
    [InlineData("2023-03-07", "2023-03-07T07:29:59Z", "2023-03-07T07:30:00Z")] // winter, UTC+02:00
    [InlineData("2023-05-09", "2023-05-09T06:29:59Z", "2023-05-09T06:30:00Z")] // summer, UTC+03:00
    public async Task Arrival_registration_closes_at_0930_Cairo_time_in_winter_and_summer(string day, string at0929, string at0930)
    {
        var date = DateOnly.Parse(day, CultureInfo.InvariantCulture);
        var classId = await fx.NewClassAsync();
        var onTime = (await fx.NewStudentAsync(classId)).Id;
        var late = (await fx.NewStudentAsync(classId)).Id;
        await using var db = fx.NewContext();
        await db.Settings.Where(s => s.Key == SettingsService.LateCutoffKey).ExecuteDeleteAsync(); // default 09:30
        await db.Students.Where(s => s.Id == onTime || s.Id == late) // enrolled before that day
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var first = await DbFixture.SchoolAttendanceAt(db, At(at0929)).RegisterArrivalAsync(onTime, date, classId);
        Assert.True(first.Created, first.Message);
        Assert.Equal(new TimeOnly(9, 29, 59), first.Row!.Time!.Value);

        var second = await DbFixture.SchoolAttendanceAt(db, At(at0930)).RegisterArrivalAsync(late, date, classId);
        Assert.False(second.Created);
        Assert.True(second.Closed);
        Assert.False(await db.StudentArrivals.AnyAsync(r => r.StudentId == late));
    }

    // ------------------------------------------------------------------ 2, 3, 9 — display

    [Fact]
    public void Stored_timestamps_are_displayed_in_Cairo_time()
    {
        Assert.Equal("15 Jul 2026, 15:00", Ui.DateTime(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Unspecified)));
        Assert.Equal("15 Jan 2026, 14:00", Ui.DateTime(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified)));
        Assert.Equal("16 Nov 2026", Ui.Date(new DateTime(2026, 11, 15, 22, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("07:27:45 PM", SchoolTime.Clock(new DateTime(2026, 11, 15, 19, 27, 45)));
        Assert.Equal("12:05:00 AM", SchoolTime.Clock(new DateTime(2026, 11, 16, 0, 5, 0)));
        Assert.Equal("Sunday, 15 November 2026", SchoolTime.LongDate(new DateTime(2026, 11, 15, 19, 27, 45)));
    }

    [Fact]
    public async Task Every_page_shows_the_live_Cairo_clock_in_the_top_bar()
    {
        var html = System.Net.WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/timetable/periods"));
        var now = SchoolTime.Current.Now;
        Assert.Contains("data-school-clock", html);
        Assert.Contains("data-tz=\"Africa/Cairo\"", html);
        Assert.Contains($"Cairo, Egypt · {SchoolTime.Current.OffsetLabel()}", html);
        // [calendar] Sun, 15 Nov 2026 | [clock] 07:43:20 PM — AM/PM in its own smaller element; full date as the tooltip.
        Assert.Matches(new Regex(@"data-clock-time datetime=""\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d""><span data-clock-hms>(0[1-9]|1[0-2]):[0-5]\d</span><span class=""school-clock__sec"" data-clock-sec>:[0-5]\d</span><span class=""school-clock__ampm"" data-clock-ampm>(AM|PM)</span></time>"), html);
        Assert.Contains($">{now.ToString("ddd, d MMM yyyy", CultureInfo.InvariantCulture)}</time>", html);
        Assert.Contains($"title=\"{SchoolTime.LongDate(now)}\"", html);
        Assert.Contains("role=\"tooltip\" data-clock-zone>Cairo, Egypt · UTC", html);
        Assert.Contains("/js/school-clock.js", html);
        // The epoch the browser ticks from is the server's UTC instant (within a minute of now).
        var epoch = long.Parse(Regex.Match(html, "data-utc=\"(\\d+)\"").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - epoch, 0, 60_000);
    }

    [Fact]
    public async Task Pdf_exports_are_stamped_with_Cairo_time()
    {
        var bytes = await fx.Factory.CreateClient().GetByteArrayAsync("/arrivals/pdf");
        using var pdf = PdfDocument.Open(bytes);
        var text = string.Concat(pdf.GetPages().Select(p => p.Text)).Replace(" ", "");
        var now = SchoolTime.Current.Now;
        // "Exported 15 Nov 2026, 19:27" (a minute boundary may pass during the request).
        Assert.True(text.Contains("Exported" + now.ToString("dMMMyyyy,HH:mm", CultureInfo.InvariantCulture))
                    || text.Contains("Exported" + now.AddMinutes(-1).ToString("dMMMyyyy,HH:mm", CultureInfo.InvariantCulture)), text[..Math.Min(400, text.Length)]);
    }
}
