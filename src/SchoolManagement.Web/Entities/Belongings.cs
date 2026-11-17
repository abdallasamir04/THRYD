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

    /// <summary>
    /// The quarter as printed in the School Calendar (e.g. 1 Nov – 9 Jan, holidays included). Only groups the
    /// calendar's months; the 8 school weeks above stay the dates Belongings, attendance and timetables use.
    /// </summary>
    public DateOnly? CalendarStartDate { get; set; }
    public DateOnly? CalendarEndDate { get; set; }
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
    /// <summary>Who first recorded the day (null for records made before it was kept).</summary>
    public string? RecordedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<AcademicMaterialRecord> Materials { get; set; } = new List<AcademicMaterialRecord>();
}

/// <summary>
/// Book and Copy Book of one student in one lesson (independent statuses; null = not checked yet).
/// Lesson-based rows (<see cref="TimetableAssignmentId"/> set) are one per student, date and lesson, and keep a
/// snapshot of the lesson as it was (period, times, teacher), so later timetable edits never rewrite history.
/// Rows recorded before lesson-based checks have no lesson: one per student, date and subject.
/// </summary>
public class AcademicMaterialRecord
{
    public const int NotesMaxLength = 300;
    public const int PeriodMaxLength = 50;
    public const int TeacherMaxLength = 150;

    public int Id { get; set; }
    public int BelongingsRecordId { get; set; }
    public BelongingsRecord Record { get; set; } = null!;
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string Subject { get; set; } = "";
    public ItemStatus? Book { get; set; }
    public ItemStatus? CopyBook { get; set; }

    /// <summary>The timetable lesson the check belongs to (null for day-based rows recorded before lesson-based checks).</summary>
    public int? TimetableAssignmentId { get; set; }
    public TimetableAssignment? Session { get; set; }

    // Lesson snapshot, taken when the first check of the lesson is saved.
    public int? PeriodNumber { get; set; }
    public string? PeriodName { get; set; }
    public TimeOnly? LessonStart { get; set; }
    public TimeOnly? LessonEnd { get; set; }
    /// <summary>The teacher of the lesson on that date (the substitute when one covered it). A snapshot, not a foreign key.</summary>
    public int? TeacherId { get; set; }
    public string? TeacherName { get; set; }

    public string? Notes { get; set; }
    public string? RecordedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One correction of a saved belongings value (a status changed from one value to another, or notes edited):
/// the value before and after, when and by whom. First recordings are not corrections.
/// </summary>
public class BelongingsChange
{
    public const int ItemMaxLength = 40;
    public const int ContextMaxLength = 200;
    public const int ValueMaxLength = 300;

    public long Id { get; set; }
    public int BelongingsRecordId { get; set; }
    public BelongingsRecord Record { get; set; } = null!;
    /// <summary>The academic row changed (not a foreign key, so the history outlives the row).</summary>
    public int? AcademicMaterialRecordId { get; set; }
    /// <summary>"Jacket", "Book", "Copy Book", "Notes"…</summary>
    public string Item { get; set; } = "";
    /// <summary>For academic rows: the lesson, e.g. "English · Period 2".</summary>
    public string? Context { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
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
