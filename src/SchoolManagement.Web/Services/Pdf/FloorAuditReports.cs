using System.Globalization;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>Floor Audit reports: the audit history, one area's inspections, and student movements (same filters as the pages).</summary>
public static class FloorAuditReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static PdfTone Tone(string tone) => tone switch { "ok" => PdfTone.Success, "bad" => PdfTone.Error, "info" => PdfTone.Info, "warn" => PdfTone.Warning, _ => PdfTone.Muted };

    private static string Range(DateOnly from, DateOnly to) =>
        from == to ? from.ToString("d MMM yyyy", C) : $"{from.ToString("d MMM yyyy", C)} – {to.ToString("d MMM yyyy", C)}";

    private static PdfCell When(InspectionBrief i) => new(i.Date.ToString("ddd d MMM yyyy", C), $"{i.SessionLabel} · {FloorAuditRounds.Label(i.Round)}", Strong: true);

    private static PdfCell Times(InspectionBrief i) => new(
        $"Scheduled {FloorAuditLabels.Time(i.ScheduledTime)}",
        i.CompletedAt is { } c ? $"Completed {FloorAuditLabels.Time(c)}{(i.LateMinutes is > 0 and var late ? $" (+{late} min)" : "")}" : $"Started {FloorAuditLabels.Time(i.StartedAt)} · not completed");

    private static PdfCell Status(InspectionBrief i) => PdfCell.Status(FloorAuditLabels.Area(i.AreaStatus), Tone(FloorAuditLabels.Tone(i.AreaStatus)));

    private static string Answers(IReadOnlyList<InspectionResponseView> responses) =>
        string.Join("; ", responses.Where(r => r.Value is not null).Select(r => $"{r.ItemName}: {r.Value}{(r.IsIssue ? " (issue)" : "")}"));

    public static PdfReport History(InspectionFilter f, IReadOnlyList<(InspectionBrief Inspection, IReadOnlyList<InspectionResponseView> Responses)> rows, string filterSummary)
    {
        var list = rows.Select(r => r.Inspection).ToList();
        return new PdfReport
        {
            Title = "Floor Audit Report",
            Subtitle = filterSummary,
            Eyebrow = "Daily Operations · Floor Audit",
            FileNameParts = ["Floor Audit Report", SchoolWeek.Iso(f.From), SchoolWeek.Iso(f.To)],
            Meta = [new("Dates", Range(f.From, f.To)), new("Inspections", list.Count.ToString(C))],
            Sections =
            [
                new PdfSummary
                {
                    Items =
                    [
                        new("Inspections", list.Count.ToString(C)),
                        new("Completed", list.Count(i => i.IsCompleted).ToString(C), PdfTone.Success),
                        new("In progress", list.Count(i => !i.IsCompleted).ToString(C), PdfTone.Info),
                        new("With issues", list.Count(i => i.Issues > 0).ToString(C), PdfTone.Error),
                        new("Issues found", list.Sum(i => i.Issues).ToString(C), PdfTone.Error),
                    ],
                },
                new PdfTable
                {
                    Title = "Inspections", RowNumbers = true, Compact = true, EmptyText = "No inspections match these filters.",
                    Columns = [new("Date · session · round", 1.6f), new("Floor · area", 1.5f), new("Times", 1.4f), new("Status", 0.9f), new("Inspector", 1.1f), new("Answers and issues", 3.6f)],
                    Rows = rows.Select(r => (IReadOnlyList<PdfCell>)
                    [
                        When(r.Inspection),
                        new PdfCell(r.Inspection.AreaName, $"{r.Inspection.FloorName} · {FloorAuditLabels.Kind(r.Inspection.Kind)}"),
                        Times(r.Inspection),
                        Status(r.Inspection),
                        r.Inspection.Inspector,
                        new PdfCell(Answers(r.Responses) is { Length: > 0 } a ? a : "—", r.Inspection.Observations is { } o ? $"Observations: {o}" : null),
                    ]).ToList(),
                },
            ],
        };
    }

    public static PdfReport Area(FloorAreaInfo area, IReadOnlyList<(InspectionBrief Inspection, IReadOnlyList<InspectionResponseView> Responses)> rows, DateOnly? from, DateOnly? to)
    {
        var list = rows.Select(r => r.Inspection).ToList();
        var dates = from is null && to is null ? "All dates" : Range(from ?? list.FirstOrDefault()?.Date ?? DateOnly.MinValue, to ?? list.LastOrDefault()?.Date ?? DateOnly.MaxValue);
        return new PdfReport
        {
            Title = "Area Inspection Report",
            Subtitle = $"{area.Name} · {area.TypeLabel} · {area.FloorName}",
            Eyebrow = "Daily Operations · Floor Audit",
            FileNameParts = ["Area Inspection Report", area.Name, from is null ? null : SchoolWeek.Iso(from.Value), to is null ? null : SchoolWeek.Iso(to.Value)],
            Meta = [new("Area", area.Name), new("Floor", area.FloorName), new("Dates", dates), new("Status", area.IsActive ? "Active" : "Inactive")],
            Sections =
            [
                new PdfSummary
                {
                    Items =
                    [
                        new("Inspections", list.Count.ToString(C)),
                        new("Completed", list.Count(i => i.IsCompleted).ToString(C), PdfTone.Success),
                        new("With issues", list.Count(i => i.Issues > 0).ToString(C), PdfTone.Error),
                        new("Floors inspected on", string.Join(", ", list.Select(i => i.FloorName).Distinct())),
                    ],
                },
                new PdfTable
                {
                    Title = "Inspections", RowNumbers = true, Compact = true, EmptyText = "This area has no inspections in these dates.",
                    Columns = [new("Date · session · round", 1.6f), new("Floor", 0.9f), new("Times", 1.4f), new("Status", 0.9f), new("Inspector", 1.1f), new("Answers and issues", 4f)],
                    Rows = rows.Select(r => (IReadOnlyList<PdfCell>)
                    [
                        When(r.Inspection), r.Inspection.FloorName, Times(r.Inspection), Status(r.Inspection), r.Inspection.Inspector,
                        new PdfCell(Answers(r.Responses) is { Length: > 0 } a ? a : "—", r.Inspection.Observations is { } o ? $"Observations: {o}" : null),
                    ]).ToList(),
                },
            ],
        };
    }

    public static PdfReport Movements(MovementFilter f, IReadOnlyList<MovementRow> rows, MovementCounts counts, string filterSummary) => new()
    {
        Title = "Student Movement Report",
        Subtitle = filterSummary,
        Eyebrow = "Daily Operations · Floor Audit · Student Access",
        FileNameParts = ["Student Movement Report", SchoolWeek.Iso(f.From), SchoolWeek.Iso(f.To)],
        Meta = [new("Dates", Range(f.From, f.To)), new("Movements", rows.Count.ToString(C))],
        Sections =
        [
            new PdfSummary
            {
                Items =
                [
                    new("Movements", counts.Total.ToString(C)),
                    new("Outside now", counts.Outside.ToString(C), PdfTone.Warning),
                    new("Overdue", counts.Overdue.ToString(C), PdfTone.Error),
                    new("Returned", counts.Returned.ToString(C), PdfTone.Success),
                    new("Average time outside", counts.AverageOutside is { } avg ? FloorAuditLabels.Duration(avg) : "—", PdfTone.Primary),
                ],
            },
            new PdfTable
            {
                Title = "Movements", RowNumbers = true, Compact = true, EmptyText = "No student movements match these filters.",
                Columns = [new("Student", 1.6f), new("From", 1.2f), new("To", 1.4f), new("Exit", 0.8f), new("Return", 0.8f), new("Duration", 0.9f), new("Status", 0.9f), new("Recorded by", 1.3f)],
                Rows = rows.Select(m => (IReadOnlyList<PdfCell>)
                [
                    new PdfCell(m.StudentName, $"{m.ClassName} · {m.Date.ToString("d MMM", C)}", Strong: true),
                    new PdfCell(m.FromName ?? m.ClassName, m.FromFloor),
                    new PdfCell(m.ToName, m.Custom ? "Custom destination" : m.ToFloor),
                    FloorAuditLabels.Time(m.ExitAt),
                    m.ReturnAt is { } back ? FloorAuditLabels.Time(back) : "Not returned",
                    new PdfCell(FloorAuditLabels.Duration(m.Duration), m.IsOpen ? "so far" : null),
                    PdfCell.Status(FloorAuditLabels.Movement(m.State), Tone(FloorAuditLabels.Tone(m.State))),
                    new PdfCell(m.RecordedBy ?? "—", m.ReturnedBy is { } rb ? $"Returned: {rb}" : m.VoidReason is { } vr ? $"Voided: {vr}" : null),
                ]).ToList(),
            },
            new PdfTable
            {
                Title = "By destination", Compact = true, EmptyText = "No destinations.",
                Columns = [new("Destination", 2f), new("Floor", 1.2f), new("Movements", 0.8f, PdfAlign.End), new("Outside now", 0.8f, PdfAlign.End)],
                Rows = counts.ByDestination.Select(d => (IReadOnlyList<PdfCell>)[d.Name, d.Floor, PdfCell.Num(d.Total), PdfCell.Num(d.Outside)]).ToList(),
            },
        ],
    };
}
