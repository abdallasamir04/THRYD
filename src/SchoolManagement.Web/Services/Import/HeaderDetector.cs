using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services.Excel;

namespace SchoolManagement.Web.Services.Import;

/// <summary>Finds the header row and matches its column names to the import fields.</summary>
public static class HeaderDetector
{
    public const int MaxHeaderScan = 15;

    public sealed record Detection(int HeaderIndex, int Score);

    /// <summary>
    /// The header row is the row (among the first 15) that names the most fields. When no row names
    /// a field, the first row is taken as the header if it holds only text, otherwise "no header".
    /// </summary>
    public static Detection Detect(SheetGrid sheet, IReadOnlyList<ImportField> fields)
    {
        var best = new Detection(-1, 0);
        var limit = Math.Min(sheet.Rows.Count, MaxHeaderScan);
        for (var i = 0; i < limit; i++)
        {
            var score = Score(sheet.Rows[i], fields);
            if (score > best.Score) best = new Detection(i, score);
        }

        if (best.Score > 0) return best;
        var first = sheet.Rows.Count > 0 ? sheet.Rows[0] : null;
        var looksLikeHeader = first is not null && sheet.Rows.Count > 1
            && !first.IsNumber.Any(n => n)
            && first.Cells.Count(c => c.Length > 0) >= Math.Min(2, fields.Count);
        return new Detection(looksLikeHeader ? 0 : -1, 0);
    }

    private static int Score(SheetRow row, IReadOnlyList<ImportField> fields)
    {
        var keys = row.Cells.Select(TextNormalizer.HeaderKey).Where(k => k.Length > 0).ToList();
        return fields.Count(f => keys.Any(k => f.SynonymKeys.Contains(k)));
    }

    /// <summary>Exact header matches first, then partial ones; with no header row, columns are taken in field order.</summary>
    public static Dictionary<string, int?> AutoMap(SheetGrid sheet, int headerIndex, IReadOnlyList<ImportField> fields)
    {
        var mapping = fields.ToDictionary(f => f.Key, _ => (int?)null);
        if (headerIndex < 0)
        {
            for (var i = 0; i < fields.Count && i < sheet.ColumnCount; i++) mapping[fields[i].Key] = i;
            return mapping;
        }

        var headers = sheet.Rows[headerIndex].Cells.Select(TextNormalizer.HeaderKey).ToList();
        var used = new HashSet<int>();

        foreach (var field in fields)
        {
            var column = headers.FindIndex(h => h.Length > 0 && field.SynonymKeys.Contains(h));
            if (column >= 0 && used.Add(column)) mapping[field.Key] = column;
        }

        foreach (var field in fields.Where(f => mapping[f.Key] is null))
        {
            for (var c = 0; c < headers.Count; c++)
            {
                if (used.Contains(c) || headers[c].Length < 3) continue;
                if (field.SynonymKeys.Any(s => s.Length >= 4 && (headers[c].Contains(s) || s.Contains(headers[c]))))
                {
                    mapping[field.Key] = c;
                    used.Add(c);
                    break;
                }
            }
        }

        return mapping;
    }

    /// <summary>Column letter as in Excel: 0 → A, 26 → AA.</summary>
    public static string ColumnLetter(int index)
    {
        var name = "";
        index++;
        while (index > 0)
        {
            var mod = (index - 1) % 26;
            name = (char)('A' + mod) + name;
            index = (index - mod) / 26;
        }
        return name;
    }
}
