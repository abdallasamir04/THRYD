namespace SchoolManagement.Web.Entities;

// ============================================================ Floor Management
//
// Each floor (SchoolFloor, shared with the floor inspections) has a management team: Stage Coordinator → Floor Supervisor →
// Matrons and a Floating Matron, plus Floor Social Workers. People are their existing staff records; a role is an
// assignment that ends (kept as history), never a copy of the person. Reports and escalations follow the team's reporting
// lines; discipline / behavioural tickets are handled by the floor's Social Worker.

/// <summary>A role on a floor's management team. Numbers are stored: add new values, never reuse one.</summary>
public enum FloorRole
{
    StageCoordinator = 1,
    FloorSupervisor = 2,
    Matron = 3,
    FloatingMatron = 4,
    SocialWorker = 5,
}

/// <summary>A staff member's role on a floor. Ending it keeps the row (IsCurrent = false, EndDate) as history.</summary>
public class FloorStaffAssignment : IAuditable
{
    public const int CoverageMaxLength = 60;
    public const int NoteMaxLength = 300;

    public int Id { get; set; }
    public string? SourceKey { get; set; }
    public int FloorId { get; set; }
    public SchoolFloor Floor { get; set; } = null!;
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public FloorRole Role { get; set; }

    /// <summary>The grade range the person covers, as written ("PreK – G1B"); Matrons list their classes in <see cref="Classes"/>.</summary>
    public string? Coverage { get; set; }
    public bool IsCurrent { get; set; } = true;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? EndReason { get; set; }
    public string? Note { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<FloorStaffClass> Classes { get; set; } = new List<FloorStaffClass>();
}

/// <summary>A class a Matron is assigned to (existing classes only).</summary>
public class FloorStaffClass
{
    public int FloorStaffAssignmentId { get; set; }
    public FloorStaffAssignment Assignment { get; set; } = null!;
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;
}

// ------------------------------------------------------------ reports and escalations

public enum FloorReportCategory
{
    DailyOperations = 1,
    StudentSafety = 2,
    StaffConcern = 3,
    Facilities = 4,
    Other = 5,
}

/// <summary>Whom a report is addressed to (resolved to the current holder when it is submitted or escalated).</summary>
public enum FloorReportRecipient
{
    FloorSupervisor = 1,
    StageCoordinator = 2,
    SchoolConsultant = 3,
}

/// <summary>A report is never resolved automatically: only its reviewer changes the status.</summary>
public enum FloorReportStatus
{
    Submitted = 1,
    UnderReview = 2,
    Escalated = 3,
    Resolved = 4,
    Closed = 5,
}

public class FloorReport : IAuditable
{
    public const int TitleMaxLength = 150;
    public const int TextMaxLength = 2000;

    public int Id { get; set; }
    public int FloorId { get; set; }
    public SchoolFloor Floor { get; set; } = null!;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public FloorReportCategory Category { get; set; }

    /// <summary>The employee the report concerns, when there is one (never its recipient).</summary>
    public int? RelatedStaffId { get; set; }
    public StaffMember? RelatedStaff { get; set; }

    /// <summary>When the reported event happened.</summary>
    public DateTime OccurredAt { get; set; }
    public FloorReportRecipient Recipient { get; set; }

    /// <summary>The person the report is with now (null: the role has no current holder).</summary>
    public int? RecipientStaffId { get; set; }
    public StaffMember? RecipientStaff { get; set; }
    public FloorReportStatus Status { get; set; } = FloorReportStatus.Submitted;
    public int? CreatedByStaffId { get; set; }
    public StaffMember? CreatedByStaff { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<FloorReportEvent> Events { get; set; } = new List<FloorReportEvent>();
}

public enum FloorCaseAction
{
    Created = 1,
    StatusChanged = 2,
    Escalated = 3,
    Assigned = 4,
    Note = 5,
    Forwarded = 6,
}

/// <summary>The audit trail of a report: who created, reviewed, escalated and updated it. Never edited.</summary>
public class FloorReportEvent
{
    public const int NoteMaxLength = 1000;

    public long Id { get; set; }
    public int FloorReportId { get; set; }
    public FloorReport Report { get; set; } = null!;
    public FloorCaseAction Action { get; set; }
    public FloorReportStatus StatusAfter { get; set; }
    public FloorReportRecipient? RecipientAfter { get; set; }
    public int? RecipientStaffIdAfter { get; set; }
    public string? Note { get; set; }
    public int? ByStaffId { get; set; }
    public string? By { get; set; }
    public DateTime At { get; set; }
}

// ------------------------------------------------------------ Social Worker tickets

public enum FloorTicketCategory
{
    Discipline = 1,
    BehavioralConcern = 2,
    StudentSupport = 3,
    Other = 4,
}

public enum FloorTicketStatus
{
    Open = 1,
    Assigned = 2,
    InProgress = 3,
    Escalated = 4,
    Resolved = 5,
    Closed = 6,
}

/// <summary>A discipline / behavioural case for the floor's Social Worker, about an existing student.</summary>
public class FloorTicket : IAuditable
{
    public const int TextMaxLength = 2000;

    public int Id { get; set; }

    /// <summary>"FT-00012" (from the id, set when saved).</summary>
    public string Number { get; set; } = "";
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public int FloorId { get; set; }
    public SchoolFloor Floor { get; set; } = null!;

    /// <summary>The student's class when the ticket was opened (kept if the student moves later).</summary>
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;
    public FloorTicketCategory Category { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public FloorTicketStatus Status { get; set; } = FloorTicketStatus.Open;
    public string Description { get; set; } = "";
    public int? AssignedSocialWorkerId { get; set; }
    public StaffMember? AssignedSocialWorker { get; set; }
    public string? ResolutionNotes { get; set; }
    public int? CreatedByStaffId { get; set; }
    public StaffMember? CreatedByStaff { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<FloorTicketEvent> Events { get; set; } = new List<FloorTicketEvent>();
}

public class FloorTicketEvent
{
    public long Id { get; set; }
    public int FloorTicketId { get; set; }
    public FloorTicket Ticket { get; set; } = null!;
    public FloorCaseAction Action { get; set; }
    public FloorTicketStatus StatusAfter { get; set; }
    public int? AssignedAfterId { get; set; }
    public string? Note { get; set; }
    public int? ByStaffId { get; set; }
    public string? By { get; set; }
    public DateTime At { get; set; }
}
