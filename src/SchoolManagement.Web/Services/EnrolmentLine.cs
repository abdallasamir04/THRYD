using System.Globalization;
using System.Text;

namespace SchoolManagement.Web.Services;

/// <summary>
/// The smooth line of the dashboard's Students by class chart, as SVG path data in chart units: point i sits at
/// x = i + 0.5 (the middle of its class's column; the chart is <c>n</c> columns wide) and y = top − enrolment (the
/// scale's top is y = 0). It is a monotone cubic (Fritsch–Carlson): it passes through every point and never rises above
/// or dips below its two neighbouring points, so smoothing adds no peaks or valleys the data does not have.
/// </summary>
public static class EnrolmentLine
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Path(IReadOnlyList<int> values, int top)
    {
        var n = values.Count;
        if (n == 0) return "";
        double X(int i) => i + 0.5;
        double Y(int i) => top - values[i];
        if (n == 1) return $"M{N(X(0))},{N(Y(0))}h0";

        // Secant slopes (x steps are 1) and Fritsch–Carlson tangents.
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++) d[i] = Y(i + 1) - Y(i);
        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (var i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var s = a * a + b * b;
            if (s > 9)
            {
                var t = 3 / Math.Sqrt(s);
                m[i] = t * a * d[i];
                m[i + 1] = t * b * d[i];
            }
        }

        var sb = new StringBuilder($"M{N(X(0))},{N(Y(0))}");
        for (var i = 0; i < n - 1; i++)
            sb.Append($"C{N(X(i) + 1 / 3.0)},{N(Y(i) + m[i] / 3)} {N(X(i + 1) - 1 / 3.0)},{N(Y(i + 1) - m[i + 1] / 3)} {N(X(i + 1))},{N(Y(i + 1))}");
        return sb.ToString();
    }

    private static string N(double v) => Math.Round(v, 3).ToString("0.###", Inv);
}
