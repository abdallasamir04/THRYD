using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Tests;

/// <summary>
/// Default class capacity (22, never a limit) and the dashboard's Students by class line chart: a point per class against
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
    public void Ratios_use_each_class_s_own_capacity()
    {
        // 14/22 open, 23/22 one over, 23/25 open: never one fixed capacity for every class.
        var a = new ClassDistribution(1, "KG1A", true, 14, 22);
        var b = new ClassDistribution(2, "G6B", true, 23, 22);
        var c = new ClassDistribution(3, "KG2A", true, 23, 25);
        Assert.Equal((8, 0, 64), (a.Available, a.Over, a.Utilization));
        Assert.Equal((0, 1, 105), (b.Available, b.Over, b.Utilization));
        Assert.Equal((2, 0, 92), (c.Available, c.Over, c.Utilization));
        Assert.Equal(new CapacityTotals(60, 69, 10, 1), CapacityTotals.Of([a, b, c]));
    }

    [Fact]
    public async Task New_classes_default_to_22_and_a_chosen_capacity_is_kept()
    {
        Assert.Equal(22, SchoolClass.DefaultCapacity);
        Assert.Equal(22, new SchoolClass().Capacity);
        Assert.Equal(22, new ClassFormModel().Capacity);

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
                Assert.Equal(22, await db.Classes.Where(c => c.Id == plain).Select(c => c.Capacity).SingleAsync());
                Assert.Equal(34, await db.Classes.Where(c => c.Id == custom).Select(c => c.Capacity).SingleAsync()); // a chosen value stays
                // An edit without a capacity (rename, activate) keeps the class's own capacity, never resetting it to 22.
                Assert.True((await DbFixture.Classes(db).UpdateAsync(custom!.Value, (await db.Classes.FindAsync(custom))!.Name, null, false)).Succeeded);
            }
            await using (var db = fx.NewContext())
            {
                Assert.Equal(34, await db.Classes.Where(c => c.Id == custom).Select(c => c.Capacity).SingleAsync());
                // The database default for a row inserted without a capacity is 22 too.
                var raw = "T" + DbFixture.Unique()[..8];
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [Classes] ([Name], [NormalizedName], [SortOrder], [IsActive], [CreatedAt], [UpdatedAt]) VALUES ({raw}, {raw.ToUpperInvariant()}, 999999, 1, SYSUTCDATETIME(), SYSUTCDATETIME())");
                var row = await db.Classes.SingleAsync(c => c.Name == raw);
                Assert.Equal(22, row.Capacity);
                await db.Classes.Where(c => c.Id == row.Id).ExecuteDeleteAsync();
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
            foreach (var key in new[] { "Enrolled", "At or over capacity" }) Assert.Contains($"</i>{key}</span>", html);
            // No class capacity reference line (nor its legend item): over capacity shows as red points and ratios.
            Assert.DoesNotContain("Class capacity", html);
            Assert.DoesNotContain("cap-pt__capacity", html);
            Assert.DoesNotContain("--c:", html);

            await using (var db = fx.NewContext())
            {
                // Active classes, plus inactive classes that still have students — one point each, in the Classes order.
                var shown = await db.Classes.AsNoTracking().Where(c => c.IsActive || c.Students.Any()).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                    .Select(c => new ClassDistribution(c.Id, c.Name, c.IsActive, c.Students.Count, c.Capacity)).ToListAsync();
                Assert.Equal(shown.Count, Regex.Matches(html, "<li class=\"cap-pt ").Count);
                Assert.Equal(shown.Count, Regex.Matches(html, "<span class=\"cap-pt__dot\"").Count);
                var order = Regex.Matches(html, "class=\"cap-pt__link\" href=\"/students\\?classId=(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                Assert.Equal(shown.Select(c => c.ClassId), order);

                // The y-axis: 0 … at least 25, labelled 0, 10, 15, 20, 25 (no 5), growing when larger values need it.
                var (top, step) = CapacityScale.For(shown.Max(c => Math.Max(c.Count, c.Capacity)));
                var axis = html[html.IndexOf("cap-chart__y", StringComparison.Ordinal)..html.IndexOf("cap-chart__scroll", StringComparison.Ordinal)];
                var ticks = Regex.Matches(axis, "<span class=\"num\"[^>]*>(\\d+)</span>").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                Assert.Equal(CapacityScale.Ticks(top, step), ticks);
                Assert.DoesNotContain(5, ticks);
                if (step == 5) Assert.Equal([0, 10, 15, 20, 25], ticks.Take(5));
                var grid = html[html.IndexOf("cap-chart__grid", StringComparison.Ordinal)..html.IndexOf("<svg class=\"cap-line\"", StringComparison.Ordinal)];
                Assert.Equal(ticks.Count, Regex.Matches(grid, "class=\"cap-chart__line").Count);   // one gridline per tick

                // Evenly spaced ticks (intentional, not proportional): tick k of n at k / (n − 1), on the axis and the gridlines.
                var even = ticks.Select((_, k) => k / (double)(ticks.Count - 1)).ToList();
                Assert.Equal(even, Regex.Matches(axis, "bottom:([0-9.]+)%").Select(m => Num(m.Groups[1].Value) / 100).ToList(), new Close());
                Assert.Equal(even, Regex.Matches(grid, "bottom:([0-9.]+)%").Select(m => Num(m.Groups[1].Value) / 100).ToList(), new Close());

                // The line goes through every point, in order: x = i + ½, y = 100 − 100 × the enrolment's axis position.
                Assert.Contains($"viewBox=\"0 0 {shown.Count} 100\"", html);
                var d = Regex.Match(html, "<path class=\"cap-line__path\" d=\"([^\"]+)\"").Groups[1].Value;
                var ends = Regex.Matches(d, "(?:M|C[^ ]+ [^ ]+ )([\\d.]+),([\\d.]+)").Select(m => (X: Num(m.Groups[1].Value), Y: Num(m.Groups[2].Value))).ToList();
                Assert.Equal(shown.Select((c, i) => (i + 0.5, Math.Round(100 - 100 * CapacityScale.Position(c.Count, ticks), 3))), ends);

                // Each point sits at its enrolment's axis position; the ratio and tooltip keep the real numbers.
                foreach (var c in shown)
                {
                    var at = html.IndexOf($"href=\"/students?classId={c.ClassId}\"", StringComparison.Ordinal);
                    var pt = html[at..html.IndexOf("</li>", at, StringComparison.Ordinal)];
                    Assert.Equal(CapacityScale.Position(c.Count, ticks), Num(Regex.Match(pt, "--v:([0-9.]+)").Groups[1].Value), 5);
                    Assert.Contains($"<dt>Enrolled</dt><dd class=\"num\">{c.Count}</dd>", pt);
                }

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
            Assert.DoesNotContain("cap-pt--over", fullPt);                  // exactly at capacity: full (red too, see app.css), not over
            Assert.Equal(("3", "3", "0", "0", "100%"), (Tip(fullPt, "Enrolled"), Tip(fullPt, "Capacity"), Tip(fullPt, "Available"), Tip(fullPt, "Over capacity"), Tip(fullPt, "Usage")));

            var overPt = Point(over);
            Assert.Contains("cap-pt--over", overPt);                        // the darker blue point
            Assert.Equal("5/3", Ratio(overPt));                             // not capped at 3
            Assert.Contains("cap-pt__dot\" data-n=\"5\"", overPt);
            Assert.Equal(("5", "3", "0", "2", "167%"), (Tip(overPt, "Enrolled"), Tip(overPt, "Capacity"), Tip(overPt, "Available"), Tip(overPt, "Over capacity"), Tip(overPt, "Usage")));
            Assert.Contains("5 of 3 students, 2 over capacity, 167% of capacity used", overPt); // the point's accessible name
            Assert.True(Var(overPt, "v") > Var(fullPt, "v"));               // one shared scale: 5 students above 3

            // Changing a capacity moves its capacity line, not the point: 5 / 10 is open, below the line, 5 seats available.
            await using (var db = fx.NewContext())
                Assert.True((await DbFixture.Classes(db).UpdateAsync(over, (await db.Classes.FindAsync(over))!.Name, null, 10, true)).Succeeded);
            html = WebUtility.HtmlDecode(await fx.Factory.CreateClient().GetStringAsync("/"));
            overPt = Point(over);
            Assert.Contains("cap-pt--open", overPt);
            Assert.Equal("5/10", Ratio(overPt));
            Assert.DoesNotContain("cap-pt--over", overPt);                  // no longer red
            Assert.Equal("5", Tip(overPt, "Available"));
        }
        finally
        {
            await using var db = fx.NewContext();
            await db.Students.Where(s => s.SchoolClassId == full || s.SchoolClassId == over).ExecuteDeleteAsync();
            await db.Classes.Where(x => x.Id == full || x.Id == over).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Default22_migration_moves_only_capacity_20_to_22_and_keeps_custom_capacities()
    {
        var sql = new SchoolManagement.Web.Data.Migrations.ClassCapacityDefault22().UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Single().Sql;
        await using var db = fx.NewContext();
        await using var tx = await db.Database.BeginTransactionAsync();   // rolled back: the shared test data is not changed
        var names = new[] { 20, 25, 30, 22, 19 }.ToDictionary(cap => cap, _ => "T" + DbFixture.Unique()[..8]);
        foreach (var (cap, name) in names)
            db.Classes.Add(new SchoolClass { Name = name, NormalizedName = name.ToUpperInvariant(), SortOrder = 999_000 + cap, Capacity = cap });
        await db.SaveChangesAsync();
        var ids = names.Values.ToList();
        var students = await db.Students.CountAsync();
        var updated = await db.Classes.Where(c => ids.Contains(c.Name)).Select(c => new { c.Name, c.UpdatedAt }).ToDictionaryAsync(c => c.Name, c => c.UpdatedAt);

        await db.Database.ExecuteSqlRawAsync(sql);
        db.ChangeTracker.Clear();

        var after = await db.Classes.AsNoTracking().Where(c => ids.Contains(c.Name)).ToDictionaryAsync(c => c.Name);
        Assert.Equal(22, after[names[20]].Capacity);   // the former default becomes the new one
        Assert.Equal(25, after[names[25]].Capacity);   // custom capacities stay
        Assert.Equal(30, after[names[30]].Capacity);
        Assert.Equal(22, after[names[22]].Capacity);
        Assert.Equal(19, after[names[19]].Capacity);
        Assert.All(after.Values, c => Assert.Equal(updated[c.Name], c.UpdatedAt));
        Assert.Equal(students, await db.Students.CountAsync());   // no student data touched
        await tx.RollbackAsync();
    }

    private static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Close : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-4;
        public int GetHashCode(double d) => 0;
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(10, 0.25)]    // the ticks sit evenly: 0, ¼, ½, ¾, 1
    [InlineData(15, 0.5)]
    [InlineData(20, 0.75)]
    [InlineData(25, 1.0)]
    [InlineData(5, 0.125)]    // linear between the two ticks around it
    [InlineData(14, 0.45)]    // 14/20: 4/5 of the way from 10 to 15
    [InlineData(23, 0.9)]     // 23/20: 3/5 of the way from 20 to 25
    public void Values_sit_on_the_evenly_spaced_axis_between_their_ticks(int value, double expected)
        => Assert.Equal(expected, CapacityScale.Position(value, [0, 10, 15, 20, 25]), 9);

    [Fact]
    public void Axis_positions_keep_the_order_of_the_values()
    {
        IReadOnlyList<int> ticks = [0, 10, 15, 20, 25, 30];
        var p = Enumerable.Range(0, 31).Select(v => CapacityScale.Position(v, ticks)).ToList();
        for (var i = 1; i < p.Count; i++) Assert.True(p[i] > p[i - 1]);
        Assert.Equal(0.2, CapacityScale.Position(10, ticks), 9);
        Assert.Equal([0, 0.25, 0.5, 0.75, 1], Enumerable.Range(0, 5).Select(k => CapacityScale.TickPosition(k, 5)));
    }

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

    [Theory]
    [InlineData(23, new[] { 0, 10, 15, 20, 25 })]          // the usual axis: no 5 tick
    [InlineData(25, new[] { 0, 10, 15, 20, 25 })]
    [InlineData(28, new[] { 0, 10, 15, 20, 25, 30 })]      // grows past 25, nothing clipped
    [InlineData(51, new[] { 0, 10, 20, 30, 40, 50, 60 })]
    public void Capacity_axis_ticks_skip_5(int peak, int[] expected)
    {
        var (top, step) = CapacityScale.For(peak);
        Assert.Equal(expected, CapacityScale.Ticks(top, step));
    }
}
