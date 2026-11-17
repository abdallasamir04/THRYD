namespace SchoolManagement.Web.Entities;

/// <summary>
/// The colour families of the school's printed teachers' calendar. They reproduce how a date or an event line
/// looks, nothing more: the calendar has no official legend, so no colour carries a meaning in code.
/// Shades live in css/school-calendar.css (light and dark theme). Numbers are stored.
/// </summary>
public enum CalendarColor
{
    None = 0,
    Yellow = 1,
    DarkTeal = 2,
    Green = 3,
    LightGreen = 4,
    Blue = 5,
    Orange = 6,
    Red = 7,
    Pink = 8,
    Gray = 9,
}

/// <summary>
/// One line of the School Calendar: an event, deadline or holiday listed under a month. A one-day event has
/// only <see cref="StartDate"/>; a range has both dates and may cross months (it is listed once, under
/// <see cref="ListMonth"/>, and shown as "continued" in the other months it covers). An event whose date the
/// school has not confirmed keeps no date at all and is listed under its month, flagged for confirmation.
/// </summary>
public class SchoolCalendarEvent : IAuditable
{
    public const int TitleMaxLength = 300;
    public const int DateLabelMaxLength = 30;
    public const int AccentMaxLength = 100;
    public const int NotesMaxLength = 1000;

    public int Id { get; set; }
    public int AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;

    /// <summary>The event name exactly as the school wrote it (English).</summary>
    public string Title { get; set; } = "";

    public DateOnly? StartDate { get; set; }
    /// <summary>Last day of a range; null for a one-day event.</summary>
    public DateOnly? EndDate { get; set; }
    /// <summary>The date as printed when it is not a plain day or range (e.g. "7/8", "22/27/28"); shown instead of the computed date.</summary>
    public string? DateLabel { get; set; }
    /// <summary>First day of the month whose card lists the event (e.g. a 31 Jan – 4 Feb break listed under February).</summary>
    public DateOnly ListMonth { get; set; }

    public CalendarColor Color { get; set; }
    /// <summary>When set, only this part of the title takes <see cref="Color"/> (e.g. "Fire Drill" in "Fire Drill / Q1 Project Submission").</summary>
    public string? AccentText { get; set; }

    /// <summary>The date or wording still has to be confirmed by the school.</summary>
    public bool NeedsConfirmation { get; set; }
    public string? Notes { get; set; }
    /// <summary>Order within the month, as in the printed calendar.</summary>
    public int SortOrder { get; set; }
    public bool IsHidden { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public DateOnly? LastDate => EndDate ?? StartDate;
    public bool Covers(DateOnly d) => StartDate is { } s && d >= s && d <= (EndDate ?? s);
}

/// <summary>A coloured date in a month grid of the School Calendar (one colour per date and year).</summary>
public class SchoolCalendarMark
{
    public int Id { get; set; }
    public int AcademicYearId { get; set; }
    public AcademicYear AcademicYear { get; set; } = null!;
    public DateOnly Date { get; set; }
    public CalendarColor Color { get; set; }
}
