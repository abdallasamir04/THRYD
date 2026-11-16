using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>The new pages render through the running app, and the arrival JSON endpoint enforces its rules.</summary>
[Collection("db")]
public partial class OperationsHttpTests(DbFixture fx)
{
    [Theory]
    [InlineData("/duties/audit")]
    [InlineData("/duties/audit?status=not-covered&type=Morning&component=students-arrival&followUp=true")]
    [InlineData("/staff")]
    [InlineData("/arrivals")]
    [InlineData("/arrivals?classId=all")]
    [InlineData("/arrivals?show=late")]
    [InlineData("/arrivals/history")]
    [InlineData("/arrivals/history?mode=school&period=range")]
    [InlineData("/settings")]
    [InlineData("/")]
    public async Task New_pages_render(string url)
    {
        var response = await fx.Factory.CreateClient().GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}\n{html[..Math.Min(2000, html.Length)]}");
    }

    [Fact]
    public async Task Profiles_forms_and_details_render()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var classId = await fx.NewClassAsync();
        var (student, _) = await fx.NewStudentAsync(classId);
        int dutyId, assignmentId, recordId;
        await using (var db = fx.NewContext())
        {
            dutyId = (await DbFixture.Duties(db).CreateAsync(new(DutyComponent.StudentsArrival, SchoolWeek.Today.AddDays(-1), "Gate",
                new TimeOnly(7, 0), new TimeOnly(7, 45), "Gate", primary, AttendanceStatus.Present, null, null, []))).EntityId!.Value;
            assignmentId = (await DbFixture.Duties(db).StaffAsync(dutyId)).Single().Id;
            await DbFixture.Arrivals(db).RecordAsync(SchoolWeek.Today, [new(student, ArrivalStatus.Present, new TimeOnly(7, 40), ArrivalMethod.ParentDropOff, null, null, false, "Brought by father")]);
            recordId = (await db.StudentArrivals.SingleAsync(r => r.StudentId == student)).Id;
        }

        var client = fx.Factory.CreateClient();
        foreach (var url in new[]
        {
            $"/staff/{primary}", $"/students/profile/{student}", $"/students/profile/{student}?from=2020-01-01&order=oldest",
            $"/duties/{dutyId}", $"/duties/{dutyId}/audit-record", $"/staff/assignments/{assignmentId}/evaluate",
            $"/arrivals/{recordId}/correct", $"/arrivals?classId={classId}", $"/teachers/details/{primary}",
        })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}");
        }

        var profile = await client.GetStringAsync($"/students/profile/{student}");
        Assert.Contains("Brought by father", profile);
        Assert.Contains("Arrival &amp; attendance history", profile);
    }

    [Fact]
    public async Task One_click_arrival_saves_once_with_the_server_time_and_needs_the_token()
    {
        var classId = await fx.NewClassAsync();
        var (student, _) = await fx.NewStudentAsync(classId);
        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync($"/arrivals?classId={classId}");
        var token = TokenPattern().Match(page).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));

        HttpRequestMessage Request(string action, bool withToken = true)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, $"/arrivals/{action}")
            {
                // Only the student: no time, method, location or receiver is ever sent.
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["studentId"] = student.ToString(), ["date"] = SchoolWeek.Iso(SchoolWeek.Today), ["classId"] = classId.ToString(),
                }),
            };
            r.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (withToken) r.Headers.Add("RequestVerificationToken", token);
            return r;
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request("register", withToken: false))).StatusCode);

        await using var db = fx.NewContext();
        var schoolDay = (await SchoolDays.LoadAsync(db, default)).IsSchoolDay(SchoolWeek.Today);
        var cutoff = await DbFixture.Settings(db).GetLateCutoffAsync();
        var before = SchoolTime.Current.Now;
        var first = await client.SendAsync(Request("register"));
        var body = await first.Content.ReadAsStringAsync();
        if (!schoolDay || TimeOnly.FromDateTime(SchoolTime.Current.Now) >= cutoff)
        {
            // Weekends and holidays take no attendance; from the cutoff (09:30) a direct request is refused on the server too.
            Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
            if (schoolDay) Assert.Contains("\"closed\":true", body);
            Assert.False(await db.StudentArrivals.AnyAsync(r => r.StudentId == student));
            return;
        }
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"success\":true", body);
        Assert.Contains("\"notRecorded\":0", body);

        var record = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.StudentId == student);
        var start = await DbFixture.Settings(db).GetArrivalDeadlineAsync();
        Assert.Equal(ArrivalStatus.Present, record.Status);
        Assert.InRange(record.ArrivalTime!.Value, TimeOnly.FromDateTime(before).Add(TimeSpan.FromSeconds(-1)), TimeOnly.FromDateTime(SchoolTime.Current.Now).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(start is { } st && record.ArrivalTime > st ? LateStatus.Late : LateStatus.OnTime, record.LateStatus);
        Assert.Null(record.Method);
        Assert.Null(record.ArrivalLocation);
        Assert.Null(record.ReceivedByStaffId);

        // A second click (or another device) never records the student again, and Absent is refused too.
        var second = await client.SendAsync(Request("register"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already recorded", await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Request("absent"))).StatusCode);
        Assert.Equal(1, await db.StudentArrivals.CountAsync(r => r.StudentId == student));
        Assert.Equal(record.ArrivalTime, (await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.StudentId == student)).ArrivalTime);
    }

    [Fact]
    public async Task Dashboard_shows_the_counts_from_the_database()
    {
        await using var db = fx.NewContext();
        var html = await fx.Factory.CreateClient().GetStringAsync("/");
        var summary = await DbFixture.Arrivals(db).DaySummaryAsync(SchoolWeek.Today);
        var counts = await DbFixture.Duties(db).StatusCountsAsync(SchoolWeek.Today);
        Assert.Contains($"\">{summary.Absent}</span><span class=\"ops-tile__label\">Absent today", html);
        Assert.Contains($"\">{summary.Late}</span><span class=\"ops-tile__label\">Late today", html);
        Assert.Contains($"\">{summary.Present}</span><span class=\"ops-tile__label\">Present today", html);
        // Removed categories: no "not yet recorded" tile and no "without a valid class" card.
        Assert.DoesNotContain("not yet recorded", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("without a valid class", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\">{counts[DutyStatus.Covered]}</span><span class=\"ops-tile__label\">Duties covered", html);
        Assert.Contains($"\">{counts[DutyStatus.Pending]}</span><span class=\"ops-tile__label\">Duties pending", html);
        // No individual feedback text on the general dashboard.
        Assert.DoesNotContain("Absent without notice", html);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
