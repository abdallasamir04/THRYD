using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Morning Duty → Morning Revision (formerly In-Class Morning): revision records of a session — an existing class, an
/// eligible teacher, the lesson reviewed and the entry / exit times (or "Not recorded") — validated, kept in the session's
/// audit trail, shown on the Sub-Duty page and in the session's details, and never changing the session's coverage.
/// </summary>
[Collection("db")]
public class MorningRevisionTests(DbFixture fx)
{
    private const DutyComponent Revision = DutyComponent.InClassMorning;
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static int _days;

    /// <summary>A school day no other test uses: each call the next Sunday from 2032 (the test database is new for every run).</summary>
    private static DateOnly Day() => new DateOnly(2032, 1, 4).AddDays(7 * Interlocked.Increment(ref _days));

    private async Task<int> SessionAsync(DateOnly day, DutyComponent component = Revision)
    {
        var (staff, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        // A Morning Revision coverage point lists the classes it covers (unchanged rule); the arrival check has none.
        IReadOnlyList<int> covered = component == Revision ? [await db.Classes.Where(c => c.Name == "KG1A").Select(c => c.Id).SingleAsync()] : [];
        var r = await DbFixture.Duties(db).CreateAsync(new DutyInput(component, day, "Revision supervision " + DbFixture.Unique(),
            new TimeOnly(7, 15), new TimeOnly(7, 45), "KG Building", staff, AttendanceStatus.NotRecorded, null, null, covered));
        Assert.True(r.Succeeded, string.Join(" ", r.Errors.Select(e => e.Value)));
        return r.EntityId!.Value;
    }

    private async Task<(int Id, string Name)> ClassAsync(string name)
    {
        await using var db = fx.NewContext();
        return await db.Classes.Where(c => c.Name == name).Select(c => new ValueTuple<int, string>(c.Id, c.Name)).SingleAsync();
    }

    [Fact]
    public void In_Class_Morning_is_now_Morning_Revision_with_the_same_code_and_link()
    {
        var info = DutyComponents.Get(Revision);
        Assert.Equal("Morning Revision", info.Name);
        Assert.Equal("in-class-morning", info.Slug);
        Assert.Equal(["Students Arrival Check", "Morning Revision", "Morning Assembly (Line) / Proadcast"], DutyComponents.Morning.Select(m => m.Name));
    }

    [Fact]
    public async Task The_form_offers_every_class_and_only_eligible_teachers()
    {
        var day = Day();
        var session = await SessionAsync(day);
        var (teacher, teacherName) = await fx.NewTeacherAsync();
        int support, manager, former;
        await using (var db = fx.NewContext())
        {
            support = (await DbFixture.Teachers(db).CreateAsync("Accountant " + DbFixture.Unique(), "Accountant", null)).EntityId!.Value;
            manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Manager " + DbFixture.Unique() })).EntityId!.Value;
            former = (await DbFixture.Teachers(db).CreateAsync("Former " + DbFixture.Unique(), "Math Teacher", null)).EntityId!.Value;
            await db.StaffMembers.Where(s => s.Id == former).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContractEndDate, day.AddDays(-1)));

            var options = await DbFixture.Duties(db).RevisionOptionsAsync(day);
            Assert.Equal(await db.Classes.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => c.Id).ToListAsync(), options.Classes.Select(c => c.Id));
            Assert.Contains(options.Sessions, s => s.Id == session);
            var ids = options.Teachers.Select(t => t.Id).ToList();
            Assert.Contains(teacher, ids);
            Assert.DoesNotContain(support, ids);    // administrative / support staff
            Assert.DoesNotContain(manager, ids);    // Management Team
            Assert.DoesNotContain(former, ids);     // contract ended before the session
        }

        var html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync($"/duties/revisions/new?date={SchoolWeek.Iso(day)}"));
        Assert.Contains("<label class=\"label\" for=\"SchoolClassId\">Class <span", html);
        Assert.DoesNotContain(">Section", html);
        Assert.Contains($">{teacherName}</option>", html);
        Assert.Contains("<option value=\"" + (await ClassAsync("KG1A")).Id + "\"", html);
        Assert.Matches($"<option value=\"{session}\" selected", html);            // the day's only session is preselected
    }

    [Fact]
    public async Task Records_are_validated_saved_with_the_existing_class_and_teacher_and_kept_in_the_audit_trail()
    {
        var day = Day();
        var session = await SessionAsync(day);
        var arrival = await SessionAsync(day, DutyComponent.StudentsArrival);
        var (t1, t1Name) = await fx.NewTeacherAsync();
        var (t2, _) = await fx.NewTeacherAsync();
        var kg1a = await ClassAsync("KG1A");
        var kg1b = await ClassAsync("KG1B");
        int manager;
        await using (var db = fx.NewContext())
            manager = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Manager " + DbFixture.Unique() })).EntityId!.Value;

        await using (var db = fx.NewContext())
        {
            var duties = DbFixture.Duties(db);
            var classesBefore = await db.Classes.CountAsync();
            var statusBefore = (await duties.ListAsync(Revision, day)).Single(d => d.Id == session).Status;
            RevisionInput Input(int? cls, int? teacher, string? lesson, TimeOnly? entry, TimeOnly? exit, int? dutyId = null) => new(dutyId ?? session, cls, teacher, lesson, entry, exit);
            static IEnumerable<string> Keys(ServiceResult r) => r.Errors.Select(e => e.Key);

            // Validation.
            Assert.Equal(["SchoolClassId", "TeacherId", "Lesson"], Keys(await duties.SaveRevisionAsync(null, Input(null, null, " ", null, null))));
            Assert.Contains("ExitTime", Keys(await duties.SaveRevisionAsync(null, Input(kg1a.Id, t1, "English – Unit 3", new(7, 35), new(7, 20)))));
            Assert.Contains("ExitTime", Keys(await duties.SaveRevisionAsync(null, Input(kg1a.Id, t1, "English – Unit 3", new(7, 20), new(7, 20)))));
            Assert.Contains("DutyAssignmentId", Keys(await duties.SaveRevisionAsync(null, Input(kg1a.Id, t1, "English", null, null, arrival))));   // not a Morning Revision session
            Assert.Contains("SchoolClassId", Keys(await duties.SaveRevisionAsync(null, Input(int.MaxValue, t1, "English", null, null))));
            Assert.Contains("TeacherId", Keys(await duties.SaveRevisionAsync(null, Input(kg1a.Id, manager, "English", null, null))));

            // Several classes and teachers in one session.
            var r1 = await duties.SaveRevisionAsync(null, Input(kg1a.Id, t1, "English – Unit 3 Vocabulary", new(7, 20), new(7, 35)));
            var r2 = await duties.SaveRevisionAsync(null, Input(kg1b.Id, t2, "Mathematics – Fractions", new(7, 22), null));
            Assert.True(r1.Succeeded && r2.Succeeded);
            // The same teacher cannot be in two classes at overlapping times, nor recorded twice for the same class and lesson.
            Assert.Contains("overlaps", (await duties.SaveRevisionAsync(null, Input(kg1b.Id, t1, "Arabic – Reading", new(7, 30), new(7, 40)))).Errors.Single().Value);
            Assert.Contains("already recorded", (await duties.SaveRevisionAsync(null, Input(kg1a.Id, t1, "english – unit 3 vocabulary", null, null))).Errors.Single().Value);

            var saved = await db.MorningRevisions.AsNoTracking().Where(r => r.DutyAssignmentId == session).OrderBy(r => r.Id).ToListAsync();
            Assert.Equal([(kg1a.Id, t1, "English – Unit 3 Vocabulary", (TimeOnly?)new TimeOnly(7, 20), (TimeOnly?)new TimeOnly(7, 35)), (kg1b.Id, t2, "Mathematics – Fractions", new TimeOnly(7, 22), null)],
                saved.Select(r => (r.SchoolClassId, r.TeacherId, r.Lesson, r.EntryTime, r.ExitTime)));
            Assert.Equal(classesBefore, await db.Classes.CountAsync());                        // no class created
            // Records never change the session's coverage.
            Assert.Equal(statusBefore, (await duties.ListAsync(Revision, day)).Single(d => d.Id == session).Status);

            // Edit and remove: each change in the session's audit trail.
            Assert.True((await duties.SaveRevisionAsync(r2.EntityId, Input(kg1b.Id, t2, "Mathematics – Fractions", new(7, 22), new(7, 40)))).Succeeded);
            Assert.Equal(new TimeOnly(7, 40), (await db.MorningRevisions.AsNoTracking().SingleAsync(r => r.Id == r2.EntityId)).ExitTime);
            // A session with revision records is kept (its records are history).
            Assert.Contains("revision", (await duties.DeleteAsync(session)).Errors.Single().Value, StringComparison.OrdinalIgnoreCase);
            Assert.True((await duties.DeleteRevisionAsync(r1.EntityId!.Value)).Succeeded);
            var trail = (await duties.AuditTrailAsync(session)).Select(a => a.Summary).ToList();
            Assert.Contains(trail, s => s.StartsWith($"Revision record added: KG1A · {t1Name} · English – Unit 3 Vocabulary · 7:20 AM–7:35 AM"));
            Assert.Contains(trail, s => s.StartsWith("Revision record changed:") && s.Contains("7:22 AM–Not recorded") && s.Contains("7:22 AM–7:40 AM"));
            Assert.Contains(trail, s => s.StartsWith("Revision record removed: KG1A"));
        }
    }

    [Fact]
    public async Task The_Morning_Revision_page_and_the_session_details_show_the_records()
    {
        var day = Day();
        var date = SchoolWeek.Iso(day);
        var session = await SessionAsync(day);
        var (teacher, teacherName) = await fx.NewTeacherAsync();
        var g1a = await ClassAsync("G1A");
        var client = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Empty state, then a record added through the form (its time not recorded).
        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/morning/in-class-morning?date={date}"));
        Assert.Contains("<h1 class=\"page-title\">Morning Revision</h1>", page);
        Assert.Contains("No revision records yet", page);
        Assert.Contains($"href=\"/duties/revisions/new?date={date}\" data-modal>", page);

        var form = await client.GetStringAsync($"/duties/revisions/new?date={date}");
        var saved = await client.PostAsync("/duties/revisions/new", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token.Match(form).Groups[1].Value), new("Date", date), new("DutyAssignmentId", session.ToString()),
            new("SchoolClassId", g1a.Id.ToString()), new("TeacherId", teacher.ToString()), new("Lesson", "Arabic – Reading Revision"),
            new("EntryTime", "07:25"), new("ExitTime", ""),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal($"/duties/morning/in-class-morning?date={date}", saved.Headers.Location!.OriginalString);

        page = WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/morning/in-class-morning?date={date}"));
        Assert.Matches($"<span class=\"badge badge--class\">G1A</span></td>\\s*<td data-label=\"Teacher\" dir=\"auto\">{Regex.Escape(teacherName)}</td>\\s*<td data-label=\"Lesson reviewed\" dir=\"auto\">Arabic – Reading Revision</td>", page);
        Assert.Contains(">7:25 AM</td>", page);
        Assert.Contains(">Not recorded</td>", page);                                 // no invented exit time

        // The session's details (opened from the Duty Audit) list it, and the audit trail has the addition.
        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/duties/{session}"));
        Assert.Contains("Arabic – Reading Revision", details);
        Assert.Contains("Revision record added: G1A", details);

        // An invalid time range is refused by the form.
        var bad = await client.PostAsync("/duties/revisions/new", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token.Match(form).Groups[1].Value), new("Date", date), new("DutyAssignmentId", session.ToString()),
            new("SchoolClassId", g1a.Id.ToString()), new("TeacherId", teacher.ToString()), new("Lesson", "Science – The Human Body"),
            new("EntryTime", "07:40"), new("ExitTime", "07:30"),
        ]));
        Assert.Contains("The exit time must be later than the entry time.", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
    }
}
