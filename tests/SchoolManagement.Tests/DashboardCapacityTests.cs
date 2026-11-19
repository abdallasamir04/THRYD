using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Tests;

/// <summary>
/// Default class capacity (20, never a limit) and the dashboard's Students by class line chart: a point per class against
/// its own capacity, darker blue over it, with totals summed per class (one class never offsets another).
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
    public async Task Dashboard_shows_one_line_chart_with_a_point_per_class_and_real_enrolment_capacity_and_totals()
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
            Assert.DoesNotContain("dist__bar", html);   // neither the old bars…
            Assert.DoesNotContain("cap-col", html);     // …nor columns…
            Assert.DoesNotContain("cap-wheel", html);   // …nor a circle / rings / class cards
            Assert.DoesNotContain("cap-ring", html);
            Assert.DoesNotContain("cap-class", html);
            Assert.Single(Regex.Matches(html, "class=\"cap-chart\" data-cap-chart"));   // exactly one chart…
            Assert.Single(Regex.Matches(html, "<path class=\"cap-line__path\""));      // …with one line
            Assert.Contains("js/dashboard-chart.js", html);
            foreach (var key in new[] { "Enrolled", "Over capacity", "Class capacity" }) Assert.Contains($"</i>{key}</span>", html);

            await using (var db = fx.NewContext())
            {
                // Active classes, plus inactive classes that still have students — one point each, in the Classes order.
                var shown = await db.Classes.AsNoTracking().Where(c => c.IsActive || c.Students.Any()).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                    .Select(c => new ClassDistribution(c.Id, c.Name, c.IsActive, c.Students.Count, c.Capacity)).ToListAsync();
                Assert.Equal(shown.Count, Regex.Matches(html, "<li class=\"cap-pt ").Count);
                Assert.Equal(shown.Count, Regex.Matches(html, "<span class=\"cap-pt__dot\"").Count);
                var order = Regex.Matches(html, "class=\"cap-pt__link\" href=\"/students\\?classId=(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                Assert.Equal(shown.Select(c => c.ClassId), order);

                // The y-axis: 0 … at least 25 in steps of 5 (wider steps only when far larger values need them).
                var (top, step) = CapacityScale.For(shown.Max(c => Math.Max(c.Count, c.Capacity)));
                var axis = html[html.IndexOf("cap-chart__y", StringComparison.Ordinal)..html.IndexOf("cap-chart__scroll", StringComparison.Ordinal)];
                var ticks = Regex.Matches(axis, "<span class=\"num\"[^>]*>(\\d+)</span>").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                Assert.Equal(Enumerable.Range(0, top / step + 1).Select(i => i * step), ticks);
                if (step == 5) Assert.Equal([0, 5, 10, 15, 20, 25], ticks.Take(6));

                // The line goes through every point, in order: x = i + ½, y = top − enrolment (chart units of the viewBox).
                Assert.Contains($"viewBox=\"0 0 {shown.Count} {top}\"", html);
                var d = Regex.Match(html, "<path class=\"cap-line__path\" d=\"([^\"]+)\"").Groups[1].Value;
                var ends = Regex.Matches(d, "(?:M|C[^ ]+ [^ ]+ )([\\d.]+),([\\d.]+)").Select(m => (X: Num(m.Groups[1].Value), Y: Num(m.Groups[2].Value))).ToList();
                Assert.Equal(shown.Select((c, i) => (i + 0.5, (double)(top - c.Count))), ends);

                var t = CapacityTotals.Of(shown);
                string Total(string label) => Regex.Match(html, $"cap-totals__value num\">([\\d,]+)</span><span class=\"cap-totals__label\">{label}<").Groups[1].Value.Replace(",", "");
                Assert.Equal((t.Students, t.Capacity, t.Available, t.Over),
                    (int.Parse(Total("Total students")), int.Parse(Total("Total capacity")), int.Parse(Total("Available seats")), int.Parse(Total("Over capacity"))));
            }

            // The full class and the over-capacity class: the ratio by the point, the point, its capacity tick and the tooltip.
            string Point(int id)
            {
                var at = html.IndexOf($"href=\"/students?classId={id}\"", StringComparison.Ordinal);
                var start = html.LastIndexOf("<li class=\"cap-pt ", at, StringComparison.Ordinal);
                return html[start..(html.IndexOf("</li>", at, StringComparison.Ordinal) + 5)];
            }
            string Tip(string point, string row) => Regex.Match(point, $"<dt>{Regex.Escape(row)}</dt><dd class=\"num\">(\\d+%?)</dd>").Groups[1].Value;
            string Ratio(string point)
            {
                var m = Regex.Match(point, "cap-pt__value num[^\"]*\">(\\d+)<span class=\"cap-pt__cap\">/(\\d+)<");
                return $"{m.Groups[1].Value}/{m.Groups[2].Value}";
            }
            double Var(string point, string name) => Num(Regex.Match(point, $"--{name}:([\\d.]+)").Groups[1].Value);

            var fullPt = Point(full);
            Assert.Contains("cap-pt--full", fullPt);
            Assert.Equal("3/3", Ratio(fullPt));
            Assert.Contains("cap-pt__dot\" data-n=\"3\"", fullPt);
            Assert.Contains("cap-pt__capacity\" data-n=\"3\"", fullPt);     // its own capacity line
            Assert.Equal(Var(fullPt, "c"), Var(fullPt, "v"), 5);             // on the capacity line
            Assert.Equal(("3", "3", "0", "0", "100%"), (Tip(fullPt, "Enrolled"), Tip(fullPt, "Capacity"), Tip(fullPt, "Available"), Tip(fullPt, "Over capacity"), Tip(fullPt, "Usage")));

            var overPt = Point(over);
            Assert.Contains("cap-pt--over", overPt);                        // the darker blue point
            Assert.Equal("5/3", Ratio(overPt));                             // not capped at 3
            Assert.Contains("cap-pt__dot\" data-n=\"5\"", overPt);
            Assert.Equal(5 / 3.0, Var(overPt, "v") / Var(overPt, "c"), 3);   // above its capacity line
            Assert.Equal(("5", "3", "0", "2", "167%"), (Tip(overPt, "Enrolled"), Tip(overPt, "Capacity"), Tip(overPt, "Available"), Tip(overPt, "Over capacity"), Tip(overPt, "Usage")));
            Assert.Contains("5 of 3 students, 2 over capacity, 167% of capacity used", overPt); // the point's accessible name
            Assert.Equal(5 / 3.0, Var(overPt, "v") / Var(fullPt, "v"), 2);  // one shared scale: 5 students vs 3

            // Changing a capacity moves its capacity line, not the point: 5 / 10 is open, below the line, 5 seats available.
            await using (var db = fx.NewContext())
                Assert.True((await DbFixture.Classes(db).UpdateAsync(over, (await db.Classes.FindAsync(over))!.Name, null, 10, true)).Succeeded);
            html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/"));
            overPt = Point(over);
            Assert.Contains("cap-pt--open", overPt);
            Assert.Equal("5/10", Ratio(overPt));
            Assert.Contains("cap-pt__capacity\" data-n=\"10\"", overPt);
            Assert.Equal(0.5, Var(overPt, "v") / Var(overPt, "c"), 5);
            Assert.Equal("5", Tip(overPt, "Available"));
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.Students.Where(s => s.SchoolClassId == full || s.SchoolClassId == over).ExecuteDeleteAsync();
            await db.Classes.Where(x => x.Id == full || x.Id == over).ExecuteDeleteAsync();
        }
    }

    private static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Line_passes_through_every_point_and_never_overshoots_between_them()
    {
        int[] values = [12, 13, 15, 14, 14, 15, 19, 17, 18, 19, 16, 17, 19, 18, 17, 15, 23, 18, 14, 15, 15, 20, 15, 18, 17];
        var d = EnrolmentLine.Path(values, 25);
        Assert.StartsWith("M0.5,13C", d);
        var segments = Regex.Matches(d, "C([\\d.]+),([\\d.]+) ([\\d.]+),([\\d.]+) ([\\d.]+),([\\d.]+)").ToList();
        Assert.Equal(values.Length - 1, segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var g = segments[i].Groups;
            double y0 = 25 - values[i], y1 = 25 - values[i + 1];
            Assert.Equal((i + 1.5, y1), (Num(g[5].Value), Num(g[6].Value)));     // through the next point
            foreach (var c in new[] { Num(g[2].Value), Num(g[4].Value) })        // control points stay between the two
                Assert.InRange(c, Math.Min(y0, y1) - 1e-9, Math.Max(y0, y1) + 1e-9);   // values: no invented peak or dip
        }
        Assert.Equal("M0.5,5h0", EnrolmentLine.Path([20], 25));
        Assert.Equal("", EnrolmentLine.Path([], 25));
    }

    [Theory]
    [InlineData(0, 25, 5)]
    [InlineData(20, 25, 5)]
    [InlineData(23, 25, 5)]   // G6B 23/20: the usual 0–25 axis
    [InlineData(25, 25, 5)]
    [InlineData(26, 30, 5)]   // beyond 25: grows in whole steps, never clipping
    [InlineData(48, 50, 5)]
    [InlineData(51, 60, 10)]  // past 50 the step widens (≤ 10 gridlines)
    [InlineData(1000, 1000, 100)]
    public void Capacity_axis_starts_at_0_to_25_in_fives_and_grows_to_fit(int peak, int top, int step)
        => Assert.Equal((top, step), CapacityScale.For(peak));
}
