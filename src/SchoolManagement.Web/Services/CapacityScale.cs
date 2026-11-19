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
}
