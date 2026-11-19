using System.Net;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Tests;

/// <summary>
/// The official name list's marks on students: the mark after the name ("*", "(*)", "#") and the row's highlight colour
/// are stored beside the name, shown next to it (a star for * and (*)) and printed with the name in the exports.
/// </summary>
[Collection("db")]
public class StudentMarksTests(DbFixture fx)
{
    [Theory]
    [InlineData("*", "*", true)]
    [InlineData(" (*)", "(*)", true)]
    [InlineData(" *", "*", true)]
    [InlineData(" #", "#", false)]
    [InlineData(null, null, false)]
    public void Marks_keep_the_printed_name_and_tell_a_star(string? marker, string? symbol, bool star)
    {
        Assert.Equal(symbol, StudentMarks.Symbol(marker));
        Assert.Equal(star, StudentMarks.IsStar(marker));
        Assert.Equal("Mohamed Ashraf Mohamed" + marker, StudentMarks.Printed("Mohamed Ashraf Mohamed", marker));
    }

    [Theory]
    [InlineData("#FBE5D6", "#FBE5D6")]
    [InlineData("#a8d08d", "#A8D08D")]
    [InlineData("red", null)]
    [InlineData("#FBE5D6;background:url(x)", null)]
    [InlineData(null, null)]
    public void Only_hex_colours_reach_a_style_attribute(string? color, string? safe) => Assert.Equal(safe, StudentMarks.SafeColor(color));

    [Fact]
    public async Task The_star_the_other_mark_and_the_highlight_show_on_the_lists_and_the_profile()
    {
        var cls = await fx.NewClassAsync();
        var u = DbFixture.Unique();
        var star = await fx.NewStudentAsync(cls);
        var hash = await fx.NewStudentAsync(cls);
        var plain = await fx.NewStudentAsync(cls);
        await using (var db = fx.NewContext())
        {
            await db.Students.Where(s => s.Id == star.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.NameMarker, " (*)").SetProperty(s => s.HighlightColor, "#FBE5D6"));
            await db.Students.Where(s => s.Id == hash.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.NameMarker, " #"));
            // The database refuses anything but a #RRGGBB colour.
            await Assert.ThrowsAnyAsync<Exception>(() => db.Students.Where(s => s.Id == plain.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.HighlightColor, "red")));
        }

        var client = fx.Factory.CreateClient();
        var list = WebUtility.HtmlDecode(await client.GetStringAsync($"/students?classId={cls}"));
        Assert.Contains($"Marked “(*)” in the official name list: {star.Name} (*)", list);       // a star, with the printed name
        Assert.Contains("student-mark--star", list);
        Assert.Contains("Marked “#” in the official name list", list);                          // another mark shows as itself
        Assert.Contains(">#</span>", list);
        Assert.Contains("class=\"student-hl\" style=\"--student-hl:#FBE5D6\"", list);          // the highlighted row
        Assert.DoesNotContain("student-mark--hl", list);                                        // no colour dot beside the name
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(list, "class=\"student-hl\"").Count);
        // The name itself is stored and shown without the mark (no duplicate star).
        Assert.Contains($">{star.Name}</a>", list);

        var profile = WebUtility.HtmlDecode(await client.GetStringAsync($"/students/profile/{star.Id}"));
        Assert.Contains("student-mark--star", profile);
        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/students/details/{star.Id}"));
        Assert.Contains($"Printed as “{star.Name} (*)”.", details);
        Assert.Contains("Row highlighted", details);

        // Search still finds the student by name.
        Assert.Contains(star.Name, WebUtility.HtmlDecode(await client.GetStringAsync($"/students?search={Uri.EscapeDataString(star.Name)}")));
        _ = u;
    }
}
