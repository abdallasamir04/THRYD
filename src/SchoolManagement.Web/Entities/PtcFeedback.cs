namespace SchoolManagement.Web.Entities;

// ============================================================ PTC Phase 7: parent feedback and concerns
//
// A parent gives feedback once per confirmed booking, after their child's class PTC (or the event) is over, from a signed,
// time-limited link (the link itself is never stored). What the parent submitted is never changed afterwards: administrators
// only add review/concern status, assignment, resolution and internal notes, and every such change is in PtcFeedbackHistory.
// Enum numbers are stored: add new values, never reuse one.

/// <summary>A four-point answer. Numbers are stored (and averaged: 4 = Excellent … 1 = Poor).</summary>
public enum PtcRating
{
    Poor = 1,
    Average = 2,
    Good = 3,
    Excellent = 4,
}

/// <summary>Was the meeting helpful?</summary>
public enum PtcHelpful
{
    Yes = 1,
    Partially = 2,
    No = 3,
}

public enum PtcConcernCategory
{
    Scheduling = 1,
    TeacherInteraction = 2,
    AcademicProgress = 3,
    Communication = 4,
    Organization = 5,
    Other = 6,
}

/// <summary>Whether an administrator has read the feedback.</summary>
public enum PtcFeedbackReview
{
    Pending = 1,
    Reviewed = 2,
}

/// <summary>Where a parent's concern stands (only feedback with a concern has one).</summary>
public enum PtcConcernStatus
{
    New = 1,
    UnderReview = 2,
    InProgress = 3,
    Resolved = 4,
    Closed = 5,
}

/// <summary>What an administrator did on a feedback record.</summary>
public enum PtcFeedbackActionKind
{
    Reviewed = 1,
    ConcernStatusChanged = 2,
    NoteAdded = 3,
    Assigned = 4,
    Unassigned = 5,
    Resolved = 6,
    Reopened = 7,
}

/// <summary>One parent's feedback on one booking (one per booking: a unique index).</summary>
public class PtcFeedback
{
    public const int ReferenceLength = 11;
    public const int CommentsMaxLength = 2000;
    public const int ConcernMaxLength = 2000;
    public const int ResolutionMaxLength = 2000;

    public int Id { get; set; }

    /// <summary>Short safe code shown to the parent and administrators ("FB-7KQ2M9XA"); never a database id.</summary>
    public string Reference { get; set; } = "";

    public int PtcEventId { get; set; }
    public PtcEvent PtcEvent { get; set; } = null!;
    public int PtcClassScheduleId { get; set; }
    public PtcClassSchedule ClassSchedule { get; set; } = null!;
    public int PtcParentBookingId { get; set; }
    public PtcParentBooking Booking { get; set; } = null!;

    // ---- what the parent submitted (never changed afterwards)
    public PtcRating? OverallRating { get; set; }
    public PtcRating? CommunicationRating { get; set; }
    public PtcRating? OrganizationRating { get; set; }
    public PtcHelpful? MeetingHelpful { get; set; }
    public string? Comments { get; set; }
    public PtcConcernCategory? ConcernCategory { get; set; }
    public string? ConcernText { get; set; }

    /// <summary>Server time (UTC) of the submission.</summary>
    public DateTime SubmittedAt { get; set; }

    // ---- administration (audited in History)
    public PtcFeedbackReview ReviewStatus { get; set; } = PtcFeedbackReview.Pending;
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }

    /// <summary>Null when the parent raised no concern.</summary>
    public PtcConcernStatus? ConcernStatus { get; set; }
    public int? AssignedStaffMemberId { get; set; }
    public StaffMember? AssignedStaffMember { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime? AssignedAt { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency: two administrators changing the same record at once cannot overwrite each other.</summary>
    public byte[] Version { get; set; } = [];

    public bool HasConcern => ConcernStatus is not null;

    public ICollection<PtcTeacherFeedback> Teachers { get; set; } = new List<PtcTeacherFeedback>();
    public ICollection<PtcFeedbackHistory> History { get; set; } = new List<PtcFeedbackHistory>();
}

/// <summary>Optional feedback about one teacher the parent was scheduled to meet in that booking.</summary>
public class PtcTeacherFeedback
{
    public const int CommentMaxLength = 1000;

    public int Id { get; set; }
    public int PtcFeedbackId { get; set; }
    public PtcFeedback Feedback { get; set; } = null!;
    public int TeacherStaffMemberId { get; set; }
    public StaffMember Teacher { get; set; } = null!;

    public PtcRating? CommunicationRating { get; set; }
    public PtcHelpful? MeetingHelpful { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// Audit of administrative work on a feedback record: reviews, concern status changes, assignments, resolution, reopening
/// and internal notes (a note is an entry of its own). Internal only: never shown to parents. Never edited or deleted.
/// </summary>
public class PtcFeedbackHistory
{
    public const int ValueMaxLength = 200;
    public const int NoteMaxLength = 2000;

    public int Id { get; set; }
    public int PtcFeedbackId { get; set; }
    public PtcFeedback Feedback { get; set; } = null!;

    public PtcFeedbackActionKind Kind { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Note { get; set; }
    public string? By { get; set; }
    public DateTime At { get; set; }
}
