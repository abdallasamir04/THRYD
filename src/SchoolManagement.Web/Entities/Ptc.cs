namespace SchoolManagement.Web.Entities;

// ============================================================ PTC — Parent-Teacher Conference (Phase 1: domain foundation)
//
// One PTC event (an academic year's conference) schedules existing classes on their own dates; each class schedule has
// Rounds (booking windows of Capacity × MeetingDuration + TransitionDuration minutes, 9 × 7 + 7 = 70 by default). A parent
// books a Round for their child directly (no approval) and picks the subjects to discuss; every subject resolves to the
// teacher who actually teaches it to the child's class (the class's current timetable lessons), and becomes one teacher
// interview. Interviews of one booking run in parallel (the same slot of the Round), and a teacher never has two
// overlapping interviews. Substitute teachers are recorded manually by administrators.
//
// Nothing here copies students, parents, staff, classes, departments or academic years: every reference is a foreign key
// to the existing record, and nothing cascades to them (Restrict). Rows are never deleted: cancelled rows keep their status
// and stay as history. Enum numbers are stored: add new values, never reuse one.

/// <summary>Lifecycle of a PTC event.</summary>
public enum PtcEventStatus
{
    Draft = 1,
    Published = 2,
    Completed = 3,
    Cancelled = 4,
}

/// <summary>Lifecycle of one class's PTC schedule.</summary>
public enum PtcScheduleStatus
{
    Scheduled = 1,
    Completed = 2,
    Cancelled = 3,
}

/// <summary>Whether a Round takes bookings.</summary>
public enum PtcRoundStatus
{
    Open = 1,
    Closed = 2,
    Cancelled = 3,
}

/// <summary>A parent's booking. Direct booking: a booking is Confirmed as soon as it is made.</summary>
public enum PtcBookingStatus
{
    Confirmed = 1,
    Cancelled = 2,
}

/// <summary>One subject selected in a booking.</summary>
public enum PtcBookingSubjectStatus
{
    Active = 1,
    Removed = 2,
}

/// <summary>One teacher interview.</summary>
public enum PtcInterviewStatus
{
    Scheduled = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4,
    NoShow = 5,
}

/// <summary>Status of a former manual KG teacher selection (history only: every class now uses its timetable teachers).</summary>
public enum PtcClassTeacherStatus
{
    Active = 1,
    Removed = 2,
}

/// <summary>A manual substitute assignment.</summary>
public enum PtcSubstituteStatus
{
    Active = 1,
    Cancelled = 2,
}

/// <summary>One organized Parent-Teacher Conference. It can span several dates and many classes (one schedule per class).</summary>
public class PtcEvent : IAuditable
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 1000;
    public const int PublicTokenMaxLength = 32;

    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }

    public int AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;

    /// <summary>First and last day of the event (e.g. the PTC week); every class schedule's date falls within them.</summary>
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>
    /// Draft: being configured, internal and never bookable. Published: checked ready and shown on the School Calendar;
    /// the only status parents can book (Phase 3). Completed / Cancelled: closed, kept as history.
    /// </summary>
    public PtcEventStatus Status { get; set; } = PtcEventStatus.Draft;

    /// <summary>
    /// The unguessable code in the event's public booking link (/ptc-booking/{token}); never the database id. Set when the
    /// event is created (or first published, for older events). The link only works while the event is Published.
    /// </summary>
    public string? PublicToken { get; set; }

    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PtcClassSchedule> ClassSchedules { get; set; } = new List<PtcClassSchedule>();
}

/// <summary>
/// An existing class's PTC in an event: its date and time window. One active schedule per class and event (a database
/// index); a cancelled one stays as history and the class can be scheduled again.
/// </summary>
public class PtcClassSchedule : IAuditable
{
    public const int NotesMaxLength = 500;

    public int Id { get; set; }
    public int PtcEventId { get; set; }
    public PtcEvent PtcEvent { get; set; } = null!;
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    /// <summary>The school-local date of the class's PTC.</summary>
    public DateOnly EventDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    public PtcScheduleStatus Status { get; set; } = PtcScheduleStatus.Scheduled;
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PtcRound> Rounds { get; set; } = new List<PtcRound>();
    public ICollection<PtcClassTeacher> Teachers { get; set; } = new List<PtcClassTeacher>();
    public ICollection<PtcSubstituteAssignment> SubstituteAssignments { get; set; } = new List<PtcSubstituteAssignment>();
}

/// <summary>
/// One booking window of a class schedule. Capacity, meeting and transition durations are copied from the PTC defaults when
/// the Round is created, so changing the defaults later never changes an existing Round. The Round's own times (Start/End)
/// are its duration; the planned default is (Capacity × MeetingDurationMinutes) + TransitionDurationMinutes.
/// </summary>
public class PtcRound : IAuditable
{
    public int Id { get; set; }
    public int PtcClassScheduleId { get; set; }
    public PtcClassSchedule ClassSchedule { get; set; } = null!;

    /// <summary>1, 2, 3… within the class schedule.</summary>
    public int RoundNumber { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    /// <summary>The most parents (confirmed bookings) the Round takes.</summary>
    public int Capacity { get; set; }
    public int MeetingDurationMinutes { get; set; }
    public int TransitionDurationMinutes { get; set; }

    public PtcRoundStatus Status { get; set; } = PtcRoundStatus.Open;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PtcParentBooking> Bookings { get; set; } = new List<PtcParentBooking>();
}

/// <summary>
/// A parent's confirmed booking of a Round for one student. THRYD has no separate parent/guardian record: the parent is
/// identified by the student's own parent contact number (<see cref="Student.ParentContactNumber"/>), and the booking keeps
/// that number (international digits, as used for WhatsApp) as the parent reference — it must match the student's record.
/// <see cref="SlotNumber"/> is the booking's place in the Round (1 … Capacity): unique among the Round's confirmed bookings,
/// so the database itself never holds more confirmed bookings than slots.
/// </summary>
public class PtcParentBooking
{
    public const int ReferenceLength = 12;
    public const int ParentContactMaxLength = 15;
    public const int NotesMaxLength = 500;

    public int Id { get; set; }
    public int PtcRoundId { get; set; }
    public PtcRound Round { get; set; } = null!;

    /// <summary>
    /// The Round's class schedule (always the Round's own; set by the booking service). A student has at most one active
    /// booking per class schedule, whichever Round: enforced by the service and by a unique database index.
    /// </summary>
    public int PtcClassScheduleId { get; set; }
    public PtcClassSchedule ClassSchedule { get; set; } = null!;

    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;

    /// <summary>The parent: the student's parent contact number as international digits (e.g. 201001234567).</summary>
    public string ParentContact { get; set; } = "";

    /// <summary>Short unique code given to the parent ("PTC-7KQ2M9XA").</summary>
    public string BookingReference { get; set; } = "";
    public int SlotNumber { get; set; }

    public PtcBookingStatus Status { get; set; } = PtcBookingStatus.Confirmed;
    public string? Notes { get; set; }

    public DateTime BookedAt { get; set; }
    public string? BookedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }

    public ICollection<PtcBookingSubject> Subjects { get; set; } = new List<PtcBookingSubject>();
    public ICollection<PtcTeacherInterview> Interviews { get; set; } = new List<PtcTeacherInterview>();
}

/// <summary>
/// A subject the parent wants to discuss and its teacher. Exactly one source is kept, so a later phase can check it is still
/// current: <see cref="SourceTimetableAssignmentId"/> (a timetable lesson of the class) or, for a KG class,
/// <see cref="SourcePtcClassTeacherId"/> (the teacher the Admin selected). One active row per department + subject in a booking.
/// </summary>
public class PtcBookingSubject : IAuditable
{
    public int Id { get; set; }
    public int PtcParentBookingId { get; set; }
    public PtcParentBooking Booking { get; set; } = null!;

    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string SubjectName { get; set; } = "";

    public int AssignedTeacherStaffMemberId { get; set; }
    public StaffMember AssignedTeacher { get; set; } = null!;

    public int? SourceTimetableAssignmentId { get; set; }
    public TimetableAssignment? SourceTimetableAssignment { get; set; }

    public int? SourcePtcClassTeacherId { get; set; }
    public PtcClassTeacher? SourceClassTeacher { get; set; }

    public PtcBookingSubjectStatus Status { get; set; } = PtcBookingSubjectStatus.Active;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One teacher's interview with a booking's parent about one subject. Planned times (<see cref="Date"/>,
/// <see cref="ScheduledStart"/>–<see cref="ScheduledEnd"/>, school-local) are separate from what actually happened
/// (<see cref="ActualStart"/> / <see cref="ActualEnd"/>, UTC instants recorded in a later phase). <see cref="Date"/> is the
/// class schedule's date, kept on the row so a teacher's interviews on a day are one indexed query.
/// </summary>
public class PtcTeacherInterview : IAuditable
{
    public int Id { get; set; }
    public int PtcParentBookingId { get; set; }
    public PtcParentBooking Booking { get; set; } = null!;
    public int PtcBookingSubjectId { get; set; }
    public PtcBookingSubject BookingSubject { get; set; } = null!;

    public int TeacherStaffMemberId { get; set; }
    public StaffMember Teacher { get; set; } = null!;

    public DateOnly Date { get; set; }
    public TimeOnly ScheduledStart { get; set; }
    public TimeOnly ScheduledEnd { get; set; }

    public DateTime? ActualStart { get; set; }
    public DateTime? ActualEnd { get; set; }

    public PtcInterviewStatus Status { get; set; } = PtcInterviewStatus.Scheduled;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A substitute chosen by an administrator to cover an original teacher's class time while the original teacher is at a
/// class's PTC (the class schedule's date, <see cref="StartTime"/>–<see cref="EndTime"/>). Never created automatically.
/// Cancelling keeps the row (status, who and when) as history.
/// </summary>
public class PtcSubstituteAssignment : IAuditable
{
    public const int ReasonMaxLength = 300;
    public const int NotesMaxLength = 500;

    public int Id { get; set; }
    public int PtcClassScheduleId { get; set; }
    public PtcClassSchedule ClassSchedule { get; set; } = null!;

    public int OriginalTeacherStaffMemberId { get; set; }
    public StaffMember OriginalTeacher { get; set; } = null!;
    public int SubstituteTeacherStaffMemberId { get; set; }
    public StaffMember SubstituteTeacher { get; set; } = null!;

    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// The original teacher's timetable lesson this cover is for (Class Coverage, Phase 6): its class, subject and department
    /// are read from it, and <see cref="StartTime"/>–<see cref="EndTime"/> are its times on the class schedule's date. The
    /// lesson itself is never changed: the cover applies to that date only. Null for a free time range recorded without a
    /// lesson. The cover counts only while it still matches the lesson (same teacher, current that week, same times).
    /// </summary>
    public int? TimetableAssignmentId { get; set; }
    public TimetableAssignment? Lesson { get; set; }

    /// <summary>Administrative notes (editable; every change is in <see cref="PtcSubstituteChange"/>).</summary>
    public string? Notes { get; set; }

    public PtcSubstituteStatus Status { get; set; } = PtcSubstituteStatus.Active;
    public string? AssignedBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// HISTORY ONLY. A teacher an Admin once selected manually for a KG class's PTC (an earlier rule). Every class, KG included,
/// now takes its PTC subjects and teachers from its timetable lessons; these rows are never offered, used or created any
/// more. They are kept (never deleted silently) and reported on the event's readiness so they can be checked.
/// </summary>
public class PtcClassTeacher : IAuditable
{
    public int Id { get; set; }
    public int PtcClassScheduleId { get; set; }
    public PtcClassSchedule ClassSchedule { get; set; } = null!;

    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;

    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string SubjectName { get; set; } = "";

    public PtcClassTeacherStatus Status { get; set; } = PtcClassTeacherStatus.Active;
    public string? AssignedBy { get; set; }
    public DateTime? RemovedAt { get; set; }
    public string? RemovedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>What an Admin schedule change did. Numbers are stored.</summary>
public enum PtcScheduleChangeKind
{
    /// <summary>A booking moved to another slot of its Round (its interviews moved with it; missing ones were allocated).</summary>
    SlotReassigned = 1,

    /// <summary>A Round moved to another start time (its bookings' interviews moved by the same amount).</summary>
    RoundMoved = 2,
}

/// <summary>
/// Audit of a schedule change made by an Admin: what was there before and after, why, who and when. Never edited or deleted.
/// One row per affected booking (and one for the Round itself when a Round moves). Holds no phone numbers.
/// </summary>
public class PtcScheduleChange
{
    public const int TextMaxLength = 500;

    public int Id { get; set; }
    public int PtcEventId { get; set; }
    public PtcEvent PtcEvent { get; set; } = null!;
    public int PtcClassScheduleId { get; set; }
    public PtcClassSchedule ClassSchedule { get; set; } = null!;
    public int PtcRoundId { get; set; }
    public PtcRound Round { get; set; } = null!;
    public int? PtcParentBookingId { get; set; }
    public PtcParentBooking? Booking { get; set; }

    public PtcScheduleChangeKind Kind { get; set; }
    public DateOnly Date { get; set; }
    public int? OldSlot { get; set; }
    public int? NewSlot { get; set; }
    public TimeOnly OldStart { get; set; }
    public TimeOnly OldEnd { get; set; }
    public TimeOnly NewStart { get; set; }
    public TimeOnly NewEnd { get; set; }

    /// <summary>The teachers whose interviews moved (names, as they were).</summary>
    public string? Teachers { get; set; }
    public string Reason { get; set; } = "";
    public string? ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}

/// <summary>What happened to a teacher's interview on the day. Numbers are stored.</summary>
public enum PtcInterviewEventKind
{
    Started = 1,
    Ended = 2,

    /// <summary>An Admin correction of the recorded status or actual times (with a reason).</summary>
    Corrected = 3,
}

/// <summary>
/// Operational history of one teacher's meeting with one booking (all that teacher's interviews of the booking, which run
/// as one meeting): who started or ended it and when (server time), and every Admin correction with the previous and new
/// values. Never edited or deleted; planned times are never stored here or changed. Holds no phone numbers.
/// </summary>
public class PtcInterviewEvent
{
    public const int ReasonMaxLength = 500;

    public int Id { get; set; }
    public int PtcParentBookingId { get; set; }
    public PtcParentBooking Booking { get; set; } = null!;
    public int TeacherStaffMemberId { get; set; }
    public StaffMember Teacher { get; set; } = null!;

    public PtcInterviewEventKind Kind { get; set; }
    public PtcInterviewStatus? OldStatus { get; set; }
    public PtcInterviewStatus NewStatus { get; set; }
    public DateTime? OldActualStart { get; set; }
    public DateTime? OldActualEnd { get; set; }
    public DateTime? NewActualStart { get; set; }
    public DateTime? NewActualEnd { get; set; }
    public string? Reason { get; set; }

    /// <summary>Who did it (the teacher, or the Super Admin) and when (UTC, server clock).</summary>
    public string? By { get; set; }
    public int? ByStaffId { get; set; }
    public DateTime At { get; set; }
}

/// <summary>What an administrator did to a PTC substitute assignment. Numbers are stored.</summary>
public enum PtcSubstituteChangeKind
{
    Assigned = 1,

    /// <summary>Another substitute took over the same cover (the previous assignment is cancelled, the new one created).</summary>
    Replaced = 2,

    Removed = 3,
    NotesChanged = 4,
}

/// <summary>
/// Audit of PTC substitute (class coverage) changes: who changed what, when and why, with the substitutes before and after.
/// Never edited or deleted. <see cref="Lesson"/> keeps the lesson as it was described at the time.
/// </summary>
public class PtcSubstituteChange
{
    public const int LessonMaxLength = 300;
    public const int TextMaxLength = 500;

    public int Id { get; set; }
    public int PtcEventId { get; set; }
    public PtcEvent PtcEvent { get; set; } = null!;
    public int PtcClassScheduleId { get; set; }
    public PtcClassSchedule ClassSchedule { get; set; } = null!;

    /// <summary>The assignment created (Assigned, Replaced) or changed (Removed, NotesChanged).</summary>
    public int PtcSubstituteAssignmentId { get; set; }
    public PtcSubstituteAssignment Assignment { get; set; } = null!;

    public PtcSubstituteChangeKind Kind { get; set; }
    public int OriginalTeacherStaffMemberId { get; set; }
    public StaffMember OriginalTeacher { get; set; } = null!;
    public int? OldSubstituteStaffMemberId { get; set; }
    public StaffMember? OldSubstitute { get; set; }
    public int? NewSubstituteStaffMemberId { get; set; }
    public StaffMember? NewSubstitute { get; set; }

    public string? Lesson { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}
