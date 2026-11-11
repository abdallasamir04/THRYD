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

    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Matching key (upper case, no spaces or separators). Unique.</summary>
    public string NormalizedName { get; set; } = "";

    public int SortOrder { get; set; }
    public int Capacity { get; set; } = 25;
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
