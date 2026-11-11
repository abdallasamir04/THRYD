using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Database-managed subcategories (Second Language languages), position history, and the timetable forms over HTTP.</summary>
[Collection("db")]
public partial class StaffManagementTests(DbFixture fx)
{
    private const int SecondLanguage = 13;

    [Fact]
    public async Task Languages_are_stored_in_the_database_and_can_be_added_renamed_and_deactivated()
    {
        await using var db = fx.NewContext();
        var depts = DbFixture.Departments(db);
        var teachers = DbFixture.Teachers(db);
        var lang = "Lang" + DbFixture.Unique()[..5];

        var initial = (await DepartmentCatalog.ForAsync(db, SecondLanguage))!;
        Assert.Contains("French", initial.Options.Select(o => o.Value));
        Assert.Contains("Deutsch", initial.Options.Select(o => o.Value));
        Assert.Equal("Language", initial.Label);

        var added = await depts.AddSubcategoryAsync(SecondLanguage, null, lang);
        Assert.True(added.Succeeded);
        Assert.False((await depts.AddSubcategoryAsync(SecondLanguage, null, lang.ToUpperInvariant())).Succeeded); // no duplicates

        // Available at once for staff (no code change, no migration).
        var t = await teachers.CreateAsync("Teacher " + DbFixture.Unique(), null, null, SecondLanguage, lang, null);
        Assert.True(t.Succeeded, string.Join("; ", t.Errors.Select(e => e.Value)));
        var id = t.EntityId!.Value;

        // Renaming carries over to staff records.
        var renamed = lang + "X";
        Assert.True((await depts.UpdateSubcategoryAsync(added.EntityId!.Value, renamed, true)).Succeeded);
        Assert.Equal(renamed, (await teachers.GetAsync(id))!.Subcategory);

        // Deactivated: not offered to new staff, kept by the existing member when unchanged.
        Assert.True((await depts.UpdateSubcategoryAsync(added.EntityId!.Value, renamed, false)).Succeeded);
        Assert.DoesNotContain(renamed, (await DepartmentCatalog.ForAsync(db, SecondLanguage))!.Options.Select(o => o.Value));
        Assert.False((await teachers.CreateAsync("Teacher " + DbFixture.Unique(), null, null, SecondLanguage, renamed, null)).Succeeded);
        await using var fresh = fx.NewContext(); // a new request: no cached copy of the record from before the rename
        var row = (await DbFixture.Teachers(fresh).GetAsync(id))!;
        var kept = await DbFixture.Teachers(fresh).UpdateAsync(id, row.FullName, "Language teacher", null, SecondLanguage, renamed, null);
        Assert.True(kept.Succeeded, string.Join("; ", kept.Errors.Select(e => e.Value)));

        var items = await depts.SubcategoriesAsync(SecondLanguage);
        Assert.Contains(items, i => i.Name == renamed && !i.IsActive && i.MemberCount == 1);
    }

    [Fact]
    public async Task Reassigning_a_position_keeps_the_previous_holder_in_the_history()
    {
        await using var db = fx.NewContext();
        var mgmt = DbFixture.Management(db);
        var a = (await mgmt.CreateAsync(new ManagementMemberInput { FullName = "Head " + DbFixture.Unique(), Position = ManagementPosition.HighSchoolCoordinator })).EntityId!.Value;
        var b = (await mgmt.CreateAsync(new ManagementMemberInput { FullName = "Head " + DbFixture.Unique() })).EntityId!.Value;
        try
        {
            var r = await mgmt.ReassignPositionAsync(ManagementPosition.HighSchoolCoordinator, b, SchoolWeek.Today);
            Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
            Assert.Null((await mgmt.GetAsync(a))!.Position);
            Assert.Equal(ManagementPosition.HighSchoolCoordinator, (await mgmt.GetAsync(b))!.Position);

            var history = await mgmt.PositionHistoryAsync(null);
            var old = history.Single(h => h.MemberId == a);
            Assert.False(old.IsCurrent);
            Assert.Contains("Replaced by", old.EndReason);
            Assert.True(history.Single(h => h.MemberId == b).IsCurrent);
            Assert.Equal(1, await db.PositionAssignments.CountAsync(p => p.Position == ManagementPosition.HighSchoolCoordinator && p.IsCurrent));
            Assert.False((await mgmt.ReassignPositionAsync(ManagementPosition.HighSchoolCoordinator, b, SchoolWeek.Today)).Succeeded);
        }
        finally
        {
            Assert.True((await DbFixture.Management(fx.NewContext()).ReassignPositionAsync(ManagementPosition.HighSchoolCoordinator, null, SchoolWeek.Today)).Succeeded); // vacate for other tests
        }
    }

    [Fact]
    public async Task Timetable_forms_assign_detect_conflicts_and_show_in_both_views_over_http()
    {
        var classA = await fx.NewClassAsync();
        var classB = await fx.NewClassAsync();
        int teacher;
        await using (var db = fx.NewContext())
            teacher = (await DbFixture.Teachers(db).CreateAsync("Teacher " + DbFixture.Unique(), "Teacher", null, 3, null, null)).EntityId!.Value;

        var client = fx.Factory.CreateClient();
        var form = await client.GetStringAsync($"/timetable/assign?classId={classA}&day=Wednesday&slotId=10");
        Assert.Contains("name=\"ClassId\" value=\"" + classA, form); // inherited from the clicked cell
        Assert.Contains("name=\"Day\" value=\"Wednesday\"", form);
        var token = TokenPattern().Match(form).Groups[1].Value;

        // Candidates JSON: workload + availability.
        var json = JsonDocument.Parse(await client.GetStringAsync("/timetable/candidates?departmentId=3&subject=Math&day=Wednesday&slotId=10"));
        Assert.Contains(json.RootElement.EnumerateArray(), c => c.GetProperty("id").GetInt32() == teacher && c.GetProperty("available").GetBoolean());

        async Task<HttpResponseMessage> Post(string url, Dictionary<string, string> data)
        {
            data["__RequestVerificationToken"] = token;
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(data) };
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return await client.SendAsync(req);
        }

        Dictionary<string, string> Session(int cls) => new()
        {
            ["ClassId"] = cls.ToString(), ["Day"] = "Wednesday", ["SlotId"] = "10", ["DepartmentId"] = "3", ["Subject"] = "Math", ["TeacherId"] = teacher.ToString(),
        };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post("/timetable/sessions", new() { ["ClassId"] = classA.ToString(), ["Day"] = "Wednesday", ["SlotId"] = "10" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("/timetable/sessions", Session(classA))).StatusCode);
        var conflict = await Post("/timetable/sessions", Session(classB));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Contains("already teaches", WebUtility.HtmlDecode(await conflict.Content.ReadAsStringAsync()));

        var classPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/timetable/classes/{classA}"));
        var teacherPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/timetable/teachers/{teacher}"));
        Assert.Contains("tt-cell--filled", classPage);
        Assert.Contains("tt-cell--filled", teacherPage);
        Assert.Contains("aria-valuenow=\"1\"", teacherPage); // the workload meter counts the session

        // The occupied slot opens the session panel; edit and remove work.
        var panel = await client.GetStringAsync($"/timetable/assign?classId={classA}&day=Wednesday&slotId=10");
        Assert.Contains("Edit session", panel);
        int sessionId;
        await using (var db = fx.NewContext())
            sessionId = await db.TimetableAssignments.Where(t => t.SchoolClassId == classA && t.EndedOn == null).Select(t => t.Id).SingleAsync();
        Assert.Equal(HttpStatusCode.OK, (await Post($"/timetable/sessions/{sessionId}/edit", Session(classA).Concat(new Dictionary<string, string> { ["Notes"] = "Room 4" }).ToDictionary())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/timetable/sessions/{sessionId}/remove", [])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post($"/timetable/sessions/{sessionId}/remove", [])).StatusCode);

        // Time slot form: invalid times are refused, valid ones saved (then cleared again).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post("/timetable/slots/10/edit", new() { ["StartTime"] = "14:00", ["EndTime"] = "13:00", ["IsActive"] = "true" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("/timetable/slots/10/edit", new() { ["Name"] = "Last period", ["StartTime"] = "14:00", ["EndTime"] = "14:40", ["IsActive"] = "true" })).StatusCode);
        Assert.Contains("value=\"14:00\"", await client.GetStringAsync("/timetable/slots/10/edit"));
        Assert.Equal(HttpStatusCode.OK, (await Post("/timetable/slots/10/edit", new() { ["StartTime"] = "", ["EndTime"] = "", ["IsActive"] = "true" })).StatusCode);

        // Language form and position form.
        var lang = "Lang" + DbFixture.Unique()[..5];
        Assert.Equal(HttpStatusCode.OK, (await Post("/staff/departments/13/subcategories/create", new() { ["Name"] = lang })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post("/staff/departments/13/subcategories/create", new() { ["Name"] = "" })).StatusCode);
        Assert.Contains($">{lang}<", await client.GetStringAsync("/staff/create?departmentId=13"));
    }

    [System.Text.RegularExpressions.GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial System.Text.RegularExpressions.Regex TokenPattern();
}
