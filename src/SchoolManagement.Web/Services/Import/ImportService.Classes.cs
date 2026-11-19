using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Import;

/// <summary>
/// Import / Export → Classes. A class has no separate grade or section columns: its name is the grade and section
/// (G3B, KG1A; Pre-K classes PreK, PreKB), as on the Classes pages. Each row's Grade Level and Section are checked against
/// its Class Name, against the stages (KG1–KG2, Grade 1 onward; High School adds one new grade at a time) and against the
/// classes that exist. A class that already exists is reported as a duplicate and never changed (its capacity, status and
/// students stay as they are). New classes are created one at a time by the Classes module's own Add Class save
/// (<see cref="IClassService.CreateInGradeAsync"/>): the same name rule, numbering lock, sort order and duplicate check.
/// </summary>
public sealed partial class ImportService
{
    private IClassService ClassModule => classes ?? new ClassService(db);

    private static readonly Dictionary<string, bool> ActiveWords = Words(new (string, bool)[]
    {
        ("Active", true), ("Yes", true), ("Y", true), ("True", true), ("1", true), ("Open", true), ("Enabled", true),
        ("Inactive", false), ("Not Active", false), ("No", false), ("N", false), ("False", false), ("0", false), ("Closed", false), ("Disabled", false),
        ("نشط", true), ("فعال", true), ("نعم", true), ("مفعل", true), ("غير نشط", false), ("غير فعال", false), ("لا", false), ("معطل", false),
    });

    /// <summary>A grade level as written: Pre-K, KG1, Grade 3, G3, Year 3 or 3.</summary>
    public static (string Stage, int Grade)? ParseGradeLevel(string? text)
    {
        var key = TextNormalizer.HeaderKey(TextNormalizer.ToLatinDigits(text ?? ""));
        if (key.Length == 0) return null;
        if (key is "prek" or "prekg" or "prekindergarten" or "nursery" or "تمهيدي" or "بريكيجي") return (ClassStages.PreKCode, 0);
        var m = GradeLevelPattern().Match(key);
        if (!m.Success) return null;
        var number = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var kg = m.Groups["p"].Value is "kg" or "k" or "kindergarten" or "روضه" or "كيجي";
        return (kg ? "KG" : "G", number);
    }

    /// <summary>The grade level of a stage and grade as the export writes it: Pre-K, KG1, Grade 3.</summary>
    public static string GradeLevelLabel(string stage, int grade) => stage switch
    {
        ClassStages.PreKCode => "Pre-K",
        "KG" => $"KG{grade}",
        _ => $"Grade {grade}",
    };

    /// <summary>A class's identity: stage, grade and section ("PreK" is Pre-K section A). Null for names outside the class format.</summary>
    public static (string Stage, int Grade, string Section)? ClassIdentity(string? name)
    {
        if (ClassStages.GradeOf(name) is { Code: ClassStages.PreKCode }) return ClassNumbering.PreKSection(name) is { } s ? (ClassStages.PreKCode, 0, s) : null;
        return ClassNumbering.Parse(name) is { } p ? (p.Stage, p.Number, p.Section) : null;
    }

    private async Task ValidateClassesAsync(List<PreviewRow> rows, CancellationToken ct)
    {
        var existing = await db.Classes.AsNoTracking().Select(c => new { c.Name, c.Capacity, c.IsActive, Students = c.Students.Count }).ToListAsync(ct);
        var byIdentity = new Dictionary<(string, int, string), (string Name, int Capacity, bool IsActive, int Students)>();
        foreach (var c in existing)
            if (ClassIdentity(c.Name) is { } id) byIdentity.TryAdd(id, (c.Name, c.Capacity, c.IsActive, c.Students));
        var existingKeys = existing.Select(c => TextNormalizer.ClassKey(c.Name)).ToHashSet();
        var seen = new Dictionary<(string, int, string), int>();
        // Names the import will have created by each row (High School may start the next grade once the one before exists).
        var names = existing.Select(c => c.Name).ToList();

        foreach (var row in rows)
        {
            var nameText = row.Value(ImportDefinitions.ClassName);
            var gradeText = row.Value(ImportDefinitions.GradeLevel);
            var sectionText = row.Value(ImportDefinitions.Section);
            var capacityText = row.Value(ImportDefinitions.Capacity);
            var activeText = row.Value(ImportDefinitions.ActiveStatus);

            if (nameText.Length == 0) row.Errors.Add("The class name is missing (e.g. G3B, KG1A, PreK).");
            if (gradeText.Length == 0) row.Errors.Add("The grade level is missing (Pre-K, KG1, KG2, Grade 1 …).");

            // Grade level: one of the school's stages.
            var grade = gradeText.Length == 0 ? null : ParseGradeLevel(gradeText);
            ClassStages.Group? group = null;
            if (gradeText.Length > 0 && grade is null)
                row.Errors.Add($"“{gradeText}” is not a grade level. Use Pre-K, KG1, KG2 or Grade 1, Grade 2 …");
            else if (grade is { } g)
            {
                group = ClassStages.Locate(g.Stage == ClassStages.PreKCode ? "PreK" : $"{g.Stage}{g.Grade}A")?.Group;
                if (group is null) row.Errors.Add($"{GradeLevelLabel(g.Stage, g.Grade)} is not a grade in THRYD (Pre-K, KG1–KG2, Grade 1 onward).");
            }

            // Section: 1–3 letters.
            var section = sectionText.Length == 0 ? null : ClassNumbering.NormalizeSection(sectionText);
            if (sectionText.Length > 0 && section is null) row.Errors.Add($"“{sectionText}” is not a section. Use 1–3 letters, e.g. A or C.");

            // Class name: grade + section, and it must agree with the Grade Level and Section columns.
            var identity = nameText.Length == 0 ? null : ClassIdentity(nameText);
            if (nameText.Length > 0 && identity is null)
                row.Errors.Add($"“{nameText}” is not a class name in THRYD's format: grade and section, e.g. PreK, KG1A, G3B.");
            else if (identity is { } id && grade is { } gl)
            {
                if (id.Stage != gl.Stage || id.Grade != gl.Grade)
                    row.Errors.Add($"The class name {nameText} is {GradeLevelLabel(id.Stage, id.Grade)}, but the grade level says {GradeLevelLabel(gl.Stage, gl.Grade)}.");
                if (section is not null && section != id.Section)
                    row.Errors.Add($"The class name {nameText} is section {id.Section}, but the section column says {section}.");
            }

            // Capacity: optional, a whole number of students (empty: the standard capacity).
            var capacity = SchoolClass.DefaultCapacity;
            var defaultCapacity = capacityText.Length == 0;
            if (!defaultCapacity)
            {
                if (!decimal.TryParse(TextNormalizer.ToLatinDigits(capacityText), NumberStyles.Number, CultureInfo.InvariantCulture, out var cap) || cap != decimal.Truncate(cap))
                    row.Errors.Add($"“{capacityText}” is not a capacity. Enter a whole number of students, 1–1,000.");
                else if (cap is < 1 or > 1000)
                    row.Errors.Add($"A capacity of {cap:0} is out of range. Enter 1–1,000 students.");
                else capacity = (int)cap;
            }

            // Active status: optional (empty: Active).
            var isActive = true;
            if (activeText.Length > 0 && !ActiveWords.TryGetValue(TextNormalizer.HeaderKey(activeText), out isActive))
                row.Errors.Add($"“{activeText}” is not a status. Use Active or Inactive (empty: Active).");

            if (row.Errors.Count > 0 || identity is not { } key || group is null) { row.Status = RowStatus.Invalid; continue; }

            // The name Add Class gives this grade and section (a new Pre-K class is PreK + its section, e.g. PreKA).
            var name = key.Stage == ClassStages.PreKCode ? ClassNumbering.PreKName(key.Section) : $"{key.Stage}{key.Grade}{key.Section}";
            row.ClassName = name;

            // Already in the school, or earlier in this file: a duplicate, never changed.
            if (byIdentity.TryGetValue(key, out var found) || existingKeys.Contains(TextNormalizer.ClassKey(name)))
            {
                row.Status = RowStatus.Duplicate;
                row.Errors.Add(found.Name is null
                    ? $"{name} already exists. It is not changed."
                    : $"{found.Name} already exists ({Ui.Plural(found.Students, "student", "students")}, capacity {found.Capacity}, {(found.IsActive ? "active" : "inactive")}). It is not changed.");
                continue;
            }
            if (seen.TryGetValue(key, out var firstRow))
            {
                row.Status = RowStatus.Duplicate;
                row.Errors.Add($"{name} is already in this file (row {firstRow}). Only the first row is imported.");
                continue;
            }

            // High School grows one grade at a time (the same rule as Add Class): Grade 12 needs Grade 11 first.
            if (group.IsOpenEnded && !ClassStages.AddableGrades(group, names).Contains(key.Grade))
            {
                row.Status = RowStatus.Invalid;
                row.Errors.Add($"Grade {key.Grade} cannot be added yet: High School adds one new grade at a time (add Grade {key.Grade - 1} classes first).");
                continue;
            }

            if (TextNormalizer.ClassKey(nameText) != TextNormalizer.ClassKey(name))
                row.Warnings.Add($"Saved as {name} (the THRYD spelling of “{nameText}”).");
            if (defaultCapacity) row.Warnings.Add($"No capacity given: the standard capacity of {SchoolClass.DefaultCapacity} is used.");
            seen[key] = row.RowNumber;
            names.Add(name);
            row.NewClass = new ClassImportInput(key.Stage, key.Grade, key.Section, name, capacity, isActive, defaultCapacity);
        }
    }

    /// <summary>Creates each ready class through Add Class's save; a row that fails there is reported with the reason.</summary>
    private async Task<int> CreateClassesAsync(List<PreviewRow> ready, CancellationToken ct)
    {
        var created = 0;
        foreach (var row in ready)
        {
            var c = row.NewClass!;
            ServiceResult result;
            try
            {
                result = await ClassModule.CreateInGradeAsync(c.Stage == ClassStages.PreKCode ? "PreK" : c.Stage, c.Grade, c.Section, c.Capacity, c.IsActive, ct);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Class import: row {Row} could not be saved", row.RowNumber);
                db.ChangeTracker.Clear();
                result = ServiceResult.Fail("", "The class could not be saved. Try this row again.");
            }
            if (result.Succeeded) { created++; continue; }
            var duplicate = result.Errors.Any(e => e.Value.Contains("already", StringComparison.OrdinalIgnoreCase));
            row.Status = duplicate ? RowStatus.Duplicate : RowStatus.Invalid;
            row.Errors.AddRange(result.Errors.Select(e => e.Value));
        }
        return created;
    }

    /// <summary>"kg1", "kindergarten2", "grade3", "g3", "year3", "3", "الصف3".</summary>
    [GeneratedRegex(@"^(?<p>kg|k|kindergarten|روضه|كيجي|grade|gr|g|year|y|الصف|صف)?(?<n>\d{1,2})$")]
    private static partial Regex GradeLevelPattern();
}
