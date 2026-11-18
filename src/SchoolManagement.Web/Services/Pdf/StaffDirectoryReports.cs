using System.Globalization;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>
/// Staff → All School Staff → Download PDF: "All School Staff Directory", A4 landscape. Summary figures, then one section per
/// staff group (Management Team, Academic Staff, Administrative &amp; Support Staff) with each person once and all their
/// roles. Built from the same <see cref="StaffDirectoryExport"/> as the Excel workbook, so both formats always agree.
/// </summary>
public static class StaffDirectoryReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private static string N(int value) => value.ToString(C);

    public static PdfReport Directory(StaffDirectoryExport x, string schoolName)
    {
        var sections = new List<PdfSection>
        {
            new PdfSummary
            {
                Items =
                [
                    new("All Staff", N(x.Rows.Count), PdfTone.Primary),
                    new("Academic Staff", N(x.Count(StaffCategory.Academic))),
                    new("Management Team", N(x.Count(StaffCategory.Management))),
                    new("Administrative & Support Staff", N(x.Count(StaffCategory.Support))),
                    new("Heads of Department", N(x.Heads), PdfTone.Success),
                ],
            },
        };
        if (x.Filtered)
            sections.Add(new PdfNotice
            {
                Text = $"Filtered export: {N(x.Rows.Count)} of {N(x.AllStaff)} staff members match "
                    + string.Join(" · ", x.Filters.Select(f => $"{f.Label}: {f.Value}")) + ".",
                Tone = PdfTone.Info,
            });

        foreach (var c in StaffDirectoryExport.GroupOrder)
        {
            var people = x.In(c);
            // With a category tab selected, the other groups are left out rather than printed empty.
            if (people.Count == 0 && x.Filters.Any(f => f.Label == "Category")) continue;
            var management = c == StaffCategory.Management;
            sections.Add(new PdfTable
            {
                Title = StaffDirectoryExport.GroupName(c),
                Subtitle = people.Count == 1 ? "1 member" : $"{N(people.Count)} members",
                RowNumbers = true,
                Columns =
                [
                    new("Full Name", 1.55f), new(management ? "Position" : "Job Title", 1.4f), new("Department", 1.0f),
                    new("Roles & Responsibilities", 1.3f), new("Contact Number", 1.55f), new("Category", 1.2f),
                ],
                Rows = people.Select(p => (IReadOnlyList<PdfCell>)
                [
                    new PdfCell(p.FullName, Strong: true),
                    p.Title ?? (management ? "No position yet" : "Not set"),
                    p.Department ?? "No department",
                    p.Roles.Count == 0 ? "—" : new PdfCell(string.Join("\n", p.Roles)),
                    new PdfCell(p.ContactNumber ?? "—", p.Email),
                    new PdfCell(p.CategoryName, p.Employment),
                ]).ToList(),
                EmptyText = $"No {StaffDirectoryExport.GroupName(c)} members {(x.Filtered ? "match the filters" : "yet")}.",
            });
        }

        return new PdfReport
        {
            Title = "All School Staff Directory",
            Subtitle = x.Filtered ? "Filtered staff directory" : "Academic staff, Management Team, and administrative and support staff",
            Eyebrow = schoolName,
            FooterShowsYear = true,
            Meta = [new("Staff members", N(x.Rows.Count)), .. x.Filters.Select(f => new PdfMeta(f.Label, f.Value))],
            Sections = sections,
            FileNameParts = [StaffDirectoryExport.FileName(Common.SchoolTime.Today, "pdf")[..^4]],
        };
    }
}
