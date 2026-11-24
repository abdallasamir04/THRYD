using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Tests;

/// <summary>Add Staff (one form for Academic and Management, with contract and working hours) and the Staff Attendance pages.</summary>
[Collection("db")]
public sealed partial class StaffCreateAndAttendanceHttpTests(DbFixture fx)
{
    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var token = TokenPattern().Match(await client.GetStringAsync(url)).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string token, IEnumerable<(string Key, string Value)> form)
    {
        var fields = form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("__RequestVerificationToken", token));
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Add_staff_form_offers_both_categories_contract_type_and_hours()
    {
        var html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/staff/create"));
        foreach (var text in new[] { "Staff category", "Staff information", "Contract information", "Employment type", "Working hours",
                     "value=\"Academic\"", "value=\"Management\"", "Contract start date", "Contract end date", "Probation period",
                     "value=\"FullTime\"", "value=\"PartTime\"", "value=\"Fixed\"", "value=\"Flexible\"" })
            Assert.Contains(text, html);
        Assert.Equal(7, Regex.Matches(html, "name=\"WorkingDays\"").Count);
        foreach (var day in new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" }) Assert.Contains($">{day}<", html);
        // Flexible start and end are limited to the school window in the picker as well as on the server.
        Assert.Matches("id=\"WorkStartFlex\"[^>]*min=\"08:00\" max=\"15:00\"", html);
        Assert.Matches("id=\"WorkEndFlex\"[^>]*min=\"08:00\" max=\"15:00\"", html);
        // ?category=academic (Academic Staff pages) preselects the category.
        Assert.Contains("value=\"Academic\" checked=\"checked\"", await fx.Factory.CreateClient().GetStringAsync("/staff/create?category=academic"));
    }

    [Fact]
    public async Task Academic_part_time_flexible_staff_member_is_saved_with_the_employment_settings()
    {
        var client = fx.Factory.CreateClient();
        var token = await TokenAsync(client, "/staff/create");
        var name = "Part Timer " + DbFixture.Unique();
        (string, string)[] Form(string start, string end) =>
        [
            ("Category", "Academic"), ("FullName", name), ("JobTitle", "Art Teacher"), ("ContactNumber", "010 5555 1234"),
            ("ContractStartDate", "2026-09-01"), ("ContractEndDate", "2027-06-30"), ("ProbationDays", "90"), ("ProbationStartDate", ""),
            ("EmploymentType", "PartTime"), ("WorkingDays", "Sunday"), ("WorkingDays", "Tuesday"),
            ("WorkingHoursType", "Flexible"), ("WorkStart", start), ("WorkEnd", end),
            // Fields of the other category are ignored.
            ("Email", "not-used@example.com"),
        ];

        // Outside the 08:00–15:00 window: refused, and nobody is created.
        var refused = await PostAsync(client, "/staff/create", token, Form("07:00", "12:00"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("Flexible hours must be within the school working window", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        await using (var db = fx.NewContext()) Assert.False(await db.StaffMembers.AnyAsync(s => s.FullName == name));

        var ok = await PostAsync(client, "/staff/create", token, Form("09:00", "13:00"));
        var body = await ok.Content.ReadAsStringAsync();
        Assert.True(ok.StatusCode == HttpStatusCode.OK, body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean());

        await using var check = fx.NewContext();
        var t = await check.Teachers.Include(x => x.WorkingDays).SingleAsync(x => x.FullName == name);
        Assert.Equal(("Art Teacher", new DateOnly(2026, 9, 1), new DateOnly(2027, 6, 30)), (t.JobTitle, t.ContractStartDate!.Value, t.ContractEndDate!.Value));
        // 90 days from the contract start (the default start): 1 Sep – 29 Nov 2026.
        Assert.Equal((90, ProbationUnit.Days), (t.ProbationLength!.Value, t.ProbationUnit!.Value));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 11, 29)), (t.ProbationStartDate!.Value, t.ProbationEndDate!.Value));
        Assert.Equal((EmploymentType.PartTime, WorkingHoursType.Flexible), (t.EmploymentType!.Value, t.WorkingHoursType!.Value));
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(13, 0)), (t.WorkStart!.Value, t.WorkEnd!.Value));
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Tuesday], t.WorkingDays.Select(d => d.Day).Order().ToArray());
    }

    [Fact]
    public async Task Management_full_time_member_is_saved_on_the_Management_Team()
    {
        var client = fx.Factory.CreateClient();
        var token = await TokenAsync(client, "/staff/create?category=management");
        var name = "Affairs Officer " + DbFixture.Unique();
        var ok = await PostAsync(client, "/staff/create", token,
        [
            ("Category", "Management"), ("FullName", name), ("Email", "officer@school.edu.eg"),
            ("ContractStartDate", "2026-01-10"), ("EmploymentType", "FullTime"), ("WorkingDays", "Friday"),
            ("WorkingHoursType", "Fixed"), ("WorkStart", "07:30"), ("WorkEnd", "15:30"),
            // Academic fields are ignored for Management (a management member never has a department).
            ("DepartmentId", "7"), ("JobTitle", "Ignored"),
        ]);
        var body = await ok.Content.ReadAsStringAsync();
        Assert.True(ok.StatusCode == HttpStatusCode.OK, body);

        await using var db = fx.NewContext();
        var m = await db.ManagementMembers.Include(x => x.WorkingDays).SingleAsync(x => x.FullName == name);
        Assert.Null(m.DepartmentId);
        Assert.Equal("officer@school.edu.eg", m.Email);
        Assert.Equal((EmploymentType.FullTime, WorkingHoursType.Fixed, new TimeOnly(7, 30), new TimeOnly(15, 30)),
            (m.EmploymentType!.Value, m.WorkingHoursType!.Value, m.WorkStart!.Value, m.WorkEnd!.Value));
        Assert.Empty(m.WorkingDays);                                   // full time: no weekday list
        Assert.Null(m.ContractEndDate);                                // ongoing
        Assert.Equal(((int?)null, (ProbationUnit?)null, (DateOnly?)null, (DateOnly?)null),
            (m.ProbationLength, m.ProbationUnit, m.ProbationStartDate, m.ProbationEndDate)); // no probation chosen
        Assert.False(await db.Teachers.AnyAsync(t => t.FullName == name)); // not duplicated as a teacher

        // Missing contract start / employment / hours: refused with field messages.
        var missing = await PostAsync(client, "/staff/create", token, [("Category", "Management"), ("FullName", name + " Two")]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        var html = WebUtility.HtmlDecode(await missing.Content.ReadAsStringAsync());
        // The messages shown next to the fields (the same texts also sit in data-val-required attributes, hence the > <).
        foreach (var text in new[] { "Enter the contract start date.", "Choose Full Time or Part Time.", "Choose Fixed Hours or Flexible Hours." }) Assert.Contains($">{text}<", html);
    }

    private static (string, string)[] ManagementForm(string name, params (string, string)[] extra) =>
    [
        ("Category", "Management"), ("FullName", name), ("ContractStartDate", "2026-01-10"), ("EmploymentType", "FullTime"),
        ("WorkingHoursType", "Fixed"), ("WorkStart", "08:00"), ("WorkEnd", "15:00"), .. extra,
    ];

    [Fact]
    public async Task Probation_offers_30_60_90_days_and_saves_the_dates()
    {
        var client = fx.Factory.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/staff/create"));
        foreach (var text in new[] { "No probation", "30 Days", "60 Days", "90 Days", "Probation Period Dates", "Probation Start Date", "Probation End Date" })
            Assert.Contains(text, html);
        Assert.Equal(4, Regex.Matches(html, "name=\"ProbationDays\"").Count);
        Assert.DoesNotContain("name=\"ProbationLength\"", html);
        Assert.DoesNotContain("name=\"ProbationUnit\"", html);

        var token = await TokenAsync(client, "/staff/create");
        var name = "Probation " + DbFixture.Unique();
        (string, string)[] Form(string days, string start, string end = "2026-12-31") =>
        [
            ("Category", "Academic"), ("FullName", name), ("JobTitle", "Science Teacher"),
            ("ContractStartDate", "2026-09-01"), ("ContractEndDate", end), ("ProbationDays", days), ("ProbationStartDate", start),
            ("EmploymentType", "FullTime"), ("WorkingHoursType", "Fixed"), ("WorkStart", "08:00"), ("WorkEnd", "15:00"),
        ];
        async Task Refused((string, string)[] form, string message)
        {
            var r = await PostAsync(client, "/staff/create", token, form);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Contains(message, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
            await using var db = fx.NewContext();
            Assert.False(await db.StaffMembers.AnyAsync(s => s.FullName == name));
        }
        await Refused(Form("45", ""), "Choose a probation period of 30, 60 or 90 days.");
        await Refused(Form("30", "2026-08-31"), "The probation period cannot start before the contract start date.");
        await Refused(Form("90", "2026-10-15"), "would end on 12 Jan 2027, after the contract end date (31 Dec 2026)");

        // 60 days from a chosen start (15 Sep): 15 Sep – 13 Nov 2026; duration and both dates are stored.
        var ok = await PostAsync(client, "/staff/create", token, Form("60", "2026-09-15"));
        Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        await using var check = fx.NewContext();
        var t = await check.StaffMembers.AsNoTracking().SingleAsync(s => s.FullName == name);
        Assert.Equal((60, ProbationUnit.Days, new DateOnly(2026, 9, 15), new DateOnly(2026, 11, 13)),
            (t.ProbationLength!.Value, t.ProbationUnit!.Value, t.ProbationStartDate!.Value, t.ProbationEndDate!.Value));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31)), (t.ContractStartDate!.Value, t.ContractEndDate!.Value)); // the contract is unchanged

        // "No probation": a stray start date is ignored and nothing is stored.
        var none = "No Probation " + DbFixture.Unique();
        var plain = Form("", "2026-09-15");
        plain[1] = ("FullName", none);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/staff/create", token, plain)).StatusCode);
        var n = await check.StaffMembers.AsNoTracking().SingleAsync(s => s.FullName == none);
        Assert.Equal(((int?)null, (DateOnly?)null, (DateOnly?)null), (n.ProbationLength, n.ProbationStartDate, n.ProbationEndDate));
    }

    [Fact]
    public async Task Management_staff_can_take_an_existing_position_or_a_new_one_without_duplicates()
    {
        var client = fx.Factory.CreateClient();
        var token = await TokenAsync(client, "/staff/create?category=management");
        var stamp = DbFixture.Unique();
        var title = "Student Affairs Manager " + stamp;
        int appointments, positions;
        await using (var db = fx.NewContext())
        {
            appointments = await db.PositionAssignments.CountAsync();
            positions = await db.ManagementMembers.CountAsync(m => m.Position != null);
        }

        // The dropdown: leadership positions (held ones name their holder) and + Add New Position.
        var form = WebUtility.HtmlDecode(await client.GetStringAsync("/staff/create?category=management"));
        Assert.Contains("name=\"PositionChoice\"", form);
        Assert.Contains("<optgroup label=\"Leadership positions\">", form);
        Assert.Contains("<option value=\"new\">+ Add New Position</option>", form);
        Assert.Contains("New Position Title", form);

        // + Add New Position needs a title, and cannot repeat a leadership position (any case).
        var empty = await PostAsync(client, "/staff/create", token, ManagementForm("Officer " + stamp, ("PositionChoice", "new"), ("NewPositionTitle", "  ")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Contains("Enter the new position title.", WebUtility.HtmlDecode(await empty.Content.ReadAsStringAsync()));
        var leadership = await PostAsync(client, "/staff/create", token, ManagementForm("Officer " + stamp, ("PositionChoice", "new"), ("NewPositionTitle", "school  DIRECTOR")));
        Assert.Contains("\"School Director\" is already a leadership position.", WebUtility.HtmlDecode(await leadership.Content.ReadAsStringAsync()));

        // Created with the member: the position exists once, the member holds it, the job title shows it, no department.
        var first = "Officer A " + stamp;
        var created = await PostAsync(client, "/staff/create", token, ManagementForm(first, ("PositionChoice", "new"), ("NewPositionTitle", " " + title + " "),
            ("DepartmentId", "7")));
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        int positionId, holderId;
        await using (var db = fx.NewContext())
        {
            var p = await db.ManagementCustomPositions.AsNoTracking().SingleAsync(x => x.Title == title);
            positionId = p.Id;
            var m = await db.ManagementMembers.AsNoTracking().SingleAsync(x => x.FullName == first);
            holderId = m.Id;
            Assert.Equal((positionId, (ManagementPosition?)null, title, (int?)null), (m.CustomPositionId!.Value, m.Position, m.JobTitle, m.DepartmentId));
            Assert.Equal(1, await db.ManagementCustomPositions.CountAsync(x => x.NormalizedTitle == TextNormalizer.NameKey(title)));
            Assert.False(await db.Departments.AnyAsync(d => d.Name == title)); // a position, not a department
        }

        // The same title again (other case and spacing) is refused; the existing one is offered, naming its holder.
        var again = await PostAsync(client, "/staff/create", token, ManagementForm("Officer B " + stamp, ("PositionChoice", "new"), ("NewPositionTitle", title.ToLowerInvariant().Replace(" ", "  "))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Contains($"A position named \"{title}\" already exists.", WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync()));
        form = WebUtility.HtmlDecode(await client.GetStringAsync("/staff/create?category=management"));
        Assert.Contains($"<option value=\"custom-{positionId}\" disabled=\"disabled\">{title} — held by {first}</option>", form);

        // An occupied position is never taken over (neither a custom nor a leadership one).
        var taken = await PostAsync(client, "/staff/create", token, ManagementForm("Officer B " + stamp, ("PositionChoice", $"custom-{positionId}")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);
        Assert.Contains($"{first} already holds the {title} position.", WebUtility.HtmlDecode(await taken.Content.ReadAsStringAsync()));
        await using (var db = fx.NewContext())
        {
            if (await db.ManagementMembers.AnyAsync(m => m.Position == ManagementPosition.SchoolDirector))
                Assert.Equal(HttpStatusCode.UnprocessableEntity,
                    (await PostAsync(client, "/staff/create", token, ManagementForm("Officer C " + stamp, ("PositionChoice", "SchoolDirector")))).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await PostAsync(client, "/staff/create", token, ManagementForm("Officer C " + stamp, ("PositionChoice", "custom-999999")))).StatusCode);
            Assert.False(await db.StaffMembers.AnyAsync(s => s.FullName == "Officer B " + stamp || s.FullName == "Officer C " + stamp));
        }

        // Editing the holder on the Management Team form keeps the position; leaving it makes it vacant and choosable.
        var edit = await TokenAsync(client, $"/management/members/{holderId}/edit");
        var editForm = WebUtility.HtmlDecode(await client.GetStringAsync($"/management/members/{holderId}/edit"));
        Assert.Contains($"<option value=\"custom-{positionId}\" selected=\"selected\">{title}</option>", editForm);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/management/members/{holderId}/edit", edit,
            [("FullName", first), ("PositionChoice", $"custom-{positionId}"), ("ContactNumber", "010 4444 5555")])).StatusCode);
        await using (var db = fx.NewContext())
            Assert.Equal((positionId, title), (await db.ManagementMembers.Where(m => m.Id == holderId).Select(m => m.CustomPositionId).SingleAsync() ?? 0,
                await db.ManagementMembers.Where(m => m.Id == holderId).Select(m => m.JobTitle).SingleAsync()));
        Assert.Contains(title, WebUtility.HtmlDecode(await client.GetStringAsync($"/management/members/{holderId}")));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/management/members/{holderId}/edit", edit, [("FullName", first), ("PositionChoice", "")])).StatusCode);
        var second = "Officer D " + stamp;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/staff/create", token, ManagementForm(second, ("PositionChoice", $"custom-{positionId}")))).StatusCode);

        await using (var db = fx.NewContext())
        {
            Assert.Null(await db.ManagementMembers.Where(m => m.Id == holderId).Select(m => m.CustomPositionId).SingleAsync());
            Assert.Equal(positionId, await db.ManagementMembers.Where(m => m.FullName == second).Select(m => m.CustomPositionId).SingleAsync());
            Assert.Equal(1, await db.ManagementCustomPositions.CountAsync(x => x.Title == title));
            // School Leadership is untouched: the seven positions' holders and their appointment history.
            Assert.Equal(appointments, await db.PositionAssignments.CountAsync());
            Assert.Equal(positions, await db.ManagementMembers.CountAsync(m => m.Position != null));
        }
        // A school-added position belongs to the Operation Team section, not School Leadership.
        Assert.Contains(title, WebUtility.HtmlDecode(await client.GetStringAsync("/management/operation-team")));
        Assert.DoesNotContain(title, WebUtility.HtmlDecode(await client.GetStringAsync("/management/leadership")));
    }

    [Fact]
    public async Task Staff_attendance_pages_and_reports_render()
    {
        var (teacher, _) = await fx.NewTeacherAsync();
        var client = fx.Factory.CreateClient();
        foreach (var url in new[]
        {
            "/staff/attendance", "/staff/attendance?status=late", "/staff/attendance?category=support",
            "/staff/attendance/history?category=management&period=month", $"/staff/attendance?date={SchoolWeek.Iso(SchoolWeek.Today.AddDays(-1))}",
            "/staff/attendance/history", "/staff/attendance/history?period=year",
            "/staff/attendance/history?period=range&from=2026-09-01&to=2026-11-30&status=none", $"/staff/attendance/history?period=day&staffId={teacher}",
            "/staff",
        })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}");
        }
        Assert.Contains("href=\"/staff/attendance\"", await client.GetStringAsync("/staff"));
        // Correct is only for saved records: there is no manual entry for someone not recorded.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/staff/attendance/record?staffId={teacher}")).StatusCode);
        Assert.Contains("Cutoff 9:30 AM", WebUtility.HtmlDecode(await client.GetStringAsync("/staff/attendance")));

        var pdf = await client.GetAsync("/staff/attendance/history/pdf?period=month");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        var excel = await client.GetAsync("/staff/attendance/history/excel?period=month");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", excel.Content.Headers.ContentType?.MediaType);

        // One click needs the anti-forgery token.
        var refused = new HttpRequestMessage(HttpMethod.Post, "/staff/attendance/mark")
        {
            Content = new FormUrlEncodedContent([new("staffId", teacher.ToString()), new("status", "Present")]),
        };
        refused.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(refused)).StatusCode);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
