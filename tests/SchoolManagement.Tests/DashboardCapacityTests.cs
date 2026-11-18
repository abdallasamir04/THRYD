using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Tests;

/// <summary>
/// Default class capacity (20, never a limit) and the dashboard's Students by class stacked columns: blue occupied up to the
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
    public async Task Dashboard_shows_a_stacked_column_per_class_with_real_enrolment_capacity_and_totals()
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
            Assert.DoesNotContain("dist__bar", html);   // neither the old bar chart…
            Assert.DoesNotContain("cap-ring", html);    // …nor the rings / class cards
            Assert.DoesNotContain("cap-class", html);
            Assert.Contains("js/dashboard-chart.js", html);
            foreach (var key in new[] { "Occupied", "Available", "Over capacity" }) Assert.Contains($"</i>{key}</span>", html);

            await using (var db = fx.NewContext())
            {
                // Active classes, plus inactive classes that still have students — one column each, in the Classes order.
                var shown = await db.Classes.AsNoTracking().Where(c => c.IsActive || c.Students.Any()).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                    .Select(c => new ClassDistribution(c.Id, c.Name, c.IsActive, c.Students.Count, c.Capacity)).ToListAsync();
                Assert.Equal(shown.Count, Regex.Matches(html, "<li class=\"cap-col ").Count);
                var order = Regex.Matches(html, "class=\"cap-col__link\" href=\"/students\\?classId=(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                Assert.Equal(shown.Select(c => c.ClassId), order);
                Assert.Contains($"--cols:{shown.Count}", html);

                var t = CapacityTotals.Of(shown);
                string Total(string label) => Regex.Match(html, $"cap-totals__value num\">([\\d,]+)</span><span class=\"cap-totals__label\">{label}<").Groups[1].Value.Replace(",", "");
                Assert.Equal((t.Students, t.Capacity, t.Available, t.Over),
                    (int.Parse(Total("Total students")), int.Parse(Total("Total capacity")), int.Parse(Total("Available seats")), int.Parse(Total("Over capacity"))));
            }

            // The full class and the over-capacity class: real numbers, the segments, the tooltip and the column heights.
            string Column(int id)
            {
                var at = html.IndexOf($"href=\"/students?classId={id}\"", StringComparison.Ordinal);
                var start = html.LastIndexOf("<li class=\"cap-col ", at, StringComparison.Ordinal);
                return html[start..(html.IndexOf("</li>", at, StringComparison.Ordinal) + 5)];
            }
            string Tip(string column, string row) => Regex.Match(column, $"</i>{Regex.Escape(row)}</dt><dd class=\"num\">(\\d+)</dd>").Groups[1].Value;
            double Height(string column) => double.Parse(Regex.Match(column, "cap-col__stack\" style=\"--h:([\\d.]+)\"").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            var fullCol = Column(full);
            Assert.Contains("cap-col--full", fullCol);
            Assert.Contains("<span class=\"cap-col__value num\">3</span>", fullCol);
            Assert.Contains("cap-seg--occupied\" style=\"flex-grow:3\"", fullCol);
            Assert.DoesNotContain("cap-seg--available", fullCol);   // no seats left: no yellow
            Assert.DoesNotContain("cap-seg--over", fullCol);
            Assert.Equal(("3", "3", "0", "0"), (Tip(fullCol, "Enrolled students"), Tip(fullCol, "Total capacity"), Tip(fullCol, "Available seats"), Tip(fullCol, "Over capacity")));
            Assert.Contains("<span class=\"num\">100%</span> of capacity used", fullCol);

            var overCol = Column(over);
            Assert.Contains("cap-col--over", overCol);
            Assert.Contains("<span class=\"cap-col__value num\">5</span>", overCol); // not capped at 3
            Assert.Contains("cap-seg--occupied\" style=\"flex-grow:3\"", overCol); // blue up to the capacity…
            Assert.Contains("cap-seg--over\" style=\"flex-grow:2\"", overCol);     // …red for the 2 beyond it
            Assert.DoesNotContain("cap-seg--available", overCol);
            Assert.Equal(("5", "3", "0", "2"), (Tip(overCol, "Enrolled students"), Tip(overCol, "Total capacity"), Tip(overCol, "Available seats"), Tip(overCol, "Over capacity")));
            Assert.Contains("<span class=\"num\">167%</span> of capacity used", overCol);
            Assert.Contains("5 of 3 students, 2 over capacity, 167% of capacity used", overCol); // the column's accessible name
            Assert.Equal(5 / 3.0, Height(overCol) / Height(fullCol), 2); // one shared scale: 5 students vs 3

            // Changing a capacity changes the column: the over class at 10 has 5 blue and 5 yellow, and stands 10 high.
            await using (var db = fx.NewContext())
                Assert.True((await DbFixture.Classes(db).UpdateAsync(over, (await db.Classes.FindAsync(over))!.Name, null, 10, true)).Succeeded);
            html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/"));
            overCol = Column(over);
            fullCol = Column(full);
            Assert.Contains("cap-col--open", overCol);
            Assert.Contains("cap-seg--occupied\" style=\"flex-grow:5\"", overCol);
            Assert.Contains("cap-seg--available\" style=\"flex-grow:5\"", overCol);
            Assert.DoesNotContain("cap-seg--over", overCol);
            Assert.Equal("5", Tip(overCol, "Available seats"));
            Assert.Equal(10 / 3.0, Height(overCol) / Height(fullCol), 2);
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.Students.Where(s => s.SchoolClassId == full || s.SchoolClassId == over).ExecuteDeleteAsync();
            await db.Classes.Where(x => x.Id == full || x.Id == over).ExecuteDeleteAsync();
        }
    }
}
