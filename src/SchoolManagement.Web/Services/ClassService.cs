using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record ClassRow(int Id, string Name, int SortOrder, int Capacity, bool IsActive, int StudentCount, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record ClassOption(int Id, string Name, bool IsActive, int StudentCount = 0, int Capacity = 25);

public interface IClassService
{
    Task<IReadOnlyList<ClassRow>> ListAsync(string? search = null, string? status = null, CancellationToken ct = default);
    /// <summary>Active classes, plus <paramref name="includeId"/> when it is inactive (so an edit form can show it).</summary>
    Task<IReadOnlyList<ClassOption>> OptionsAsync(int? includeId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ClassOption>> AllOptionsAsync(CancellationToken ct = default);
    Task<ClassRow?> GetAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(string name, int? sortOrder, bool isActive, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(string name, int? sortOrder, int capacity, bool isActive, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, string name, int? sortOrder, bool isActive, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, string name, int? sortOrder, int capacity, bool isActive, CancellationToken ct = default);
    Task<ServiceResult> SetActiveAsync(int id, bool isActive, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, int? reassignToClassId, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, int? exceptId, CancellationToken ct = default);

    /// <summary>Existing sections (A, B, C…) used by classes of the given stage (KG or G).</summary>
    Task<IReadOnlyList<string>> SectionsAsync(string stage, CancellationToken ct = default);
    /// <summary>The name the next class in this stage + section will get, e.g. KG3A.</summary>
    Task<string?> NextNameAsync(string stage, string section, CancellationToken ct = default);
    /// <summary>Creates the next class in a stage + section; the number is calculated on the server.</summary>
    Task<ServiceResult> CreateInSectionAsync(string stage, string section, int capacity, bool isActive, CancellationToken ct = default);
}

/// <summary>
/// Automatic class numbering. A class name is Stage + Number + Section (KG3A, G11B). Each
/// Stage + Section pair has its own sequence, so KG Section A and KG Section B count separately,
/// and KG and G never share a counter. A new section starts at 1.
/// </summary>
public static class ClassNumbering
{
    public static readonly IReadOnlyList<(string Code, string Label)> Stages = [("KG", "Kindergarten (KG)"), ("G", "Grade (G)")];

    private static readonly System.Text.RegularExpressions.Regex Pattern =
        new(@"^(KG|G)(\d{1,3})([A-Z]{1,3})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Splits "KG2A" into ("KG", 2, "A"); null for names outside the pattern (e.g. PreK).</summary>
    public static (string Stage, int Number, string Section)? Parse(string? name)
    {
        var clean = TextNormalizer.CleanText(name).ToUpperInvariant().Replace(" ", "").Replace("-", "");
        var m = Pattern.Match(clean);
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value) : null;
    }

    public static string? NormalizeStage(string? stage)
    {
        var s = TextNormalizer.CleanText(stage).ToUpperInvariant();
        return Stages.Any(x => x.Code == s) ? s : null;
    }

    /// <summary>One to three letters A–Z; null when invalid.</summary>
    public static string? NormalizeSection(string? section)
    {
        var s = TextNormalizer.CleanText(section).ToUpperInvariant().Replace(" ", "");
        return s.Length is >= 1 and <= 3 && s.All(c => c is >= 'A' and <= 'Z') ? s : null;
    }

    /// <summary>Next number = highest existing number in this stage + section, plus one (1 for a new section).</summary>
    public static int NextNumber(IEnumerable<string> existingNames, string stage, string section) =>
        existingNames.Select(Parse)
            .Where(p => p is { } x && x.Stage == stage && x.Section == section)
            .Select(p => p!.Value.Number)
            .DefaultIfEmpty(0).Max() + 1;
}

public sealed class ClassService(SchoolDbContext db) : IClassService
{
    public async Task<IReadOnlyList<ClassRow>> ListAsync(string? search = null, string? status = null, CancellationToken ct = default)
    {
        var query = db.Classes.AsNoTracking();
        var term = TextNormalizer.ClassKey(search);
        if (term.Length > 0) query = query.Where(c => c.NormalizedName.Contains(term));
        if (status == "active") query = query.Where(c => c.IsActive);
        else if (status == "inactive") query = query.Where(c => !c.IsActive);

        return await query
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new ClassRow(c.Id, c.Name, c.SortOrder, c.Capacity, c.IsActive, c.Students.Count, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ClassOption>> OptionsAsync(int? includeId = null, CancellationToken ct = default) =>
        await db.Classes.AsNoTracking()
            .Where(c => c.IsActive || c.Id == includeId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new ClassOption(c.Id, c.Name, c.IsActive, c.Students.Count, c.Capacity))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ClassOption>> AllOptionsAsync(CancellationToken ct = default) =>
        await db.Classes.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new ClassOption(c.Id, c.Name, c.IsActive, c.Students.Count, c.Capacity))
            .ToListAsync(ct);

    public Task<ClassRow?> GetAsync(int id, CancellationToken ct = default) =>
        db.Classes.AsNoTracking().Where(c => c.Id == id)
            .Select(c => new ClassRow(c.Id, c.Name, c.SortOrder, c.Capacity, c.IsActive, c.Students.Count, c.CreatedAt, c.UpdatedAt))
            .FirstOrDefaultAsync(ct);

    public Task<bool> NameExistsAsync(string name, int? exceptId, CancellationToken ct = default)
    {
        var key = TextNormalizer.ClassKey(name);
        return db.Classes.AnyAsync(c => c.NormalizedName == key && c.Id != exceptId, ct);
    }

    public Task<ServiceResult> CreateAsync(string name, int? sortOrder, bool isActive, CancellationToken ct = default) =>
        CreateAsync(name, sortOrder, 25, isActive, ct);

    public async Task<ServiceResult> CreateAsync(string name, int? sortOrder, int capacity, bool isActive, CancellationToken ct = default)
    {
        var check = await ValidateAsync(name, null, ct);
        if (!check.Succeeded) return check;

        var order = sortOrder ?? await CalculateSortOrderAsync(name, ct);
        var entity = new SchoolClass
        {
            Name = TextNormalizer.CleanText(name),
            NormalizedName = TextNormalizer.ClassKey(name),
            SortOrder = order,
            Capacity = Math.Max(1, capacity),
            IsActive = isActive,
        };
        db.Classes.Add(entity);
        if (!await TrySaveAsync(ct)) return DuplicateName(name);
        return ServiceResult.Ok(entity.Id);
    }

    public async Task<IReadOnlyList<string>> SectionsAsync(string stage, CancellationToken ct = default)
    {
        var code = ClassNumbering.NormalizeStage(stage);
        var names = await db.Classes.AsNoTracking().Select(c => c.Name).ToListAsync(ct);
        return names.Select(ClassNumbering.Parse)
            .Where(p => p is { } x && (code is null || x.Stage == code))
            .Select(p => p!.Value.Section).Distinct()
            .OrderBy(s => s.Length).ThenBy(s => s, StringComparer.Ordinal).ToList();
    }

    public async Task<string?> NextNameAsync(string stage, string section, CancellationToken ct = default)
    {
        var code = ClassNumbering.NormalizeStage(stage);
        var sec = ClassNumbering.NormalizeSection(section);
        if (code is null || sec is null) return null;
        var names = await db.Classes.AsNoTracking().Select(c => c.Name).ToListAsync(ct);
        return $"{code}{ClassNumbering.NextNumber(names, code, sec)}{sec}";
    }

    public async Task<ServiceResult> CreateInSectionAsync(string stage, string section, int capacity, bool isActive, CancellationToken ct = default)
    {
        var result = ServiceResult.Ok();
        var code = ClassNumbering.NormalizeStage(stage);
        var sec = ClassNumbering.NormalizeSection(section);
        if (code is null) result.AddError("GradeLevel", "Choose KG or Grade.");
        if (sec is null) result.AddError("Section", "Enter a section of 1–3 letters (A–Z), e.g. A or C.");
        if (capacity is < 1 or > 1000) result.AddError("Capacity", "Enter a capacity between 1 and 1,000.");
        if (!result.Succeeded) return result;

        // Serialize numbering so two people adding to the same section cannot both get the same number.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "EXEC sp_getapplock @Resource = N'classes:numbering', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000", ct);

        var names = await db.Classes.AsNoTracking().Select(c => c.Name).ToListAsync(ct);
        var name = $"{code}{ClassNumbering.NextNumber(names, code!, sec!)}{sec}";
        if (await NameExistsAsync(name, null, ct)) return DuplicateName(name);

        var entity = new SchoolClass
        {
            Name = name,
            NormalizedName = TextNormalizer.ClassKey(name),
            SortOrder = await CalculateSortOrderAsync(name, ct),
            Capacity = capacity,
            IsActive = isActive,
        };
        db.Classes.Add(entity);
        if (!await TrySaveAsync(ct)) return DuplicateName(name);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(entity.Id);
    }

    public Task<ServiceResult> UpdateAsync(int id, string name, int? sortOrder, bool isActive, CancellationToken ct = default) =>
        UpdateAsync(id, name, sortOrder, 25, isActive, ct);

    public async Task<ServiceResult> UpdateAsync(int id, string name, int? sortOrder, int capacity, bool isActive, CancellationToken ct = default)
    {
        var entity = await db.Classes.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This class no longer exists.");

        var check = await ValidateAsync(name, id, ct);
        if (!check.Succeeded) return check;

        entity.Name = TextNormalizer.CleanText(name);
        entity.NormalizedName = TextNormalizer.ClassKey(name);
        if (sortOrder is not null) entity.SortOrder = sortOrder.Value;
        if (capacity > 0) entity.Capacity = capacity;
        entity.IsActive = isActive;
        if (!await TrySaveAsync(ct)) return DuplicateName(name);
        return ServiceResult.Ok(entity.Id);
    }

    public async Task<ServiceResult> SetActiveAsync(int id, bool isActive, CancellationToken ct = default)
    {
        var entity = await db.Classes.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This class no longer exists.");
        entity.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> DeleteAsync(int id, int? reassignToClassId, CancellationToken ct = default)
    {
        var entity = await db.Classes.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This class no longer exists.");

        // Workload and duty records point at the class; keep them intact.
        var references = await db.WorkloadAssignments.CountAsync(w => w.SchoolClassId == id, ct)
            + await db.DutyAssignmentClasses.CountAsync(d => d.SchoolClassId == id, ct)
            + await db.StudentArrivals.CountAsync(a => a.SchoolClassId == id, ct)
            + await db.TimetableAssignments.CountAsync(t => t.SchoolClassId == id, ct)
            + await db.BelongingsRecords.CountAsync(b => b.SchoolClassId == id, ct);
        if (references > 0)
            return ServiceResult.Fail("", $"{entity.Name} is used in the timetable, teacher workload, morning duty or student arrival records, so it cannot be deleted. Deactivate it instead.");

        var students = await db.Students.Where(s => s.SchoolClassId == id).ToListAsync(ct);
        if (students.Count == 0)
        {
            db.Classes.Remove(entity);
            await db.SaveChangesAsync(ct);
            return ServiceResult.Ok(id);
        }

        // A class with students is deleted only after every student is moved to another active class.
        if (reassignToClassId is null)
            return ServiceResult.Fail("ReassignToClassId", $"{entity.Name} has {students.Count} student(s). Choose a class to move them to, or deactivate the class instead.");
        if (reassignToClassId == id)
            return ServiceResult.Fail("ReassignToClassId", "Choose a different class to move the students to.");

        var target = await db.Classes.FirstOrDefaultAsync(c => c.Id == reassignToClassId, ct);
        if (target is null || !target.IsActive)
            return ServiceResult.Fail("ReassignToClassId", "Choose an active class to move the students to.");

        // Moving must not create two students with the same name in the target class.
        var targetNames = await db.Students.Where(s => s.SchoolClassId == target.Id)
            .Select(s => s.NormalizedName).ToListAsync(ct);
        var clashes = students.Where(s => targetNames.Contains(s.NormalizedName)).Select(s => s.FullName).ToList();
        if (clashes.Count > 0)
            return ServiceResult.Fail("ReassignToClassId",
                $"{target.Name} already has student(s) with the same name: {string.Join(", ", clashes.Take(5))}{(clashes.Count > 5 ? "…" : "")}. Resolve these first.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var s in students) s.SchoolClassId = target.Id;
        await db.SaveChangesAsync(ct);
        db.Classes.Remove(entity);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    private async Task<ServiceResult> ValidateAsync(string name, int? exceptId, CancellationToken ct)
    {
        var cleaned = TextNormalizer.CleanText(name);
        if (cleaned.Length == 0) return ServiceResult.Fail("Name", "Enter the class name.");
        if (cleaned.Length > SchoolClass.NameMaxLength) return ServiceResult.Fail("Name", $"Use {SchoolClass.NameMaxLength} characters or fewer.");
        if (TextNormalizer.ClassKey(cleaned).Length == 0) return ServiceResult.Fail("Name", "Use letters or numbers in the class name.");
        if (await NameExistsAsync(cleaned, exceptId, ct)) return DuplicateName(cleaned);
        return ServiceResult.Ok();
    }

    private static ServiceResult DuplicateName(string name) =>
        ServiceResult.Fail("Name", $"A class named \"{TextNormalizer.CleanText(name)}\" already exists.");

    private async Task<int> CalculateSortOrderAsync(string name, CancellationToken ct)
    {
        var existing = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Name, c.SortOrder }).ToListAsync(ct);
        if (existing.Count == 0) return 10;

        // Insert right after the existing class that comes last among those ranked before the new one
        // (PreK → KG1A, KG1B, KG2A… → G1A, G1B… → other names).
        var target = SortKey(name);
        var predecessor = existing.Where(c => CompareKeys(SortKey(c.Name), target) < 0).MaxBy(c => c.SortOrder);

        if (predecessor is null)
        {
            var first = existing[0].SortOrder;
            if (first >= 2) return first / 2;
            await db.Database.ExecuteSqlRawAsync("UPDATE Classes SET SortOrder = SortOrder + 10", ct);
            return first + 5;
        }

        var next = existing.Where(c => c.SortOrder > predecessor.SortOrder).Select(c => (int?)c.SortOrder).Min();
        if (next is null) return predecessor.SortOrder + 10;
        if (next.Value - predecessor.SortOrder >= 2) return predecessor.SortOrder + (next.Value - predecessor.SortOrder) / 2;

        await db.Database.ExecuteSqlRawAsync("UPDATE Classes SET SortOrder = SortOrder + 10 WHERE SortOrder > {0}", [predecessor.SortOrder], ct);
        return predecessor.SortOrder + 5;
    }

    /// <summary>Ordering key: stage (PreK, KG, G, other), then number, then section.</summary>
    private static (int Stage, int Number, string Tail) SortKey(string name)
    {
        var clean = TextNormalizer.CleanText(name).ToUpperInvariant().Replace(" ", "").Replace("-", "");
        if (clean == "PREK") return (0, 0, "");
        if (ClassNumbering.Parse(clean) is { } p) return (p.Stage == "KG" ? 1 : 2, p.Number, p.Section);
        return (3, 0, clean);
    }

    private static int CompareKeys((int Stage, int Number, string Tail) a, (int Stage, int Number, string Tail) b)
    {
        var c = a.Stage.CompareTo(b.Stage);
        if (c == 0) c = a.Number.CompareTo(b.Number);
        return c != 0 ? c : string.CompareOrdinal(a.Tail, b.Tail);
    }

    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
