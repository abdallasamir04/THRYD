using System.Globalization;
using SchoolManagement.Web.Common;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>Attendance History &amp; Reports as PDFs: one student, a class / grade, or the whole school, over a date or a period.</summary>
public static class AttendanceReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public const string Rules =
        "Not Recorded = no attendance record for that school day (never counted as an absence). Attendance % = (Present + Late) ÷ days with a record. " +
        "School days exclude weekends and school holidays; a student counts from the day they were added.";

    private static string Period(DateOnly from, DateOnly to) =>
        from == to ? from.ToString("dddd d MMMM yyyy", C) : $"{from.ToString("d MMM yyyy", C)} – {to.ToString("d MMM yyyy", C)}";

    private static PdfCell Status(ArrivalOutcome? o) => o switch
    {
        ArrivalOutcome.Present => PdfCell.Status("Present", PdfTone.Success),
        ArrivalOutcome.Late => PdfCell.Status("Late", PdfTone.Warning),
        ArrivalOutcome.Absent => PdfCell.Status("Absent", PdfTone.Error),
        _ => PdfCell.Status("Not Recorded", PdfTone.Muted),
    };

    private static PdfSummary Counts(AttendanceCounts k, int? days = null) => new()
    {
        Items =
        [
            .. days is { } d ? new[] { new PdfStat("School days", d.ToString(C)) } : [],
            new("Present", k.Present.ToString(C), PdfTone.Success), new("Late", k.Late.ToString(C), PdfTone.Warning),
            new("Absent", k.Absent.ToString(C), PdfTone.Error), new("Not Recorded", k.NotRecorded.ToString(C), PdfTone.Muted),
            new("Attendance", k.PercentText, PdfTone.Primary),
        ],
    };

    private static IReadOnlyList<PdfCell> Group(GroupRow g) =>
        [new PdfCell(g.Label, Strong: true), g.Students.ToString(C), g.Counts.Present.ToString(C), g.Counts.Late.ToString(C),
         g.Counts.Absent.ToString(C), g.Counts.NotRecorded.ToString(C), new PdfCell(g.Counts.PercentText, Strong: true)];

    private static readonly IReadOnlyList<PdfColumn> GroupColumns =
        [new("Name", 2.2f), new("Students", 1, PdfAlign.End), new("Present", 1, PdfAlign.End), new("Late", 1, PdfAlign.End),
         new("Absent", 1, PdfAlign.End), new("Not Recorded", 1.2f, PdfAlign.End), new("Attendance %", 1.2f, PdfAlign.End)];

    public static PdfReport Student(StudentAttendanceReport r) => new()
    {
        Title = "Student Attendance Report",
        Subtitle = r.Name,
        Meta = [new("Student ID", r.StudentId.ToString(C)), new("Class", r.ClassName), new("Period", Period(r.From, r.To))],
        Sections =
        [
            Counts(r.Counts, r.Counts.Days),
            new PdfTable
            {
                Title = "Daily attendance",
                RowNumbers = true,
                Columns = [new("Date", 1.3f), new("Day", 1), new("Status", 1.1f), new("Arrival time", 1), new("Notes", 2.4f), new("Corrections", 2.2f)],
                Rows = r.Days.Select(d => (IReadOnlyList<PdfCell>)
                [
                    d.Date.ToString("d MMM yyyy", C), d.Date.ToString("dddd", C), Status(d.Outcome),
                    d.Time is { } t ? Arrivals.Time(t) : "—", d.Notes,
                    d.Corrections > 0 ? new PdfCell($"Corrected {d.Corrections}×", d.LastCorrection) : "—",
                ]).ToList(),
                EmptyText = "No school days in this period.",
            },
            new PdfNotice { Text = Rules, Tone = PdfTone.Info },
        ],
        FileNameParts = ["Attendance", r.Name, SchoolWeek.Iso(r.From), r.From == r.To ? null : SchoolWeek.Iso(r.To)],
    };

    public static PdfReport Group(GroupAttendanceReport r)
    {
        var sections = new List<PdfSection> { Counts(r.Counts, r.SchoolDays) };
        if (r.Scope != "class")
        {
            sections.Add(new PdfTable { Title = "Attendance by grade", Columns = GroupColumns, Rows = r.ByGrade.Select(Group).ToList(), EmptyText = "No students." });
            sections.Add(new PdfTable { Title = "Attendance by class", Columns = GroupColumns, Rows = r.ByClass.Select(Group).ToList(), EmptyText = "No students." });
        }
        sections.Add(r.SingleDay
            ? new PdfTable
            {
                Title = "Students",
                RowNumbers = true,
                Columns = [new("Student", 2.4f), new("Student ID", 1), new("Class", 1), new("Status", 1.2f), new("Arrival time", 1.1f)],
                Rows = r.StudentRows.Select(x => (IReadOnlyList<PdfCell>)
                    [new PdfCell(x.Name, Strong: true), x.StudentId.ToString(C), x.ClassName, Status(x.DayOutcome), x.DayTime is { } t ? Arrivals.Time(t) : "—"]).ToList(),
                EmptyText = "No students match.",
            }
            : new PdfTable
            {
                Title = "Students",
                RowNumbers = true,
                Columns = [new("Student", 2.4f), new("Student ID", 1), new("Class", 1), new("Days", 0.8f, PdfAlign.End), new("Present", 0.9f, PdfAlign.End),
                    new("Late", 0.8f, PdfAlign.End), new("Absent", 0.9f, PdfAlign.End), new("Not Recorded", 1.1f, PdfAlign.End), new("Attendance %", 1.1f, PdfAlign.End)],
                Rows = r.StudentRows.Select(x => (IReadOnlyList<PdfCell>)
                [
                    new PdfCell(x.Name, Strong: true), x.StudentId.ToString(C), x.ClassName, x.Counts.Days.ToString(C), x.Counts.Present.ToString(C),
                    x.Counts.Late.ToString(C), x.Counts.Absent.ToString(C), x.Counts.NotRecorded.ToString(C), new PdfCell(x.Counts.PercentText, Strong: true),
                ]).ToList(),
                EmptyText = "No students match.",
            });
        sections.Add(new PdfNotice { Text = Rules, Tone = PdfTone.Info });
        return new PdfReport
        {
            Title = r.Scope switch { "class" => "Class Attendance Report", "grade" => "Grade Attendance Report", _ => "Whole-School Attendance Report" },
            Subtitle = r.Title,
            Meta = [new("Period", Period(r.From, r.To)), new("Students", r.Students.ToString(C))],
            Sections = sections,
            FileNameParts = ["Attendance", r.Title, SchoolWeek.Iso(r.From), r.From == r.To ? null : SchoolWeek.Iso(r.To)],
        };
    }
}
