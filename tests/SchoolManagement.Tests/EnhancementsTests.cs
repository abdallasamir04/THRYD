using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// The six enhancements: Management split (School Leadership / Operation Team), the global Back button, Timetables →
/// Create Table, the School Calendar toolbar, the Duties landing with Duty Audit authorization, and class capacity without
/// a "near capacity" state.
/// </summary>
[Collection("db")]
public class EnhancementsTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private HttpClient Client(bool follow = true) => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = follow });

    private static HttpRequestMessage Ajax(HttpMethod method, string url, HttpContent? content = null)
    {
        var r = new HttpRequestMessage(method, url) { Content = content };
        r.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return r;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string formUrl, string action, params (string Key, string Value)[] fields)
    {
        var token = Token.Match(await client.GetStringAsync(formUrl)).Groups[1].Value;
        return await client.PostAsync(action, new FormUrlEncodedContent([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]));
    }

    private static string Body(string html) => html[html.IndexOf("</aside>", StringComparison.Ordinal)..];

    // ================================================================== 6. Class capacity

    [Theory]
    [InlineData(25, 18, false)]
    [InlineData(25, 24, false)]
    [InlineData(25, 25, true)]
    [InlineData(25, 26, true)]
    public void A_class_is_flagged_only_at_or_above_its_capacity(int capacity, int students, bool full)
    {
        Assert.Equal(full, ClassCapacity.IsFull(students, capacity));
        Assert.Equal(full, ClassCapacity.Badge(students, capacity).ToString()!.Contains("badge--error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_red_indicator_follows_additions_removals_and_transfers_with_no_near_capacity_state()
    {
        int cls, other;
        await using (var db = fx.NewContext())
        {
            cls = (await DbFixture.Classes(db).CreateAsync("T" + DbFixture.Unique()[..8], null, 3, true)).EntityId!.Value;
            other = (await DbFixture.Classes(db).CreateAsync("T" + DbFixture.Unique()[..8], null, 30, true)).EntityId!.Value;
        }
        try
        {
            string name;
            await using (var db = fx.NewContext()) name = (await db.Classes.FindAsync(cls))!.Name;
            var client = Client();
            async Task<(string Table, string Card)> ShownAsync()
            {
                var table = await (await client.SendAsync(Ajax(HttpMethod.Get, $"/classes/all?search={name}&view=table"))).Content.ReadAsStringAsync();
                var card = await (await client.SendAsync(Ajax(HttpMethod.Get, $"/classes/all?search={name}"))).Content.ReadAsStringAsync();
                return (table, card);
            }

            var a = await fx.NewStudentAsync(cls);
            await fx.NewStudentAsync(cls);                                   // 2 of 3: one place left — no warning
            var (t, c) = await ShownAsync();
            Assert.DoesNotContain("Near", t + c);
            Assert.DoesNotContain("badge--warning", t + c);
            Assert.DoesNotContain("badge--error", t + c);

            var b = await fx.NewStudentAsync(cls);                           // 3 of 3: red
            (t, c) = await ShownAsync();
            Assert.Contains("<span class=\"badge badge--error\">Full</span>", t);
            Assert.Contains("badge--error", c);
            Assert.Contains("badge--error", await client.GetStringAsync($"/classes/{cls}"));

            await using (var db = fx.NewContext())                           // transferred out: 2 of 3, no indicator
                Assert.True((await DbFixture.Students(db).TransferClassAsync(b.Id, other, "test")).Succeeded);
            (t, c) = await ShownAsync();
            Assert.DoesNotContain("badge--error", t + c);

            await using (var db = fx.NewContext())                           // capacity lowered below the count: over capacity, red
                await db.Classes.Where(x => x.Id == cls).ExecuteUpdateAsync(u => u.SetProperty(x => x.Capacity, 1));
            (t, c) = await ShownAsync();
            Assert.Contains("badge--error", t);

            await using (var db = fx.NewContext())                           // removals back below capacity: no indicator
            {
                await db.Classes.Where(x => x.Id == cls).ExecuteUpdateAsync(u => u.SetProperty(x => x.Capacity, 3));
                Assert.True((await DbFixture.Students(db).DeleteAsync(a.Id)).Succeeded);
            }
            (t, c) = await ShownAsync();
            Assert.DoesNotContain("badge--error", t + c);
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.StudentTransfers.Where(s => s.FromClassId == cls || s.ToClassId == cls).ExecuteDeleteAsync();
            await db.Students.Where(s => s.SchoolClassId == cls || s.SchoolClassId == other).ExecuteDeleteAsync();
            await db.Classes.Where(x => x.Id == cls || x.Id == other).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public void The_roster_reports_have_no_near_capacity_note()
    {
        RosterClass Class(int capacity, int students) => new(1, "G1A", capacity, true, null, null, false,
            Enumerable.Range(1, students).Select(i => new RosterStudent(i, "S" + i)).ToList());
        Assert.Null(SchoolManagement.Web.Services.Pdf.ClassRosterReports.CapacityNote(Class(25, 24)));
        Assert.Equal("Full", SchoolManagement.Web.Services.Pdf.ClassRosterReports.CapacityNote(Class(25, 25)));
        Assert.Equal("Full", SchoolManagement.Web.Services.Pdf.ClassRosterReports.CapacityNote(Class(25, 26)));
    }

    // ================================================================== 4. School Calendar

    [Fact]
    public async Task Add_year_sits_beside_Add_event_and_the_calendar_is_the_last_section()
    {
        var name = "Year " + DbFixture.Unique();
        var client = Client(follow: false);
        try
        {
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "/school-calendar/years/new", "/school-calendar/years", ("name", name))).StatusCode);
            int yearId;
            await using (var db = fx.NewContext()) yearId = await db.AcademicYears.Where(y => y.Name == name).Select(y => y.Id).SingleAsync();
            var page = await client.GetStringAsync($"/school-calendar?year={yearId}");
            var actions = Regex.Match(page, "(?s)<div class=\"page-head__actions\">(.*?)\n    </div>").Groups[1].Value;
            var addYear = actions.IndexOf("href=\"/school-calendar/years/new\" data-modal>", StringComparison.Ordinal);
            var addEvent = actions.IndexOf($"href=\"/school-calendar/events/new?year={yearId}\" data-modal>", StringComparison.Ordinal);
            Assert.True(addYear >= 0 && addEvent > addYear, "Add year is in the toolbar, just before Add event");
            Assert.Equal(1, Regex.Matches(page, "href=\"/school-calendar/years/new\"").Count);   // the bottom button is gone
            Assert.DoesNotContain("sc-addyear", page);
            var main = page[page.IndexOf("<main", StringComparison.Ordinal)..page.IndexOf("</main>", StringComparison.Ordinal)];
            var calendar = Math.Max(main.LastIndexOf("class=\"sc-months\"", StringComparison.Ordinal), main.LastIndexOf("sc-nomonths", StringComparison.Ordinal));
            Assert.True(calendar > 0);
            Assert.True(main.LastIndexOf("sc-unplaced", StringComparison.Ordinal) < calendar);
            Assert.DoesNotContain("<section", main[calendar..]);                               // nothing after the calendar
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.AcademicQuarters.Where(q => q.AcademicYear.Name == name).ExecuteDeleteAsync();
            await db.AcademicYears.Where(y => y.Name == name).ExecuteDeleteAsync();
        }
    }

    // ================================================================== 2. Back button

    [Fact]
    public async Task Every_page_has_one_back_button_with_a_safe_fallback()
    {
        var client = Client();
        // The global Back button is permanent: every page (the Dashboard too, and pages with their own contextual back
        // link such as All School Staff) renders exactly one.
        foreach (var url in new[] { "/", "/classes", "/timetables", "/school-calendar", "/management", "/duties", "/students/hub", "/settings", "/staff/all" })
        {
            var html = await client.GetStringAsync(url);
            Assert.Single(Regex.Matches(html, "<a class=\"btn btn--secondary btn--sm topbar__back\" href=\"/\" data-back"));
        }
        // A page that names its fallback keeps it; app.js uses the breadcrumb parent otherwise.
        var (ordinary, _) = await fx.NewTeacherAsync();
        var denied = await fx.ClientAs(fx.Factory, ordinary).GetAsync("/duties/audit");
        Assert.Contains("href=\"/duties\" data-back data-back-fixed title=", await denied.Content.ReadAsStringAsync());
        Assert.DoesNotContain("data-back-fixed", await client.GetStringAsync("/classes"));             // no fixed fallback: the breadcrumb parent
        var js = await client.GetStringAsync("/js/app.js");
        Assert.Contains("window.history.back()", js);
        Assert.Contains("from.origin === window.location.origin", js);
        Assert.Contains(".page-head .crumbs a[href]", js);
    }

    // ================================================================== 1. Management

    [Fact]
    public async Task Management_is_split_into_School_Leadership_and_the_Operation_Team()
    {
        var title = "Ops Position " + DbFixture.Unique();
        var name = "Ops Member " + DbFixture.Unique();
        int id;
        await using (var db = fx.NewContext())
            id = (await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = name, NewPositionTitle = title })).EntityId!.Value;
        try
        {
            var client = Client();
            var landing = Body(await client.GetStringAsync("/management"));
            Assert.Equal(2, Regex.Matches(landing, "<a class=\"hub-card hub-card--").Count);
            Assert.Contains("href=\"/management/leadership\"", landing);
            Assert.Contains("href=\"/management/operation-team\"", landing);

            var operation = await client.GetStringAsync("/management/operation-team");
            Assert.Contains($"href=\"/management/members/{id}\"", operation);
            Assert.Contains(title, operation);
            Assert.Contains("data-download=\"/management/operation-team/pdf\"", operation);
            var leadership = await client.GetStringAsync("/management/leadership");
            Assert.DoesNotContain($"href=\"/management/members/{id}\"", leadership);        // one section only, no duplicate
            Assert.Contains("data-download=\"/management/leadership/pdf\"", leadership);

            var profile = await client.GetStringAsync($"/management/members/{id}");
            Assert.Contains("<a href=\"/management/operation-team\">Operation Team</a>", profile);
            foreach (var pdf in new[] { "/management/operation-team/pdf", "/management/leadership/pdf", "/management/members/pdf" })
                Assert.Equal("application/pdf", (await client.GetAsync(pdf)).Content.Headers.ContentType!.MediaType);
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.ManagementMembers.Where(m => m.Id == id).ExecuteDeleteAsync();
            await db.ManagementCustomPositions.Where(p => p.Title == title).ExecuteDeleteAsync();
        }
    }

    // ================================================================== 3. Timetables → Create Table

    [Fact]
    public async Task Create_Table_gives_a_class_without_a_timetable_ten_editable_period_slots_on_five_days()
    {
        var cls = await fx.NewClassAsync();
        try
        {
            async Task<List<string>> OtherSlots()
            {
                await using var db = fx.NewContext();
                return (await db.TimeSlots.Where(s => s.SchoolClassId == null || s.SchoolClassId != cls).OrderBy(s => s.Id)
                    .Select(s => new { s.Id, s.Name, s.StartTime, s.EndTime, s.IsActive, s.ArchivedAt }).ToListAsync()).Select(x => x.ToString()!).ToList();
            }
            var otherSlots = await OtherSlots();
            var client = Client(follow: false);
            Assert.Contains("href=\"/timetables/create\" data-modal>", await client.GetStringAsync("/timetables"));
            var form = await client.GetStringAsync("/timetables/create");
            Assert.Matches($"<option value=\"{cls}\"[^>]*data-note=\"No timetable yet\"", form);

            var created = await PostAsync(client, "/timetables/create", "/timetables/create", ("ClassId", cls.ToString()));
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            Assert.Equal($"/timetable/periods?classId={cls}", created.Headers.Location!.OriginalString);

            await using (var db = fx.NewContext())
            {
                var map = await PeriodMap.LoadAsync(db, default);
                Assert.Equal(5, ScheduleRules.Days.Length);
                foreach (var day in ScheduleRules.Days) Assert.Equal(PeriodService.NewTablePeriods, map.ForClassDay(cls, day).Count);
                Assert.All(map.ForClass(cls), s => Assert.Equal(cls, s.ClassId));                 // the class's own slots
                Assert.Equal(Enumerable.Range(1, PeriodService.NewTablePeriods), map.ForClass(cls).Select(s => s.Number));
                Assert.All(map.ForClass(cls), s => Assert.True(s.IsActive && s.AllDays && !string.IsNullOrWhiteSpace(s.Name)));
                Assert.False(await db.TimetableAssignments.AnyAsync(t => t.SchoolClassId == cls));    // no subjects or teachers
                // The school's periods and every other class are unchanged.
                Assert.Equal(otherSlots, await OtherSlots());
            }

            // One timetable per class: a second Create Table for it asks to confirm the replacement first (nothing changes
            // until then), and the dialog still offers the class.
            var again = await PostAsync(client, "/timetables/create", "/timetables/create", ("ClassId", cls.ToString()));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);                                       // the form again (not a redirect), with the plan
            var againHtml = WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync());
            Assert.Contains("already has a timetable", againHtml);
            Assert.Contains("The new timetable will replace the existing one.", againHtml);
            Assert.Contains("Replace timetable", againHtml);
            Assert.DoesNotContain("disabled=\"disabled\"", await client.GetStringAsync("/timetables/create"));
            Assert.Matches($"<option value=\"{cls}\"[^>]*data-note=\"Has a timetable", await client.GetStringAsync("/timetables/create"));
            await using (var db = fx.NewContext())
            {
                Assert.Equal(PeriodService.NewTablePeriods, (await PeriodMap.LoadAsync(db, default)).ForClass(cls).Count);
                var unconfirmed = await DbFixture.Periods(db).CreateClassTableAsync(cls);
                Assert.False(unconfirmed.Result.Succeeded);
                Assert.True(unconfirmed.NeedsConfirmation);
            }

            // Edit a period's name and times, add a slot, remove a slot — each persists (read back with a new context).
            int first, second;
            await using (var db = fx.NewContext())
            {
                var slots = (await PeriodMap.LoadAsync(db, default)).ForClass(cls);
                (first, second) = (slots[0].Id, slots[1].Id);
                var periods = DbFixture.Periods(db);
                var bad = await periods.EditAsync(first, new PeriodInput("Assembly", new TimeOnly(8, 0), new TimeOnly(7, 30), 1, true, true, null), PeriodScope.ThisClass, cls, null);
                Assert.Contains(bad.Result.Errors, e => e.Key == "EndTime");                          // time range validated
                var edited = await periods.EditAsync(first, new PeriodInput("Assembly", new TimeOnly(6, 0), new TimeOnly(6, 30), 1, true, true, null), PeriodScope.ThisClass, cls, null);
                Assert.True(edited.Result.Succeeded, string.Join(" ", edited.Result.Errors.Select(e => e.Value)));
                var added = await periods.AddAsync(new PeriodInput("Period 11", new TimeOnly(17, 0), new TimeOnly(17, 40), null, true, true, null), PeriodScope.ThisClass, cls, null);
                Assert.True(added.Result.Succeeded, string.Join(" ", added.Result.Errors.Select(e => e.Value)));
                var removed = await periods.DeleteAsync(second, PeriodScope.ThisClass, cls, false, null);
                Assert.True(removed.Result.Succeeded, string.Join(" ", removed.Result.Errors.Select(e => e.Value)));
            }
            await using (var db = fx.NewContext())
            {
                var map = await PeriodMap.LoadAsync(db, default);
                var slots = map.ForClass(cls);
                Assert.Equal(PeriodService.NewTablePeriods, slots.Count);                              // 10 − 1 + 1
                Assert.Contains(slots, s => s.Id == first && s.Label == "Assembly" && s.Start == new TimeOnly(6, 0) && s.End == new TimeOnly(6, 30));
                Assert.DoesNotContain(slots, s => s.Id == second);
                Assert.Contains(slots, s => s.Label == "Period 11" && s.Start == new TimeOnly(17, 0));
                foreach (var day in ScheduleRules.Days) Assert.Equal(PeriodService.NewTablePeriods, map.ForClassDay(cls, day).Count);
            }
            Assert.Contains("Assembly", await client.GetStringAsync($"/timetable/periods?classId={cls}"));
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.TimeSlots.Where(s => s.SchoolClassId == cls).ExecuteDeleteAsync();
            await db.Classes.Where(c => c.Id == cls).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Create_Table_asks_to_confirm_for_a_class_that_already_has_lessons_and_needs_a_class()
    {
        var client = Client(follow: false);
        var none = await PostAsync(client, "/timetables/create", "/timetables/create", ("ClassId", ""));
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);                                            // the form again, with the reason
        Assert.Contains("Choose a class.", await none.Content.ReadAsStringAsync());

        await using var db = fx.NewContext();
        var withLessons = await db.TimetableAssignments.Where(t => t.EndedOn == null && t.SchoolClass.IsActive).Select(t => (int?)t.SchoolClassId).FirstOrDefaultAsync();
        if (withLessons is not { } id) return; // the test database has no lessons at this point of the run
        var before = await db.TimeSlots.CountAsync(s => s.SchoolClassId == id);
        var lessons = await db.TimetableAssignments.CountAsync(t => t.SchoolClassId == id && t.EndedOn == null);
        var outcome = await DbFixture.Periods(db).CreateClassTableAsync(id);
        Assert.False(outcome.Result.Succeeded);
        Assert.True(outcome.NeedsConfirmation);
        Assert.Contains("already has a timetable", outcome.Plan!.Summary);
        Assert.Equal(lessons, outcome.Plan.Lessons.Count);                                                 // the lessons that would end
        // A wrong / stale confirmation is not a confirmation.
        Assert.True((await DbFixture.Periods(db).CreateClassTableAsync(id, "0000000000000000")).NeedsConfirmation);
        Assert.Equal(before, await db.TimeSlots.CountAsync(s => s.SchoolClassId == id));                  // nothing written
        Assert.Equal(lessons, await db.TimetableAssignments.CountAsync(t => t.SchoolClassId == id && t.EndedOn == null));
    }

    // ================================================================== 5. Duties and Duty Audit

    [Fact]
    public async Task Duties_opens_on_exactly_two_cards()
    {
        var html = Body(await Client().GetStringAsync("/duties"));
        Assert.Equal(2, Regex.Matches(html, "<a class=\"hub-card hub-card--").Count);
        Assert.Contains("<a class=\"hub-card hub-card--blue\" href=\"/duties/board\">", html);
        Assert.Matches("<a class=\"hub-card hub-card--\\w+\" href=\"/duties/audit\">", html);
        Assert.Contains(">Duties</span>", html);
        Assert.Contains(">Duty Audit</span>", html);
        Assert.Contains(">Authorized</span>", html);                                                   // the Super Admin
        var (ordinary, _) = await fx.NewTeacherAsync();
        Assert.Contains("Restricted · authorized staff only", WebUtility.HtmlDecode(await fx.ClientAs(fx.Factory, ordinary).GetStringAsync("/duties")));
        var board = await Client().GetStringAsync("/duties/board");
        Assert.Contains("id=\"duties-section\"", board);
        Assert.DoesNotContain("id=\"duty-audit-title\"", board);
    }

    [Fact]
    public async Task Duty_Audit_is_refused_to_everyone_not_authorized_on_every_endpoint()
    {
        int dutyId;
        await using (var db = fx.NewContext()) dutyId = await db.DutyAssignments.Select(d => d.Id).FirstOrDefaultAsync();
        // Working as an ordinary staff member (not the Super Admin, not configured).
        var (ordinary, _) = await fx.NewTeacherAsync();
        var client = fx.ClientAs(fx.Factory, ordinary, follow: false);
        foreach (var url in new[] { "/duties/audit", "/duties/audit?status=covered", "/duties/audit/staff-records", "/duties/audit/pdf", "/staff/duty-records/pdf", $"/duties/{dutyId}/audit-record" })
        {
            var r = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.DoesNotContain("id=\"audit-list\"", await r.Content.ReadAsStringAsync());
        }
        foreach (var url in new[] { "/duties/audit?status=covered", "/duties/audit/staff-records?search=a", "/staff?view=duties" })
        {
            var r = await client.SendAsync(Ajax(HttpMethod.Get, url));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Contains("\"success\":false", await r.Content.ReadAsStringAsync());
        }
        // Saving an audit record is refused too (the token comes from a page that is open to everyone).
        var token = Token.Match(await client.GetStringAsync("/timetables/create")).Groups[1].Value;
        var post = await client.PostAsync($"/duties/{dutyId}/audit-record", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("AuditNotes", "x")]));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    // ================================================================== Super Admin (Administration mode: no "Working as")

    [Fact]
    public async Task The_Super_Admin_opens_Duty_Audit_and_every_module_without_a_staff_identity()
    {
        var client = Client(follow: false);   // no "Working as" cookie, no sign-in, no verification
        foreach (var url in new[] { "/", "/staff", "/staff/all", "/students/hub", "/classes", "/timetables", "/school-calendar", "/duties", "/duties/board",
                     "/duties/audit", "/duties/audit/staff-records", "/duties/audit/pdf", "/staff/duty-records/pdf", "/floor-audit", "/floor-audit/manage",
                     "/floor-audit/manage/items/new", "/tasks", "/import-export", "/management", "/management/leadership", "/management/operation-team", "/settings" })
        {
            var r = await client.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} → {(int)r.StatusCode}");
        }
        var audit = await client.GetStringAsync("/duties/audit");
        Assert.Contains("id=\"audit-list\"", audit);                                       // the audit itself, unchanged
        Assert.DoesNotContain("Not allowed", audit);
        // The header names the Super Admin, never "Working as · not verified".
        Assert.Contains("<span class=\"account__name\">Super Admin</span>", audit);
        Assert.DoesNotContain("not verified", audit);
        Assert.DoesNotContain("Set “Working as”", audit);
    }

    [Fact]
    public async Task Choosing_a_staff_member_under_Working_as_leaves_Super_Admin_and_clearing_it_returns()
    {
        var (staffId, name) = await fx.NewTeacherAsync();
        var client = Client(follow: false);
        var settings = await client.GetStringAsync("/settings");
        Assert.Contains("Nobody — Super Admin (full access)", settings);
        Assert.DoesNotContain("form=\"operator-super-admin\"", settings);              // nothing to clear yet
        var chosen = await PostAsync(client, "/settings", "/operator", ("staffId", staffId.ToString()), ("returnUrl", "/settings"));
        Assert.Equal(HttpStatusCode.Redirect, chosen.StatusCode);

        // Working as an ordinary staff member: that person's identity and permissions (not verified, no Duty Audit).
        var page = await client.GetStringAsync("/duties");
        Assert.Contains(name, page);
        Assert.Contains("Working as · not verified", WebUtility.HtmlDecode(page));
        Assert.DoesNotContain("<span class=\"account__name\">Super Admin</span>", page);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/duties/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/floor-audit/manage/items/new")).StatusCode);

        // "Use as Super Admin" clears it: full access again.
        settings = await client.GetStringAsync("/settings");
        Assert.Contains("form=\"operator-super-admin\"", settings);
        var token = Token.Match(settings[settings.IndexOf("id=\"operator-super-admin\"", StringComparison.Ordinal)..]).Groups[1].Value;
        var cleared = await client.PostAsync("/operator", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("returnUrl", "/settings")]));
        Assert.Equal(HttpStatusCode.Redirect, cleared.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/duties/audit")).StatusCode);
        Assert.Contains("<span class=\"account__name\">Super Admin</span>", await client.GetStringAsync("/"));
    }

    [Theory]
    [InlineData(false, null, null, true)]            // Administration mode: no sign-in, no "Working as"
    [InlineData(false, null, 1, false)]              // working as a staff member
    [InlineData(true, null, null, false)]            // a signed-in user without the role is not the Super Admin
    [InlineData(true, "Teachers", null, false)]
    [InlineData(true, ICurrentUser.SuperAdminRole, null, true)]
    public async Task The_Super_Admin_is_the_Administration_mode_or_a_signed_in_SuperAdmin(bool signedIn, string? role, int? workingAs, bool superAdmin)
    {
        await using var db = fx.NewContext();
        var staffId = workingAs is null ? (int?)null : await db.StaffMembers.Select(s => s.Id).FirstAsync();
        var http = new DefaultHttpContext();
        if (staffId is { } id) http.Request.Headers.Cookie = $"{HttpCurrentUser.OperatorCookie}={id}";
        if (signedIn)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user"), .. role is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.Role, role)]], "test"));
        var user = new HttpCurrentUser(new HttpContextAccessor { HttpContext = http }, db);
        Assert.Equal(superAdmin, user.IsSuperAdmin);
        // Outside a request (background work, the test-data command line) nobody is the Super Admin.
        Assert.False(new HttpCurrentUser(new HttpContextAccessor { HttpContext = null }, db).IsSuperAdmin);
    }

    [Fact]
    public async Task Duty_Audit_opens_for_a_configured_staff_member_and_position_holder()
    {
        var (factory, auditor, positionHolder) = await fx.DutyAuditFactoryAsync();
        foreach (var who in new[] { auditor, positionHolder })
        {
            var client = fx.ClientAs(factory, who, follow: false);
            var page = await client.GetAsync("/duties/audit");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("id=\"audit-list\"", html);
            Assert.DoesNotContain("id=\"duties-section\"", html);                               // the duty board is its own page
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/duties/audit/staff-records")).StatusCode);
            Assert.Equal("application/pdf", (await client.GetAsync("/duties/audit/pdf")).Content.Headers.ContentType!.MediaType);
            Assert.Contains("Authorized", await client.GetStringAsync("/duties"));
        }
        // The same configuration does not open it for someone else.
        var (other, _) = await fx.NewTeacherAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.ClientAs(factory, other, follow: false).GetAsync("/duties/audit")).StatusCode);
    }

    [Fact]
    public async Task The_duty_audit_services_refuse_callers_without_access()
    {
        await using var db = fx.NewContext();
        var refused = new DutyService(db, new TestUser(), auditAccess: NoDutyAuditAccess.Instance);
        await Assert.ThrowsAsync<DutyAuditAccessDeniedException>(() => refused.AuditListAsync(new DutyAuditQuery()));
        await Assert.ThrowsAsync<DutyAuditAccessDeniedException>(() => refused.AuditReportAsync(new DutyAuditQuery()));
        var id = await db.DutyAssignments.Select(d => d.Id).FirstOrDefaultAsync();
        var save = await refused.UpdateAuditAsync(id, "notes", null, false);
        Assert.False(save.Succeeded);
        Assert.Equal(DutyAuditAccess.DeniedMessage, save.Errors[0].Value);
        await Assert.ThrowsAsync<DutyAuditAccessDeniedException>(() => new StaffDutyService(db, new TestUser()).OverviewAsync(null));
        // Built without an access check at all, the services refuse too (no access by default).
        await Assert.ThrowsAsync<DutyAuditAccessDeniedException>(() => new DutyService(db, new TestUser()).AuditListAsync(new DutyAuditQuery()));
        // The plain duty listing used by the Timetables duty rota is not part of Duty Audit.
        await refused.AuditListAllAsync(new DutyAuditQuery());
    }

    [Theory]
    [InlineData(new int[0], new string[0], new string[0], 7, null, null, false)]                  // nothing configured: nobody
    [InlineData(new[] { 7 }, new string[0], new string[0], 7, null, null, true)]                  // a configured staff id
    [InlineData(new[] { 7 }, new string[0], new string[0], 8, null, null, false)]
    [InlineData(new int[0], new string[0], new[] { "DutyAuditors" }, null, "DutyAuditors", null, true)] // a signed-in user's role
    [InlineData(new int[0], new string[0], new[] { "DutyAuditors" }, null, "Teachers", null, false)]
    [InlineData(new int[0], new string[0], new string[0], null, null, null, false)]               // no identity at all (not the Super Admin)
    [InlineData(new int[0], new string[0], new string[0], null, null, "super", true)]             // the Super Admin, nothing configured
    public async Task Access_follows_the_configuration_only(int[] staff, string[] positions, string[] roles, int? staffId, string? role, string? super, bool allowed)
    {
        await using var db = fx.NewContext();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (role is not null) http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "x"), new Claim(ClaimTypes.Role, role)], "test"));
        var access = new DutyAuditAccess(new Monitor(new DutyAuditOptions { AuthorizedStaffIds = staff, AuthorizedPositions = positions, AuthorizedRoles = roles }),
            new TestUser(staffId, super is not null), http, db);
        Assert.Equal(allowed, await access.CanAccessAsync());
    }

    private sealed class Monitor(DutyAuditOptions value) : IOptionsMonitor<DutyAuditOptions>
    {
        public DutyAuditOptions CurrentValue => value;
        public DutyAuditOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DutyAuditOptions, string?> listener) => null;
    }

    private sealed class TestUser(int? staffId = null, bool superAdmin = false) : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => staffId;
        public bool IsVerified => false;
        public bool IsSuperAdmin => superAdmin;
    }
}
