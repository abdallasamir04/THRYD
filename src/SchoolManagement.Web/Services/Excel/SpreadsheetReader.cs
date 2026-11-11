using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace SchoolManagement.Web.Services.Excel;

/// <summary>One row as read from the file. RowNumber is the row number the user sees in Excel (1-based).</summary>
public sealed record SheetRow(int RowNumber, string[] Cells, bool[] IsNumber)
{
    public string Cell(int column) => column >= 0 && column < Cells.Length ? Cells[column] : "";
    public bool CellIsNumber(int column) => column >= 0 && column < IsNumber.Length && IsNumber[column];
    public bool IsEmpty => Cells.All(string.IsNullOrWhiteSpace);
}

public sealed record SheetGrid(string Name, IReadOnlyList<SheetRow> Rows, int ColumnCount);

public sealed class SpreadsheetReadException(string message) : Exception(message);

/// <summary>Reads .xlsx (ClosedXML) and .csv files into text grids. Every value is read as the text the user sees.</summary>
public static class SpreadsheetReader
{
    public const int MaxRows = 10_000;
    public const int MaxColumns = 50;
    public const long MaxFileBytes = 10 * 1024 * 1024;

    public static readonly string[] AllowedExtensions = [".xlsx", ".csv"];

    public static IReadOnlyList<SheetGrid> Read(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ReadXlsx(stream),
            ".csv" => [ReadCsv(stream, Path.GetFileNameWithoutExtension(fileName))],
            _ => throw new SpreadsheetReadException("Upload an Excel workbook (.xlsx) or a CSV file (.csv)."),
        };
    }

    private static IReadOnlyList<SheetGrid> ReadXlsx(Stream stream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception)
        {
            throw new SpreadsheetReadException("This file could not be opened as an Excel workbook. Save it as .xlsx and try again.");
        }

        using (workbook)
        {
            var sheets = new List<SheetGrid>();
            foreach (var ws in workbook.Worksheets)
            {
                if (ws.Visibility != XLWorksheetVisibility.Visible) continue;
                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
                var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
                if (lastRow == 0 || lastCol == 0) continue;
                if (lastRow > MaxRows + 50)
                    throw new SpreadsheetReadException($"The sheet \"{ws.Name}\" has {lastRow:N0} rows. Import at most {MaxRows:N0} rows at a time.");
                lastCol = Math.Min(lastCol, MaxColumns);

                var rows = new List<SheetRow>();
                for (var r = 1; r <= lastRow; r++)
                {
                    var cells = new string[lastCol];
                    var numbers = new bool[lastCol];
                    for (var c = 1; c <= lastCol; c++)
                    {
                        var cell = ws.Cell(r, c);
                        (cells[c - 1], numbers[c - 1]) = CellText(cell);
                    }
                    var row = new SheetRow(r, cells, numbers);
                    if (!row.IsEmpty) rows.Add(row);
                }

                if (rows.Count > 0) sheets.Add(new SheetGrid(ws.Name, rows, lastCol));
            }

            if (sheets.Count == 0) throw new SpreadsheetReadException("The workbook has no data.");
            return sheets;
        }
    }

    private static (string Text, bool IsNumber) CellText(IXLCell cell)
    {
        XLCellValue value;
        try
        {
            value = cell.HasFormula ? cell.CachedValue : cell.Value;
        }
        catch
        {
            return ("", false);
        }

        switch (value.Type)
        {
            case XLDataType.Blank:
            case XLDataType.Error:
                return ("", false);
            case XLDataType.Text:
                return (value.GetText().Trim(), false);
            case XLDataType.Boolean:
                return (value.GetBoolean() ? "TRUE" : "FALSE", false);
            case XLDataType.Number:
                {
                    // A custom format such as 00000000000 keeps leading zeros: use the displayed text then.
                    var formatId = cell.Style.NumberFormat.NumberFormatId;
                    var custom = cell.Style.NumberFormat.Format;
                    if (formatId == 0 && string.IsNullOrEmpty(custom))
                        return (value.GetNumber().ToString("0.###############", CultureInfo.InvariantCulture), true);
                    try
                    {
                        return (cell.GetFormattedString().Trim(), true);
                    }
                    catch
                    {
                        return (value.GetNumber().ToString("0.###############", CultureInfo.InvariantCulture), true);
                    }
                }
            default:
                try
                {
                    return (cell.GetFormattedString().Trim(), false);
                }
                catch
                {
                    return (value.ToString(CultureInfo.InvariantCulture).Trim(), false);
                }
        }
    }

    private static SheetGrid ReadCsv(Stream stream, string name)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var text = Decode(buffer.ToArray());
        var delimiter = DetectDelimiter(text);

        var rows = new List<SheetRow>();
        var maxCols = 0;
        var recordNumber = 0;
        foreach (var record in ParseCsv(text, delimiter))
        {
            recordNumber++;
            if (recordNumber > MaxRows + 50)
                throw new SpreadsheetReadException($"The file has more than {MaxRows:N0} rows. Import at most {MaxRows:N0} rows at a time.");
            var cells = record.Take(MaxColumns).Select(v => v.Trim()).ToArray();
            var row = new SheetRow(recordNumber, cells, new bool[cells.Length]);
            if (row.IsEmpty) continue;
            maxCols = Math.Max(maxCols, cells.Length);
            rows.Add(row);
        }

        if (rows.Count == 0) throw new SpreadsheetReadException("The file has no data.");
        return new SheetGrid(name, rows, maxCols);
    }

    /// <summary>UTF-8 (with or without BOM) or UTF-16 by BOM; otherwise Windows Arabic (1256), which Excel uses for "CSV" on Arabic Windows.</summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1256).GetString(bytes);
        }
    }

    private static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var candidates = new[] { ',', ';', '\t' };
        return candidates.OrderByDescending(c => firstLine.Count(ch => ch == c)).First();
    }

    /// <summary>RFC 4180: quoted fields, doubled quotes, line breaks inside quotes.</summary>
    public static IEnumerable<List<string>> ParseCsv(string text, char delimiter)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var any = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            any = true;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            if (ch == '"' && field.Length == 0) inQuotes = true;
            else if (ch == delimiter) { record.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { /* handled with \n */ }
            else if (ch == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                yield return record;
                record = [];
                any = false;
            }
            else field.Append(ch);
        }

        if (any || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}
