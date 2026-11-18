using System.Text;

namespace SchoolManagement.Web.Services.Excel;

/// <summary>
/// Writes CSV files that Excel opens correctly: UTF-8 with a byte-order mark (so Arabic is kept), comma-separated, CRLF
/// rows, fields quoted when needed (RFC 4180). Text starting with = + - @ (or a tab / CR) is prefixed with an apostrophe
/// so a spreadsheet never runs it as a formula.
/// </summary>
public static class CsvWriter
{
    public static byte[] Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        Line(sb, headers);
        foreach (var row in rows) Line(sb, row);
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        return [.. preamble, .. body];
    }

    private static void Line(StringBuilder sb, IReadOnlyList<string?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Field(values[i]));
        }
        sb.Append("\r\n");
    }

    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
