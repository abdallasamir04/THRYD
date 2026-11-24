using Microsoft.AspNetCore.Html;

namespace SchoolManagement.Web.Common;

/// <summary>
/// The one class-capacity rule used wherever a class's enrolment is shown against its capacity (Classes cards and list,
/// class page, student pages, dashboard chart, roster PDF / Excel): no warning below the capacity, a red indicator once the
/// class reaches or exceeds it. There is no "near capacity" state. Counts and capacities are the stored, live values, so
/// the indicator follows every student addition, removal and transfer on the next page load or list refresh.
/// </summary>
public static class ClassCapacity
{
    /// <summary>True when the class has reached or exceeded its capacity (e.g. 25 of 25, 26 of 25).</summary>
    public static bool IsFull(int students, int capacity) => capacity > 0 && students >= capacity;

    /// <summary>The red "Full" badge for a class at or above capacity; nothing otherwise.</summary>
    public static IHtmlContent Badge(int students, int capacity) =>
        IsFull(students, capacity)
            ? new HtmlString($"<span class=\"badge badge--error\" title=\"{students} students for a capacity of {capacity}\">Full</span>")
            : HtmlString.Empty;
}
