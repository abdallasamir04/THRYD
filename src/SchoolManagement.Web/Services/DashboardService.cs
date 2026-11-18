using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>
/// One class on the dashboard: its enrolled students and its own configured capacity. The ring splits into Occupied
/// (students up to the capacity), Available (seats left) and Over (students beyond the capacity); never capped.
/// </summary>
public sealed record ClassDistribution(int ClassId, string Name, bool IsActive, int Count, int Capacity = SchoolClass.DefaultCapacity)
{
    public int Occupied => Math.Min(Count, Capacity);
    public int Available => Math.Max(0, Capacity - Count);
    public int Over => Math.Max(0, Count - Capacity);
    public bool IsFull => Capacity > 0 && Count == Capacity;
    /// <summary>Enrolled as a share of capacity (may exceed 100).</summary>
    public int Utilization => Capacity <= 0 ? 0 : (int)Math.Round(Count * 100.0 / Capacity, MidpointRounding.AwayFromZero);
}

/// <summary>Totals of the classes shown. Available and Over are summed per class, so one class's free seats never offset another's excess.</summary>
public sealed record CapacityTotals(int Students, int Capacity, int Available, int Over)
{
    public static CapacityTotals Of(IEnumerable<ClassDistribution> classes)
    {
        var list = classes.ToList();
        return new(list.Sum(c => c.Count), list.Sum(c => c.Capacity), list.Sum(c => c.Available), list.Sum(c => c.Over));
    }
}

public sealed class DashboardData
{
    public int TotalStudents { get; init; }
    public int TotalTeachers { get; init; }
    public int TotalClasses { get; init; }
    public int ActiveClasses { get; init; }
    public int StudentsInInactiveClasses { get; init; }
    public int ClassesWithoutStudents { get; init; }
    public int TeachersWithoutContact { get; init; }
    public IReadOnlyList<ClassDistribution> Distribution { get; init; } = [];
    public DateTime GeneratedAtUtc { get; init; }
}

public sealed record NavCounts(int Students, int Teachers, int Classes, int Departments = 0, int Management = 0, int OpenTasks = 0);

public interface IDashboardService
{
    Task<DashboardData> GetAsync(CancellationToken ct = default);
    Task<NavCounts> NavCountsAsync(CancellationToken ct = default);
}

/// <summary>Every figure is counted from the database on each request.</summary>
public sealed class DashboardService(SchoolDbContext db) : IDashboardService
{
    public async Task<NavCounts> NavCountsAsync(CancellationToken ct = default) =>
        new(await db.Students.CountAsync(ct), await db.Teachers.CountAsync(ct), await db.Classes.CountAsync(ct),
            await db.Departments.CountAsync(ct), await db.ManagementMembers.CountAsync(ct),
            await db.ManagementTasks.CountAsync(t => t.Status != ManagementTaskStatus.Completed && t.Status != ManagementTaskStatus.Cancelled, ct));

    public async Task<DashboardData> GetAsync(CancellationToken ct = default)
    {
        var distribution = await db.Classes.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new ClassDistribution(c.Id, c.Name, c.IsActive, c.Students.Count, c.Capacity))
            .ToListAsync(ct);

        return new DashboardData
        {
            TotalStudents = distribution.Sum(d => d.Count),
            TotalTeachers = await db.Teachers.CountAsync(ct),
            // All staff records (teachers and management members); staff have no inactive state.
            TotalClasses = distribution.Count,
            ActiveClasses = distribution.Count(d => d.IsActive),
            StudentsInInactiveClasses = distribution.Where(d => !d.IsActive).Sum(d => d.Count),
            ClassesWithoutStudents = distribution.Count(d => d.IsActive && d.Count == 0),
            TeachersWithoutContact = await db.Teachers.CountAsync(t => t.NormalizedContact == "", ct),
            Distribution = distribution,
            GeneratedAtUtc = DateTime.UtcNow,
        };
    }
}
