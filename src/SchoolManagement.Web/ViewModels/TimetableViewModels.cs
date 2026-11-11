using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

public sealed class TimetablePageViewModel
{
    public required TimetableGrid Grid { get; init; }

    /// <summary>"class" or "teacher": which side of the same data is shown.</summary>
    public required string Mode { get; init; }
    public required int EntityId { get; init; }
    public required string Name { get; init; }
    public string? Subtitle { get; init; }
    public WorkloadSummary? Workload { get; init; }

    /// <summary>Quick switcher: other classes / teachers.</summary>
    public IReadOnlyList<(int Id, string Name, string? Note)> Others { get; init; } = [];

    public bool IsClass => Mode == "class";
}

public sealed class ClassPickerViewModel
{
    public required IReadOnlyList<ClassTimetableSummary> Classes { get; init; }
    public required int ActiveSlots { get; init; }
}

public sealed class TeacherPickerViewModel
{
    public required IReadOnlyList<TeacherTimetableSummary> Teachers { get; init; }
    public required IReadOnlyList<Department> Departments { get; init; }
    public string? Search { get; init; }
    public int? DepartmentId { get; init; }
}

public sealed class SlotsViewModel
{
    public required IReadOnlyList<SlotView> Slots { get; init; }
    public required IReadOnlyDictionary<int, int> InUse { get; init; }
}

public sealed class SlotFormModel
{
    public int Id { get; set; }
    public int Number { get; set; }

    [Display(Name = "Name")]
    [StringLength(TimeSlot.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Name { get; set; }

    [Display(Name = "Start time")]
    public TimeOnly? StartTime { get; set; }

    [Display(Name = "End time")]
    public TimeOnly? EndTime { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    /// <summary>Required when the times of a slot with assignments change.</summary>
    public bool ConfirmAffected { get; set; }

    public int InUse { get; set; }
}

/// <summary>Assign / edit a teaching session. The class, day and slot come from the clicked cell.</summary>
public sealed class AssignPeriodModel
{
    public int? SessionId { get; set; }
    public int ClassId { get; set; }
    public DayOfWeek Day { get; set; }
    public int SlotId { get; set; }
    public string? Week { get; set; }

    [Display(Name = "Department")]
    [Required(ErrorMessage = "Choose the department.")]
    public int? DepartmentId { get; set; }

    [Display(Name = "Subject")]
    [Required(ErrorMessage = "Choose the subject.")]
    public string? Subject { get; set; }

    [Display(Name = "Teacher")]
    [Required(ErrorMessage = "Choose the teacher.")]
    public int? TeacherId { get; set; }

    [Display(Name = "Notes")]
    [StringLength(TimetableAssignment.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    // Display
    public string ClassName { get; set; } = "";
    public SlotView? Slot { get; set; }
    public IReadOnlyList<(int Id, string Name, IReadOnlyList<string> Subjects)> Departments { get; set; } = [];
    public IReadOnlyList<TeacherCandidate> Candidates { get; set; } = [];
    public bool IsEdit => SessionId is > 0;

    public SessionInput ToInput() => new(ClassId, Day, SlotId, DepartmentId, Subject, TeacherId, Notes);
}

/// <summary>An occupied class slot: the session, and its substitutions in the shown week.</summary>
public sealed class SlotPanelModel
{
    public required SessionView Session { get; init; }
    public required SlotView Slot { get; init; }
    public required DateOnly Week { get; init; }
    public IReadOnlyList<WeekPeriodView> Substitutions { get; init; } = [];
    public bool Editable { get; init; }
}

public sealed class SubstituteModel
{
    public int SessionId { get; set; }
    public string? Week { get; set; }

    [Display(Name = "Substitute teacher")]
    [Required(ErrorMessage = "Choose the substitute teacher.")]
    public int? TeacherId { get; set; }

    [Display(Name = "Note")]
    [StringLength(TeacherWorkloadAssignment.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    public SessionView? Session { get; set; }
    public SlotView? Slot { get; set; }
    public DateOnly WeekStart { get; set; }
    public IReadOnlyList<TeacherCandidate> Candidates { get; set; } = [];
}

/// <summary>An empty slot of a teacher's timetable: office hours, or counting one of the teacher's duties.</summary>
public sealed class TeacherSlotModel
{
    public int TeacherId { get; set; }
    public DayOfWeek Day { get; set; }
    public int SlotId { get; set; }
    public string? Week { get; set; }
    public string Kind { get; set; } = "office";

    [Display(Name = "Note")]
    [StringLength(TeacherWorkloadAssignment.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    public int? DutyAssignmentId { get; set; }

    public string TeacherName { get; set; } = "";
    public SlotView? Slot { get; set; }
    public DateOnly WeekStart { get; set; }
    public WorkloadSummary? Workload { get; set; }
    public IReadOnlyList<DutyView> Duties { get; set; } = [];
    public IReadOnlyList<WeekPeriodView> Periods { get; set; } = [];
}
