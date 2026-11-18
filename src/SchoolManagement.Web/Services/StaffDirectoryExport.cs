using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>
/// One person in the All School Staff exports, with the same display rules as the page (Views/Staff/All.cshtml): the job
/// title or Management position, the department (Management Team members without one show "Management Team"), and every
/// other role (Head of Department, KG class teaching team). No database ids.
/// </summary>
/// <param name="Title">The job title or position; null when not set (a Management Team member without a position yet).</param>
/// <param name="Department">The department, or null when there is none.</param>
/// <param name="Roles">The roles other than <paramref name="Title"/> itself.</param>
/// <param name="Employment">"Full Time" / "Part Time", or null when not recorded.</param>
public sealed record StaffExportRow(string FullName, StaffCategory Category, string? Title, string? Department, string? ContactNumber, string? Email,
    IReadOnlyList<string> Roles, bool IsHead, string? Employment)
{
    public string CategoryName => StaffCategories.Name(Category);
}

/// <summary>
/// The dataset behind Staff → All School Staff → Download PDF / Download Excel: exactly the staff the page shows for the
/// same query (search, category tab, department, job title, position / role), each person once, grouped like the page.
/// </summary>
/// <param name="Filters">The active filters as "Label: value" pairs (empty when the whole directory is exported).</param>
/// <param name="AllStaff">The number of staff in the whole directory (before filters).</param>
public sealed record StaffDirectoryExport(IReadOnlyList<StaffExportRow> Rows, IReadOnlyList<(string Label, string Value)> Filters, int AllStaff,
    string? AcademicYear)
{
    /// <summary>The page's group order: Management Team, Academic Staff, Administrative &amp; Support Staff.</summary>
    public static readonly StaffCategory[] GroupOrder = [StaffCategory.Management, StaffCategory.Academic, StaffCategory.Support];

    public static string GroupName(StaffCategory c) => c switch
    {
        StaffCategory.Management => "Management Team",
        StaffCategory.Academic => "Academic Staff",
        _ => "Administrative & Support Staff",
    };

    public IReadOnlyList<StaffExportRow> In(StaffCategory c) => Rows.Where(r => r.Category == c).ToList();

    /// <summary>Every row, in group order (the order of the page's sections) and the page's order inside each group.</summary>
    public IReadOnlyList<StaffExportRow> Ordered => GroupOrder.SelectMany(In).ToList();

    public int Count(StaffCategory c) => Rows.Count(r => r.Category == c);
    public int Heads => Rows.Count(r => r.IsHead);
    public bool Filtered => Filters.Count > 0;

    /// <summary>THRYD_All_School_Staff_2026-11-18 (+ .pdf / .xlsx).</summary>
    public static string FileName(DateOnly day, string extension) =>
        $"THRYD_All_School_Staff_{day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.{extension}";

    /// <summary>The page's Position / role filter options (value → label).</summary>
    public static string? RoleName(string? value) => value switch
    {
        null or "" => null,
        "hod" => "Head of Department",
        "kg-homeroom" => "KG Homeroom Teacher",
        "kg-co" => "KG Co-Teacher",
        _ => ManagementPositions.All.Where(p => p.ToString() == value).Select(p => (string?)ManagementPositions.Name(p)).FirstOrDefault(),
    };

    /// <summary>Builds the export from the directory the page itself loads (<see cref="IStaffDirectoryService"/>).</summary>
    public static StaffDirectoryExport From(StaffDirectory d, StaffDirectoryQuery q, IReadOnlyDictionary<int, EmploymentType?> employment)
    {
        var rows = d.Staff.Select(s =>
        {
            // Same rules as the page: a Management Team member without a position shows none; the position or job title is
            // not repeated among the other roles.
            var noPosition = s.IsManagement && s.Position is null && (s.JobTitle is null || s.JobTitle == "Management Team");
            var title = noPosition || string.IsNullOrWhiteSpace(s.JobTitle) ? null : s.JobTitle.Trim();
            var roles = s.Roles.Where(r => !string.Equals(r.Label, title, StringComparison.OrdinalIgnoreCase)).Select(r => r.Label).ToList();
            var job = employment.TryGetValue(s.Id, out var e) ? e switch { EmploymentType.FullTime => "Full Time", EmploymentType.PartTime => "Part Time", _ => null } : null;
            return new StaffExportRow(s.FullName, s.Category, title, s.Department ?? (s.IsManagement ? "Management Team" : null),
                string.IsNullOrWhiteSpace(s.ContactNumber) ? null : s.ContactNumber.Trim(), string.IsNullOrWhiteSpace(s.Email) ? null : s.Email.Trim(),
                roles, s.Roles.Any(r => r.Key == "hod"), job);
        }).ToList();

        var filters = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(q.Search)) filters.Add(("Name", q.Search.Trim()));
        if (StaffCategories.Parse(q.Category) is { } cat) filters.Add(("Category", StaffCategories.Name(cat)));
        if (q.Department == "none") filters.Add(("Department", "No department"));
        else if (int.TryParse(q.Department, out var deptId) && d.Departments.FirstOrDefault(x => x.Id == deptId).Name is { } dn) filters.Add(("Department", dn));
        if (q.JobTitle == "none") filters.Add(("Job title", "Not set"));
        else if (!string.IsNullOrWhiteSpace(q.JobTitle)) filters.Add(("Job title", q.JobTitle.Trim()));
        if (RoleName(q.Role) is { } rn) filters.Add(("Position / role", rn));

        return new StaffDirectoryExport(rows, filters, d.Counts.Total, d.AcademicYear);
    }
}

public interface IStaffDirectoryExportService
{
    /// <summary>The staff the All School Staff page shows for <paramref name="query"/>, ready for the PDF and Excel downloads.</summary>
    Task<StaffDirectoryExport> LoadAsync(StaffDirectoryQuery query, CancellationToken ct = default);
}

/// <summary>Reuses the page's own <see cref="IStaffDirectoryService"/>; adds the employment type in one query. Read-only.</summary>
public sealed class StaffDirectoryExportService(IStaffDirectoryService directory, SchoolDbContext db) : IStaffDirectoryExportService
{
    public async Task<StaffDirectoryExport> LoadAsync(StaffDirectoryQuery query, CancellationToken ct = default)
    {
        var d = await directory.LoadAsync(query, ct);
        var employment = await db.StaffMembers.AsNoTracking().Select(s => new { s.Id, s.EmploymentType }).ToDictionaryAsync(s => s.Id, s => s.EmploymentType, ct);
        return StaffDirectoryExport.From(d, query, employment);
    }
}
