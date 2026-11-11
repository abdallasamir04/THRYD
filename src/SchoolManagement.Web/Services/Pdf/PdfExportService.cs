using System.Globalization;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SchoolManagement.Web.Services.Pdf;

public interface IPdfExportService
{
    /// <summary>Renders a report as a branded, landscape A4 PDF (always a light, printable document).</summary>
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
        var exported = DateTime.Now;
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.MarginHorizontal(30);
            page.MarginTop(24);
            page.MarginBottom(20);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(8.5f).FontColor(Ink).LineHeight(1.25f));
            page.Header().Element(c => Header(c, report, profile, exported));
            page.Content().PaddingTop(10).Column(col =>
            {
                col.Spacing(14);
                foreach (var section in report.Sections) col.Item().Element(c => Section(c, section));
            });
            page.Footer().Element(c => Footer(c, profile, exported));
        }));
        document.WithMetadata(new DocumentMetadata
        {
            Title = report.Subtitle is null ? report.Title : $"{report.Title} — {report.Subtitle}",
            Author = profile.SchoolName,
            Subject = report.Title,
            Creator = "THRYD",
            Producer = "THRYD School Management",
            CreationDate = DateTimeOffset.Now,
            ModifiedDate = DateTimeOffset.Now,
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

    private void Header(IContainer container, PdfReport report, SchoolProfile profile, DateTime exported)
    {
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
                    row.ConstantItem(170).AlignLeft().AlignMiddle().Height(30).Image(logo).FitArea();
                else
                    row.ConstantItem(170).AlignMiddle().Text(t => t.Span("THRYD").FontSize(20).ExtraBold().FontColor(Brand));

                row.RelativeItem().AlignRight().AlignMiddle().Column(title =>
                {
                    title.Item().Text(t => { t.AlignRight(); t.Span(Iso(report.Title)).FontSize(17).ExtraBold().FontColor(Ink); });
                    if (!string.IsNullOrWhiteSpace(report.Subtitle))
                        title.Item().Text(t => { t.AlignRight(); t.Span(Iso(report.Subtitle)).FontSize(10.5f).Bold().FontColor(Brand); });
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

    private static void Footer(IContainer container, SchoolProfile profile, DateTime exported) =>
        container.PaddingTop(8).BorderTop(0.6f).BorderColor(Border).PaddingTop(5).Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                t.Span("THRYD").FontSize(7.5f).ExtraBold().FontColor(Brand);
                t.Span("  ·  The AI-Powered School Operating System  ·  ").FontSize(7.5f).FontColor(Muted);
                t.Span(Iso(profile.SchoolName)).FontSize(7.5f).FontColor(Muted);
            });
            row.AutoItem().Text(t => t.Span("Exported " + exported.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)).FontSize(7.5f).FontColor(Muted));
            row.RelativeItem().Text(t =>
            {
                t.AlignRight();
                t.Span("Page ").FontSize(7.5f).FontColor(Muted);
                t.CurrentPageNumber().FontSize(7.5f).SemiBold().FontColor(Ink);
                t.Span(" of ").FontSize(7.5f).FontColor(Muted);
                t.TotalPages().FontSize(7.5f).SemiBold().FontColor(Ink);
            });
        });

    // ------------------------------------------------------------------ sections

    private static void Section(IContainer container, PdfSection section)
    {
        container.Column(col =>
        {
            if (!string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Subtitle))
            {
                // Keep a heading together with the start of its content.
                col.Item().EnsureSpace(80).PaddingBottom(6).Column(head =>
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
                    case PdfSummary summary: Summary(c, summary); break;
                    case PdfNotice notice: Notice(c, notice.Text, notice.Tone); break;
                }
            });
        });
    }

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
            t.ColumnsDefinition(c =>
            {
                if (table.RowNumbers) c.ConstantColumn(26);
                foreach (var column in table.Columns) c.RelativeColumn(column.Width);
            });

            t.Header(h =>
            {
                if (table.RowNumbers) HeaderCell(h.Cell(), "#", PdfAlign.End);
                foreach (var column in table.Columns) HeaderCell(h.Cell(), column.Header, column.Align);
            });

            for (var r = 0; r < table.Rows.Count; r++)
            {
                var background = r % 2 == 1 ? Zebra : "#FFFFFF";
                var row = table.Rows[r];
                if (table.RowNumbers)
                    BodyCell(t.Cell(), background).Element(c => CellText(c, new PdfCell((r + 1).ToString(CultureInfo.InvariantCulture), Tone: PdfTone.Muted), PdfAlign.End));
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    var cell = i < row.Count ? row[i] : new PdfCell("—");
                    var align = table.Columns[i].Align;
                    BodyCell(t.Cell(), background).Element(c => CellText(c, cell, align));
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

    private static void HeaderCell(IContainer cell, string text, PdfAlign align) =>
        cell.Background(BrandDeep).PaddingVertical(5).PaddingHorizontal(5).AlignMiddle()
            .Text(t => { Align(t, align); t.Span(text).FontSize(7.5f).Bold().FontColor(Colors.White); });

    // ShowEntire: a cell's lines are never split between two pages (the row moves to the next page instead).
    private static IContainer BodyCell(IContainer cell, string background) =>
        cell.Background(background).BorderBottom(0.5f).BorderColor(Border).PaddingVertical(4).PaddingHorizontal(5).ShowEntire();

    private static IContainer FooterCell(IContainer cell) =>
        cell.Background(BrandSoft).BorderTop(1).BorderColor(Brand).PaddingVertical(5).PaddingHorizontal(5);

    private static void CellText(IContainer container, PdfCell cell, PdfAlign align) =>
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
