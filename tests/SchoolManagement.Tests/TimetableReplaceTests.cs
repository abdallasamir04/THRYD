using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Timetables → Create Table always opens with every active class; a class that already has a timetable gets a new one only
/// after the replacement is confirmed — the old structure is archived and its lessons end from this week (history kept),
/// never deleted, and there is never a second current timetable for the class.
/// </summary>
[Collection("db")]
public class TimetableReplaceTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, params (string Key, string Value)[] fields)
    {
        var token = Token.Match(await client.GetStringAsync("/timetables/create")).Groups[1].Value;
        return await client.PostAsync("/timetables/create",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]));
    }

    [Fact]
    public async Task Create_Table_always_opens_with_every_active_class()
    {
        var client = fx.Factory.CreateClient();
        var form = WebUtility.HtmlDecode(await client.GetStringAsync("/timetables/create"));
        Assert.DoesNotContain("Every active class already has a timetable", form);
        Assert.DoesNotContain("disabled=\"disabled\"", form);
        Assert.Contains("name=\"ClassId\"", form);
        Assert.Contains(">Create Table</button>", form.Replace("\n", ""));
        await using var db = fx.NewContext();
        foreach (var id in await db.Classes.Where(c => c.IsActive).Select(c => c.Id).ToListAsync())
            Assert.Matches($"<option value=\"{id}\"", form);
    }

    [Fact]
    public async Task A_class_with_a_timetable_is_replaced_only_after_confirmation_and_its_history_is_kept()
    {
        var cls = await fx.NewClassAsync();
        var client = fx.Factory.CreateClient(new() { AllowAutoRedirect = false });

        // 1. A class without a timetable: created at once (no confirmation), its period editor opens.
        var created = await PostAsync(client, ("ClassId", cls.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        // The class uses its timetable: a renamed period and a lesson.
        int teacher, lessonId;
        List<int> oldSlots;
        await using (var db = fx.NewContext())
        {
            var map = await PeriodMap.LoadAsync(db, default);
            oldSlots = map.ForClass(cls).Select(s => s.Id).ToList();
            Assert.Equal(PeriodService.NewTablePeriods, oldSlots.Count);
            var rename = await DbFixture.Periods(db).EditAsync(oldSlots[0], new PeriodInput("Assembly", null, null, 1, true, true, null), PeriodScope.ThisClass, cls, null);
            Assert.True(rename.Result.Succeeded, string.Join(" ", rename.Result.Errors.Select(e => e.Value)));
            var math = await db.Departments.Where(d => d.NormalizedName == "MATH").Select(d => d.Id).SingleAsync();
            teacher = (await DbFixture.Teachers(db).CreateAsync("Teacher " + DbFixture.Unique(), "Math Teacher", null, math, null, null)).EntityId!.Value;
            var lesson = await DbFixture.Timetable(db).AssignAsync(new SessionInput(cls, DayOfWeek.Monday, oldSlots[1], math, "Math", teacher, null));
            Assert.True(lesson.Succeeded, string.Join(" ", lesson.Errors.Select(e => e.Value)));
            lessonId = await db.TimetableAssignments.Where(t => t.SchoolClassId == cls && t.EndedOn == null).Select(t => t.Id).SingleAsync();
        }
        async Task<List<string>> Others()
        {
            await using var db = fx.NewContext();
            return (await db.TimeSlots.Where(s => s.SchoolClassId == null || s.SchoolClassId != cls).OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.Name, s.StartTime, s.EndTime, s.IsActive, s.ArchivedAt, s.ReplacesTimeSlotId }).ToListAsync()).Select(x => x.ToString()!).ToList();
        }
        var others = await Others();
        int rowsBefore;
        await using (var db = fx.NewContext()) rowsBefore = await db.TimeSlots.CountAsync(s => s.SchoolClassId == cls);

        // 2. Create Table again: the form explains the replacement; nothing changes yet.
        var ask = await PostAsync(client, ("ClassId", cls.ToString()));
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var askHtml = WebUtility.HtmlDecode(await ask.Content.ReadAsStringAsync());
        Assert.Contains("The new timetable will replace the existing one.", askHtml);
        Assert.Contains("1 lesson ends from this week", askHtml);
        Assert.Contains("Math", askHtml);
        Assert.Contains("Replace timetable", askHtml);
        var fingerprint = Regex.Match(askHtml, "name=\"Confirmation\" value=\"([0-9A-F]{16})\"").Groups[1].Value;
        Assert.NotEmpty(fingerprint);
        await using (var db = fx.NewContext())
        {
            Assert.Equal(oldSlots, (await PeriodMap.LoadAsync(db, default)).ForClass(cls).Select(s => s.Id).ToList());
            Assert.Null((await db.TimetableAssignments.SingleAsync(t => t.Id == lessonId)).EndedOn);
        }

        // 3. Confirmed: replaced.
        var replaced = await PostAsync(client, ("ClassId", cls.ToString()), ("Confirmation", fingerprint));
        Assert.Equal(HttpStatusCode.Redirect, replaced.StatusCode);
        Assert.Equal($"/timetable/periods?classId={cls}", replaced.Headers.Location!.OriginalString);
        await using (var db = fx.NewContext())
        {
            var map = await PeriodMap.LoadAsync(db, default);
            var slots = map.ForClass(cls);
            // One current timetable: exactly the 10 new slots of the class, on every school day, with no lessons.
            Assert.Equal(PeriodService.NewTablePeriods, slots.Count);
            Assert.All(slots, s => Assert.Equal(cls, s.ClassId));
            Assert.Empty(slots.Select(s => s.Id).Intersect(oldSlots));
            Assert.DoesNotContain(slots, s => s.Label == "Assembly");
            foreach (var day in ScheduleRules.Days) Assert.Equal(PeriodService.NewTablePeriods, map.ForClassDay(cls, day).Count);
            Assert.False(await db.TimetableAssignments.AnyAsync(t => t.SchoolClassId == cls && t.EndedOn == null));

            // History kept: the old lesson ended (not deleted), the old periods archived (not deleted).
            var old = await db.TimetableAssignments.SingleAsync(t => t.Id == lessonId);
            Assert.NotNull(old.EndedOn);
            Assert.Equal("Timetable replaced (Create Table)", old.EndReason);
            var archived = await db.TimeSlots.Where(s => oldSlots.Contains(s.Id)).ToListAsync();
            Assert.Equal(oldSlots.Count, archived.Count);
            Assert.All(archived, s => Assert.True(s.ArchivedAt is not null && !s.IsActive && s.ReplacesTimeSlotId is null));
            Assert.Contains(archived, s => s.Name == "Assembly");
            Assert.True(await db.TimeSlots.CountAsync(s => s.SchoolClassId == cls) > rowsBefore);
        }
        // The school's periods and every other class are unchanged.
        Assert.Equal(others, await Others());

        // 4. The same fingerprint cannot replace the new timetable again (it describes the old one).
        var stale = await PostAsync(client, ("ClassId", cls.ToString()), ("Confirmation", fingerprint));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Contains("Replace timetable", await stale.Content.ReadAsStringAsync());
    }
}
