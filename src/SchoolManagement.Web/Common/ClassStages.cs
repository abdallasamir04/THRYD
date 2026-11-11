using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.Common;

/// <summary>A class's grade, read from its name: PreK → ("PREK", 0), KG1A → ("KG", 1), G10B → ("G", 10).</summary>
public readonly record struct ClassGrade(string Code, int Number)
{
    public string Label => Code switch
    {
        ClassStages.PreKCode => "Pre-K",
        _ => $"{Code}{Number}",
    };
}

public enum StageLayout
{
    /// <summary>The stage page lists each group with its classes (KG Stages: Pre-K, KG1, KG2).</summary>
    Sections,
    /// <summary>The stage page shows a card per group; each card opens that group's classes (Elementary).</summary>
    Cards,
    /// <summary>The stage has one group, so the stage page is its class list (Middle School, High School).</summary>
    Single,
}

/// <summary>
/// The four academic stages of the Classes page. A class's stage is worked out from its name
/// (PreK, KG1A, G7B…), so stages need no table or column of their own: every class keeps its Id,
/// its students and its other records, and renaming a class moves it to the matching stage.
/// High School has no upper grade: it starts with G10, and G11, G12… classes join it when added.
/// </summary>
public static class ClassStages
{
    public const string PreKCode = "PREK";

    /// <summary>A group of grades inside a stage. <see cref="MaxGrade"/> null = open-ended (High School).</summary>
    public sealed record Group(string Slug, string Title, string Grades, string Code, int MinGrade, int? MaxGrade)
    {
        public bool IsPreK => Code == PreKCode;
        public bool IsOpenEnded => MaxGrade is null;

        /// <summary>Pre-K has no numbered name format (KG/G + number + section), so it has no automatic Add Class.</summary>
        public bool CanAdd => !IsPreK;

        public bool Contains(ClassGrade grade) =>
            grade.Code == Code && grade.Number >= MinGrade && (MaxGrade is null || grade.Number <= MaxGrade);
    }

    public sealed record Stage(string Slug, string Title, string Description, string Icon, string Tone, StageLayout Layout, IReadOnlyList<Group> Groups)
    {
        public string Href => $"/classes/stages/{Slug}";
        public string GroupHref(Group g) => Layout == StageLayout.Single ? Href : $"{Href}/{g.Slug}";
    }

    public static readonly IReadOnlyList<Stage> All =
    [
        new("kg", "KG Stages", "Pre-Kindergarten and Kindergarten classes: Pre-K, KG1 and KG2.", "sparkles", "violet", StageLayout.Sections,
        [
            new("prek", "Pre-K", "Pre-Kindergarten", PreKCode, 0, 0),
            new("kg1", "KG1", "Kindergarten 1", "KG", 1, 1),
            new("kg2", "KG2", "Kindergarten 2", "KG", 2, 2),
        ]),
        new("elementary", "Elementary", "Lower Elementary (Grades 1–3) and Upper Elementary (Grades 4–6).", "school", "blue", StageLayout.Cards,
        [
            new("lower", "Lower Elementary", "Grades 1–3", "G", 1, 3),
            new("upper", "Upper Elementary", "Grades 4–6", "G", 4, 6),
        ]),
        new("middle", "Middle School", "Grades 7, 8 and 9.", "layers", "teal", StageLayout.Single,
        [
            new("middle", "Middle School", "Grades 7–9", "G", 7, 9),
        ]),
        new("high", "High School", "Grade 10 classes. Higher grades join this stage when they are added.", "graduation-cap", "indigo", StageLayout.Single,
        [
            new("high", "High School", "Grade 10 onward", "G", 10, null),
        ]),
    ];

    public static Stage? Find(string? slug) =>
        All.FirstOrDefault(s => string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a group by its slug (unique across stages), with its stage.</summary>
    public static (Stage Stage, Group Group)? FindGroup(string? slug)
    {
        foreach (var s in All)
            foreach (var g in s.Groups)
                if (string.Equals(g.Slug, slug, StringComparison.OrdinalIgnoreCase)) return (s, g);
        return null;
    }

    /// <summary>Reads the grade from a class name; null for names outside the PreK / KG / G formats.</summary>
    public static ClassGrade? GradeOf(string? name)
    {
        var clean = TextNormalizer.CleanText(name).ToUpperInvariant().Replace(" ", "").Replace("-", "");
        // "PreK", "Pre-K", and Pre-K classes with a section letter ("PreK A").
        if (clean.StartsWith(PreKCode, StringComparison.Ordinal) && clean.Length <= PreKCode.Length + 3
            && clean[PreKCode.Length..].All(c => c is >= 'A' and <= 'Z'))
            return new ClassGrade(PreKCode, 0);
        return ClassNumbering.Parse(clean) is { } p ? new ClassGrade(p.Stage, p.Number) : null;
    }

    /// <summary>The stage and group a class belongs to; null when its grade is in no stage (e.g. KG3A).</summary>
    public static (Stage Stage, Group Group)? Locate(string? name)
    {
        if (GradeOf(name) is not { } grade) return null;
        foreach (var s in All)
            foreach (var g in s.Groups)
                if (g.Contains(grade)) return (s, g);
        return null;
    }

    /// <summary>
    /// Grades offered by the group's Add Class form. A bounded group offers its range (G1–G3). High School
    /// offers its existing grades plus the next one (G10 → G10, G11), so a new grade can be started.
    /// </summary>
    public static IReadOnlyList<int> AddableGrades(Group group, IEnumerable<string> existingNames)
    {
        if (!group.CanAdd) return [];
        if (group.MaxGrade is { } max) return Enumerable.Range(group.MinGrade, max - group.MinGrade + 1).ToList();
        var used = existingNames.Select(GradeOf).Where(g => g is { } x && group.Contains(x)).Select(g => g!.Value.Number).ToList();
        var top = used.Count == 0 ? group.MinGrade - 1 : used.Max();
        return Enumerable.Range(group.MinGrade, Math.Min(top + 1, 99) - group.MinGrade + 1).ToList();
    }

    /// <summary>Grades shown as sections on the group page: the whole range, or (open-ended) the grades that have classes.</summary>
    public static IReadOnlyList<int> ShownGrades(Group group, IEnumerable<string> namesInGroup)
    {
        if (group.MaxGrade is { } max) return Enumerable.Range(group.MinGrade, max - group.MinGrade + 1).ToList();
        var used = namesInGroup.Select(GradeOf).Where(g => g is not null).Select(g => g!.Value.Number).Distinct().Order().ToList();
        return used.Count == 0 ? [group.MinGrade] : used;
    }
}
