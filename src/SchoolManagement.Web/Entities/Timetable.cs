namespace SchoolManagement.Web.Entities;

/// <summary>
/// One of the 10 daily time slots. The same slots apply to every school day (Sunday–Thursday).
/// Times start empty: the school sets them in Time Slots. Active slots may not overlap.
/// </summary>
public class TimeSlot
{
    public const int Count = 10;
    public const int NameMaxLength = 50;

    public int Id { get; set; }

    /// <summary>1–10, unique.</summary>
    public int Number { get; set; }
    public string? Name { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public bool IsActive { get; set; } = true;
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
