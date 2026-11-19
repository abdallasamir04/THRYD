namespace SchoolManagement.Web.Entities;

/// <summary>
/// A period of the school day (the table keeps its original name, TimeSlots). The administrator defines
/// as many periods as needed — nothing about their number, names or times is fixed in code:
/// <list type="bullet">
/// <item><b>School-wide</b> (<see cref="SchoolClassId"/> null): part of every class's timetable.</item>
/// <item><b>Class-specific</b> (<see cref="SchoolClassId"/> set): only in that class's timetable. When it
/// <see cref="ReplacesTimeSlotId">replaces</see> a school-wide period, that class uses this one instead
/// (e.g. different times for one class). An archived replacement removes the school period from that class.</item>
/// </list>
/// <see cref="DayMask"/> chooses the weekdays (0 = every configured school day). Deleted periods that were
/// ever used are archived, not removed, so historical lessons and attendance keep their period and times.
/// The ten periods created by the first migration are school-wide, every day, with no times until the school sets them.
/// </summary>
public class TimeSlot
{
    /// <summary>Periods created by the original migration (ids 1–10). Only a default: more can be added.</summary>
    public const int SeededCount = 10;
    public const int NameMaxLength = 50;
    public const int MaxOrder = 999;

    public int Id { get; set; }

    /// <summary>Display order (1, 2, 3…). Within one class and day, the order follows the start times.</summary>
    public int Number { get; set; }
    public string? Name { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    /// <summary>Disabled periods stay in the timetable structure (shown as disabled) but take no lessons.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Null: school-wide period. Set: this class only.</summary>
    public int? SchoolClassId { get; set; }
    public SchoolClass? SchoolClass { get; set; }

    /// <summary>For a class-specific period: the school-wide period it replaces for that class.</summary>
    public int? ReplacesTimeSlotId { get; set; }
    public TimeSlot? Replaces { get; set; }

    /// <summary>Weekdays the period runs: bit (1 &lt;&lt; (int)DayOfWeek); 0 = every school day.</summary>
    public int DayMask { get; set; }

    /// <summary>Set when the period was deleted after being used (kept for history, never shown as current).</summary>
    public DateTime? ArchivedAt { get; set; }

    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A weekly teaching session: one class, one day, one slot, one subject, one teacher. The class
/// timetable and the teacher timetable both read these rows (there is no second copy).
/// A session counts as one Teaching Session in its teacher's workload for every week from
/// <see cref="StartsOn"/> until <see cref="EndedOn"/> (exclusive). Removing or changing the
/// teacher ends the row instead of deleting it, so earlier weeks keep their history.
/// </summary>
public class TimetableAssignment : IAuditable
{
    public const int SubjectMaxLength = 100;
    public const int NotesMaxLength = 300;

    public int Id { get; set; }
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    /// <summary>Sunday (0) to Thursday (4).</summary>
    public DayOfWeek Day { get; set; }

    public int TimeSlotId { get; set; }
    public TimeSlot TimeSlot { get; set; } = null!;

    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string Subject { get; set; } = "";

    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;
    public string? Notes { get; set; }

    /// <summary>First school week (Sunday) the session is taught.</summary>
    public DateOnly StartsOn { get; set; }

    /// <summary>First school week it is no longer taught; null while current.</summary>
    public DateOnly? EndedOn { get; set; }
    public string? EndReason { get; set; }

    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One confirmed timetable upload (Timetables → Upload Timetable): which class and academic year it was for, the file,
/// who saved it and what it created. The timetable itself is stored as ordinary periods (<see cref="TimeSlot"/>) and
/// lessons (<see cref="TimetableAssignment"/>) — this row is the record of the import, and keeps what could not become a
/// lesson yet (a lesson whose teacher was left unassigned) in <see cref="Details"/> so it can be assigned later.
/// </summary>
public class TimetableImport
{
    public const int FileNameMaxLength = 260;
    public const int SheetMaxLength = 100;

    public int Id { get; set; }
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;
    public int? AcademicYearId { get; set; }
    public AcademicYear? AcademicYear { get; set; }

    public string FileName { get; set; } = "";
    public string? SheetName { get; set; }

    public int Days { get; set; }
    public int Periods { get; set; }

    /// <summary>Cells with a subject (saved lessons plus lessons waiting for a teacher).</summary>
    public int SubjectAssignments { get; set; }

    /// <summary>Lessons saved with their teacher (each one a weekly teaching session).</summary>
    public int TeacherAssignments { get; set; }

    /// <summary>Lessons whose teacher was not matched or was left unassigned (listed in <see cref="Details"/>).</summary>
    public int UnassignedTeachers { get; set; }
    public int Breaks { get; set; }
    public int Warnings { get; set; }

    /// <summary>The class's lessons that this upload replaced (ended from the import week; kept as history).</summary>
    public int ReplacedLessons { get; set; }

    /// <summary>JSON: the lessons waiting for a teacher and the warnings shown when it was saved.</summary>
    public string? Details { get; set; }

    public string? ImportedBy { get; set; }
    public DateTime ImportedAt { get; set; }
}
