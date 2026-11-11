using System.Globalization;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>
/// One builder per downloadable table: each turns the data a page shows (with its filters already
/// applied by the same service call the page uses) into a <see cref="PdfReport"/>. Titles, subtitles and
/// file names come from the records themselves, never from fixed text.
/// </summary>
public static class PdfReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    // ================================================================== timetables

    /// <summary>Class timetable: days down the side, periods across the top (as on the website).</summary>
    public static PdfReport ClassTimetable(TimetableGrid g, string className, string? details)
    {
        var slots = Active(g);
        var cells = ScheduleRules.Days.Select(day => Row(slots.Select(slot =>
        {
            var list = new List<PdfGridEntry>();
            if (g.Session(day, slot.Id) is { } s) list.Add(new(s.Subject, Lines(s.Subject, s.TeacherName, s.DepartmentName)));
            foreach (var sub in g.PeriodsAt(day, slot.Id).Where(p => p.Category == WorkloadCategory.Substitution))
                list.Add(new("Substitute", [sub.TeacherName, "This week only"], PdfTone.Warning));
            return (IReadOnlyList<PdfGridEntry>)list;
        }))).ToList();

        return new PdfReport
        {
            Title = "Class Timetable",
            Subtitle = $"Class: {className}",
            Meta = [new("Week", SchoolWeek.Label(g.Week)), new("Details", details)],
            Sections =
            [
                .. PastWeek(g.Editable),
                new PdfGrid
                {
                    Corner = "Day / Period",
                    Columns = slots.Select(SlotAxis).ToList(),
                    Rows = ScheduleRules.Days.Select(d => DayAxis(g.Week, d)).ToList(),
                    Cells = cells,
                    Legend = [("Teaching session (every week)", PdfTone.Primary), ("Substitute this week", PdfTone.Warning)],
                },
            ],
            FileNameParts = ["Class Timetable", className],
        };
    }

    /// <summary>Teacher timetable: periods down the side, days across the top (as on the website), plus duties and workload.</summary>
    public static PdfReport TeacherTimetable(TimetableGrid g, string teacherName, string? department, WorkloadSummary? workload)
    {
        var slots = Active(g);
        var cells = slots.Select(slot => Row(ScheduleRules.Days.Select(day =>
        {
            var list = new List<PdfGridEntry>();
            if (g.Session(day, slot.Id) is { } s) list.Add(new(s.ClassName, Lines(s.ClassName, s.Subject, s.DepartmentName)));
            else
                foreach (var p in g.PeriodsAt(day, slot.Id))
                    list.Add(p.Category == WorkloadCategory.Substitution
                        ? new PdfGridEntry("Substitution", [p.ClassName ?? "", "This week only"], PdfTone.Warning)
                        : new PdfGridEntry("Office hours", [p.Description ?? "", "This week only"], PdfTone.Info));
            return (IReadOnlyList<PdfGridEntry>)list;
        }))).ToList();

        // The grid comes first so the week's ten periods stay together on the first page.
        var sections = new List<PdfSection>(PastWeek(g.Editable));
        sections.Add(new PdfGrid
        {
            Corner = "Period / Day",
            Columns = ScheduleRules.Days.Select(d => DayAxis(g.Week, d)).ToList(),
            Rows = slots.Select(SlotAxis).ToList(),
            Cells = cells,
            RowHeaderWidth = 82,
            Legend = [("Teaching session (every week)", PdfTone.Primary), ("Office hours (this week)", PdfTone.Info), ("Substitution (this week)", PdfTone.Warning)],
        });
        if (workload is { } w)
            sections.Add(new PdfSummary
            {
                Title = "Workload this week",
                Items =
                [
                    .. WorkloadRules.Categories.Select(c => new PdfStat(WorkloadRules.Label(c), w.Breakdown[c].ToString(C))),
                    new PdfStat("Total this week", $"{w.Total} / {w.Max}", PdfTone.Primary),
                    new PdfStat("Status", WorkloadRules.Label(w.Status), StatusTone(w.Status)),
                ],
            });
        if (g.Duties.Count > 0)
            sections.Add(new PdfTable
            {
                Title = "Duties this week",
                Columns = [new("Day", 1.2f), new("Duty", 2.6f), new("Time", 1), new("Location", 1.4f), new("Role", 1), new("Counted in workload", 1.1f, PdfAlign.Center)],
                Rows = g.Duties.OrderBy(d => d.Date).ThenBy(d => d.Start).Select(d => R(
                    d.Date.ToString("ddd d MMM", C),
                    new PdfCell(d.Description, DutyComponents.Name(d.Component)),
                    ScheduleRules.Times(d.Start, d.End),
                    d.Location,
                    d.Role,
                    PdfCell.Status(d.Counted ? "Yes" : "No", d.Counted ? PdfTone.Success : PdfTone.Muted))).ToList(),
            });

        return new PdfReport
        {
            Title = "Teacher Timetable",
            Subtitle = $"Teacher Name: {teacherName}",
            Meta = [new("Week", SchoolWeek.Label(g.Week)), new("Department", department)],
            Sections = sections,
            FileNameParts = ["Teacher Timetable", teacherName],
        };
    }

    /// <summary>P.E. timetable (the same module on /pe and on the PE department page).</summary>
    public static PdfReport PeTimetable(DateOnly week, IReadOnlyList<SlotView> slots, IReadOnlyList<PeChip> sessions, string? department, int teachers)
    {
        static PdfTone Tone(PeFacility? f) => f switch
        {
            PeFacility.KgGarden => PdfTone.Success,
            PeFacility.SwimmingPool => PdfTone.Info,
            PeFacility.MainGarden => PdfTone.Warning,
            _ => PdfTone.Muted,
        };
        var active = slots.Where(s => s.IsActive).ToList();
        var cells = ScheduleRules.Days.Select(day => Row(active.Select(slot => (IReadOnlyList<PdfGridEntry>)sessions
            .Where(s => s.Day == day && s.SlotId == slot.Id)
            .Select(s => new PdfGridEntry(s.ClassName,
                [s.CoTeacherName is null ? s.TeacherName : $"{s.TeacherName} + {s.CoTeacherName}", s.Facility is { } f ? PeService.FacilityName(f) : "Facility not set"],
                Tone(s.Facility)))
            .ToList()))).ToList();

        return new PdfReport
        {
            Title = "P.E. Timetable",
            Subtitle = "Physical Education",
            Meta =
            [
                new("Week", SchoolWeek.Label(week)), new("Department", department),
                new("Sessions", sessions.Count.ToString(C)), new("P.E. teachers", teachers.ToString(C)),
            ],
            Sections =
            [
                .. PastWeek(week >= SchoolWeek.Current),
                new PdfGrid
                {
                    Corner = "Day / Period",
                    Columns = active.Select(SlotAxis).ToList(),
                    Rows = ScheduleRules.Days.Select(d => DayAxis(week, d)).ToList(),
                    Cells = cells,
                    Legend = [("KG Garden", PdfTone.Success), ("Swimming Pool", PdfTone.Info), ("Main Garden", PdfTone.Warning), ("Facility not set", PdfTone.Muted)],
                },
            ],
            FileNameParts = ["PE Timetable", SchoolWeek.Iso(week)],
        };
    }

    // ================================================================== daily operations

    /// <summary>Student attendance for a day: the checklist rows with the same class and tab filter as the page.</summary>
    public static PdfReport Attendance(ArrivalChecklist c, string? className, string show)
    {
        var rows = c.Rows.Where(r => show switch
        {
            "missing" => !r.IsRecorded,
            "late" => r.Late == LateStatus.Late,
            "absent" => r.Status == ArrivalStatus.Absent,
            "present" => r.Status == ArrivalStatus.Present,
            _ => true,
        }).ToList();
        var s = c.Summary;
        return new PdfReport
        {
            Title = "Student Attendance",
            Subtitle = $"Date: {c.Date.ToString("dddd, d MMMM yyyy", C)}",
            Meta =
            [
                new("Class", className ?? "All classes"),
                new("Showing", show switch { "missing" => "Not recorded", "late" => "Late", "absent" => "Absent", "present" => "Present", _ => "All students" }),
                new("Arrival deadline", c.Deadline?.ToString("HH:mm", C) ?? "Not set"),
            ],
            Sections =
            [
                new PdfSummary
                {
                    Items =
                    [
                        new("Students", s.Students.ToString(C)), new("Recorded", s.Recorded.ToString(C)),
                        new("Present", s.Present.ToString(C), PdfTone.Success), new("Absent", s.Absent.ToString(C), PdfTone.Error),
                        new("Late", s.Late.ToString(C), PdfTone.Warning), new("On time", s.OnTimePercent is { } p ? p.ToString("0.#", C) + "%" : "—"),
                        new("Not recorded", s.NotRecorded.ToString(C), PdfTone.Muted),
                    ],
                },
                new PdfTable
                {
                    RowNumbers = true,
                    Columns =
                    [
                        new("Student", 2.2f), new("Class", 1), new("Attendance", 1.3f), new("Arrival time", 0.9f), new("Punctuality", 1),
                        new("Method", 1.4f), new("Received by", 1.5f), new("Notes", 1.8f), new("Recorded by", 1.3f),
                    ],
                    Rows = rows.Select(r => R(
                        new PdfCell(r.StudentName, Strong: true),
                        r.ClassName,
                        !r.IsRecorded ? PdfCell.Status("Not recorded", PdfTone.Muted)
                            : r.Status == ArrivalStatus.Present ? PdfCell.Status("Present", PdfTone.Success) : PdfCell.Status("Absent", PdfTone.Error),
                        r.Time is { } t ? t.ToString(t.Second == 0 ? "HH:mm" : "HH:mm:ss", C) : "—",
                        r.Late switch { LateStatus.Late => PdfCell.Status("Late", PdfTone.Error), LateStatus.OnTime => PdfCell.Status("On time", PdfTone.Success), _ => "—" },
                        r.Status == ArrivalStatus.Present ? new PdfCell(Arrivals.Method(r.Method), r.Location) : "—",
                        r.ReceivedByName is null ? "—" : new PdfCell(r.ReceivedByName, r.Confirmed ? "Receiving confirmed" : null),
                        r.Notes,
                        r.IsRecorded ? new PdfCell(r.RecordedBy ?? "—", r.Corrections > 0 ? $"Corrected {r.Corrections}×" : null) : "—")).ToList(),
                    EmptyText = "No students match this filter on this date.",
                },
            ],
            FileNameParts = ["Student Attendance", className, SchoolWeek.Iso(c.Date)],
        };
    }

    /// <summary>Duty audit: every duty session matching the page's filters (all pages, not just the visible one).</summary>
    public static PdfReport DutyAudit(IReadOnlyList<DutyRow> rows, DutyAuditQuery q, string? staffName)
    {
        static string Att(AttendanceStatus a) => a == AttendanceStatus.NotRecorded ? "Attendance not recorded" : a.ToString();
        var from = Date(q.From);
        var to = Date(q.To);
        var range = (from, to) switch
        {
            ({ } f, { } t) when f == t => f.ToString("dddd, d MMMM yyyy", C),
            ({ } f, { } t) => $"{f.ToString("d MMM yyyy", C)} – {t.ToString("d MMM yyyy", C)}",
            ({ } f, null) => $"From {f.ToString("d MMM yyyy", C)}",
            (null, { } t) => $"Up to {t.ToString("d MMM yyyy", C)}",
            _ => "All dates",
        };
        return new PdfReport
        {
            Title = "Duty Audit Report",
            Subtitle = range,
            Meta =
            [
                new("Duty type", Enum.TryParse<DutyType>(q.Type, true, out var type) ? DutyComponents.TypeName(type) : null),
                new("Component", DutyComponents.FromSlug(q.Component)?.Name),
                new("Location", string.IsNullOrWhiteSpace(q.Location) ? null : q.Location.Trim()),
                new("Staff", staffName),
                new("Status", DutyStatuses.FromSlug(q.Status) is { } st ? DutyStatuses.Label(st) : null),
                new("Follow-up", q.FollowUp ? "Required only" : null),
                new("Duty sessions", rows.Count.ToString(C)),
            ],
            Sections =
            [
                new PdfSummary
                {
                    Items =
                    [
                        .. DutyStatuses.All.Select(s => new PdfStat(DutyStatuses.Label(s), rows.Count(r => r.Status == s).ToString(C), DutyTone(s))),
                        new PdfStat("Follow-up required", rows.Count(r => r.FollowUpRequired).ToString(C), PdfTone.Warning),
                    ],
                },
                new PdfTable
                {
                    Columns =
                    [
                        new("Date & time", 1.2f), new("Duty", 2.4f), new("Location", 1.1f), new("Assigned staff", 1.6f),
                        new("Backup", 1.5f), new("Status", 1.1f), new("Audit", 2.3f),
                    ],
                    Rows = rows.Select(d => R(
                        new PdfCell(d.DutyDate.ToString("ddd d MMM yyyy", C), ScheduleRules.Times(d.StartTime, d.EndTime), Strong: true),
                        new PdfCell(d.Description, $"{DutyComponents.TypeName(d.DutyType)} · {DutyComponents.Name(d.Component)}"),
                        d.Location,
                        new PdfCell(d.PrimaryName, Att(d.PrimaryAttendance)),
                        d.BackupName is null ? "—" : new PdfCell(d.BackupName,
                            $"Request {(d.BackupStatus == BackupRequestStatus.Rejected ? "declined" : d.BackupStatus.ToString().ToLowerInvariant())}{(d.BackupStatus == BackupRequestStatus.Accepted ? " · " + Att(d.BackupAttendance) : "")}"),
                        PdfCell.Status(DutyStatuses.Label(d.Status), DutyTone(d.Status)),
                        new PdfCell(AuditText(d), $"By {d.UpdatedBy ?? "—"} · {Ui.DateTime(d.UpdatedAt)}"))).ToList(),
                    EmptyText = "No duty sessions match these filters.",
                },
            ],
            FileNameParts = ["Duty Audit Report", from is null && to is null ? null : SchoolWeek.Iso(from ?? to!.Value), to is not null && from is not null && to != from ? SchoolWeek.Iso(to.Value) : null],
        };
    }

    /// <summary>Staff duty records: each staff member's duty totals and average evaluation.</summary>
    public static PdfReport StaffDutyRecords(IReadOnlyList<StaffDutyOverviewRow> rows, string? search) => new()
    {
        Title = "Staff Duty Records",
        Subtitle = "Duties and evaluations per staff member",
        Meta = [new("Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()), new("Staff members", rows.Count.ToString(C))],
        Sections =
        [
            new PdfTable
            {
                RowNumbers = true,
                Columns =
                [
                    new("Staff member", 2.6f), new("Assigned", 1, PdfAlign.End), new("Covered", 1, PdfAlign.End), new("Not covered", 1, PdfAlign.End),
                    new("Pending / in progress", 1.3f, PdfAlign.End), new("Average rating", 1.3f, PdfAlign.End),
                ],
                Rows = rows.Select(r => R(
                    new PdfCell(r.Staff.FullName, r.Staff.JobTitle ?? r.Staff.StaffType, Strong: true),
                    PdfCell.Num(r.Summary.TotalAssigned),
                    new PdfCell(r.Summary.Covered.ToString(C), Tone: PdfTone.Success),
                    new PdfCell(r.Summary.NotCovered.ToString(C), Tone: r.Summary.NotCovered > 0 ? PdfTone.Error : PdfTone.Default),
                    PdfCell.Num(r.Summary.Open),
                    r.Summary.AverageRating is { } a ? new PdfCell($"{a.ToString("0.0", C)} / 5", $"{r.Summary.EvaluatedAssignments} rated") : "Not rated")).ToList(),
                EmptyText = "No staff match this search.",
            },
        ],
        FileNameParts = ["Staff Duty Records", search],
    };

    // ================================================================== workload

    /// <summary>Teacher workload overview for a week (the same filters and order as the page).</summary>
    public static PdfReport Workload(DateOnly week, IReadOnlyList<WorkloadSummary> rows, IReadOnlyList<WorkloadSummary> all, int max, IReadOnlyList<PdfMeta> filters) => new()
    {
        Title = "Teacher Workload Report",
        Subtitle = $"Week of {SchoolWeek.Label(week)}",
        Meta = [.. filters, new("Maximum", $"{max} periods per teacher per week"), new("Teachers shown", $"{rows.Count} of {all.Count}")],
        Sections =
        [
            new PdfSummary
            {
                Items =
                [
                    new("Teachers", all.Count.ToString(C)),
                    new("Available", all.Count(r => r.Status == WorkloadStatus.Available).ToString(C), PdfTone.Success),
                    new("Near capacity", all.Count(r => r.Status == WorkloadStatus.NearCapacity).ToString(C), PdfTone.Warning),
                    new("Full", all.Count(r => r.Status is WorkloadStatus.Full or WorkloadStatus.OverLimit).ToString(C), PdfTone.Error),
                    new("Periods assigned", $"{all.Sum(r => r.Total)} of {all.Count * max}", PdfTone.Primary),
                ],
            },
            new PdfTable
            {
                RowNumbers = true,
                Columns =
                [
                    new("Teacher", 2.4f), new("Department", 1.5f),
                    .. WorkloadRules.Categories.Select(c => new PdfColumn(WorkloadRules.Label(c), 1, PdfAlign.End)),
                    new("Total", 0.9f, PdfAlign.End), new("Remaining", 0.9f, PdfAlign.End), new("Status", 1.1f),
                ],
                Rows = rows.Select(r => R(
                [
                    new PdfCell(r.TeacherName, r.JobTitle ?? "Job title not set", Strong: true),
                    r.DepartmentName ?? "Not set",
                    .. WorkloadRules.Categories.Select(c => new PdfCell(r.Breakdown[c].ToString(C), Tone: r.Breakdown[c] == 0 ? PdfTone.Muted : PdfTone.Default)),
                    new PdfCell($"{r.Total} / {r.Max}", Strong: true),
                    PdfCell.Num(r.Remaining),
                    PdfCell.Status(WorkloadRules.Label(r.Status), StatusTone(r.Status)),
                ])).ToList(),
                Footer =
                [
                    "Total", "",
                    .. WorkloadRules.Categories.Select(c => (PdfCell)rows.Sum(r => r.Breakdown[c]).ToString(C)),
                    rows.Sum(r => r.Total).ToString(C), rows.Sum(r => r.Remaining).ToString(C), "",
                ],
                EmptyText = "No teachers match these filters.",
            },
        ],
        FileNameParts = ["Teacher Workload Report", SchoolWeek.Iso(week)],
    };

    /// <summary>One teacher's workload for a week: assignments (with the total) and the change history.</summary>
    public static PdfReport TeacherWorkload(TeacherRow t, WorkloadSummary s, IReadOnlyList<WorkloadAssignmentRow> assignments, IReadOnlyList<WorkloadHistoryRow> history)
    {
        var rows = new List<IReadOnlyList<PdfCell>>();
        if (s.FromTimetable > 0)
            rows.Add(R("Teaching Sessions", PdfCell.Num(s.FromTimetable), "Weekly sessions on the class timetable", "Automatic"));
        rows.AddRange(assignments.Select(a => R(
            WorkloadRules.Label(a.Category),
            PdfCell.Num(a.Periods),
            new PdfCell(string.Join(" · ", new[] { a.Day is { } d && a.SlotNumber is { } n ? $"{ScheduleRules.DayShort(d)} · P{n}" : null, a.ClassName, a.DutyLabel }
                .Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } rel ? rel : "—", a.Description),
            Ui.DateTime(a.UpdatedAt))));
        return new PdfReport
        {
            Title = "Teacher Workload",
            Subtitle = $"Teacher Name: {t.FullName}",
            Meta = [new("Week", SchoolWeek.Label(s.WeekStart)), new("Job title", t.JobTitle), new("Department", t.DepartmentName), new("Contact", t.ContactNumber)],
            Sections =
            [
                new PdfSummary
                {
                    Items =
                    [
                        .. WorkloadRules.Categories.Select(c => new PdfStat(WorkloadRules.Label(c), s.Breakdown[c].ToString(C))),
                        new PdfStat("Total this week", $"{s.Total} / {s.Max}", PdfTone.Primary),
                        new PdfStat("Remaining", s.Remaining.ToString(C), StatusTone(s.Status)),
                    ],
                },
                new PdfTable
                {
                    Title = "Assignments this week",
                    Columns = [new("Category", 1.4f), new("Periods", 0.7f, PdfAlign.End), new("Related to", 3), new("Last change", 1.3f)],
                    Rows = rows,
                    Footer = rows.Count == 0 ? null : ["Total", $"{s.Total} / {s.Max}", $"{s.Remaining} remaining", ""],
                    EmptyText = "No periods assigned this week.",
                },
                new PdfTable
                {
                    Title = "Assignment history",
                    Subtitle = "Every add, change and removal for this teacher, newest first (all weeks).",
                    Columns = [new("When", 1.3f), new("Week", 1.2f), new("Change", 0.9f), new("Details", 3.4f), new("Week total after", 1, PdfAlign.End), new("By", 1.2f)],
                    Rows = history.Select(h => R(
                        Ui.DateTime(h.ChangedAt),
                        SchoolWeek.Label(h.WeekStart),
                        PdfCell.Status(h.Change.ToString(), h.Change switch { WorkloadChange.Created => PdfTone.Success, WorkloadChange.Updated => PdfTone.Primary, _ => PdfTone.Error }),
                        h.Summary,
                        $"{h.WeekTotalAfter}/{s.Max}",
                        h.ChangedBy ?? "No sign-in yet")).ToList(),
                    EmptyText = "No changes recorded yet.",
                },
            ],
            FileNameParts = ["Teacher Workload", t.FullName, SchoolWeek.Iso(s.WeekStart)],
        };
    }

    // ================================================================== staff structure

    /// <summary>A department: its staff (with the page's search, subcategory and sort) and its HOD history.</summary>
    public static PdfReport Department(DepartmentCard d, IReadOnlyList<DepartmentMember> members, IReadOnlyList<HeadHistoryRow> history, DepartmentMemberQuery q)
    {
        var subLabel = d.Rules?.Label;
        return new PdfReport
        {
            Title = "Department Report",
            Subtitle = $"Department: {d.Name}",
            Meta =
            [
                new("Head of Department", d.Head?.FullName ?? "Not assigned"),
                new("Members", d.MemberCount.ToString(C)),
                new("Search", string.IsNullOrWhiteSpace(q.Search) ? null : q.Search.Trim()),
                new(subLabel ?? "Subcategory", string.IsNullOrWhiteSpace(q.Subcategory) ? null : q.Subcategory.Replace("|", " · ")),
            ],
            Sections =
            [
                .. (string.IsNullOrWhiteSpace(d.Description) ? Array.Empty<PdfSection>() : [new PdfNotice { Text = d.Description, Tone = PdfTone.Primary }]),
                new PdfTable
                {
                    Title = "Department staff",
                    Subtitle = q.HasFilters ? $"{members.Count} matching member(s) · the HOD is listed first." : $"{members.Count} member(s) · the HOD is listed first.",
                    RowNumbers = true,
                    Columns =
                    [
                        new("Name", 2.4f), new("Role", 1.3f), new("Job title", 1.8f),
                        .. (subLabel is null ? Array.Empty<PdfColumn>() : [new PdfColumn(subLabel, 1.6f)]),
                        new("Contact number", 1.3f), new("Added", 1),
                    ],
                    Rows = members.Select(m => R(
                    [
                        new PdfCell(m.FullName, Strong: true),
                        m.IsHead ? PdfCell.Status("Head of Department", PdfTone.Warning) : "Member",
                        m.JobTitle ?? "Not set",
                        .. (subLabel is null ? Array.Empty<PdfCell>() : [(PdfCell)m.SubcategoryLabel]),
                        m.ContactNumber,
                        Ui.Date(m.CreatedAt),
                    ])).ToList(),
                    EmptyText = q.HasFilters ? "No members match these filters." : $"No staff in {d.Name} yet.",
                },
                new PdfTable
                {
                    Title = "HOD history",
                    Columns = [new("Head of Department", 2), new("From", 1), new("To", 1), new("Status", 0.9f), new("Notes", 2.6f), new("Assigned", 1.6f)],
                    Rows = history.Select(h => R(
                        new PdfCell(h.FullName, Strong: true),
                        h.StartDate.ToString("d MMM yyyy", C),
                        h.IsCurrent ? "Now" : h.EndDate?.ToString("d MMM yyyy", C),
                        h.IsCurrent ? PdfCell.Status("Current", PdfTone.Success) : PdfCell.Status("Ended", PdfTone.Muted),
                        string.Join(" · ", new[] { h.EndReason, h.Note }.Where(x => !string.IsNullOrWhiteSpace(x))),
                        new PdfCell(Ui.DateTime(h.AssignedAt), h.AssignedBy is null ? null : "By " + h.AssignedBy))).ToList(),
                    EmptyText = "No HOD has been assigned yet.",
                },
            ],
            FileNameParts = ["Department Report", d.Name],
        };
    }

    /// <summary>HOD Management: every department's current Head of Department.</summary>
    public static PdfReport Heads(IReadOnlyList<DepartmentCard> list, string? filter) => new()
    {
        Title = "HOD Management",
        Subtitle = "Heads of Department",
        Meta =
        [
            new("Showing", filter switch { "assigned" => "Departments with an HOD", "missing" => "Departments without an HOD", _ => "All departments" }),
            new("Departments", list.Count.ToString(C)),
            new("Without an HOD", list.Count(d => d.Head is null).ToString(C)),
        ],
        Sections =
        [
            new PdfTable
            {
                RowNumbers = true,
                Columns = [new("Department", 1.8f), new("Head of Department", 2.4f), new("Job title", 1.8f), new("Since", 1.1f), new("Members", 0.8f, PdfAlign.End), new("Status", 1.1f)],
                Rows = list.Select(d => R(
                    new PdfCell(d.Name, Strong: true),
                    d.Head?.FullName,
                    d.Head?.JobTitle,
                    d.Head?.StartDate.ToString("d MMM yyyy", C),
                    PdfCell.Num(d.MemberCount),
                    d.Head is null ? PdfCell.Status("No HOD", PdfTone.Warning) : PdfCell.Status("Assigned", PdfTone.Success))).ToList(),
                EmptyText = "No department matches this filter.",
            },
        ],
        FileNameParts = ["HOD Management", filter],
    };

    /// <summary>Academic staff members (all pages of the filtered list, in the page's sort order).</summary>
    public static PdfReport StaffMembers(IReadOnlyList<TeacherRow> rows, TeacherListQuery q) => new()
    {
        Title = "Academic Staff Members",
        Subtitle = string.IsNullOrWhiteSpace(q.Department) ? "All departments" : $"Department: {q.Department}",
        Meta =
        [
            new("Search", string.IsNullOrWhiteSpace(q.Search) ? null : q.Search.Trim()),
            new("Subcategory", string.IsNullOrWhiteSpace(q.Subcategory) ? null : q.Subcategory),
            new("Job title", q.JobTitle == TeacherListQuery.NoJobTitle ? "Not set" : q.JobTitle),
            new("Staff members", rows.Count.ToString(C)),
        ],
        Sections =
        [
            new PdfTable
            {
                RowNumbers = true,
                Columns = [new("Staff name", 2.4f), new("Department", 2), new("Job title / Position", 2), new("Contact number", 1.4f), new("Added", 1)],
                Rows = rows.Select(t => R(
                    new PdfCell(t.FullName, Strong: true),
                    t.DepartmentName is null ? "—" : new PdfCell(t.DepartmentName + (t.SubcategoryLabel.Length == 0 ? "" : " · " + t.SubcategoryLabel), t.IsHead ? "Head of Department" : null),
                    t.JobTitle ?? "Not set",
                    t.ContactNumber,
                    Ui.Date(t.CreatedAt))).ToList(),
                EmptyText = "No staff members match these filters.",
            },
        ],
        FileNameParts = ["Staff Members", q.Department],
    };

    public static PdfReport Students(IReadOnlyList<StudentRow> rows, string? search, string? className) => new()
    {
        Title = "Student List",
        Subtitle = className is null ? "All classes" : $"Class: {className}",
        Meta = [new("Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()), new("Students", rows.Count.ToString(C))],
        Sections =
        [
            new PdfTable
            {
                RowNumbers = true,
                Columns = [new("Student name", 3), new("Class", 1), new("Class status", 1), new("Added on", 1.2f), new("Last updated", 1.2f)],
                Rows = rows.Select(s => R(
                    new PdfCell(s.FullName, Strong: true),
                    s.ClassName,
                    s.ClassActive ? PdfCell.Status("Active", PdfTone.Success) : PdfCell.Status("Inactive", PdfTone.Muted),
                    Ui.Date(s.CreatedAt),
                    Ui.Date(s.UpdatedAt))).ToList(),
                EmptyText = "No students match these filters.",
            },
        ],
        FileNameParts = ["Student List", className],
    };

    public static PdfReport Classes(IReadOnlyList<ClassRow> rows, string? search, string? status) => new()
    {
        Title = "Classes Report",
        Subtitle = status switch { "active" => "Active classes", "inactive" => "Inactive classes", _ => "All classes" },
        Meta =
        [
            new("Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()),
            new("Classes", rows.Count.ToString(C)),
            new("Students placed", rows.Sum(r => r.StudentCount).ToString(C)),
        ],
        Sections =
        [
            new PdfTable
            {
                Columns = [new("Class", 1), new("Enrolled", 0.9f, PdfAlign.End), new("Capacity", 0.9f, PdfAlign.End), new("Capacity status", 1.2f), new("Status", 1), new("Display order", 0.9f, PdfAlign.End), new("Last updated", 1.2f)],
                Rows = rows.Select(c =>
                {
                    var full = c.StudentCount >= c.Capacity;
                    var near = !full && (c.StudentCount >= c.Capacity - 3 || c.StudentCount >= (int)(c.Capacity * 0.85));
                    return R(
                        new PdfCell(c.Name, Strong: true),
                        PdfCell.Num(c.StudentCount),
                        PdfCell.Num(c.Capacity),
                        full ? PdfCell.Status("Full", PdfTone.Error) : near ? PdfCell.Status("Near capacity", PdfTone.Warning) : PdfCell.Status("Available", PdfTone.Success),
                        c.IsActive ? PdfCell.Status("Active", PdfTone.Success) : PdfCell.Status("Inactive", PdfTone.Muted),
                        PdfCell.Num(c.SortOrder),
                        Ui.Date(c.UpdatedAt));
                }).ToList(),
                Footer = ["Total", rows.Sum(r => r.StudentCount).ToString(C), rows.Sum(r => r.Capacity).ToString(C), "", "", "", ""],
                EmptyText = "No classes match these filters.",
            },
        ],
        FileNameParts = ["Classes Report", status],
    };

    // ================================================================== management team

    public static PdfReport ManagementMembers(IReadOnlyList<ManagementMemberRow> rows, string? search, string? position) => new()
    {
        Title = "Management Team",
        Subtitle = position == "none" ? "Members without a position"
            : Enum.TryParse<ManagementPosition>(position, out var p) ? $"Position: {ManagementPositions.Name(p)}" : "Team members",
        Meta = [new("Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()), new("Members", rows.Count.ToString(C))],
        Sections =
        [
            new PdfTable
            {
                RowNumbers = true,
                Columns =
                [
                    new("Name", 2.2f), new("Position", 2), new("Contact number", 1.3f), new("Email", 2), new("Appointed", 1),
                    new("Open tasks", 0.8f, PdfAlign.End), new("Overdue", 0.8f, PdfAlign.End), new("Completed", 0.8f, PdfAlign.End),
                ],
                Rows = rows.Select(m => R(
                    new PdfCell(m.FullName, Strong: true),
                    m.Position is null ? new PdfCell(m.PositionName, Tone: PdfTone.Muted) : m.PositionName,
                    m.ContactNumber,
                    m.Email,
                    m.AppointedOn?.ToString("d MMM yyyy", C),
                    PdfCell.Num(m.OpenTasks),
                    new PdfCell(m.OverdueTasks.ToString(C), Tone: m.OverdueTasks > 0 ? PdfTone.Error : PdfTone.Default),
                    PdfCell.Num(m.CompletedTasks))).ToList(),
                EmptyText = "No members match these filters.",
            },
        ],
        FileNameParts = ["Management Team", position],
    };

    public static PdfReport Tasks(IReadOnlyList<TaskRow> rows, TaskListQuery q, string? memberName) => new()
    {
        Title = "Management Team Tasks",
        Subtitle = memberName is null ? "All team members" : $"Member: {memberName}",
        Meta =
        [
            new("Search", string.IsNullOrWhiteSpace(q.Search) ? null : q.Search.Trim()),
            new("Status", q.Status switch { "open" => "Open", "overdue" => "Overdue", "todo" => "To Do", "in-progress" => "In Progress", "on-hold" => "On Hold", "completed" => "Completed", _ => null }),
            new("Priority", string.IsNullOrWhiteSpace(q.Priority) ? null : C.TextInfo.ToTitleCase(q.Priority)),
            new("Due", q.Due switch { "overdue" => "Overdue", "today" => "Due today", "week" => "Next 7 days", "later" => "Due later", "none" => "No due date", _ => null }),
            new("Tasks", rows.Count.ToString(C)),
        ],
        Sections =
        [
            new PdfSummary
            {
                Items =
                [
                    .. TaskLabels.Statuses.Select(s => new PdfStat(TaskLabels.Status(s), rows.Count(r => r.Status == s).ToString(C), TaskTone(s))),
                    new PdfStat("Overdue", rows.Count(r => r.IsOverdue).ToString(C), PdfTone.Error),
                ],
            },
            new PdfTable
            {
                RowNumbers = true,
                Columns = [new("Task", 3), new("Assigned to", 1.8f), new("Priority", 0.9f), new("Status", 1.1f), new("Due", 1.3f), new("Last updated", 1.3f)],
                Rows = rows.Select(t => R(
                    new PdfCell(t.Title, t.Description, Strong: true),
                    new PdfCell(t.MemberName, ManagementPositions.Name(t.MemberPosition)),
                    PdfCell.Status(TaskLabels.Priority(t.Priority), t.Priority switch { TaskPriority.Urgent => PdfTone.Error, TaskPriority.High => PdfTone.Warning, TaskPriority.Medium => PdfTone.Info, _ => PdfTone.Muted }),
                    PdfCell.Status(TaskLabels.Status(t.Status), TaskTone(t.Status)),
                    t.Status == ManagementTaskStatus.Completed && t.CompletedAt is { } done
                        ? new PdfCell("Completed " + Ui.Date(done), Tone: PdfTone.Success)
                        : new PdfCell(TaskLabels.Due(t.DueDate, t.DueTime), t.IsOverdue ? "Overdue" : t.IsDueToday ? "Due today" : null, t.IsOverdue ? PdfTone.Error : PdfTone.Default),
                    new PdfCell(Ui.DateTime(t.UpdatedAt), t.UpdatedBy is null ? null : "By " + t.UpdatedBy))).ToList(),
                EmptyText = "No tasks match these filters.",
            },
        ],
        FileNameParts = ["Management Tasks", memberName],
    };

    // ================================================================== belongings

    /// <summary>Weekly belongings readiness of every student of a grade.</summary>
    public static PdfReport BelongingsGradeWeek(string grade, QuarterView quarter, WeekInfo week, IReadOnlyList<StudentWeekRow> rows) => new()
    {
        Title = "Belongings Report",
        Subtitle = $"{GradeCatalog.Label(grade)} · Week {week.Number}",
        ShowAcademicYear = false, // the quarter's own academic year is shown instead
        Meta =
        [
            new("Academic Year", quarter.YearName), new("Quarter", quarter.Name),
            new("Week", $"{week.WeekStart.ToString("d MMM", C)} – {week.WeekEnd.ToString("d MMM yyyy", C)}"),
            new("Students", rows.Count.ToString(C)),
        ],
        Sections =
        [
            new PdfNotice { Text = "Readiness = items in order ÷ items checked. N/A items and days without a record are never counted as Red.", Tone = PdfTone.Muted },
            new PdfTable
            {
                RowNumbers = true,
                Columns =
                [
                    new("Student", 2.6f), new("Class", 0.9f), new("Personal", 1, PdfAlign.End), new("Academic", 1, PdfAlign.End),
                    new("Overall", 1, PdfAlign.End), new("Recorded days", 1, PdfAlign.End), new("Missing days", 1, PdfAlign.End),
                ],
                Rows = rows.Select(r => R(
                    new PdfCell(r.Name, Strong: true),
                    r.ClassName,
                    ReadinessCounts.Text(r.Readiness.Personal.Percent),
                    ReadinessCounts.Text(r.Readiness.Academic.Percent),
                    new PdfCell(ReadinessCounts.Text(r.Readiness.Overall.Percent), Strong: true, Tone: ReadinessTone(r.Readiness.Overall.Percent)),
                    PdfCell.Num(r.RecordedDays),
                    new PdfCell(r.MissingDays.ToString(C), Tone: r.MissingDays > 0 ? PdfTone.Warning : PdfTone.Muted))).ToList(),
                EmptyText = "No students in this grade.",
            },
        ],
        FileNameParts = ["Belongings Report", GradeCatalog.Label(grade), $"Week {week.Number}"],
    };

    // ================================================================== helpers

    private static IReadOnlyList<PdfCell> R(params PdfCell[] cells) => cells;

    /// <summary>Detail lines of a grid entry without blanks or repeats (a "PE" subject in the "PE" department prints once).</summary>
    private static IReadOnlyList<string> Lines(string title, params string?[] lines) =>
        lines.Where(l => !string.IsNullOrWhiteSpace(l) && !string.Equals(l, title, StringComparison.OrdinalIgnoreCase))
            .Select(l => l!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static IReadOnlyList<IReadOnlyList<PdfGridEntry>> Row(IEnumerable<IReadOnlyList<PdfGridEntry>> cells) => cells.ToList();
    private static List<SlotView> Active(TimetableGrid g) => g.Slots.Where(s => s.IsActive).ToList();
    private static PdfAxis SlotAxis(SlotView s) => new(s.Label, s.HasTimes ? s.Times : "Time not set");

    private static PdfAxis DayAxis(DateOnly week, DayOfWeek day)
    {
        var date = ScheduleRules.DateOf(week, day);
        return new(ScheduleRules.DayName(day), date.ToString("d MMM", C), date == SchoolWeek.Today);
    }

    private static PdfSection[] PastWeek(bool editable) =>
        editable ? [] : [new PdfNotice { Text = "Past week — shown as history. Changes to the timetable take effect from the current week.", Tone = PdfTone.Muted }];

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", C, DateTimeStyles.None, out var d) ? d : null;

    private static string AuditText(DutyRow d)
    {
        var parts = new List<string>();
        if (d.FollowUpRequired) parts.Add("Follow-up required");
        if (!string.IsNullOrWhiteSpace(d.IssuesEncountered)) parts.Add("Issues: " + d.IssuesEncountered.Trim());
        if (!string.IsNullOrWhiteSpace(d.AuditNotes)) parts.Add("Notes: " + d.AuditNotes.Trim());
        return parts.Count == 0 ? "No audit notes" : string.Join(" · ", parts);
    }

    private static PdfTone DutyTone(DutyStatus s) => s switch
    {
        DutyStatus.Covered => PdfTone.Success,
        DutyStatus.InProgress => PdfTone.Info,
        DutyStatus.NotCovered => PdfTone.Error,
        _ => PdfTone.Muted,
    };

    private static PdfTone StatusTone(WorkloadStatus s) => s switch
    {
        WorkloadStatus.Available => PdfTone.Success,
        WorkloadStatus.NearCapacity => PdfTone.Warning,
        _ => PdfTone.Error,
    };

    private static PdfTone TaskTone(ManagementTaskStatus s) => s switch
    {
        ManagementTaskStatus.Completed => PdfTone.Success,
        ManagementTaskStatus.InProgress => PdfTone.Info,
        ManagementTaskStatus.OnHold => PdfTone.Warning,
        _ => PdfTone.Muted,
    };

    private static PdfTone ReadinessTone(double? percent) => percent switch
    {
        null => PdfTone.Muted,
        >= 80 => PdfTone.Success,
        >= 50 => PdfTone.Warning,
        _ => PdfTone.Error,
    };
}
