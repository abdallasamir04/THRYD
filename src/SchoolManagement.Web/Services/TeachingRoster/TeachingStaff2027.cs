namespace SchoolManagement.Web.Services.TeachingRoster;

/// <summary>
/// Teaching Staff 2027 (Royal Kings International School, academic year 2026/2027), transcribed row by row. Every cell is
/// kept exactly as written; a blank cell ("—") is null, never zero. Columns: No., Name, Subject, Grade Levels, Number of
/// Classes, ص, م, بريك. Rows 49, 51 and 55 are blank in the source; row 31 has a subject, grade and class count but no name
/// (no staff member is created for it). Row 42 carries handwritten corrections that are not confirmed: its printed values
/// are here and the corrections are review items (see <see cref="Row42Corrections"/>).
/// </summary>
public static class TeachingStaff2027
{
    public const string SourceName = "Teaching Staff 2027";
    public const string AcademicYear = "2026/2027";
    public const string KeyPrefix = "teaching-staff-2027";

    /// <param name="HomeroomClass">Homeroom rows only: the class the Grade Levels cell names ("KG 1 A" → KG1A, "Pre" → PreK).</param>
    public sealed record Row(int No, string? Name, string? Subject, string? Grades, int? Classes, int? Sad, int? Meem, int? Break, string? HomeroomClass = null)
    {
        /// <summary>The row has values but no name: it is kept as an unresolved source row and never becomes a staff member.</summary>
        public bool HasNoName => Name is null && !IsBlank;

        public bool IsBlank => Name is null && Subject is null && Grades is null && Classes is null && Sad is null && Meem is null && Break is null;
        public string Key => $"{KeyPrefix}:row-{No:00}";
    }

    public static readonly Row[] Rows =
    [
        // ---- page 1
        new(1, "Mamdouh", "Math", "G5 + G10A", 14, null, null, 2),
        new(2, "Mohamed Ali", "Math", "G8+G9+G10B", 22, null, null, null),
        new(3, "Marwa", "Math", "G6B-7/G4-5-6", 18, null, null, null),
        new(4, "Menna", "Math", "G6A/G7-8-9", 16, null, null, 1),
        new(5, "Heba Emad", "Math", "G3-4", 16, null, null, null),
        new(6, "Salma", "Math", "G1 + G2", 16, null, null, null),
        new(7, "Houda", "Math", "G1-2-3", 12, 5, 5, 1),
        new(8, "Mohamed Essam", "Science", "G8 + G9 + G7A", 20, null, null, null),
        new(9, "Maro", "Science", "G10", 4, null, null, null),
        new(10, "Mehrail", "Science", "G6 + G7B-7A", 14, 5, null, 1),
        new(11, "Menaa", "Science", "G4 + G5", 16, null, null, 1),
        new(12, "Sara Abdelkader", "Science", "G3-4-5", 12, 5, 5, 1),
        new(13, "Rana", "Science +AI", "G1 + G2/G6-9", 20, 5, null, null),
        new(14, "Amr", "Science", "G8-9-10", 10, 5, 5, 1),
        new(15, "Mohamed Samir", "English", "G10", 8, null, null, null),
        new(16, "Eslam", "English", "G8+10", 14, null, null, null),
        new(17, "Rania", "English", "G7 + G9A", 16, null, null, null),
        new(18, "Eman Gooda", "English+CB", "G9B/G7 to G9", 12, null, null, null),
        new(19, "Asmaa", "English", "G5 + G6", 20, null, null, null),
        new(20, "Aisha", "English + CB", "G4-5-6", 16, null, null, null),
        new(21, "Saritha", "English", "G3", 18, null, null, null),
        new(22, "Jackie", "English + CB", "G2", 20, null, null, null),
        new(23, "Cherry", "English + CB", "G1", 20, null, null, null),
        // ---- page 2
        new(24, "Enjy", "F", "G6 to G10", 11, null, null, null),
        new(25, "Osama", "F", "Pre – G1-2", 6, 5, 5, 2),
        new(26, "Amira", "F", "G3 to G4", 10, null, 5, 1),
        new(27, "Manar", "F", "Not specified", 0, null, 5, 1),
        new(28, "Habiba", "G", "G5 to G10", 14, null, null, 3),
        new(29, "Mohamed Ali", "G", "Kg1-kg2-g1-2-3", 11, null, null, null),
        new(30, "Rawan", "G", "G4", 4, null, 5, 1),
        new(31, null, "G", "G4", 4, null, null, null),   // no name in the source
        new(32, "Makak Khaled", "ESS", "G6 to G10", 20, null, null, null),
        new(33, "Nagwa", "ESS+CBG3", "G2 + G4+5", 14, 5, null, 1),
        new(34, "Alaa", "ESS + ASS", "G1 + G3+G7A", 10, null, 5, 1),
        new(35, "Fatma Salah", "ASS", "G7B to 10", 14, 5, null, null),
        new(36, "Zinab", "ASS", "G4 to G6", 12, 5, 5, null),
        new(37, "Eman", "Arabic", "G1-2", 16, null, null, null),
        new(38, "Sara", "Arabic", "G4-5", 16, 5, null, null),
        new(39, "Shaimaa", "Arabic", "G6-7", 16, 5, null, null),
        new(40, "Adel", "Arabic +Rel", "G9-10", 18, null, 5, 1),
        new(41, "Khaled", "Arabic +Rel", "G8-7", 18, 5, 5, 1),
        // Row 42: the printed values only (name "Mostafa"; G8 and G10 printed, one of them crossed/modified; no printed
        // class count). The handwritten corrections are review items, never confirmed data.
        new(42, "Mostafa", "Arabic +Rel", Row42PrintedGrades, null, null, null, null),
        new(43, "Norhan", "Arabic +Rel", "G3/4-5-6", 18, 5, 5, null),
        new(44, "Ester", "Rel", "Pre to G5", 10, null, 5, 1),
        // ---- page 3
        new(45, "Nabila", "Rel", "G6to G10", 14, null, null, 1),
        new(46, "Mohamed Morsy", "PE", "G4B-8-9-10", 15, null, null, null),
        new(47, "Mahmoud Ahmady", "PE", "G4A+G5+G7", 16, null, null, null),
        new(48, "Nadia", "PE", "Pre to G1", 14, null, null, null),
        new(49, null, null, null, null, null, null, null),
        new(50, "Fatma", "PE", "G2-3", 10, null, 5, null),
        new(51, null, null, null, null, null, null, null),
        new(52, "Nada", "Art", "Pre to G3", 20, null, null, null),
        new(53, "Sara", "Art", "G4B to G10", 16, null, null, null),
        new(54, "Naira", "Music", "Pre -kg-g3", 16, null, 5, null),
        new(55, null, null, null, null, null, null, null),
        new(56, "Heba Gamal", "Rel", "Pre – kg2B – G1", 6, null, 5, 1),
        new(57, "Afaf", "Arabic + Rel", "KG2A-Rel 2-3", 14, null, null, 1),
        new(58, "Elham", "Arabic", "Pre -KG2B", 13, null, null, null),
        new(59, "Norhan", "Arabic + Rel", "KG1A-KG1B", 16, 5, 5, null),
        new(60, "Nessly", "Home Room", "Pre", 20, null, null, null, "PreK"),
        new(61, "Nada", "Home Room", "KG 1 A", 20, null, null, null, "KG1A"),
        new(62, "Jane", "Home Room", "KG 1 B", 20, null, null, null, "KG1B"),
        new(63, "Milka", "Home Room", "KG 2 A", 20, null, null, null, "KG2A"),
        new(64, "Adrea", "Home Room", "KG 2 B", 20, null, null, null, "KG2B"),
    ];

    /// <summary>Row 42's Grade Levels cell as printed: two grades, one of them crossed out or modified by hand.</summary>
    public const string Row42PrintedGrades = "G8 and G10 (one appears crossed/modified)";

    /// <summary>Row 42's handwritten observations (unconfirmed): field, printed value, what appears to be written.</summary>
    public static readonly (string Field, string? Printed, string Observed)[] Row42Corrections =
    [
        ("Name", "Mostafa", "Ali"),
        ("Grade Levels", "G8 and G10", "One of the two appears crossed/modified"),
        ("Number of Classes", null, "28"),
    ];

    /// <summary>
    /// Subject parts → existing department (and subcategory). F and G follow the school's own convention for the Second
    /// Language department (StudentMarks.LanguageName: F = French, G = Deutsch). "AI" of "Science +AI" is kept in the
    /// subject label only (mapped to Science, as instructed). CB / CBG3 have no confirmed department: review items.
    /// </summary>
    public static readonly Dictionary<string, (string Department, string? Subcategory)> SubjectParts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Math"] = ("Math", null),
        ["Science"] = ("Science", null),
        ["English"] = ("English", null),
        ["F"] = ("Second Language", "French"),
        ["G"] = ("Second Language", "Deutsch"),
        ["ESS"] = ("ESS", null),
        ["ASS"] = ("ASS", null),
        ["Arabic"] = ("Arabic", null),
        ["Rel"] = ("Religion", null),
        ["PE"] = ("PE", null),
        ["Art"] = ("Art", null),
        ["Music"] = ("Music", null),
    };

    /// <summary>Subject parts kept in the label but deliberately not mapped to a department.</summary>
    public static readonly HashSet<string> LabelOnlyParts = new(StringComparer.OrdinalIgnoreCase) { "AI" };

    public const string HomeRoom = "Home Room";
}
