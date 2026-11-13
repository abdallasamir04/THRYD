using System.Globalization;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>One lesson of a week as read from the timetable (plus a substitute placed in its slot that week).</summary>
public sealed record WeekLesson(
    int SessionId, DayOfWeek Day, int SlotId, int ClassId, string ClassName, int ClassOrder, int DepartmentId, string DepartmentName,
    string Subject, int TeacherId, string TeacherName, string? SubstituteName);

/// <summary>
/// PDFs of the Timetables Center that have no page of their own: subject, department and school-wide
/// timetables (different views of the same timetable sessions), the duty rota and the bell schedule.
/// They use the same THRYD document (logo, title, metadata, academic year, export date) as every other PDF.
/// </summary>
public static class TimetableReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>One subject across all classes: days down the side, periods across; each cell lists the classes (and teachers).</summary>
    public static PdfReport Subject(DateOnly week, IReadOnlyList<SlotView> slots, string department, string subject, IReadOnlyList<WeekLesson> lessons, IReadOnlyList<DayOfWeek> days)
    {
        var active = slots.Where(s => s.IsActive).ToList();
        var cells = days.Select(day => (IReadOnlyList<IReadOnlyList<PdfGridEntry>>)active.Select(slot =>
        {
            var here = lessons.Where(l => l.Day == day && l.SlotId == slot.Id).OrderBy(l => l.ClassOrder).ThenBy(l => l.ClassName).ToList();
            // A teacher teaches one class per slot, so a cell holds at most one entry per subject teacher; cap it anyway.
            var list = here.Take(12).Select(l => Entry(l.ClassName, l, subject: false, teacher: true)).ToList();
            if (here.Count > 12) list.Add(new PdfGridEntry($"+{here.Count - 12} more", [], PdfTone.Muted));
            return (IReadOnlyList<PdfGridEntry>)list;
        }).ToList()).ToList();

        return new PdfReport
        {
            Title = "Subject Timetable",
            Subtitle = $"Subject: {subject}",
            Meta = Meta(week, ("Department", department), ("Classes", lessons.Select(l => l.ClassId).Distinct().Count().ToString(C)),
                ("Lessons", lessons.Count.ToString(C)), ("Teachers", lessons.Select(l => l.TeacherId).Distinct().Count().ToString(C))),
            Sections =
            [
                .. Past(week),
                new PdfGrid
                {
                    Corner = "Day / Period",
                    Columns = active.Select(SlotAxis).ToList(),
                    Rows = days.Select(d => DayAxis(week, d)).ToList(),
                    Cells = cells,
                    Legend = Legend,
                },
            ],
            FileNameParts = ["Subject Timetable", department, subject],
        };
    }

    /// <summary>A department: one page per day, its teachers down the side and the periods across (class + subject in each cell).</summary>
    public static PdfReport Department(DateOnly week, IReadOnlyList<SlotView> slots, string department, IReadOnlyList<(int Id, string Name)> teachers,
        IReadOnlyList<WeekLesson> lessons, IReadOnlyList<DayOfWeek> days)
    {
        var active = slots.Where(s => s.IsActive).ToList();
        var sections = new List<PdfSection>(Past(week));
        var first = true;
        foreach (var day in days)
        {
            var rows = teachers.Where(t => lessons.Any(l => l.TeacherId == t.Id)).ToList();
            sections.Add(new PdfGrid
            {
                Title = $"{ScheduleRules.DayName(day)} · {ScheduleRules.DateOf(week, day).ToString("d MMM yyyy", C)}",
                PageBreakBefore = !first,
                Corner = "Teacher / Period",
                RowHeaderWidth = 120,
                Columns = active.Select(SlotAxis).ToList(),
                Rows = rows.Select(t => new PdfAxis(t.Name, $"{lessons.Count(l => l.TeacherId == t.Id && l.Day == day)} lesson(s)")).ToList(),
                Cells = rows.Select(t => (IReadOnlyList<IReadOnlyList<PdfGridEntry>>)active.Select(slot => (IReadOnlyList<PdfGridEntry>)lessons
                    .Where(l => l.TeacherId == t.Id && l.Day == day && l.SlotId == slot.Id)
                    .Select(l => Entry(l.ClassName, l, subject: true, teacher: false)).ToList()).ToList()).ToList(),
                Legend = first ? Legend : [],
            });
            first = false;
        }
        if (teachers.All(t => lessons.All(l => l.TeacherId != t.Id)))
            sections.Add(new PdfNotice { Text = "No lessons of this department on the timetable in this week.", Tone = PdfTone.Muted });

        return new PdfReport
        {
            Title = "Department Timetable",
            Subtitle = $"Department: {department}",
            Meta = Meta(week, ("Subjects", string.Join(", ", lessons.Select(l => l.Subject).Distinct().Order())),
                ("Teachers", teachers.Count.ToString(C)), ("Lessons", lessons.Count.ToString(C))),
            Sections = sections,
            FileNameParts = ["Department Timetable", department],
        };
    }

    /// <summary>The whole school: one page per day, every class down the side and the periods across (subject + teacher).</summary>
    public static PdfReport School(DateOnly week, IReadOnlyList<SlotView> slots, IReadOnlyList<(int Id, string Name)> classes, IReadOnlyList<WeekLesson> lessons, IReadOnlyList<DayOfWeek> days)
    {
        var active = slots.Where(s => s.IsActive).ToList();
        var sections = new List<PdfSection>(Past(week));
        var first = true;
        foreach (var day in days)
        {
            sections.Add(new PdfGrid
            {
                Title = $"{ScheduleRules.DayName(day)} · {ScheduleRules.DateOf(week, day).ToString("d MMM yyyy", C)}",
                PageBreakBefore = !first,
                Corner = "Class / Period",
                RowHeaderWidth = 64,
                Columns = active.Select(SlotAxis).ToList(),
                Rows = classes.Select(c => new PdfAxis(c.Name)).ToList(),
                Cells = classes.Select(c => (IReadOnlyList<IReadOnlyList<PdfGridEntry>>)active.Select(slot => (IReadOnlyList<PdfGridEntry>)lessons
                    .Where(l => l.ClassId == c.Id && l.Day == day && l.SlotId == slot.Id)
                    .Select(l => Entry(l.Subject, l, subject: false, teacher: true)).ToList()).ToList()).ToList(),
                Legend = first ? Legend : [],
            });
            first = false;
        }
        return new PdfReport
        {
            Title = "School Timetable",
            Subtitle = "All classes",
            Meta = Meta(week, ("Classes", classes.Count.ToString(C)), ("Lessons", lessons.Count.ToString(C)),
                ("Teachers", lessons.Select(l => l.TeacherId).Distinct().Count().ToString(C))),
            Sections = sections,
            FileNameParts = ["School Timetable", SchoolWeek.Iso(week)],
        };
    }

    /// <summary>A week's duty rota of one duty type (morning, midday or dismissal), day by day.</summary>
    public static PdfReport DutyRota(DateOnly week, DutyType type, IReadOnlyList<DutyRow> rows, IReadOnlyList<DayOfWeek> days)
    {
        var shown = rows.Where(r => days.Contains(r.DutyDate.DayOfWeek)).OrderBy(r => r.DutyDate).ThenBy(r => r.StartTime).ThenBy(r => r.Component).ToList();
        return new PdfReport
        {
            Title = "Duty Rota",
            Subtitle = DutyComponents.TypeName(type),
            Meta = Meta(week, ("Duties", shown.Count.ToString(C)), ("Staff", shown.Select(r => r.PrimaryStaffId).Distinct().Count().ToString(C))),
            Sections =
            [
                new PdfTable
                {
                    Columns = [new("Day", 1.1f), new("Duty", 2.4f), new("Time", 0.9f), new("Location", 1.3f), new("Assigned", 1.6f), new("Backup", 1.5f), new("Classes", 1.4f)],
                    Rows = shown.Select(r => (IReadOnlyList<PdfCell>)
                    [
                        r.DutyDate.ToString("ddd d MMM", C),
                        new PdfCell(r.Description, DutyComponents.Name(r.Component)),
                        ScheduleRules.Times(r.StartTime, r.EndTime),
                        r.Location,
                        new PdfCell(r.PrimaryName, Strong: true),
                        r.BackupName,
                        r.ClassNames.Count == 0 ? null : string.Join(", ", r.ClassNames),
                    ]).ToList(),
                    EmptyText = $"No {DutyComponents.TypeName(type).ToLowerInvariant()} recorded in this week.",
                },
            ],
            FileNameParts = ["Duty Rota", DutyComponents.TypeName(type), SchoolWeek.Iso(week)],
        };
    }

    /// <summary>The school day's periods (Time Slots): the same ten periods apply Sunday to Thursday.</summary>
    public static PdfReport BellSchedule(DateOnly week, IReadOnlyList<SlotView> slots, IReadOnlyDictionary<int, int> lessonsPerSlot) => new()
    {
        Title = "Bell Schedule",
        Subtitle = "School periods (Sunday – Thursday)",
        Meta = Meta(week, ("Active periods", slots.Count(s => s.IsActive).ToString(C))),
        Sections =
        [
            new PdfTable
            {
                Columns = [new("Period", 0.7f, PdfAlign.Center), new("Name", 1.6f), new("Start", 1, PdfAlign.Center), new("End", 1, PdfAlign.Center), new("Status", 1, PdfAlign.Center), new("Lessons this week", 1.2f, PdfAlign.End)],
                Rows = slots.OrderBy(s => s.Number).Select(s => (IReadOnlyList<PdfCell>)
                [
                    PdfCell.Num(s.Number),
                    new PdfCell(s.Label, Strong: true),
                    s.Start?.ToString("HH:mm", C),
                    s.End?.ToString("HH:mm", C),
                    PdfCell.Status(s.IsActive ? "Active" : "Inactive", s.IsActive ? PdfTone.Success : PdfTone.Muted),
                    PdfCell.Num(lessonsPerSlot.GetValueOrDefault(s.Id)),
                ]).ToList(),
            },
        ],
        FileNameParts = ["Bell Schedule"],
    };

    // ------------------------------------------------------------------ helpers

    private static readonly IReadOnlyList<(string, PdfTone)> Legend = [("Teaching session (every week)", PdfTone.Primary), ("Substitute this week", PdfTone.Warning)];

    /// <summary>A grid entry: a bold title (class or subject) and the detail lines the view needs; substitutes in amber.</summary>
    private static PdfGridEntry Entry(string title, WeekLesson l, bool subject, bool teacher)
    {
        var lines = new List<string>();
        if (subject) lines.Add(l.Subject);
        if (teacher) lines.Add(l.TeacherName);
        if (l.SubstituteName is not null) lines.Add($"Substitute: {l.SubstituteName}");
        return new PdfGridEntry(title, lines, l.SubstituteName is null ? PdfTone.Primary : PdfTone.Warning);
    }

    private static IReadOnlyList<PdfMeta> Meta(DateOnly week, params (string Label, string? Value)[] items) =>
        [new("Week", SchoolWeek.Label(week)), .. items.Select(i => new PdfMeta(i.Label, i.Value))];

    private static PdfSection[] Past(DateOnly week) =>
        week >= SchoolWeek.Current ? [] : [new PdfNotice { Text = "Past week — shown as history. Changes to the timetable take effect from the current week.", Tone = PdfTone.Muted }];

    private static PdfAxis SlotAxis(SlotView s) => new(s.Label, s.HasTimes ? s.Times : "Time not set");

    private static PdfAxis DayAxis(DateOnly week, DayOfWeek day)
    {
        var date = ScheduleRules.DateOf(week, day);
        return new(ScheduleRules.DayName(day), date.ToString("d MMM", C), date == SchoolWeek.Today);
    }
}
