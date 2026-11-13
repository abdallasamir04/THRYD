namespace SchoolManagement.Web.Entities;

// ============================================================ Two-level student attendance
//
// School Attendance = StudentArrivalRecord (one per student per school day, unchanged).
// Session Attendance = SessionAttendanceRecord (one per student per scheduled lesson). A lesson is a
// TimetableAssignment row (class, day, slot, subject, teacher) on a date of a week in which that row
// is active: nothing of the timetable is copied here, only the link and the date.
// Alerts are derived from both levels by AttendanceAlertService and kept with their follow-up history.

/// <summary>A day or range of days without school (Sunday–Thursday dates inside it are not school days).</summary>
public class SchoolHoliday : IAuditable
{
    public const int NameMaxLength = 100;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Status of a student in one lesson. Numbers are stored: add new values, never reuse one.</summary>
public enum SessionAttendanceStatus
{
    Present = 1,
    Absent = 2,
    Late = 3,
    Excused = 4,
}

/// <summary>
/// One student's attendance in one scheduled lesson on one date (unique per student, session and date).
/// A lesson without a record is "Not recorded" — never absent.
/// </summary>
public class SessionAttendanceRecord : IAuditable
{
    public const int NotesMaxLength = 300;

    public int Id { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;

    /// <summary>The timetable session (class, weekday, slot, subject, teacher).</summary>
    public int TimetableAssignmentId { get; set; }
    public TimetableAssignment Session { get; set; } = null!;

    /// <summary>The calendar date of the lesson (its weekday is the session's day).</summary>
    public DateOnly SessionDate { get; set; }

    /// <summary>The student's class on that day (kept if the student moves class later).</summary>
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    public SessionAttendanceStatus Status { get; set; }
    public string? Notes { get; set; }
    public string? RecordedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<SessionAttendanceCorrection> Corrections { get; set; } = new List<SessionAttendanceCorrection>();
}

/// <summary>Audit trail of changes to a session attendance record: before, after, why (optional) and who.</summary>
public class SessionAttendanceCorrection
{
    public const int TextMaxLength = 400;
    public const int ReasonMaxLength = 500;

    public int Id { get; set; }
    public int SessionAttendanceRecordId { get; set; }
    public SessionAttendanceRecord Record { get; set; } = null!;
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime CorrectedAt { get; set; }
    public string? CorrectedBy { get; set; }
}

/// <summary>A scheduled lesson that does not take place on one date (no attendance expected, no alerts).</summary>
public class SessionCancellation
{
    public const int ReasonMaxLength = 300;

    public int Id { get; set; }
    public int TimetableAssignmentId { get; set; }
    public TimetableAssignment Session { get; set; } = null!;
    public DateOnly SessionDate { get; set; }
    public string Reason { get; set; } = "";
    public string? CancelledBy { get; set; }
    public DateTime CancelledAt { get; set; }
}

public enum AttendanceAlertType
{
    /// <summary>Absent from school on three or more consecutive school days.</summary>
    ConsecutiveAbsence = 1,

    /// <summary>Present at school but absent from a scheduled lesson.</summary>
    AbsentFromSession = 2,
}

public enum AttendanceAlertStatus
{
    /// <summary>New: nobody has followed it up yet.</summary>
    Open = 1,

    /// <summary>Being followed up (e.g. the parent was called, waiting for an answer).</summary>
    FollowUp = 2,

    /// <summary>Closed by staff.</summary>
    Resolved = 3,

    /// <summary>Closed automatically: after a correction the attendance no longer matches the rule.</summary>
    Cleared = 4,
}

/// <summary>
/// An alert for the administration. <see cref="Key"/> identifies what it is about (student + streak start,
/// or student + lesson + date) and is unique, so the same situation never creates a second alert:
/// it is updated, cleared or reopened instead.
/// </summary>
public class AttendanceAlert : IAuditable
{
    public const int KeyMaxLength = 100;
    public const int DatesMaxLength = 2000;
    public const int ResolutionMaxLength = 1000;

    public int Id { get; set; }
    public AttendanceAlertType Type { get; set; }
    public string Key { get; set; } = "";

    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;

    /// <summary>The student's class on the (last) day concerned.</summary>
    public int SchoolClassId { get; set; }
    public SchoolClass SchoolClass { get; set; } = null!;

    /// <summary>Streak start, or the lesson's date.</summary>
    public DateOnly FirstDate { get; set; }

    /// <summary>Last absent day of the streak, or the lesson's date.</summary>
    public DateOnly LastDate { get; set; }

    /// <summary>Consecutive school days absent (1 for a lesson alert).</summary>
    public int DayCount { get; set; }

    /// <summary>The absent dates of the streak as yyyy-MM-dd, comma separated.</summary>
    public string? AbsenceDates { get; set; }

    /// <summary>The lesson, for <see cref="AttendanceAlertType.AbsentFromSession"/>.</summary>
    public int? TimetableAssignmentId { get; set; }
    public TimetableAssignment? Session { get; set; }

    public AttendanceAlertStatus Status { get; set; } = AttendanceAlertStatus.Open;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? Resolution { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<AttendanceAlertEvent> Events { get; set; } = new List<AttendanceAlertEvent>();
}

public enum AttendanceAlertEventKind
{
    Created = 1,
    Updated = 2,
    FollowUp = 3,
    Resolved = 4,
    Reopened = 5,
    Cleared = 6,
}

/// <summary>History of an alert: what the system detected and what staff did (never edited).</summary>
public class AttendanceAlertEvent
{
    public const int TextMaxLength = 1000;

    public int Id { get; set; }
    public int AttendanceAlertId { get; set; }
    public AttendanceAlert Alert { get; set; } = null!;
    public AttendanceAlertEventKind Kind { get; set; }
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
    public string? By { get; set; }
}
