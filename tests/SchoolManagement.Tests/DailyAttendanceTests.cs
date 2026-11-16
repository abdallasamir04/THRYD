using System.Globalization;
using System.Net;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// School Attendance with the 09:30 cutoff (Africa/Cairo): one-click arrivals before it (Present by the 07:40 start time, Late
/// after), registration refused on the server from 09:30:00, students with no arrival Absent after it (shown at once, stored by
/// the finalizer), alerts and reports on those absences, earlier history left as recorded, corrections still available.
/// Day-specific tests use 2023 dates (each test its own days) and students enrolled in 2023, so no other test's students are involved.
/// </summary>
[Collection("db")]
public class DailyAttendanceTests(DbFixture fx)
{
    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    /// <summary>School time stopped at a Cairo wall time on a 2023 date (UTC+02:00 in winter, +03:00 from 28 April 2023).</summary>
    private static SchoolTimeService CairoAt(DateOnly day, int h, int m, int s = 0)
    {
        var local = day.ToDateTime(new TimeOnly(h, m, s));
        var offset = TimeZoneInfo.FindSystemTimeZoneById(SchoolTimeService.DefaultTimeZoneId).GetUtcOffset(local);
        return new(new FixedClock(new DateTimeOffset(local, offset)), SchoolTimeService.DefaultTimeZoneId);
    }

    private static readonly DateOnly AutomaticFrom = new(2023, 1, 1);

    /// <summary>School attendance at a Cairo time, with automatic absences from 1 Jan 2023 (or without them).</summary>
    private static ArrivalService At(SchoolDbContext db, DateOnly day, int h, int m, int s = 0, bool automatic = true) =>
        DbFixture.SchoolAttendanceWith(db, CairoAt(day, h, m, s), new AttendanceOptions(automatic, AutomaticFrom));

    private static void Ok(ServiceResult r) => Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));

    private static readonly TimeOnly Start = new(7, 40), Cutoff = new(9, 30);

    /// <summary>School start time 07:40 and the default 09:30 cutoff for the test; the previous settings are restored afterwards.</summary>
    private async Task WithRulesAsync(Func<Task> test)
    {
        await using var db = fx.NewContext();
        var settings = DbFixture.Settings(db);
        var oldStart = await settings.GetArrivalDeadlineAsync();
        await db.Settings.Where(s => s.Key == SettingsService.LateCutoffKey).ExecuteDeleteAsync(); // default 09:30
        Ok(await settings.SaveArrivalDeadlineAsync(Start));
        try { await test(); }
        finally
        {
            await db.Settings.Where(s => s.Key == SettingsService.LateCutoffKey).ExecuteDeleteAsync();
            if (oldStart is { } t) Ok(await settings.SaveArrivalDeadlineAsync(t));
            else await db.Settings.Where(s => s.Key == "Arrival.Deadline").ExecuteDeleteAsync();
        }
    }

    private async Task<(int ClassId, int[] Students)> StudentsEnrolledIn2023Async(int count)
    {
        var classId = await fx.NewClassAsync();
        var ids = new List<int>();
        for (var i = 0; i < count; i++) ids.Add((await fx.NewStudentAsync(classId)).Id);
        await using var db = fx.NewContext();
        await db.Students.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        return (classId, ids.ToArray());
    }

    // ------------------------------------------------------------------ the rule

    [Theory]
    [InlineData("07:15:00", ArrivalOutcome.Present)]
    [InlineData("07:40:00", ArrivalOutcome.Present)]
    [InlineData("07:40:01", ArrivalOutcome.Late)]
    [InlineData("09:20:00", ArrivalOutcome.Late)]
    [InlineData("09:29:59", ArrivalOutcome.Late)]
    [InlineData("09:30:00", ArrivalOutcome.Absent)]
    [InlineData("09:31:00", ArrivalOutcome.Absent)]
    public void Present_by_the_start_time_Late_before_0930_and_closed_from_0930(string time, ArrivalOutcome expected)
    {
        Assert.Equal(expected, Arrivals.Evaluate(TimeOnly.Parse(time, CultureInfo.InvariantCulture), Start, Cutoff));
        // Without a start time every arrival before the cutoff is Present; the cutoff still closes.
        Assert.Equal(expected == ArrivalOutcome.Absent ? ArrivalOutcome.Absent : ArrivalOutcome.Present,
            Arrivals.Evaluate(TimeOnly.Parse(time, CultureInfo.InvariantCulture), null, Cutoff));
    }

    [Fact]
    public void Times_are_shown_in_12_hour_format()
    {
        Assert.Equal("7:42 AM", Arrivals.Time(new TimeOnly(7, 42, 10)));
        Assert.Equal("1:05 PM", Arrivals.Time(new TimeOnly(13, 5)));
        Assert.Equal("Not Recorded", Arrivals.Label((ArrivalOutcome?)null));
    }

    // ------------------------------------------------------------------ the cutoff, on the server

    [Fact]
    public async Task Arrivals_are_accepted_until_092959_and_refused_from_0930_on_the_server()
    {
        await WithRulesAsync(async () =>
        {
            var day = new DateOnly(2023, 2, 5); // Sunday
            var (classId, s) = await StudentsEnrolledIn2023Async(4);
            await using var db = fx.NewContext();

            var at0720 = await At(db, day, 7, 20).RegisterArrivalAsync(s[0], day, classId);
            Assert.True(at0720.Created, at0720.Message);
            Assert.Equal(ArrivalOutcome.Present, at0720.Row!.Outcome);

            var at0929 = await At(db, day, 9, 29, 59).RegisterArrivalAsync(s[1], day, classId);
            Assert.True(at0929.Created, at0929.Message);
            Assert.Equal((ArrivalOutcome.Late, new TimeOnly(9, 29, 59)), (at0929.Row!.Outcome!.Value, at0929.Row.Time!.Value));
            var stored = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.StudentId == s[1]);
            Assert.Equal((LateStatus.Late, Start, Cutoff), (stored.LateStatus!.Value, stored.DeadlineUsed!.Value, stored.CutoffUsed!.Value));

            // Exactly 09:30 and 09:31: refused, nothing stored, and the service says registration is closed.
            foreach (var (h, m, student) in new[] { (9, 30, s[2]), (9, 31, s[3]) })
            {
                var refused = await At(db, day, h, m).RegisterArrivalAsync(student, day, classId);
                Assert.False(refused.Created);
                Assert.True(refused.Closed);
                Assert.StartsWith("Arrival registration closed at 9:30 AM.", refused.Message);
                Assert.False(await db.StudentArrivals.AnyAsync(r => r.StudentId == student));
            }

            // Arrivals recorded before the cutoff keep their time and status after it.
            var after = (await At(db, day, 9, 45).ChecklistAsync(day, classId)).Rows.ToDictionary(r => r.StudentId);
            Assert.Equal((ArrivalOutcome.Present, new TimeOnly(7, 20)), (after[s[0]].Outcome!.Value, after[s[0]].Time!.Value));
            Assert.Equal(ArrivalOutcome.Late, after[s[1]].Outcome);

            // Without automatic absences (history before the rule) the cutoff still closes registration.
            Assert.True((await At(db, day, 10, 0, automatic: false).RegisterArrivalAsync(s[2], day, classId)).Closed);

            // A manual entry with a time at or after the cutoff is refused too.
            var manual = (await At(db, day, 12, 0).RecordAsync(day, [new ArrivalEntry(s[3], ArrivalStatus.Present, new TimeOnly(9, 30), null, null, null, false, null)])).Single();
            Assert.False(manual.Saved);
            Assert.Contains("closes at 9:30 AM", manual.Error);
        });
    }

    [Fact]
    public async Task The_cutoff_is_0930_Cairo_time_in_summer_too()
    {
        var day = new DateOnly(2023, 6, 6); // Tuesday, UTC+03:00
        var (classId, s) = await StudentsEnrolledIn2023Async(2);
        await using var db = fx.NewContext();
        await db.Settings.Where(x => x.Key == SettingsService.LateCutoffKey).ExecuteDeleteAsync();
        var before = new SchoolTimeService(new FixedClock(new DateTimeOffset(2023, 6, 6, 6, 29, 59, TimeSpan.Zero)), SchoolTimeService.DefaultTimeZoneId);
        var at = new SchoolTimeService(new FixedClock(new DateTimeOffset(2023, 6, 6, 6, 30, 0, TimeSpan.Zero)), SchoolTimeService.DefaultTimeZoneId);
        Assert.True((await DbFixture.SchoolAttendanceAt(db, before).RegisterArrivalAsync(s[0], day, classId)).Created);
        Assert.True((await DbFixture.SchoolAttendanceAt(db, at).RegisterArrivalAsync(s[1], day, classId)).Closed);
    }

    [Fact]
    public async Task Students_with_no_arrival_are_absent_after_the_cutoff_shown_at_once_and_stored_by_the_finalizer()
    {
        await WithRulesAsync(async () =>
        {
            var day = new DateOnly(2023, 2, 6); // Monday
            var (classId, s) = await StudentsEnrolledIn2023Async(3);
            await using var db = fx.NewContext();
            Assert.True((await At(db, day, 7, 30).RegisterArrivalAsync(s[0], day, classId)).Created);

            // Before the cutoff: registration open, nobody absent, nothing to finalize yet.
            var before = At(db, day, 9, 29);
            var open = await before.DayAsync(day);
            Assert.Equal((true, false, false), (open.RegistrationOpen, open.AutoAbsent, open.ClosedToday));
            var early = (await before.ChecklistAsync(day, classId)).Summary;
            Assert.Equal((1, 0, 2), (early.Present, early.Absent, early.NotRecorded));
            Assert.False((await before.FinalizeAsync(day)).Succeeded);

            // From 09:30: closed, the two are Absent everywhere at once (register, day summary) — before anything is stored.
            var after = At(db, day, 9, 30);
            var closed = await after.DayAsync(day);
            Assert.Equal((false, true, true), (closed.RegistrationOpen, closed.AutoAbsent, closed.ClosedToday));
            var list = await after.ChecklistAsync(day, classId);
            Assert.Equal((1, 2, 0), (list.Summary.Present, list.Summary.Absent, list.Summary.NotRecorded));
            Assert.All(list.Rows.Where(r => r.StudentId != s[0]), r => Assert.Equal((ArrivalOutcome.Absent, (int?)null), (r.Outcome!.Value, r.RecordId)));
            Assert.Equal(0, (await after.DaySummaryAsync(day)).NotRecorded);
            Assert.False(await db.StudentArrivals.AnyAsync(r => r.StudentId == s[1]));

            // The finalizer stores them: Absent, no time, the cutoff and the automatic recorder; the arrival is unchanged.
            var created = await after.FinalizeAsync(day);
            Ok(created);
            Assert.True(created.EntityId >= 2);
            var absent = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.StudentId == s[1] && r.ArrivalDate == day);
            Assert.Equal((ArrivalStatus.Absent, (TimeOnly?)null, (LateStatus?)null, Cutoff, Arrivals.AutomaticRecorder),
                (absent.Status, absent.ArrivalTime, absent.LateStatus, absent.CutoffUsed!.Value, absent.RecordedBy));
            Assert.Equal(new TimeOnly(7, 30), (await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.StudentId == s[0])).ArrivalTime);
            Assert.Equal(0, (await after.FinalizeAsync(day)).EntityId ?? 0); // idempotent
            Assert.Equal(1, await db.StudentArrivals.CountAsync(r => r.StudentId == s[1]));

            // Weekends never, future days never.
            Assert.Equal(0, (await after.FinalizeAsync(new DateOnly(2023, 2, 10))).EntityId ?? 0); // Friday
            Assert.False((await after.FinalizeAsync(day.AddDays(1))).Succeeded);
            Assert.False((await after.DayAsync(day.AddDays(1))).AutoAbsent);

            // The correction workflow stays available: an excused late arrival.
            Ok(await after.CorrectAsync(absent.Id, ArrivalOutcome.Late, new TimeOnly(9, 50), null, "Arrived 9:50 with a parent (doctor's appointment)"));
            var corrected = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.Id == absent.Id);
            Assert.Equal((ArrivalStatus.Present, LateStatus.Late, new TimeOnly(9, 50)), (corrected.Status, corrected.LateStatus!.Value, corrected.ArrivalTime!.Value));
            var trail = await db.StudentArrivalCorrections.AsNoTracking().SingleAsync(c => c.StudentArrivalRecordId == absent.Id);
            Assert.StartsWith("Absent", trail.Before);
            Assert.StartsWith("Late at 9:50 AM", trail.After);
        });
    }

    [Fact]
    public async Task Days_before_automatic_absences_started_keep_their_records()
    {
        var day = new DateOnly(2023, 2, 7); // Tuesday
        var (classId, s) = await StudentsEnrolledIn2023Async(1);
        await using var db = fx.NewContext();
        // Automatic absences start on 1 March 2023: 7 February is history and stays Not Recorded.
        var later = DbFixture.SchoolAttendanceWith(db, CairoAt(new DateOnly(2023, 3, 1), 11, 0), new AttendanceOptions(true, new DateOnly(2023, 3, 1)));
        Assert.False((await later.DayAsync(day)).AutoAbsent);
        Assert.Null((await later.ChecklistAsync(day, classId)).Rows.Single().Outcome);
        await later.FinalizeDueAsync();
        Assert.False(await db.StudentArrivals.AnyAsync(r => r.StudentId == s[0] && r.ArrivalDate == day));
    }

    [Fact]
    public async Task Three_consecutive_absences_raise_the_alert_including_automatic_ones()
    {
        var (classId, s) = await StudentsEnrolledIn2023Async(1);
        await using var db = fx.NewContext();
        DateOnly sun = new(2023, 2, 12), mon = new(2023, 2, 13), tue = new(2023, 2, 14);
        Assert.True((await At(db, sun, 8, 0).MarkAbsentAsync(s[0], sun, classId)).Created); // marked absent
        Ok(await At(db, mon, 10, 0).FinalizeAsync(mon));                                      // no arrival by 09:30
        Ok(await At(db, tue, 10, 0).FinalizeAsync(tue));
        var alert = await db.AttendanceAlerts.AsNoTracking().SingleAsync(a => a.StudentId == s[0] && a.Type == AttendanceAlertType.ConsecutiveAbsence);
        Assert.Equal((sun, tue, 3), (alert.FirstDate, alert.LastDate, alert.DayCount));
    }

    // ------------------------------------------------------------------ one click, duplicates

    [Fact]
    public async Task One_click_records_the_server_time_once()
    {
        await WithRulesAsync(async () =>
        {
            var day = new DateOnly(2023, 2, 19); // Sunday
            var (classId, s) = await StudentsEnrolledIn2023Async(2);
            await using var db = fx.NewContext();
            var first = await At(db, day, 7, 42, 10).RegisterArrivalAsync(s[0], day, classId);
            Assert.True(first.Created, first.Message);
            var record = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.StudentId == s[0]);
            Assert.Equal((ArrivalStatus.Present, new TimeOnly(7, 42, 10), LateStatus.Late), (record.Status, record.ArrivalTime!.Value, record.LateStatus!.Value));
            Assert.Equal((null, null, (int?)null), (record.Method, record.ArrivalLocation, record.ReceivedByStaffId));
            Assert.Equal("Test runner", record.RecordedBy);

            var again = await At(db, day, 8, 5).RegisterArrivalAsync(s[0], day, classId);
            Assert.False(again.Created);
            Assert.Contains("already recorded", again.Message);
            Assert.False((await At(db, day, 8, 6).MarkAbsentAsync(s[0], day, classId)).Created);
            Assert.Equal(1, await db.StudentArrivals.CountAsync(r => r.StudentId == s[0]));

            // A page left open from another day, a weekend, an unknown student: refused.
            Assert.False((await At(db, day, 7, 50).RegisterArrivalAsync(s[1], day.AddDays(-1), classId)).Created);
            Assert.False((await At(db, new DateOnly(2023, 2, 17), 8, 0).RegisterArrivalAsync(s[1], new DateOnly(2023, 2, 17), classId)).Created);
            Assert.False((await At(db, day, 7, 50).RegisterArrivalAsync(int.MaxValue, day, classId)).Created);
            Assert.False(await db.StudentArrivals.AnyAsync(r => r.StudentId == s[1]));
        });
    }

    // ------------------------------------------------------------------ history left as recorded

    [Fact]
    public async Task Existing_records_keep_the_outcome_they_were_recorded_with()
    {
        var student = (await fx.NewStudentAsync(await fx.NewClassAsync())).Id;
        await using var db = fx.NewContext();
        var classId = await db.Students.Where(x => x.Id == student).Select(x => x.SchoolClassId).SingleAsync();
        var legacyLate = new StudentArrivalRecord
        {
            StudentId = student, SchoolClassId = classId, ArrivalDate = SchoolWeek.Today.AddDays(-2), Status = ArrivalStatus.Present,
            ArrivalTime = new TimeOnly(8, 10), LateStatus = LateStatus.Late, DeadlineUsed = new TimeOnly(7, 40), Method = ArrivalMethod.SchoolBus,
            ArrivalLocation = "Main gate", RecordedBy = "Before the redesign",
        };
        var legacyAbsent = new StudentArrivalRecord
        {
            StudentId = student, SchoolClassId = classId, ArrivalDate = SchoolWeek.Today.AddDays(-1), Status = ArrivalStatus.Absent,
            ArrivalTime = new TimeOnly(9, 45), CutoffUsed = new TimeOnly(9, 30), RecordedBy = Arrivals.AutomaticRecorder,
        };
        db.StudentArrivals.AddRange(legacyLate, legacyAbsent);
        await db.SaveChangesAsync();

        var rows = (await DbFixture.Arrivals(db).StudentHistoryAsync(student, null, null, false)).Records;
        Assert.Equal([ArrivalOutcome.Late, ArrivalOutcome.Absent], rows.Select(r => r.Outcome!.Value));
        var stored = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.Id == legacyLate.Id);
        Assert.Equal((ArrivalMethod.SchoolBus, "Main gate"), (stored.Method!.Value, stored.ArrivalLocation));
    }

    // ------------------------------------------------------------------ Attendance History & Reports

    [Fact]
    public async Task Reports_count_school_days_only_and_apply_the_cutoff_rule()
    {
        await WithRulesAsync(async () =>
        {
            DateOnly sun = new(2023, 3, 12), mon = new(2023, 3, 13), tue = new(2023, 3, 14), sat = new(2023, 3, 18);
            var (classId, s) = await StudentsEnrolledIn2023Async(2);
            await using var db = fx.NewContext();
            // s[0]: Sun Present 07:30, Mon Late 09:10, Tue marked Absent, Wed–Thu nothing. Fri 17 + Sat 18 are the weekend.
            Assert.True((await At(db, sun, 7, 30).RegisterArrivalAsync(s[0], sun, classId)).Created);
            Assert.True((await At(db, mon, 9, 10).RegisterArrivalAsync(s[0], mon, classId)).Created);
            Assert.True((await At(db, tue, 9, 0).MarkAbsentAsync(s[0], tue, classId)).Created);

            // Without the automatic rule (history): days with no record are Not Recorded.
            var plain = new AttendanceReportService(db);
            var r = (await plain.StudentAsync(s[0], sun, sat))!;
            Assert.Equal(5, r.Days.Count);
            Assert.Equal((1, 1, 1, 2), (r.Counts.Present, r.Counts.Late, r.Counts.Absent, r.Counts.NotRecorded));
            Assert.Equal(66.7, r.Counts.Percent);
            Assert.Equal([sun, mon, tue, new DateOnly(2023, 3, 15), new DateOnly(2023, 3, 16)], r.Days.Select(d => d.Date));

            // With the cutoff rule (as in the app): those days are Absent, as the register shows them.
            var monday20 = new DateOnly(2023, 3, 20);
            var withRule = new AttendanceReportService(db, CairoAt(monday20, 12, 0), At(db, monday20, 12, 0));
            var rr = (await withRule.StudentAsync(s[0], sun, sat))!;
            Assert.Equal((1, 1, 3, 0), (rr.Counts.Present, rr.Counts.Late, rr.Counts.Absent, rr.Counts.NotRecorded));
            var g = await withRule.GroupAsync(classId, null, sun, sat, null);
            Assert.Equal(("class", 5, 2), (g.Scope, g.SchoolDays, g.Students));
            var other = g.StudentRows.Single(x => x.StudentId == s[1]).Counts;
            Assert.Equal((0, 5, 0), (other.Present, other.Absent, other.NotRecorded));

            // One date + status filter; a weekend-only period counts nothing.
            var monday = (await plain.GroupAsync(classId, null, mon, mon, "late")).StudentRows.Single();
            Assert.Equal((s[0], ArrivalOutcome.Late, new TimeOnly(9, 10)), (monday.StudentId, monday.DayOutcome!.Value, monday.DayTime!.Value));
            Assert.Equal(0, (await plain.GroupAsync(classId, null, new DateOnly(2023, 3, 17), sat, null)).Counts.Days);
        });
    }

    [Fact]
    public async Task History_pages_and_PDF_and_Excel_exports_work_for_student_class_and_school()
    {
        var mon = new DateOnly(2023, 3, 27);
        var (classId, s) = await StudentsEnrolledIn2023Async(1);
        await using (var db = fx.NewContext())
        {
            Assert.True((await At(db, mon, 9, 15).RegisterArrivalAsync(s[0], mon, classId)).Created);
        }
        var client = fx.Factory.CreateClient();
        var range = "period=range&from=2023-03-26&to=2023-04-01";
        foreach (var q in new[] { $"mode=student&studentId={s[0]}&{range}", $"mode=class&classId={classId}&{range}", "mode=school&period=day&date=2023-03-27", "mode=grade&grade=KG1&" + range })
        {
            var page = await client.GetAsync($"/arrivals/history?{q}");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("Attendance History & Reports", WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));
            var pdf = await client.GetAsync($"/arrivals/history/pdf?{q}");
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            using var wb = new XLWorkbook(await (await client.GetAsync($"/arrivals/history/excel?{q}")).Content.ReadAsStreamAsync());
            Assert.Equal("Summary", wb.Worksheets.First().Name);
        }
        var student = WebUtility.HtmlDecode(await client.GetStringAsync($"/arrivals/history?mode=student&studentId={s[0]}&{range}"));
        foreach (var part in new[] { "9:15 AM", "School days" }) Assert.Contains(part, student);
        using var book = new XLWorkbook(await (await client.GetAsync($"/arrivals/history/excel?mode=student&studentId={s[0]}&{range}")).Content.ReadAsStreamAsync());
        Assert.Equal(5, book.Worksheet("Daily attendance").RowsUsed().Count() - 1); // five school days, the weekend left out
    }

    // ------------------------------------------------------------------ the page itself

    [Fact]
    public async Task The_daily_page_has_no_manual_time_method_location_or_receiver_and_shows_the_cutoff()
    {
        var (classId, _) = await StudentsEnrolledIn2023Async(1);
        var client = fx.Factory.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/arrivals?classId={classId}"));
        foreach (var gone in new[] { "Arrival location", "Received by", "Receiving confirmed", "Default method", "Save entries", "type=\"time\"" })
            Assert.DoesNotContain(gone, html);
        Assert.Contains("Registration closes 9:30 AM", html);
        var now = TimeOnly.FromDateTime(SchoolTime.Current.Now);
        await using var db = fx.NewContext();
        if ((await SchoolDays.LoadAsync(db, default)).IsSchoolDay(SchoolWeek.Today))
        {
            if (now >= Cutoff)
            {
                Assert.Contains("Arrival registration closed at 9:30 AM.", html);
                Assert.DoesNotContain("data-att-arrive", html);
            }
            else Assert.Contains("data-att-arrive", html);
        }
        Assert.Contains("Choose a class to start", await client.GetStringAsync("/arrivals"));
    }
}
