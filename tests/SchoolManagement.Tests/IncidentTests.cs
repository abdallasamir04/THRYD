using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using UglyToad.PdfPig;

namespace SchoolManagement.Tests;

/// <summary>
/// Behavioural / Discipline → Incident: the full official report (header, students involved, timeline, findings &amp;
/// actions, sign-off &amp; closure), audit history, search, the student's history and the official PDF.
/// </summary>
[Collection("db")]
public class IncidentTests(DbFixture fx)
{
    private sealed class User(string? name, int? staffId = null) : ICurrentUser
    {
        public string? Name => name;
        public int? StaffId => staffId;
        public bool IsVerified => false;
    }

    private static IncidentService Service(SchoolDbContext db, string? user = "Discipline Officer") =>
        new(db, new User(user), DbFixture.Settings(db));

    private static void Ok(ServiceResult r) => Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));

    private async Task<(int ClassId, string ClassName, int[] Students)> ClassAsync(int students)
    {
        var classId = await fx.NewClassAsync();
        var ids = new List<int>();
        for (var i = 0; i < students; i++) ids.Add((await fx.NewStudentAsync(classId)).Id);
        await using var db = fx.NewContext();
        return (classId, await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync(), ids.ToArray());
    }

    private static IncidentHeaderInput Header(string category = "Physical Aggression & Verbal Misconduct") =>
        new(category, SchoolWeek.Today.AddDays(-2), "Discipline Department", IncidentSeverity.Level2, IncidentStatus.UnderInvestigation);

    [Fact]
    public async Task Behaviour_has_two_independent_sections()
    {
        var client = fx.Factory.CreateClient();
        var landing = await client.GetStringAsync("/students/behaviour");
        Assert.Equal(2, Regex.Matches(landing, "class=\"hub-card ").Count);
        Assert.Contains("<span class=\"hub-card__title\">Incident</span>", landing);
        Assert.Contains("<span class=\"hub-card__title\">Discipline</span>", landing);
        var discipline = await client.GetStringAsync("/students/behaviour/discipline");
        Assert.Contains("<h1 class=\"page-title\">Discipline</h1>", discipline);
        Assert.DoesNotContain("<table", discipline); // no invented records
        var response = await client.GetAsync("/students/behaviour/incidents");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString()); // confidential: never cached
    }

    [Fact]
    public async Task A_complete_report_with_students_timeline_actions_sign_off_and_closure()
    {
        var (_, className, s) = await ClassAsync(3);
        var staff = (await fx.NewTeacherAsync()).Id;
        var reviewer = (await fx.NewTeacherAsync()).Id;
        Student[] before;
        await using (var db = fx.NewContext()) before = await db.Students.AsNoTracking().Where(x => s.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync();

        int id;
        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            Assert.Contains((await Service(db, user: null).CreateAsync(Header())).Errors, e => e.Value.Contains("working as")); // an author is required
            Assert.Contains((await svc.CreateAsync(Header() with { Date = SchoolWeek.Today.AddDays(1) })).Errors, e => e.Key == "Date"); // not in the future
            var created = await svc.CreateAsync(Header());
            Ok(created);
            id = created.EntityId!.Value;
        }

        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            // B. Several students, each with their own role and involvement; the same student only once.
            Ok(await svc.AddStudentAsync(id, new(s[0], IncidentRole.ReportedConduct, null, "Pushed another student during the line-up and used offensive language.")));
            Ok(await svc.AddStudentAsync(id, new(s[1], IncidentRole.AffectedStudent, null, "Was pushed and verbally insulted; no injury.")));
            Ok(await svc.AddStudentAsync(id, new(s[2], IncidentRole.Other, "Called the duty teacher", "Ran to get the duty teacher.")));
            Assert.False((await svc.AddStudentAsync(id, new(s[0], IncidentRole.Witness, null, "Again"))).Succeeded);
            Assert.Contains((await svc.AddStudentAsync(id, new(s[1], IncidentRole.Other, null, "x"))).Errors, e => e.Key == "RoleOther");

            var rows = (await svc.GetAsync(id))!.Students;
            // C. Timeline: timed events placed chronologically whatever order they are entered in.
            Ok(await svc.AddTimelineAsync(id, new("Escalation", new TimeOnly(10, 40), "Discipline Office", [rows[0].Id, rows[1].Id], null, "Both students were brought to the discipline office.")));
            Ok(await svc.AddTimelineAsync(id, new("Morning Line", new TimeOnly(7, 45), "Assembly & Classroom", [rows[0].Id, rows[1].Id], "Grade 7 line", "Argument started in the morning line.")));
            Ok(await svc.AddTimelineAsync(id, new("Break Time", new TimeOnly(10, 15), "School Yard / Corridor", [rows[0].Id, rows[1].Id, rows[2].Id], null, "Pushing in the corridor; a student fetched the duty teacher.")));
            Assert.False((await svc.AddTimelineAsync(id, new("No one", null, "Yard", [], null, "Nobody present"))).Succeeded);

            // D. Actions, custom and suggested, tracked on their own.
            foreach (var (item, status) in new[] { ("Individual Accounts Inquiry", IncidentActionStatus.Completed), ("Formal Verbal Warning", IncidentActionStatus.IssuedAndLogged), ("Peer mediation (custom)", IncidentActionStatus.Pending) })
                Ok(await svc.AddActionAsync(id, new(item, $"{item}: finding text.", "Restorative details.", status)));
        }

        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            var d = (await svc.GetAsync(id))!;
            Assert.Matches(@"^INC-\d{4}-\d{4}$", d.Reference);
            Assert.Equal(["Morning Line", "Break Time", "Escalation"], d.Timeline.Select(t => t.TimePeriod));
            Assert.Equal(3, d.Timeline.Single(t => t.TimePeriod == "Break Time").Present.Count);
            Assert.Contains(className[^1..], d.Students[0].GradeSection);
            Assert.Equal("Other: Called the duty teacher", d.Students[2].RoleLabel);
            Assert.Equal(3, d.Actions.Count);

            // Reorder: an untimed event can be moved; timed events keep their order.
            Ok(await svc.AddTimelineAsync(id, new("Follow-up", null, "Counsellor", [d.Students[0].Id], null, "Counsellor spoke to the student.")));
            var followUp = (await svc.GetAsync(id))!.Timeline.Last();
            Ok(await svc.MoveTimelineAsync(id, followUp.Id, -1));
            Assert.Equal("Follow-up", (await svc.GetAsync(id))!.Timeline[2].TimePeriod);
            var second = (await svc.GetAsync(id))!.Timeline[1];
            Assert.False((await svc.MoveTimelineAsync(id, second.Id, -1)).Succeeded); // 10:15 cannot go above 07:45
            Ok(await svc.DeleteTimelineAsync(id, (await svc.GetAsync(id))!.Timeline[2].Id));
            Assert.Equal(["Morning Line", "Break Time", "Escalation"], (await svc.GetAsync(id))!.Timeline.Select(t => t.TimePeriod));

            // Closure needs every step: still blocked (pending action, no sign-off).
            Assert.False((await svc.CloseAsync(id)).Succeeded);
            var pending = (await svc.GetAsync(id))!.Actions.Single(a => a.Status == IncidentActionStatus.Pending);
            Ok(await svc.SetActionStatusAsync(id, pending.Id, IncidentActionStatus.Resolved));
            Ok(await svc.UpdateHeaderAsync(id, Header() with { Status = IncidentStatus.Resolved }));
            Ok(await svc.SaveSignOffAsync(id, new(staff, "Test School", SchoolWeek.Today, reviewer, IncidentApproval.Approved, "Endorsed by the principal.")));
            Assert.Empty((await svc.GetAsync(id))!.ClosureBlockers);
            Ok(await svc.CloseAsync(id));

            // Closed = read-only; reopening needs a reason.
            Assert.Contains((await svc.AddActionAsync(id, new("Late", "x", null, IncidentActionStatus.Pending))).Errors, e => e.Value.Contains("closed"));
            Assert.False((await svc.ReopenAsync(id, " ")).Succeeded);
            Ok(await svc.ReopenAsync(id, "Parent meeting brought new information."));
            Ok(await svc.CloseAsync(id));

            var final = (await svc.GetAsync(id))!;
            Assert.True(final.IsClosed);
            Assert.Equal(("Test School", IncidentApproval.Approved), (final.SignOffSchool, final.Approval));
            foreach (var action in new[] { "Report created", "Student added", "Timeline event added", "Timeline reordered", "Timeline event deleted", "Action added", "Action status changed", "Incident information updated", "Sign-off saved", "Record closed", "Record reopened" })
                Assert.Contains(final.History, h => h.Action == action);
            Assert.All(final.History, h => Assert.Equal("Discipline Officer", h.By));
        }

        // Student records are unchanged, and a student named in a report is not deleted.
        await using (var db = fx.NewContext())
        {
            var after = await db.Students.AsNoTracking().Where(x => s.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync();
            Assert.Equal(before.Select(x => (x.FullName, x.SchoolClassId, x.UpdatedAt)), after.Select(x => (x.FullName, x.SchoolClassId, x.UpdatedAt)));
            Assert.Contains("incident report", (await DbFixture.Students(db).DeleteAsync(s[2])).Errors.Single().Value);
        }
    }

    [Fact]
    public async Task Reference_ids_are_unique_and_reports_are_searchable()
    {
        var (_, _, s) = await ClassAsync(1);
        var category = "Category " + DbFixture.Unique();
        int a, b;
        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            a = (await svc.CreateAsync(Header(category))).EntityId!.Value;
            b = (await svc.CreateAsync(Header(category) with { Severity = IncidentSeverity.Level4, Status = IncidentStatus.UnderMonitoring })).EntityId!.Value;
            Ok(await svc.AddStudentAsync(b, new(s[0], IncidentRole.Witness, null, "Saw it happen.")));
        }
        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            var refs = await db.Incidents.Where(i => i.Id == a || i.Id == b).Select(i => i.ReferenceId).ToListAsync();
            Assert.Equal(2, refs.Distinct().Count());
            Assert.Equal(await db.Incidents.CountAsync(), await db.Incidents.Select(i => i.ReferenceId).Distinct().CountAsync());

            Assert.Equal(2, (await svc.ListAsync(new IncidentListQuery(Search: category))).Count);
            Assert.Equal([b], (await svc.ListAsync(new IncidentListQuery(Category: category, Severity: IncidentSeverity.Level4))).Select(r => r.Id));
            Assert.Equal([b], (await svc.ListAsync(new IncidentListQuery(Category: category, Status: IncidentStatus.UnderMonitoring))).Select(r => r.Id));
            Assert.Equal([b], (await svc.ListAsync(new IncidentListQuery(StudentId: s[0]))).Select(r => r.Id));
            var name = await db.Students.Where(x => x.Id == s[0]).Select(x => x.FullName).SingleAsync();
            Assert.Contains(b, (await svc.ListAsync(new IncidentListQuery(Search: name))).Select(r => r.Id));
            var referenceB = await db.Incidents.Where(i => i.Id == b).Select(i => i.ReferenceId).SingleAsync();
            Assert.Equal([b], (await svc.ListAsync(new IncidentListQuery(Search: referenceB))).Select(r => r.Id));
        }

        var client = fx.Factory.CreateClient();
        var history = WebUtility.HtmlDecode(await client.GetStringAsync($"/students/behaviour/incidents/student/{s[0]}"));
        Assert.Contains("Incident history", history);
        Assert.Contains($"/students/behaviour/incidents/{b}", history);
        Assert.DoesNotContain($"/students/behaviour/incidents/{a}\"", history);
    }

    [Fact]
    public async Task The_official_pdf_contains_every_section_and_all_saved_text()
    {
        var (_, _, s) = await ClassAsync(2);
        var longAccount = "Long factual account " + string.Join(" ", Enumerable.Range(1, 120).Select(i => "word" + i)) + " END-OF-ACCOUNT";
        int id;
        string reference;
        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            id = (await svc.CreateAsync(Header())).EntityId!.Value;
            Ok(await svc.AddStudentAsync(id, new(s[0], IncidentRole.ReportedConduct, null, "Involvement of the first student.")));
            Ok(await svc.AddStudentAsync(id, new(s[1], IncidentRole.InterveningStudent, null, "Separated the students.")));
            var rows = (await svc.GetAsync(id))!.Students;
            Ok(await svc.AddTimelineAsync(id, new("Morning Line", null, "Assembly & Classroom", [rows[0].Id], null, longAccount)));
            Ok(await svc.AddActionAsync(id, new("Restorative Apologies Exchanged", "Students exchanged apologies.", "Acknowledged the importance of respectful conduct.", IncidentActionStatus.Resolved)));
            Ok(await svc.SaveSignOffAsync(id, new(null, "Signed School", SchoolWeek.Today, null, IncidentApproval.ApprovedWithConditions, "Monitor for two weeks.")));
            reference = (await svc.GetAsync(id))!.Reference;
        }
        string school;
        await using (var db = fx.NewContext()) school = (await DbFixture.Settings(db).GetProfileAsync()).SchoolName;

        var response = await fx.Factory.CreateClient().GetAsync($"/students/behaviour/incidents/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"Incident_Report_{reference}.pdf", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var doc = PdfDocument.Open(bytes);
        Assert.All(doc.GetPages(), p => Assert.True(p.Width > p.Height));
        var text = Regex.Replace(string.Concat(doc.GetPages().Select(p => p.Text)), @"\s+", "");
        static string Sq(string v) => Regex.Replace(v, @"\s+", "");
        foreach (var v in new[]
        {
            school, "OFFICIAL STUDENT INCIDENT REPORT", reference, "Incident Information", "Physical Aggression & Verbal Misconduct", "Discipline Department",
            "Level 2 (Restorative Intervention)", "Under Investigation",
            "Students Involved", "Grade & Section", "Role Classification", "Reported Conduct & Involvement", "Involvement of the first student.", "Intervening Student",
            "Chronological Timeline of Events", "Morning Line", "Assembly & Classroom", "END-OF-ACCOUNT",
            "Investigation Findings & Administrative Actions", "Restorative Apologies Exchanged", "Acknowledged the importance of respectful conduct.", "Resolved",
            "Administrative Sign-off & Record Closure", "Prepared By", "Reviewed By", "Signed School", "Approved with conditions", "Monitor for two weeks.", "Record Closure Status",
            "CONFIDENTIAL", "official administrative use only",
        })
            Assert.True(text.Contains(Sq(v)), $"\"{v}\" missing from the PDF");

        // The export is recorded in the audit history.
        await using var check = fx.NewContext();
        Assert.True(await check.IncidentHistoryEntries.AnyAsync(h => h.IncidentId == id && h.Action == "PDF exported"));
    }

    [Fact]
    public async Task Pages_render_the_report_and_changes_need_an_identified_user()
    {
        var (_, _, s) = await ClassAsync(1);
        var teacher = await fx.NewTeacherAsync();
        int id;
        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            id = (await svc.CreateAsync(Header())).EntityId!.Value;
            Ok(await svc.AddStudentAsync(id, new(s[0], IncidentRole.ReportedConduct, null, "Conduct text.")));
        }
        var anonymous = fx.Factory.CreateClient();
        var page = WebUtility.HtmlDecode(await anonymous.GetStringAsync($"/students/behaviour/incidents/{id}"));
        foreach (var part in new[] { "Incident information", "Students involved", "Chronological timeline of events", "Investigation findings & administrative actions", "Administrative sign-off & record closure", "Audit history", "You are not identified" })
            Assert.Contains(part, page);
        Assert.DoesNotContain("/students/add\" data-modal", page); // no edit controls without an author

        var identified = fx.Factory.CreateClient();
        identified.DefaultRequestHeaders.Add("Cookie", $"{HttpCurrentUser.OperatorCookie}={teacher.Id}");
        var editable = WebUtility.HtmlDecode(await identified.GetStringAsync($"/students/behaviour/incidents/{id}"));
        Assert.Contains($"/students/behaviour/incidents/{id}/students/add", editable);
        Assert.Contains("New incident report", await identified.GetStringAsync("/students/behaviour/incidents"));
        foreach (var form in new[] { "edit", "students/add", "timeline/add", "actions/add", "signoff" })
            Assert.Equal(HttpStatusCode.OK, (await identified.GetAsync($"/students/behaviour/incidents/{id}/{form}")).StatusCode);
    }
}
