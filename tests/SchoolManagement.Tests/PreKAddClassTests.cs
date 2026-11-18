using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>KG Stages → Pre-K has the same Add class button as KG1 and KG2, opening the existing form with Pre-K selected.</summary>
[Collection("db")]
public sealed partial class PreKAddClassTests(DbFixture fx)
{
    [Fact]
    public void Pre_K_sections_follow_the_KG_lettering_with_a_bare_PreK_as_A()
    {
        Assert.Equal("A", ClassNumbering.NextSection([], ClassStages.PreKCode, 0));
        Assert.Equal("B", ClassNumbering.NextSection(["PreK", "KG1A", "KG1B"], ClassStages.PreKCode, 0));
        Assert.Equal("C", ClassNumbering.NextSection(["PreK", "PreKB"], ClassStages.PreKCode, 0));
        Assert.Equal("B", ClassNumbering.PreKSection("PreK B"));
        Assert.Equal("A", ClassNumbering.PreKSection("Pre-K"));
        Assert.Null(ClassNumbering.PreKSection("KG1A"));
        // KG numbering is unchanged.
        Assert.Equal("C", ClassNumbering.NextSection(["KG1A", "KG1B", "PreK"], "KG", 1));
    }

    [Fact]
    public async Task Pre_K_header_has_Add_class_and_it_creates_the_next_Pre_K_class()
    {
        var client = fx.Factory.CreateClient();
        var kg = await client.GetStringAsync("/classes/stages/kg");

        // Same button as KG1 / KG2, right after Open Pre-K in the Pre-K header.
        foreach (var (slug, title) in new[] { ("prek", "Pre-K"), ("kg1", "KG1"), ("kg2", "KG2") })
            Assert.Contains($"<a class=\"btn btn--primary btn--sm\" href=\"/classes/create?group={slug}\" data-modal aria-label=\"Add class to {title}\">", kg);
        var prekActions = Regex.Match(kg, "id=\"block-pre-k\".*?<div class=\"stage-block__actions\">(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Matches("Open Pre-K.*?</a>\\s*<a class=\"btn btn--primary btn--sm\" href=\"/classes/create\\?group=prek\"", prekActions);

        // The existing Add class form, with Pre-K selected and the next name shown (worked out from the classes there now).
        List<string> names;
        await using (var db = fx.NewContext()) names = await db.Classes.AsNoTracking().Select(c => c.Name).ToListAsync();
        var taken = names.Select(ClassNumbering.PreKSection).OfType<string>().ToList();
        var expected = ClassNumbering.PreKName(ClassNumbering.NextSection(names, ClassStages.PreKCode, 0)!);
        var form = WebUtility.HtmlDecode(await client.GetStringAsync("/classes/create?group=prek"));
        Assert.Contains("name=\"Group\" value=\"prek\"", form);
        Assert.Contains($"<option value=\"0\" selected=\"selected\">Pre-K — next class {expected}</option>", form);
        var token = TokenPattern().Match(form).Groups[1].Value;

        try
        {
            HttpRequestMessage Post(string? section)
            {
                var r = new HttpRequestMessage(HttpMethod.Post, "/classes/create")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["__RequestVerificationToken"] = token, ["Group"] = "prek", ["GradeNumber"] = "0", ["Section"] = section ?? "",
                        ["Capacity"] = "20", ["IsActive"] = "true",
                    }),
                };
                r.Headers.Add("X-Requested-With", "XMLHttpRequest");
                return r;
            }

            // A section already used in Pre-K (a bare "PreK" is A) is refused as a duplicate; nothing is created.
            if (taken.Count > 0)
            {
                var count = names.Count;
                Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(Post(taken[0]))).StatusCode);
                await using var db = fx.NewContext();
                Assert.Equal(count, await db.Classes.CountAsync());
            }

            var saved = await client.SendAsync(Post(null));
            var body = await saved.Content.ReadAsStringAsync();
            Assert.True(saved.StatusCode == HttpStatusCode.OK, body);
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean());

            await using (var db = fx.NewContext())
            {
                var created = await db.Classes.AsNoTracking().SingleAsync(c => c.Name == expected);
                Assert.Equal((20, true), (created.Capacity, created.IsActive));
                // Sorted with Pre-K: after every other Pre-K class, before every KG / Grade class.
                var order = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).Select(c => c.Name).ToListAsync();
                var at = order.IndexOf(expected);
                Assert.All(order.Take(at), n => Assert.NotNull(ClassNumbering.PreKSection(n)));
                Assert.All(order.Skip(at + 1).Where(n => ClassNumbering.Parse(n) is not null), n => Assert.Null(ClassNumbering.PreKSection(n)));
            }
            Assert.Equal("prek", ClassStages.Locate(expected)?.Group.Slug);
            var page = await client.GetStringAsync("/classes/stages/kg/prek");
            Assert.Contains(expected, page);
            Assert.DoesNotContain("rather than added here", page);
        }
        finally
        {
            // Other tests check the exact class order: remove only the class this test added.
            await using var db = fx.NewContext();
            await db.Classes.Where(c => c.Name == expected && !names.Contains(c.Name)).ExecuteDeleteAsync();
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
