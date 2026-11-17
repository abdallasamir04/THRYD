using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Duties → Activities: Duties shows Morning / Midday / Dismissal / Activities; Activities shows P.E. (the existing module),
/// Add Activity (always) and one card per activity from the database; each activity has its own management page.
/// </summary>
[Collection("db")]
public class ActivityTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static ActivityInput Input(string name, string description = "Painting and drawing after school.", bool active = true,
        IReadOnlyList<int>? staff = null, IReadOnlyList<int>? classes = null) =>
        new(name, description, "Arts", "sparkles", "rose", null, "Art room", active, staff, classes);

    private static void Ok(ServiceResult r) => Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));

    private static string Name(string prefix) => $"{prefix} {DbFixture.Unique()}";

    private static async Task<HttpResponseMessage> PostForm(HttpClient client, string formUrl, string action, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(formUrl);
        fields["__RequestVerificationToken"] = Token.Match(page).Groups[1].Value;
        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }

    // ------------------------------------------------------------------ 1–7 navigation

    [Fact]
    public async Task Duties_leads_to_Activities_which_holds_PE_and_Add_Activity()
    {
        var client = fx.Factory.CreateClient();
        var duties = WebUtility.HtmlDecode(await client.GetStringAsync("/duties"));
        foreach (var title in new[] { "Morning Duty", "Midday Duty", "Dismissal Duty", "Activities" })
            Assert.Matches($"<h2[^>]*><a href=\"[^\"]+\">{System.Text.RegularExpressions.Regex.Escape(title)}</a></h2>", duties);
        Assert.DoesNotContain("<span class=\"hub-card__title\">P.E.</span>", duties);
        Assert.DoesNotContain("Add Activity", duties);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/duties/activities"));
        Assert.Contains("<h1 class=\"page-title\">Activities</h1>", page);
        Assert.Matches("<a class=\"hub-card hub-card--green\" href=\"/pe\\?date=[0-9-]+\">", page); // the existing P.E. module
        Assert.Contains("<span class=\"hub-card__title\">P.E.</span>", page);
        Assert.Contains("<a class=\"hub-card hub-card--add\" href=\"/duties/activities/new\">", page);
        Assert.Contains("Create and manage a new school activity.", page);

        // P.E. still opens its own, unchanged module, now under Duties › Activities.
        var pe = WebUtility.HtmlDecode(await client.GetStringAsync("/pe"));
        Assert.Contains("<h1 class=\"page-title\">P.E. Timetable</h1>", pe);
        Assert.Contains("href=\"/duties/activities?date=", pe);
        Assert.Contains("Activity name", WebUtility.HtmlDecode(await client.GetStringAsync("/duties/activities/new")));
    }

    // ------------------------------------------------------------------ 8–11 create through the form

    [Fact]
    public async Task A_created_activity_is_saved_and_appears_as_a_card_with_Add_Activity_still_there()
    {
        var client = fx.Factory.CreateClient();
        var name = Name("Art Club");
        var response = await PostForm(client, "/duties/activities/new", "/duties/activities/new", new()
        {
            ["Name"] = name, ["Description"] = "Painting and drawing after school.", ["Category"] = "Arts", ["Icon"] = "sparkles", ["Color"] = "rose",
            ["Location"] = "Art room", ["IsActive"] = "true",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // redirected to the Activities page
        Assert.EndsWith("/duties/activities", response.RequestMessage!.RequestUri!.AbsolutePath);

        await using var db = fx.NewContext();
        var saved = await db.Activities.AsNoTracking().SingleAsync(a => a.Name == name);
        Assert.Equal(("Arts", "rose", "Art room", true), (saved.Category, saved.Color, saved.Location, saved.IsActive));

        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains($"href=\"/duties/activities/{saved.Id}\"", page);
        Assert.Contains(name, page);
        Assert.Contains("href=\"/duties/activities/new\"", page); // Add Activity is still there
        Assert.True(page.IndexOf("/pe?date=", StringComparison.Ordinal) < page.IndexOf("/duties/activities/new\"", StringComparison.Ordinal)); // P.E., Add Activity, then activities

        var manage = WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/activities/{saved.Id}"));
        foreach (var part in new[] { name, "Overview", "Assigned staff", "Classes", "Weekly schedule", "History", "Created" }) Assert.Contains(part, manage);
    }

    [Fact]
    public async Task Invalid_activities_are_refused_with_clear_messages()
    {
        var client = fx.Factory.CreateClient();
        var empty = await PostForm(client, "/duties/activities/new", "/duties/activities/new", new() { ["Name"] = "", ["Description"] = "" });
        var html = WebUtility.HtmlDecode(await empty.Content.ReadAsStringAsync());
        Assert.Contains("Enter the activity name.", html);
        Assert.Contains("Enter a short description.", html);

        await using var db = fx.NewContext();
        var activities = new ActivityService(db, new TestUser());
        var name = Name("Music");
        Ok(await activities.CreateAsync(Input(name)));
        Assert.Contains((await activities.CreateAsync(Input(name.ToUpperInvariant() + "  "))).Errors, e => e.Key == "Name" && e.Value.Contains("already exists"));
        Assert.Contains((await activities.CreateAsync(Input("P.E."))).Errors, e => e.Value.Contains("P.E. is already an activity"));
        Assert.Contains((await activities.CreateAsync(Input("Physical Education"))).Errors, e => e.Key == "Name");
        Assert.Contains((await activities.CreateAsync(Input(Name("Chess")) with { Icon = "<script>" })).Errors, e => e.Key == "Icon");
        Assert.Contains((await activities.CreateAsync(Input(Name("Chess")) with { Color = "black" })).Errors, e => e.Key == "Color");
        Assert.Contains((await activities.CreateAsync(Input(Name("Chess")) with { DepartmentId = -1 })).Errors, e => e.Key == "DepartmentId");
        Assert.Contains((await activities.CreateAsync(Input(Name("Chess"), staff: [-5]))).Errors, e => e.Key == "StaffIds");
    }

    // ------------------------------------------------------------------ 12 edit, schedule, deactivate, delete

    [Fact]
    public async Task Activities_are_edited_scheduled_deactivated_and_only_deleted_when_safe()
    {
        var cls = await fx.NewClassAsync();
        var (teacher, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var activities = new ActivityService(db, new TestUser());
        var created = await activities.CreateAsync(Input(Name("Choir")));
        Ok(created);
        var id = created.EntityId!.Value;

        Ok(await activities.UpdateAsync(id, Input(Name("School Choir"), "Singing practice.", staff: [teacher], classes: [cls]) with { Icon = "users", Color = "indigo" }));
        var a = (await activities.GetAsync(id))!;
        Assert.Equal(("users", "indigo", "Singing practice."), (a.Icon, a.Color, a.Description));
        Assert.Contains(a.Staff, s => s.Id == teacher);
        Assert.Contains(a.Classes, c => c.Id == cls);
        Assert.False((await activities.UpdateAsync(id, Input(a.Name, a.Description, staff: [teacher], classes: [cls]) with { Icon = "users", Color = "indigo" })).Succeeded); // nothing changed

        // Schedule: school days only, end after start, no overlapping meetings.
        Ok(await activities.AddScheduleAsync(id, new(DayOfWeek.Tuesday, new(14, 0), new(15, 0), null, null)));
        Assert.Contains((await activities.AddScheduleAsync(id, new(DayOfWeek.Tuesday, new(14, 30), new(15, 30), null, null))).Errors, e => e.Value.Contains("overlaps"));
        Assert.Contains((await activities.AddScheduleAsync(id, new(DayOfWeek.Friday, new(9, 0), new(10, 0), null, null))).Errors, e => e.Key == "Day");
        Assert.Contains((await activities.AddScheduleAsync(id, new(DayOfWeek.Monday, new(10, 0), new(9, 0), null, null))).Errors, e => e.Key == "End");

        // Unsafe delete is refused (deactivate instead); deactivating keeps everything.
        var refused = await activities.DeleteAsync(id);
        Assert.Contains("Deactivate it instead", refused.Errors[0].Value);
        Ok(await activities.SetActiveAsync(id, false));
        Assert.DoesNotContain(await activities.ListAsync(false), x => x.Id == id);
        Assert.Contains(await activities.ListAsync(true), x => x.Id == id && !x.IsActive);
        a = (await activities.GetAsync(id))!;
        Assert.Equal((1, 1, 1), (a.Staff.Count, a.Classes.Count, a.Schedule.Count));
        Assert.Contains(a.History, h => h.Action == "Deactivated");
        Assert.Contains(a.History, h => h.Action == "Schedule added");

        // Once nothing is attached, it can be deleted.
        Ok(await activities.RemoveScheduleAsync(id, a.Schedule[0].Id));
        Ok(await activities.UpdateAsync(id, Input(a.Name, a.Description, active: false) with { Icon = "users", Color = "indigo" }));
        Ok(await activities.DeleteAsync(id));
        Assert.False(await db.Activities.AnyAsync(x => x.Id == id));
        Assert.True(await db.Classes.AnyAsync(c => c.Id == cls)); // class and staff themselves untouched
        Assert.True(await db.StaffMembers.AnyAsync(s => s.Id == teacher));
    }

    // ------------------------------------------------------------------ 13 P.E. untouched

    [Fact]
    public async Task Activities_never_copy_or_change_PE_data()
    {
        await using var db = fx.NewContext();
        var before = (await db.PeSessionDetails.CountAsync(), await db.PeEquipmentItems.CountAsync(), await db.TimetableAssignments.CountAsync(), await db.Departments.CountAsync());
        var activities = new ActivityService(db, new TestUser());
        Ok(await activities.CreateAsync(Input(Name("Robotics"))));
        var client = fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/duties/activities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/pe")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/pe/pdf")).StatusCode);
        Assert.Equal(before, (await db.PeSessionDetails.CountAsync(), await db.PeEquipmentItems.CountAsync(), await db.TimetableAssignments.CountAsync(), await db.Departments.CountAsync()));
        Assert.False(await db.Activities.AnyAsync(a => a.NormalizedName == "PE" || a.NormalizedName == "P.E."));
    }

    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
}
