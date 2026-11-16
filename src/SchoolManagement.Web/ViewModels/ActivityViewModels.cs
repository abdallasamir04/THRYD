using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

/// <summary>Duties → Activities: the built-in P.E. card, the Add Activity card and one card per activity from the database.</summary>
public sealed class ActivitiesPageViewModel
{
    public required DateOnly Date { get; init; }
    public required int PeSessionsThisWeek { get; init; }
    public required int PeSessionsToday { get; init; }

    /// <summary>Every activity session scheduled on <see cref="Date"/> with its coverage (the dashboard's Activities count).</summary>
    public required ActivityCoverageDay Sessions { get; init; }
    public required IReadOnlyList<ActivityCardView> Activities { get; init; }
    public int InactiveCount { get; init; }
    public bool ShowInactive { get; init; }
}

/// <summary>Add / edit an activity. Name and description are required; the rest can be set now or later.</summary>
public sealed class ActivityFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Activity name")]
    [Required(ErrorMessage = "Enter the activity name.")]
    [StringLength(SchoolActivity.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Name { get; set; }

    [Display(Name = "Short description")]
    [Required(ErrorMessage = "Enter a short description.")]
    [StringLength(SchoolActivity.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    [Display(Name = "Category")]
    [StringLength(SchoolActivity.CategoryMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Category { get; set; }

    public string? Icon { get; set; } = ActivityStyles.DefaultIcon;
    public string? Color { get; set; } = ActivityStyles.DefaultColor;

    [Display(Name = "Department")]
    public int? DepartmentId { get; set; }

    [Display(Name = "Location")]
    [StringLength(SchoolActivity.LocationMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Location { get; set; }

    public bool IsActive { get; set; } = true;
    public List<int> StaffIds { get; set; } = [];
    public List<int> ClassIds { get; set; } = [];

    // Display
    public IReadOnlyList<Department> Departments { get; set; } = [];
    public IReadOnlyList<StaffOption> Staff { get; set; } = [];
    public IReadOnlyList<ClassOption> Classes { get; set; } = [];
    public IReadOnlyList<string> Categories { get; set; } = [];
    public bool IsEdit => Id is not null;

    public ActivityInput ToInput() => new(Name, Description, Category, Icon, Color, DepartmentId, Location, IsActive, StaffIds, ClassIds);
}

/// <summary>A new weekly meeting on the activity's management page.</summary>
public sealed class ActivityScheduleFormModel
{
    [Display(Name = "Day")]
    [Required(ErrorMessage = "Choose the day.")]
    public DayOfWeek? Day { get; set; }

    [Display(Name = "Start time")]
    [Required(ErrorMessage = "Enter the start time.")]
    public TimeOnly? Start { get; set; }

    [Display(Name = "End time")]
    [Required(ErrorMessage = "Enter the end time.")]
    public TimeOnly? End { get; set; }

    [StringLength(SchoolActivity.LocationMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Location { get; set; }

    [StringLength(SchoolActivitySchedule.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    public ActivityScheduleInput ToInput() => new(Day, Start, End, Location, Notes);
}

public sealed class ActivityDetailsViewModel
{
    public required ActivityDetails Activity { get; init; }
    public ActivityScheduleFormModel Meeting { get; init; } = new();
}
