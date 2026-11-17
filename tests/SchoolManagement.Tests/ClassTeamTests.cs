using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// KG Stage class teaching teams: exactly one Homeroom Teacher and one Co-Teacher per Pre-K / KG1 / KG2 class and
/// academic year, replacements kept as history, never for other stages, and never touching timetable, P.E. or workload data.
/// </summary>
[Collection("db")]
public class ClassTeamTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static void Ok(ServiceResult r) => Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));

    private static void Fails(ServiceResult r, string text)
    {
        Assert.False(r.Succeeded);
        Assert.Contains(r.Errors, e => e.Value.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class User : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static ClassTeamService Teams(SchoolDbContext db) => new(db, new User());

    /// <summary>A new class named <paramref name="prefix"/> + a random section (KG1 → KG1QXZ), so tests never share classes.</summary>
    private async Task<(int Id, string Name)> NewClassAsync(string prefix)
    {
        await using var db = fx.NewContext();
        for (var i = 0; i < 50; i++)
        {
            var name = prefix + new string(Enumerable.Range(0, 3).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray());
            var r = await DbFixture.Classes(db).CreateAsync(name, null, true);
            if (r.Succeeded) return (r.EntityId!.Value, name);
        }
        throw new InvalidOperationException("No free class name.");
    }

    private async Task<int> NewYearAsync()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "Test " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        return year.Id;
    }

    // ------------------------------------------------------------------ scope

    [Theory]
    [InlineData("PreK", true)]
    [InlineData("PreK A", true)]
    [InlineData("KG1A", true)]
    [InlineData("KG1B", true)]
    [InlineData("KG2A", true)]
    [InlineData("KG2C", true)]
    [InlineData("G1A", false)]
    [InlineData("G5B", false)]
    [InlineData("G7A", false)]
    [InlineData("G9B", false)]
    [InlineData("G10A", false)]
    [InlineData("G11A", false)]
    [InlineData("KG3A", false)]
    public void Only_PreK_KG1_and_KG2_classes_are_KG_Stage(string name, bool kg) => Assert.Equal(kg, ClassStages.IsKgStage(name));

    [Fact]
    public async Task Class_page_shows_students_by_default_and_the_KG_team_only_on_request()
    {
        await NewYearAsync(); // at least one academic year exists
        var client = fx.Factory.CreateClient();
        foreach (var prefix in new[] { "PreK", "KG1", "KG2" })
        {
            var (id, _) = await NewClassAsync(prefix);
            var (_, student) = await fx.NewStudentAsync(id);

            // Default: the students list, a Teaching Team button and summary, no form, no staff checklist.
            var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/{id}"));
            Assert.Contains(student, html);
            Assert.Contains($"href=\"/classes/{id}?panel=team", html);
            Assert.Contains("Teaching Team", html);
            Assert.DoesNotContain("Save Changes", html);
            Assert.DoesNotContain("data-team-co", html);
            Assert.DoesNotContain("name=\"HomeroomId\"", html);

            // Panel: current team, year and Edit action; still no form or checklist, and no student list.
            var panel = WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/{id}?panel=team"));
            Assert.Contains("Class Teaching Team", panel);
            Assert.Contains("No Homeroom Teacher assigned.", panel);
            Assert.Contains("No Co-Teacher assigned.", panel);
            Assert.Contains("Edit Teaching Team", panel);
            Assert.DoesNotContain("Save Changes", panel);
            Assert.DoesNotContain("data-team-co", panel);
            Assert.DoesNotContain(student, panel);

            // Edit: the same searchable dropdown for both roles, with Save Changes and Cancel.
            var edit = WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/{id}?panel=team&edit=1"));
            Assert.Contains("name=\"HomeroomId\"", edit);
            Assert.Contains("data-team-co", edit);
            Assert.Contains("Save Changes", edit);
            Assert.Contains("Cancel", edit);
        }
        foreach (var prefix in new[] { "G2", "G5", "G8", "G10" }) // Elementary (lower, upper), Middle School, High School
        {
            var (id, _) = await NewClassAsync(prefix);
            var (_, student) = await fx.NewStudentAsync(id);
            foreach (var url in new[] { $"/classes/{id}", $"/classes/{id}?panel=team&edit=1" })
            {
                var response = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
                Assert.Contains(student, html); // normal class page with its students
                Assert.DoesNotContain("Teaching Team", html);
                Assert.DoesNotContain("Homeroom", html);
            }
        }
    }

    [Fact]
    public async Task Class_page_student_list_is_limited_to_the_class_and_reloads_on_its_own()
    {
        var (mine, _) = await NewClassAsync("KG1");
        var (other, _) = await NewClassAsync("KG1");
        var (_, inMine) = await fx.NewStudentAsync(mine);
        var (_, elsewhere) = await fx.NewStudentAsync(other);
        var client = fx.Factory.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/{mine}"));
        Assert.Contains(inMine, html);
        Assert.DoesNotContain(elsewhere, html);
        Assert.DoesNotContain("/students/export", html); // "export all" is not offered on a class page

        // Sorting / paging / refresh after an edit load just the list (AJAX), still for this class only.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/classes/{mine}?sort=name&dir=desc");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var partial = WebUtility.HtmlDecode(await (await client.SendAsync(request)).Content.ReadAsStringAsync());
        Assert.Contains(inMine, partial);
        Assert.DoesNotContain(elsewhere, partial);
        Assert.DoesNotContain("page-head", partial);
    }

    [Fact]
    public async Task Assignments_to_non_KG_classes_are_rejected_server_side()
    {
        var yearId = await NewYearAsync();
        var (g4, _) = await NewClassAsync("G4");
        var (staff, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        Fails(await Teams(db).SaveAsync(g4, yearId, staff, null), "not a KG Stage class");
        Assert.Null(await Teams(db).TeamAsync(g4, yearId));
        Assert.False(await db.ClassTeachingAssignments.AnyAsync(a => a.SchoolClassId == g4));

        // Through the endpoint too (the form is not on the page, so the token comes from a KG class page).
        var (kg, _) = await NewClassAsync("KG2");
        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync($"/classes/{kg}?panel=team&edit=1");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", Token.Match(page).Groups[1].Value), new("yearId", yearId.ToString()), new("HomeroomId", staff.ToString()),
        };
        await client.PostAsync($"/classes/{g4}/team", new FormUrlEncodedContent(fields));
        Assert.False(await db.ClassTeachingAssignments.AnyAsync(a => a.SchoolClassId == g4));
    }

    // ------------------------------------------------------------------ assignment rules

    [Fact]
    public async Task One_homeroom_and_one_co_teacher_replacements_are_kept_and_no_second_of_either_role()
    {
        var yearId = await NewYearAsync();
        var (cls, _) = await NewClassAsync("KG1");
        var (a, _) = await fx.NewTeacherAsync();
        var (b, _) = await fx.NewTeacherAsync();
        var (c, cName) = await fx.NewTeacherAsync();
        var (d, dName) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var teams = Teams(db);

        // One Homeroom Teacher and one Co-Teacher at the same time.
        Ok(await teams.SaveAsync(cls, yearId, a, b));
        var team = (await teams.TeamAsync(cls, yearId))!;
        Assert.Equal((a, b, false), (team.Homeroom!.StaffId, team.CoTeacher!.StaffId, team.HasExtraCoTeachers));

        // Same person in both roles, an unknown staff member: rejected, nothing changes.
        Fails(await teams.SaveAsync(cls, yearId, a, a), "both roles");
        Fails(await teams.SaveAsync(cls, yearId, a, int.MaxValue), "from the list");
        Assert.Equal(2, await db.ClassTeachingAssignments.CountAsync(x => x.SchoolClassId == cls && x.IsActive));

        // The database itself refuses a second active Homeroom Teacher and a second active Co-Teacher.
        foreach (var role in new[] { ClassTeachingRole.HomeroomTeacher, ClassTeachingRole.CoTeacher })
        {
            await using var raw = fx.NewContext();
            raw.ClassTeachingAssignments.Add(new ClassTeachingAssignment { SchoolClassId = cls, AcademicYearId = yearId, StaffMemberId = d, Role = role, AssignedAt = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync());
        }

        // Replacing the Co-Teacher works exactly like replacing the Homeroom Teacher: the previous assignment is ended
        // and kept, the replacement logged; the other role is untouched.
        var aRow = team.Homeroom.AssignmentId;
        var bRow = team.CoTeacher.AssignmentId;
        Ok(await teams.SaveAsync(cls, yearId, a, c));
        team = (await teams.TeamAsync(cls, yearId))!;
        Assert.Equal((aRow, c), (team.Homeroom!.AssignmentId, team.CoTeacher!.StaffId));
        var oldCo = await db.ClassTeachingAssignments.AsNoTracking().SingleAsync(x => x.Id == bRow);
        Assert.Equal((false, $"Replaced by {cName}"), (oldCo.IsActive, oldCo.EndReason));

        var cRow = team.CoTeacher.AssignmentId;
        Ok(await teams.SaveAsync(cls, yearId, d, c));
        team = (await teams.TeamAsync(cls, yearId))!;
        Assert.Equal((d, cRow), (team.Homeroom!.StaffId, team.CoTeacher!.AssignmentId));
        var oldHr = await db.ClassTeachingAssignments.AsNoTracking().SingleAsync(x => x.Id == aRow);
        Assert.Equal((false, $"Replaced by {dName}"), (oldHr.IsActive, oldHr.EndReason));

        // Removing one role leaves the other alone; the row is ended, not deleted.
        Ok(await teams.RemoveAsync(cls, cRow));
        team = (await teams.TeamAsync(cls, yearId))!;
        Assert.Equal(d, team.Homeroom!.StaffId);
        Assert.Null(team.CoTeacher);

        // Moving someone between roles is a role change; a class may also have no team at all.
        Ok(await teams.SaveAsync(cls, yearId, c, d));
        Ok(await teams.SaveAsync(cls, yearId, null, null));
        team = (await teams.TeamAsync(cls, yearId))!;
        Assert.Null(team.Homeroom);
        Assert.Null(team.CoTeacher);
        Fails(await teams.SaveAsync(cls, yearId, null, null), "Nothing was changed");

        // History: both kinds of replacement, the removal and the role change; every row is kept.
        var history = await db.ClassTeachingHistory.AsNoTracking().Where(h => h.SchoolClassId == cls).ToListAsync();
        Assert.All(history, h => { Assert.Equal(yearId, h.AcademicYearId); Assert.Equal("Test runner", h.By); });
        Assert.Contains(history, h => h.StaffMemberId == b && h.Operation == ClassTeachingOperation.Replaced && h.PreviousRole == ClassTeachingRole.CoTeacher
            && h.Details == $"Replaced as Co-Teacher by {cName}");
        Assert.Contains(history, h => h.StaffMemberId == a && h.Operation == ClassTeachingOperation.Replaced && h.PreviousRole == ClassTeachingRole.HomeroomTeacher
            && h.Details == $"Replaced as Homeroom Teacher by {dName}");
        Assert.Contains(history, h => h.StaffMemberId == c && h.Operation == ClassTeachingOperation.Removed && h.PreviousRole == ClassTeachingRole.CoTeacher);
        Assert.Contains(history, h => h.StaffMemberId == d && h.Operation == ClassTeachingOperation.RoleChanged
            && h.PreviousRole == ClassTeachingRole.HomeroomTeacher && h.NewRole == ClassTeachingRole.CoTeacher);
        Assert.Equal(6, await db.ClassTeachingAssignments.CountAsync(x => x.SchoolClassId == cls)); // a, b, c, d, then c as homeroom and d as co: none deleted
        Assert.Equal(0, await db.ClassTeachingAssignments.CountAsync(x => x.SchoolClassId == cls && x.IsActive));

        // Removing an already-ended assignment is refused.
        Fails(await teams.RemoveAsync(cls, bRow), "already been removed");
    }

    [Fact]
    public async Task Several_active_co_teachers_from_before_are_shown_and_only_the_chosen_one_stays_when_saved()
    {
        var yearId = await NewYearAsync();
        var (cls, _) = await NewClassAsync("KG2");
        var (a, _) = await fx.NewTeacherAsync();
        var (b, _) = await fx.NewTeacherAsync();
        var (c, cName) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        // Data saved under the old rule: two active Co-Teachers (the one-Co-Teacher index is paused to recreate it).
        await db.Database.ExecuteSqlRawAsync("ALTER INDEX [UX_ClassTeaching_CoTeacher] ON [ClassTeachingAssignments] DISABLE");
        try
        {
            foreach (var staff in new[] { b, c })
                db.ClassTeachingAssignments.Add(new ClassTeachingAssignment { SchoolClassId = cls, AcademicYearId = yearId, StaffMemberId = staff, Role = ClassTeachingRole.CoTeacher, AssignedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var teams = Teams(db);

            // Shown as they are, flagged, nothing changed automatically.
            var team = (await teams.TeamAsync(cls, yearId))!;
            Assert.Equal((true, 2), (team.HasExtraCoTeachers, team.CoTeachers.Count));
            var page = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync($"/classes/{cls}?panel=team&year={yearId}"));
            Assert.Contains("2 active Co-Teachers from before the one-Co-Teacher rule", page);
            Assert.Equal(2, await db.ClassTeachingAssignments.CountAsync(x => x.SchoolClassId == cls && x.IsActive));

            // Saving with one chosen Co-Teacher keeps that one; the other is ended (kept, with the reason).
            Ok(await teams.SaveAsync(cls, yearId, a, c));
            team = (await teams.TeamAsync(cls, yearId))!;
            Assert.Equal((a, c, false), (team.Homeroom!.StaffId, team.CoTeacher!.StaffId, team.HasExtraCoTeachers));
            var ended = await db.ClassTeachingAssignments.AsNoTracking().SingleAsync(x => x.SchoolClassId == cls && x.StaffMemberId == b);
            Assert.Equal((false, $"One Co-Teacher per class: {cName} kept"), (ended.IsActive, ended.EndReason));
        }
        finally
        {
            await db.ClassTeachingAssignments.Where(x => x.SchoolClassId == cls && x.IsActive && x.Role == ClassTeachingRole.CoTeacher && x.StaffMemberId == b)
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.IsActive, false).SetProperty(r => r.EndedAt, DateTime.UtcNow));
            await db.Database.ExecuteSqlRawAsync("ALTER INDEX [UX_ClassTeaching_CoTeacher] ON [ClassTeachingAssignments] REBUILD");
        }
    }

    // ------------------------------------------------------------------ academic years, profile, integration

    [Fact]
    public async Task Teams_are_per_academic_year_history_stays_and_staff_profile_lists_them()
    {
        var y1 = await NewYearAsync();
        var y2 = await NewYearAsync();
        var (kg1, kg1Name) = await NewClassAsync("KG1");
        var (kg2, kg2Name) = await NewClassAsync("KG2");
        var (a, _) = await fx.NewTeacherAsync();
        var (b, _) = await fx.NewTeacherAsync();
        var (c, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var teams = Teams(db);

        Ok(await teams.SaveAsync(kg1, y1, a, b));
        Ok(await teams.SaveAsync(kg1, y2, c, a)); // a new year: different team, y1 untouched
        Ok(await teams.SaveAsync(kg2, y1, null, a));

        var t1 = (await teams.TeamAsync(kg1, y1))!;
        var t2 = (await teams.TeamAsync(kg1, y2))!;
        Assert.Equal(a, t1.Homeroom!.StaffId);
        Assert.Equal(b, t1.CoTeacher!.StaffId);
        Assert.Equal(c, t2.Homeroom!.StaffId);
        Assert.Equal(a, t2.CoTeacher!.StaffId);

        // Staff profile data: every KG assignment, by year, with role and status (ended ones kept).
        Ok(await teams.SaveAsync(kg2, y1, null, null));
        var mine = await teams.ForStaffAsync(a);
        Assert.Contains(mine, x => x.ClassId == kg1 && x.YearId == y1 && x.Role == ClassTeachingRole.HomeroomTeacher && x.IsActive);
        Assert.Contains(mine, x => x.ClassId == kg1 && x.YearId == y2 && x.Role == ClassTeachingRole.CoTeacher && x.IsActive);
        Assert.Contains(mine, x => x.ClassId == kg2 && x.YearId == y1 && x.Role == ClassTeachingRole.CoTeacher && !x.IsActive && x.EndedAt is not null);

        // Pages: the staff profile shows the section; the class page shows the chosen year's team.
        var client = fx.Factory.CreateClient();
        var profile = WebUtility.HtmlDecode(await client.GetStringAsync($"/staff/{a}"));
        Assert.Contains("KG Class Assignments", profile);
        Assert.Contains($"href=\"/classes/{kg1}?panel=team&year={y1}\"", profile);
        Assert.Contains(kg2Name, profile);
        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/{kg1}?panel=team&year={y2}"));
        Assert.Contains($"{kg1Name} — Class Teaching Team", page);
        Assert.Contains("Previous assignments", WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/{kg2}?panel=team&year={y1}")));
    }

    [Fact]
    public async Task Save_and_remove_work_through_the_page_and_show_validation_messages()
    {
        var yearId = await NewYearAsync();
        var (cls, _) = await NewClassAsync("PreK");
        var (a, aName) = await fx.NewTeacherAsync();
        var (b, bName) = await fx.NewTeacherAsync();
        var client = fx.Factory.CreateClient();
        async Task<HttpResponseMessage> Post(string action, params (string, string)[] values)
        {
            var page = await client.GetStringAsync($"/classes/{cls}?panel=team&edit=1&year={yearId}");
            var fields = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", Token.Match(page).Groups[1].Value), new("yearId", yearId.ToString()) };
            fields.AddRange(values.Select(v => new KeyValuePair<string, string>(v.Item1, v.Item2)));
            return await client.PostAsync(action, new FormUrlEncodedContent(fields));
        }

        // The edit form: the same searchable dropdown for both roles.
        var form = await client.GetStringAsync($"/classes/{cls}?panel=team&edit=1&year={yearId}");
        Assert.Contains("<select class=\"select\" id=\"HomeroomId\" name=\"HomeroomId\" data-searchable", form);
        Assert.Contains("<select class=\"select\" id=\"CoTeacherId\" name=\"CoTeacherId\" data-searchable", form);
        Assert.DoesNotContain("name=\"CoTeacherIds\"", form);

        var saved = await Post($"/classes/{cls}/team", ("HomeroomId", a.ToString()), ("CoTeacherId", b.ToString()));
        var html = WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Contains("Class teaching team saved.", html);
        Assert.Contains(aName, html);
        Assert.Contains(bName, html);

        var bad = await Post($"/classes/{cls}/team", ("HomeroomId", a.ToString()), ("CoTeacherId", a.ToString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("cannot hold both roles", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));

        // A direct request with two Co-Teachers is refused (one per class), nothing changes.
        var (c, _) = await fx.NewTeacherAsync();
        var two = await Post($"/classes/{cls}/team", ("HomeroomId", a.ToString()), ("CoTeacherIds", b.ToString()), ("CoTeacherIds", c.ToString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, two.StatusCode);
        Assert.Contains("one Co-Teacher", WebUtility.HtmlDecode(await two.Content.ReadAsStringAsync()));

        await using var db = fx.NewContext();
        var bRow = await db.ClassTeachingAssignments.Where(x => x.SchoolClassId == cls && x.StaffMemberId == b && x.IsActive).Select(x => x.Id).SingleAsync();
        var removed = await Post($"/classes/{cls}/team/{bRow}/remove");
        Assert.Contains("Assignment removed.", WebUtility.HtmlDecode(await removed.Content.ReadAsStringAsync()));
        Assert.True(await db.ClassTeachingAssignments.AnyAsync(x => x.SchoolClassId == cls && x.StaffMemberId == a && x.IsActive));
        Assert.False(await db.ClassTeachingAssignments.AnyAsync(x => x.SchoolClassId == cls && x.StaffMemberId == b && x.IsActive));
    }

    [Fact]
    public async Task Teaching_teams_do_not_touch_timetable_workload_or_PE_and_keep_records_from_deletion()
    {
        var yearId = await NewYearAsync();
        var (cls, _) = await NewClassAsync("KG2");
        var (a, _) = await fx.NewTeacherAsync();
        var (b, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        async Task<(int Sessions, int Workload, int Pe, int Duties)> Counts() =>
            (await db.TimetableAssignments.CountAsync(), await db.WorkloadAssignments.CountAsync(), await db.PeSessionDetails.CountAsync(), await db.DutyAssignments.CountAsync());
        var before = await Counts();
        var workloadBefore = await DbFixture.Workload(db).SummaryAsync(a, SchoolWeek.Current);

        Ok(await Teams(db).SaveAsync(cls, yearId, a, b));

        Assert.Equal(before, await Counts());
        var workloadAfter = await DbFixture.Workload(db).SummaryAsync(a, SchoolWeek.Current);
        Assert.Equal(workloadBefore!.Total, workloadAfter!.Total);
        Assert.Equal(workloadBefore.Breakdown, workloadAfter.Breakdown);

        // Assignment history keeps the class and the staff members from being deleted.
        Assert.False((await DbFixture.Teachers(db).DeleteAsync(a)).Succeeded);
        Assert.False((await DbFixture.Classes(db).DeleteAsync(cls, null)).Succeeded);
        Assert.True(await db.StaffMembers.AnyAsync(s => s.Id == a));
        Assert.True(await db.Classes.AnyAsync(s => s.Id == cls));
    }
}
