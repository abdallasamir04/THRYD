using System.Globalization;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>
/// The official incident report PDF: school name and title at the top, then the five parts of the report
/// with every saved value (full texts, all rows), and a confidentiality line on every page.
/// </summary>
public static class IncidentReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public const string Confidential =
        "CONFIDENTIAL — This report contains personal information about students and is intended for official administrative use only. Do not copy or share it outside authorised school staff.";

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
        var closure = d.IsClosed
            ? new PdfCell("Closed", $"{d.ClosedAt?.ToLocalTime().ToString("d MMM yyyy, HH:mm", C)}{(d.ClosedBy is null ? "" : " · " + d.ClosedBy)}", PdfTone.Success, Badge: true)
            : new PdfCell("Open", d.ClosureBlockers.Count == 0 ? "Ready to close" : "Investigation / administrative steps in progress", PdfTone.Warning, Badge: true);

        return new PdfReport
        {
            Eyebrow = schoolName,
            Title = "OFFICIAL STUDENT INCIDENT REPORT",
            Subtitle = $"Reference ID: {d.Reference}",
            Meta = [new("Incident date", d.Date.ToString("d MMM yyyy", C)), new("Record", d.IsClosed ? "Closed" : "Open")],
            FooterNote = Confidential,
            Sections =
            [
                new PdfFields
                {
                    Title = "Incident Information",
                    Items =
                    [
                        new("School Name", new PdfCell(schoolName, Strong: true)),
                        new("Report Title", "Official Student Incident Report"),
                        new("Incident Category", new PdfCell(d.Category, Strong: true)),
                        new("Date of Incident", d.Date.ToString("dddd d MMMM yyyy", C)),
                        new("Reporting Department", d.Department),
                        new("Severity Level", new PdfCell(IncidentLabels.Severity(d.Severity), Tone: SeverityTone(d.Severity), Badge: true)),
                        new("Reference ID", new PdfCell(d.Reference, Strong: true)),
                        new("Incident Status", new PdfCell(IncidentLabels.Status(d.Status), Tone: StatusTone(d.Status), Badge: true)),
                    ],
                },
                new PdfTable
                {
                    Title = "Students Involved",
                    RowNumbers = true,
                    Columns = [new("Student Name", 1.5f), new("Grade & Section", 1.2f), new("Role Classification", 1.2f), new("Reported Conduct & Involvement", 4.6f)],
                    Rows = d.Students.Select(s => (IReadOnlyList<PdfCell>)
                        [new PdfCell(s.Name, Strong: true), new PdfCell(s.GradeSection, s.ClassName), s.RoleLabel, s.Involvement]).ToList(),
                    EmptyText = "No students recorded.",
                },
                new PdfTable
                {
                    Title = "Chronological Timeline of Events",
                    RowNumbers = true,
                    Columns = [new("Time / Period", 1.2f), new("Location", 1.3f), new("Students Present", 1.8f), new("Factual Account of Incident", 4.2f)],
                    Rows = d.Timeline.Select(t => (IReadOnlyList<PdfCell>)
                        [new PdfCell(t.TimePeriod, t.Time?.ToString("HH:mm", C), Strong: true), t.Location, t.PresentText, t.Account]).ToList(),
                    EmptyText = "No timeline events recorded.",
                },
                new PdfTable
                {
                    Title = "Investigation Findings & Administrative Actions",
                    RowNumbers = true,
                    Columns = [new("Action Item", 1.5f), new("Investigation Finding & Action", 3.2f), new("Restorative & Disciplinary Details", 3), new("Status", 1.1f, PdfAlign.Center)],
                    Rows = d.Actions.Select(a => (IReadOnlyList<PdfCell>)
                        [new PdfCell(a.ActionItem, Strong: true), a.Finding, a.Details, PdfCell.Status(IncidentLabels.ActionStatus(a.Status), ActionTone(a.Status))]).ToList(),
                    EmptyText = "No findings or actions recorded.",
                },
                new PdfFields
                {
                    Title = "Administrative Sign-off & Record Closure",
                    Items =
                    [
                        new("Prepared By", d.PreparedByName),
                        new("School", d.SignOffSchool ?? schoolName),
                        new("Date of Report", d.ReportDate?.ToString("d MMMM yyyy", C)),
                        new("Reviewed By", d.ReviewedByName),
                        new("Administrative Approval / Endorsement", new PdfCell(IncidentLabels.Approval(d.Approval), d.ApprovalNotes,
                            d.Approval switch { IncidentApproval.Approved or IncidentApproval.ApprovedWithConditions => PdfTone.Success, IncidentApproval.NotApproved => PdfTone.Error, _ => PdfTone.Muted }, Badge: true), Wide: true),
                        new("Record Closure Status", closure, Wide: true),
                    ],
                },
            ],
            FileNameParts = ["Incident Report", d.Reference],
        };
    }
}
