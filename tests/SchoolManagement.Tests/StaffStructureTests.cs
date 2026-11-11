using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Academic departments, HODs, department subcategories, the Management Team and its tasks.</summary>
[Collection("db")]
public class StaffStructureTests(DbFixture fx)
{
    private static DepartmentService Departments(SchoolDbContext db) => new(db, new TestUser());
    private static ManagementService Management(SchoolDbContext db) => new(db);
    private static ManagementTaskService Tasks(SchoolDbContext db) => new(db, new TestUser());

    private static async Task<int> DeptId(SchoolDbContext db, string name) =>
        await db.Departments.Where(d => d.Name == name).Select(d => d.Id).SingleAsync();

    private async Task<int> NewMemberOfAsync(string department, string? sub = null, string? subject = null)
    {
        await using var db = fx.NewContext();
        var r = await DbFixture.Teachers(db).CreateAsync("Teacher " + DbFixture.Unique(), "Teacher", null, await DeptId(db, department), sub, subject);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        return r.EntityId!.Value;
    }

    private async Task<int> NewManagerAsync(ManagementPosition? position = null)
    {
        await using var db = fx.NewContext();
        var r = await Management(db).CreateAsync(new ManagementMemberInput { FullName = "Manager " + DbFixture.Unique(), Position = position });
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        return r.EntityId!.Value;
    }

    // ------------------------------------------------------------------ departments

    [Fact]
    public async Task All_fifteen_academic_departments_exist_exactly_once()
    {
        string[] expected = ["Arabic", "English", "Math", "Science", "ASS", "ESS", "Religion", "PE", "Art", "ICT", "Music",
            "Library / Reading", "Second Language", "Drama", "WSD"];
        await using var db = fx.NewContext();
        var names = await db.Departments.Select(d => d.Name).ToListAsync();
        Assert.Equal(expected.Order(), names.Order());
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var cards = await Departments(db).ListAsync(new DepartmentListQuery());
        Assert.Equal(15, cards.Count);
        Assert.Equal("Wellbeing, Support & Development", cards.Single(c => c.Name == "WSD").Description);
    }

    [Fact]
    public async Task Religion_offers_Islamic_with_Religion_or_Quran_and_Christian_Religion()
    {
        var quran = await NewMemberOfAsync("Religion", "Islamic", "Quran");
        var religion = await NewMemberOfAsync("Religion", "islamic", "religion"); // stored in canonical spelling
        var christian = await NewMemberOfAsync("Religion", "Christian"); // the only subject is filled in

        await using var db = fx.NewContext();
        var rows = await db.Teachers.Where(t => t.Id == quran || t.Id == religion || t.Id == christian)
            .ToDictionaryAsync(t => t.Id, t => (t.Subcategory, t.Subject));
        Assert.Equal(("Islamic", "Quran"), rows[quran]);
        Assert.Equal(("Islamic", "Religion"), rows[religion]);
        Assert.Equal(("Christian", "Christian Religion"), rows[christian]);

        var teachers = DbFixture.Teachers(db);
        var rel = await DeptId(db, "Religion");
        Assert.Contains((await teachers.CreateAsync("T " + DbFixture.Unique(), null, null, rel, null, null)).Errors, e => e.Key == "Subcategory");
        Assert.Contains((await teachers.CreateAsync("T " + DbFixture.Unique(), null, null, rel, "French", null)).Errors, e => e.Key == "Subcategory");
        Assert.Contains((await teachers.CreateAsync("T " + DbFixture.Unique(), null, null, rel, "Islamic", null)).Errors, e => e.Key == "Subject");
        Assert.Contains((await teachers.CreateAsync("T " + DbFixture.Unique(), null, null, rel, "Islamic", "Christian Religion")).Errors, e => e.Key == "Subject");
    }

    [Fact]
    public async Task Second_Language_ICT_and_WSD_have_their_own_subcategories_and_others_have_none()
    {
        var french = await NewMemberOfAsync("Second Language", "French");
        var ai = await NewMemberOfAsync("ICT", "AI");
        var character = await NewMemberOfAsync("WSD", "Character Building");
        var math = await NewMemberOfAsync("Math", "French", "Quran"); // ignored for a department without subcategories

        await using var db = fx.NewContext();
        var rows = await db.Teachers.Where(t => new[] { french, ai, character, math }.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => (t.Subcategory, t.Subject));
        Assert.Equal(("French", (string?)null), rows[french]);
        Assert.Equal(("AI", (string?)null), rows[ai]);
        Assert.Equal(("Character Building", (string?)null), rows[character]);
        Assert.Equal(((string?)null, (string?)null), rows[math]);

        var teachers = DbFixture.Teachers(db);
        Assert.False((await teachers.CreateAsync("T " + DbFixture.Unique(), null, null, await DeptId(db, "Second Language"), "Spanish", null)).Succeeded);
        Assert.False((await teachers.CreateAsync("T " + DbFixture.Unique(), null, null, await DeptId(db, "WSD"), null, null)).Succeeded);
        Assert.Equal(["Wellbeing", "Support", "Discipline", "Character Building"], (await DepartmentCatalog.ForAsync(db, await DeptId(db, "WSD")))!.Options.Select(o => o.Value));
        Assert.Equal(["ICT", "AI"], (await DepartmentCatalog.ForAsync(db, await DeptId(db, "ICT")))!.Options.Select(o => o.Value));
    }

    // ------------------------------------------------------------------ HOD

    [Fact]
    public async Task Reassigning_the_HOD_replaces_the_previous_one_and_keeps_history()
    {
        var first = await NewMemberOfAsync("Drama");
        var second = await NewMemberOfAsync("Drama");
        await using var db = fx.NewContext();
        var drama = await DeptId(db, "Drama");
        var service = Departments(db);
        var today = SchoolWeek.Today;

        Assert.True((await service.AssignHeadAsync(drama, first, today, "Founding HOD")).Succeeded);
        Assert.Equal(first, (await service.GetAsync(drama))!.Head!.TeacherId);
        Assert.False((await service.AssignHeadAsync(drama, first, today, null)).Succeeded); // already the HOD

        Assert.True((await service.AssignHeadAsync(drama, second, today, null)).Succeeded);
        await using var check = fx.NewContext();
        var current = await check.DepartmentHeads.Where(h => h.DepartmentId == drama && h.IsCurrent).ToListAsync();
        Assert.Equal(second, Assert.Single(current).TeacherId);
        var ended = await check.DepartmentHeads.SingleAsync(h => h.DepartmentId == drama && h.TeacherId == first);
        Assert.False(ended.IsCurrent);
        Assert.NotNull(ended.EndedAt);
        Assert.Contains("Replaced by", ended.EndReason);
        Assert.True((await Departments(check).HeadHistoryAsync(drama)).Count >= 2);
    }

    [Fact]
    public async Task HOD_must_be_an_academic_member_of_the_department()
    {
        var outsider = await NewMemberOfAsync("Music");
        var manager = await NewManagerAsync();
        await using var db = fx.NewContext();
        var art = await DeptId(db, "Art");
        var service = Departments(db);

        var notMember = await service.AssignHeadAsync(art, outsider, SchoolWeek.Today, null);
        Assert.Contains(notMember.Errors, e => e.Key == "TeacherId" && e.Value.Contains("not a member"));
        var management = await service.AssignHeadAsync(art, manager, SchoolWeek.Today, null);
        Assert.Contains(management.Errors, e => e.Value.Contains("Management Team"));
        Assert.Contains((await service.AssignHeadAsync(art, null, SchoolWeek.Today, null)).Errors, e => e.Key == "TeacherId");
        Assert.Contains((await service.AssignHeadAsync(art, outsider, null, null)).Errors, e => e.Key == "StartDate");
    }

    [Fact]
    public async Task Leaving_the_department_ends_the_HOD_appointment_and_a_current_HOD_cannot_be_deleted()
    {
        var head = await NewMemberOfAsync("PE");
        await using var db = fx.NewContext();
        var pe = await DeptId(db, "PE");
        Assert.True((await Departments(db).AssignHeadAsync(pe, head, SchoolWeek.Today, null)).Succeeded);

        var delete = await DbFixture.Teachers(db).DeleteAsync(head);
        Assert.False(delete.Succeeded);
        Assert.Contains("Head of PE", delete.Errors[0].Value);

        var row = (await DbFixture.Teachers(db).GetAsync(head))!;
        Assert.True(row.IsHead);
        Assert.True((await DbFixture.Teachers(db).UpdateAsync(head, row.FullName, row.JobTitle, row.ContactNumber, await DeptId(db, "Art"), null, null)).Succeeded);

        await using var check = fx.NewContext();
        Assert.Null((await Departments(check).GetAsync(pe))!.Head);
        Assert.False((await DbFixture.Teachers(check).GetAsync(head))!.IsHead);
        Assert.True((await DbFixture.Teachers(check).DeleteAsync(head)).Succeeded); // history goes with the record
    }

    [Fact]
    public async Task Removing_a_member_from_a_department_keeps_the_staff_record()
    {
        var member = await NewMemberOfAsync("ICT", "ICT");
        await using var db = fx.NewContext();
        var ict = await DeptId(db, "ICT");
        Assert.True((await Departments(db).RemoveMemberAsync(ict, member)).Succeeded);
        var row = (await DbFixture.Teachers(db).GetAsync(member))!;
        Assert.Equal(((int?)null, (string?)null), (row.DepartmentId, row.Subcategory));
        Assert.False((await Departments(db).RemoveMemberAsync(ict, member)).Succeeded);
    }

    // ------------------------------------------------------------------ Management Team

    [Fact]
    public async Task Management_members_are_separate_from_academic_staff()
    {
        var id = await NewManagerAsync();
        await using var db = fx.NewContext();
        Assert.False(await db.Teachers.AnyAsync(t => t.Id == id));
        Assert.Null(await DbFixture.Teachers(db).GetAsync(id));
        Assert.Null((await db.ManagementMembers.SingleAsync(m => m.Id == id)).DepartmentId);
        Assert.DoesNotContain((await DbFixture.Teachers(db).ListAllAsync(new TeacherListQuery())), t => t.Id == id);

        // The database itself refuses a department for a Management Team record.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE StaffMembers SET DepartmentId = 1 WHERE Id = {id}"));
        Assert.Contains("CK_StaffMembers_ManagementHasNoDepartment", ex.Message);

        // A management record cannot be edited through the academic staff service either.
        Assert.False((await DbFixture.Teachers(db).UpdateAsync(id, "X " + DbFixture.Unique(), null, null, 1, null, null)).Succeeded);
    }

    [Fact]
    public async Task Each_of_the_seven_positions_has_at_most_one_holder()
    {
        Assert.Equal(7, ManagementPositions.All.Length);
        Assert.Equal(["School Director", "SQAM", "KG Stage Coordinator", "Lower Elementary Coordinator", "Upper Elementary Coordinator",
            "Middle School Coordinator", "High School Coordinator"], ManagementPositions.All.Select(ManagementPositions.Name));

        var director = await NewManagerAsync(ManagementPosition.SchoolDirector);
        await using var db = fx.NewContext();
        var service = Management(db);
        var second = await service.CreateAsync(new ManagementMemberInput { FullName = "Manager " + DbFixture.Unique(), Position = ManagementPosition.SchoolDirector });
        Assert.Contains(second.Errors, e => e.Key == "Position");

        var slots = await service.PositionsAsync();
        Assert.Equal(7, slots.Count);
        Assert.Equal(director, slots.Single(s => s.Position == ManagementPosition.SchoolDirector).Holder!.Id);
        Assert.Equal("School Director", (await db.ManagementMembers.SingleAsync(m => m.Id == director)).JobTitle);

        // Free the position for other tests.
        var row = (await service.GetAsync(director))!;
        Assert.True((await service.UpdateAsync(director, new ManagementMemberInput { FullName = row.FullName })).Succeeded);
        Assert.False((await service.CreateAsync(new ManagementMemberInput { FullName = "Bad", Email = "not-an-email" })).Succeeded);
    }

    // ------------------------------------------------------------------ tasks

    [Fact]
    public async Task Tasks_persist_for_the_right_member_with_counts_history_and_completion()
    {
        var member = await NewManagerAsync();
        var other = await NewManagerAsync();
        var teacher = await NewMemberOfAsync("Arabic");
        await using var db = fx.NewContext();
        var tasks = Tasks(db);
        var due = SchoolWeek.Today.AddDays(3);

        var created = await tasks.CreateAsync(new TaskInput { MemberId = member, Title = "  Plan   open day ", Priority = TaskPriority.High, DueDate = due, DueTime = new TimeOnly(9, 30), Description = "Agenda" });
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Value)));
        var id = created.EntityId!.Value;

        var saved = await db.ManagementTasks.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal(("Plan open day", member, due, new TimeOnly(9, 30), ManagementTaskStatus.ToDo), (saved.Title, saved.ManagementMemberId, saved.DueDate!.Value, saved.DueTime!.Value, saved.Status));

        var mine = await tasks.ListAsync(new TaskListQuery { MemberId = member });
        Assert.Equal(id, Assert.Single(mine.Items).Id);
        Assert.Empty((await tasks.ListAsync(new TaskListQuery { MemberId = other })).Items);
        Assert.Single((await tasks.ListAsync(new TaskListQuery { MemberId = member, Priority = "high", Due = "week", Status = "open" })).Items);
        Assert.Empty((await tasks.ListAsync(new TaskListQuery { MemberId = member, Priority = "low" })).Items);

        // Teachers never receive Management Team tasks; a time needs a date; a new task cannot be due in the past.
        Assert.Contains((await tasks.CreateAsync(new TaskInput { MemberId = teacher, Title = "X" })).Errors, e => e.Key == "MemberId" && e.Value.Contains("academic staff"));
        Assert.Contains((await tasks.CreateAsync(new TaskInput { MemberId = member, Title = "X", DueTime = new TimeOnly(8, 0) })).Errors, e => e.Key == "DueTime");
        Assert.Contains((await tasks.CreateAsync(new TaskInput { MemberId = member, Title = "X", DueDate = SchoolWeek.Today.AddDays(-1) })).Errors, e => e.Key == "DueDate");
        Assert.Contains((await tasks.CreateAsync(new TaskInput { MemberId = member, Title = " " })).Errors, e => e.Key == "Title");

        Assert.True((await tasks.SetStatusAsync(id, ManagementTaskStatus.Completed)).Succeeded);
        var done = await db.ManagementTasks.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.NotNull(done.CompletedAt);
        var counts = await tasks.CountsAsync(member);
        Assert.Equal((1, 1, 0), (counts.Total, counts.Completed, counts.Open));

        Assert.True((await tasks.SetStatusAsync(id, ManagementTaskStatus.InProgress)).Succeeded);
        Assert.Null((await db.ManagementTasks.AsNoTracking().SingleAsync(t => t.Id == id)).CompletedAt);

        // Reassigning moves the task and logs it on both members' histories.
        Assert.True((await tasks.UpdateAsync(id, new TaskInput { MemberId = other, Title = "Plan open day", Priority = TaskPriority.Urgent, DueDate = due, Status = ManagementTaskStatus.InProgress })).Succeeded);
        Assert.Single((await tasks.ListAsync(new TaskListQuery { MemberId = other })).Items);
        Assert.Contains(await tasks.HistoryAsync(member, 50), h => h.Action == TaskHistoryAction.Reassigned);
        Assert.Contains(await tasks.HistoryAsync(other, 50), h => h.Action == TaskHistoryAction.Reassigned);

        // A member with tasks cannot be removed; deleting the task keeps its history entry.
        Assert.False((await Management(db).DeleteAsync(other)).Succeeded);
        Assert.True((await tasks.DeleteAsync(id)).Succeeded);
        Assert.Contains(await tasks.HistoryAsync(other, 50), h => h.Action == TaskHistoryAction.Deleted && h.TaskId == id);
        Assert.True((await Management(db).DeleteAsync(other)).Succeeded);
    }

    [Fact]
    public async Task Overdue_tasks_are_counted_and_filtered()
    {
        var member = await NewManagerAsync();
        await using var db = fx.NewContext();
        // An old task, inserted directly (the form does not allow new past due dates).
        db.ManagementTasks.Add(new ManagementTask { ManagementMemberId = member, Title = "Late report", DueDate = SchoolWeek.Today.AddDays(-2), Status = ManagementTaskStatus.InProgress, Priority = TaskPriority.Medium });
        db.ManagementTasks.Add(new ManagementTask { ManagementMemberId = member, Title = "Old but done", DueDate = SchoolWeek.Today.AddDays(-2), Status = ManagementTaskStatus.Completed, Priority = TaskPriority.Low });
        await db.SaveChangesAsync();

        var tasks = Tasks(db);
        var counts = await tasks.CountsAsync(member);
        Assert.Equal((2, 1, 1), (counts.Total, counts.Overdue, counts.Completed));
        var overdue = await tasks.ListAsync(new TaskListQuery { MemberId = member, Status = "overdue" });
        var row = Assert.Single(overdue.Items);
        Assert.True(row.IsOverdue);
        Assert.Equal(1, (await Management(db).GetAsync(member))!.OverdueTasks);

        // Editing an overdue task without changing its date is allowed.
        Assert.True((await tasks.UpdateAsync(row.Id, new TaskInput { MemberId = member, Title = "Late report", DueDate = row.DueDate, Notes = "Chased", Status = ManagementTaskStatus.InProgress })).Succeeded);
    }

    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
}
