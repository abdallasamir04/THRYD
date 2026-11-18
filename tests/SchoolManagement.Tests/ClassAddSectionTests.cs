using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Add Class asks only for the grade (and capacity / active), from every entry point: the server gives the class the
/// next section letter of that grade (A, B → C; a grade without classes → A), each grade counted on its own, never a
/// duplicate name, and never a section taken from the request.
/// </summary>
[Collection("db")]
public sealed partial class ClassAddSectionTests(DbFixture fx)
{
    private static string Token(string html) => TokenPattern().Match(html).Groups[1].Value;

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = token;
        var r = new HttpRequestMessage(HttpMethod.Post, "/classes/create") { Content = new FormUrlEncodedContent(fields) };
        r.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(r);
    }

    private async Task<List<string>> NamesAsync()
    {
        await using var db = fx.NewContext();
        return await db.Classes.AsNoTracking().Select(c => c.Name).ToListAsync();
    }

    [Fact]
    public async Task No_Add_Class_form_has_a_Section_field()
    {
        var client = fx.Factory.CreateClient();
        var urls = new List<string> { "/classes/create" };
        urls.AddRange(ClassStages.All.SelectMany(s => s.Groups).Select(g => $"/classes/create?group={g.Slug}"));
        foreach (var url in urls)
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(url));
            Assert.DoesNotContain("name=\"Section\"", html);
            Assert.DoesNotContain("placeholder=\"Automatic\"", html);
            Assert.DoesNotContain("Leave empty to use the next letter", html);
            Assert.DoesNotContain(">Section<", html);
            // What stays: Grade, Class capacity, Active.
            Assert.Contains(">Grade <span class=\"req\"", html);
            Assert.Contains("name=\"Capacity\"", html);
            Assert.Contains("Active — can receive new students", html);
        }

        // Every Add class button on the Classes pages opens one of these forms.
        foreach (var page in new[] { "/classes", "/classes/all" }.Concat(ClassStages.All.Select(s => s.Href)))
            foreach (Match m in Regex.Matches(await client.GetStringAsync(page), "href=\"(/classes/create[^\"]*)\""))
                Assert.Contains(Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), "&grade=\\d+$", ""), urls);
    }

    [Fact]
    public async Task The_next_section_is_per_grade_and_starts_at_A()
    {
        var client = fx.Factory.CreateClient();
        var created = new List<string>();
        try
        {
            // The all-grades form (Classes → All classes): every stage, each grade with its next class name.
            var form = WebUtility.HtmlDecode(await client.GetStringAsync("/classes/create"));
            foreach (var stage in ClassStages.All) Assert.Contains($"<optgroup label=\"{stage.Title}\">", form);
            var names = await NamesAsync();
            var kg2Next = $"KG2{ClassNumbering.NextSection(names, "KG", 2)}";
            Assert.Contains($"<option value=\"kg2:2\">KG2 — next class {kg2Next}</option>", form);
            var token = Token(form);

            // A grade without classes (High School's next new grade) starts at A, then B — while KG2's next letter is unchanged.
            var newGrade = ClassStages.AddableGrades(ClassStages.FindGroup("high")!.Value.Group, names).Max();
            Assert.DoesNotContain(names, n => ClassStages.GradeOf(n) == new ClassGrade("G", newGrade));
            foreach (var expected in new[] { $"G{newGrade}A", $"G{newGrade}B" })
            {
                var r = await PostAsync(client, token, new() { ["GradeKey"] = $"high:{newGrade}", ["Capacity"] = "22", ["IsActive"] = "true", ["Section"] = "Q" });
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.Contains($"Class {expected} was added.", await r.Content.ReadAsStringAsync());
                created.Add(expected);
            }
            Assert.Equal(kg2Next, $"KG2{ClassNumbering.NextSection(await NamesAsync(), "KG", 2)}");

            // A grade with A and B gets C (the new grade above now has A and B), from a group form too.
            var groupForm = WebUtility.HtmlDecode(await client.GetStringAsync($"/classes/create?group=high&grade={newGrade}"));
            Assert.Contains($"next class G{newGrade}C", groupForm);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, token, new()
                { ["Group"] = "high", ["GradeNumber"] = newGrade.ToString(), ["Capacity"] = "20", ["IsActive"] = "true" })).StatusCode);
            created.Add($"G{newGrade}C");

            // KG2 from the all-grades form gets its own next letter, unaffected by the High School classes.
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, token, new() { ["GradeKey"] = "kg2:2", ["Capacity"] = "18", ["IsActive"] = "false" })).StatusCode);
            created.Add(kg2Next);

            await using (var db = fx.NewContext())
            {
                var rows = await db.Classes.AsNoTracking().Where(c => created.Contains(c.Name)).ToDictionaryAsync(c => c.Name);
                Assert.Equal(created.Order(), rows.Keys.Order());
                Assert.Equal((22, true), (rows[$"G{newGrade}A"].Capacity, rows[$"G{newGrade}A"].IsActive));
                Assert.Equal((18, false), (rows[kg2Next].Capacity, rows[kg2Next].IsActive));
                // Every class name is unique.
                var all = await db.Classes.AsNoTracking().Select(c => c.Name).ToListAsync();
                Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }

            // No grade, an unknown group, or a grade outside its group: refused, nothing created.
            var count = (await NamesAsync()).Count;
            foreach (var key in new[] { "", "nope:1", "kg1:2", "kg1" })
            {
                var bad = await PostAsync(client, token, new() { ["GradeKey"] = key, ["Capacity"] = "20", ["IsActive"] = "true" });
                Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
                Assert.Contains("Choose a grade.", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
            }
            Assert.Equal(count, (await NamesAsync()).Count);
        }
        finally
        {
            // Other tests check the exact class order: remove only the classes this test added.
            await using var db = fx.NewContext();
            await db.Classes.Where(c => created.Contains(c.Name)).ExecuteDeleteAsync();
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
