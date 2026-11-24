using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.Common;

/// <summary>
/// Route constraint "dutytype": the segment is the address (slug) of a Main Duty in the catalog — morning, midday, dismissal
/// and any duty added with Add Duty. Replaces the former fixed regex, so an added duty's pages (/duties/{slug}/schedule,
/// /duties/{slug}/{sub-duty}, its coverage report) work like the original three.
/// </summary>
public sealed class DutyTypeRouteConstraint : IRouteConstraint
{
    public const string Name = "dutytype";

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
        values.TryGetValue(routeKey, out var value) && DutyComponents.IsTypeSlug(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
}
