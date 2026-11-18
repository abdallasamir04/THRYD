namespace SchoolManagement.Web.Entities;

// Employment configuration and daily attendance of school staff (every StaffMember: teachers and management).

public enum EmploymentType { FullTime = 1, PartTime = 2 }

public enum WorkingHoursType { Fixed = 1, Flexible = 2 }

/// <summary>Probation length unit. New staff use Days (the 30 / 60 / 90-day presets); Weeks and Months stay valid for earlier records.</summary>
public enum ProbationUnit { Weeks = 1, Months = 2, Days = 3 }

/// <summary>A weekday a part-time staff member works (one row per day; full-time staff have none).</summary>
public class StaffWorkingDay
{
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public DayOfWeek Day { get; set; }
}

/// <summary>A staff member's attendance on one school day: Present / Late (with the actual arrival time) or Absent.</summary>
public enum StaffAttendanceStatus { Present = 1, Late = 2, Absent = 3 }

/// <summary>
/// One staff member on one school date (one record at most). No record means Not Recorded — never Absent.
/// Present and Late always carry the arrival time that was actually recorded; Absent never has one.
/// </summary>
public class StaffAttendanceRecord : IAuditable
{
    public const int NotesMaxLength = 300;

    public int Id { get; set; }
    public int StaffMemberId { get; set; }
    public StaffMember StaffMember { get; set; } = null!;
    public DateOnly Date { get; set; }
    public StaffAttendanceStatus Status { get; set; }
    public TimeOnly? ArrivalTime { get; set; }
    public string? Notes { get; set; }
    public string? RecordedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<StaffAttendanceCorrection> Corrections { get; set; } = new List<StaffAttendanceCorrection>();
}

/// <summary>A correction of a saved staff attendance record: the record before and after, why, when and by whom.</summary>
public class StaffAttendanceCorrection
{
    public const int TextMaxLength = 100;
    public const int ReasonMaxLength = 300;

    public long Id { get; set; }
    public int StaffAttendanceRecordId { get; set; }
    public StaffAttendanceRecord Record { get; set; } = null!;
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime CorrectedAt { get; set; }
    public string? CorrectedBy { get; set; }
}
