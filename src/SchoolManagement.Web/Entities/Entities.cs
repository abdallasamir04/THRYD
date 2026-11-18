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
    /// </summary>
    public const int DefaultCapacity = 20;

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

    public int Id { get; set; }
    public string FullName { get; set; } = "";

    /// <summary>Matching key used for duplicate detection. Unique per class.</summary>
    public string NormalizedName { get; set; } = "";

    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
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
