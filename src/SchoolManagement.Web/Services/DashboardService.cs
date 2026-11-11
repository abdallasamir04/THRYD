using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record ClassDistribution(int ClassId, string Name, bool IsActive, int Count);

public sealed record RecentStudent(int Id, string FullName, string ClassName, DateTime CreatedAt);

public sealed record RecentTeacher(int Id, string FullName, string? JobTitle, DateTime CreatedAt);

public sealed class DashboardData
{
    public int TotalStudents { get; init; }
    public int TotalTeachers { get; init; }
    public int TotalClasses { get; init; }
    public int ActiveClasses { get; init; }
    public int StudentsInInactiveClasses { get; init; }
    public int ClassesWithoutStudents { get; init; }
    public int TeachersWithoutContact { get; init; }
    public IReadOnlyList<RecentStudent> RecentStudents { get; init; } = [];
    public IReadOnlyList<RecentTeacher> RecentTeachers { get; init; } = [];
    public IReadOnlyList<ClassDistribution> Distribution { get; init; } = [];
    public IReadOnlyList<JobTitleCount> TeachersByJobTitle { get; init; } = [];
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
            await db.ManagementTasks.CountAsync(t => t.Status != ManagementTaskStatus.Completed, ct));

    public async Task<DashboardData> GetAsync(CancellationToken ct = default)
    {
        var distribution = await db.Classes.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new ClassDistribution(c.Id, c.Name, c.IsActive, c.Students.Count))
            .ToListAsync(ct);

        var recentStudents = await db.Students.AsNoTracking()
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).Take(6)
            .Select(s => new RecentStudent(s.Id, s.FullName, s.SchoolClass.Name, s.CreatedAt))
            .ToListAsync(ct);

        var recentTeachers = await db.Teachers.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(6)
            .Select(t => new RecentTeacher(t.Id, t.FullName, t.JobTitle, t.CreatedAt))
            .ToListAsync(ct);

        var byTitle = await db.Teachers.AsNoTracking()
            .GroupBy(t => t.JobTitle ?? "")
            .Select(g => new JobTitleCount(g.Key, g.Count()))
            .ToListAsync(ct);

        return new DashboardData
        {
            TotalStudents = distribution.Sum(d => d.Count),
            TotalTeachers = await db.Teachers.CountAsync(ct),
            TotalClasses = distribution.Count,
            ActiveClasses = distribution.Count(d => d.IsActive),
            StudentsInInactiveClasses = distribution.Where(d => !d.IsActive).Sum(d => d.Count),
            ClassesWithoutStudents = distribution.Count(d => d.IsActive && d.Count == 0),
            TeachersWithoutContact = await db.Teachers.CountAsync(t => t.NormalizedContact == "", ct),
            RecentStudents = recentStudents,
            RecentTeachers = recentTeachers,
            Distribution = distribution,
            TeachersByJobTitle = byTitle.OrderByDescending(x => x.Count).ThenBy(x => x.JobTitle).ToList(),
            GeneratedAtUtc = DateTime.UtcNow,
        };
    }
}
