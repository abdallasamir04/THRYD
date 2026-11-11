using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Automatic class numbering per stage + section, student transfers, and the workload overview by department.</summary>
[Collection("db")]
public class ClassNumberingTests(DbFixture fx)
{
    [Fact]
    public void Each_stage_and_section_has_its_own_sequence()
    {
        string[] names = ["PreK", "KG1A", "KG2A", "KG1B", "KG2B", "G1A", "G2A", "G10A", "G1B"];
        Assert.Equal(3, ClassNumbering.NextNumber(names, "KG", "A"));   // KG1A, KG2A → KG3A
        Assert.Equal(3, ClassNumbering.NextNumber(names, "KG", "B"));   // KG1B, KG2B → KG3B
        Assert.Equal(1, ClassNumbering.NextNumber(names, "KG", "C"));   // new section → KG1C
        Assert.Equal(2, ClassNumbering.NextNumber([.. names, "KG1C"], "KG", "C"));
        Assert.Equal(11, ClassNumbering.NextNumber(names, "G", "A"));   // G counts separately from KG
        Assert.Equal(2, ClassNumbering.NextNumber(names, "G", "B"));
        Assert.Null(ClassNumbering.Parse("PreK"));
        Assert.Equal(("KG", 12, "B"), ClassNumbering.Parse("kg 12-b"));
        Assert.Null(ClassNumbering.NormalizeSection("A1"));
        Assert.Null(ClassNumbering.NormalizeStage("PreK"));
    }

    [Fact]
    public async Task Creating_classes_follows_the_examples_and_keeps_existing_classes()
    {
        await using var db = fx.NewContext();
        var classes = DbFixture.Classes(db);
        var before = await db.Classes.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync();

        async Task<string> AddAsync(string stage, string section, int capacity = 25)
        {
            var r = await classes.CreateInSectionAsync(stage, section, capacity, true);
            Assert.True(r.Succeeded, string.Join(" ", r.Errors.Select(e => e.Value)));
            return (await classes.GetAsync(r.EntityId!.Value))!.Name;
        }

        // The seeded classes include KG1A, KG2A, KG1B, KG2B and G1A…G10B.
        Assert.Equal("KG3A", await AddAsync("KG", "A", 18));
        Assert.Equal("KG3B", await AddAsync("kg", "b"));
        var firstC = await AddAsync("KG", "C");
        Assert.Equal("KG1C", firstC);
        Assert.Equal("KG2C", await AddAsync("KG", "C"));
        Assert.Equal("G11A", await AddAsync("G", "A"));

        Assert.Equal(18, (await db.Classes.AsNoTracking().SingleAsync(c => c.Name == "KG3A")).Capacity);
        // KG3A sorts after KG2B and before G1A.
        var order = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).Select(c => c.Name).ToListAsync();
        Assert.True(order.IndexOf("KG2B") < order.IndexOf("KG3A") && order.IndexOf("KG3A") < order.IndexOf("G1A"), string.Join(",", order));
        Assert.Equal(["PreK", "KG1A", "KG1B", "KG1C", "KG2A", "KG2B", "KG2C", "KG3A", "KG3B", "G1A"], order.Take(10));
        Assert.Equal(order.IndexOf("G10B") + 1, order.IndexOf("G11A"));

        // Server-side validation.
        Assert.False((await classes.CreateInSectionAsync("PreK", "A", 25, true)).Succeeded);
        Assert.False((await classes.CreateInSectionAsync("KG", "A1", 25, true)).Succeeded);
        Assert.False((await classes.CreateInSectionAsync("KG", "D", 0, true)).Succeeded);

        // Existing classes are untouched.
        var after = await db.Classes.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync();
        Assert.All(before, b => Assert.Contains(after, a => a.Id == b.Id && a.Name == b.Name));
    }

    [Fact]
    public async Task Transfers_respect_capacity_and_are_recorded_in_history()
    {
        await using var db = fx.NewContext();
        var classes = DbFixture.Classes(db);
        var from = await fx.NewClassAsync();
        var small = (await classes.CreateAsync("S" + DbFixture.Unique()[..8], null, 1, true)).EntityId!.Value;
        var (student, _) = await fx.NewStudentAsync(from);
        var (other, _) = await fx.NewStudentAsync(from);
        var students = DbFixture.Students(db);

        Assert.False((await students.TransferClassAsync(student, from, "Test runner")).Succeeded);   // same class
        Assert.True((await students.TransferClassAsync(student, small, "Test runner")).Succeeded);
        var full = await students.TransferClassAsync(other, small, "Test runner");                   // capacity 1
        Assert.False(full.Succeeded);
        Assert.Contains("full", full.Errors[0].Value);

        var history = await students.GetTransferHistoryAsync(student);
        var row = Assert.Single(history);
        Assert.Equal((from, small, "Test runner"), (row.FromClassId, row.ToClassId, row.TransferredBy));
        Assert.Empty(await students.GetTransferHistoryAsync(other));
    }

    [Fact]
    public async Task Workload_overview_filters_by_department_and_uses_the_configured_maximum()
    {
        await using var db = fx.NewContext();
        var department = await db.Departments.AsNoTracking().OrderBy(d => d.Id).FirstAsync();
        var name = "Teacher " + DbFixture.Unique();
        var teacherId = (await DbFixture.Teachers(db).CreateAsync(name, "Teacher", null, department.Id, null)).EntityId!.Value;
        var week = SchoolWeek.Current;
        var workload = DbFixture.Workload(db);

        var rows = await workload.OverviewAsync(week, new WorkloadOverviewQuery { DepartmentId = department.Id });
        var mine = Assert.Single(rows, r => r.TeacherId == teacherId);
        Assert.Equal(department.Name, mine.DepartmentName);
        Assert.All(rows, r => Assert.StartsWith(department.Name, r.DepartmentName));
        Assert.DoesNotContain(await workload.OverviewAsync(week, new WorkloadOverviewQuery { DepartmentId = department.Id + 1 }), r => r.TeacherId == teacherId);

        Assert.True((await DbFixture.Settings(db).SaveMaxWeeklyPeriodsAsync(20)).Succeeded);
        Assert.False((await DbFixture.Settings(db).SaveMaxWeeklyPeriodsAsync(30)).Succeeded); // 24 is the ceiling
        try
        {
            Assert.True((await workload.CreateAsync(new(teacherId, week, WorkloadCategory.Office, 20, null, null, "Office A"))).Succeeded);
            Assert.False((await workload.CreateAsync(new(teacherId, week, WorkloadCategory.Office, 1, null, null, "Office B"))).Succeeded);
            var summary = (await workload.SummaryAsync(teacherId, week))!;
            Assert.Equal((20, 20, 0, WorkloadStatus.Full), (summary.Total, summary.Max, summary.Remaining, summary.Status));
        }
        finally
        {
            Assert.True((await DbFixture.Settings(db).SaveMaxWeeklyPeriodsAsync(24)).Succeeded);
        }
    }
}
