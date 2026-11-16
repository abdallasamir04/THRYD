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

/// <summary>Period Management: every period of the school, or one class's period structure.</summary>
public sealed class PeriodsPageViewModel
{
    public required PeriodMap Map { get; init; }
    public int? ClassId { get; init; }
    public string? ClassName { get; init; }

    /// <summary>Current lessons per period (of the class when one is chosen).</summary>
    public required IReadOnlyDictionary<int, int> Lessons { get; init; }

    public bool IsClass => ClassId is not null;

    /// <summary>The class's periods, or the school-wide periods.</summary>
    public IReadOnlyList<SlotView> Periods => ClassId is { } c ? Map.ForClass(c) : Map.SchoolWide;

    /// <summary>All classes' own periods (school view).</summary>
    public IReadOnlyList<SlotView> ClassSpecific => PeriodMap.Order(Map.All.Where(s => s.ClassId is not null && !s.Archived));

    /// <summary>School periods the class has replaced with its own version or removed.</summary>
    public IReadOnlyList<SlotView> Replacements => ClassId is { } c ? Map.Replacements(c) : [];
}

/// <summary>Add / edit a period: name, times, display order, enabled, which days and which classes.</summary>
public sealed class PeriodFormModel
{
    public int? Id { get; set; }

    /// <summary>The class timetable the form was opened from (for the scope default and the way back).</summary>
    public int? ContextClassId { get; set; }

    public PeriodScope Scope { get; set; } = PeriodScope.AllClasses;

    /// <summary>The class for "this class only".</summary>
    public int? ClassId { get; set; }

    [Display(Name = "Name")]
    [StringLength(TimeSlot.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Name { get; set; }

    [Display(Name = "Start time")]
    public TimeOnly? StartTime { get; set; }

    [Display(Name = "End time")]
    public TimeOnly? EndTime { get; set; }

    [Display(Name = "Display order")]
    [Range(1, TimeSlot.MaxOrder, ErrorMessage = "Enter a display order from {1} to {2}.")]
    public int? Order { get; set; }

    public bool IsActive { get; set; } = true;
    public bool AllDays { get; set; } = true;
    public List<DayOfWeek> Days { get; set; } = [];

    /// <summary>The fingerprint of the plan the administrator confirmed (see <see cref="PeriodPlan.Fingerprint"/>).</summary>
    public string? Confirmation { get; set; }

    // Display
    public SlotView? Period { get; set; }
    public IReadOnlyList<(int Id, string Name)> Classes { get; set; } = [];
    public int ClassesUsing { get; set; }
    public string? ContextClassName { get; set; }
    public PeriodPlan? Plan { get; set; }
    public bool IsEdit => Id is not null;

    /// <summary>A class's own period only ever affects that class.</summary>
    public bool ScopeFixed => Period is { ClassId: not null };

    public PeriodInput ToInput() => new(Name, StartTime, EndTime, Order, IsActive, AllDays, Days);
}

public sealed class PeriodDeleteModel
{
    public int Id { get; set; }
    public int? ContextClassId { get; set; }
    public PeriodScope Scope { get; set; } = PeriodScope.AllClasses;
    public int? ClassId { get; set; }

    /// <summary>The explicit choice to end the period's current lessons from this week (history kept).</summary>
    public bool EndLessons { get; set; }
    public string? Confirmation { get; set; }

    public SlotView? Period { get; set; }
    public IReadOnlyList<(int Id, string Name)> Classes { get; set; } = [];
    public string? ContextClassName { get; set; }
    public IReadOnlyList<LessonRef> Lessons { get; set; } = [];
    public PeriodPlan? Plan { get; set; }
}

/// <summary>Move a lesson to another day / period of its class. <see cref="Target"/> = "Day:SlotId".</summary>
public sealed class MoveSessionModel
{
    public int SessionId { get; set; }
    public string? Target { get; set; }
    public DayOfWeek? Day { get; set; }
    public int? SlotId { get; set; }

    /// <summary>Sent by drag and drop: a failure is a message, not the form again.</summary>
    public bool DragDrop { get; set; }

    public SessionView? Session { get; set; }
    public SlotView? Slot { get; set; }
    public IReadOnlyList<(DayOfWeek Day, SlotView Slot)> Targets { get; set; } = [];

    public (DayOfWeek? Day, int? SlotId) Parse()
    {
        if (Day is not null && SlotId is not null) return (Day, SlotId);
        var parts = (Target ?? "").Split(':');
        return parts.Length == 2 && Enum.TryParse<DayOfWeek>(parts[0], out var d) && int.TryParse(parts[1], out var s) ? (d, s) : (null, null);
    }
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
