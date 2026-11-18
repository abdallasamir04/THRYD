using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record RosterStudent(int Id, string FullName);

/// <summary>One class with its current KG teaching team (KG Stage classes only) and every enrolled student.</summary>
public sealed record RosterClass(int Id, string Name, int Capacity, bool IsActive, string? Homeroom, string? CoTeacher, bool HasTeachingTeam,
    IReadOnlyList<RosterStudent> Students);

/// <summary>A group of a stage (Pre-K, KG1, Lower Elementary…) with its coordinator, when the school has one.</summary>
public sealed record RosterGroup(string Title, string Grades, string? CoordinatorRole, string? Coordinator, IReadOnlyList<RosterClass> Classes);

public sealed record RosterStage(string Title, string Description, IReadOnlyList<RosterGroup> Groups)
{
    public IEnumerable<RosterClass> Classes => Groups.SelectMany(g => g.Classes);
}

/// <summary>All classes by stage and grade (plus classes whose names match no stage), for the Classes &amp; Students report.</summary>
public sealed record ClassRoster(string? YearName, IReadOnlyList<RosterStage> Stages, IReadOnlyList<RosterClass> Other)
{
    public IEnumerable<RosterClass> AllClasses => Stages.SelectMany(s => s.Classes).Concat(Other);

    /// <summary>The same roster with only these classes (e.g. the classes matching a page's search and status).</summary>
    public ClassRoster Only(IReadOnlySet<int> classIds) => this with
    {
        Stages = Stages.Select(s => s with { Groups = s.Groups.Select(g => g with { Classes = g.Classes.Where(c => classIds.Contains(c.Id)).ToList() }).ToList() }).ToList(),
        Other = Other.Where(c => classIds.Contains(c.Id)).ToList(),
    };
}

public interface IClassRosterService
{
    Task<ClassRoster> LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// Read-only: every class (active and inactive) in the Classes pages' order, its enrolled students (each student is in
/// exactly one class), the current academic year's KG teaching team, and each stage's coordinator (Management Team).
/// </summary>
public sealed class ClassRosterService(SchoolDbContext db, IClassTeamService teams) : IClassRosterService
{
    /// <summary>The Management Team position that coordinates each class group.</summary>
    private static ManagementPosition? CoordinatorOf(string groupSlug) => groupSlug switch
    {
        "prek" or "kg1" or "kg2" => ManagementPosition.KgStageCoordinator,
        "lower" => ManagementPosition.LowerElementaryCoordinator,
        "upper" => ManagementPosition.UpperElementaryCoordinator,
        "middle" => ManagementPosition.MiddleSchoolCoordinator,
        "high" => ManagementPosition.HighSchoolCoordinator,
        _ => null,
    };

    public async Task<ClassRoster> LoadAsync(CancellationToken ct = default)
    {
        var classes = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Capacity, c.IsActive }).ToListAsync(ct);
        var students = (await db.Students.AsNoTracking().Select(s => new { s.Id, s.FullName, s.SchoolClassId }).ToListAsync(ct))
            .GroupBy(s => s.SchoolClassId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RosterStudent>)g.OrderBy(s => s.FullName, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id)
                .Select(s => new RosterStudent(s.Id, s.FullName)).ToList());

        var year = await teams.CurrentYearAsync(ct);
        var team = year is null ? []
            : await db.ClassTeachingAssignments.AsNoTracking().Where(a => a.IsActive && a.AcademicYearId == year.Id)
                .Select(a => new { a.SchoolClassId, a.Role, a.StaffMember.FullName }).ToListAsync(ct);
        var coordinators = await db.ManagementMembers.AsNoTracking().Where(m => m.Position != null)
            .Select(m => new { m.Position, m.FullName }).ToListAsync(ct);

        RosterClass Row(int id, string name, int capacity, bool active) => new(id, name, capacity, active,
            team.FirstOrDefault(a => a.SchoolClassId == id && a.Role == ClassTeachingRole.HomeroomTeacher)?.FullName,
            team.FirstOrDefault(a => a.SchoolClassId == id && a.Role == ClassTeachingRole.CoTeacher)?.FullName,
            ClassStages.IsKgStage(name),
            students.GetValueOrDefault(id) ?? []);

        var placed = new HashSet<int>();
        var stages = new List<RosterStage>();
        foreach (var stage in ClassStages.All)
        {
            var groups = new List<RosterGroup>();
            foreach (var g in stage.Groups)
            {
                var rows = classes.Where(c => ClassStages.GradeOf(c.Name) is { } grade && g.Contains(grade))
                    .Select(c => { placed.Add(c.Id); return Row(c.Id, c.Name, c.Capacity, c.IsActive); }).ToList();
                var position = CoordinatorOf(g.Slug);
                groups.Add(new RosterGroup(g.Title, g.Grades, position is { } p ? ManagementPositions.Name(p) : null,
                    position is { } q ? coordinators.FirstOrDefault(m => m.Position == q)?.FullName : null, rows));
            }
            stages.Add(new RosterStage(stage.Title, stage.Description, groups));
        }
        var other = classes.Where(c => !placed.Contains(c.Id)).Select(c => Row(c.Id, c.Name, c.Capacity, c.IsActive)).ToList();
        return new ClassRoster(year?.Name, stages, other);
    }
}
