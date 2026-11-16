using SchoolManagement.Web.Common;
using System.Globalization;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>
/// The official Student Incident Report PDF, in the structure and wording of the school's official form: the school name
/// and "OFFICIAL STUDENT INCIDENT REPORT" at the top; Incident Information; 1. Students Involved; 2. Chronological Timeline
/// of Events; 3. Investigation Findings &amp; Administrative Actions; 5. Administrative Sign-off &amp; Record Closure (the
/// numbering of the official form); the confidentiality footer on every page. Landscape, every saved value in full,
/// table headers repeated on each page and rows never split.
/// </summary>
public static class IncidentReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>The footer line of the official form.</summary>
    public static string Confidential(string schoolName) =>
        $"Confidential Student Incident Record • {schoolName} • For Official Administrative & Pastoral Use Only";

    public static PdfReport Official(IncidentDetails d, string schoolName)
    {
        static PdfTone SeverityTone(IncidentSeverity s) => s switch { IncidentSeverity.Level1 => PdfTone.Primary, IncidentSeverity.Level2 => PdfTone.Warning, _ => PdfTone.Error };
        static PdfTone StatusTone(IncidentStatus s) => s switch
        {
            IncidentStatus.Resolved => PdfTone.Success, IncidentStatus.UnderMonitoring => PdfTone.Info, IncidentStatus.UnderInvestigation => PdfTone.Warning, _ => PdfTone.Muted,
        };
        static PdfTone ActionTone(IncidentActionStatus s) => s switch
        {
            IncidentActionStatus.Pending => PdfTone.Muted, IncidentActionStatus.InProgress => PdfTone.Warning, _ => PdfTone.Success,
        };
        static PdfTone ApprovalTone(IncidentApproval a) => a switch
        {
            IncidentApproval.Approved or IncidentApproval.ApprovedWithConditions => PdfTone.Success, IncidentApproval.NotApproved => PdfTone.Error, _ => PdfTone.Muted,
        };
        var school = string.IsNullOrWhiteSpace(d.SignOffSchool) ? schoolName : d.SignOffSchool!;
        var record = d.IsDraft ? "Draft" : d.IsClosed ? "Closed" : "Open";

        return new PdfReport
        {
            Eyebrow = schoolName.ToUpperInvariant(),
            Title = "OFFICIAL STUDENT INCIDENT REPORT",
            Subtitle = $"Reference ID: {d.Reference}",
            Meta = [new("Date of Incident", d.Date.ToString("d MMM yyyy", C)), new("Record", record)],
            FooterNote = Confidential(schoolName),
            Sections =
            [
                new PdfFields
                {
                    Title = "Incident Information",
                    Items =
                    [
                        new("Incident Category", new PdfCell(d.Category, Strong: true)),
                        new("Date of Incident", d.Date.ToString("dddd d MMMM yyyy", C)),
                        new("Reporting Dept.", d.Department),
                        new("Severity Level", new PdfCell(IncidentLabels.Severity(d.Severity), Tone: SeverityTone(d.Severity), Badge: true)),
                        new("Reference ID", new PdfCell(d.Reference, Strong: true)),
                        new("Incident Status", new PdfCell(IncidentLabels.Status(d.Status), Tone: StatusTone(d.Status), Badge: true)),
                    ],
                },
                new PdfTable
                {
                    Title = "1. STUDENTS INVOLVED",
                    RowNumbers = true,
                    Columns = [new("Student Name", 1.5f), new("Grade & Section", 1.2f), new("Role Classification", 1.2f), new("Reported Conduct & Involvement", 4.6f)],
                    Rows = d.Students.Select(s => (IReadOnlyList<PdfCell>)
                        [new PdfCell(s.Name, Strong: true), new PdfCell(s.GradeSection, s.ClassName), s.RoleLabel, s.Involvement]).ToList(),
                    EmptyText = "No students recorded.",
                },
                new PdfTable
                {
                    Title = "2. CHRONOLOGICAL TIMELINE OF EVENTS",
                    RowNumbers = true,
                    Columns = [new("Time / Period", 1.2f), new("Location", 1.3f), new("Students Present", 1.8f), new("Factual Account of Incident", 4.2f)],
                    Rows = d.Timeline.Select(t => (IReadOnlyList<PdfCell>)
                        [new PdfCell(t.TimePeriod, t.Time is { } tm ? TaskLabels.Time(tm) : null, Strong: true), t.Location, t.PresentText, t.Account]).ToList(),
                    EmptyText = "No timeline events recorded.",
                },
                new PdfTable
                {
                    Title = "3. INVESTIGATION FINDINGS & ADMINISTRATIVE ACTIONS",
                    RowNumbers = true,
                    Columns = [new("Action Item", 1.5f), new("Investigation Finding & Action", 3.2f), new("Restorative & Disciplinary Details", 3), new("Status", 1.1f, PdfAlign.Center)],
                    Rows = d.Actions.Select(a => (IReadOnlyList<PdfCell>)
                        [new PdfCell(a.ActionItem, Strong: true), a.Finding, a.Details, PdfCell.Status(IncidentLabels.ActionStatus(a.Status), ActionTone(a.Status))]).ToList(),
                    EmptyText = "No findings or actions recorded.",
                },
                new PdfFields
                {
                    Title = "5. ADMINISTRATIVE SIGN-OFF & RECORD CLOSURE",
                    Subtitle = "ADMINISTRATIVE APPROVAL & ENDORSEMENT",
                    Items =
                    [
                        new("Prepared By", d.PreparedByDisplay),
                        new("School", school),
                        new("Date of Report", d.ReportDate?.ToString("d MMMM yyyy", C)),
                        new("Reviewed By", new PdfCell(d.ReviewedByName ?? "—", IncidentLabels.Approval(d.Approval) + (d.ApprovalNotes is null ? "" : " · " + d.ApprovalNotes),
                            d.ReviewedByName is null ? PdfTone.Muted : ApprovalTone(d.Approval))),
                    ],
                },
            ],
            FileNameParts = ["Incident Report", d.Reference],
        };
    }
}
