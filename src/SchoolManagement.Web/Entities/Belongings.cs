namespace SchoolManagement.Web.Entities;

/// <summary>Status of one belonging or academic item on one day. Numbers are stored.</summary>
public enum ItemStatus
{
    Green = 1,          // good / available
    Red = 2,            // missing / not good
    NotApplicable = 3,  // not required: excluded from every readiness calculation
}

/// <summary>An academic year (e.g. "2026–2027") with exactly four quarters.</summary>
public class AcademicYear : IAuditable
{
    public const int NameMaxLength = 50;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<AcademicQuarter> Quarters { get; set; } = new List<AcademicQuarter>();
}

/// <summary>
/// One of the four quarters. Dates are set by the administration; the 8 school weeks (Sunday–Thursday)
/// are derived from them: week 1 is the school week containing the start date, week 8 must contain the
/// end date. Days before the start / after the end are not part of the quarter (partial weeks).
/// </summary>
public class AcademicQuarter
{
    public const int NameMaxLength = 50;
    public const int Weeks = 8;

    public int Id { get; set; }
    public int AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;

    /// <summary>1–4.</summary>
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

/// <summary>
/// A student's belongings on one school day: the five personal items (null = not recorded yet), the
/// day's notes and the academic material rows per subject. One per student per date.
/// </summary>
public class BelongingsRecord : IAuditable
{
    public const int NotesMaxLength = 2000;

    public int Id { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;

    /// <summary>The student's class on that day (kept if the student moves class later).</summary>
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    public int AcademicQuarterId { get; set; }
    public AcademicQuarter Quarter { get; set; } = null!;
    public DateOnly Date { get; set; }

    public ItemStatus? Jacket { get; set; }
    public ItemStatus? SchoolBag { get; set; }
    public ItemStatus? PencilCase { get; set; }
    public ItemStatus? WaterFlask { get; set; }
    public ItemStatus? LunchBox { get; set; }

    public string? Notes { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<AcademicMaterialRecord> Materials { get; set; } = new List<AcademicMaterialRecord>();
}

/// <summary>Book and Copy Book of one subject on one day (independent statuses). One per record + department + subject.</summary>
public class AcademicMaterialRecord
{
    public int Id { get; set; }
    public int BelongingsRecordId { get; set; }
    public BelongingsRecord Record { get; set; } = null!;
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string Subject { get; set; } = "";
    public ItemStatus Book { get; set; }
    public ItemStatus CopyBook { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Earlier versions of a day's notes (kept when notes are edited).</summary>
public class BelongingsNoteHistory
{
    public long Id { get; set; }
    public int BelongingsRecordId { get; set; }
    public BelongingsRecord Record { get; set; } = null!;
    public string? PreviousNotes { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
}
