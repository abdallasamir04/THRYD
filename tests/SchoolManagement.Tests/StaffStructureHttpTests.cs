using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Tests;

/// <summary>
/// The new Staff pages through the running app: every page renders, and every form saves to SQL
/// Server (antiforgery token → POST → database), returns 422 with messages when invalid, and
/// follows the non-AJAX redirect path.
/// </summary>
public partial class HttpTests
{
    [Theory]
    [InlineData("/staff")]
    [InlineData("/staff/departments")]
    [InlineData("/staff/departments?hod=missing&search=re")]
    [InlineData("/staff/departments/7")]
    [InlineData("/staff/departments/15?subcategory=Wellbeing&sort=job")]
    [InlineData("/staff/departments/7/edit")]
    [InlineData("/staff/departments/7/head")]
    [InlineData("/staff/create?departmentId=7")]
    [InlineData("/teachers/create?departmentId=13")]
    [InlineData("/management")]
    [InlineData("/management/members/create")]
    [InlineData("/management/members/create?position=Sqam")]
    [InlineData("/management/tasks")]
    [InlineData("/management/tasks?status=overdue&priority=high&due=week&sort=priority")]
    [InlineData("/management/tasks/create")]
    [InlineData("/staff/academic")]
    [InlineData("/staff/members")]
    [InlineData("/staff?view=duties&search=a")]
    [InlineData("/staff/departments/heads")]
    [InlineData("/staff/departments/heads?filter=missing")]
    [InlineData("/staff/departments/13/subcategories")]
    [InlineData("/staff/departments/13/subcategories/create")]
    [InlineData("/management/members")]
    [InlineData("/management/members?position=none")]
    [InlineData("/management/positions")]
    [InlineData("/management/positions/Sqam/assign")]
    [InlineData("/timetable/classes")]
    [InlineData("/timetable/teachers")]
    [InlineData("/timetable/teachers?departmentId=1&search=t")]
    [InlineData("/timetable/slots")]
    [InlineData("/timetable/slots/1/edit")]
    [InlineData("/timetable/classes/1")]
    [InlineData("/timetable/classes/1?week=2020-01-05")]
    [InlineData("/timetable/assign?classId=1&day=Sunday&slotId=1")]
    [InlineData("/workload?teacherId=1")]
    [InlineData("/staff/academic/management")]
    [InlineData("/duties?type=midday")]
    [InlineData("/duties?type=dismissal")]
    [InlineData("/duties/midday/middle-high-break")]
    [InlineData("/duties/midday/prek-kg1")]
    [InlineData("/duties/dismissal/bus")]
    [InlineData("/duties/dismissal/pickup")]
    [InlineData("/duties/create?component=kg2")]
    [InlineData("/duties/create?component=pickup")]
    public async Task Staff_structure_pages_render(string url)
    {
        var response = await fx.Factory.CreateClient().GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
    }

    [Fact]
    public async Task Staff_opens_a_landing_page_with_two_cards_leading_to_separate_areas()
    {
        var client = fx.Factory.CreateClient();
        var staff = await client.GetStringAsync("/staff");
        // Exactly two main cards: Academic Staff and Management Team, each one link.
        Assert.Equal(2, Regex.Matches(staff, "class=\"hub-card ").Count);
        Assert.Contains("href=\"/staff/academic\"", staff);
        Assert.Contains("href=\"/management\"", staff);
        // The sidebar shows Staff only, not every submodule.
        var sidebar = staff[..staff.IndexOf("</aside>", StringComparison.Ordinal)];
        Assert.Contains("href=\"/staff\"", sidebar);
        foreach (var hidden in new[] { "/staff/departments\"", "/management/tasks\"", "/timetable/", "/workload\"" })
            Assert.DoesNotContain(hidden, sidebar);

        var academic = await client.GetStringAsync("/staff/academic");
        // Two cards: Departments and Department Management. Each card is itself the link.
        Assert.Equal(2, Regex.Matches(academic, "class=\"hub-card ").Count);
        foreach (var href in new[] { "/staff/departments", "/staff/academic/management" })
            Assert.Matches($"<a class=\"hub-card hub-card--\\w+\" href=\"{Regex.Escape(href)}\">", academic);

        var deptManagement = await client.GetStringAsync("/staff/academic/management");
        Assert.Equal(5, Regex.Matches(deptManagement, "class=\"hub-card ").Count);
        foreach (var href in new[] { "/staff/members", "/staff/departments/heads", "/workload", "/timetable/classes", "/timetable/teachers" })
            Assert.Matches($"<a class=\"hub-card hub-card--\\w+\" href=\"{Regex.Escape(href)}\">", deptManagement);
        Assert.Contains("<a href=\"/staff/academic\">Academic Staff</a>", deptManagement); // breadcrumb back

        var management = await client.GetStringAsync("/management");
        Assert.Equal(3, Regex.Matches(management, "class=\"hub-card ").Count);
        foreach (var href in new[] { "/management/members", "/management/positions", "/management/tasks" })
            Assert.Contains($"href=\"{href}\"", management);

        // Breadcrumbs lead back to the parents; all fifteen departments open their own page.
        var depts = await client.GetStringAsync("/staff/departments");
        Assert.Contains("<a href=\"/staff\">Staff</a></li><li><a href=\"/staff/academic\">Academic Staff</a>", depts);
        for (var id = 1; id <= 15; id++) Assert.Contains($"href=\"/staff/departments/{id}\"", depts);
        Assert.Contains("Library / Reading", depts);
        Assert.DoesNotContain("Reading Club", depts);
    }

    [Fact]
    public async Task Staff_form_shows_religion_options_only_for_Religion()
    {
        var client = fx.Factory.CreateClient();
        var plain = await client.GetStringAsync("/staff/create");
        Assert.Contains("data-subcat-field hidden=\"hidden\"", plain);  // no subcategory field until a department needs it
        Assert.DoesNotContain("<option value=\"Islamic\"", plain);      // the old generic Religion list is gone

        var religion = await client.GetStringAsync("/staff/create?departmentId=7");
        Assert.DoesNotContain("data-subcat-field hidden", religion);
        Assert.Contains("<option value=\"Islamic\"", religion);
        Assert.Contains("<option value=\"Christian\"", religion);
    }

    [Fact]
    public async Task Staff_form_saves_department_subcategory_and_subject()
    {
        var client = fx.Factory.CreateClient();
        var token = await TokenAsync(client, "/staff/create?departmentId=7");
        var name = "Quran Teacher " + DbFixture.Unique();

        var missing = await PostAsync(client, "/teachers/create", token, new() { ["FullName"] = name, ["DepartmentId"] = "7", ["Subcategory"] = "Islamic" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("Choose the Islamic subject", WebUtility.HtmlDecode(await missing.Content.ReadAsStringAsync()));

        var ok = await PostAsync(client, "/teachers/create", token, new() { ["FullName"] = name, ["DepartmentId"] = "7", ["Subcategory"] = "Islamic", ["Subject"] = "Quran" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("success").GetBoolean());

        await using var db = fx.NewContext();
        var t = await db.Teachers.SingleAsync(x => x.FullName == name);
        Assert.Equal((7, "Islamic", "Quran"), (t.DepartmentId!.Value, t.Subcategory, t.Subject));

        // The edit form loads the saved values.
        var edit = await client.GetStringAsync($"/teachers/edit/{t.Id}");
        Assert.Contains("<option value=\"Quran\" selected=\"selected\">", edit);
    }

    [Fact]
    public async Task Department_edit_and_HOD_forms_save()
    {
        var client = fx.Factory.CreateClient();
        await using (var db = fx.NewContext())
        {
            var r = await DbFixture.Teachers(db).CreateAsync("Reader " + DbFixture.Unique(), "Librarian", null, 12, null, null);
            Assert.True(r.Succeeded);
        }
        await using var check = fx.NewContext();
        var member = await check.Teachers.Where(t => t.DepartmentId == 12).OrderByDescending(t => t.Id).Select(t => t.Id).FirstAsync();
        var outsider = (await fx.NewTeacherAsync()).Id;

        var token = await TokenAsync(client, "/staff/departments/12/edit");
        var edit = await PostAsync(client, "/staff/departments/12/edit", token, new() { ["Description"] = "Library and reading programme" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/staff/departments/12/edit", token, new() { ["Description"] = new string('x', 501) })).StatusCode);

        var bad = await PostAsync(client, "/staff/departments/12/head", token, new() { ["TeacherId"] = outsider.ToString(), ["StartDate"] = SchoolWeek.Iso(SchoolWeek.Today) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/staff/departments/12/head", token, new() { ["TeacherId"] = member.ToString(), ["StartDate"] = "31/02/2026" })).StatusCode);
        var ok = await PostAsync(client, "/staff/departments/12/head", token, new() { ["TeacherId"] = member.ToString(), ["StartDate"] = SchoolWeek.Iso(SchoolWeek.Today), ["Note"] = "Acting" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        await using var after = fx.NewContext();
        Assert.Equal("Library and reading programme", (await after.Departments.SingleAsync(d => d.Id == 12)).Description);
        var head = await after.DepartmentHeads.SingleAsync(h => h.DepartmentId == 12 && h.IsCurrent);
        Assert.Equal((member, SchoolWeek.Today, "Acting"), (head.TeacherId, head.StartDate, head.Note));

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/staff/departments/12"));
        Assert.Contains("Library and reading programme", page);
        Assert.Contains("Change HOD", page);
    }

    [Fact]
    public async Task Management_member_and_task_forms_save_edit_and_delete()
    {
        var client = fx.Factory.CreateClient();
        var token = await TokenAsync(client, "/management/members/create");
        var name = "Coordinator " + DbFixture.Unique();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/management/members/create", token, new() { ["FullName"] = "", ["Position"] = "Sqam" })).StatusCode);
        var created = await PostAsync(client, "/management/members/create", token, new()
        {
            ["FullName"] = name, ["Position"] = "MiddleSchoolCoordinator", ["Email"] = "coord@school.edu.eg", ["AppointedOn"] = "2025-09-01", ["ContactNumber"] = "010 2222 3333",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var redirect = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("redirect").GetString()!;
        var memberId = int.Parse(redirect.Split('/').Last());

        await using (var db = fx.NewContext())
        {
            var m = await db.ManagementMembers.SingleAsync(x => x.Id == memberId);
            Assert.Equal((ManagementPosition.MiddleSchoolCoordinator, "coord@school.edu.eg", new DateOnly(2025, 9, 1), (int?)null), (m.Position!.Value, m.Email, m.AppointedOn!.Value, m.DepartmentId));
        }
        var editForm = await client.GetStringAsync($"/management/members/{memberId}/edit");
        Assert.Contains("value=\"2025-09-01\"", editForm);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/management/members/{memberId}/edit", token, new()
        {
            ["FullName"] = name, ["Position"] = "MiddleSchoolCoordinator", ["Email"] = "", ["AppointedOn"] = "2025-09-01",
        })).StatusCode);

        // Tasks: create with a due date and time, refuse a time without a date, edit, change status, delete.
        var due = SchoolWeek.Iso(SchoolWeek.Today.AddDays(5));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/management/tasks/create", token, new() { ["MemberId"] = memberId.ToString(), ["Title"] = "T", ["DueTime"] = "10:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/management/tasks/create", token, new() { ["MemberId"] = memberId.ToString(), ["Title"] = "T", ["DueDate"] = "not-a-date" })).StatusCode);
        var task = await PostAsync(client, "/management/tasks/create", token, new()
        {
            ["MemberId"] = memberId.ToString(), ["Title"] = "Middle school timetable", ["Priority"] = "High", ["Status"] = "ToDo",
            ["DueDate"] = due, ["DueTime"] = "13:45", ["Description"] = "Draft and circulate", ["Notes"] = "Ask HODs",
        });
        Assert.Equal(HttpStatusCode.OK, task.StatusCode);

        int taskId;
        await using (var db = fx.NewContext())
        {
            var t = await db.ManagementTasks.SingleAsync(x => x.ManagementMemberId == memberId);
            taskId = t.Id;
            Assert.Equal((TaskPriority.High, SchoolWeek.Today.AddDays(5), new TimeOnly(13, 45), "Ask HODs"), (t.Priority, t.DueDate!.Value, t.DueTime!.Value, t.Notes));
        }

        var profile = await client.GetStringAsync($"/management/members/{memberId}");
        Assert.Contains("Middle school timetable", profile);
        Assert.Contains("Middle School Coordinator", profile);
        var taskEdit = await client.GetStringAsync($"/management/tasks/{taskId}/edit");
        Assert.Contains($"value=\"{due}\"", taskEdit);
        Assert.Contains("value=\"13:45\"", taskEdit);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/management/tasks/{taskId}/edit", token, new()
        {
            ["MemberId"] = memberId.ToString(), ["Title"] = "Middle school timetable v2", ["Priority"] = "Urgent", ["Status"] = "InProgress", ["DueDate"] = due, ["DueTime"] = "",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/management/tasks/{taskId}/status", token, new() { ["status"] = "Completed" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, $"/management/tasks/{taskId}/status", token, new() { ["status"] = "Nonsense" })).StatusCode);

        await using (var db = fx.NewContext())
        {
            var t = await db.ManagementTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(("Middle school timetable v2", TaskPriority.Urgent, ManagementTaskStatus.Completed, (TimeOnly?)null), (t.Title, t.Priority, t.Status, t.DueTime));
            Assert.NotNull(t.CompletedAt);
            Assert.True(await db.ManagementTaskHistory.CountAsync(h => h.TaskId == taskId) >= 3);
        }

        // Member with a task cannot be removed (409), the task can be deleted, then the member can.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, $"/management/members/{memberId}/delete", token, [])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/management/tasks/{taskId}/delete", token, [])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, $"/management/tasks/{taskId}/delete", token, [])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/management/members/{memberId}/delete", token, [])).StatusCode);
        await using var final = fx.NewContext();
        Assert.False(await final.ManagementMembers.AnyAsync(m => m.Id == memberId));
    }

    [Fact]
    public async Task Non_ajax_form_posts_redirect_with_a_message()
    {
        var client = fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await TokenAsync(client, "/management/tasks/create");
        var request = new HttpRequestMessage(HttpMethod.Post, "/staff/departments/14/edit")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Description"] = "Theatre and performance" }),
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/staff/departments/14", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Duties_page_has_three_tabs_and_new_duties_save_under_their_type()
    {
        var client = fx.Factory.CreateClient();
        // The Duties page is four cards: each duty, then P.E. (opens the P.E. module).
        var landing = await client.GetStringAsync("/duties");
        Assert.Equal(4, Regex.Matches(landing, "class=\"hub-card ").Count);
        foreach (var t in new[] { "morning", "midday", "dismissal" }) Assert.Contains($"href=\"/duties?type={t}&amp;date=", landing);
        Assert.Matches("<a class=\"hub-card hub-card--\\w+\" href=\"/pe\\?date=[0-9-]+\">", landing);
        Assert.Contains("Physical Education", landing);

        // Midday: youngest first, each with its grade line.
        var midday = WebUtility.HtmlDecode(await client.GetStringAsync("/duties?type=midday"));
        Assert.Equal(5, Regex.Matches(midday, "class=\"duty-component\"").Count);
        var order = new[] { "Pre-K / KG1", "KG2", "Lower Elementary", "Upper Elementary", "Middle / High School Break" }
            .Select(n => midday.IndexOf($">{n}</a>", StringComparison.Ordinal)).ToList();
        Assert.True(order.All(i => i > 0) && order.SequenceEqual(order.Order()), string.Join(",", order));
        foreach (var g in new[] { "Grades Pre-K and KG1", "Grade KG2", "Grades 1–3", "Grades 4–6", "Grades 7–10" }) Assert.Contains(g, midday);
        Assert.Contains("Grades 4–6", WebUtility.HtmlDecode(await client.GetStringAsync("/duties/midday/upper-elementary?date=2026-11-10")));
        var dismissal = await client.GetStringAsync("/duties?type=dismissal");
        Assert.Equal(2, Regex.Matches(dismissal, "class=\"duty-component\"").Count);
        foreach (var tab in new[] { "Morning Duty", "Midday Duty", "Dismissal Duty" }) Assert.Contains(tab, dismissal);

        // The create form is preselected for its section and saves with the right duty type.
        var (staff, _) = await fx.NewTeacherAsync();
        var form = await client.GetStringAsync("/duties/create?component=bus&date=2030-03-05");
        Assert.Contains("Bus · Dismissal Duty", WebUtility.HtmlDecode(form));
        var token = TokenPattern().Match(form).Groups[1].Value;
        var ok = await PostAsync(client, "/duties/create", token, new()
        {
            ["Component"] = "DismissalBus", ["DutyDate"] = "2030-03-05", ["Description"] = "Gate 2 buses", ["Location"] = "Gate 2",
            ["StartTime"] = "14:30", ["EndTime"] = "15:00", ["PrimaryStaffId"] = staff.ToString(),
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        await using var db = fx.NewContext();
        var duty = await db.DutyAssignments.SingleAsync(d => d.PrimaryStaffId == staff);
        Assert.Equal((DutyComponent.DismissalBus, DutyType.Dismissal, "Gate 2"), (duty.Component, duty.DutyType, duty.Location));
        Assert.Contains("Gate 2 buses", await client.GetStringAsync("/duties/dismissal/bus?date=2030-03-05"));
        Assert.DoesNotContain("Gate 2 buses", await client.GetStringAsync("/duties/morning/students-arrival?date=2030-03-05"));
    }

    [Fact]
    public async Task PE_sessions_are_created_and_their_details_and_equipment_persist()
    {
        var client = fx.Factory.CreateClient();
        int pe, teacher, coTeacher, cls;
        await using (var db = fx.NewContext())
        {
            pe = await db.Departments.Where(d => d.NormalizedName == "PE").Select(d => d.Id).SingleAsync();
            Assert.Equal(1, await db.Departments.CountAsync(d => d.NormalizedName == "PE")); // existing department, not a new one
            teacher = (await DbFixture.Teachers(db).CreateAsync("Coach " + DbFixture.Unique(), "PE Teacher", null, pe, null, null)).EntityId!.Value;
            coTeacher = (await DbFixture.Teachers(db).CreateAsync("Coach " + DbFixture.Unique(), "PE Teacher", null, pe, null, null)).EntityId!.Value;
        }
        cls = await fx.NewClassAsync();

        var page = await client.GetStringAsync("/pe?date=2026-11-20");
        var sidebar = page[..page.IndexOf("</aside>", StringComparison.Ordinal)];
        Assert.Contains("aria-current=\"page\"", sidebar); // Duties highlighted in the sidebar (P.E. opens from Duties)
        Assert.DoesNotContain("href=\"/pe\"", sidebar); // no standalone P.E. sidebar item
        Assert.Equal(10, Regex.Matches(page, "class=\"tt__period\"").Count);
        Assert.Equal(5, Regex.Matches(page, "class=\"tt__day[ \"]").Count);

        var form = await client.GetStringAsync("/pe/new?day=Thursday&slotId=4");
        var token = TokenPattern().Match(form).Groups[1].Value;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/pe/new", token, new() { ["Day"] = "Thursday", ["SlotId"] = "4" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/pe/new", token, new()
        {
            ["Day"] = "Thursday", ["SlotId"] = "4", ["ClassId"] = cls.ToString(), ["TeacherId"] = teacher.ToString(),
        })).StatusCode);

        int session;
        await using (var db = fx.NewContext())
        {
            var row = await db.TimetableAssignments.SingleAsync(t => t.SchoolClassId == cls && t.EndedOn == null);
            Assert.Equal((pe, teacher, DayOfWeek.Thursday), (row.DepartmentId, row.TeacherId, row.Day));
            session = row.Id;
        }
        // The same session is on the class timetable and counts in the teacher's workload.
        Assert.Contains("tt-cell--filled", await client.GetStringAsync($"/timetable/classes/{cls}"));

        // Details: co-teacher, facility, activity, equipment checklist, notes.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, $"/pe/sessions/{session}", token, new() { ["CoTeacherId"] = teacher.ToString() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/pe/sessions/{session}", token, new()
        {
            ["CoTeacherId"] = coTeacher.ToString(), ["Facility"] = "SwimmingPool", ["Activity"] = "Freestyle drills", ["Notes"] = "Great effort",
            ["Equipment[a].Name"] = "Kickboards", ["Equipment[a].Checked"] = "true", ["Equipment[b].Name"] = "Pull buoys", ["Equipment[c].Name"] = "  ",
        })).StatusCode);

        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/pe/sessions/{session}"));
        Assert.Contains("Freestyle drills", details);
        Assert.Contains("value=\"Kickboards\"", details);
        await using (var db = fx.NewContext())
        {
            var d = await db.PeSessionDetails.Include(x => x.Equipment).SingleAsync(x => x.TimetableAssignmentId == session);
            Assert.Equal((coTeacher, PeFacility.SwimmingPool, "Great effort"), (d.CoTeacherId!.Value, d.Facility!.Value, d.Notes));
            Assert.Equal([("Kickboards", true), ("Pull buoys", false)], d.Equipment.OrderBy(e => e.SortOrder).Select(e => (e.Name, e.IsChecked)));
        }

        // Unticking and removing items persists too.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/pe/sessions/{session}", token, new()
        {
            ["CoTeacherId"] = "", ["Facility"] = "MainGarden", ["Equipment[x].Name"] = "Kickboards",
        })).StatusCode);
        await using (var db = fx.NewContext())
        {
            var d = await db.PeSessionDetails.Include(x => x.Equipment).SingleAsync(x => x.TimetableAssignmentId == session);
            Assert.Null(d.CoTeacherId);
            Assert.Equal(PeFacility.MainGarden, d.Facility);
            Assert.Equal([("Kickboards", false)], d.Equipment.Select(e => (e.Name, e.IsChecked)));
        }

        // The PE department page renders the same P.E. module (no extra click) as a full-width row of the
        // department grid, directly under Department Staff.
        var deptPage = await client.GetStringAsync($"/staff/departments/{pe}");
        Assert.Contains("class=\"grid-3-1 dept-grid--pe\"", deptPage);
        var staffAt = deptPage.IndexOf("id=\"members-title\"", StringComparison.Ordinal);
        var peAt = deptPage.IndexOf("<section class=\"dept-pe\"", StringComparison.Ordinal);
        Assert.True(staffAt > 0 && peAt > staffAt, $"{staffAt},{peAt}");
        Assert.Contains("aria-label=\"P.E. timetable\"", deptPage[peAt..]);
        Assert.Equal(10, Regex.Matches(deptPage, "class=\"tt__period\"").Count);
        Assert.Contains($"href=\"/pe/sessions/{session}?date=", deptPage);
        Assert.Contains("id=\"hodhist-title\"", deptPage);

        // Other departments are unchanged.
        int other;
        await using (var db = fx.NewContext()) other = await db.Departments.Where(d => d.NormalizedName != "PE").Select(d => d.Id).FirstAsync();
        var otherPage = await client.GetStringAsync($"/staff/departments/{other}");
        Assert.Contains("id=\"members-title\"", otherPage);
        Assert.DoesNotContain("P.E. timetable", otherPage);
        Assert.DoesNotContain("dept-grid--pe", otherPage);
    }

    [Fact]
    public async Task Student_Arrivals_is_shown_as_Student_Attendance()
    {
        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync("/arrivals"); // same route and checklist as before
        Assert.Contains("<h1 class=\"page-title\">Student Attendance</h1>", page);
        Assert.Contains("<li>Student Attendance</li></ol>", page);
        Assert.Contains("Student Attendance", page[..page.IndexOf("</aside>", StringComparison.Ordinal)]);
        Assert.Contains("data-arrival-table", page);
        foreach (var url in new[] { "/arrivals", "/", "/staff/departments" })
            Assert.DoesNotContain("Student Arrivals", await client.GetStringAsync(url));
    }

    [Fact]
    public async Task Add_department_saves_a_new_department_that_works_like_the_existing_ones()
    {
        var client = fx.Factory.CreateClient();
        Assert.Contains("href=\"/staff/departments/create\" data-modal", await client.GetStringAsync("/staff/departments"));
        var form = await client.GetStringAsync("/staff/departments/create");
        var token = TokenPattern().Match(form).Groups[1].Value;
        var name = "Geography " + DbFixture.Unique();
        try
        {
            // The name is required, and must not repeat an existing department (any case or spacing).
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/staff/departments/create", token, new() { ["Name"] = "   " })).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/staff/departments/create", token, new() { ["Name"] = "1234" })).StatusCode);
            var duplicate = await PostAsync(client, "/staff/departments/create", token, new() { ["Name"] = "  math " });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
            Assert.Contains("A department named \"Math\" already exists.", WebUtility.HtmlDecode(await duplicate.Content.ReadAsStringAsync()));

            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/staff/departments/create", token, new()
            {
                ["Name"] = "  " + name + " ", ["Description"] = "Maps, places and people", ["SubcategoryLabel"] = "Strand",
            })).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(client, "/staff/departments/create", token, new() { ["Name"] = name.ToUpperInvariant() })).StatusCode);

            int id;
            await using (var db = fx.NewContext())
            {
                var d = await db.Departments.AsNoTracking().SingleAsync(x => x.Name == name);
                id = d.Id;
                Assert.Equal((TextNormalizer.NameKey(name), "Maps, places and people", "Strand"), (d.NormalizedName, d.Description, d.SubcategoryLabel));
                Assert.True(d.SortOrder > 15); // listed after the existing departments
                // Nothing is created for it: no staff, HOD, subcategories or timetable sessions.
                Assert.False(await db.StaffMembers.AnyAsync(s => s.DepartmentId == id));
                Assert.False(await db.DepartmentHeads.AnyAsync(h => h.DepartmentId == id));
                Assert.False(await db.DepartmentSubcategories.AnyAsync(s => s.DepartmentId == id));
                Assert.False(await db.TimetableAssignments.AnyAsync(t => t.DepartmentId == id));
            }

            // Listed, opens, edits and is offered on the staff form like any department.
            Assert.Contains($"href=\"/staff/departments/{id}\"", await client.GetStringAsync("/staff/departments"));
            var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/staff/departments/{id}"));
            Assert.Contains($"<h1 class=\"page-title\">{name}</h1>", details);
            Assert.Contains("No HOD assigned.", details);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/staff/departments/{id}/edit", token, new() { ["Description"] = "Updated description" })).StatusCode);
            await using (var db = fx.NewContext())
                Assert.Equal("Updated description", await db.Departments.Where(d => d.Id == id).Select(d => d.Description).SingleAsync());
            Assert.Contains(name, await client.GetStringAsync("/staff/create"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/timetable/teachers?departmentId={id}")).StatusCode);
        }
        finally
        {
            // Other tests count the fifteen existing departments.
            await using var db = fx.NewContext();
            await db.Departments.Where(d => d.Name.StartsWith("Geography ")).ExecuteDeleteAsync();
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string token, Dictionary<string, string> form)
    {
        form["__RequestVerificationToken"] = token;
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(request);
    }
}
