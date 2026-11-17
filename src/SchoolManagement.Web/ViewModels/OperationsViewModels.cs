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

    /// <summary>The Sub-Duty on that date: schedule, calculated status, coverage points and reports.</summary>
    public SubDutyView? Sub { get; init; }
    public MainDutyView? Main { get; init; }
    public IReadOnlyList<DutyReportView> ReportHistory { get; init; } = [];
}

/// <summary>Duties landing page: every Main Duty of the day with its Sub-Duties, plus Activities.</summary>
public sealed class DutyHubViewModel
{
    public required DutyDay Day { get; init; }
    public int Activities { get; init; }
}

/// <summary>A Main Duty page: its schedule, status, Sub-Duties and the coverage report.</summary>
public sealed class MainDutyPageViewModel
{
    public required DutyDay Day { get; init; }
    public required MainDutyView Main { get; init; }
}

public sealed class DutyDepartmentsViewModel
{
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<DutyDepartmentView> Departments { get; init; }
}

/// <summary>Schedule page: the Main Duty range and each Sub-Duty's schedule (all versions kept).</summary>
public sealed class DutySchedulePageViewModel
{
    public required DutyScheduleView Schedule { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Departments { get; init; }
}

/// <summary>Main Duty time range, from a date.</summary>
public sealed class MainScheduleForm
{
    public string? EffectiveFrom { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public bool AllDays { get; set; } = true;
    public List<DayOfWeek> Days { get; set; } = [];
}

/// <summary>A Sub-Duty: name and description (when added or renamed) and its schedule from a date.</summary>
public sealed class SectionForm
{
    public int? Id { get; set; }
    public DutyType Type { get; set; }
    public string? Name { get; set; }
    public string? Summary { get; set; }
    public string? EffectiveFrom { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public bool Required { get; set; } = true;
    public int? DepartmentId { get; set; }
    public bool AllDays { get; set; } = true;
    public List<DayOfWeek> Days { get; set; } = [];

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public IReadOnlyList<(int Id, string Name)> Departments { get; set; } = [];
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public TimeOnly? MainStart { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public TimeOnly? MainEnd { get; set; }

    public SectionInput ToInput(DateOnly today) => new(
        Name, Summary, DutyForms.Date(EffectiveFrom) ?? today, DutyForms.Time(StartTime), DutyForms.Time(EndTime), Required, DepartmentId,
        PeriodDays.Mask(AllDays, Days));
}

/// <summary>Report that a responsibility was not covered (or edit a report: a new version replaces it).</summary>
public sealed class CoverageReportForm
{
    public string? Component { get; set; }
    public string? Date { get; set; }
    public int? PointId { get; set; }
    public string? Area { get; set; }
    public DutyUncoveredReason? Reason { get; set; }
    public string? Notes { get; set; }
    public int? Supersedes { get; set; }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public SubDutyView? Sub { get; set; }
}

public static class DutyForms
{
    public static DateOnly? Date(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    public static TimeOnly? Time(string? s) =>
        TimeOnly.TryParseExact(s, ["HH:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
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
