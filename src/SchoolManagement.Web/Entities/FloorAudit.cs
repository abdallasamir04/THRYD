namespace SchoolManagement.Web.Entities;

// ============================================================ Floor Audit & Student Movement
//
// The school's physical floors, the areas on them (a classroom is an existing SchoolClass; a facility is managed here),
// the inspections of those areas in the three rounds of every timetable session, and students leaving class and coming
// back. Nothing of the timetable is copied: a session is a period (TimeSlot) with lessons on a date, read from the
// timetable when needed. Each inspection keeps a snapshot of what it was about (floor, area name, session time,
// scheduled time and the checklist questions), so later configuration changes never rewrite history.

/// <summary>A physical floor of the school building (Ground Floor, 1st Floor…). Floors are deactivated, never deleted.</summary>
public class SchoolFloor : IAuditable
{
    public const int NameMaxLength = 50;

    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Matching key (upper case, no separators). Unique.</summary>
    public string NormalizedName { get; set; } = "";

    /// <summary>Display order (0 = ground floor, 1, 2…).</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Floor Management: the grades the floor's management team is responsible for, as written ("PreK – G1B"). A floor with a
    /// grade range is shown on the Floor Management dashboard. Independent of which classrooms are on the floor (inspections).
    /// </summary>
    public string? GradeRange { get; set; }
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<FloorArea> Areas { get; set; } = new List<FloorArea>();
}

/// <summary>Numbers are stored: add new values, never reuse one.</summary>
public enum FloorAreaKind
{
    Classroom = 1,
    Facility = 2,
}

/// <summary>What kind of facility an area is. Numbers are stored: add new values, never reuse one.</summary>
public enum FacilityType
{
    Bathroom = 1,
    Corridor = 2,
    Stairs = 3,
    Reception = 4,
    Library = 5,
    NurseRoom = 6,
    Administration = 7,
    Laboratory = 8,
    Playground = 9,
    Canteen = 10,
    Office = 11,
    PrayerRoom = 12,
    Other = 99,
}

/// <summary>
/// An area of a floor: a classroom (one existing <see cref="SchoolClass"/>, at most one area per class) or a facility
/// (named here). Moving an area to another floor changes only <see cref="FloorId"/>: inspections keep the floor they
/// were made on. Areas are deactivated, never deleted, so their history stays.
/// </summary>
public class FloorArea : IAuditable
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 300;

    public int Id { get; set; }
    public int FloorId { get; set; }
    public SchoolFloor Floor { get; set; } = null!;
    public FloorAreaKind Kind { get; set; }

    /// <summary>Classrooms only: the class taught in this room (its name is the area's name).</summary>
    public int? SchoolClassId { get; set; }
    public SchoolClass? SchoolClass { get; set; }

    /// <summary>Facilities only.</summary>
    public string? Name { get; set; }
    public string? NormalizedName { get; set; }
    public FacilityType? FacilityType { get; set; }
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Which areas a checklist item is asked for. Numbers are stored.</summary>
public enum AuditItemScope
{
    Classroom = 1,
    Facility = 2,
    Both = 3,
}

/// <summary>
/// How a checklist item is answered. The negative answer of each choice type is recorded as an issue (Fail, No,
/// Needs Attention, Absent); anything written in an <see cref="IssueText"/> item is an issue; numbers and observations
/// never are. Numbers are stored: add new values, never reuse one.
/// </summary>
public enum AuditResponseType
{
    PassFail = 1,
    YesNo = 2,
    GoodNeedsAttention = 3,
    PresentAbsent = 4,
    Number = 5,
    Text = 6,
    IssueText = 7,
}

/// <summary>
/// A configurable inspection checklist item. Items are deactivated, never deleted; editing one changes only
/// inspections started afterwards (each inspection keeps the question it was asked).
/// </summary>
public class FloorAuditItem : IAuditable
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 300;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
    public AuditItemScope AppliesTo { get; set; }
    public AuditResponseType ResponseType { get; set; }
    public bool IsRequired { get; set; } = true;
    public bool IsActive { get; set; } = true;

    /// <summary>One of the items the module started with (shown as "Default"; still editable).</summary>
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Numbers are stored.</summary>
public enum FloorInspectionStatus
{
    /// <summary>Started and saved, not finished: can be continued.</summary>
    InProgress = 1,
    Completed = 2,
}

/// <summary>
/// One area inspected in one round of one session on one date (unique per area, date, period and round). The floor,
/// area name, session label and times are the values at the time of the inspection and are never recalculated.
/// </summary>
public class FloorAreaInspection : IAuditable
{
    public const int AreaNameMaxLength = 150;
    public const int SessionLabelMaxLength = 100;
    public const int ObservationsMaxLength = 2000;
    public const int IssueSummaryMaxLength = 2000;
    public const int PersonMaxLength = 150;

    public int Id { get; set; }
    public int FloorAreaId { get; set; }
    public FloorArea Area { get; set; } = null!;

    /// <summary>The area's floor when it was inspected (kept if the area moves later).</summary>
    public int FloorId { get; set; }
    public SchoolFloor Floor { get; set; } = null!;
    public string AreaName { get; set; } = "";
    public FloorAreaKind AreaKind { get; set; }
    public int? SchoolClassId { get; set; }
    public int? AcademicYearId { get; set; }
    public AcademicYear? AcademicYear { get; set; }

    /// <summary>The school date.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The timetable period (session) the round belongs to.</summary>
    public int TimeSlotId { get; set; }
    public TimeSlot TimeSlot { get; set; } = null!;
    public string SessionLabel { get; set; } = "";

    /// <summary>The session's start time when the inspection was made (school time).</summary>
    public TimeOnly SessionStart { get; set; }

    /// <summary>1, 2 or 3.</summary>
    public int Round { get; set; }

    /// <summary>When the round was scheduled (session start + 6 / 25 / 40 minutes), school time.</summary>
    public TimeOnly ScheduledTime { get; set; }

    public FloorInspectionStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? InspectorStaffId { get; set; }
    public StaffMember? Inspector { get; set; }
    public string? InspectorName { get; set; }
    public string? CompletedBy { get; set; }
    public string? Observations { get; set; }
    public int IssueCount { get; set; }

    /// <summary>The items answered with an issue, as recorded ("Cleanliness: Needs Attention; …").</summary>
    public string? IssueSummary { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<FloorInspectionResponse> Responses { get; set; } = new List<FloorInspectionResponse>();
    public ICollection<FloorInspectionAttachment> Attachments { get; set; } = new List<FloorInspectionAttachment>();
}

/// <summary>One checklist answer, with the question exactly as it was asked (name, description, type, required).</summary>
public class FloorInspectionResponse
{
    public const int ValueMaxLength = 1000;

    public int Id { get; set; }
    public int InspectionId { get; set; }
    public FloorAreaInspection Inspection { get; set; } = null!;

    /// <summary>The item the question came from (kept for reports; the item itself may have changed since).</summary>
    public int? ItemId { get; set; }
    public FloorAuditItem? Item { get; set; }
    public string ItemName { get; set; } = "";
    public string? ItemDescription { get; set; }
    public AuditResponseType ResponseType { get; set; }
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
    public string? Value { get; set; }
    public bool IsIssue { get; set; }
}

/// <summary>A photo or document attached to an inspection (stored with the task attachment file store).</summary>
public class FloorInspectionAttachment
{
    public const int FileNameMaxLength = 200;

    public int Id { get; set; }
    public int InspectionId { get; set; }
    public FloorAreaInspection Inspection { get; set; } = null!;

    /// <summary>32 hex characters: the file's name in the store.</summary>
    public string StoredName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? UploadedBy { get; set; }
}

/// <summary>
/// A student leaving class and coming back. Open while <see cref="ReturnAt"/> is null (never filled in automatically);
/// a student has at most one open movement (unique filtered index). A movement recorded by mistake is voided with a
/// reason, never deleted. Attendance is not touched: a student outside the classroom is not absent from school.
/// </summary>
public class StudentMovement : IAuditable
{
    public const int DestinationMaxLength = 150;
    public const int NotesMaxLength = 500;
    public const int PersonMaxLength = 150;

    public int Id { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;

    /// <summary>The school date of the exit.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The student's class when they left (kept if the student changes class later).</summary>
    public int FromClassId { get; set; }
    public SchoolClass FromClass { get; set; } = null!;

    /// <summary>The classroom area they left from, and its floor at that time (when the class has a room).</summary>
    public int? FromAreaId { get; set; }
    public FloorArea? FromArea { get; set; }
    public int? FromFloorId { get; set; }
    public SchoolFloor? FromFloor { get; set; }

    /// <summary>The destination area (a facility or another classroom); null for a custom destination.</summary>
    public int? ToAreaId { get; set; }
    public FloorArea? ToArea { get; set; }
    public int? ToFloorId { get; set; }
    public SchoolFloor? ToFloor { get; set; }

    /// <summary>The destination as it was named when recorded (the area's name, or the custom destination).</summary>
    public string ToName { get; set; } = "";
    public bool IsCustomDestination { get; set; }

    /// <summary>When the student left (UTC).</summary>
    public DateTime ExitAt { get; set; }

    /// <summary>When the student came back (UTC); null while they are outside.</summary>
    public DateTime? ReturnAt { get; set; }
    public string? Notes { get; set; }
    public int? RecordedByStaffId { get; set; }
    public string? RecordedBy { get; set; }
    public int? ReturnedByStaffId { get; set; }
    public string? ReturnedBy { get; set; }

    public DateTime? VoidedAt { get; set; }
    public string? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Numbers are stored.</summary>
public enum FloorAuditRole
{
    /// <summary>Manages floors, areas, checklist items and access; sees and corrects everything.</summary>
    Manager = 1,

    /// <summary>Inspects the areas of a floor (or of every floor) and, when allowed, records student movements.</summary>
    Inspector = 2,
}

/// <summary>
/// Who may do what in Floor Audit, on top of the Management Team (whose current members are managers). Ended, never
/// deleted, so it is known who had access when.
/// </summary>
public class FloorAuditAccess
{
    public int Id { get; set; }
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public FloorAuditRole Role { get; set; }

    /// <summary>Inspectors: the floor they inspect; null = every floor.</summary>
    public int? FloorId { get; set; }
    public SchoolFloor? Floor { get; set; }

    /// <summary>Inspectors: may record student movements and mark students returned.</summary>
    public bool CanRecordMovements { get; set; } = true;
    public DateTime GrantedAt { get; set; }
    public string? GrantedBy { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndedBy { get; set; }
}

/// <summary>
/// Floor Audit's audit trail: who created, changed, completed, recorded or corrected what, and when. Never edited.
/// </summary>
public class FloorAuditLogEntry
{
    public const int EntityMaxLength = 40;
    public const int ActionMaxLength = 40;
    public const int TextMaxLength = 2000;

    public int Id { get; set; }

    /// <summary>"Floor", "Area", "Item", "Inspection", "Movement", "Access", "Settings".</summary>
    public string Entity { get; set; } = "";
    public int? EntityId { get; set; }
    public int? FloorId { get; set; }
    public string Action { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
    public int? ByStaffId { get; set; }
    public string? By { get; set; }
}
