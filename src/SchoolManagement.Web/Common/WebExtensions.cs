using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.WebUtilities;

namespace SchoolManagement.Web.Common;

public static class WebExtensions
{
    /// <summary>True for fetch() calls made by wwwroot/js/app.js (they send X-Requested-With).</summary>
    public static bool IsAjax(this HttpRequest request) =>
        string.Equals(request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    /// <summary>The current path with the query string, changed by <paramref name="changes"/> (null or "" removes a key).</summary>
    public static string WithQuery(this HttpRequest request, params (string Key, object? Value)[] changes)
    {
        var values = request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in changes)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) values.Remove(key);
            else values[key] = text;
        }
        var path = request.PathBase.Add(request.Path).Value ?? "/";
        var kept = values.Where(v => !string.IsNullOrEmpty(v.Value)).ToDictionary(v => v.Key, v => v.Value);
        return kept.Count == 0 ? path : QueryHelpers.AddQueryString(path, kept);
    }
}

/// <summary>Small presentation helpers shared by the Razor views.</summary>
public static class Ui
{
    private static readonly string[] Tones = ["violet", "blue", "teal", "amber", "rose", "indigo", "green", "cyan"];

    /// <summary>An icon from the inline sprite (Views/Shared/_Icons.cshtml).</summary>
    public static IHtmlContent Icon(string name, string? css = null) =>
        new HtmlString($"<svg class=\"icon{(css is null ? "" : " " + HtmlEncoder.Default.Encode(css))}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#i-{HtmlEncoder.Default.Encode(name)}\"></use></svg>");

    public static string Initials(string? name)
    {
        var parts = TextNormalizer.CleanText(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var first = char.ToUpperInvariant(parts[0][0]);
        return parts.Length == 1 ? first.ToString() : $"{first}{char.ToUpperInvariant(parts[^1][0])}";
    }

    /// <summary>A stable avatar colour for a name.</summary>
    public static string Tone(string? name)
    {
        var hash = 0;
        foreach (var ch in name ?? "") hash = unchecked(hash * 31 + ch);
        return Tones[Math.Abs(hash % Tones.Length)];
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string DateTime(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    public static string Ago(DateTime utc)
    {
        var span = System.DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} d ago";
        return Date(utc);
    }

    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Plural(int count, string one, string many) => $"{Count(count)} {(count == 1 ? one : many)}";
}

public sealed record PagerModel(int Page, int TotalPages, int TotalCount, int FirstItem, int LastItem, int PageSize, string Noun)
{
    public static PagerModel From<T>(PagedResult<T> r, string noun) =>
        new(r.Page, r.TotalPages, r.TotalCount, r.FirstItem, r.LastItem, r.PageSize, noun);

    /// <summary>Page numbers to show, with 0 for a gap: 1 … 4 5 6 … 20.</summary>
    public IEnumerable<int> Window()
    {
        var pages = new SortedSet<int> { 1, TotalPages };
        for (var p = Page - 1; p <= Page + 1; p++) if (p >= 1 && p <= TotalPages) pages.Add(p);
        var last = 0;
        foreach (var p in pages)
        {
            if (last > 0 && p - last > 1) yield return 0;
            yield return p;
            last = p;
        }
    }
}

public static class SortHelper
{
    /// <summary>A column header link that sorts by <paramref name="key"/>; a second click reverses the order.</summary>
    public static Microsoft.AspNetCore.Html.IHtmlContent SortLink(HttpRequest request, string key, string label, string? currentSort, string? currentDir, string defaultSort = "name")
    {
        var sorted = string.Equals(currentSort ?? defaultSort, key, StringComparison.OrdinalIgnoreCase);
        var desc = string.Equals(currentDir, "desc", StringComparison.OrdinalIgnoreCase);
        var nextDir = sorted && !desc ? "desc" : "asc";
        var href = request.WithQuery(("sort", key), ("dir", nextDir), ("page", null));
        var icon = !sorted ? "arrow-up-down" : desc ? "arrow-down" : "arrow-up";
        var aria = sorted ? (desc ? "descending" : "ascending") : "none";
        var enc = System.Text.Encodings.Web.HtmlEncoder.Default;
        return new Microsoft.AspNetCore.Html.HtmlString(
            $"<a class=\"sort-link{(sorted ? " is-sorted" : "")}\" href=\"{enc.Encode(href)}\" data-list-link data-sort=\"{aria}\">{enc.Encode(label)}{Ui.Icon(icon).ToHtml()}</a>");
    }

    private static string ToHtml(this Microsoft.AspNetCore.Html.IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }
}
