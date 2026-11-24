namespace SchoolManagement.Web.Entities;

// ============================================================ Duty schedules, sub-duties and coverage reports
//
// Main Duty   = a duty type (Morning, Midday, Dismissal) with a schedule (start, end, days).
// Sub-Duty    = a duty section (Students Arrival Check, In-Class Morning…) with its own schedule, required / optional
//               designation and responsible department. Its code is the DutyComponent value the duty records use.
// Coverage point = a DutyAssignment row of that section on a date (a gate, a building, a class group…) with its staff.
//
// Schedules are versioned by the date they take effect: a change adds (or, on the same date, updates) a version, so a past
// day is always read with the schedule that applied to it. Nothing here is copied into the duty records.

/// <summary>
/// A Main Duty definition. <see cref="Id"/> is the <see cref="DutyType"/> value stored on the duty records, Sub-Duties and
/// schedules: 1–3 are the original Morning, Midday and Dismissal duties (same names and slugs as before); duties added later
/// (Duties → Add Duty) get new numbers, never reused.
/// </summary>
public class DutyMainDuty
{
    public const int NameMaxLength = 100;
    public const int SummaryMaxLength = 300;
    public const int SlugMaxLength = 60;
    public const int IconMaxLength = 40;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Icon { get; set; } = "shield-check";
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// A Sub-Duty definition. <see cref="Id"/> is the <see cref="DutyComponent"/> code stored on the duty records (1–10 are the
/// original sections; sections added later get new numbers, never reused). A removed section is retired from a date and
/// keeps all its records.
/// </summary>
public class DutySection
{
    public const int NameMaxLength = 100;
    public const int SummaryMaxLength = 300;
    public const int SlugMaxLength = 60;
    public const int IconMaxLength = 40;
    public const int GradesMaxLength = 80;

    public int Id { get; set; }
    public DutyType DutyType { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Icon { get; set; } = "shield-check";

    /// <summary>The duty records list the classes they cover (In-Class Morning).</summary>
    public bool HasClasses { get; set; }

    /// <summary>Requirements still to come from the school (shown as a notice).</summary>
    public bool DetailsPending { get; set; }
    public string? Grades { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Not scheduled from this date on (records before it stay).</summary>
    public DateOnly? RetiredOn { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }

    public ICollection<DutySectionSchedule> Schedules { get; set; } = new List<DutySectionSchedule>();
}

/// <summary>
/// One version of a Sub-Duty's schedule, in force from <see cref="EffectiveFrom"/> until the next version: its time range
/// (school-local, Africa/Cairo), the days it runs, whether it is required for its Main Duty, and the responsible department.
/// </summary>
public class DutySectionSchedule
{
    public int Id { get; set; }
    public int DutySectionId { get; set; }
    public DutySection Section { get; set; } = null!;
    public DateOnly EffectiveFrom { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    /// <summary>Weekdays: bit (1 &lt;&lt; (int)DayOfWeek); 0 = every school day.</summary>
    public int DayMask { get; set; }

    /// <summary>Required sub-duties decide the Main Duty's result; optional ones are tracked but never invalidate it.</summary>
    public bool IsRequired { get; set; } = true;

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>One version of a Main Duty's overall time range, in force from <see cref="EffectiveFrom"/>.</summary>
public class DutyMainSchedule
{
    public int Id { get; set; }
    public DutyType DutyType { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    /// <summary>Weekdays: bit (1 &lt;&lt; (int)DayOfWeek); 0 = every school day.</summary>
    public int DayMask { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>Why a responsibility was not covered. Numbers are stored: never reuse one.</summary>
public enum DutyUncoveredReason
{
    StaffAbsent = 1,
    NoReplacement = 2,
    NotCompleted = 3,
    AreaUnattended = 4,
    OperationalIssue = 5,
    Other = 6,
}

/// <summary>
/// A report that a responsibility of a Sub-Duty on a date was not covered: the coverage point (a duty record) or an area
/// typed by the user, the reason and notes, who and when. Append-only: an edit adds a new report that supersedes the old
/// one, a withdrawal adds one marked withdrawn, so every earlier version stays. A report explains a failure; it never
/// counts as coverage.
/// </summary>
public class DutyCoverageReport
{
    public const int AreaMaxLength = 150;
    public const int NotesMaxLength = 1000;

    public int Id { get; set; }
    public DateOnly DutyDate { get; set; }

    /// <summary>The Sub-Duty (DutySection id / DutyComponent code).</summary>
    public DutyComponent Component { get; set; }

    /// <summary>The coverage point, when the report is about one duty record (not cascaded: the report outlives it).</summary>
    public int? DutyAssignmentId { get; set; }

    /// <summary>The uncovered area or responsibility as typed (or the point's location / description).</summary>
    public string Area { get; set; } = "";
    public DutyUncoveredReason Reason { get; set; }
    public string? Notes { get; set; }

    /// <summary>The report this one replaces (an edit or a withdrawal).</summary>
    public int? SupersedesId { get; set; }
    public bool Withdrawn { get; set; }

    public DateTime RecordedAt { get; set; }
    public string? RecordedBy { get; set; }
}
