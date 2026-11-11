namespace SchoolManagement.Web.Entities;

// ============================================================ Academic departments: HOD

/// <summary>
/// One Head of Department appointment. At most one row per department has IsCurrent = true (filtered
/// unique index); a change of HOD ends the current row and adds a new one, so the history is kept.
/// </summary>
public class DepartmentHeadAssignment
{
    public const int NoteMaxLength = 300;

    public int Id { get; set; }
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;

    /// <summary>Always an academic staff member (Teacher) who belongs to the department.</summary>
    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;

    public bool IsCurrent { get; set; } = true;

    /// <summary>The day the appointment takes effect (chosen by the user).</summary>
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public DateTime AssignedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public string? Note { get; set; }
    public string? AssignedBy { get; set; }
}

/// <summary>
/// A department-specific subcategory stored in the database (Religion → Islamic / Christian,
/// Second Language → French / Deutsch / any language added later, ICT → ICT / AI, WSD → its four
/// subdepartments). A row with a parent is a subject under that subcategory (Islamic → Quran).
/// Staff records keep the chosen names; renaming here updates them.
/// </summary>
public class DepartmentSubcategory : IAuditable
{
    public const int NameMaxLength = 50;

    public int Id { get; set; }
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public int? ParentId { get; set; }
    public DepartmentSubcategory? Parent { get; set; }
    public ICollection<DepartmentSubcategory> Children { get; set; } = new List<DepartmentSubcategory>();
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// ============================================================ Management Team

/// <summary>
/// Who held which Management Team position, and when. One current row per position and per member
/// (filtered unique indexes); a replacement ends the current row, so the history is kept.
/// </summary>
public class ManagementPositionAssignment
{
    public const int ReasonMaxLength = 200;

    public int Id { get; set; }
    public int ManagementMemberId { get; set; }
    public ManagementMember Member { get; set; } = null!;
    public ManagementPosition Position { get; set; }
    public bool IsCurrent { get; set; } = true;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime AssignedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public string? AssignedBy { get; set; }
}

/// <summary>The seven Management Team positions. Numbers are stored: add new values, never reuse one.</summary>
public enum ManagementPosition
{
    SchoolDirector = 1,
    Sqam = 2,
    KgStageCoordinator = 3,
    LowerElementaryCoordinator = 4,
    UpperElementaryCoordinator = 5,
    MiddleSchoolCoordinator = 6,
    HighSchoolCoordinator = 7,
}

/// <summary>
/// A Management Team member: its own kind of staff record (StaffType = "Management"), never part of
/// an academic department (a check constraint keeps DepartmentId empty). One holder per position.
/// </summary>
public class ManagementMember : StaffMember
{
    public const int EmailMaxLength = 150;

    public ManagementPosition? Position { get; set; }
    public string? Email { get; set; }
    public DateOnly? AppointedOn { get; set; }
}

// ============================================================ Management Team tasks

public enum ManagementTaskStatus
{
    ToDo = 1,
    InProgress = 2,
    Completed = 3,
    OnHold = 4,
}

public enum TaskPriority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4,
}

/// <summary>A task assigned to one Management Team member. Separate from teacher workload.</summary>
public class ManagementTask : IAuditable
{
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 2000;

    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Notes { get; set; }

    public int ManagementMemberId { get; set; }
    public ManagementMember Member { get; set; } = null!;

    public ManagementTaskStatus Status { get; set; } = ManagementTaskStatus.ToDo;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public DateOnly? DueDate { get; set; }

    /// <summary>Optional time on the due date (only with a due date).</summary>
    public TimeOnly? DueTime { get; set; }

    public DateTime? CompletedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum TaskHistoryAction
{
    Created = 1,
    Updated = 2,
    StatusChanged = 3,
    Reassigned = 4,
    Deleted = 5,
}

/// <summary>
/// Append-only log of task changes, kept per member. No foreign key to the task, so the entry
/// (with its title snapshot) survives when the task is deleted.
/// </summary>
public class ManagementTaskHistory
{
    public const int SummaryMaxLength = 500;

    public long Id { get; set; }
    public int TaskId { get; set; }
    public int ManagementMemberId { get; set; }
    public ManagementMember Member { get; set; } = null!;
    public string TaskTitle { get; set; } = "";
    public TaskHistoryAction Action { get; set; }
    public ManagementTaskStatus StatusAfter { get; set; }
    public string Summary { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
}
