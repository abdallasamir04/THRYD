using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Management Team tasks for the whole staff: the creator comes from "Working as", the executor and reviewer can be
/// anyone on the staff, instruction files are checked, stored and served, and nothing touches workload or timetables.
/// </summary>
[Collection("db")]
public class TaskWorkflowTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    // ------------------------------------------------------------------ helpers

    /// <summary>A browser working as <paramref name="staffId"/> (the "Working as" cookie), with its own cookie jar.</summary>
    private (HttpClient Client, CookieContainer Cookies) Browser(int? staffId)
    {
        var cookies = new CookieContainer();
        var client = fx.Factory.CreateDefaultClient(new Uri("http://localhost"), new CookieContainerHandler(cookies));
        if (staffId is { } id) WorkAs(cookies, id);
        return (client, cookies);
    }

    private static void WorkAs(CookieContainer cookies, int staffId) =>
        cookies.Add(new Uri("http://localhost"), new Cookie(HttpCurrentUser.OperatorCookie, staffId.ToString(CultureInfo.InvariantCulture)));

    private static async Task<string> TokenAsync(HttpClient client, string page = "/management/tasks/create") =>
        Token.Match(await client.GetStringAsync(page)).Groups[1].Value;

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string token, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(token), "__RequestVerificationToken");
        var request = new HttpRequestMessage(HttpMethod.Post, "/management/tasks/attachments") { Content = form };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(request);
    }

    private static async Task<Guid> UploadOkAsync(HttpClient client, string token, string fileName, byte[] bytes)
    {
        var response = await UploadAsync(client, token, fileName, bytes);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{fileName}: {(int)response.StatusCode} {body}");
        return Guid.Parse(Regex.Match(body, "\"key\":\"([0-9a-f-]+)\"").Groups[1].Value);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string token, IEnumerable<(string Key, string Value)> fields)
    {
        var body = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        body.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return client.PostAsync(url, new FormUrlEncodedContent(body));
    }

    private static byte[] Pdf(string text = "task") => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {text}\n1 0 obj << /Type /Catalog >> endobj\ntrailer << >>\n%%EOF\n");
    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];

    private static byte[] Office(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("<x/>");
            }
        return ms.ToArray();
    }

    private async Task<int> StaffAsync(string jobTitle, string? department)
    {
        var (id, _) = await fx.NewTeacherAsync(jobTitle);
        if (department is null) return id;
        await using var db = fx.NewContext();
        var deptId = await db.Departments.Where(d => d.Name == department).Select(d => d.Id).SingleAsync();
        await db.Teachers.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.DepartmentId, deptId));
        return id;
    }

    private async Task<int> ManagerAsync()
    {
        await using var db = fx.NewContext();
        var r = await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Leader " + DbFixture.Unique() });
        Assert.True(r.Succeeded);
        return r.EntityId!.Value;
    }

    // ------------------------------------------------------------------ the whole workflow over HTTP

    [Fact]
    public async Task A_manager_assigns_tasks_to_any_staff_with_reviewer_and_files_and_everything_is_kept()
    {
        var manager = await ManagerAsync();
        var teacher = await StaffAsync("Math Teacher", "Math");
        var admin = await StaffAsync("School Registrar", null);           // administrative staff, no department
        var reviewer = await StaffAsync("Science Teacher", "Science");   // reviewer from another department
        int workloadBefore, sessionsBefore;
        await using (var db = fx.NewContext())
        {
            workloadBefore = await db.WorkloadAssignments.CountAsync();
            sessionsBefore = await db.TimetableAssignments.CountAsync();
        }

        var (client, cookies) = Browser(manager);
        var token = await TokenAsync(client);
        var form = await client.GetStringAsync("/management/tasks/create");
        // All three roles, every kind of staff member in the pickers, the THRYD pickers and the file area.
        foreach (var text in new[] { "Created by", "Assigned to", "Submitted to / Reviewer", "data-datepicker", "data-timepicker", "data-task-files", $"value=\"{teacher}\"", $"value=\"{admin}\"", $"value=\"{manager}\"" })
            Assert.Contains(text, form);

        // Upload: valid PDF, Word and PNG files; refused: wrong type, renamed content, Office packages that are fake or have macros.
        var pdf = Pdf("brief");
        var k1 = await UploadOkAsync(client, token, "Exam brief.pdf", pdf);
        var k2 = await UploadOkAsync(client, token, "../../Seating plan.docx", Office("[Content_Types].xml", "word/document.xml"));
        var k3 = await UploadOkAsync(client, token, "room.png", Png());
        foreach (var (name, bytes) in new[]
                 {
                     ("tool.exe", Encoding.ASCII.GetBytes("MZ....")), ("renamed.pdf", Encoding.ASCII.GetBytes("MZ not a pdf")),
                     ("fake.docx", Office("[Content_Types].xml", "xl/workbook.xml")), ("macro.docx", Office("[Content_Types].xml", "word/document.xml", "word/vbaProject.bin")),
                     ("empty.pdf", Array.Empty<byte>()),
                 })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(client, token, name, bytes)).StatusCode);

        var due = SchoolWeek.Iso(SchoolWeek.Today.AddDays(3));
        var title = "Prepare the Grade 5 math exam " + DbFixture.Unique();
        var created = await PostAsync(client, "/management/tasks/create", token, [
            ("Title", title), ("Description", "Two versions, answer key included."), ("Priority", "Critical"), ("Status", "ToDo"),
            ("AssignedToId", teacher.ToString()), ("ReviewerId", reviewer.ToString()),
            ("DueDate", due), ("DueTime", "15:30"),
            ("AttachmentKeys", k1.ToString()), ("AttachmentKeys", k2.ToString()), ("AttachmentKeys", k3.ToString()),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        // A second task for administrative staff, no reviewer, no due date.
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "/management/tasks/create", token, [
            ("Title", "Update the admissions register " + DbFixture.Unique()), ("Priority", "Low"), ("Status", "ToDo"), ("AssignedToId", admin.ToString()),
        ])).StatusCode);

        int taskId;
        List<TaskAttachment> files;
        await using (var db = fx.NewContext())
        {
            var t = await db.ManagementTasks.Include(x => x.Attachments).SingleAsync(x => x.Title == title);
            taskId = t.Id;
            Assert.Equal((teacher, (int?)reviewer, (int?)manager, TaskPriority.Critical), (t.AssignedToId, t.ReviewerId, t.CreatedById, t.Priority));
            Assert.Equal((SchoolWeek.Today.AddDays(3), new TimeOnly(15, 30)), (t.DueDate!.Value, t.DueTime!.Value));
            files = t.Attachments.OrderBy(a => a.Id).ToList();
            Assert.Equal(["Exam brief.pdf", "Seating plan.docx", "room.png"], files.Select(f => f.FileName));       // path part removed
            Assert.All(files, f => Assert.True(File.Exists(Path.Combine(DbFixture.AttachmentRoot, f.StoredName))));
            Assert.All(files, f => Assert.Matches("^[0-9a-f]{32}$", f.StoredName));                                // never the uploaded name
            Assert.Equal(1, await db.ManagementTasks.CountAsync(x => x.AssignedToId == admin));
            // Persisted: the history names the executor and the reviewer.
            Assert.Contains(await db.ManagementTaskHistory.Where(h => h.TaskId == taskId).Select(h => h.Summary).ToListAsync(), s => s.Contains("3 attachments") && s.Contains("submitted to"));
        }

        // Details: all three people with department / job title, the deadline and the files.
        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/management/tasks/{taskId}"));
        foreach (var text in new[] { title, "Two versions, answer key included.", "Math · Math Teacher", "Science · Science Teacher", "Management Team", "3:30 PM", "Exam brief.pdf", "Seating plan.docx", "Download", "left" })
            Assert.Contains(text, details);

        // Open (PDF inline) and download (Word as a file), byte for byte; the browser may not sniff another type.
        var open = await client.GetAsync($"/management/tasks/{taskId}/attachments/{files[0].Id}");
        Assert.Equal("application/pdf", open.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", open.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", open.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(pdf, await open.Content.ReadAsByteArrayAsync());
        var download = await client.GetAsync($"/management/tasks/{taskId}/attachments/{files[1].Id}?download=true");
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("Seating plan.docx", download.Content.Headers.ContentDisposition.FileNameStar);
        // A file is only served through its own task.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/management/tasks/{taskId + 100000}/attachments/{files[0].Id}")).StatusCode);

        // After a restart (a new application instance on the same database and file store) everything is still there.
        await using (var restarted = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>())
        {
            var fresh = restarted.CreateClient();
            var page = WebUtility.HtmlDecode(await fresh.GetStringAsync($"/management/tasks/{taskId}"));
            Assert.Contains("Seating plan.docx", page);
            Assert.Contains("Science · Science Teacher", page);
            Assert.Equal(pdf, await fresh.GetByteArrayAsync($"/management/tasks/{taskId}/attachments/{files[0].Id}"));
        }

        // The task list shows the executor, reviewer and attachment count; filters work for any staff member.
        var list = await client.GetStringAsync($"/tasks?memberId={teacher}");
        Assert.Contains(title, list);
        Assert.Contains("Submitted to", list);
        foreach (var url in new[] { $"/tasks?reviewerId={reviewer}", "/tasks?department=" + await DeptAsync("Math"), "/tasks?mine=created" })
            Assert.Contains(title, await client.GetStringAsync(url));
        Assert.DoesNotContain(title, await client.GetStringAsync("/tasks?department=management"));
        Assert.DoesNotContain(title, await client.GetStringAsync("/tasks?mine=assigned"));     // the manager is not the executor
        var (teacherBrowser, _) = Browser(teacher);
        Assert.Contains(title, await teacherBrowser.GetStringAsync("/tasks?mine=assigned"));    // "Assigned to me" for the teacher
        var (reviewerBrowser, _) = Browser(reviewer);
        Assert.Contains(title, await reviewerBrowser.GetStringAsync("/tasks?mine=review"));

        // Edit as somebody else: new reviewer, one file removed, one added. The creator never changes; the history grows.
        WorkAs(cookies, admin);
        var k4 = await UploadOkAsync(client, token, "Marks.xlsx", Office("[Content_Types].xml", "xl/workbook.xml"));
        var edited = await PostAsync(client, $"/management/tasks/{taskId}/edit", token, [
            ("Title", title), ("Description", "Two versions, answer key included."), ("Priority", "High"), ("Status", "InProgress"),
            ("AssignedToId", teacher.ToString()), ("ReviewerId", manager.ToString()), ("DueDate", due), ("DueTime", "15:30"),
            ("RemoveAttachmentIds", files[2].Id.ToString()), ("AttachmentKeys", k4.ToString()),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        await using (var db = fx.NewContext())
        {
            var t = await db.ManagementTasks.Include(x => x.Attachments).SingleAsync(x => x.Id == taskId);
            Assert.Equal(((int?)manager, (int?)manager, ManagementTaskStatus.InProgress), (t.CreatedById, t.ReviewerId, t.Status));
            Assert.Equal(["Exam brief.pdf", "Seating plan.docx", "Marks.xlsx"], t.Attachments.OrderBy(a => a.Id).Select(a => a.FileName));
            Assert.False(File.Exists(Path.Combine(DbFixture.AttachmentRoot, files[2].StoredName)));      // removed file is gone from disk
            var history = await db.ManagementTaskHistory.Where(h => h.TaskId == taskId).OrderBy(h => h.Id).ToListAsync();
            Assert.Equal(2, history.Count);
            Assert.Contains("reviewer", history[1].Summary);
            Assert.Contains("removed room.png", history[1].Summary);
            Assert.Contains("attached Marks.xlsx", history[1].Summary);
        }

        // Counts and overdue follow the database; a cancelled task is neither open nor overdue.
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, $"/management/tasks/{taskId}/status", token, [("status", "Cancelled")])).StatusCode);
        await using (var db = fx.NewContext())
        {
            var counts = await new ManagementTaskService(db, new NoOne()).ScopeCountsAsync(new TaskListQuery { MemberId = teacher });
            Assert.Equal((1, 1, 0, 0), (counts.Total, counts.Cancelled, counts.Open, counts.Overdue));
        }

        // Deleting the task deletes its files and keeps the history; workload and timetable never changed.
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, $"/management/tasks/{taskId}/delete", token, [])).StatusCode);
        await using (var db = fx.NewContext())
        {
            var stored = await db.TaskAttachments.Where(a => a.TaskId == taskId).CountAsync();
            Assert.Equal(0, stored);
            Assert.All(files.Take(2), f => Assert.False(File.Exists(Path.Combine(DbFixture.AttachmentRoot, f.StoredName))));
            Assert.Contains(await db.ManagementTaskHistory.Where(h => h.TaskId == taskId).ToListAsync(), h => h.Action == TaskHistoryAction.Deleted);
            Assert.Equal(workloadBefore, await db.WorkloadAssignments.CountAsync());
            Assert.Equal(sessionsBefore, await db.TimetableAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task Pending_uploads_are_not_served_and_cannot_join_a_task_twice()
    {
        var teacher = await StaffAsync("Art Teacher", "Art");
        var (client, _) = Browser(null);
        var token = await TokenAsync(client);
        var key = await UploadOkAsync(client, token, "brief.pdf", Pdf());
        await using (var db = fx.NewContext())
        {
            var staged = await db.TaskAttachments.SingleAsync(a => a.StagingKey == key);
            Assert.Null(staged.TaskId);
            // No task owns it yet: not reachable through any task.
            var anyTask = await db.ManagementTasks.Select(t => t.Id).FirstOrDefaultAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/management/tasks/{anyTask}/attachments/{staged.Id}")).StatusCode);
        }

        string Title() => "Art display " + DbFixture.Unique();
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "/management/tasks/create", token, [("Title", Title()), ("AssignedToId", teacher.ToString()), ("AttachmentKeys", key.ToString())])).StatusCode);
        // The same upload again: refused with a message, the form comes back.
        var again = await PostAsync(client, "/management/tasks/create", token, [("Title", Title()), ("AssignedToId", teacher.ToString()), ("AttachmentKeys", key.ToString())]);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("expired or was already used", await again.Content.ReadAsStringAsync());
        // Without "Working as" the creator is simply not recorded.
        await using (var db = fx.NewContext())
            Assert.Null((await db.ManagementTasks.Where(t => t.AssignedToId == teacher).SingleAsync()).CreatedById);
    }

    [Fact]
    public async Task Staff_referenced_by_tasks_are_kept_and_their_history_survives()
    {
        var teacher = await StaffAsync("Music Teacher", "Music");
        var reviewer = await StaffAsync("Drama Teacher", "Drama");
        await using var db = fx.NewContext();
        var tasks = new ManagementTaskService(db, new NoOne());
        var id = (await tasks.CreateAsync(new TaskInput { AssignedToId = teacher, ReviewerId = reviewer, Title = "Concert rehearsal plan" })).EntityId!.Value;
        Assert.Contains("task", (await DbFixture.Teachers(db).DeleteAsync(teacher)).Errors[0].Value);
        Assert.Contains("task", (await DbFixture.Teachers(db).DeleteAsync(reviewer)).Errors[0].Value);
        Assert.True((await tasks.DeleteAsync(id)).Succeeded);
        // The task is gone, its history is not: the executor's record is kept for it.
        Assert.False((await DbFixture.Teachers(db).DeleteAsync(teacher)).Succeeded);
        Assert.True(await db.ManagementTaskHistory.AnyAsync(h => h.TaskId == id));
    }

    // ------------------------------------------------------------------ time: 12-hour display, Cairo, daylight saving

    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private static SchoolTimeService At(string utcIso) =>
        new(new FixedClock(DateTimeOffset.Parse(utcIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)));

    [Fact]
    public void Deadlines_use_school_time_and_count_daylight_saving_changes()
    {
        // 12-hour display of the stored 24-hour value; never both.
        Assert.Equal("3:30 PM", TaskLabels.Time(new TimeOnly(15, 30)));
        Assert.Equal("12:05 AM", TaskLabels.Time(new TimeOnly(0, 5)));
        Assert.Equal("12:00 PM", TaskLabels.Time(new TimeOnly(12, 0)));

        // Spring forward (Fri 24 Apr 2026): noon Thursday → noon Friday is 23 real hours.
        var spring = At("2026-04-23T10:00:00Z"); // 12:00 Cairo, UTC+2
        Assert.Equal(TimeSpan.FromHours(23), TaskLabels.Remaining(new DateOnly(2026, 4, 24), new TimeOnly(12, 0), spring));
        Assert.Equal("23 hours left", TaskLabels.RemainingText(ManagementTaskStatus.ToDo, new DateOnly(2026, 4, 24), new TimeOnly(12, 0), spring));
        // Fall back (Thu 29 Oct 2026): 13:00 Thursday → 13:00 Friday is 25 real hours.
        var autumn = At("2026-10-29T10:00:00Z"); // 13:00 Cairo, UTC+3
        Assert.Equal(TimeSpan.FromHours(25), TaskLabels.Remaining(new DateOnly(2026, 10, 30), new TimeOnly(13, 0), autumn));
        // A date without a time is due at the end of that school day.
        Assert.Equal(TimeSpan.FromHours(12), TaskLabels.Remaining(new DateOnly(2026, 1, 15), null, At("2026-01-15T10:00:00Z")));

        // Overdue compares school-local wall times; closed tasks are never overdue.
        var now = new DateTime(2026, 4, 24, 12, 0, 0);
        Assert.True(TaskLabels.IsOverdue(ManagementTaskStatus.InProgress, new DateOnly(2026, 4, 24), new TimeOnly(11, 59), now));
        Assert.False(TaskLabels.IsOverdue(ManagementTaskStatus.InProgress, new DateOnly(2026, 4, 24), null, now));
        Assert.False(TaskLabels.IsOverdue(ManagementTaskStatus.Cancelled, new DateOnly(2026, 4, 1), null, now));
        Assert.StartsWith("Overdue by 1 day", TaskLabels.RemainingText(ManagementTaskStatus.ToDo, new DateOnly(2026, 4, 22), new TimeOnly(9, 0), spring));
        Assert.Null(TaskLabels.RemainingText(ManagementTaskStatus.Completed, new DateOnly(2026, 4, 22), null, spring));
    }

    [Fact]
    public void Attachment_names_and_types_are_checked_on_the_server()
    {
        Assert.Equal("report.pdf", TaskAttachmentRules.CleanName(@"C:\Users\x\report.pdf", "pdf"));
        Assert.Equal("a_b.docx", TaskAttachmentRules.CleanName("a<b.docx", "docx"));
        Assert.Equal("attachment.png", TaskAttachmentRules.CleanName(".png", "png"));
        Assert.Equal(TaskAttachment.FileNameMaxLength, TaskAttachmentRules.CleanName(new string('x', 400) + ".pdf", "pdf").Length);
        Assert.False(TaskAttachmentRules.IsAllowed("exe"));
        Assert.False(TaskAttachmentRules.IsAllowed("docm"));
        Assert.True(TaskAttachmentRules.IsAllowed("jpeg"));
        Assert.Null(TaskAttachmentRules.CheckContent("png", Png(), new MemoryStream(Png())));
        Assert.NotNull(TaskAttachmentRules.CheckContent("jpg", Png(), new MemoryStream(Png())));
        Assert.Null(TaskAttachmentRules.CheckContent("xls", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0 }, Stream.Null));
    }

    private async Task<int> DeptAsync(string name)
    {
        await using var db = fx.NewContext();
        return await db.Departments.Where(d => d.Name == name).Select(d => d.Id).SingleAsync();
    }

    private sealed class NoOne : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
}
