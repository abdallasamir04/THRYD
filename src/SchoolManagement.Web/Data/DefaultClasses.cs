using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

/// <summary>
/// The 25 classes created with the database. They are inserted once by the InitialCreate migration
/// (EF Core seed data with fixed Ids), so restarting the app or applying later migrations never
/// creates them again, and renaming or deactivating one is never undone.
/// </summary>
public static class DefaultClasses
{
    public static readonly string[] Names =
    [
        "PreK",
        "KG1A", "KG1B", "KG2A", "KG2B",
        "G1A", "G1B", "G2A", "G2B", "G3A", "G3B", "G4A", "G4B", "G5A", "G5B",
        "G6A", "G6B", "G7A", "G7B", "G8A", "G8B", "G9A", "G9B", "G10A", "G10B",
    ];

    private static readonly DateTime SeededAt = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<SchoolClass> Seed() =>
        Names.Select((name, i) => new SchoolClass
        {
            Id = i + 1,
            Name = name,
            NormalizedName = TextNormalizer.ClassKey(name),
            SortOrder = (i + 1) * 10,
            Capacity = 25,
            IsActive = true,
            CreatedAt = SeededAt,
            UpdatedAt = SeededAt,
        });
}
