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

    /// <summary>One of the seven leadership positions (with its own page and appointment history)…</summary>
    public ManagementPosition? Position { get; set; }
    /// <summary>…or a position added by the school (never both; one holder per position).</summary>
    public int? CustomPositionId { get; set; }
    public ManagementCustomPosition? CustomPosition { get; set; }
    public string? Email { get; set; }
    public DateOnly? AppointedOn { get; set; }
}

/// <summary>
/// A Management Team job title added by the school beside the seven leadership positions (e.g. "Student Affairs
/// Manager"). A position, not a department. Titles are unique ignoring case and spacing; one holder at a time.
/// </summary>
public class ManagementCustomPosition
{
    public const int TitleMaxLength = StaffMember.JobTitleMaxLength;

    public int Id { get; set; }
    public string Title { get; set; } = "";
    /// <summary>TextNormalizer.NameKey(Title): the uniqueness key.</summary>
    public string NormalizedTitle { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

// ============================================================ Management Team tasks

public enum ManagementTaskStatus
{
    ToDo = 1,
    InProgress = 2,
    Completed = 3,
    /// <summary>Kept for tasks put on hold before Cancelled existed; new tasks use the other four.</summary>
    OnHold = 4,
    Cancelled = 5,
}

public enum TaskPriority
{
    Low = 1,
    Medium = 2,
    High = 3,
    /// <summary>Shown as "Critical" (stored value 4, formerly "Urgent").</summary>
    Critical = 4,
}

/// <summary>
/// A Management Team task. The Management Team creates and manages tasks, but any staff member can carry one out
/// (<see cref="AssignedTo"/>) or review it (<see cref="Reviewer"/>). Never part of teacher workload or the timetable.
/// </summary>
public class ManagementTask : IAuditable
{
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 2000;

    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Notes { get; set; }

    /// <summary>The executor: the staff member responsible for doing the task (any staff member).</summary>
    public int AssignedToId { get; set; }
    public StaffMember AssignedTo { get; set; } = null!;

    /// <summary>Optional: the staff member who receives, reviews or approves the finished task.</summary>
    public int? ReviewerId { get; set; }
    public StaffMember? Reviewer { get; set; }

    /// <summary>
    /// Who created and assigned the task: the "Working as" staff member at creation. Set once, never changed by an edit.
    /// Null for tasks created without a "Working as" choice; <see cref="CreatedBy"/> keeps the name shown then.
    /// </summary>
    public int? CreatedById { get; set; }
    public StaffMember? Creator { get; set; }

    public ICollection<TaskAttachment> Attachments { get; set; } = new List<TaskAttachment>();

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
/// Append-only log of task changes, kept per assignee (any staff member). No foreign key to the task, so the entry
/// (with its title snapshot) survives when the task is deleted.
/// </summary>
public class ManagementTaskHistory
{
    public const int SummaryMaxLength = 500;

    public long Id { get; set; }
    public int TaskId { get; set; }
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public string TaskTitle { get; set; } = "";
    public TaskHistoryAction Action { get; set; }
    public ManagementTaskStatus StatusAfter { get; set; }
    public string Summary { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
}

/// <summary>
/// What an attachment is for. Only instruction / reference files exist today; a future "work submitted for review"
/// upload gets its own value so the two are never mixed.
/// </summary>
public enum TaskAttachmentPurpose
{
    Instructions = 1,
}

/// <summary>
/// A file attached to a task. The bytes live in the task file store (outside wwwroot, under a random name); this row
/// keeps the metadata. A row without a task is an upload waiting for its form to be saved (<see cref="StagingKey"/>),
/// removed after a day if the form never is.
/// </summary>
public class TaskAttachment
{
    public const int FileNameMaxLength = 200;

    public int Id { get; set; }
    public int? TaskId { get; set; }
    public ManagementTask? Task { get; set; }
    public TaskAttachmentPurpose Purpose { get; set; } = TaskAttachmentPurpose.Instructions;

    /// <summary>Random key the browser holds between uploading the file and saving the task form.</summary>
    public Guid StagingKey { get; set; }

    /// <summary>The uploaded file's name, cleaned (display and download name only, never a path).</summary>
    public string FileName { get; set; } = "";

    /// <summary>Lower-case extension without the dot (pdf, docx…); decides the content type served.</summary>
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>Name in the file store: a GUID, never derived from the uploaded name.</summary>
    public string StoredName { get; set; } = "";
    public string Sha256 { get; set; } = "";

    public DateTime UploadedAt { get; set; }
    public string? UploadedBy { get; set; }
}
