namespace SchoolManagement.Web.Entities;

/// <summary>Entities with CreatedAt / UpdatedAt maintained by the DbContext (UTC).</summary>
public interface IAuditable
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}

public class SchoolClass : IAuditable
{
    public const int NameMaxLength = 50;

    /// <summary>
    /// The standard class size for a new class (and the database default). A class's own capacity can be changed at any
    /// time and is never a hard limit: a class may hold more students than its capacity (shown as over capacity).
    /// Changing it changes only new classes; existing classes keep their own stored capacity.
    /// </summary>
    public const int DefaultCapacity = 22;

    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Matching key (upper case, no spaces or separators). Unique.</summary>
    public string NormalizedName { get; set; } = "";

    public int SortOrder { get; set; }
    public int Capacity { get; set; } = DefaultCapacity;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Student> Students { get; set; } = new List<Student>();
}

public class Student : IAuditable
{
    public const int NameMaxLength = 150;
    public const int ParentContactMaxLength = StaffMember.ContactMaxLength;

    public int Id { get; set; }
    public string FullName { get; set; } = "";

    /// <summary>Matching key used for duplicate detection. Unique per class.</summary>
    public string NormalizedName { get; set; } = "";

    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    /// <summary>
    /// The mark printed after the name in the school's official name list, exactly as printed including any space before
    /// it ("*", " (*)", " *", " #"); null when there is none. <see cref="FullName"/> is the name without it, and
    /// <see cref="StudentMarks.Printed"/> gives the name exactly as the list printed it. The list does not say what the
    /// marks mean, so they are shown as they are (a star for * and (*)).
    /// </summary>
    public string? NameMarker { get; set; }

    /// <summary>
    /// The background colour of the student's row in the official name list ("#FBE5D6"), when the row was highlighted;
    /// null otherwise. The list does not say what each colour means, so the colour itself is kept and shown.
    /// </summary>
    public string? HighlightColor { get; set; }

    /// <summary>The colour of the student's name in the official name list when it is not black ("#0000FF"); null otherwise.</summary>
    public string? NameColor { get; set; }

    /// <summary>REL as printed in the official name list ("M", "CH"); null when the list gives none.</summary>
    public string? Religion { get; set; }

    /// <summary>
    /// The second language as printed in the official name list's "Sec Lang" column ("F", "G"); null when the list gives
    /// none (classes without the column, or a dash).
    /// </summary>
    public string? SecondLanguage { get; set; }

    // ---- Student profile (all optional: existing students without this information stay valid; nothing is invented) ----
    public DateOnly? BirthDate { get; set; }

    /// <summary>The day the family applied for admission.</summary>
    public DateOnly? AdmissionApplicationDate { get; set; }

    /// <summary>How the student leaves school (Bus / Car); null when not known.</summary>
    public DismissalMethod? DismissalMethod { get; set; }

    /// <summary>The parent's phone number as entered (cleaned); see <see cref="Common.WhatsApp"/> for the chat link.</summary>
    public string? ParentContactNumber { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>How a student leaves school at the end of the day. Numbers are stored: add new values, never reuse one.</summary>
public enum DismissalMethod
{
    Bus = 1,
    Car = 2,
}

/// <summary>The official name list's marks on a student (see <see cref="Student.NameMarker"/> and <see cref="Student.HighlightColor"/>).</summary>
public static class StudentMarks
{
    public const int MarkerMaxLength = 8;
    public const int ColorLength = 7;
    public const int ReligionMaxLength = 10;
    public const int LanguageMaxLength = 20;

    /// <summary>
    /// The second language a "Sec Lang" code stands for: F = French, G = German (Deutsch) — the two languages of the
    /// school's Second Language department; any other value is shown as printed.
    /// </summary>
    public static string? LanguageName(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        null or "" => null,
        "F" => "French",
        "G" => "Deutsch",
        _ => code.Trim(),
    };

    /// <summary>The name exactly as the official list printed it (name + mark).</summary>
    public static string Printed(string fullName, string? marker) => marker is null ? fullName : fullName + marker;

    /// <summary>The mark itself without the space before it ("*", "(*)", "#").</summary>
    public static string? Symbol(string? marker) => string.IsNullOrWhiteSpace(marker) ? null : marker.Trim();

    /// <summary>A star mark: "*" or "(*)".</summary>
    public static bool IsStar(string? marker) => Symbol(marker) is "*" or "(*)";

    /// <summary>A highlight colour that is safe to put in a style attribute ("#RRGGBB"), or null.</summary>
    public static string? SafeColor(string? color) =>
        color is { Length: ColorLength } c && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit) ? c.ToUpperInvariant() : null;
}

/// <summary>
/// Any school employee. Stored in one table (StaffMembers) with a StaffType discriminator, so
/// General Staff can be added later as another subclass without touching Teachers.
/// </summary>
public abstract class StaffMember : IAuditable
{
    public const int NameMaxLength = 150;
    public const int JobTitleMaxLength = 100;
    public const int ContactMaxLength = 30;

    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? JobTitle { get; set; }

    /// <summary>Stored as text to keep leading zeros and international prefixes.</summary>
    public string? ContactNumber { get; set; }

    /// <summary>Digits only; empty when there is no number. Used for duplicate detection.</summary>
    public string NormalizedContact { get; set; } = "";

    /// <summary>A second phone number, when the source gives two (kept as written).</summary>
    public string? SecondaryContactNumber { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>Department-specific subcategory (e.g. Islamic, French, AI, Wellbeing). See DepartmentCatalog.</summary>
    public string? Subcategory { get; set; }

    /// <summary>Second level under a subcategory where the department has one (Religion: Islamic → Religion / Quran).</summary>
    public string? Subject { get; set; }

    // ---- Employment (all optional: staff added before these were kept stay valid with nothing set) ----
    /// <summary>First day of the contract. Before it (and after <see cref="ContractEndDate"/>) the person is not active staff.</summary>
    public DateOnly? ContractStartDate { get; set; }
    /// <summary>Last day of the contract; null = ongoing.</summary>
    public DateOnly? ContractEndDate { get; set; }
    /// <summary>The initial trial period (e.g. 90 days), separate from the contract duration.</summary>
    public int? ProbationLength { get; set; }
    public ProbationUnit? ProbationUnit { get; set; }
    /// <summary>First day of probation (defaults to the contract start date).</summary>
    public DateOnly? ProbationStartDate { get; set; }
    /// <summary>Last day of probation (start + length − 1 day, both days included).</summary>
    public DateOnly? ProbationEndDate { get; set; }
    public EmploymentType? EmploymentType { get; set; }
    /// <summary>Part-time staff: the weekdays they work (full-time staff have none).</summary>
    public ICollection<StaffWorkingDay> WorkingDays { get; set; } = new List<StaffWorkingDay>();
    public WorkingHoursType? WorkingHoursType { get; set; }
    /// <summary>Daily start time (fixed hours) or start of the agreed range (flexible hours, within 08:00–15:00).</summary>
    public TimeOnly? WorkStart { get; set; }
    public TimeOnly? WorkEnd { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Teacher : StaffMember
{
}

/// <summary>An academic department. Management Team positions are not departments.</summary>
public class Department
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Legacy comma list kept for existing data; the managed list is <see cref="SubcategoryItems"/>.</summary>
    public string? Subcategories { get; set; }

    /// <summary>Form label for the first level, e.g. "Religion type", "Language", "Subdepartment".</summary>
    public string? SubcategoryLabel { get; set; }

    /// <summary>Form label for the second level, e.g. "Subject" (Religion → Islamic → Quran).</summary>
    public string? SubjectLabel { get; set; }

    public int SortOrder { get; set; }

    public ICollection<StaffMember> StaffMembers { get; set; } = new List<StaffMember>();
    public ICollection<DepartmentHeadAssignment> HeadAssignments { get; set; } = new List<DepartmentHeadAssignment>();
    public ICollection<DepartmentSubcategory> SubcategoryItems { get; set; } = new List<DepartmentSubcategory>();
}

public class StudentTransferHistory
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public int FromClassId { get; set; }
    public SchoolClass FromClass { get; set; } = null!;
    public int ToClassId { get; set; }
    public SchoolClass ToClass { get; set; } = null!;
    public DateTime TransferredAt { get; set; }
    public string? TransferredBy { get; set; }
}

/// <summary>Simple key / value application settings (school name, academic year).</summary>
public class AppSetting
{
    public const int KeyMaxLength = 100;
    public const int ValueMaxLength = 500;

    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}
