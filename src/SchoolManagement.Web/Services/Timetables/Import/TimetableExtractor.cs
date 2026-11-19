using System.Globalization;
using System.Text.RegularExpressions;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services.Excel;

namespace SchoolManagement.Web.Services.Timetables.Import;

/// <summary>What one timetable cell holds. Numbers are posted by the preview form.</summary>
public enum ImportCellKind
{
    /// <summary>A lesson: a subject, and a teacher when the file names one.</summary>
    Lesson = 1,

    /// <summary>The period runs that day but has no lesson.</summary>
    Free = 2,

    /// <summary>Break, recess, lunch, snack…: not a lesson, no teacher.</summary>
    Break = 3,

    /// <summary>Assembly, clubs, prayer, circle time…: a school activity, not a lesson.</summary>
    Activity = 4,

    /// <summary>The period does not run on that day (the day ends earlier).</summary>
    NoPeriod = 5,
}

/// <summary>A period row as read from the file: its label and times when the file gives them.</summary>
public sealed record ExtractedPeriod(string Label, int? Number, TimeOnly? Start, TimeOnly? End);

/// <summary>One day × period cell as read from the file (the raw text is kept; the subject / teacher split happens later).</summary>
public sealed record ExtractedCell(int Period, DayOfWeek Day, ImportCellKind Kind, string Raw, string? Label);

public sealed class ExtractedTimetable
{
    public required string Sheet { get; init; }
    public required bool DaysInRows { get; init; }
    public required IReadOnlyList<DayOfWeek> Days { get; init; }
    public required IReadOnlyList<ExtractedPeriod> Periods { get; init; }
    public required IReadOnlyList<ExtractedCell> Cells { get; init; }

    /// <summary>What the extraction noticed (ignored days, other sheets, rows it skipped…), shown as warnings.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Class names written above the timetable (a title such as "Class G1A"), as class keys.</summary>
    public IReadOnlyList<string> ClassesMentioned { get; init; } = [];

    public ExtractedCell? Cell(int period, DayOfWeek day) => Cells.FirstOrDefault(c => c.Period == period && c.Day == day);
}

/// <summary>
/// Reads a timetable laid out as a grid from a spreadsheet: the days across the top (or down the side — the sheet is
/// then read transposed) and one row per period, with the period's name and times in the cells before the first day.
/// Merged cells (a break across every day) are read as one value per cell. It only reads: nothing is matched against
/// the database here (subjects and teachers are matched by <see cref="TimetableImportService"/>) and nothing is saved.
/// </summary>
public static partial class TimetableExtractor
{
    public const int MaxPeriods = 20;
    public const int MaxCellText = 200;

    private static readonly Dictionary<string, DayOfWeek> DayWords = BuildDays();

    /// <summary>
    /// Words (<see cref="TextNormalizer.HeaderKey"/> of one word) that mark a break. A cell matches on whole words only, so a
    /// name that merely contains the letters ("Hanapi") is still a lesson. Two-word keys ("circletime") match the whole cell.
    /// </summary>
    private static readonly string[] BreakWords =
        ["break", "recess", "lunch", "snack", "breakfast", "playtime", "nap", "restperiod", "استراحه", "فسحه", "راحه", "فطار", "غداء"];

    /// <summary>Words that mark a school activity that is not a lesson.</summary>
    private static readonly string[] ActivityWords =
        ["assembly", "prayer", "prayers", "salah", "dismissal", "arrival", "registration", "circletime", "freeplay", "club", "clubs",
         "activity", "activities", "lineup", "morningmeeting", "طابور", "صلاه", "نشاط", "انصراف"];

    private static readonly string[] FreeWords = ["free", "freeperiod", "none", "na", "x", "nolesson"];

    public static ExtractedTimetable? Extract(IReadOnlyList<SheetGrid> sheets, string? classKey, out string? error)
    {
        error = null;
        var found = new List<(ExtractedTimetable Table, bool NamedForClass)>();
        foreach (var sheet in sheets)
        {
            var table = ExtractSheet(sheet);
            if (table is null) continue;
            var named = classKey is not null && (TextNormalizer.ClassKey(sheet.Name) == classKey || table.ClassesMentioned.Contains(classKey));
            found.Add((table, named));
        }
        if (found.Count == 0)
        {
            error = "No timetable was found in this file. Put the days (Sunday … Thursday) in one row or one column, and one row (or column) per period.";
            return null;
        }

        var pick = found.FirstOrDefault(f => f.NamedForClass);
        if (pick.Table is null) pick = found[0];
        var others = found.Where(f => f.Table != pick.Table).Select(f => f.Table.Sheet).ToList();
        if (others.Count == 0) return pick.Table;

        var notes = pick.Table.Notes.ToList();
        notes.Insert(0, $"The workbook has {found.Count} timetable sheets; “{pick.Table.Sheet}” was read{(pick.NamedForClass ? " (it is named for this class)" : "")}. "
            + $"Other sheets: {string.Join(", ", others.Take(8))}{(others.Count > 8 ? "…" : "")}. To import another one, upload a file with only that sheet, or name the sheet after the class.");
        return new ExtractedTimetable
        {
            Sheet = pick.Table.Sheet, DaysInRows = pick.Table.DaysInRows, Days = pick.Table.Days, Periods = pick.Table.Periods,
            Cells = pick.Table.Cells, ClassesMentioned = pick.Table.ClassesMentioned, Notes = notes,
        };
    }

    /// <summary>The day a header cell names ("Sunday", "Sun", "SUNDAY 12/11", "الأحد"), or null.</summary>
    public static DayOfWeek? ParseDay(string? text)
    {
        var key = TextNormalizer.HeaderKey(text);
        if (key.Length < 2) return null;
        if (DayWords.TryGetValue(key, out var day)) return day;
        // "sunday12nov", "sunday (week A)": a full day name at the start.
        foreach (var (word, d) in DayWords)
            if (word.Length >= 6 && key.StartsWith(word, StringComparison.Ordinal)) return d;
        return null;
    }

    /// <summary>The kind a non-lesson cell names (Break / Activity / Free), or null for anything else (a lesson).</summary>
    public static ImportCellKind? SpecialKind(string? text)
    {
        var t = TextNormalizer.CleanText(text);
        if (t.Length == 0) return null;
        if (t is "-" or "—" or "–" or "/" or "--") return ImportCellKind.Free;
        var key = TextNormalizer.HeaderKey(t);
        if (key.Length == 0) return null;
        if (FreeWords.Contains(key)) return ImportCellKind.Free;
        // A short text with one of the words: "Break", "Lunch break", "Morning assembly", "Clubs (Robotics)", "الاستراحة".
        if (key.Length > 30) return null;
        var words = WordSplit().Split(t).Select(TextNormalizer.HeaderKey).Where(w => w.Length > 0).ToList();
        bool Has(string[] list) => list.Any(k => key == k || words.Any(w => w == k || w == k + "s" || w == k + "es" || (k[0] > 127 && w.EndsWith(k, StringComparison.Ordinal))));
        if (Has(BreakWords)) return ImportCellKind.Break;
        if (Has(ActivityWords)) return ImportCellKind.Activity;
        return null;
    }

    /// <summary>Times in a label: "08:00 - 08:45", "8.00–8.45", "8:00 AM to 8:45 AM", "1:30-2:15" (afternoon).</summary>
    public static (TimeOnly? Start, TimeOnly? End) ParseTimes(string? text)
    {
        var t = TextNormalizer.ToLatinDigits(text ?? "");
        var times = new List<TimeOnly>();
        foreach (Match m in TimePattern().Matches(t))
        {
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            var ampm = m.Groups["ap"].Value.Replace(".", "").ToLowerInvariant();
            if (min > 59 || h > 23) continue;
            if (ampm is "pm" or "م" && h < 12) h += 12;
            else if (ampm is "am" or "ص" && h == 12) h = 0;
            else if (ampm.Length == 0 && h is >= 1 and <= 6) h += 12; // a school day: "1:30" is the afternoon
            times.Add(new TimeOnly(h, min));
            if (times.Count == 2) break;
        }
        return (times.Count > 0 ? times[0] : null, times.Count > 1 ? times[1] : null);
    }

    /// <summary>The period number in a label once its times are removed: "Period 3", "P3", "3", "3rd", "Lesson 3", "Third".</summary>
    public static int? ParseNumber(string? label)
    {
        var t = TimePattern().Replace(TextNormalizer.ToLatinDigits(label ?? ""), " ");
        var m = NumberPattern().Match(t);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is >= 1 and <= 30) return n;
        var key = TextNormalizer.HeaderKey(t);
        string[] words = ["first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"];
        for (var i = 0; i < words.Length; i++)
            if (key.Contains(words[i], StringComparison.Ordinal)) return i + 1;
        return null;
    }

    // ------------------------------------------------------------------ one sheet

    private static ExtractedTimetable? ExtractSheet(SheetGrid sheet)
    {
        var rows = sheet.Rows.Select(r => Enumerable.Range(0, sheet.ColumnCount).Select(c => Clean(r.Cell(c))).ToArray()).ToList();
        if (rows.Count < 2) return null;

        // Days across a row, or down a column (then read transposed).
        var (headerRow, rowDays) = BestRow(rows);
        var cols = Transpose(rows, sheet.ColumnCount);
        var (headerCol, colDays) = BestRow(cols);
        var daysInRows = colDays > rowDays;
        if (Math.Max(rowDays, colDays) < 2) return null;
        var grid = daysInRows ? cols : rows;
        var h = daysInRows ? headerCol : headerRow;
        return Read(sheet.Name, grid, h, daysInRows);
    }

    private static ExtractedTimetable? Read(string sheetName, List<string[]> grid, int h, bool daysInRows)
    {
        var notes = new List<string>();
        var header = grid[h];
        var dayCols = new List<(int Col, DayOfWeek Day)>();
        var ignored = new List<string>();
        for (var c = 0; c < header.Length; c++)
        {
            if (ParseDay(header[c]) is not { } day) continue;
            if (!ScheduleRules.IsSchoolDay(day)) { ignored.Add(ScheduleRules.DayName(day)); continue; }
            if (dayCols.Any(d => d.Day == day)) { notes.Add($"{ScheduleRules.DayName(day)} appears twice; the first one was read."); continue; }
            dayCols.Add((c, day));
        }
        if (dayCols.Count == 0) return null;
        if (ignored.Count > 0)
            notes.Add($"{string.Join(" and ", ignored.Distinct())} {(ignored.Distinct().Count() == 1 ? "is" : "are")} not a school day in THRYD (Sunday–Thursday) and {(ignored.Distinct().Count() == 1 ? "was" : "were")} not imported.");
        var labelCols = Enumerable.Range(0, header.Length).Where(c => dayCols.All(d => d.Col != c) && !ignoredDay(c)).ToList();
        bool ignoredDay(int c) => ParseDay(header[c]) is { } d && !ScheduleRules.IsSchoolDay(d);

        // Class names written above the timetable ("Timetable — Class G1A").
        var mentioned = new List<string>();
        for (var r = 0; r < h; r++)
            foreach (var text in grid[r].Where(t => t.Length > 0))
                foreach (Match m in ClassPattern().Matches(TextNormalizer.ToLatinDigits(text).ToUpperInvariant()))
                    mentioned.Add(TextNormalizer.ClassKey(m.Value));
        // A label column header (e.g. "Time" in the header row) also tells which column holds the times.

        var periods = new List<ExtractedPeriod>();
        var raw = new List<(string[] Days, ImportCellKind? RowKind, string? RowLabel)>();
        for (var r = h + 1; r < grid.Count && periods.Count < MaxPeriods + 1; r++)
        {
            var row = grid[r];
            var dayTexts = dayCols.Select(d => Cut(row[d.Col])).ToArray();
            var labels = labelCols.Select(c => row[c]).Where(t => t.Length > 0).ToList();
            if (dayTexts.All(t => t.Length == 0) && labels.Count == 0) continue;
            // The same days again: a second table (another week / class) starts here.
            if (dayTexts.Count(t => ParseDay(t) is not null) >= Math.Min(2, dayCols.Count)) { notes.Add("A second table further down the sheet was not read."); break; }

            var labelText = string.Join(" ", labels);
            var (start, end) = ParseTimes(labelText);
            var number = ParseNumber(labelText);
            var cleanLabel = TextNormalizer.CleanText(TimePattern().Replace(labelText, " ").Trim(' ', '-', '–', '—', '(', ')', '|', '/', ','));
            var rowKind = SpecialKind(cleanLabel);
            if (rowKind is ImportCellKind.Free) rowKind = null;
            var filled = dayTexts.Where(t => t.Length > 0).ToList();

            // A footer ("Class teacher: …", "Approved by …"): text only before the days, no number, no times, nothing under the days.
            if (filled.Count == 0 && rowKind is null && start is null && number is null)
            {
                if (periods.Count > 0) notes.Add($"“{Cut(labelText, 60)}” below the timetable was not imported.");
                continue;
            }

            // Every filled day cell names the same break / activity (a merged "BREAK" across the days).
            if (rowKind is null && filled.Count > 0 && filled.All(t => SpecialKind(t) is ImportCellKind.Break or ImportCellKind.Activity)
                && filled.Select(t => TextNormalizer.HeaderKey(t)).Distinct().Count() == 1)
            {
                rowKind = SpecialKind(filled[0]);
                if (cleanLabel.Length == 0 || number is not null && cleanLabel.All(ch => char.IsDigit(ch) || ch == ' ')) cleanLabel = filled[0];
            }

            if (periods.Count == MaxPeriods)
            {
                notes.Add($"Only the first {MaxPeriods} periods were read.");
                break;
            }
            var label = cleanLabel.Length > 0 ? cleanLabel : number is { } n ? $"Period {n}" : rowKind is null ? $"Period {periods.Count + 1}" : "Break";
            periods.Add(new ExtractedPeriod(Cut(label, 50), number, start, end));
            raw.Add((dayTexts, rowKind, rowKind is null ? null : Cut(label, 50)));
        }
        if (periods.Count == 0) return null;

        // Cells: a blank before the day's last filled period is a free period; after it, the day has ended (no period).
        var cells = new List<ExtractedCell>();
        for (var d = 0; d < dayCols.Count; d++)
        {
            var last = -1;
            for (var p = 0; p < raw.Count; p++)
                if (raw[p].Days[d].Length > 0) last = p;
            // A break row with blank day cells (its name only before the days) belongs to the days that continue after it.
            for (var p = 0; p < raw.Count; p++)
            {
                var text = raw[p].Days[d];
                ImportCellKind kind;
                string? cellLabel = null;
                if (text.Length == 0)
                {
                    if (p > last) kind = ImportCellKind.NoPeriod;
                    else if (raw[p].RowKind is { } rk) (kind, cellLabel) = (rk, raw[p].RowLabel);
                    else kind = ImportCellKind.Free;
                }
                else if (SpecialKind(text) is { } sk)
                {
                    kind = sk;
                    if (sk is ImportCellKind.Break or ImportCellKind.Activity) cellLabel = Cut(text, 50);
                }
                else kind = ImportCellKind.Lesson;
                cells.Add(new ExtractedCell(p, dayCols[d].Day, kind, text, cellLabel));
            }
        }

        var days = dayCols.Select(d => d.Day).ToList();
        var missing = ScheduleRules.Days.Where(d => !days.Contains(d)).ToList();
        if (missing.Count > 0) notes.Add($"No column for {string.Join(", ", missing.Select(ScheduleRules.DayName))}: {(missing.Count == 1 ? "that day gets" : "those days get")} no periods unless you add {(missing.Count == 1 ? "it" : "them")}.");
        if (daysInRows) notes.Insert(0, "The days are down the side of this timetable; it was read with the periods across the top.");
        return new ExtractedTimetable
        {
            Sheet = sheetName, DaysInRows = daysInRows, Days = days.OrderBy(d => d).ToList(), Periods = periods,
            Cells = cells, Notes = notes, ClassesMentioned = mentioned.Distinct().ToList(),
        };
    }

    // ------------------------------------------------------------------ helpers

    private static (int Index, int Days) BestRow(List<string[]> rows)
    {
        var best = (Index: -1, Days: 0);
        for (var r = 0; r < Math.Min(rows.Count, 40); r++)
        {
            var n = rows[r].Select(ParseDay).Where(d => d is not null).Distinct().Count();
            if (n > best.Days) best = (r, n);
        }
        return best;
    }

    private static List<string[]> Transpose(List<string[]> rows, int columns)
    {
        var result = new List<string[]>(columns);
        for (var c = 0; c < columns; c++)
        {
            var col = new string[rows.Count];
            for (var r = 0; r < rows.Count; r++) col[r] = c < rows[r].Length ? rows[r][c] : "";
            result.Add(col);
        }
        // Columns with nothing in them are not rows of the transposed table.
        return result.Where(c => c.Any(t => t.Length > 0)).ToList();
    }

    /// <summary>Trims each line and drops empty lines; keeps the line breaks (a subject and a teacher on two lines).</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(TextNormalizer.CleanText).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    private static string Cut(string text, int max = MaxCellText) => text.Length <= max ? text : text[..max].TrimEnd();

    private static Dictionary<string, DayOfWeek> BuildDays()
    {
        var map = new Dictionary<string, DayOfWeek>(StringComparer.Ordinal);
        void Add(DayOfWeek d, params string[] words) { foreach (var w in words) map[TextNormalizer.HeaderKey(w)] = d; }
        Add(DayOfWeek.Sunday, "sunday", "sun", "الأحد", "الاحد", "احد");
        Add(DayOfWeek.Monday, "monday", "mon", "الاثنين", "الإثنين", "اثنين");
        Add(DayOfWeek.Tuesday, "tuesday", "tue", "tues", "الثلاثاء", "ثلاثاء");
        Add(DayOfWeek.Wednesday, "wednesday", "wed", "الأربعاء", "الاربعاء", "اربعاء");
        Add(DayOfWeek.Thursday, "thursday", "thu", "thur", "thurs", "الخميس", "خميس");
        Add(DayOfWeek.Friday, "friday", "fri", "الجمعة", "جمعه");
        Add(DayOfWeek.Saturday, "saturday", "sat", "السبت", "سبت");
        return map;
    }

    [GeneratedRegex(@"(?<h>\d{1,2})\s*[:.]\s*(?<m>\d{2})\s*(?<ap>a\.?m\.?|p\.?m\.?|ص|م)?", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"(?<![\d:.])(\d{1,2})(?:st|nd|rd|th)?(?![\d:.])", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"\b(?:PRE-?K|KG\s?\d\s?-?[A-Z]|G(?:RADE)?\s?-?\d{1,2}\s?-?[A-Z])\b")]
    private static partial Regex ClassPattern();
}
