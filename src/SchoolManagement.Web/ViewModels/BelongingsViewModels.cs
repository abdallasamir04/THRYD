using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

/// <summary>One student's row on a Personal Belongings class page (also redrawn after each click).</summary>
public sealed record PersonalRowModel(PersonalStudentRow Row, DateOnly Date, bool CanRecord);

/// <summary>One student's row on an Academic Belongings lesson page (also redrawn after each click).</summary>
public sealed record AcademicRowModel(AcademicStudentRow Row, int SessionId, DateOnly Date, bool CanRecord);

public sealed record AcademicDailyViewModel(DateOnly Date, bool IsToday, IReadOnlyList<AcademicClassCard> Cards);

public sealed record PersonalHistoryViewModel(PersonalHistory History, HistoryOptions Options, BelongingsQuery Query);

public sealed record AcademicHistoryViewModel(AcademicHistory History, HistoryOptions Options, BelongingsQuery Query);

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

    /// <summary>The quarter as printed in the School Calendar (optional; groups the calendar's months only).</summary>
    [Display(Name = "School calendar: first day")]
    public DateOnly? CalendarStartDate { get; set; }

    [Display(Name = "School calendar: last day")]
    public DateOnly? CalendarEndDate { get; set; }

    public QuarterView? Quarter { get; set; }
}
