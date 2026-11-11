using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

public sealed class BelongingsPageViewModel
{
    public required IReadOnlyList<YearView> Years { get; init; }
    public YearView? Year { get; init; }
    public QuarterView? Quarter { get; init; }
    public WeekInfo? Week { get; init; }
    public DateOnly? Date { get; init; }
    public string? Grade { get; init; }
    public IReadOnlyList<StudentOption> Students { get; init; } = [];
    public StudentOption? Student { get; init; }

    public DayView? Day { get; init; }
    public PeriodAnalytics<DayPoint>? WeekStats { get; init; }
    public PeriodAnalytics<WeekPoint>? QuarterStats { get; init; }
    public IReadOnlyList<StudentWeekRow> GradeWeek { get; init; } = [];
    public IReadOnlyList<(int Id, string Name, IReadOnlyList<string> Subjects)> Departments { get; init; } = [];

    /// <summary>Query string of the current selection (forms return here after saving).</summary>
    public string Query(params (string Key, object? Value)[] changes)
    {
        var values = new Dictionary<string, string?>
        {
            ["yearId"] = Year?.Id.ToString(), ["quarterId"] = Quarter?.Id.ToString(), ["week"] = Week?.Number.ToString(),
            ["date"] = Date is { } d ? Common.SchoolWeek.Iso(d) : null, ["grade"] = Grade, ["studentId"] = Student?.Id.ToString(),
        };
        foreach (var (k, v) in changes) values[k] = v?.ToString();
        return "/belongings?" + string.Join("&", values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!)}"));
    }
}

public sealed class QuarterFormModel
{
    public int Id { get; set; }

    [Display(Name = "Quarter name")]
    [Required(ErrorMessage = "Enter the quarter name.")]
    [StringLength(AcademicQuarter.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Name { get; set; } = "";

    [Display(Name = "Start date")]
    [Required(ErrorMessage = "Enter the start date.")]
    public DateOnly? StartDate { get; set; }

    [Display(Name = "End date")]
    [Required(ErrorMessage = "Enter the end date.")]
    public DateOnly? EndDate { get; set; }

    public QuarterView? Quarter { get; set; }
}

public sealed class PersonalFormModel
{
    public int StudentId { get; set; }
    public DateOnly Date { get; set; }
    public string? Grade { get; set; }
    public ItemStatus? Jacket { get; set; }
    public ItemStatus? SchoolBag { get; set; }
    public ItemStatus? PencilCase { get; set; }
    public ItemStatus? WaterFlask { get; set; }
    public ItemStatus? LunchBox { get; set; }

    [StringLength(BelongingsRecord.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    public PersonalInput ToInput() => new(StudentId, Date, Grade, Jacket, SchoolBag, PencilCase, WaterFlask, LunchBox, Notes);
}

public sealed class MaterialFormModel
{
    public int StudentId { get; set; }
    public DateOnly Date { get; set; }
    public string? Grade { get; set; }
    public int? DepartmentId { get; set; }
    public string? Subject { get; set; }
    public ItemStatus? Book { get; set; }
    public ItemStatus? CopyBook { get; set; }

    public MaterialInput ToInput() => new(StudentId, Date, Grade, DepartmentId, Subject, Book, CopyBook);
}
