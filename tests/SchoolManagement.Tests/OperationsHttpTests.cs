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
    [InlineData("/arrivals?show=late")]
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
    public async Task Arrival_checklist_saves_json_once_per_student_and_needs_the_token()
    {
        var classId = await fx.NewClassAsync();
        var (student, _) = await fx.NewStudentAsync(classId);
        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync($"/arrivals?classId={classId}");
        var token = TokenPattern().Match(page).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));

        HttpRequestMessage Request(bool withToken)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/arrivals/save")
            {
                Content = JsonContent.Create(new
                {
                    date = SchoolWeek.Iso(SchoolWeek.Today),
                    entries = new[] { new { studentId = student, status = 1, time = "07:31:15", method = 3, notes = "via http" } },
                }),
            };
            r.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (withToken) r.Headers.Add("RequestVerificationToken", token);
            return r;
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request(withToken: false))).StatusCode);

        var first = await client.SendAsync(Request(withToken: true));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"saved\":1", await first.Content.ReadAsStringAsync());

        var second = await (await client.SendAsync(Request(withToken: true))).Content.ReadAsStringAsync();
        Assert.Contains("\"saved\":0", second);
        Assert.Contains("Already recorded", second);

        await using var db = fx.NewContext();
        var record = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.StudentId == student);
        Assert.Equal((new TimeOnly(7, 31, 15), ArrivalMethod.ParentDropOff), (record.ArrivalTime!.Value, record.Method!.Value));
    }

    [Fact]
    public async Task Dashboard_shows_the_counts_from_the_database()
    {
        await using var db = fx.NewContext();
        var html = await fx.Factory.CreateClient().GetStringAsync("/");
        var summary = await DbFixture.Arrivals(db).DaySummaryAsync(SchoolWeek.Today);
        var counts = await DbFixture.Duties(db).StatusCountsAsync(SchoolWeek.Today);
        Assert.Contains($"\">{summary.NotRecorded}</span><span class=\"ops-tile__label\">Students not yet recorded", html);
        Assert.Contains($"\">{summary.Late}</span><span class=\"ops-tile__label\">Late arrivals today", html);
        Assert.Contains($"\">{counts[DutyStatus.Covered]}</span><span class=\"ops-tile__label\">Duties covered", html);
        Assert.Contains($"\">{counts[DutyStatus.Pending]}</span><span class=\"ops-tile__label\">Duties pending", html);
        // No individual feedback text on the general dashboard.
        Assert.DoesNotContain("Absent without notice", html);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
