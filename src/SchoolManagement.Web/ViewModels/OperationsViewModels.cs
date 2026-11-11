using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

// ---------- Morning duty ----------

public sealed class MorningDutyViewModel
{
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<DutyComponentSummary> Components { get; init; }

    /// <summary>The selected tab: Morning, Midday or Dismissal.</summary>
    public DutyType Type { get; init; } = DutyType.Morning;
}

public sealed class DutyListViewModel
{
    public required DutyComponentInfo Info { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<DutyRow> Duties { get; init; }
}

public sealed class DutyDetailsViewModel
{
    public required DutyRow Duty { get; init; }
    public required DutyComponentInfo Info { get; init; }
    public required IReadOnlyList<BackupResponseRow> History { get; init; }
    public IReadOnlyList<DutyAuditRow> AuditTrail { get; init; } = [];
    public IReadOnlyList<DutyStaffRow> Staff { get; init; } = [];
}

public sealed class DutyFormModel
{
    public int? Id { get; set; }
    public DutyComponent Component { get; set; }

    [Display(Name = "Date")]
    [Required(ErrorMessage = "Choose the date.")]
    public DateOnly? DutyDate { get; set; }

    [Display(Name = "Duty description")]
    [Required(ErrorMessage = "Describe the duty.")]
    [StringLength(DutyAssignment.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Description { get; set; } = "";

    [Required(ErrorMessage = "Enter the start time.")]
    public TimeOnly? StartTime { get; set; }

    [Required(ErrorMessage = "Enter the end time.")]
    public TimeOnly? EndTime { get; set; }

    [Display(Name = "Duty location")]
    [StringLength(DutyAssignment.LocationMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Location { get; set; }

    [Display(Name = "Assigned staff")]
    [Required(ErrorMessage = "Choose the assigned staff member.")]
    public int? PrimaryStaffId { get; set; }

    public AttendanceStatus PrimaryAttendance { get; set; }

    [Display(Name = "Backup staff")]
    public int? BackupStaffId { get; set; }

    [Display(Name = "Operational notes")]
    [StringLength(DutyAssignment.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    public List<int> ClassIds { get; set; } = [];

    // Display only
    public IReadOnlyList<StaffOption> Staff { get; set; } = [];
    public IReadOnlyList<ClassOption> Classes { get; set; } = [];
    public BackupRequestStatus BackupStatus { get; set; }
    public AttendanceStatus BackupAttendance { get; set; }
    public int? CurrentBackupId { get; set; }
    public bool IsEdit => Id is > 0;
    public DutyComponentInfo Info => DutyComponents.Get(Component);

    public DutyInput ToInput() => new(Component, DutyDate ?? Common.SchoolWeek.Today, Description, StartTime, EndTime, Location,
        PrimaryStaffId, PrimaryAttendance, PrimaryAttendance == AttendanceStatus.Absent ? BackupStaffId : null, Notes, ClassIds);
}

// ---------- Teacher workload ----------

public sealed class WorkloadOverviewViewModel
{
    public required DateOnly Week { get; init; }
    public required WorkloadOverviewQuery Query { get; init; }
    public required IReadOnlyList<WorkloadSummary> Rows { get; init; }
    public required IReadOnlyList<WorkloadSummary> AllRows { get; init; }
    public required IReadOnlyList<JobTitleCount> JobTitles { get; init; }
    public IReadOnlyList<Department> Departments { get; init; } = [];
    public int MaxWeeklyPeriods { get; init; } = 24;
    /// <summary>Near capacity starts 4 periods below the maximum (20 when the maximum is 24).</summary>
    public int NearFrom => Math.Max(0, MaxWeeklyPeriods - 4);
}

public sealed class TeacherWorkloadViewModel
{
    public required TeacherRow Teacher { get; init; }
    public required WorkloadSummary Summary { get; init; }
    public required IReadOnlyList<WorkloadAssignmentRow> Assignments { get; init; }
    public required IReadOnlyList<WorkloadHistoryRow> History { get; init; }
}

public sealed class TeacherDetailsViewModel
{
    public required TeacherRow Teacher { get; init; }
    public required WorkloadSummary Workload { get; init; }
}

public sealed class WorkloadFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Teacher")]
    [Required(ErrorMessage = "Choose a teacher.")]
    public int? TeacherId { get; set; }

    [Display(Name = "Week")]
    [Required(ErrorMessage = "Choose the week.")]
    public DateOnly? Week { get; set; }

    public WorkloadCategory Category { get; set; } = WorkloadCategory.Class;

    [Display(Name = "Periods")]
    [Required(ErrorMessage = "Enter the number of periods.")]
    [Range(1, TeacherWorkloadAssignment.MaxWeeklyPeriods, ErrorMessage = "Enter a number from {1} to {2}.")]
    public int? Periods { get; set; }

    [Display(Name = "Class")]
    public int? SchoolClassId { get; set; }

    [Display(Name = "Recorded duty")]
    public int? DutyAssignmentId { get; set; }

    [Display(Name = "Note")]
    [StringLength(TeacherWorkloadAssignment.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    // Display only
    public IReadOnlyList<TeacherRow> Teachers { get; set; } = [];
    public IReadOnlyList<ClassOption> Classes { get; set; } = [];
    public IReadOnlyList<DutyOption> Duties { get; set; } = [];
    public WorkloadSummary? Summary { get; set; }
    public int CurrentPeriods { get; set; }
    public bool TeacherLocked { get; set; }
    public bool IsEdit => Id is > 0;

    /// <summary>Kept when editing a period that was placed in a timetable slot.</summary>
    public DayOfWeek? Day { get; set; }
    public int? TimeSlotId { get; set; }

    public WorkloadInput ToInput() => new(TeacherId ?? 0, Week ?? Common.SchoolWeek.Current, Category, Periods ?? 0,
        SchoolClassId, DutyAssignmentId, Description, Day, TimeSlotId);
}
