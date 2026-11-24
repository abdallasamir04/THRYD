using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Timetables;

namespace SchoolManagement.Web.ViewModels;

/// <summary>Timetables → Create Table: the class to create an empty timetable for.</summary>
public sealed class CreateTableViewModel
{
    public int? ClassId { get; set; }
    public IReadOnlyList<Services.ClassTableOption> Classes { get; set; } = [];

    /// <summary>The replacement plan's fingerprint, sent back by "Replace timetable" (a stale or missing one asks again).</summary>
    public string? Confirmation { get; set; }

    /// <summary>Set when the chosen class already has a timetable: what replacing it would do. Nothing has changed yet.</summary>
    public Services.PeriodPlan? Plan { get; set; }

    /// <summary>Where a class's timetable periods are edited (add / remove slots, names, start and end times).</summary>
    public static string EditorHref(int classId) => $"/timetable/periods?classId={classId}";

    /// <summary>The class's timetable (its lessons by day and period).</summary>
    public static string TimetableHref(int classId) => $"/timetable/classes/{classId}";
}

/// <summary>Academics → Timetables: the categories, the current selection and its timetables.</summary>
public sealed class TimetableCenterViewModel
{
    public required IReadOnlyList<ITimetableSource> Sources { get; init; }
    public required ITimetableSource Source { get; init; }
    public required IReadOnlyDictionary<string, int> Counts { get; init; }
    public required TimetableRequest Request { get; init; }
    public required IReadOnlyList<TimetableItem> Items { get; init; }
    public required TimetableFilterOptions Options { get; init; }
    public required IReadOnlyList<YearView> Years { get; init; }
    public YearView? Year { get; init; }
    public string? RangeNote { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }

    public bool Has(TimetableFilters f) => Source.Filters.HasFlag(f);

    public string? DepartmentName => Request.DepartmentId is { } d ? Options.Departments.FirstOrDefault(x => x.Id == d).Name : null;

    /// <summary>The current selection as a query string on <paramref name="path"/>, with changes (null removes a key).</summary>
    public string Query(string path, params (string Key, object? Value)[] changes)
    {
        var r = Request;
        var values = new Dictionary<string, string?>
        {
            ["category"] = Source.Key, ["yearId"] = r.YearId?.ToString(), ["from"] = From, ["to"] = To,
            ["day"] = r.Day?.ToString().ToLowerInvariant(),
            ["departmentId"] = Has(TimetableFilters.Department) ? r.DepartmentId?.ToString() : null,
            ["subject"] = Has(TimetableFilters.Subject) ? r.Subject : null,
            ["teacherId"] = Has(TimetableFilters.Teacher) ? r.TeacherId?.ToString() : null,
            ["classId"] = Has(TimetableFilters.Class) ? r.ClassId?.ToString() : null,
        };
        foreach (var (k, v) in changes) values[k] = v?.ToString();
        var qs = string.Join("&", values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!)}"));
        return path + (qs.Length == 0 ? "" : "?" + qs);
    }

    public string WeeksLabel => Request.Weeks.Count == 1
        ? $"Week of {SchoolWeek.Label(Request.FirstWeek)}"
        : $"{Request.Weeks.Count} weeks · {SchoolWeek.Label(Request.FirstWeek)} to {SchoolWeek.Label(Request.Weeks[^1])}";
}
