using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Common;

/// <summary>
/// The one place that turns a <see cref="CalendarColor"/> into markup: a CSS class whose shades (light and dark
/// theme) live in css/school-calendar.css, and a plain colour name for lists. Names describe the colour only:
/// the printed calendar has no legend, so no colour is given a meaning.
/// </summary>
public static class CalendarColors
{
    public static readonly IReadOnlyList<CalendarColor> All = Enum.GetValues<CalendarColor>();

    public static string Css(CalendarColor c) => c switch
    {
        CalendarColor.None => "",
        CalendarColor.DarkTeal => "sc-c-dark-teal",
        CalendarColor.LightGreen => "sc-c-light-green",
        _ => "sc-c-" + c.ToString().ToLowerInvariant(),
    };

    public static string Name(CalendarColor c) => c switch
    {
        CalendarColor.None => "No colour",
        CalendarColor.DarkTeal => "Dark teal",
        CalendarColor.LightGreen => "Light green",
        _ => c.ToString(),
    };
}
