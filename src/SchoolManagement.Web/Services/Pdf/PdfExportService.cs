using SchoolManagement.Web.Common;
using System.Globalization;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SchoolManagement.Web.Services.Pdf;

public interface IPdfExportService
{
    /// <summary>Renders a report as a branded A4 PDF, landscape unless the report asks for portrait (always light and printable).</summary>
    Task<byte[]> RenderAsync(PdfReport report, CancellationToken ct = default);
}

/// <summary>
/// The one PDF generator of the application. Every downloadable table builds a <see cref="PdfReport"/>
/// from its own data; this class gives all of them the same THRYD layout: logo top-left, title and
/// details on the right, repeated table headers, page numbers and a footer with the export date.
/// </summary>
public sealed class PdfExportService(ISettingsService settings, IWebHostEnvironment env) : IPdfExportService
{
    private const string LogoPath = "img/logo-light.png"; // the THRYD logo for light backgrounds (transparent PNG)
    private static readonly string[] FontStack = ["Manrope", "Noto Sans Arabic", "Lato"];
    private static readonly Lazy<bool> Setup = new(Configure);
    private static byte[]? _logo;

    // Palette: the website's light theme.
    private const string Brand = "#6D28D9";
    private const string BrandDeep = "#4C1D95";
    private const string BrandSoft = "#F3EEFF";
    private const string Ink = "#131829";
    private const string Muted = "#5F6A82";
    private const string Faint = "#98A1B5";
    private const string Border = "#DDE1EE";
    private const string Zebra = "#F7F8FC";
    private const string HeadSoft = "#F1F2F8";

    public async Task<byte[]> RenderAsync(PdfReport report, CancellationToken ct = default)
    {
        _ = Setup.Value;
        var profile = await settings.GetProfileAsync(ct);
        var exported = SchoolTime.Now; // "Exported" in school time (Africa/Cairo)
        void Frame(PageDescriptor page, string? subtitle)
        {
            page.Size(report.Portrait ? PageSizes.A4 : PageSizes.A4.Landscape());
            page.MarginHorizontal(report.Portrait ? 34 : 30);
            page.MarginTop(24);
            page.MarginBottom(20);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(8.5f).FontColor(Ink).LineHeight(1.25f));
            page.Header().Element(c => Header(c, report, profile, exported, subtitle));
            page.Footer().Element(c => Footer(c, profile, exported, report.FooterNote, report.FooterShowsYear, report.Portrait));
        }

        var document = Document.Create(container =>
        {
            if (report.Pages is { Count: > 0 } pages)
            {
                // One dedicated page per entry: ScaleToFit shrinks an entry that is taller than the page, so it is never split.
                foreach (var entry in pages)
                    container.Page(page =>
                    {
                        Frame(page, entry.Subtitle);
                        page.Content().PaddingTop(10).ScaleToFit().Column(col =>
                        {
                            col.Spacing(12);
                            foreach (var section in entry.Sections) col.Item().Element(c => Section(c, section));
                        });
                    });
                return;
            }
            container.Page(page =>
            {
                Frame(page, report.Subtitle);
                page.Content().PaddingTop(10).Column(col =>
                {
                    col.Spacing(14);
                    foreach (var section in report.Sections)
                    {
                        if (section.PageBreakBefore) col.Item().PageBreak();
                        col.Item().Element(c => Section(c, section));
                    }
                });
            });
        });
        document.WithMetadata(new DocumentMetadata
        {
            Title = report.Subtitle is null ? report.Title : $"{report.Title} — {report.Subtitle}",
            Author = profile.SchoolName,
            Subject = report.Title,
            Creator = "THRYD",
            Producer = "THRYD School Management",
            CreationDate = new DateTimeOffset(exported, SchoolTime.Current.OffsetAt()),
            ModifiedDate = new DateTimeOffset(exported, SchoolTime.Current.OffsetAt()),
        });
        return document.GeneratePdf();
    }

    private static bool Configure()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // An unexpected character (an emoji in a note) prints as a blank instead of failing the download.
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        var assembly = typeof(PdfExportService).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("PdfFonts.", StringComparison.Ordinal)))
            FontManager.RegisterFontFromEmbeddedResource(assembly, name);
        return true;
    }

    private byte[]? Logo()
    {
        if (_logo is not null) return _logo;
        var file = env.WebRootFileProvider.GetFileInfo(LogoPath);
        if (!file.Exists) return null;
        using var stream = file.CreateReadStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return _logo = ms.ToArray();
    }

    // ------------------------------------------------------------------ header and footer

    private void Header(IContainer container, PdfReport report, SchoolProfile profile, DateTime exported, string? subtitle)
    {
        var compact = report.CompactHeader;
        var meta = new List<PdfMeta>();
        if (report.ShowAcademicYear && !string.IsNullOrWhiteSpace(profile.AcademicYear)) meta.Add(new("Academic Year", profile.AcademicYear));
        meta.AddRange(report.Meta.Where(m => !string.IsNullOrWhiteSpace(m.Value)));
        meta.Add(new("Exported", exported.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)));

        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.Spacing(24);
                var logo = Logo();
                if (logo is not null)
                    // 640×123 px logo: 30 pt high is about 156 pt wide (FitArea keeps the aspect ratio in any case).
                    row.ConstantItem(compact ? 130 : 170).AlignLeft().AlignMiddle().Height(compact ? 23 : 30).Image(logo).FitArea();
                else
                    row.ConstantItem(170).AlignMiddle().Text(t => t.Span("THRYD").FontSize(20).ExtraBold().FontColor(Brand));

                row.RelativeItem().AlignRight().AlignMiddle().Column(title =>
                {
                    if (!string.IsNullOrWhiteSpace(report.Eyebrow))
                        title.Item().Text(t => { t.AlignRight(); t.Span(Iso(report.Eyebrow)).FontSize(compact ? 9.5f : 11).Bold().FontColor(Ink); });
                    title.Item().Text(t => { t.AlignRight(); t.Span(Iso(report.Title)).FontSize(compact ? 13.5f : 17).ExtraBold().FontColor(Ink); });
                    if (!string.IsNullOrWhiteSpace(subtitle))
                        title.Item().Text(t => { t.AlignRight(); t.Span(Iso(subtitle)).FontSize(compact ? 10 : 10.5f).Bold().FontColor(Brand); });
                });
            });

            // Full details on the first page; later pages repeat only the logo and title.
            col.Item().ShowOnce().PaddingTop(6).Text(t =>
            {
                t.AlignRight();
                for (var i = 0; i < meta.Count; i++)
                {
                    if (i > 0) t.Span("     ").FontSize(8);
                    t.Span(meta[i].Label + ": ").FontSize(8).FontColor(Muted);
                    t.Span(Iso(meta[i].Value!)).FontSize(8).SemiBold().FontColor(Ink);
                }
            });

            col.Item().PaddingTop(8).Row(rule =>
            {
                rule.RelativeItem(3).Height(2).Background(Brand);
                rule.RelativeItem(2).Height(2).Background("#7C3AED");
                rule.RelativeItem(1).Height(2).Background("#2563EB");
            });
        });
    }

    private static void Footer(IContainer container, SchoolProfile profile, DateTime exported, string? note, bool showYear, bool portrait = false) =>
        container.PaddingTop(8).BorderTop(0.6f).BorderColor(Border).PaddingTop(5).Column(col =>
        {
            if (!string.IsNullOrWhiteSpace(note))
                col.Item().PaddingBottom(4).Text(t => { t.AlignCenter(); t.Span(Iso(note)).FontSize(7.5f).SemiBold().FontColor(Ink); });
            col.Item().Row(row => FooterRow(row, profile, exported, showYear, portrait));
        });

    private static void FooterRow(RowDescriptor row, SchoolProfile profile, DateTime exported, bool showYear, bool portrait = false)
        {
            row.RelativeItem().Text(t =>
            {
                t.Span("THRYD").FontSize(7.5f).ExtraBold().FontColor(Brand);
                // The narrower portrait page keeps the footer on one line: the school name without the tagline.
                t.Span(portrait ? "  ·  " : "  ·  The AI-Powered School Operating System  ·  ").FontSize(7.5f).FontColor(Muted);
                t.Span(Iso(profile.SchoolName)).FontSize(7.5f).FontColor(Muted);
            });
            row.AutoItem().Text(t =>
            {
                if (showYear && !string.IsNullOrWhiteSpace(profile.AcademicYear))
                    t.Span("Academic Year " + profile.AcademicYear + "  ·  ").FontSize(7.5f).FontColor(Muted);
                t.Span("Exported " + exported.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)).FontSize(7.5f).FontColor(Muted);
            });
            row.RelativeItem().Text(t =>
            {
                t.AlignRight();
                t.Span("Page ").FontSize(7.5f).FontColor(Muted);
                t.CurrentPageNumber().FontSize(7.5f).SemiBold().FontColor(Ink);
                t.Span(" of ").FontSize(7.5f).FontColor(Muted);
                t.TotalPages().FontSize(7.5f).SemiBold().FontColor(Ink);
            });
        }

    // ------------------------------------------------------------------ sections

    private static void Section(IContainer container, PdfSection section)
    {
        var headed = !string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Subtitle);
        // A heading never stays alone at the foot of a page: with less than 120 pt left, the whole section
        // (heading + the start of its content) starts on the next page.
        (headed ? container.EnsureSpace(120) : container).Column(col =>
        {
            if (headed)
            {
                col.Item().PaddingBottom(6).Column(head =>
                {
                    if (!string.IsNullOrWhiteSpace(section.Title))
                        head.Item().Text(t => t.Span(Iso(section.Title)).FontSize(11).Bold().FontColor(Ink));
                    if (!string.IsNullOrWhiteSpace(section.Subtitle))
                        head.Item().Text(t => t.Span(Iso(section.Subtitle)).FontSize(8).FontColor(Muted));
                });
            }
            col.Item().Element(c =>
            {
                switch (section)
                {
                    case PdfTable table: Table(c, table); break;
                    case PdfGrid grid: Grid(c, grid); break;
                    case PdfFields fields: Fields(c, fields); break;
                    case PdfSummary summary: Summary(c, summary); break;
                    case PdfNotice notice: Notice(c, notice.Text, notice.Tone); break;
                }
            });
        });
    }

    /// <summary>Label / value pairs: shaded label cells, white value cells, thin borders; wide fields span the row.</summary>
    private static void Fields(IContainer container, PdfFields fields)
    {
        var perRow = Math.Max(1, fields.Columns);
        container.Border(0.6f).BorderColor(Border).Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                for (var i = 0; i < perRow; i++) { c.ConstantColumn(fields.LabelWidth); c.RelativeColumn(); }
            });
            var used = 0;
            foreach (var f in fields.Items)
            {
                if (f.Wide && used > 0) { FillRow(t, perRow - used); used = 0; }
                t.Cell().Element(c => FieldLabel(c, f.Label));
                var span = f.Wide ? (uint)(perRow * 2 - 1) : 1u;
                t.Cell().ColumnSpan(span).Element(c => FieldValue(c, f.Value));
                used = f.Wide ? 0 : (used + 1) % perRow;
            }
            if (used > 0) FillRow(t, perRow - used);
        });
    }

    private static void FillRow(TableDescriptor t, int pairs)
    {
        for (var i = 0; i < pairs; i++) { t.Cell().Element(c => FieldLabel(c, "")); t.Cell().Element(c => FieldValue(c, new PdfCell(""))); }
    }

    private static void FieldLabel(IContainer c, string label) =>
        c.Border(0.4f).BorderColor(Border).Background(HeadSoft).PaddingVertical(6).PaddingHorizontal(8)
            .Text(t => t.Span(Iso(label)).FontSize(7.5f).Bold().FontColor(Muted));

    private static void FieldValue(IContainer c, PdfCell v) =>
        c.Border(0.4f).BorderColor(Border).PaddingVertical(6).PaddingHorizontal(8).Column(col =>
        {
            if (v.Badge)
                col.Item().AlignLeft().Background(Soft(v.Tone)).PaddingVertical(1.5f).PaddingHorizontal(6)
                    .Text(t => t.Span(Iso(v.Text)).FontSize(8).Bold().FontColor(Strong(v.Tone)));
            else
                col.Item().Text(t => t.Span(Iso(v.Text)).FontSize(8.5f).FontColor(Ink).Style(v.Strong ? TextStyle.Default.SemiBold() : TextStyle.Default));
            if (!string.IsNullOrWhiteSpace(v.Note))
                col.Item().Text(t => t.Span(Iso(v.Note)).FontSize(7).FontColor(Muted));
        });

    private static void Summary(IContainer container, PdfSummary summary) =>
        container.Row(row =>
        {
            row.Spacing(8);
            foreach (var item in summary.Items)
                row.RelativeItem().Border(0.6f).BorderColor(Border).Background(Colors.White).PaddingVertical(6).PaddingHorizontal(9).Column(c =>
                {
                    c.Item().Text(t => t.Span(Iso(item.Value)).FontSize(14).ExtraBold().FontColor(item.Tone == PdfTone.Default ? Ink : Strong(item.Tone)));
                    c.Item().Text(t => t.Span(Iso(item.Label)).FontSize(7.5f).FontColor(Muted));
                });
        });

    private static void Notice(IContainer container, string text, PdfTone tone) =>
        container.Background(Soft(tone)).BorderLeft(3).BorderColor(Strong(tone)).PaddingVertical(6).PaddingHorizontal(9)
            .Text(t => t.Span(Iso(text)).FontSize(8).FontColor(Ink));

    // ------------------------------------------------------------------ tables

    private static void Table(IContainer container, PdfTable table)
    {
        if (table.Rows.Count == 0)
        {
            Notice(container, table.EmptyText, PdfTone.Muted);
            return;
        }

        container.Table(t =>
        {
            var roomy = table.Comfortable;
            var tight = !roomy && table.Compact;
            t.ColumnsDefinition(c =>
            {
                if (table.RowNumbers) c.ConstantColumn(roomy ? 34 : 26);
                foreach (var column in table.Columns) c.RelativeColumn(column.Width);
            });

            t.Header(h =>
            {
                if (table.RowNumbers) HeaderCell(h.Cell(), "#", roomy ? PdfAlign.Center : PdfAlign.End, roomy);
                foreach (var column in table.Columns) HeaderCell(h.Cell(), column.Header, column.Align, roomy);
            });

            for (var r = 0; r < table.Rows.Count; r++)
            {
                var background = r % 2 == 1 ? Zebra : "#FFFFFF";
                var row = table.Rows[r];
                // Serial number of the row (1, 2, 3… in English digits), never a database id.
                if (table.RowNumbers)
                    BodyCell(t.Cell(), background, roomy, tight).Element(c => CellText(c, new PdfCell((r + 1).ToString(CultureInfo.InvariantCulture), Tone: PdfTone.Muted), roomy ? PdfAlign.Center : PdfAlign.End, roomy));
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    var cell = i < row.Count ? row[i] : new PdfCell("—");
                    var align = table.Columns[i].Align;
                    BodyCell(t.Cell(), background, roomy, tight).Element(c => CellText(c, cell, align, roomy));
                }
            }

            if (table.Footer is { } footer)
            {
                if (table.RowNumbers) FooterCell(t.Cell());
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    var cell = i < footer.Count ? footer[i] : new PdfCell("");
                    var align = table.Columns[i].Align;
                    FooterCell(t.Cell()).Element(c => CellText(c, cell with { Strong = true }, align));
                }
            }
        });
    }

    private static void HeaderCell(IContainer cell, string text, PdfAlign align, bool roomy = false) =>
        cell.Background(BrandDeep).PaddingVertical(roomy ? 6 : 5).PaddingHorizontal(roomy ? 8 : 5).AlignMiddle()
            .Text(t => { Align(t, align); t.Span(text).FontSize(roomy ? 9 : 7.5f).Bold().FontColor(Colors.White); });

    // ShowEntire: a cell's lines are never split between two pages (the row moves to the next page instead).
    private static IContainer BodyCell(IContainer cell, string background, bool roomy = false, bool tight = false) =>
        cell.Background(background).BorderBottom(0.5f).BorderColor(Border).PaddingVertical(roomy ? 5 : tight ? 1.5f : 4).PaddingHorizontal(roomy ? 8 : 5).ShowEntire();

    private static IContainer FooterCell(IContainer cell) =>
        cell.Background(BrandSoft).BorderTop(1).BorderColor(Brand).PaddingVertical(5).PaddingHorizontal(5);

    private static void CellText(IContainer container, PdfCell cell, PdfAlign align, bool roomy = false) =>
        container.Column(col =>
        {
            if (cell.Badge && cell.Text.Length > 0)
            {
                var item = col.Item();
                item = align switch { PdfAlign.End => item.AlignRight(), PdfAlign.Center => item.AlignCenter(), _ => item.AlignLeft() };
                item.Background(Soft(cell.Tone)).CornerRadius(7).PaddingHorizontal(6).PaddingVertical(1.5f)
                    .Text(t => t.Span(Iso(cell.Text)).FontSize(7.5f).Bold().FontColor(Strong(cell.Tone)));
            }
            else
            {
                col.Item().Text(t =>
                {
                    Align(t, align);
                    var span = t.Span(Iso(cell.Text)).FontColor(cell.Tone == PdfTone.Default ? Ink : Strong(cell.Tone));
                    if (roomy) span.FontSize(10.5f);
                    if (cell.Strong) span.Bold();
                });
            }
            if (!string.IsNullOrWhiteSpace(cell.Note))
                col.Item().Text(t => { Align(t, align); t.Span(Iso(cell.Note)).FontSize(7).FontColor(Muted); });
        });

    // ------------------------------------------------------------------ timetable grids

    private static void Grid(IContainer container, PdfGrid grid)
    {
        container.Column(col =>
        {
            col.Spacing(6);
            // Legend above the grid, as on the website.
            if (grid.Legend.Count > 0)
                col.Item().Row(row =>
                {
                    row.Spacing(14);
                    foreach (var (label, tone) in grid.Legend)
                        row.AutoItem().Row(item =>
                        {
                            item.Spacing(4);
                            item.AutoItem().AlignMiddle().Width(9).Height(9).Background(Soft(tone)).Border(1).BorderColor(Strong(tone));
                            item.AutoItem().AlignMiddle().Text(t => t.Span(label).FontSize(7.5f).FontColor(Muted));
                        });
                });

            // One outer row per day / period, each drawn whole (never split over two pages); the column
            // headers repeat on every page.
            col.Item().Table(outer =>
            {
                outer.ColumnsDefinition(c => c.RelativeColumn());
                outer.Header(h => h.Cell().Element(c => GridRow(c, grid, null)));
                for (var r = 0; r < grid.Rows.Count; r++)
                {
                    var index = r;
                    outer.Cell().ShowEntire().Element(c => GridRow(c, grid, index));
                }
            });
        });
    }

    /// <summary>The column headers (<paramref name="row"/> null) or one row of a grid; its cells share the row's height.</summary>
    private static void GridRow(IContainer container, PdfGrid grid, int? row) =>
        container.Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(grid.RowHeaderWidth);
                foreach (var _ in grid.Columns) c.RelativeColumn();
            });
            if (row is not { } r)
            {
                t.Cell().Padding(1.5f).AlignMiddle().PaddingHorizontal(4).Text(x => x.Span(grid.Corner).FontSize(7).SemiBold().FontColor(Faint));
                foreach (var column in grid.Columns) t.Cell().Padding(1.5f).Element(c => AxisBox(c, column, center: true));
                return;
            }
            t.Cell().Padding(1.5f).Element(c => AxisBox(c, grid.Rows[r], center: false));
            for (var i = 0; i < grid.Columns.Count; i++)
            {
                var entries = r < grid.Cells.Count && i < grid.Cells[r].Count ? grid.Cells[r][i] : [];
                t.Cell().Padding(1.5f).Element(c => GridCell(c, entries));
            }
        });

    private static void AxisBox(IContainer container, PdfAxis axis, bool center) =>
        container.Background(axis.Highlight ? BrandSoft : HeadSoft).Border(axis.Highlight ? 0.8f : 0).BorderColor(Brand)
            .PaddingVertical(4).PaddingHorizontal(5).AlignMiddle().Column(c =>
            {
                c.Item().Text(t => { if (center) t.AlignCenter(); t.Span(Iso(axis.Title)).FontSize(8).Bold().FontColor(axis.Highlight ? Brand : Ink); });
                if (!string.IsNullOrWhiteSpace(axis.Note))
                    c.Item().Text(t => { if (center) t.AlignCenter(); t.Span(Iso(axis.Note)).FontSize(6.5f).FontColor(Muted); });
            });

    private static void GridCell(IContainer container, IReadOnlyList<PdfGridEntry> entries) =>
        container.MinHeight(32).Border(0.6f).BorderColor(Border).Background(Colors.White).Padding(2).Column(col =>
        {
            col.Spacing(2);
            foreach (var e in entries)
                col.Item().Background(Soft(e.Tone)).BorderLeft(2.5f).BorderColor(Strong(e.Tone)).PaddingVertical(1.5f).PaddingHorizontal(4).Column(entry =>
                {
                    entry.Item().Text(t => t.Span(Iso(e.Title)).FontSize(7.5f).Bold().FontColor(Ink).LineHeight(1.1f));
                    foreach (var line in e.Lines.Where(l => !string.IsNullOrWhiteSpace(l)))
                        entry.Item().Text(t => t.Span(Iso(line)).FontSize(6.5f).FontColor(Muted).LineHeight(1.1f));
                });
        });

    // ------------------------------------------------------------------ helpers

    private static void Align(TextDescriptor t, PdfAlign align)
    {
        if (align == PdfAlign.End) t.AlignRight();
        else if (align == PdfAlign.Center) t.AlignCenter();
        else t.AlignLeft();
    }

    private static string Strong(PdfTone tone) => tone switch
    {
        PdfTone.Primary => Brand,
        PdfTone.Success => "#0D7A63",
        PdfTone.Warning => "#B45309",
        PdfTone.Error => "#C62828",
        PdfTone.Info => "#1D4ED8",
        PdfTone.Muted => Muted,
        _ => Ink,
    };

    private static string Soft(PdfTone tone) => tone switch
    {
        PdfTone.Primary => "#EFE7FF",
        PdfTone.Success => "#DFF4EC",
        PdfTone.Warning => "#FDF0D8",
        PdfTone.Error => "#FDECEC",
        PdfTone.Info => "#E5EEFF",
        PdfTone.Muted => "#EEF0F6",
        _ => "#F4F5FA",
    };

    /// <summary>
    /// Wraps text containing Arabic (or Hebrew) in Unicode isolates, as dir="auto" does on the website,
    /// so a right-to-left name never reorders the numbers or punctuation printed next to it.
    /// </summary>
    internal static string Iso(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var ch in text)
            if (ch is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
                return "⁨" + text + "⁩";
        return text;
    }
}
