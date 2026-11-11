using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Workload rules against SQL Server: the 24-period limit, edits, removals and concurrency.</summary>
[Collection("db")]
public class WorkloadTests(DbFixture fx)
{
    private static readonly DateOnly Week = new(2030, 1, 6); // a Sunday

    private static WorkloadInput Input(int teacherId, WorkloadCategory category, int periods, int? classId = null, DateOnly? week = null) =>
        new(teacherId, week ?? Week, category, periods, classId, null, null);

    private async Task<ServiceResult> AddAsync(int teacherId, WorkloadCategory category, int periods, int? classId = null)
    {
        await using var db = fx.NewContext();
        return await DbFixture.Workload(db).CreateAsync(Input(teacherId, category, periods, classId));
    }

    private async Task<WorkloadSummary> SummaryAsync(int teacherId)
    {
        await using var db = fx.NewContext();
        return (await DbFixture.Workload(db).SummaryAsync(teacherId, Week))!;
    }

    [Fact]
    public async Task Totals_are_the_sum_of_the_four_categories()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.True((await AddAsync(id, WorkloadCategory.Class, 10, classId: 6)).Succeeded);
        Assert.True((await AddAsync(id, WorkloadCategory.Class, 2, classId: 7)).Succeeded);
        Assert.True((await AddAsync(id, WorkloadCategory.Substitution, 4)).Succeeded);
        Assert.True((await AddAsync(id, WorkloadCategory.Duty, 5)).Succeeded);
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 3)).Succeeded);

        var s = await SummaryAsync(id);
        Assert.Equal((12, 4, 5, 3), (s.Breakdown.Class, s.Breakdown.Substitution, s.Breakdown.Duty, s.Breakdown.Office));
        Assert.Equal(24, s.Total);
        Assert.Equal(0, s.Remaining);
        Assert.Equal(WorkloadStatus.Full, s.Status);
    }

    [Fact]
    public async Task Teacher_at_23_can_take_one_more_and_is_then_full()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 23)).Succeeded);
        Assert.Equal(WorkloadStatus.NearCapacity, (await SummaryAsync(id)).Status);

        var r = await AddAsync(id, WorkloadCategory.Substitution, 1);
        Assert.True(r.Succeeded);
        Assert.Equal(24, (await SummaryAsync(id)).Total);
        Assert.Equal(WorkloadStatus.Full, (await SummaryAsync(id)).Status);
    }

    [Fact]
    public async Task Teacher_at_24_cannot_take_another_period()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 24)).Succeeded);

        var r = await AddAsync(id, WorkloadCategory.Duty, 1);
        Assert.False(r.Succeeded);
        Assert.Contains("Cannot assign this teacher", r.Errors[0].Value);
        Assert.Contains("already 24/24 periods", r.Errors[0].Value);
        Assert.Equal(24, (await SummaryAsync(id)).Total);
    }

    [Fact]
    public async Task Assignment_larger_than_remaining_capacity_is_rejected()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 20)).Succeeded);
        var r = await AddAsync(id, WorkloadCategory.Substitution, 5);
        Assert.False(r.Succeeded);
        Assert.Contains("at most 4 periods", r.Errors[0].Value);
        Assert.Equal(20, (await SummaryAsync(id)).Total);
    }

    [Fact]
    public async Task Periods_outside_1_to_24_are_rejected()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.False((await AddAsync(id, WorkloadCategory.Office, 0)).Succeeded);
        Assert.False((await AddAsync(id, WorkloadCategory.Office, 25)).Succeeded);
        Assert.False((await AddAsync(id, WorkloadCategory.Office, -3)).Succeeded);
        Assert.Equal(0, (await SummaryAsync(id)).Total);
    }

    [Fact]
    public async Task Editing_does_not_double_count_the_old_value()
    {
        var (id, _) = await fx.NewTeacherAsync();
        var first = await AddAsync(id, WorkloadCategory.Class, 10, classId: 8);
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 10)).Succeeded);   // total 20

        await using var db = fx.NewContext();
        var service = DbFixture.Workload(db);
        // 10 → 14 makes 24 (would be 34 if the old 10 were counted twice).
        var ok = await service.UpdateAsync(first.EntityId!.Value, Input(id, WorkloadCategory.Class, 14, classId: 8));
        Assert.True(ok.Succeeded, string.Join(" ", ok.Errors.Select(e => e.Value)));
        Assert.Equal(24, (await SummaryAsync(id)).Total);

        // 14 → 15 would make 25.
        var tooMany = await service.UpdateAsync(first.EntityId!.Value, Input(id, WorkloadCategory.Class, 15, classId: 8));
        Assert.False(tooMany.Succeeded);
        Assert.Equal(24, (await SummaryAsync(id)).Total);

        // Changing the category moves the periods between categories without adding them.
        var moved = await service.UpdateAsync(first.EntityId!.Value, Input(id, WorkloadCategory.Substitution, 14));
        Assert.True(moved.Succeeded);
        var s = await SummaryAsync(id);
        Assert.Equal((0, 14, 0, 10, 24), (s.Breakdown.Class, s.Breakdown.Substitution, s.Breakdown.Duty, s.Breakdown.Office, s.Total));
    }

    [Fact]
    public async Task Removing_an_assignment_recalculates_and_frees_capacity()
    {
        var (id, _) = await fx.NewTeacherAsync();
        var office = await AddAsync(id, WorkloadCategory.Office, 20);
        Assert.True((await AddAsync(id, WorkloadCategory.Duty, 4)).Succeeded);
        Assert.Equal(24, (await SummaryAsync(id)).Total);

        await using (var db = fx.NewContext())
            Assert.True((await DbFixture.Workload(db).DeleteAsync(office.EntityId!.Value)).Succeeded);
        Assert.Equal(4, (await SummaryAsync(id)).Total);
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 20)).Succeeded);

        await using var check = fx.NewContext();
        var history = await DbFixture.Workload(check).HistoryAsync(id);
        Assert.Contains(history, h => h.Change == WorkloadChange.Removed && h.WeekTotalAfter == 4);
        Assert.Equal(4, history.Count); // created, created, removed, created
    }

    [Fact]
    public async Task Simultaneous_assignments_cannot_exceed_24()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 20)).Succeeded);

        // Eight requests of 4 periods each at the same moment, each with its own connection: only one fits.
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = fx.NewContext();
            return await DbFixture.Workload(db).CreateAsync(Input(id, WorkloadCategory.Substitution, 4));
        }).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(24, (await SummaryAsync(id)).Total);
    }

    [Fact]
    public async Task Database_trigger_rejects_direct_inserts_over_24()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 24)).Succeeded);

        await using var db = fx.NewContext();
        var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO TeacherWorkloadAssignments (TeacherId, WeekStart, Category, Periods, CreatedAt, UpdatedAt) VALUES ({id}, {Week}, 2, 1, SYSUTCDATETIME(), SYSUTCDATETIME())"));
        Assert.Equal(WorkloadService.LimitErrorNumber, ex.Number);
        Assert.Equal(24, (await SummaryAsync(id)).Total);
    }

    [Fact]
    public async Task Class_periods_need_a_class_and_are_not_duplicated_per_week()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.False((await AddAsync(id, WorkloadCategory.Class, 4)).Succeeded); // no class
        Assert.True((await AddAsync(id, WorkloadCategory.Class, 4, classId: 10)).Succeeded);
        var dup = await AddAsync(id, WorkloadCategory.Class, 2, classId: 10);
        Assert.False(dup.Succeeded);
        Assert.Contains("already has Class periods", dup.Errors[0].Value);
        Assert.Equal(4, (await SummaryAsync(id)).Total);
    }

    [Fact]
    public async Task Weeks_are_counted_separately()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 24)).Succeeded);
        await using var db = fx.NewContext();
        // Any day of the next week is normalised to that week's Sunday.
        var r = await DbFixture.Workload(db).CreateAsync(Input(id, WorkloadCategory.Office, 24, week: Week.AddDays(9)));
        Assert.True(r.Succeeded);
        var row = await db.WorkloadAssignments.AsNoTracking().FirstAsync(w => w.Id == r.EntityId);
        Assert.Equal(Week.AddDays(7), row.WeekStart);
    }

    [Fact]
    public async Task Teacher_with_workload_cannot_be_deleted()
    {
        var (id, _) = await fx.NewTeacherAsync();
        Assert.True((await AddAsync(id, WorkloadCategory.Office, 2)).Succeeded);
        await using var db = fx.NewContext();
        var r = await DbFixture.Teachers(db).DeleteAsync(id);
        Assert.False(r.Succeeded);
        Assert.NotNull(await DbFixture.Teachers(db).GetAsync(id));
    }
}
