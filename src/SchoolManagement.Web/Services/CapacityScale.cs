namespace SchoolManagement.Web.Services;

/// <summary>
/// The y-axis of the dashboard's Students by class columns: 0 to at least 25 in steps of 5, growing in whole steps when a
/// class's enrolment or capacity goes beyond it (the step widens past 50 so there are never more than 10 gridlines), so no
/// column is ever clipped.
/// </summary>
public static class CapacityScale
{
    public const int MinTop = 25;
    private static readonly int[] Steps = [5, 10, 20, 25, 50, 100, 200, 250, 500, 1000];

    /// <param name="peak">The largest of every shown class's enrolment and capacity.</param>
    public static (int Top, int Step) For(int peak)
    {
        var p = Math.Max(MinTop, peak);
        var step = Steps.FirstOrDefault(s => (p + s - 1) / s <= 10, Steps[^1]);
        return ((p + step - 1) / step * step, step);
    }

    /// <summary>
    /// The labelled ticks (and gridlines) from 0 to <paramref name="top"/>: every step except 5, so the usual axis reads
    /// 0, 10, 15, 20, 25 (and keeps going 30, 35 … when the scale grows). The scale itself is unchanged, so no point moves.
    /// </summary>
    public static IReadOnlyList<int> Ticks(int top, int step)
        => Enumerable.Range(0, top / step + 1).Select(i => i * step).Where(t => t != 5).ToList();

    /// <summary>
    /// Where a tick sits on the axis, as a fraction of the plot's height: the ticks are evenly spaced (tick k of n at
    /// k / (n − 1)), so 0, 10, 15, 20, 25 sit at 0, ¼, ½, ¾ and 1 — an intentionally even visual axis, not a proportional one.
    /// </summary>
    public static double TickPosition(int index, int count) => count <= 1 ? 0 : index / (double)(count - 1);

    /// <summary>
    /// Where a value sits on that evenly spaced axis (0 at the bottom, 1 at the top): linear between the two ticks around it,
    /// so a value on a tick sits exactly on its gridline (e.g. 14 is 4/5 of the way from 10 to 15) and a larger value is
    /// always higher. Values outside the ticks are clamped to the ends.
    /// </summary>
    public static double Position(double value, IReadOnlyList<int> ticks)
    {
        var n = ticks.Count;
        if (n < 2 || value <= ticks[0]) return 0;
        if (value >= ticks[^1]) return 1;
        var k = 0;
        while (value > ticks[k + 1]) k++;
        return (k + (value - ticks[k]) / (ticks[k + 1] - ticks[k])) / (n - 1);
    }
}
