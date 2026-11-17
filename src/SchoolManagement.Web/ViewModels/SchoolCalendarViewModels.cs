using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

/// <summary>School Calendar page: the year's calendar and, below it, the academic years and their 8-week quarters.</summary>
public sealed record SchoolCalendarIndexModel(SchoolCalendarPage Calendar, IReadOnlyList<YearView> Years, string SchoolName);

/// <summary>Add / edit one line of the School Calendar.</summary>
public sealed class CalendarEventFormModel
{
    public int? Id { get; set; }
    public int YearId { get; set; }

    [Display(Name = "Event name")]
    [Required(ErrorMessage = "Enter the event name.")]
    [StringLength(SchoolCalendarEvent.TitleMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Title { get; set; } = "";

    [Display(Name = "Date (or first day)")]
    public DateOnly? StartDate { get; set; }

    [Display(Name = "Last day")]
    public DateOnly? EndDate { get; set; }

    [Display(Name = "Date as printed")]
    [StringLength(SchoolCalendarEvent.DateLabelMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? DateLabel { get; set; }

    [Display(Name = "Listed under")]
    public DateOnly? ListMonth { get; set; }

    public CalendarColor Color { get; set; }

    [Display(Name = "Coloured part only")]
    [StringLength(SchoolCalendarEvent.AccentMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? AccentText { get; set; }

    public bool NeedsConfirmation { get; set; }
    public bool IsHidden { get; set; }

    [Display(Name = "Order in the month")]
    [Range(0, 9999, ErrorMessage = "Use a number from 0 to 9999.")]
    public int SortOrder { get; set; }

    [StringLength(SchoolCalendarEvent.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    // Display only
    public string YearName { get; set; } = "";
    public IReadOnlyList<DateOnly> Months { get; set; } = [];

    public CalendarEventInput ToInput() =>
        new(Title, StartDate, EndDate, DateLabel, ListMonth, Color, AccentText, NeedsConfirmation, Notes, SortOrder, IsHidden);

    public static CalendarEventFormModel From(CalendarEventView e) => new()
    {
        Id = e.Id, YearId = e.YearId, Title = e.Title, StartDate = e.StartDate, EndDate = e.EndDate,
        DateLabel = e.DateLabel, ListMonth = e.ListMonth, Color = e.Color, AccentText = e.AccentText,
        NeedsConfirmation = e.NeedsConfirmation, IsHidden = e.IsHidden, SortOrder = e.SortOrder, Notes = e.Notes,
    };
}
