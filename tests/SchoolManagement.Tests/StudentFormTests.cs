using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace SchoolManagement.Tests;

/// <summary>The Add Student form, opened from a class or from the general Students page, saves through the real HTTP flow.</summary>
[Collection("db")]
public partial class StudentFormTests(DbFixture fx)
{
    private static HttpRequestMessage Ajax(HttpMethod method, string url, HttpContent? content = null)
    {
        var r = new HttpRequestMessage(method, url) { Content = content };
        r.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return r;
    }

    private static string Token(string html) => TokenPattern().Match(html).Groups[1].Value;

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    [Fact]
    public async Task General_form_shows_the_class_dropdown_and_saves()
    {
        var classId = await fx.NewClassAsync();
        var client = fx.Factory.CreateClient();
        var (status, html) = await SendAsync(client, Ajax(HttpMethod.Get, "/students/create"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("<select", html);
        Assert.Contains("name=\"SchoolClassId\"", html);
        // The live duplicate check points at an action that exists (it used to 404 and block the form).
        Assert.Contains("data-val-remote-url=\"/students/checkduplicate\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Students/CheckDuplicate?FullName=x&SchoolClassId={classId}&Id=")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Students/CheckDuplicate?FullName=x&SchoolClassId=&Id=")).StatusCode);

        var name = "Student " + DbFixture.Unique();
        var (postStatus, body) = await SendAsync(client, Ajax(HttpMethod.Post, "/Students/Create",
            Form(("__RequestVerificationToken", Token(html)), ("FullName", name), ("SchoolClassId", classId.ToString()))));
        Assert.True(postStatus == HttpStatusCode.OK, body);
        Assert.Contains("\"success\":true", body);

        await using var db = fx.NewContext();
        Assert.Equal(classId, (await db.Students.AsNoTracking().SingleAsync(s => s.FullName == name)).SchoolClassId);
    }

    [Fact]
    public async Task Class_form_has_no_dropdown_and_saves_to_the_class_from_the_route()
    {
        var classId = await fx.NewClassAsync();
        var otherClass = await fx.NewClassAsync();
        var client = fx.Factory.CreateClient();
        var (status, html) = await SendAsync(client, Ajax(HttpMethod.Get, $"/classes/{classId}/students/create"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("<select", html);
        Assert.Contains($"action=\"/classes/{classId}/students/create\"", html);

        // Even a tampered class field cannot move the student: the route decides.
        var name = "Student " + DbFixture.Unique();
        var (postStatus, body) = await SendAsync(client, Ajax(HttpMethod.Post, $"/classes/{classId}/students/create",
            Form(("__RequestVerificationToken", Token(html)), ("FullName", name), ("SchoolClassId", otherClass.ToString()))));
        Assert.True(postStatus == HttpStatusCode.OK, body);
        Assert.Contains("\"success\":true", body);
        await using (var db = fx.NewContext())
            Assert.Equal(classId, (await db.Students.AsNoTracking().SingleAsync(s => s.FullName == name)).SchoolClassId);

        // The class roster shows the new student.
        Assert.Contains(name, await client.GetStringAsync($"/students?classId={classId}"));

        // Invalid data: the form comes back (422) with clear messages, nothing saved.
        var (badStatus, badBody) = await SendAsync(client, Ajax(HttpMethod.Post, $"/classes/{classId}/students/create",
            Form(("__RequestVerificationToken", Token(html)), ("FullName", ""))));
        Assert.Equal((HttpStatusCode)422, badStatus);
        Assert.Contains("Enter the student", badBody);
        Assert.DoesNotContain("<select", badBody);

        // Duplicate name in the same class.
        var (dupStatus, dupBody) = await SendAsync(client, Ajax(HttpMethod.Post, $"/classes/{classId}/students/create",
            Form(("__RequestVerificationToken", Token(html)), ("FullName", name))));
        Assert.Equal((HttpStatusCode)422, dupStatus);
        Assert.Contains("already in", dupBody);

        // Without the anti-forgery token the request is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Ajax(HttpMethod.Post, $"/classes/{classId}/students/create",
            Form(("FullName", "Student " + DbFixture.Unique()))))).StatusCode);
    }

    [Fact]
    public async Task A_full_class_is_rejected_on_the_server()
    {
        int classId;
        await using (var db = fx.NewContext())
            classId = (await DbFixture.Classes(db).CreateAsync("F" + DbFixture.Unique()[..8], null, 1, true)).EntityId!.Value;
        await fx.NewStudentAsync(classId); // fills the only place

        var client = fx.Factory.CreateClient();
        var (_, html) = await SendAsync(client, Ajax(HttpMethod.Get, $"/classes/{classId}/students/create"));
        Assert.Contains("is full", html);
        var name = "Student " + DbFixture.Unique();
        var (status, body) = await SendAsync(client, Ajax(HttpMethod.Post, $"/classes/{classId}/students/create",
            Form(("__RequestVerificationToken", Token(html)), ("FullName", name))));
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Contains("is full (1/1 students)", body);

        await using var check = fx.NewContext();
        Assert.False(await check.Students.AnyAsync(s => s.FullName == name));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
