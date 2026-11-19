using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Tests;

/// <summary>
/// The student list's columns: #, Student name, REL, Second Language, Added (no Class column); REL and Sec Lang as printed
/// in the official name list (a dash when the list gives none); the name colour on the name, the row colour on the row.
/// </summary>
[Collection("db")]
public class StudentListColumnsTests(DbFixture fx)
{
    [Theory]
    [InlineData("F", "French")]
    [InlineData("g", "Deutsch")]
    [InlineData(" G ", "Deutsch")]
    [InlineData("Spanish", "Spanish")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Sec_Lang_codes_read_as_languages(string? code, string? name) => Assert.Equal(name, StudentMarks.LanguageName(code));

    [Fact]
    public async Task The_list_shows_REL_and_Second_Language_instead_of_Class_and_sorts_by_them()
    {
        var cls = await fx.NewClassAsync();
        var french = await fx.NewStudentAsync(cls);
        var deutsch = await fx.NewStudentAsync(cls);
        var none = await fx.NewStudentAsync(cls);
        await using (var db = fx.NewContext())
        {
            await db.Students.Where(s => s.Id == french.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Religion, "M").SetProperty(s => s.SecondLanguage, "F")
                .SetProperty(s => s.NameColor, "#0000FF"));
            await db.Students.Where(s => s.Id == deutsch.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.Religion, "CH").SetProperty(s => s.SecondLanguage, "G")
                .SetProperty(s => s.HighlightColor, "#FBE5D6"));
            // The database refuses anything but a #RRGGBB name colour.
            await Assert.ThrowsAnyAsync<Exception>(() => db.Students.Where(s => s.Id == none.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.NameColor, "blue")));
        }

        var client = fx.Factory.CreateClient();
        var list = WebUtility.HtmlDecode(await client.GetStringAsync($"/students?classId={cls}"));
        var heads = Regex.Matches(Regex.Match(list, "<thead>(.*?)</thead>", RegexOptions.Singleline).Groups[1].Value, "<th[^>]*>(.*?)</th>", RegexOptions.Singleline)
            .Select(m => Regex.Replace(m.Groups[1].Value, "<[^>]+>", "").Trim()).ToList();
        Assert.Equal(["#", "Student name", "REL", "Second Language", "Added", "Actions"], heads.Select(h => Regex.Replace(h, @"\s+", " ")).ToList());
        Assert.DoesNotContain("data-label=\"Class\"", list);
        Assert.DoesNotContain("badge--class", list);

        string Row(int id) => Regex.Match(list, $"<tr[^>]*>(?:(?!</tr>).)*?/students/profile/{id}\".*?</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("<td data-label=\"REL\" class=\"col-rel\">M</td>", Row(french.Id));
        Assert.Contains(">French</td>", Row(french.Id));
        Assert.Contains("student-name-color", Row(french.Id));                                    // name colour on the name
        Assert.Contains("--student-name:#0000FF", Row(french.Id));
        Assert.DoesNotContain("student-hl", Row(french.Id));                                     // not as a background
        Assert.Contains(">CH</td>", Row(deutsch.Id));
        Assert.Contains(">Deutsch</td>", Row(deutsch.Id));
        Assert.Contains("class=\"student-hl\" style=\"--student-hl:#FBE5D6\"", Row(deutsch.Id));  // row colour on the row
        Assert.DoesNotContain("student-name-color", Row(deutsch.Id));                            // not as a text colour
        Assert.Equal(2, Regex.Matches(Row(none.Id), ">—</td>").Count);                          // no REL, no language

        // Sorting: by REL and by Second Language, students without a value last.
        string Order(string sort, string dir)
        {
            var html = WebUtility.HtmlDecode(client.GetStringAsync($"/students?classId={cls}&sort={sort}&dir={dir}").Result);
            return string.Concat(Regex.Matches(html, @"/students/profile/(\d+)""").Select(m => m.Groups[1].Value == french.Id.ToString() ? "F" : m.Groups[1].Value == deutsch.Id.ToString() ? "D" : "N"));
        }
        Assert.Equal("DFN", Order("rel", "asc"));    // CH, M, —
        Assert.Equal("FDN", Order("rel", "desc"));   // M, CH, —
        Assert.Equal("FDN", Order("lang", "asc"));   // F, G, —
        Assert.Equal("DFN", Order("lang", "desc"));  // G, F, —

        // The class's own page shows the column too (some student there has a language).
        Assert.Contains("Second Language", WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/{cls}")));

        // The details show both; editing the name keeps them.
        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/students/details/{french.Id}"));
        Assert.Contains("French (F)", details);
        Assert.Contains("Name printed in #0000FF", details);
    }

    private static bool HasLanguageColumn(string html) => html.Contains("data-label=\"Second Language\"") || html.Contains(">Second Language<");

    [Fact]
    public async Task The_Second_Language_column_shows_only_when_a_student_in_the_results_has_one()
    {
        var client = fx.Factory.CreateClient();
        async Task<string> List(string query) => WebUtility.HtmlDecode(await client.GetStringAsync("/students?" + query));
        async Task SetLanguage(int id, string? code)
        {
            await using var db = fx.NewContext();
            await db.Students.Where(s => s.Id == id).ExecuteUpdateAsync(x => x.SetProperty(s => s.SecondLanguage, code));
        }

        // 1. No student in the results has a second language: no column, no header, no dashes for it.
        var cls = await fx.NewClassAsync();
        var a = await fx.NewStudentAsync(cls);
        var b = await fx.NewStudentAsync(cls);
        var c = await fx.NewStudentAsync(cls);
        var none = await List($"classId={cls}");
        Assert.False(HasLanguageColumn(none));
        Assert.DoesNotContain("col-lang", none);
        Assert.Contains("data-label=\"REL\"", none);                                  // the other columns stay
        Assert.DoesNotContain("Second Language", WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/{cls}")));
        // Ajax refreshes (search / filter / sort / page) get the same decision.
        var ajax = new HttpRequestMessage(HttpMethod.Get, $"/students?classId={cls}&sort=name&dir=desc");
        ajax.Headers.Add("X-Requested-With", "XMLHttpRequest");
        Assert.False(HasLanguageColumn(WebUtility.HtmlDecode(await (await client.SendAsync(ajax)).Content.ReadAsStringAsync())));

        // 2. One student has one: the column shows, the others get a dash.
        await SetLanguage(b.Id, "F");
        var one = await List($"classId={cls}");
        Assert.True(HasLanguageColumn(one));
        Assert.Equal(1, Regex.Matches(one, ">French</td>").Count);
        Assert.Equal(2, Regex.Matches(one, "class=\"col-lang\"[^>]*>—</td>").Count);

        // 3. Several students with different languages.
        await SetLanguage(c.Id, "G");
        var several = await List($"classId={cls}");
        Assert.Contains(">French</td>", several);
        Assert.Contains(">Deutsch</td>", several);
        Assert.Single(Regex.Matches(several, "class=\"col-lang\"[^>]*>—</td>"));

        // 4. Search within the class: the column follows the matching students.
        Assert.False(HasLanguageColumn(await List($"classId={cls}&search={Uri.EscapeDataString(a.Name)}")));
        Assert.True(HasLanguageColumn(await List($"classId={cls}&search={Uri.EscapeDataString(b.Name)}")));

        // The whole result set decides, not just the page: with 13 students sorted by language (the two with one first),
        // page 2 holds only students without one and still keeps the column, so it does not come and go while paging.
        for (var i = 0; i < 10; i++) await fx.NewStudentAsync(cls);
        var page2 = await List($"classId={cls}&pageSize=10&sort=lang&dir=asc&page=2");
        Assert.Equal(3, Regex.Matches(page2, @"/students/profile/\d+""").Count);
        Assert.True(HasLanguageColumn(page2));
        Assert.Equal(3, Regex.Matches(page2, "class=\"col-lang\"[^>]*>—</td>").Count);

        // The data is untouched by hiding the column.
        await using var check = fx.NewContext();
        Assert.Equal(["F", "G"], await check.Students.Where(s => s.SchoolClassId == cls && s.SecondLanguage != null).OrderBy(s => s.SecondLanguage).Select(s => s.SecondLanguage!).ToListAsync());
    }
}
