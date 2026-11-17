using System.Globalization;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>A Main Duty's final coverage report: what was covered, what was not (down to the area), why, and what is still open.</summary>
public static class DutyCoverageReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static PdfTone Tone(DutyStatus s) => s switch
    {
        DutyStatus.Covered => PdfTone.Success,
        DutyStatus.NotCovered => PdfTone.Error,
        DutyStatus.InProgress => PdfTone.Info,
        _ => PdfTone.Muted,
    };

    public static string Label(DutyStatus s) => DutyStatuses.Label(s).ToUpperInvariant();

    private static string Covered(SubDutyView s) =>
        string.Join("; ", s.Points.Select(p => $"{p.Area}: {p.Staff}{(p.CoveredAt is { } at ? $", recorded {SchoolTime.Current.ToLocal(at.At).ToString("h:mm tt", C)}{(at.By is null ? "" : " by " + at.By)}" : "")}"));

    /// <summary>Every uncovered area of a Sub-Duty with its reason (reported, or what the records show).</summary>
    public static IReadOnlyList<string> Gaps(SubDutyView s)
    {
        var lines = new List<string>();
        if (s.Reason is not null) lines.Add(s.Reason);
        foreach (var p in s.UncoveredPoints)
            lines.Add($"{p.Area} ({p.Staff}): " + (p.Report is { } r ? $"{r.ReasonLabel}{(r.Notes is null ? "" : $" — {r.Notes}")}" : p.Reason));
        foreach (var r in s.AreaReports) lines.Add($"{r.Area}: {r.ReasonLabel}{(r.Notes is null ? "" : $" — {r.Notes}")}");
        return lines;
    }

    public static PdfReport Build(DutyDay day, MainDutyView m)
    {
        var subs = m.Subs.Where(s => s.Scheduled).ToList();
        var covered = subs.Where(s => s.Status == DutyStatus.Covered).ToList();
        var notCovered = subs.Where(s => s.Status == DutyStatus.NotCovered).ToList();
        var open = subs.Where(s => s.Status is DutyStatus.Pending or DutyStatus.InProgress).ToList();
        PdfCell Name(SubDutyView s) => new(s.Info.Name, $"{s.Times} · {(s.Required ? "Required" : "Optional")}{(s.DepartmentName is null ? "" : " · " + s.DepartmentName)}", Strong: true);
        var sections = new List<PdfSection>
        {
            new PdfSummary
            {
                Items =
                [
                    new("Overall status", Label(m.Status), Tone(m.Status)),
                    new("Required Sub-Duties", m.Required.Count.ToString(C)),
                    new("Covered", m.RequiredCovered.ToString(C), PdfTone.Success),
                    new("Not covered", m.RequiredNotCovered.ToString(C), PdfTone.Error),
                    new("Pending / in progress", m.RequiredOpen.ToString(C), PdfTone.Muted),
                    new("Coverage", m.Percent is { } p ? p.ToString("0", C) + "%" : "—", PdfTone.Primary),
                ],
            },
            new PdfTable
            {
                Title = $"Covered ({covered.Count})", RowNumbers = true,
                Columns = [new("Sub-Duty", 2.2f), new("Staff and coverage recorded", 3.6f)],
                Rows = covered.Select(s => (IReadOnlyList<PdfCell>)[Name(s), Covered(s)]).ToList(),
                EmptyText = "No Sub-Duty was covered.",
            },
            new PdfTable
            {
                Title = $"Not covered ({notCovered.Count})", RowNumbers = true,
                Columns = [new("Sub-Duty", 2.2f), new("Uncovered area and reason", 3.6f)],
                Rows = notCovered.Select(s => (IReadOnlyList<PdfCell>)[Name(s), string.Join("\n", Gaps(s))]).ToList(),
                EmptyText = "Nothing was left uncovered.",
            },
        };
        if (open.Count > 0)
            sections.Add(new PdfTable
            {
                Title = $"Not finished yet ({open.Count})", RowNumbers = true,
                Columns = [new("Sub-Duty", 2.2f), new("Status", 1.2f), new("Staff", 2.4f)],
                Rows = open.Select(s => (IReadOnlyList<PdfCell>)[Name(s), PdfCell.Status(DutyStatuses.Label(s.Status), Tone(s.Status)), string.Join(", ", s.Staff)]).ToList(),
            });
        sections.Add(new PdfNotice
        {
            Text = "Statuses are calculated from the school clock (Africa/Cairo) and the recorded attendance: a Sub-Duty is Covered only after its end time " +
                   "with someone on duty recorded present at every point; a Main Duty only after its own end time with every required Sub-Duty covered. " +
                   "Optional Sub-Duties never decide the Main Duty. Reasons explain a gap; they never count as coverage.",
            Tone = PdfTone.Info,
        });
        return new PdfReport
        {
            Title = $"{m.Name} — Coverage Report",
            Subtitle = day.Date.ToString("dddd d MMMM yyyy", C),
            Meta = [new("Scheduled", m.Times), new("Status", Label(m.Status)), new("Departments", m.Departments.Count == 0 ? "—" : string.Join(", ", m.Departments))],
            Sections = sections,
            FileNameParts = [m.Name, "Coverage", SchoolWeek.Iso(day.Date)],
        };
    }
}
