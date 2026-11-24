using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.OrgStructure;

namespace SchoolManagement.Web.Services;

/// <summary>The directory's staff category (derived, never stored).</summary>
public enum StaffCategory
{
    /// <summary>Teaching staff: a teaching job title ("… Teacher"), or no job title yet.</summary>
    Academic = 1,

    /// <summary>A Management Team record.</summary>
    Management = 2,

    /// <summary>Everyone else with a non-teaching job title (librarian, counsellor, student affairs, learning support…).</summary>
    Support = 3,
}

public static class StaffCategories
{
    public static readonly StaffCategory[] All = [StaffCategory.Academic, StaffCategory.Management, StaffCategory.Support];

    public static string Name(StaffCategory c) => c switch
    {
        StaffCategory.Academic => "Academic staff",
        StaffCategory.Management => "Management Team",
        StaffCategory.Support => "Administrative & support",
        _ => c.ToString(),
    };

    public static string Slug(StaffCategory c) => c switch
    {
        StaffCategory.Academic => "academic",
        StaffCategory.Management => "management",
        _ => "support",
    };

    public static StaffCategory? Parse(string? slug) => All.Cast<StaffCategory?>().FirstOrDefault(c => Slug(c!.Value) == slug);

    /// <summary>
    /// By job title: a teaching title (or none yet) is academic, any other title is administrative / support, even when the
    /// person also has some lessons or workload hours. Management Team records are always Management.
    /// </summary>
    public static StaffCategory Of(bool isManagement, string? jobTitle, bool academicLeader = false) =>
        isManagement ? StaffCategory.Management
        // An academic department's HOD, coordinator or team leader (Positions & Assignments) is academic staff.
        : academicLeader || string.IsNullOrWhiteSpace(jobTitle) || jobTitle.Contains("teacher", StringComparison.OrdinalIgnoreCase) ? StaffCategory.Academic
        : StaffCategory.Support;
}

/// <summary>A role a staff member holds: a leadership position, Head of Department, or KG class teaching team.</summary>
public sealed record StaffRoleBadge(string Key, string Label, string Icon, string Tone);

public sealed record StaffDirectoryEntry(int Id, string FullName, string? JobTitle, int? DepartmentId, string? Department, string? ContactNumber, string? Email,
    StaffCategory Category, ManagementPosition? Position, IReadOnlyList<StaffRoleBadge> Roles, bool Teaches)
{
    public bool IsManagement => Category == StaffCategory.Management;

    /// <summary>Current organizational positions (Positions &amp; Assignments), primary first.</summary>
    public IReadOnlyList<StaffOrgPosition> Org { get; init; } = [];

    /// <summary>The source's job title could not be read: shown as "Position requires confirmation".</summary>
    public bool PendingConfirmation { get; init; }

    public StaffOrgPosition? PrimaryOrg => Org.FirstOrDefault(p => p.IsPrimary) ?? Org.FirstOrDefault();
    public IEnumerable<StaffOrgPosition> AdditionalOrg => Org.Where(p => p != PrimaryOrg);

    /// <summary>The person's existing profile: the Management Team profile (positions, tasks) or the staff profile.</summary>
    public string ProfileHref => IsManagement ? $"/management/members/{Id}" : $"/staff/{Id}";
    public string EditHref => IsManagement ? $"/management/members/{Id}/edit" : $"/staff/edit/{Id}";
}

public sealed class StaffDirectoryQuery
{
    public string? Search { get; set; }

    /// <summary>A department id, or "none" (staff without a department).</summary>
    public string? Department { get; set; }

    /// <summary>academic, management or support.</summary>
    public string? Category { get; set; }

    /// <summary>A job title, or "none" (not set).</summary>
    public string? JobTitle { get; set; }

    /// <summary>A <see cref="ManagementPosition"/> name, "hod", "kg-homeroom", "kg-co", or "tier-{id}" (holds a position in that organizational tier).</summary>
    public string? Role { get; set; }

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Department) || !string.IsNullOrWhiteSpace(Category)
        || !string.IsNullOrWhiteSpace(JobTitle) || !string.IsNullOrWhiteSpace(Role);
}

public sealed record StaffDirectoryCounts(int Total, int Academic, int Management, int Support, int WithoutDepartment, int Heads);

/// <param name="Matching">Staff per category matching every filter except the category (the category bar's counts).</param>
public sealed record StaffDirectory(IReadOnlyList<StaffDirectoryEntry> Staff, IReadOnlyList<StaffDirectoryEntry> All, StaffDirectoryCounts Counts,
    IReadOnlyList<(int Id, string Name)> Departments, IReadOnlyList<string> JobTitles, string? AcademicYear,
    IReadOnlyDictionary<StaffCategory, int> Matching)
{
    /// <summary>The organizational tiers, for the Position / role filter ("tier-{id}").</summary>
    public IReadOnlyList<(int Id, string Name)> Tiers { get; init; } = [];
}

public interface IStaffDirectoryService
{
    Task<StaffDirectory> LoadAsync(StaffDirectoryQuery query, CancellationToken ct = default);
}

/// <summary>
/// Staff → All School Staff: one row per person, grouped by staff category. Every person is one <see cref="StaffMember"/> row (Management Team
/// members are the "Management" kind of the same table), so nobody appears twice; their roles come from the existing
/// relationships (current HOD appointments, Management positions, the current year's KG class teaching teams). Read-only.
/// </summary>
public sealed class StaffDirectoryService(SchoolDbContext db, IClassTeamService classTeams, IOrgStructureService org) : IStaffDirectoryService
{
    public async Task<StaffDirectory> LoadAsync(StaffDirectoryQuery q, CancellationToken ct = default)
    {
        // Current staff only: an archived record (not in the official staff sources) keeps its history but is not listed.
        var people = await db.StaffMembers.AsNoTracking().Where(s => s.ArchivedAt == null)
            .Select(s => new
            {
                s.Id, s.FullName, s.NormalizedName, s.JobTitle, s.DepartmentId, Department = s.Department != null ? s.Department.Name : null, s.ContactNumber,
                IsManagement = s is ManagementMember,
                Position = s is ManagementMember ? ((ManagementMember)s).Position : null,
                Email = s is ManagementMember ? ((ManagementMember)s).Email : null,
            })
            .ToListAsync(ct);

        // Roles from the existing relationships (one person can hold several).
        var heads = (await db.DepartmentHeads.AsNoTracking().Where(h => h.IsCurrent).Select(h => new { h.TeacherId, h.Department.Name }).ToListAsync(ct))
            .ToLookup(h => h.TeacherId, h => h.Name);
        var teaching = (await db.WorkloadAssignments.AsNoTracking().Select(w => w.TeacherId).Distinct().ToListAsync(ct))
            .Concat(await db.TimetableAssignments.AsNoTracking().Select(t => t.TeacherId).Distinct().ToListAsync(ct))
            .ToHashSet();
        // Organizational positions of every person, read from their own assignments (database → entry → card).
        var positions = await org.CurrentByStaffAsync(null, ct);
        var pending = (await org.OpenFlagsAsync(null, ct)).Where(f => f.Kind == DataQualityFlagKind.PositionRequiresConfirmation).Select(f => f.StaffId).ToHashSet();
        var year = await classTeams.CurrentYearAsync(ct);
        // Teaching Staff 2027 subject rows (subject and grade levels as the source writes them).
        var subjects = (await db.TeachingRosterEntries.AsNoTracking().Where(e => e.StaffMemberId != null && e.Role == RosterRole.SubjectTeacher)
                .OrderBy(e => e.RowNumber).Select(e => new { StaffId = e.StaffMemberId!.Value, e.Subject, e.GradeLevels }).ToListAsync(ct))
            .ToLookup(e => e.StaffId);
        var kg = year is null ? Enumerable.Empty<(int StaffId, ClassTeachingRole Role, string Class)>().ToLookup(k => k.StaffId)
            : (await db.ClassTeachingAssignments.AsNoTracking().Where(a => a.IsActive && a.AcademicYearId == year.Id)
                    .Select(a => new { a.StaffMemberId, a.Role, Class = a.SchoolClass.Name }).ToListAsync(ct))
                .Select(a => (StaffId: a.StaffMemberId, a.Role, a.Class)).ToLookup(k => k.StaffId);

        var all = people.Select(p =>
        {
            var roles = new List<StaffRoleBadge>();
            if (p.Position is { } pos) roles.Add(new(pos.ToString(), ManagementPositions.Name(pos), ManagementPositions.Icon(pos), "management"));
            foreach (var dept in heads[p.Id]) roles.Add(new("hod", $"Head of {dept}", "shield-check", "warning"));
            foreach (var k in kg[p.Id].OrderBy(k => k.Role).ThenBy(k => k.Class))
                roles.Add(new(k.Role == ClassTeachingRole.HomeroomTeacher ? "kg-homeroom" : "kg-co",
                    $"{(k.Role == ClassTeachingRole.HomeroomTeacher ? "Homeroom" : "Co-Teacher")} · {k.Class}", "graduation-cap", "secondary"));
            foreach (var sub in subjects[p.Id])
                roles.Add(new("subject", $"{sub.Subject} · {sub.GradeLevels}", "list", "secondary"));
            var teaches = teaching.Contains(p.Id);
            // Lessons or workload hours are shown by the Timetable / Workload actions; they do not change the category.
            var held = positions.TryGetValue(p.Id, out var list) ? list : [];
            var category = StaffCategories.Of(p.IsManagement, p.JobTitle, held.Any(h => h.IsAcademicDepartment));
            var entry = new StaffDirectoryEntry(p.Id, p.FullName, p.JobTitle, p.DepartmentId, p.Department, p.ContactNumber, p.Email, category, p.Position, roles, teaches)
            {
                Org = held,
                PendingConfirmation = pending.Contains(p.Id),
            };
            // Search matches the name or any current position title.
            return (Entry: entry, Key: p.NormalizedName + "|" + string.Join("|", held.Select(h => TextNormalizer.NameKey(h.DisplayTitle))));
        })
        .OrderBy(x => x.Entry.Category == StaffCategory.Management ? 0 : 1).ThenBy(x => x.Entry.Position ?? (ManagementPosition)99)
        .ThenBy(x => x.Entry.FullName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

        IEnumerable<(StaffDirectoryEntry Entry, string Key)> list = all;
        var term = TextNormalizer.NameKey(q.Search);
        if (term.Length > 0) list = list.Where(x => x.Key.Contains(term, StringComparison.Ordinal));
        if (q.Department == "none") list = list.Where(x => x.Entry.DepartmentId is null);
        else if (int.TryParse(q.Department, out var deptId)) list = list.Where(x => x.Entry.DepartmentId == deptId);
        if (q.JobTitle == "none") list = list.Where(x => string.IsNullOrWhiteSpace(x.Entry.JobTitle));
        else if (!string.IsNullOrWhiteSpace(q.JobTitle)) list = list.Where(x => string.Equals(x.Entry.JobTitle, q.JobTitle, StringComparison.OrdinalIgnoreCase));
        if (q.Role is { } role && role.StartsWith("tier-", StringComparison.Ordinal) && int.TryParse(role[5..], out var tierId))
            list = list.Where(x => x.Entry.Org.Any(o => o.TierId == tierId));
        else if (!string.IsNullOrWhiteSpace(q.Role)) list = list.Where(x => x.Entry.Roles.Any(r => r.Key == q.Role));
        // The category bar counts what the other filters leave; the category itself is applied last.
        var beforeCategory = list.ToList();
        var matching = StaffCategories.All.ToDictionary(c => c, c => beforeCategory.Count(x => x.Entry.Category == c));
        list = StaffCategories.Parse(q.Category) is { } cat ? beforeCategory.Where(x => x.Entry.Category == cat) : beforeCategory;

        var entries = all.Select(x => x.Entry).ToList();
        var counts = new StaffDirectoryCounts(
            entries.Count,
            entries.Count(e => e.Category == StaffCategory.Academic),
            entries.Count(e => e.Category == StaffCategory.Management),
            entries.Count(e => e.Category == StaffCategory.Support),
            entries.Count(e => e.DepartmentId is null && !e.IsManagement),
            heads.Count);
        var departments = await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var titles = entries.Where(e => !string.IsNullOrWhiteSpace(e.JobTitle)).Select(e => e.JobTitle!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
        var tiers = await db.OrgTiers.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.SortOrder).Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        return new StaffDirectory(list.Select(x => x.Entry).ToList(), entries, counts, departments.Select(d => (d.Id, d.Name)).ToList(), titles, year?.Name, matching)
        {
            Tiers = tiers.Select(t => (t.Id, t.Name)).ToList(),
        };
    }
}
