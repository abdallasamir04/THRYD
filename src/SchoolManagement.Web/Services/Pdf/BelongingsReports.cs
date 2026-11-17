using System.Globalization;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>
/// Personal and Academic Belongings reports (History & Reports → Download PDF). Built from the same filtered history as
/// the page, with every record of the selected scope (not only the page shown). The school name heads every page.
/// </summary>
public static class BelongingsReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static IReadOnlyList<PdfCell> R(params PdfCell[] cells) => cells;

    private static PdfTone Tone(ItemStatus? s) => s switch
    {
        ItemStatus.Green => PdfTone.Success, ItemStatus.Red => PdfTone.Error, ItemStatus.NotApplicable => PdfTone.Muted, _ => PdfTone.Warning,
    };

    private static PdfCell Status(ItemStatus? s) => PdfCell.Status(BelongingsStatus.Label(s), Tone(s));

    private static PdfTone ReadinessTone(double? percent) => percent switch
    {
        null => PdfTone.Muted, >= 80 => PdfTone.Success, >= 50 => PdfTone.Warning, _ => PdfTone.Error,
    };

    private static PdfCell Readiness(ReadinessCounts r) => new(ReadinessCounts.Text(r.Percent), Tone: ReadinessTone(r.Percent), Strong: true);

    private static IReadOnlyList<PdfMeta> Meta(BelongingsScope s, string records) =>
    [
        new("Academic Year", s.YearName ?? "—"),
        new("Quarter", s.QuarterName ?? "All quarters in the period"),
        new("Period", $"{s.PeriodLabel} · {s.DatesLabel}"),
        new("Scope", s.Audience),
        new("Records", records),
    ];

    private static PdfTable Groups(string title, string column, IReadOnlyList<GroupStat> groups) => new()
    {
        Title = title,
        Columns = [new(column, 2.4f), new("Records", 1, PdfAlign.End), new("Complete", 1, PdfAlign.End), new("Completion", 1, PdfAlign.End), new("Readiness", 1, PdfAlign.End)],
        Rows = groups.Select(g => R(new PdfCell(g.Label, Strong: true), PdfCell.Num(g.Records), PdfCell.Num(g.Complete), $"{g.CompletePercent}%", Readiness(g.Readiness))).ToList(),
        EmptyText = "No records in this period.",
    };

    private static PdfTable Trend(IReadOnlyList<TrendPoint> points) => new()
    {
        Title = "Weekly trend",
        Columns = [new("School week", 2.4f), new("Records", 1, PdfAlign.End), new("Complete", 1, PdfAlign.End), new("Completion", 1, PdfAlign.End), new("Readiness", 1, PdfAlign.End)],
        Rows = points.Select(p => R(p.Label, PdfCell.Num(p.Records), PdfCell.Num(p.Complete), $"{p.CompletePercent}%", Readiness(p.Readiness))).ToList(),
        EmptyText = "No records in this period.",
    };

    private const string ReadinessNote = "Readiness = items Available ÷ items checked (Available + Missing). N/A items and items not checked are never counted as Missing. A check is complete when every item has a status.";

    public static PdfReport Personal(PersonalHistory h) => new()
    {
        Title = "Personal Belongings Report",
        Subtitle = $"{h.Scope.Audience} · {h.Scope.PeriodLabel}",
        ShowAcademicYear = false,
        Meta = Meta(h.Scope, h.Total.ToString(C)),
        Sections =
        [
            new PdfSummary
            {
                Items =
                [
                    new("Student-days recorded", h.Total.ToString(C)),
                    new("Complete checks", h.Complete.ToString(C), PdfTone.Success),
                    new("Incomplete checks", h.Incomplete.ToString(C), h.Incomplete > 0 ? PdfTone.Warning : PdfTone.Default),
                    new("With missing items", h.WithMissing.ToString(C), h.WithMissing > 0 ? PdfTone.Error : PdfTone.Default),
                    new("Readiness", ReadinessCounts.Text(h.Readiness.Percent), ReadinessTone(h.Readiness.Percent)),
                ],
            },
            new PdfNotice { Text = ReadinessNote, Tone = PdfTone.Muted },
            Groups("Readiness by class", "Class", h.ByClass),
            new PdfTable
            {
                Title = "Students with repeated missing items",
                Columns = [new("Student", 2.2f), new("Class", 1), new("Days with missing items", 1.2f, PdfAlign.End), new("Items", 3)],
                Rows = h.Repeated.Select(x => R(new PdfCell(x.Name, Strong: true), x.ClassName, PdfCell.Num(x.Days), x.Items)).ToList(),
                EmptyText = "No student had missing items on more than one day.",
            },
            Trend(h.Trend),
            new PdfTable
            {
                Title = "Records",
                PageBreakBefore = h.Rows.Count > 0,
                RowNumbers = true,
                Columns =
                [
                    new("Date", 1.1f), new("Class", 0.8f), new("Student", 1.9f),
                    .. PersonalItems.All.Select(i => new PdfColumn(i.Label, 0.95f)),
                    new("Notes", 1.9f), new("Recorded by", 1.1f),
                ],
                Rows = h.Rows.Select(x => R(
                    [
                        x.Date.ToString("ddd d MMM yyyy", C), x.ClassName, new PdfCell(x.StudentName, Strong: true),
                        .. x.Items.Select(Status),
                        x.Notes, x.UpdatedBy ?? x.RecordedBy,
                    ])).ToList(),
                EmptyText = "No Personal Belongings records match these filters.",
            },
        ],
        FileNameParts = ["Personal Belongings Report", h.Scope.Audience, h.Scope.DatesLabel],
    };

    public static PdfReport Academic(AcademicHistory h) => new()
    {
        Title = "Academic Belongings Report",
        Subtitle = $"{h.Scope.Audience} · {h.Scope.PeriodLabel}",
        ShowAcademicYear = false,
        Meta = Meta(h.Scope, h.Checks.ToString(C)),
        Sections =
        [
            new PdfSummary
            {
                Items =
                [
                    new("Lessons recorded", h.Lessons.ToString(C)),
                    new("Student lesson checks", h.Checks.ToString(C)),
                    new("Complete", h.Complete.ToString(C), PdfTone.Success),
                    new("Incomplete", h.Incomplete.ToString(C), h.Incomplete > 0 ? PdfTone.Warning : PdfTone.Default),
                    new("Missing Book", h.MissingBook.ToString(C), h.MissingBook > 0 ? PdfTone.Error : PdfTone.Default),
                    new("Missing Copy Book", h.MissingCopyBook.ToString(C), h.MissingCopyBook > 0 ? PdfTone.Error : PdfTone.Default),
                    new("Readiness", ReadinessCounts.Text(h.Readiness.Percent), ReadinessTone(h.Readiness.Percent)),
                ],
            },
            new PdfNotice { Text = ReadinessNote + " Rows marked \"Daily record\" were recorded per day and subject, before lesson-based checks.", Tone = PdfTone.Muted },
            Groups("Readiness by class", "Class", h.ByClass),
            Groups("Readiness by subject", "Subject", h.BySubject),
            Groups("Readiness by teacher", "Teacher", h.ByTeacher),
            Groups("Readiness by quarter", "Quarter", h.ByQuarter),
            Groups("Monthly trend", "Month", h.ByMonth),
            Trend(h.Trend),
            new PdfTable
            {
                Title = "Student lesson records",
                PageBreakBefore = h.Rows.Count > 0,
                RowNumbers = true,
                Columns =
                [
                    new("Date", 1.05f), new("Class", 0.7f), new("Period", 0.8f), new("Time", 1.05f), new("Subject", 1.1f), new("Teacher", 1.4f),
                    new("Student", 1.7f), new("Book", 0.85f), new("Copy Book", 0.85f), new("Notes", 1.5f),
                ],
                Rows = h.Rows.Select(x => R(
                    x.Date.ToString("ddd d MMM yyyy", C), x.ClassName, x.Period, x.Times, x.Subject, x.TeacherName,
                    new PdfCell(x.StudentName, Strong: true), Status(x.Book), Status(x.CopyBook), x.Notes)).ToList(),
                EmptyText = "No Academic Belongings records match these filters.",
            },
        ],
        FileNameParts = ["Academic Belongings Report", h.Scope.Audience, h.Scope.DatesLabel],
    };
}
