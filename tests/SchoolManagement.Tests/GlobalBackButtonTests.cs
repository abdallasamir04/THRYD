using System.Net;
using System.Text.RegularExpressions;

namespace SchoolManagement.Tests;

/// <summary>
/// The global Back button is a permanent part of the shared header (_Layout): every full page renders exactly one,
/// beside the menu button, and no page can switch it off.
/// </summary>
public partial class HttpTests
{
    private static readonly Regex BackButton =
        new("<a class=\"btn btn--secondary btn--sm topbar__back\" href=\"(?<href>[^\"]*)\" data-back", RegexOptions.Compiled);

    [Theory]
    [InlineData("/")]
    [InlineData("/staff")]
    [InlineData("/staff/all")]
    [InlineData("/students/hub")]
    [InlineData("/students")]
    [InlineData("/classes")]
    [InlineData("/timetables")]
    [InlineData("/school-calendar")]
    [InlineData("/duties")]
    [InlineData("/duties/audit")]
    [InlineData("/floor-audit")]
    [InlineData("/floor-management")]
    [InlineData("/tasks")]
    [InlineData("/management")]
    [InlineData("/management/leadership")]
    [InlineData("/management/operation-team")]
    [InlineData("/settings")]
    [InlineData("/import-export")]
    public async Task Every_page_has_the_global_back_button_in_the_header(string url)
    {
        var response = await fx.Factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var header = html[html.IndexOf("<header class=\"topbar\">", StringComparison.Ordinal)..html.IndexOf("</header>", StringComparison.Ordinal)];
        var matches = BackButton.Matches(header);
        Assert.Single(matches);
        // The first control of the header (there is no menu button), before the trail.
        Assert.DoesNotContain("data-nav-toggle", header);
        Assert.True(matches[0].Index < header.IndexOf("class=\"trail\"", StringComparison.Ordinal));
        Assert.True(matches[0].Index < header.IndexOf("class=\"trail\"", StringComparison.Ordinal));
        // Its no-JavaScript fallback stays inside the application.
        Assert.StartsWith("/", matches[0].Groups["href"].Value);
        Assert.False(matches[0].Groups["href"].Value.StartsWith("//", StringComparison.Ordinal));
    }

    [Fact]
    public void No_view_hides_the_global_back_button()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SchoolManagement.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var views = Path.Combine(dir, "src", "SchoolManagement.Web", "Views");

        var offenders = Directory.EnumerateFiles(views, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"ViewData\[""BackButton""\]"))
            .Select(f => Path.GetRelativePath(views, f))
            .ToList();
        Assert.True(offenders.Count == 0, "Views must not switch off the global Back button: " + string.Join(", ", offenders));

        // The layout renders it in one of two branches (with or without a page's fixed fallback) — never behind a switch.
        var layout = File.ReadAllText(Path.Combine(views, "Shared", "_Layout.cshtml"));
        Assert.Equal(2, Regex.Matches(layout, "topbar__back\"").Count);
        Assert.Contains("@if (backHref is not null)", layout);
    }
}
