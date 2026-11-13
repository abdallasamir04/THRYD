using System.Text;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>Colour role of a value: the same meanings as the website's badges.</summary>
public enum PdfTone { Default, Muted, Primary, Success, Warning, Error, Info }

public enum PdfAlign { Start, Center, End }

/// <summary>One item of the header information line, e.g. "Week: 8–12 Nov 2026".</summary>
public sealed record PdfMeta(string Label, string? Value);

/// <summary>A table column. <paramref name="Width"/> is relative to the other columns.</summary>
public sealed record PdfColumn(string Header, float Width = 1, PdfAlign Align = PdfAlign.Start);

/// <summary>A table cell: the value, an optional second line, and optional status styling.</summary>
public sealed record PdfCell(string Text, string? Note = null, PdfTone Tone = PdfTone.Default, bool Badge = false, bool Strong = false)
{
    /// <summary>Empty values print as "—" so a blank cell never looks like missing data.</summary>
    public static implicit operator PdfCell(string? text) => new(string.IsNullOrWhiteSpace(text) ? "—" : text);

    public static PdfCell Status(string text, PdfTone tone) => new(text, Tone: tone, Badge: true);
    public static PdfCell Num(int value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>A block of the document body. Every section may have its own heading.</summary>
public abstract class PdfSection
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }

    /// <summary>Starts the section on a new page (e.g. one day of a school-wide timetable per page).</summary>
    public bool PageBreakBefore { get; init; }
}

/// <summary>A table with repeated headers on every page, alternating rows and an optional totals row.</summary>
public sealed class PdfTable : PdfSection
{
    public required IReadOnlyList<PdfColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<PdfCell>> Rows { get; init; }
    public IReadOnlyList<PdfCell>? Footer { get; init; }
    public bool RowNumbers { get; init; }
    public string EmptyText { get; init; } = "No records to show.";
}

/// <summary>A row of summary figures (totals shown above a table).</summary>
public sealed class PdfSummary : PdfSection
{
    public required IReadOnlyList<PdfStat> Items { get; init; }
}

public sealed record PdfStat(string Label, string Value, PdfTone Tone = PdfTone.Default);

/// <summary>Label / value pairs in a bordered grid (a form-like block, e.g. the incident information).</summary>
public sealed class PdfFields : PdfSection
{
    public required IReadOnlyList<PdfField> Items { get; init; }

    /// <summary>Pairs per row (each pair = a label cell and a value cell).</summary>
    public int Columns { get; init; } = 2;
}

/// <summary>One field. <paramref name="Wide"/> spans the whole row (long text).</summary>
public sealed record PdfField(string Label, PdfCell Value, bool Wide = false);

/// <summary>A short highlighted note, e.g. "Past week — shown as history".</summary>
public sealed class PdfNotice : PdfSection
{
    public required string Text { get; init; }
    public PdfTone Tone { get; init; } = PdfTone.Info;
}

/// <summary>
/// A timetable-style grid: column headers across the top, row headers down the side and any number of
/// entries in each cell (Cells[row][column]). Used for the class, teacher and P.E. timetables.
/// </summary>
public sealed class PdfGrid : PdfSection
{
    public string Corner { get; init; } = "";
    public required IReadOnlyList<PdfAxis> Columns { get; init; }
    public required IReadOnlyList<PdfAxis> Rows { get; init; }
    public required IReadOnlyList<IReadOnlyList<IReadOnlyList<PdfGridEntry>>> Cells { get; init; }
    public float RowHeaderWidth { get; init; } = 74;
    public IReadOnlyList<(string Label, PdfTone Tone)> Legend { get; init; } = [];
}

/// <summary>A row or column header of a grid (a day with its date, a period with its times).</summary>
public sealed record PdfAxis(string Title, string? Note = null, bool Highlight = false);

/// <summary>One item in a grid cell: a bold first line and smaller detail lines.</summary>
public sealed record PdfGridEntry(string Title, IReadOnlyList<string> Lines, PdfTone Tone = PdfTone.Primary);

/// <summary>Everything one exported document shows. Built by each page from its own (filtered) data.</summary>
public sealed class PdfReport
{
    /// <summary>Document title, e.g. "Teacher Timetable".</summary>
    public required string Title { get; init; }

    /// <summary>The line under the title, e.g. "Teacher Name: Taha Fawzy".</summary>
    public string? Subtitle { get; init; }

    /// <summary>Further header information (week, department, filters). The academic year and export date are added automatically.</summary>
    public IReadOnlyList<PdfMeta> Meta { get; init; } = [];

    public required IReadOnlyList<PdfSection> Sections { get; init; }

    /// <summary>Parts of the download name: ["Teacher Timetable", "Taha Fawzy"] → Teacher_Timetable_Taha_Fawzy.pdf.</summary>
    public required IReadOnlyList<string?> FileNameParts { get; init; }

    public bool ShowAcademicYear { get; init; } = true;

    /// <summary>A line above the title, e.g. the school name on an official report.</summary>
    public string? Eyebrow { get; init; }

    /// <summary>An extra footer line on every page, e.g. a confidentiality notice.</summary>
    public string? FooterNote { get; init; }

    public string FileName => PdfFileName.From(FileNameParts);
}

public static class PdfFileName
{
    /// <summary>Letters (any script) and digits joined by underscores; never empty, never a path.</summary>
    public static string From(IEnumerable<string?> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            foreach (var ch in part!.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '-') sb.Append(ch);
                else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
            }
            if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var name = sb.ToString().Trim('_', '-');
        if (name.Length > 120) name = name[..120].TrimEnd('_', '-');
        return (name.Length == 0 ? "THRYD_Report" : name) + ".pdf";
    }
}
