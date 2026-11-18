using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Tests;

/// <summary>
/// Default class capacity (20, never a limit) and the dashboard's Students by class rings: blue occupied up to the
/// capacity, yellow seats left, red students over it, with totals summed per class (one class never offsets another).
/// </summary>
[Collection("db")]
public sealed class DashboardCapacityTests(DbFixture fx)
{
    [Fact]
    public void Segments_and_totals_follow_the_three_colour_rule()
    {
        var open = new ClassDistribution(1, "KG1A", true, 15, 20);
        Assert.Equal((15, 5, 0, false, 75), (open.Occupied, open.Available, open.Over, open.IsFull, open.Utilization));
        var full = new ClassDistribution(2, "G9A", true, 20, 20);
        Assert.Equal((20, 0, 0, true, 100), (full.Occupied, full.Available, full.Over, full.IsFull, full.Utilization));
        var over = new ClassDistribution(3, "G6B", true, 23, 20);
        Assert.Equal((20, 0, 3, false, 115), (over.Occupied, over.Available, over.Over, over.IsFull, over.Utilization));
        Assert.Equal(23, over.Count); // the real enrolment is never capped

        // 18 / 20 and 23 / 20: 2 seats available and 3 over capacity, not "1 net over".
        var totals = CapacityTotals.Of([new ClassDistribution(4, "A", true, 18, 20), over]);
        Assert.Equal(new CapacityTotals(41, 40, 2, 3), totals);
    }

    [Fact]
    public async Task New_classes_default_to_20_and_a_chosen_capacity_is_kept()
    {
        Assert.Equal(20, SchoolClass.DefaultCapacity);
        Assert.Equal(20, new SchoolClass().Capacity);
        Assert.Equal(20, new ClassFormModel().Capacity);

        int? plain = null, custom = null;
        try
        {
            await using (var db = fx.NewContext())
            {
                var classes = DbFixture.Classes(db);
                plain = (await classes.CreateAsync("T" + DbFixture.Unique()[..8], null, true)).EntityId;
                custom = (await classes.CreateAsync("T" + DbFixture.Unique()[..8], null, 32, true)).EntityId;
                Assert.True((await classes.UpdateAsync(custom!.Value, (await db.Classes.FindAsync(custom))!.Name, null, 34, true)).Succeeded);
            }
            await using (var db = fx.NewContext())
            {
                Assert.Equal(20, await db.Classes.Where(c => c.Id == plain).Select(c => c.Capacity).SingleAsync());
                Assert.Equal(34, await db.Classes.Where(c => c.Id == custom).Select(c => c.Capacity).SingleAsync()); // a chosen value stays
            }
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.Classes.Where(c => c.Id == plain || c.Id == custom).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Dashboard_shows_a_ring_per_class_with_real_enrolment_capacity_and_totals()
    {
        int full = 0, over = 0;
        try
        {
            // Two classes of this test: 3 / 3 (full) and 5 / 3 — over capacity the way it happens in the school: five students,
            // then the capacity lowered to 3 (adding a student to a full class is refused by the existing student rules).
            await using (var db = fx.NewContext())
            {
                var classes = DbFixture.Classes(db);
                full = (await classes.CreateAsync("T" + DbFixture.Unique()[..8], null, 3, true)).EntityId!.Value;
                over = (await classes.CreateAsync("T" + DbFixture.Unique()[..8], null, 5, true)).EntityId!.Value;
            }
            for (var i = 0; i < 3; i++) await fx.NewStudentAsync(full);
            for (var i = 0; i < 5; i++) await fx.NewStudentAsync(over);
            await using (var db = fx.NewContext())
                Assert.True((await DbFixture.Classes(db).UpdateAsync(over, (await db.Classes.FindAsync(over))!.Name, null, 3, true)).Succeeded);

            var html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/"));
            Assert.Contains("Students by class", html);
            Assert.DoesNotContain("dist__bar", html);                       // the bar chart is gone
            foreach (var key in new[] { "Occupied", "Available", "Over capacity" }) Assert.Contains($"</i>{key}</span>", html);

            await using (var db = fx.NewContext())
            {
                // Active classes, plus inactive classes that still have students — one ring each.
                var shown = await db.Classes.AsNoTracking().Where(c => c.IsActive || c.Students.Any())
                    .Select(c => new ClassDistribution(c.Id, c.Name, c.IsActive, c.Students.Count, c.Capacity)).ToListAsync();
                Assert.Equal(shown.Count, Regex.Matches(html, "<a class=\"cap-class ").Count);
                foreach (var c in shown) Assert.Contains($"href=\"/students?classId={c.ClassId}\"", html);

                var t = CapacityTotals.Of(shown);
                string Total(string label) => Regex.Match(html, $"cap-totals__value num\">([\\d,]+)</span><span class=\"cap-totals__label\">{label}<").Groups[1].Value.Replace(",", "");
                Assert.Equal((t.Students, t.Capacity, t.Available, t.Over),
                    (int.Parse(Total("Total students")), int.Parse(Total("Total capacity")), int.Parse(Total("Available seats")), int.Parse(Total("Over capacity"))));
            }

            // The full class and the over-capacity class, with their real numbers and the three segments.
            string Card(int id)
            {
                var at = html.IndexOf($"href=\"/students?classId={id}\"", StringComparison.Ordinal);
                var start = html.LastIndexOf("<a class=\"cap-class ", at, StringComparison.Ordinal);
                return html[start..html.IndexOf("</a>", at, StringComparison.Ordinal)];
            }
            var fullCard = Card(full);
            Assert.Contains("cap-class--full", fullCard);
            Assert.Contains("<strong class=\"num\">3</strong><small class=\"num\">/ 3</small>", fullCard);
            Assert.Contains(">Full<", fullCard);
            Assert.Contains("cap-ring__seg--occupied", fullCard);
            Assert.DoesNotContain("cap-ring__seg--available", fullCard);   // no seats left: no yellow
            Assert.DoesNotContain("cap-ring__seg--over", fullCard);

            var overCard = Card(over);
            Assert.Contains("cap-class--over", overCard);
            Assert.Contains("<strong class=\"num\">5</strong><small class=\"num\">/ 3</small>", overCard); // not capped at 3
            Assert.Contains(">+2 over capacity<", overCard);
            Assert.Contains("cap-ring__seg--over", overCard);
            Assert.DoesNotContain("cap-ring__seg--available", overCard);
            // Blue 3 and red 2 of a ring measured against 5 students: 60% and 40% of the circumference.
            var ring = 2 * Math.PI * 42;
            Assert.Contains($"stroke-dasharray=\"{Math.Round(3 * ring / 5, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)} ", overCard);
            Assert.Contains($"stroke-dasharray=\"{Math.Round(2 * ring / 5, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)} ", overCard);

            // Changing a capacity changes the ring: the over class at 10 has 5 seats available.
            await using (var db = fx.NewContext())
                Assert.True((await DbFixture.Classes(db).UpdateAsync(over, (await db.Classes.FindAsync(over))!.Name, null, 10, true)).Succeeded);
            html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/"));
            overCard = Card(over);
            Assert.Contains("<strong class=\"num\">5</strong><small class=\"num\">/ 10</small>", overCard);
            Assert.Contains(">5 seats available<", overCard);
            Assert.Contains("cap-ring__seg--available", overCard);
            Assert.DoesNotContain("cap-ring__seg--over", overCard);
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.Students.Where(s => s.SchoolClassId == full || s.SchoolClassId == over).ExecuteDeleteAsync();
            await db.Classes.Where(x => x.Id == full || x.Id == over).ExecuteDeleteAsync();
        }
    }
}
