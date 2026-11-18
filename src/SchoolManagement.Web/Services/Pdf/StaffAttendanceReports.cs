using System.Globalization;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>Staff Attendance · History & Reports as a PDF: the same filters as the page, every matching line (not only the page shown).</summary>
public static class StaffAttendanceReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static IReadOnlyList<PdfCell> R(params PdfCell[] cells) => cells;

    public static PdfTone Tone(StaffAttendanceStatus? s) => s switch
    {
        StaffAttendanceStatus.Present => PdfTone.Success, StaffAttendanceStatus.Late => PdfTone.Warning,
        StaffAttendanceStatus.Absent => PdfTone.Error, _ => PdfTone.Muted,
    };

    public static string Dates(DateOnly from, DateOnly to) =>
        from == to ? from.ToString("d MMM yyyy", C) : $"{from.ToString("d MMM yyyy", C)} – {to.ToString("d MMM yyyy", C)}";

    public static string Percent(double? p) => p is { } x ? $"{x.ToString("0.#", C)}%" : "—";

    public static PdfReport History(StaffAttendanceHistory h, string scope) => new()
    {
        Title = "Staff Attendance Report",
        Subtitle = $"{scope} · {h.PeriodLabel}",
        ShowAcademicYear = false,
        Meta =
        [
            new("Period", $"{h.PeriodLabel} · {Dates(h.From, h.To)}"),
            new("Staff", scope),
            new("School days", h.SchoolDays.ToString(C)),
            new("Lines", h.Lines.Count.ToString(C)),
        ],
        Sections =
        [
            new PdfSummary
            {
                Items =
                [
                    new("Expected staff-days", h.Totals.Expected.ToString(C)),
                    new("Present", h.Totals.Present.ToString(C), PdfTone.Success),
                    new("Late", h.Totals.Late.ToString(C), h.Totals.Late > 0 ? PdfTone.Warning : PdfTone.Default),
                    new("Absent", h.Totals.Absent.ToString(C), h.Totals.Absent > 0 ? PdfTone.Error : PdfTone.Default),
                    new("Not Recorded", h.Totals.NotRecorded.ToString(C), PdfTone.Muted),
                ],
            },
            new PdfNotice
            {
                Text = "Only recorded attendance is counted. Not Recorded means no decision was saved for that staff member on that school day; it is never counted as Absent. Attendance % = (Present + Late) ÷ recorded days.",
                Tone = PdfTone.Muted,
            },
            new PdfTable
            {
                Title = "By staff member",
                Columns = [new("Staff member", 2.2f), new("Department", 1.6f), new("Expected", 0.9f, PdfAlign.End), new("Present", 0.9f, PdfAlign.End),
                    new("Late", 0.8f, PdfAlign.End), new("Absent", 0.8f, PdfAlign.End), new("Not Recorded", 1, PdfAlign.End), new("Attendance %", 1, PdfAlign.End)],
                Rows = h.ByStaff.Select(s => R(new PdfCell(s.Name, Strong: true), s.Group, PdfCell.Num(s.Counts.Expected), PdfCell.Num(s.Counts.Present),
                    PdfCell.Num(s.Counts.Late), PdfCell.Num(s.Counts.Absent), PdfCell.Num(s.Counts.NotRecorded), Percent(s.Percent))).ToList(),
                EmptyText = "No staff expected in this period.",
            },
            new PdfTable
            {
                Title = "Daily records",
                PageBreakBefore = h.Lines.Count > 0,
                RowNumbers = true,
                Columns = [new("Date", 1.2f), new("Staff member", 2), new("Department", 1.5f), new("Status", 1), new("Arrival", 0.9f), new("Recorded by", 1.2f), new("Corrected", 0.8f, PdfAlign.End)],
                Rows = h.Lines.Select(l => R(l.Date.ToString("ddd d MMM yyyy", C), new PdfCell(l.Name, Strong: true), l.Group,
                    PdfCell.Status(StaffAttendanceStatuses.Label(l.Status), Tone(l.Status)), l.ArrivalTime is { } t ? Arrivals.Time(t) : "—",
                    l.RecordedBy ?? "—", l.Corrections > 0 ? PdfCell.Num(l.Corrections) : "—")).ToList(),
                EmptyText = "No staff attendance matches these filters.",
            },
            new PdfTable
            {
                Title = "Correction history",
                Columns = [new("Date", 1.1f), new("Staff member", 1.8f), new("Before", 1.3f), new("After", 1.3f), new("Reason", 2.2f), new("Corrected", 1.5f)],
                Rows = h.Corrections.Select(c => R(c.Date.ToString("d MMM yyyy", C), new PdfCell(c.StaffName, Strong: true), c.Before, c.After, c.Reason ?? "—",
                    $"{SchoolTime.Current.ToLocal(c.At).ToString("d MMM, h:mm tt", C)}{(c.By is null ? "" : $" · {c.By}")}")).ToList(),
                EmptyText = "No corrections in this period.",
            },
        ],
        FileNameParts = ["Staff Attendance Report", scope, Dates(h.From, h.To)],
    };
}
