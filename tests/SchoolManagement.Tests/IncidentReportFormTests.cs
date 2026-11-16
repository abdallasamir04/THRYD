using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Add Report / Edit Report: the whole official report (five sections) saved in one transaction, as a draft or a final
/// report, without "Working as"; students come from the Student Directory with their class; events with a time are ordered.
/// </summary>
[Collection("db")]
public class IncidentReportFormTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private sealed class Nobody : ICurrentUser
    {
        public string? Name => null;
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static IncidentService Service(SchoolDbContext db) => new(db, new Nobody(), DbFixture.Settings(db));
    private static void Ok(ServiceResult r) => Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => $"{e.Key}: {e.Value}")));

    private async Task<(int ClassId, string ClassName, int[] Students)> ClassAsync(int count)
    {
        var classId = await fx.NewClassAsync();
        var ids = new List<int>();
        for (var i = 0; i < count; i++) ids.Add((await fx.NewStudentAsync(classId)).Id);
        await using var db = fx.NewContext();
        return (classId, await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync(), ids.ToArray());
    }

    private static IncidentReportInput Report(string category, IReadOnlyList<ReportStudentInput> students, IReadOnlyList<ReportTimelineInput>? timeline = null,
        IReadOnlyList<ReportActionInput>? actions = null, string? department = "Discipline Department", int? reviewer = null) =>
        new(category, SchoolWeek.Today.AddDays(-1), department, IncidentSeverity.Level2, IncidentStatus.UnderInvestigation,
            students, timeline ?? [], actions ?? [], null, "Discipline Department", null, SchoolWeek.Today, reviewer, IncidentApproval.Pending, null);

    [Fact]
    public async Task A_draft_can_be_incomplete_and_the_final_report_is_checked_in_full_all_in_one_transaction()
    {
        var (classId, className, s) = await ClassAsync(3);
        var category = "Playground Conflict " + DbFixture.Unique();
        int id;
        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            // Draft: category + date + one student without any text is enough. Nobody is identified.
            var draft = await svc.SaveReportAsync(null, Report(category, [new(null, s[0], IncidentRole.ReportedConduct, null, null)], department: null), draft: true);
            Ok(draft);
            id = draft.EntityId!.Value;
            var saved = await db.Incidents.Include(i => i.Students).SingleAsync(i => i.Id == id);
            Assert.True(saved.IsDraft);
            Assert.Matches(@"^INC-\d{4}-\d{4}$", saved.ReferenceId);
            Assert.Equal(classId, saved.Students.Single().SchoolClassId);   // grade & section from the student's class
            Assert.Null(saved.CreatedBy);                                    // no invented author
            Assert.Equal("Discipline Department", saved.PreparedByText);
            // A draft cannot be closed.
            Assert.Contains((await svc.GetAsync(id))!.ClosureBlockers, b => b.Contains("draft"));
        }

        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            var d = (await svc.GetAsync(id))!;
            var row = d.Students.Single().Id;
            // Final save with gaps: every missing field is reported on its own key, nothing is saved.
            var bad = await svc.SaveReportAsync(id, Report(category,
                [new(row, s[0], IncidentRole.ReportedConduct, null, null), new(null, s[0], IncidentRole.Witness, null, "Again"), new(null, 987654, IncidentRole.Witness, null, "x")],
                [new(null, null, null, null, [s[2]], null, "Something happened")],
                [new(null, "Formal Verbal Warning", null, null, IncidentActionStatus.Pending)]), draft: false);
            Assert.False(bad.Succeeded);
            var keys = bad.Errors.Select(e => e.Key).ToList();
            foreach (var key in new[] { "Students[0].Involvement", "Students[1].StudentId", "Students[2].StudentId", "Timeline[0].TimePeriod", "Timeline[0].Location", "Timeline[0].PresentStudentIds", "Actions[0].Finding" })
                Assert.Contains(key, keys);
            Assert.True((await svc.GetAsync(id))!.IsDraft);

            // Complete it: several students with roles, events entered out of order, actions; saved as the final report.
            Ok(await svc.SaveReportAsync(id, Report(category,
                [
                    new(row, s[0], IncidentRole.ReportedConduct, null, "Pushed another student."),
                    new(null, s[1], IncidentRole.AffectedStudent, null, "Was pushed; no injury."),
                    new(null, s[2], IncidentRole.Witness, null, "Saw it from the bench."),
                ],
                [
                    new(null, "Break Time", new TimeOnly(10, 40), "School Yard", [s[0], s[1], s[2]], null, "The argument started."),
                    new(null, "Period 3", new TimeOnly(9, 15), "Classroom", [s[0], s[1]], "Ms. Laila", "Earlier tension in class."),
                    new(null, "Discipline Office", null, "Discipline Office", [s[0]], null, "Accounts taken."),
                ],
                [
                    new(null, "Individual Accounts Inquiry", "All students interviewed separately.", "Accounts recorded.", IncidentActionStatus.Completed),
                    new(null, "Formal Verbal Warning", "Warning issued.", null, IncidentActionStatus.IssuedAndLogged),
                ]), draft: false));
        }

        await using (var db = fx.NewContext())
        {
            var d = (await Service(db).GetAsync(id))!;
            Assert.False(d.IsDraft);
            Assert.Equal([s[0], s[1], s[2]], d.Students.Select(x => x.StudentId));
            Assert.Equal([IncidentRole.ReportedConduct, IncidentRole.AffectedStudent, IncidentRole.Witness], d.Students.Select(x => x.Role));
            Assert.All(d.Students, x => Assert.Equal(className, x.ClassName));
            // Timed events in time order (09:15 before 10:40); the untimed event keeps its place.
            Assert.Equal(["Period 3", "Break Time", "Discipline Office"], d.Timeline.Select(t => t.TimePeriod));
            Assert.Equal((2, "Ms. Laila"), (d.Timeline[0].Present.Count, d.Timeline[0].OthersPresent));
            Assert.Equal(2, d.Actions.Count);
            Assert.Contains(d.History, h => h.Action == "Draft created");
            Assert.Contains(d.History, h => h.Action == "Report finalised");
        }

        // Edit: remove a student (and their place in the events), the history keeps everything; a final report never returns to draft.
        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            var d = (await svc.GetAsync(id))!;
            var input = Report(category,
                d.Students.Where(x => x.StudentId != s[2]).Select(x => new ReportStudentInput(x.Id, x.StudentId, x.Role, x.RoleOther, x.Involvement)).ToList(),
                d.Timeline.Select(t => new ReportTimelineInput(t.Id, t.TimePeriod, t.Time, t.Location,
                    t.Present.Select(p => d.Students.First(x => x.Id == p.IncidentStudentId).StudentId).Where(x => x != s[2]).ToList(), t.OthersPresent, t.Account)).ToList(),
                d.Actions.Select(a => new ReportActionInput(a.Id, a.ActionItem, a.Finding, a.Details, a.Status)).ToList());
            Ok(await svc.SaveReportAsync(id, input, draft: true));
            var after = (await svc.GetAsync(id))!;
            Assert.False(after.IsDraft);
            Assert.Equal(2, after.Students.Count);
            Assert.DoesNotContain(after.Timeline.SelectMany(t => t.Present), p => p.Name.Length == 0);
            Assert.Contains(after.History, h => h.Action == "Report updated" && h.Details!.Contains("−1"));
            Assert.Contains(after.History, h => h.Action == "Draft created");   // earlier history is preserved
        }
    }

    [Fact]
    public async Task The_add_report_page_saves_the_whole_form_without_working_as_and_ignores_a_second_submit()
    {
        var (_, className, s) = await ClassAsync(2);
        var client = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/students/behaviour/incidents/new"));
        foreach (var part in new[] { "Add Incident Report", "Incident Information", "Students Involved", "Chronological Timeline of Events",
                     "Investigation Findings & Administrative Actions", "Administrative Sign-off & Record Closure", "Add Student", "Add Timeline Entry", "Add Action",
                     "Save Draft", "Save Report", "data-datepicker", "data-timepicker", "data-searchable", "Back to Incident Reports" })
            Assert.Contains(part, page);
        Assert.DoesNotContain("You are not identified", page);
        Assert.Matches(@"INC-\d{4}-\d{4}", page);   // the reference it will get
        var token = Token.Match(page).Groups[1].Value;
        var key = Guid.NewGuid().ToString();
        var category = "Lunch Queue Dispute " + DbFixture.Unique();
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token), new("SubmissionKey", key), new("intent", "final"),
            new("Category", category), new("Date", SchoolWeek.Iso(SchoolWeek.Today)), new("Department", "Discipline Department"),
            new("Severity", "Level1"), new("Status", "Reported"),
            new("Students[0].StudentId", s[0].ToString()), new("Students[0].Role", "ReportedConduct"), new("Students[0].Involvement", "Pushed in the queue."),
            new("Students[1].StudentId", s[1].ToString()), new("Students[1].Role", "Witness"), new("Students[1].Involvement", "Saw it."),
            new("Timeline[0].TimePeriod", "Lunch Break"), new("Timeline[0].Time", "12:30"), new("Timeline[0].Location", "Canteen"),
            new("Timeline[0].PresentStudentIds", s[0].ToString()), new("Timeline[0].PresentStudentIds", s[1].ToString()), new("Timeline[0].Account", "Pushing in the queue."),
            new("Actions[0].ActionItem", "Behavioral & Conflict Guidance"), new("Actions[0].Finding", "Talked through queueing rules."), new("Actions[0].Status", "Completed"),
            new("PreparedByMode", "text"), new("PreparedByText", "Discipline Department"), new("School", "Test School"), new("ReportDate", SchoolWeek.Iso(SchoolWeek.Today)),
            new("Approval", "Pending"),
        };
        var first = await client.PostAsync("/students/behaviour/incidents/new", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var second = await client.PostAsync("/students/behaviour/incidents/new", new FormUrlEncodedContent(fields)); // double click
        Assert.Equal(first.Headers.Location, second.Headers.Location);

        int id;
        await using (var db = fx.NewContext())
        {
            var made = await db.Incidents.Include(i => i.Students).Include(i => i.Timeline).ThenInclude(t => t.Present).Include(i => i.Actions)
                .Where(i => i.Category == category).ToListAsync();
            var one = Assert.Single(made);
            id = one.Id;
            Assert.False(one.IsDraft);
            Assert.Equal(2, one.Students.Count);
            Assert.Equal(new TimeOnly(12, 30), one.Timeline.Single().Time);
            Assert.Equal(2, one.Timeline.Single().Present.Count);
            Assert.Single(one.Actions);
            Assert.Equal("Test School", one.SignOffSchool);
        }
        Assert.Equal($"/students/behaviour/incidents/{id}", first.Headers.Location!.OriginalString);

        // The new report is in the list, opens, and shows the student's grade & section.
        var list = WebUtility.HtmlDecode(await client.GetStringAsync("/students/behaviour/incidents?search=" + Uri.EscapeDataString(category)));
        Assert.Contains($"/students/behaviour/incidents/{id}", list);
        Assert.Contains(IncidentLabels.GradeSection(className), list);
        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/students/behaviour/incidents/{id}"));
        Assert.Contains("12:30 PM", details);
        Assert.Contains("Discipline Department", details);

        // Edit Report: a failed save keeps everything typed and says what is missing.
        var edit = WebUtility.HtmlDecode(await client.GetStringAsync($"/students/behaviour/incidents/{id}/report"));
        Assert.Contains("Edit Incident Report", edit);
        Assert.DoesNotContain("Save Draft", edit);   // a final report is not turned back into a draft
        var editToken = Token.Match(edit).Groups[1].Value;
        var broken = fields.Where(f => f.Key is not ("__RequestVerificationToken" or "Actions[0].Finding" or "SubmissionKey")).Append(new("__RequestVerificationToken", editToken)).ToList();
        var failed = await client.PostAsync($"/students/behaviour/incidents/{id}/report", new FormUrlEncodedContent(broken));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        var again = WebUtility.HtmlDecode(await failed.Content.ReadAsStringAsync());
        Assert.Contains("Describe the investigation finding and action.", again);
        Assert.Contains("Behavioral & Conflict Guidance", again);
        Assert.Contains("Pushing in the queue.", again);

        // The PDF downloads without anybody identified.
        var pdf = await client.GetAsync($"/students/behaviour/incidents/{id}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Existing_reports_open_in_the_new_form_with_all_their_entries()
    {
        var (_, _, s) = await ClassAsync(1);
        int id;
        await using (var db = fx.NewContext())
        {
            var svc = Service(db);
            id = (await svc.CreateAsync(new("Older Report " + DbFixture.Unique(), SchoolWeek.Today, "Science", IncidentSeverity.Level1, IncidentStatus.Reported))).EntityId!.Value;
            Ok(await svc.AddStudentAsync(id, new(s[0], IncidentRole.ReportedConduct, null, "Old involvement text.")));
            var row = (await svc.GetAsync(id))!.Students[0].Id;
            Ok(await svc.AddTimelineAsync(id, new("Morning Line", new TimeOnly(7, 45), "Assembly Area", [row], null, "Old account text.")));
            Ok(await svc.AddActionAsync(id, new("Formal Verbal Warning", "Old finding text.", null, IncidentActionStatus.Pending)));
        }
        var page = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync($"/students/behaviour/incidents/{id}/report"));
        foreach (var v in new[] { "Old involvement text.", "Old account text.", "Old finding text.", "value=\"07:45\"", $"value=\"{s[0]}\" selected" })
            Assert.Contains(v, page);
    }
}
