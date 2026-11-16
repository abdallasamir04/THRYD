namespace SchoolManagement.Web.Entities;

/// <summary>
/// A school activity created by an administrator (Duties → Activities): Art Club, Music, School Events… Each one has
/// its own card and management page. P.E. is NOT stored here: it is the existing P.E. module (/pe, built on the P.E.
/// department's timetable sessions) and only appears on the Activities page as a built-in card, so none of its data
/// is copied or duplicated.
/// </summary>
public class SchoolActivity : IAuditable
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 300;
    public const int CategoryMaxLength = 60;
    public const int LocationMaxLength = 120;
    public const int IconMaxLength = 40;
    public const int ColorMaxLength = 20;

    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Unique, case- and spacing-insensitive (TextNormalizer.NameKey).</summary>
    public string NormalizedName { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Category { get; set; }

    /// <summary>A name from the app's icon set (allowed values in ActivityStyles).</summary>
    public string Icon { get; set; } = "sparkles";

    /// <summary>A THRYD tone (violet, blue, teal, green, amber, rose, indigo, cyan).</summary>
    public string Color { get; set; } = "violet";

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public string? Location { get; set; }

    /// <summary>Inactive activities keep all their records; they are listed separately on the Activities page.</summary>
    public bool IsActive { get; set; } = true;

    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<SchoolActivityStaff> Staff { get; set; } = new List<SchoolActivityStaff>();
    public ICollection<SchoolActivityClass> Classes { get; set; } = new List<SchoolActivityClass>();
    public ICollection<SchoolActivitySchedule> Schedule { get; set; } = new List<SchoolActivitySchedule>();
}

/// <summary>A staff member assigned to an activity.</summary>
public class SchoolActivityStaff
{
    public int SchoolActivityId { get; set; }
    public SchoolActivity Activity { get; set; } = null!;
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
}

/// <summary>A class taking part in an activity.</summary>
public class SchoolActivityClass
{
    public int SchoolActivityId { get; set; }
    public SchoolActivity Activity { get; set; } = null!;
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;
}

/// <summary>A weekly meeting of an activity: a school day with school-local start and end times.</summary>
public class SchoolActivitySchedule
{
    public const int NotesMaxLength = 200;

    public int Id { get; set; }
    public int SchoolActivityId { get; set; }
    public SchoolActivity Activity { get; set; } = null!;
    public DayOfWeek Day { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    /// <summary>Overrides the activity's location for this meeting.</summary>
    public string? Location { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Which kind of scheduled activity session a coverage record is about. Numbers are stored: never reuse one.</summary>
public enum ActivitySessionKind
{
    /// <summary>A P.E. lesson: a timetable session of the P.E. department on a date.</summary>
    PeLesson = 1,

    /// <summary>A weekly meeting of an administrator-created activity on a date.</summary>
    ActivityMeeting = 2,
}

/// <summary>Coverage of one activity session on one date. Numbers are stored: never reuse one.</summary>
public enum ActivitySessionStatus
{
    /// <summary>Scheduled; coverage not confirmed yet (also what a session without a record means).</summary>
    Pending = 1,
    Covered = 2,
    NotCovered = 3,
}

/// <summary>
/// The coverage status of one scheduled activity session on one date (Dashboard → Duties covered today → Activities).
/// Exactly one link is set: <see cref="TimetableAssignmentId"/> for a P.E. lesson, <see cref="ActivityScheduleId"/> for
/// an activity meeting. A scheduled session without a record is Pending. Nothing of the timetable or schedule is copied.
/// </summary>
public class ActivitySessionCoverage : IAuditable
{
    public int Id { get; set; }
    public ActivitySessionKind Kind { get; set; }

    public int? TimetableAssignmentId { get; set; }
    public TimetableAssignment? Session { get; set; }

    public int? ActivityScheduleId { get; set; }
    public SchoolActivitySchedule? Meeting { get; set; }

    /// <summary>The school-local calendar date of the session.</summary>
    public DateOnly SessionDate { get; set; }
    public ActivitySessionStatus Status { get; set; } = ActivitySessionStatus.Pending;

    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>What happened to an activity, when and by whom (created, edited, staff / schedule changes, status).</summary>
public class SchoolActivityHistory
{
    public const int TextMaxLength = 500;

    public int Id { get; set; }
    public int SchoolActivityId { get; set; }
    public SchoolActivity Activity { get; set; } = null!;
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public DateTime At { get; set; }
    public string? By { get; set; }
}
