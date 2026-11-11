namespace SchoolManagement.Web.ViewModels;

/// <summary>
/// The shared PDF download button (Views/Shared/_Download.cshtml). <paramref name="Path"/> is the export
/// endpoint without a query string: the page's current query (filters, date, week) is added to it.
/// </summary>
public sealed record DownloadLink(string Path, string Label = "Download Report", bool Small = false)
{
    public static DownloadLink Timetable(string path, bool small = false) => new(path, "Download Timetable", small);
    public static DownloadLink Report(string path, bool small = false) => new(path, "Download Report", small);
}
