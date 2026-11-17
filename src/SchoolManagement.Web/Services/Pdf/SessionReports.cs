using System.Globalization;
using SchoolManagement.Web.Common;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>Session Attendance reports as PDFs: one student, one class, one lesson, or the whole school, over a date or a period.</summary>
public static class SessionReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public const string Rules =
        "School Absent = absent from school that day (no lesson attendance is taken; never a lesson absence). Absent at school = marked absent " +
        "from the lesson while present at school. Not Recorded = no lesson record. Attendance % = (Present + Late) ÷ (Present + Late + lesson Absent). " +
        "Only lessons on the timetable that took place count (not cancelled, already started).";

    private static string Period(DateOnly from, DateOnly to) =>
        from == to ? from.ToString("dddd d MMMM yyyy", C) : $"{from.ToString("d MMM yyyy", C)} – {to.ToString("d MMM yyyy", C)}";

    public static PdfCell Kind(SessionKind k, SchoolPresence school) => k switch
    {
        SessionKind.Present => PdfCell.Status("Present", PdfTone.Success),
        SessionKind.Late => PdfCell.Status("Late", PdfTone.Warning),
        SessionKind.Absent => PdfCell.Status(school is SchoolPresence.Present or SchoolPresence.Late ? "Absent (at school)" : "Absent", PdfTone.Error),
        SessionKind.Excused => PdfCell.Status("Excused", PdfTone.Info),
        SessionKind.SchoolAbsent => PdfCell.Status("School Absent", PdfTone.Muted),
        _ => PdfCell.Status("Not Recorded", PdfTone.Muted),
    };

    private static PdfSummary Summary(SessionReport r) => new()
    {
        Items =
        [
            new("Lessons", r.Lessons.ToString(C)), new("Present", r.Tally.Present.ToString(C), PdfTone.Success), new("Late", r.Tally.Late.ToString(C), PdfTone.Warning),
            new("Absent", r.Tally.Absent.ToString(C), PdfTone.Error), new("Absent at school", r.Tally.AbsentAtSchool.ToString(C), PdfTone.Error),
            new("School Absent", r.Tally.SchoolAbsent.ToString(C), PdfTone.Muted), new("Not Recorded", r.Tally.NotRecorded.ToString(C), PdfTone.Muted),
            new("Attendance", r.Tally.PercentText, PdfTone.Primary),
        ],
    };

    private static readonly IReadOnlyList<PdfColumn> TallyColumns =
    [
        new("Name", 2.2f), new("Lessons", 0.9f, PdfAlign.End), new("Present", 0.9f, PdfAlign.End), new("Late", 0.8f, PdfAlign.End),
        new("Absent", 0.9f, PdfAlign.End), new("At school", 0.9f, PdfAlign.End), new("School Absent", 1.1f, PdfAlign.End),
        new("Not Recorded", 1.1f, PdfAlign.End), new("%", 0.8f, PdfAlign.End),
    ];

    private static IReadOnlyList<PdfCell> TallyRow(string label, int lessons, SessionTally t) =>
    [
        new PdfCell(label, Strong: true), lessons.ToString(C), t.Present.ToString(C), t.Late.ToString(C), t.Absent.ToString(C), t.AbsentAtSchool.ToString(C),
        t.SchoolAbsent.ToString(C), t.NotRecorded.ToString(C), new PdfCell(t.PercentText, Strong: true),
    ];

    public static PdfReport Build(SessionReport r)
    {
        var sections = new List<PdfSection> { Summary(r) };
        var q = r.Query;
        if (q.Mode == "school")
        {
            sections.Add(new PdfTable { Title = "By class", Columns = TallyColumns, Rows = r.ByClass.Select(g => TallyRow(g.Label, g.Lessons, g.Tally)).ToList(), EmptyText = "No lessons." });
        }
        if (q.Mode is "school" or "class" or "student")
        {
            sections.Add(new PdfTable { Title = "By subject", Columns = TallyColumns, Rows = r.BySubject.Select(g => TallyRow(g.Label, g.Lessons, g.Tally)).ToList(), EmptyText = "No lessons." });
        }
        if (r.Repeated.Count > 0)
        {
            sections.Add(new PdfTable
            {
                Title = "Repeated lesson absences while at school",
                Columns = [new("Student", 2.4f), new("Class", 1), new("Lesson absences at school", 1.6f, PdfAlign.End), new("Lesson absences (all)", 1.4f, PdfAlign.End)],
                Rows = r.Repeated.Select(s => (IReadOnlyList<PdfCell>)[new PdfCell(s.Name, Strong: true), s.ClassName, s.Tally.AbsentAtSchool.ToString(C), s.Tally.Absent.ToString(C)]).ToList(),
            });
        }
        if (q.Mode is "school" or "class")
        {
            sections.Add(new PdfTable
            {
                Title = "Students", RowNumbers = true,
                Columns = [new("Student", 2.4f), new("Class", 1), .. TallyColumns.Skip(1)],
                Rows = r.Students.Select(s => (IReadOnlyList<PdfCell>)[new PdfCell(s.Name, Strong: true), s.ClassName, s.Tally.Total.ToString(C), s.Tally.Present.ToString(C),
                    s.Tally.Late.ToString(C), s.Tally.Absent.ToString(C), s.Tally.AbsentAtSchool.ToString(C), s.Tally.SchoolAbsent.ToString(C), s.Tally.NotRecorded.ToString(C),
                    new PdfCell(s.Tally.PercentText, Strong: true)]).ToList(),
                EmptyText = "No students.",
            });
        }
        sections.Add(new PdfTable
        {
            Title = q.Mode switch { "student" => "Lessons", "lesson" => "Register", _ => "Lesson absences" },
            RowNumbers = true,
            Columns = q.Mode == "lesson"
                ? [new("Student", 2.6f), new("Status", 1.4f), new("Notes", 2.4f)]
                : [new("Date", 1.2f), new("Lesson", 2.4f), new("Teacher", 1.6f), .. q.Mode == "student" ? Array.Empty<PdfColumn>() : [new PdfColumn("Student", 2)], new("Status", 1.3f)],
            Rows = r.Lines.Select(x => (IReadOnlyList<PdfCell>)(q.Mode == "lesson"
                ? [new PdfCell(x.StudentName, Strong: true), Kind(x.Kind, x.School), x.Notes]
                : [x.Lesson.Date.ToString("ddd d MMM", C), new PdfCell($"{x.Lesson.Subject} · {x.Lesson.ClassName}", $"{x.Lesson.Period} · {x.Lesson.Times12}"), x.Lesson.Teacher,
                    .. q.Mode == "student" ? Array.Empty<PdfCell>() : [new PdfCell(x.StudentName, Strong: true)], Kind(x.Kind, x.School)])).ToList(),
            EmptyText = q.Mode is "student" or "lesson" ? "No lessons took place in this period." : "No lesson absences in this period.",
        });
        sections.Add(new PdfNotice { Text = Rules, Tone = PdfTone.Info });
        return new PdfReport
        {
            Title = r.Title,
            Subtitle = r.Subtitle,
            Meta = [new("Period", Period(q.From, q.To)), new("Lessons", r.Lessons.ToString(C))],
            Sections = sections,
            FileNameParts = ["Session Attendance", r.Subtitle, SchoolWeek.Iso(q.From), q.From == q.To ? null : SchoolWeek.Iso(q.To)],
        };
    }
}
