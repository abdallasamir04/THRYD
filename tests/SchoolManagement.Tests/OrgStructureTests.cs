using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.OrgStructure;

namespace SchoolManagement.Tests;

/// <summary>
/// Phase 1 — organizational structure: the six tiers, units, positions and assignments recorded from the official source,
/// employee reconciliation (one record per person, never merged on a name or a shared number), history, authorization, and
/// the positions shown on staff cards, profiles, the Organizational Chart and the Management sections. Runs on the test
/// database only (DbFixture).
/// </summary>
[Collection("db")]
public class OrgStructureTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static OrgSeedReport? _first;
    private static int _preExistingSaritha;

    /// <summary>
    /// Seeds once per test run. Before it, an existing staff record for one source person (same phone, name without the
    /// honorific) is created, so the import must reuse it instead of creating a duplicate.
    /// </summary>
    private async Task<OrgSeedReport> SeededAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_first is not null) return _first;
            await using (var db = fx.NewContext())
            {
                var english = await db.Departments.FirstAsync(d => d.NormalizedName == "ENGLISH");
                var existing = new Teacher
                {
                    FullName = "Saritha", NormalizedName = "SARITHA", ContactNumber = "01091170531", NormalizedContact = "01091170531",
                    DepartmentId = english.Id, JobTitle = "English Teacher",
                };
                db.Teachers.Add(existing);
                await db.SaveChangesAsync();
                _preExistingSaritha = existing.Id;
            }
            await using var ctx = fx.NewContext();
            _first = await new OrgStructureSeeder(ctx).SeedAsync();
            return _first;
        }
        finally { Gate.Release(); }
    }

    private static OrgStructureService Service(SchoolDbContext db, IOrgStructureAccess? access = null) => new(db, access ?? TrustedOrgStructureAccess.Instance);

    private async Task<int> StaffIdAsync(string name, string phone)
    {
        await using var db = fx.NewContext();
        return await db.StaffMembers.Where(s => s.FullName == name && s.NormalizedContact == phone).Select(s => s.Id).SingleAsync();
    }

    private async Task<List<string>> CurrentTitlesAsync(int staffId)
    {
        await using var db = fx.NewContext();
        return (await Service(db).StaffAsync(staffId)).Current.Select(p => p.DisplayTitle).ToList();
    }

    /// <summary>The page's main content, HTML-decoded (Razor encodes "–" and "&amp;").</summary>
    private static string Body(string html) => System.Net.WebUtility.HtmlDecode(html[html.IndexOf("<main", StringComparison.Ordinal)..]);

    // ================================================================== structure

    [Fact]
    public async Task Six_tiers_with_their_units_and_positions_are_recorded()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        var tiers = await db.OrgTiers.Where(t => t.SourceKey != null).OrderBy(t => t.SortOrder).ToListAsync();
        Assert.Equal(["Executive Advisory & Governance", "Academic Leadership", "Parents Care, PR & Counseling", "Floor Supervisors",
            "Administrative & Operational Management", "Academic Department Leadership"], tiers.Select(t => t.Name));
        Assert.Equal([OrgSection.SchoolLeadership, OrgSection.SchoolLeadership, OrgSection.SchoolLeadership, OrgSection.OperationTeam,
            OrgSection.OperationTeam, OrgSection.SchoolLeadership], tiers.Select(t => t.Section));

        // Units belong to their tier; every official position belongs to a unit.
        Assert.Equal(OrgStructureSource.Units.Length, await db.OrgUnits.CountAsync(u => u.SourceKey != null));
        Assert.Equal(OrgStructureSource.Positions.Length, await db.StaffPositions.CountAsync(p => p.SourceKey != null));
        var floor = await db.OrgUnits.Include(u => u.Tier).SingleAsync(u => u.SourceKey == "unit-floor-supervisors");
        Assert.Equal("tier-4", floor.Tier.SourceKey);
        var tier5Units = await db.OrgUnits.Where(u => u.Tier.SourceKey == "tier-5").Select(u => u.Name).ToListAsync();
        foreach (var name in new[] { "Operations & Student Conduct Department", "Facilities & Infrastructure Unit", "Campus Safety & Security Unit", "IT & Logistics Units",
                     "LMS Support", "Copy Center", "Bookstore & Student Distribution", "Storage & Inventory", "Inclusion Department", "Facilities & Maintenance" })
            Assert.Contains(name, tier5Units);
    }

    [Fact]
    public async Task Academic_departments_are_reused_not_duplicated()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        // No English, French, German, Arabic or Religion department was added.
        foreach (var key in new[] { "ENGLISH", "FRENCH", "GERMAN", "ARABIC", "RELIGION", "ASS", "ESS" })
            Assert.True(await db.Departments.CountAsync(d => d.NormalizedName == key) <= 1, key);
        Assert.False(await db.Departments.AnyAsync(d => d.NormalizedName == "FRENCH" || d.NormalizedName == "GERMAN"));

        var links = await db.OrgUnitDepartments.Include(l => l.Department).Include(l => l.Subcategory).Include(l => l.Unit).ToListAsync();
        string[] Of(string unit) => links.Where(l => l.Unit.SourceKey == unit).Select(l => l.Subcategory is null ? l.Department.Name : $"{l.Department.Name}/{l.Subcategory.Name}").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["ESS", "English"], Of("unit-dept-english"));
        Assert.Equal(["Arabic", "Religion"], Of("unit-dept-arabic-religion"));
        Assert.Equal(["ASS"], Of("unit-dept-ass"));
        Assert.Equal(["Second Language/French"], Of("unit-dept-french"));
        Assert.Equal(["Second Language/Deutsch"], Of("unit-dept-german"));
    }

    [Fact]
    public async Task Seeding_twice_creates_nothing_new()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        int Count() => db.StaffMembers.Count() + db.OrgTiers.Count() + db.OrgUnits.Count() + db.StaffPositions.Count() + db.StaffPositionAssignments.Count() + db.StaffDataQualityFlags.Count();
        var before = Count();
        var again = await new OrgStructureSeeder(db).SeedAsync();
        Assert.False(again.ChangedAnything);
        Assert.Equal(0, again.StaffCreated);
        Assert.Equal(OrgStructureSource.People.Length - again.Conflicts.Count, again.StaffReused);
        Assert.Equal(OrgStructureSource.Assignments.Length, again.AssignmentsAlreadyRecorded);
        Assert.Equal(before, Count());
    }

    [Fact]
    public async Task An_existing_record_with_the_same_phone_and_name_is_reused()
    {
        var report = await SeededAsync();
        var saritha = report.People.Single(p => p.Key == "p-saritha");
        Assert.Equal(SeedOutcome.Reused, saritha.Outcome);
        Assert.Equal(_preExistingSaritha, saritha.StaffId);
        await using var db = fx.NewContext();
        Assert.Equal(1, await db.StaffMembers.CountAsync(s => s.NormalizedContact == "01091170531"));
        var record = await db.StaffMembers.SingleAsync(s => s.Id == _preExistingSaritha);
        Assert.Equal("Saritha", record.FullName);            // existing details are never overwritten
        Assert.Equal("English Teacher", record.JobTitle);
        Assert.Equal(["Team Leader (Grades 1–3)"], await CurrentTitlesAsync(_preExistingSaritha));
    }

    // ================================================================== people and assignments

    [Fact]
    public async Task People_with_several_positions_have_one_record_and_every_assignment()
    {
        await SeededAsync();
        var expected = new (string Name, string Phone, string[] Titles)[]
        {
            ("Ms. Donia Mostafa", "01145309275", ["Floor Supervisor", "Bookstore & Student Distribution Coordinator", "Storage Room / Inventory Clerk", "Facilities & Maintenance Supervisor"]),
            ("Ms. Basma Farghali", "01067103468", ["Academic Head (Grades 2–10)", "Head of Stages (Grades 2–6)"]),
            ("Ms. Merna Ahmed", "01274961909", ["Admissions Tour Specialist", "School Receptionist"]),
            ("Mr. Mohamed Abdelrasol", "01025172837", ["Operations & Student Conduct Department", "Facilities & Infrastructure Unit", "Campus Safety & Security Unit"]),
            ("Mr. Yusuf Shabaan", "01225569068", ["Operations & Student Conduct Department", "Facilities & Infrastructure Unit", "Campus Safety & Security Unit"]),
            ("Ms. Marwa Safwat", "01001224414", ["Public Relations (PR)", "LMS Support"]),
            ("Ms. Eman Gooda", "01028355886", ["Academic LMS Reviser", "Team Leader (Grades 4–10)"]),
        };
        await using var db = fx.NewContext();
        foreach (var (name, phone, titles) in expected)
        {
            Assert.Equal(1, await db.StaffMembers.CountAsync(s => s.NormalizedContact == phone && s.FullName == name));
            var id = await StaffIdAsync(name, phone);
            var held = await CurrentTitlesAsync(id);
            Assert.Equal(titles.Order(), held.Order());
            Assert.Equal(titles[0], held[0]); // primary = first listed in the source
            Assert.Equal(1, await db.StaffPositionAssignments.CountAsync(a => a.StaffMemberId == id && a.IsCurrent && a.IsPrimary));
        }
    }

    [Fact]
    public async Task Executive_tier_has_its_six_people_with_titles_and_phones_exactly_as_the_source()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        var tier1 = await db.StaffPositionAssignments.Where(a => a.IsCurrent && a.Position.Unit.Tier.SourceKey == "tier-1")
            .Select(a => new { a.Position.Title, a.StaffMember.FullName, a.StaffMember.ContactNumber }).ToListAsync();
        Assert.Equal(6, tier1.Count);
        foreach (var (title, name, phone) in new[]
                 {
                     ("School Principal / Head of School", "Dr. Noha Osama", "01111219254"), ("School Consultant", "Dr. Reham Gamil", "01009602017"),
                     ("School Deputy Headmaster", "Mr. Mohamed Abdul-Rasoul", "01004748355"), ("Academic Dean", "Dr. Rania Moh. Hijazi", "01020975325"),
                     ("Quality Assurance Manager", "Ms. Nour Aladawy", "01100626917"), ("Assistant Principal", "Farah Akram", "01061182914"),
                 })
            Assert.Contains(tier1, x => x.Title == title && x.FullName == name && x.ContactNumber == phone);
    }

    [Fact]
    public async Task Identity_conflicts_are_kept_as_separate_people()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        // Deputy Headmaster ≠ Operations: two people.
        var deputy = await StaffIdAsync("Mr. Mohamed Abdul-Rasoul", "01004748355");
        var operations = await StaffIdAsync("Mr. Mohamed Abdelrasol", "01025172837");
        Assert.NotEqual(deputy, operations);
        Assert.Equal(["School Deputy Headmaster"], await CurrentTitlesAsync(deputy));

        // The two Mohamed Ali: Math Coordinator and German HOD.
        var aliMath = await StaffIdAsync("Mr. Mohamed Ali", "01112191030");
        var aliGerman = await StaffIdAsync("Mr. Mohamed Ali", "01032853184");
        Assert.NotEqual(aliMath, aliGerman);
        Assert.Equal(["Math Coordinator (Grades 2–9)"], await CurrentTitlesAsync(aliMath));
        Assert.Equal(["HOD (PreK to Grade 3)"], await CurrentTitlesAsync(aliGerman));

        // The shared number: two records, both flagged for verification.
        var brakat = await StaffIdAsync("Mr. Mahmoud Brakat", "01025172837");
        Assert.NotEqual(brakat, operations);
        Assert.Equal(["Floor Supervisor"], await CurrentTitlesAsync(brakat));
        foreach (var id in new[] { brakat, operations })
            Assert.True(await db.StaffDataQualityFlags.AnyAsync(f => f.StaffMemberId == id && f.Kind == DataQualityFlagKind.SharedPhoneNumber && !f.IsResolved));
    }

    [Fact]
    public async Task Nour_Tarek_is_recorded_for_confirmation_without_a_position()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        var id = await StaffIdAsync("Mr. Nour Tarek", "01020492629");
        Assert.False(await db.StaffPositionAssignments.AnyAsync(a => a.StaffMemberId == id));
        Assert.True(await db.StaffDataQualityFlags.AnyAsync(f => f.StaffMemberId == id && f.Kind == DataQualityFlagKind.PositionRequiresConfirmation && !f.IsResolved));
        var row = await DbFixture.Management(db).GetAsync(id);
        Assert.Equal(OrgLabels.PendingPosition, row!.PositionName);
        Assert.False(row.HasPosition);
    }

    [Fact]
    public async Task German_has_two_HODs_by_grade_range_and_floor_supervisors_have_no_invented_floors()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        var german = await db.StaffPositionAssignments.Where(a => a.IsCurrent && a.Position.Unit.SourceKey == "unit-dept-german")
            .Select(a => new { a.Position.Title, a.Position.GradeRange, a.StaffMember.FullName }).ToListAsync();
        Assert.Equal(2, german.Count);
        Assert.All(german, g => Assert.Equal("HOD", g.Title));
        Assert.Contains(german, g => g.GradeRange == "Grades 4–10" && g.FullName == "Ms. Habiba Essam");
        Assert.Contains(german, g => g.GradeRange == "PreK to Grade 3" && g.FullName == "Mr. Mohamed Ali");

        var floor = await db.StaffPositionAssignments.Where(a => a.IsCurrent && a.Position.SourceKey == "pos-floor-supervisor").ToListAsync();
        Assert.Equal(4, floor.Count);
        Assert.All(floor, a => Assert.Null(a.Scope));
        Assert.All(floor, a => Assert.Null(a.ReportsToStaffMemberId));
        Assert.False(await db.StaffPositionAssignments.AnyAsync(a => a.SourceKey != null && a.ReportsToStaffMemberId != null));
    }

    [Fact]
    public async Task Tier_six_leaders_are_teachers_in_their_existing_departments()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        var enjy = await db.Teachers.Include(t => t.Department).SingleAsync(t => t.NormalizedContact == "01005645393");
        Assert.Equal("Second Language", enjy.Department!.Name);
        Assert.Equal("French", enjy.Subcategory);
        var habiba = await db.Teachers.Include(t => t.Department).SingleAsync(t => t.NormalizedContact == "01146642303");
        Assert.Equal(("Second Language", "Deutsch"), (habiba.Department!.Name, habiba.Subcategory));
        var fatma = await db.Teachers.Include(t => t.Department).SingleAsync(t => t.NormalizedContact == "01009149132");
        Assert.Equal("ASS", fatma.Department!.Name);
        // Leaders are not the full teaching staff: nobody else was added to the departments by the import.
        var report = await SeededAsync();
        Assert.Equal(OrgStructureSource.People.Length, report.People.Count);
    }

    // ================================================================== assignment rules

    [Fact]
    public async Task Assignments_keep_history_one_primary_and_no_duplicates()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        var org = Service(db);
        var unit = await db.OrgUnits.FirstAsync(u => u.SourceKey == "unit-it-logistics");
        var a = (await org.SavePositionAsync(null, new StaffPositionInput { Title = "Test Role A " + Unique(), UnitId = unit.Id, RoleType = LeadershipRoleType.Operational })).EntityId!.Value;
        var b = (await org.SavePositionAsync(null, new StaffPositionInput { Title = "Test Role B " + Unique(), UnitId = unit.Id, RoleType = LeadershipRoleType.Operational, GradeRange = "Grades 1–3" })).EntityId!.Value;
        var (staff, _) = await fx.NewTeacherAsync();

        var first = await org.AssignAsync(new AssignmentInput { StaffMemberId = staff, PositionId = a });
        Assert.True(first.Succeeded);
        var second = await org.AssignAsync(new AssignmentInput { StaffMemberId = staff, PositionId = b, IsPrimary = true });
        Assert.True(second.Succeeded);
        // The same position twice at once is refused.
        Assert.False((await org.AssignAsync(new AssignmentInput { StaffMemberId = staff, PositionId = a })).Succeeded);
        // Reporting to oneself is refused.
        Assert.False((await org.AssignAsync(new AssignmentInput { StaffMemberId = staff, PositionId = b, ReportsToStaffMemberId = staff })).Succeeded);

        var summary = await org.StaffAsync(staff);
        Assert.Equal(2, summary.Current.Count);
        Assert.Equal(b, summary.Primary!.PositionId);                    // the new primary took over
        Assert.Single(summary.Current, p => p.IsPrimary);
        Assert.Equal(summary.Primary.DisplayTitle, (await db.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == staff)).JobTitle);

        // Ending the primary keeps it as history and promotes the remaining position.
        Assert.True((await org.EndAssignmentAsync(second.EntityId!.Value, SchoolWeek.Today, "Reorganized")).Succeeded);
        summary = await org.StaffAsync(staff);
        Assert.Equal([a], summary.Current.Select(p => p.PositionId));
        Assert.True(summary.Current[0].IsPrimary);
        var ended = Assert.Single(summary.Ended);
        Assert.Equal(("Reorganized", SchoolWeek.Today), (ended.EndReason, ended.EndDate));

        // A change of position ends the old assignment and starts a new one: nothing is deleted.
        var moved = await org.UpdateAssignmentAsync(first.EntityId!.Value, new AssignmentInput { PositionId = b, StartDate = SchoolWeek.Today });
        Assert.True(moved.Succeeded);
        summary = await org.StaffAsync(staff);
        Assert.Equal([b], summary.Current.Select(p => p.PositionId));
        Assert.Equal(2, summary.Ended.Count);
        Assert.Equal(3, await db.StaffPositionAssignments.CountAsync(x => x.StaffMemberId == staff));

        // A position with history cannot be deleted (only deactivated once its assignments end).
        Assert.False((await org.DeletePositionAsync(a)).Succeeded);
        Assert.False((await org.SavePositionAsync(b, new StaffPositionInput { Title = "x " + Unique(), UnitId = unit.Id, RoleType = LeadershipRoleType.Operational, IsActive = false })).Succeeded);

        // The staff record with organizational history is kept.
        var deleted = await DbFixture.Teachers(db).DeleteAsync(staff);
        Assert.False(deleted.Succeeded);
        Assert.True(await db.StaffMembers.AnyAsync(s => s.Id == staff));
    }

    [Fact]
    public async Task Tiers_units_and_positions_can_be_created_and_validated()
    {
        await using var db = fx.NewContext();
        var org = Service(db);
        var tier = await org.SaveTierAsync(null, new OrgTierInput { Name = "Test Tier " + Unique(), Section = OrgSection.OperationTeam, SortOrder = 50 });
        Assert.True(tier.Succeeded);
        Assert.False((await org.SaveTierAsync(null, new OrgTierInput { Name = "", Section = OrgSection.OperationTeam })).Succeeded);
        var math = await db.Departments.FirstAsync(d => d.NormalizedName == "MATH");
        var parent = await org.SaveUnitAsync(null, new OrgUnitInput { Name = "Parent " + Unique(), TierId = tier.EntityId!.Value, DepartmentIds = [math.Id] });
        Assert.True(parent.Succeeded);
        var child = await org.SaveUnitAsync(null, new OrgUnitInput { Name = "Child " + Unique(), TierId = tier.EntityId.Value, ParentUnitId = parent.EntityId });
        Assert.True(child.Succeeded);
        // A unit cannot be put under its own sub-unit.
        var cycle = await org.SaveUnitAsync(parent.EntityId, new OrgUnitInput { Name = "Parent cycle " + Unique(), TierId = tier.EntityId.Value, ParentUnitId = child.EntityId, DepartmentIds = [math.Id] });
        Assert.False(cycle.Succeeded);
        var position = await org.SavePositionAsync(null, new StaffPositionInput { Title = "Lead", UnitId = child.EntityId!.Value, RoleType = LeadershipRoleType.Coordinator, GradeRange = "Grades 1–3" });
        Assert.True(position.Succeeded);
        // The same title is allowed for another grade range, not twice for the same one.
        Assert.True((await org.SavePositionAsync(null, new StaffPositionInput { Title = "Lead", UnitId = child.EntityId.Value, RoleType = LeadershipRoleType.Coordinator, GradeRange = "Grades 4–6" })).Succeeded);
        Assert.False((await org.SavePositionAsync(null, new StaffPositionInput { Title = "lead", UnitId = child.EntityId.Value, RoleType = LeadershipRoleType.Coordinator, GradeRange = "Grades 1–3" })).Succeeded);
        // Never assigned: it can be deleted.
        Assert.True((await org.DeletePositionAsync(position.EntityId!.Value)).Succeeded);

        var tree = await org.StructureAsync(true);
        var node = tree.Tiers.Single(t => t.Id == tier.EntityId);
        Assert.Equal(child.EntityId, Assert.Single(Assert.Single(node.Units).Children).Id); // nested under its parent
        Assert.True(node.Units[0].IsAcademicDepartment);
    }

    [Fact]
    public async Task Changes_need_organizational_structure_access()
    {
        await SeededAsync();
        await using (var db = fx.NewContext())
        {
            var denied = new OrgStructureService(db, new DenyAccess());
            await Assert.ThrowsAsync<OrgStructureAccessDeniedException>(() => denied.SaveTierAsync(null, new OrgTierInput { Name = "Nope", Section = OrgSection.SchoolLeadership }));
            await Assert.ThrowsAsync<OrgStructureAccessDeniedException>(() => denied.AssignAsync(new AssignmentInput { StaffMemberId = 1, PositionId = 1 }));
        }

        // A staff member working as themselves (not the Super Admin, not configured) can view but not change.
        var (teacher, _) = await fx.NewTeacherAsync();
        var client = fx.ClientAs(fx.Factory, teacher, follow: false);
        var page = await client.GetStringAsync(OrgLabels.PositionsHref);
        Assert.Contains("View only", page);
        Assert.DoesNotContain("/management/org/assignments/create\" data-modal", page);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/management/org/tiers/create")).StatusCode);
        var adminForm = await fx.Factory.CreateClient().GetStringAsync("/management/org/tiers/create");
        var token = Token.Match(adminForm).Groups[1].Value;
        var post = await client.PostAsync("/management/org/tiers/create", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("Name", "Blocked " + Unique()), new("Section", "SchoolLeadership")]));
        Assert.True(post.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, post.StatusCode.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(OrgLabels.OrgChartHref)).StatusCode);

        // The Super Admin (Administration mode) can.
        var admin = fx.Factory.CreateClient();
        var form = await admin.GetStringAsync("/management/org/tiers/create");
        var name = "Created Tier " + Unique();
        var created = await admin.PostAsync("/management/org/tiers/create", new FormUrlEncodedContent([new("__RequestVerificationToken", Token.Match(form).Groups[1].Value),
            new("Name", name), new("Section", "OperationTeam"), new("SortOrder", "60"), new("IsActive", "true")]));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        await using var check = fx.NewContext();
        Assert.True(await check.OrgTiers.AnyAsync(t => t.Name == name));
    }

    // ================================================================== pages

    [Fact]
    public async Task Staff_directory_cards_show_the_official_positions()
    {
        await SeededAsync();
        var client = fx.Factory.CreateClient();
        var html = Body(await client.GetStringAsync("/staff/all?search=Noha"));
        Assert.Contains("Dr. Noha Osama", html);
        Assert.Contains("School Principal / Head of School", html);
        Assert.Contains("Executive Directorate · Executive Advisory & Governance", html);
        Assert.DoesNotContain("No position yet", html);
        Assert.DoesNotContain("No position assigned", html);

        // One row per person, with the additional positions as badges.
        html = Body(await client.GetStringAsync("/staff/all?search=Donia"));
        Assert.Single(Regex.Matches(html, "class=\"dir-row\""));
        foreach (var title in new[] { "Floor Supervisor", "Bookstore & Student Distribution Coordinator", "Storage Room / Inventory Clerk", "Facilities & Maintenance Supervisor" })
            Assert.Contains(title, html);

        // Every Phase 1 employee with an assignment shows a position, never "No position".
        html = Body(await client.GetStringAsync("/staff/all?category=management"));
        await using var db = fx.NewContext();
        var assigned = await db.StaffPositionAssignments.Where(a => a.IsCurrent && a.SourceKey != null && a.StaffMember is ManagementMember)
            .Select(a => a.StaffMemberId).Distinct().ToListAsync();
        foreach (var id in assigned)
        {
            var row = Regex.Match(html, $"<article class=\"dir-row\" aria-labelledby=\"sd-{id}\">(.*?)</article>", RegexOptions.Singleline);
            Assert.True(row.Success, $"row {id}");
            Assert.DoesNotContain("No position", row.Value);
        }
        // The unreadable title shows as pending confirmation.
        Assert.Contains(OrgLabels.PendingPosition, Body(await client.GetStringAsync("/staff/all?search=Nour+Tarek")));
        // Search by position title; tier 6 leaders are academic staff.
        Assert.Contains("Ms. Habiba Essam", Body(await client.GetStringAsync("/staff/all?search=HOD")));
        Assert.Contains(">Saritha<", Body(await client.GetStringAsync("/staff/all?category=academic&search=Team+Leader")));
    }

    [Fact]
    public async Task Staff_profiles_show_every_position()
    {
        await SeededAsync();
        var client = fx.Factory.CreateClient();
        var donia = await StaffIdAsync("Ms. Donia Mostafa", "01145309275");
        var html = Body(await client.GetStringAsync($"/management/members/{donia}"));
        Assert.Contains("Organizational positions", html);
        Assert.Contains("4 current positions", html);
        Assert.Contains("Primary position", html);
        Assert.Contains("Floor Supervisors", html);
        Assert.DoesNotContain("No position assigned", html);

        var eman = await StaffIdAsync("Ms. Eman Gooda", "01028355886");
        html = Body(await client.GetStringAsync($"/staff/{eman}"));
        Assert.Contains("Academic LMS Reviser", html);
        Assert.Contains("Team Leader", html);
        Assert.Contains("Grades 4–10", html);
        Assert.Contains("English, ESS", html);

        var nour = await StaffIdAsync("Mr. Nour Tarek", "01020492629");
        html = Body(await client.GetStringAsync($"/management/members/{nour}"));
        Assert.Contains(OrgLabels.PendingPosition, html);
    }

    [Fact]
    public async Task Organizational_chart_and_tier_views_render_the_structure()
    {
        await SeededAsync();
        var client = fx.Factory.CreateClient();
        var chart = Body(await client.GetStringAsync(OrgLabels.OrgChartHref));
        foreach (var p in OrgStructureSource.People.Where(p => p.Key is not "p-nour-tarek" and not "p-saritha"))
            Assert.Contains(p.Name, chart);
        foreach (var t in OrgStructureSource.Tiers) Assert.Contains(t.Name, chart);
        Assert.Contains("data-org-search", chart);
        Assert.Contains("data-org-expand", chart);
        Assert.Contains("Mr. Nour Tarek", chart); // the review warning

        var executive = Body(await client.GetStringAsync(OrgLabels.ExecutiveHref));
        Assert.Equal(6, Regex.Matches(executive, "class=\"org-person\"").Count);
        Assert.Contains("01111219254", executive);

        var academic = Body(await client.GetStringAsync(OrgLabels.AcademicLeadershipHref));
        foreach (var text in new[] { "Academic Deputy", "Academic Head", "Grades 2–10", "Head of Stages", "Grades 2–6", "Academic LMS Reviser", "Human Resources (HR)", "Math Coordinator" })
            Assert.Contains(text, academic);

        var operations = Body(await client.GetStringAsync(OrgLabels.OperationsHref));
        foreach (var text in new[] { "Parents Care", "Public Relations (PR)", "Admissions", "Reception", "Counseling", "Discipline Office", "Floor Supervisors", "Copy Center", "Inclusion Department" })
            Assert.Contains(text, operations);

        var departments = Body(await client.GetStringAsync(OrgLabels.DepartmentsLeadershipHref));
        Assert.Contains("German Department", departments);
        Assert.Contains("PreK to Grade 3", departments);
        Assert.Contains("Grades 4–10", departments);
        Assert.Contains("Ms. Fatma Salah", departments);

        Assert.Contains("Positions & Assignments", Body(await client.GetStringAsync(OrgLabels.PositionsHref + "?tab=positions&status=vacant")));
    }

    [Fact]
    public async Task School_Leadership_and_Operation_Team_show_their_own_tiers()
    {
        await SeededAsync();
        var client = fx.Factory.CreateClient();
        var leadership = Body(await client.GetStringAsync(ManagementTeams.LeadershipHref));
        var ops = Body(await client.GetStringAsync(ManagementTeams.OperationHref));
        string[] Titles(string html) => Regex.Matches(html, "<h2 class=\"org-tier__title\" id=\"tier-title-\\d+\">([^<]+)</h2>").Select(m => m.Groups[1].Value).ToArray();
        var lt = Titles(leadership);
        var ot = Titles(ops);
        foreach (var t in new[] { "Executive Advisory & Governance", "Academic Leadership", "Parents Care, PR & Counseling", "Academic Department Leadership" }) Assert.Contains(t, lt);
        foreach (var t in new[] { "Floor Supervisors", "Administrative & Operational Management" }) { Assert.Contains(t, ot); Assert.DoesNotContain(t, lt); }
        Assert.DoesNotContain("Executive Advisory & Governance", ot);

        // Each person appears in one section's member list only; an Operation Team person is not a leadership member card.
        var donia = await StaffIdAsync("Ms. Donia Mostafa", "01145309275");
        Assert.DoesNotContain($"id=\"lm-{donia}\"", leadership);
        Assert.Equal(HttpStatusCode.Redirect, (await fx.ClientAs(fx.Factory, donia, follow: false).GetAsync("/management/school-leadership")).StatusCode);
        // The Management landing page keeps the two sections and links the structure.
        var hub = Body(await client.GetStringAsync("/management"));
        Assert.Contains(ManagementTeams.LeadershipHref, hub);
        Assert.Contains(ManagementTeams.OperationHref, hub);
        Assert.Contains(OrgLabels.OrgChartHref, hub);
    }

    [Fact]
    public async Task Department_page_shows_its_official_leadership()
    {
        await SeededAsync();
        await using var db = fx.NewContext();
        var math = await db.Departments.FirstAsync(d => d.NormalizedName == "MATH");
        var html = Body(await fx.Factory.CreateClient().GetStringAsync($"/staff/departments/{math.Id}"));
        Assert.Contains("Official leadership", html);
        Assert.Contains("Mr. Mamdouh Awaad", html);
        Assert.Contains("PreK to Grade 9", html);
    }

    [Fact]
    public void Person_key_ignores_honorifics_and_punctuation()
    {
        Assert.Equal("RANIA MOH HIJAZI", OrgStructureSeeder.PersonKey("Dr. Rania Moh. Hijazi"));
        Assert.Equal(OrgStructureSeeder.PersonKey("Saritha"), OrgStructureSeeder.PersonKey("Ms. Saritha"));
        Assert.NotEqual(OrgStructureSeeder.PersonKey("Mr. Mohamed Abdul-Rasoul"), OrgStructureSeeder.PersonKey("Mr. Mohamed Abdelrasol"));
        Assert.Equal("NO", Ui.Initials("Dr. Noha Osama"));
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private sealed class DenyAccess : IOrgStructureAccess
    {
        public Task<bool> CanManageAsync(CancellationToken ct = default) => Task.FromResult(false);
    }
}
