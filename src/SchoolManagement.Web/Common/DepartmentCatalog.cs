using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Common;

/// <summary>A subcategory and, where the department has a second level, its subjects.</summary>
public sealed record SubcategoryOption(string Value, IReadOnlyList<string> Subjects);

/// <summary>The subcategory rules of one department (label shown on the form + allowed values).</summary>
public sealed record DepartmentSubcategories(string Label, string? SubjectLabel, IReadOnlyList<SubcategoryOption> Options)
{
    public bool HasSubjects => Options.Any(o => o.Subjects.Count > 0);
    public SubcategoryOption? Find(string? value) => Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>What can be taught in this department: subjects where there are any, otherwise the subcategory.</summary>
    public IEnumerable<string> TeachingSubjects => Options.SelectMany(o => o.Subjects.Count > 0 ? o.Subjects : [o.Value]);
}

/// <summary>
/// Department-specific subcategories, read from the DepartmentSubcategories table (managed on each
/// department's page, so a new Second Language needs no code change). Only active rows are offered;
/// the server validates every staff save against the same rules the form shows.
/// </summary>
public static class DepartmentCatalog
{
    /// <summary>Active rules per department id; departments without active subcategories are absent.</summary>
    public static async Task<Dictionary<int, DepartmentSubcategories>> LoadAsync(SchoolDbContext db, int? departmentId = null, CancellationToken ct = default)
    {
        var depts = await db.Departments.AsNoTracking().Where(d => departmentId == null || d.Id == departmentId)
            .Select(d => new { d.Id, d.SubcategoryLabel, d.SubjectLabel }).ToListAsync(ct);
        var rows = await db.DepartmentSubcategories.AsNoTracking()
            .Where(s => s.IsActive && (departmentId == null || s.DepartmentId == departmentId))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.DepartmentId, s.ParentId, s.Name }).ToListAsync(ct);

        var result = new Dictionary<int, DepartmentSubcategories>();
        foreach (var d in depts)
        {
            var mine = rows.Where(r => r.DepartmentId == d.Id).ToList();
            var top = mine.Where(r => r.ParentId == null).ToList();
            if (top.Count == 0) continue;
            var options = top.Select(t => new SubcategoryOption(t.Name, mine.Where(c => c.ParentId == t.Id).Select(c => c.Name).ToList())).ToList();
            result[d.Id] = new DepartmentSubcategories(d.SubcategoryLabel ?? "Subcategory", d.SubjectLabel ?? "Subject", options);
        }
        return result;
    }

    public static async Task<DepartmentSubcategories?> ForAsync(SchoolDbContext db, int departmentId, CancellationToken ct = default) =>
        (await LoadAsync(db, departmentId, ct)).GetValueOrDefault(departmentId);

    /// <summary>
    /// Checks a subcategory / subject pair against a department's rules and returns the values to store
    /// (canonical spelling; null where the department has none). Errors use the form field names.
    /// </summary>
    public static (string? Subcategory, string? Subject, List<KeyValuePair<string, string>> Errors) Normalize(
        string? departmentName, DepartmentSubcategories? rules, string? subcategory, string? subject)
    {
        var errors = new List<KeyValuePair<string, string>>();
        if (rules is null) return (null, null, errors); // nothing to choose, nothing stored

        var option = rules.Find(subcategory?.Trim());
        if (option is null)
        {
            errors.Add(new("Subcategory", string.IsNullOrWhiteSpace(subcategory)
                ? $"Choose the {rules.Label.ToLowerInvariant()} for {departmentName}."
                : $"\"{subcategory}\" is not an active {rules.Label.ToLowerInvariant()} of {departmentName}."));
            return (null, null, errors);
        }

        if (option.Subjects.Count == 0) return (option.Value, null, errors);

        var chosen = option.Subjects.FirstOrDefault(s => string.Equals(s, subject?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null && option.Subjects.Count == 1 && string.IsNullOrWhiteSpace(subject)) chosen = option.Subjects[0];
        if (chosen is null)
            errors.Add(new("Subject", string.IsNullOrWhiteSpace(subject)
                ? $"Choose the {option.Value} subject."
                : $"\"{subject}\" is not a subject of {option.Value}."));
        return (option.Value, chosen, errors);
    }

    /// <summary>"Islamic · Quran"-style label after the department name.</summary>
    public static string Detail(string? subcategory, string? subject) =>
        string.Join(" · ", new[] { subcategory, subject }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>JSON for the staff form's dropdowns (data attribute on each department option).</summary>
    public static string? ClientJson(DepartmentSubcategories? rules) =>
        rules is null ? null : JsonSerializer.Serialize(new
        {
            label = rules.Label,
            subjectLabel = rules.SubjectLabel ?? "Subject",
            options = rules.Options.Select(o => new { value = o.Value, subjects = o.Subjects }),
        });
}
