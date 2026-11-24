using System.ComponentModel.DataAnnotations.Schema;

namespace SchoolManagement.Web.Entities;

// ============================================================ Teaching Staff roster (source data)
//
// One row of a source roster (Teaching Staff 2027), kept exactly as the source gives it: name, subject and grade levels
// as written, Number of Classes and the three Arabic columns ص, م and بريك (a blank cell is NULL, never zero). The roster
// row links to the existing staff member when the identity is reliable; it is never workload, timetable or a calculation.

/// <summary>What a roster row describes. Numbers are stored.</summary>
public enum RosterRole
{
    SubjectTeacher = 1,
    HomeroomTeacher = 2,
}

public class TeachingRosterEntry
{
    public const int TextMaxLength = 100;
    public const int KeyMaxLength = 60;
    public const int NoteMaxLength = 300;

    public int Id { get; set; }

    /// <summary>Stable key of the row ("teaching-staff-2027:row-07"): re-running the import never adds it twice.</summary>
    public string SourceKey { get; set; } = "";

    /// <summary>The source document, e.g. "Teaching Staff 2027".</summary>
    public string SourceName { get; set; } = "";

    /// <summary>The academic year the source covers, as written: "2026/2027".</summary>
    public string AcademicYear { get; set; } = "";
    public int RowNumber { get; set; }

    /// <summary>The source row is empty (kept so every row number is accounted for; no staff member is created).</summary>
    public bool IsBlank { get; set; }

    // ---- the source cells, exactly as written (null = blank cell)
    public string? StaffName { get; set; }
    public string? Subject { get; set; }
    public string? GradeLevels { get; set; }
    public int? NumberOfClasses { get; set; }

    /// <summary>Source column "ص" (kept as written; not interpreted).</summary>
    [Column("ص")]
    public int? ص { get; set; }

    /// <summary>Source column "م" (kept as written; not interpreted).</summary>
    [Column("م")]
    public int? م { get; set; }

    /// <summary>Source column "بريك" (kept as written; not interpreted).</summary>
    [Column("بريك")]
    public int? بريك { get; set; }

    public RosterRole? Role { get; set; }

    /// <summary>The existing / created staff member this row belongs to; null while the identity is unresolved.</summary>
    public int? StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }

    /// <summary>Why the row is linked to <see cref="StaffMember"/> (or why it is not).</summary>
    public string? MatchNote { get; set; }

    public DateTime CreatedAt { get; set; }

    public ICollection<TeachingRosterDepartment> Departments { get; set; } = new List<TeachingRosterDepartment>();
    public ICollection<TeachingRosterReviewItem> ReviewItems { get; set; } = new List<TeachingRosterReviewItem>();
}

/// <summary>An existing academic department (and subcategory) a roster row's subject maps to.</summary>
public class TeachingRosterDepartment
{
    public int TeachingRosterEntryId { get; set; }
    public TeachingRosterEntry Entry { get; set; } = null!;
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public int? DepartmentSubcategoryId { get; set; }
    public DepartmentSubcategory? Subcategory { get; set; }

    /// <summary>The source subject part this link comes from ("Arabic", "Rel", "F").</summary>
    public string SourcePart { get; set; } = "";
}

/// <summary>Why a roster row needs a person's decision. Numbers are stored.</summary>
public enum RosterReviewKind
{
    /// <summary>A handwritten correction on the printed source (the printed value is kept on the row).</summary>
    HandwrittenCorrection = 1,

    /// <summary>The identity of the person cannot be confirmed from the source.</summary>
    IdentityUnresolved = 2,

    /// <summary>The same name appears on several roster rows and no existing record identifies the person.</summary>
    DuplicateName = 3,

    /// <summary>The name also matches an existing staff member in another department (kept as a separate record).</summary>
    NameMatchesOtherDepartment = 4,

    /// <summary>A part of the subject could not be mapped to an existing department.</summary>
    UnmappedSubject = 5,

    /// <summary>A source value that is kept as written but may not be what the source means.</summary>
    SourceValueUnclear = 6,

    /// <summary>A homeroom role could not be recorded as a class teaching-team assignment.</summary>
    HomeroomNotAssigned = 7,
}

public class TeachingRosterReviewItem
{
    public const int ValueMaxLength = 200;
    public const int NoteMaxLength = 500;

    public int Id { get; set; }
    public string SourceKey { get; set; } = "";
    public int TeachingRosterEntryId { get; set; }
    public TeachingRosterEntry Entry { get; set; } = null!;
    public RosterReviewKind Kind { get; set; }

    /// <summary>The source field concerned ("Name", "Grade Levels", "Number of Classes", "Subject").</summary>
    public string? Field { get; set; }

    /// <summary>The value as printed / written in the source.</summary>
    public string? SourceValue { get; set; }

    /// <summary>What appears to be written over it (unconfirmed).</summary>
    public string? ObservedValue { get; set; }
    public string Note { get; set; } = "";
    public bool IsResolved { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>When the item was resolved, and why (the source answer or the person's decision that settled it).</summary>
    public DateTime? ResolvedAt { get; set; }
    public const int ResolutionMaxLength = 300;
    public string? Resolution { get; set; }
}
