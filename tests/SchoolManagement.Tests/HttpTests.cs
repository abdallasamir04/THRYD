using System.Net;
using System.Text.RegularExpressions;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Through the running app: every page renders, and direct HTTP posts cannot bypass the rules.</summary>
[Collection("db")]
public partial class HttpTests(DbFixture fx)
{
    private static readonly DateOnly Week = new(2030, 3, 3); // a Sunday

    [Theory]
    [InlineData("/")]
    [InlineData("/students")]
    [InlineData("/teachers")]
    [InlineData("/classes")]
    [InlineData("/classes?view=table")]
    [InlineData("/import-export")]
    [InlineData("/import-export?kind=Teachers")]
    [InlineData("/settings")]
    [InlineData("/duties")]
    [InlineData("/duties/morning/students-arrival")]
    [InlineData("/duties/morning/in-class-morning")]
    [InlineData("/duties/morning/morning-assembly")]
    [InlineData("/duties/create?component=in-class-morning")]
    [InlineData("/workload")]
    [InlineData("/workload?status=capacity&category=office")]
    [InlineData("/workload/create")]
    [InlineData("/students/create")]
    [InlineData("/teachers/create")]
    [InlineData("/classes/create")]
    public async Task Pages_render(string url)
    {
        var client = fx.Factory.CreateClient();
        var response = await client.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
    }

    [Fact]
    public async Task Teacher_pages_and_exports_render()
    {
        var (id, _) = await fx.NewTeacherAsync();
        var client = fx.Factory.CreateClient();
        foreach (var url in new[] { $"/workload/teacher/{id}", $"/teachers/details/{id}", "/workload/export", "/students/export", "/teachers/export", "/import-export/template/Students", "/import-export/template/Teachers" })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Morning_duty_page_shows_exactly_three_components()
    {
        var html = await fx.Factory.CreateClient().GetStringAsync("/duties?type=morning");
        Assert.Equal(3, Regex.Matches(html, "class=\"duty-component\"").Count);
        Assert.Contains("Students Arrival Check", html);
        Assert.Contains("In-Class Morning", html);
        Assert.Contains("Morning Assembly (Line) / Proadcast", html);
        Assert.DoesNotContain("broadcast", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direct_http_posts_cannot_exceed_24_periods()
    {
        var (id, _) = await fx.NewTeacherAsync();
        var client = fx.Factory.CreateClient();
        var token = await TokenAsync(client, $"/workload/create?teacherId={id}");

        async Task<HttpResponseMessage> Post(int periods, string category = "Office")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/workload/create")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["TeacherId"] = id.ToString(),
                    ["Week"] = Week.ToString("yyyy-MM-dd"),
                    ["Category"] = category,
                    ["Periods"] = periods.ToString(),
                }),
            };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.OK, (await Post(23)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(1, "Substitution")).StatusCode);   // 24/24

        var blocked = await Post(1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("already 24/24 periods", WebUtility.HtmlDecode(await blocked.Content.ReadAsStringAsync()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(25)).StatusCode);   // over the per-row limit too
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(-5)).StatusCode);

        await using var db = fx.NewContext();
        var s = await DbFixture.Workload(db).SummaryAsync(id, Week);
        Assert.Equal(24, s!.Total);
    }

    [Fact]
    public async Task Posts_without_the_antiforgery_token_are_rejected()
    {
        var (id, _) = await fx.NewTeacherAsync();
        var response = await fx.Factory.CreateClient().PostAsync("/workload/create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["TeacherId"] = id.ToString(), ["Week"] = Week.ToString("yyyy-MM-dd"), ["Category"] = "Office", ["Periods"] = "1",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duty_backup_workflow_through_http()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var (backup, _) = await fx.NewTeacherAsync();
        var client = fx.Factory.CreateClient();
        var token = await TokenAsync(client, "/duties/create?component=students-arrival");

        async Task<HttpResponseMessage> Post(string url, Dictionary<string, string> form)
        {
            form["__RequestVerificationToken"] = token;
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return await client.SendAsync(request);
        }

        var day = "2030-03-04";
        var baseForm = new Dictionary<string, string>
        {
            ["Component"] = "StudentsArrival", ["DutyDate"] = day, ["Description"] = "Gate", ["StartTime"] = "07:15", ["EndTime"] = "08:00",
            ["PrimaryStaffId"] = primary.ToString(), ["PrimaryAttendance"] = "Absent", ["BackupStaffId"] = primary.ToString(),
        };
        // Same person as primary and backup → refused.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post("/duties/create", new(baseForm))).StatusCode);

        baseForm["BackupStaffId"] = backup.ToString();
        Assert.Equal(HttpStatusCode.OK, (await Post("/duties/create", new(baseForm))).StatusCode);

        await using var db = fx.NewContext();
        var duty = (await DbFixture.Duties(db).ListAsync(DutyComponent.StudentsArrival, DateOnly.Parse(day))).Single(d => d.PrimaryStaffId == primary);
        Assert.Equal(CoverageState.AwaitingBackupResponse, duty.Coverage);

        // Backup attendance before acceptance → refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Post($"/duties/{duty.Id}/backup-attendance", new() { ["status"] = "Present" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/duties/{duty.Id}/backup-response", new() { ["accepted"] = "true" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/duties/{duty.Id}/backup-attendance", new() { ["status"] = "Present" })).StatusCode);

        await using var check = fx.NewContext();
        Assert.Equal(CoverageState.CoveredByBackup, (await DbFixture.Duties(check).GetAsync(duty.Id))!.Coverage);
    }

    private static async Task<string> TokenAsync(HttpClient client, string formUrl)
    {
        var html = await client.GetStringAsync(formUrl);
        var match = TokenPattern().Match(html);
        Assert.True(match.Success, "No antiforgery token on " + formUrl);
        return match.Groups[1].Value;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
