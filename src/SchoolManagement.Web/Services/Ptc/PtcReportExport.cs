using System.Globalization;
using ClosedXML.Excel;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>
/// The PTC report workbook (Super Admin only): a summary with the filters, generation time and definitions, then one sheet
/// per report, all built from the same filtered <see cref="PtcReportData"/> the reports page shows. Never contains parent
/// phone numbers, feedback links or any account data. An empty report keeps its headers and says so in its first row.
/// </summary>
public static class PtcReportExport
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static readonly string[] SheetNames =
    [
        "Summary", "Event Summary", "Parent Bookings", "Teacher Interviews", "Teacher Performance", "Classes", "Departments", "Parent Feedback",
        "Concerns", "Substitute Coverage",
    ];

    public static string FileName(DateTime generatedLocal) => $"PTC_Reports_{generatedLocal.ToString("yyyyMMdd_HHmm", C)}.xlsx";

    private static string Pct(double? v) => v is { } x ? x.ToString("0.0", C) + "%" : "No data";
    private static string Local(DateTime? utc) => utc is { } u ? SchoolTime.Current.ToLocal(u).ToString("yyyy-MM-dd HH:mm", C) : "";
    private static string Rating(PtcRating? r) => r?.ToString() ?? "";

    private static void Sheet(XLWorkbook wb, string name, string[] headers, IEnumerable<object?[]> rows)
    {
        var ws = wb.Worksheets.Add(name);
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var header = ws.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F3A5F");
        header.Style.Font.FontColor = XLColor.White;
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
            {
                var cell = ws.Cell(r, c + 1);
                switch (row[c])
                {
                    case null: break;
                    case int i: cell.Value = i; break;
                    case double d: cell.Value = Math.Round(d, 1); break;
                    case DateOnly d: cell.Value = d.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "yyyy-mm-dd"; break;
                    default: cell.Value = row[c]!.ToString(); break;
                }
            }
            r++;
        }
        if (r == 2) ws.Cell(2, 1).Value = "No records match the selected filters.";
        ws.SheetView.FreezeRows(1);
        if (r > 2) ws.Range(1, 1, r - 1, headers.Length).SetAutoFilter();
        ws.Columns(1, headers.Length).AdjustToContents(1, Math.Min(r, 200), 8, 60);
    }

    public static XLWorkbook Build(PtcReportData d)
    {
        var wb = new XLWorkbook();
        var f = d.Filter;
        var k = d.Kpis;
        var generated = SchoolTime.Current.ToLocal(d.GeneratedUtc);

        // 1. Summary: filters, time, key figures, definitions.
        Sheet(wb, SheetNames[0], ["Item", "Value"], new object?[][]
        {
            ["Report", "THRYD PTC reports (Super Admin)"],
            ["Generated", generated.ToString("yyyy-MM-dd HH:mm", C) + " (school time)"],
            ["Academic year", d.FilterLabel("year", f.YearId, d.Years) ?? "All"],
            ["PTC event", d.FilterLabel("event", f.EventId, d.EventOptions) ?? "All"],
            ["Date range", f.From is null && f.To is null ? "All dates" : $"{f.From?.ToString("yyyy-MM-dd", C) ?? "…"} to {f.To?.ToString("yyyy-MM-dd", C) ?? "…"}"],
            ["Class", d.FilterLabel("class", f.ClassId, d.ClassOptions) ?? "All"],
            ["Department", d.FilterLabel("dept", f.DepartmentId, d.DepartmentOptions) ?? "All"],
            ["Teacher", d.FilterLabel("teacher", f.TeacherId, d.TeacherOptions) ?? "All"],
            ["Interview status", f.State is { } s ? PtcLiveRules.Label(s) : "All"],
            ["Feedback status", f.Feedback?.ToString() ?? "All"],
            ["PTC events", k.Events], ["Completed events", k.CompletedEvents],
            ["Parent bookings (confirmed)", k.Bookings], ["Cancelled bookings", k.CancelledBookings],
            ["Teacher interviews (not cancelled)", k.Interviews], ["Completed interviews", k.Completed], ["Missed interviews", k.Missed],
            ["Upcoming interviews", k.Upcoming], ["In-progress interviews", k.InProgress], ["Cancelled interviews", k.Cancelled],
            ["Interview completion rate", Pct(k.CompletionRate)], ["Cancellation rate", Pct(k.CancellationRate)],
            ["Average actual meeting duration", k.AverageActualMinutes.Text("0.0", " min")],
            ["Participating teachers", k.Teachers], ["Participating classes", k.Classes],
            ["Feedback submissions", k.Feedback], ["Eligible bookings", k.Eligible], ["Feedback response rate", Pct(k.ResponseRate)],
            ["Average parent satisfaction (overall)", k.Satisfaction.Text()], ["Concerns", k.Concerns], ["Open concerns", k.OpenConcerns], ["Resolved concerns", k.ResolvedConcerns],
            ["Definitions", "Parent booking = one family's meeting window; teacher interview = one teacher × one subject of a booking. Completion rate = completed / (completed + missed). Missed = never started and planned end passed (server time). Durations use server Start/End stamps. Response rate = eligible bookings with feedback / eligible bookings (confirmed, class PTC over). Averages need at least " + PtcReportService.MinSample + " answers."],
        });

        Sheet(wb, SheetNames[1], ["Event", "Year", "Start", "End", "Status", "Classes", "Rounds", "Parent bookings", "Cancelled bookings", "Teacher interviews", "Completed",
            "Missed", "Cancelled", "Upcoming", "In progress", "Teachers", "Participating classes", "Substitutes", "Uncovered coverage (live)", "Feedback", "Eligible", "Response rate",
            "Concerns", "Open concerns", "Avg actual minutes", "Avg overall rating"],
            d.Events.Select(e => new object?[] { e.Title, e.Year, e.Start, e.End, e.Status.ToString(), e.Classes, e.Rounds, e.Bookings, e.CancelledBookings, e.Interviews,
                e.Completed, e.Missed, e.Cancelled, e.Upcoming, e.InProgress, e.Teachers, e.ParticipatingClasses, e.Substitutes,
                e.CoverageConflicts is { } cc ? cc : "Not computed (event closed)", e.Feedback, e.Eligible, Pct(e.ResponseRate), e.Concerns, e.OpenConcerns,
                e.AverageMinutes.Text("0.0", ""), e.Overall.Text() }));

        Sheet(wb, SheetNames[2], ["Booking reference", "Event", "Class", "Date", "Round", "Slot", "Student", "Status", "Booked at", "Feedback eligibility", "Feedback reference"],
            d.Bookings.OrderBy(b => b.Date).ThenBy(b => b.ClassName).ThenBy(b => b.RoundNumber).ThenBy(b => b.Slot).Select(b => new object?[]
            {
                b.Reference, b.EventTitle, b.ClassName, b.Date, b.RoundNumber, b.Slot, b.StudentName, b.Status.ToString(), Local(b.BookedAt), b.FeedbackAvailability.ToString(),
                d.Feedback.FirstOrDefault(x => x.BookingId == b.Id)?.Reference,
            }));

        Sheet(wb, SheetNames[3], ["Booking reference", "Event", "Class", "Date", "Round", "Teacher", "Department", "Subject", "Planned start", "Planned end", "Actual start",
            "Actual end", "Actual minutes", "State"],
            d.Interviews.Select(i => new object?[]
            {
                i.BookingReference, i.EventTitle, i.ClassName, i.Date, i.RoundNumber, i.TeacherName, i.Department, i.Subject, i.PlannedStart.ToString("HH:mm", C),
                i.PlannedEnd.ToString("HH:mm", C), Local(i.ActualStart), Local(i.ActualEnd),
                i.ActualStart is { } s && i.ActualEnd is { } e && e >= s ? (e - s).TotalMinutes : null, PtcLiveRules.Label(i.State),
            }));

        Sheet(wb, SheetNames[4], ["Teacher", "Department", "Events", "Meetings", "Completed", "Missed", "Cancelled", "Upcoming", "Avg actual minutes", "Total actual minutes",
            "Parent feedback (their bookings)", "Teacher-specific feedback", "Avg communication (teacher-specific)", "Positive", "Negative",
            "Open concerns (their parents)", "Resolved concerns (their parents)"],
            d.Teachers.Select(t => new object?[]
            {
                t.Name, t.Department, t.Events, t.Meetings, t.Completed, t.Missed, t.Cancelled, t.Upcoming, t.AverageMinutes.Text("0.0", ""), t.TotalMinutes,
                t.ParentFeedback, t.TeacherFeedback, t.Communication.Text(), t.Positive, t.Negative, t.OpenConcerns, t.ResolvedConcerns,
            }));

        Sheet(wb, SheetNames[5], ["Class", "Events", "Subjects", "Teachers", "Parent bookings", "Completed", "Missed", "Cancelled", "Feedback", "Avg overall", "Concerns", "Open concerns"],
            d.Classes.Select(c => new object?[]
            {
                c.Name, string.Join(", ", c.Events), string.Join(", ", c.Subjects), c.Teachers, c.Bookings, c.Completed, c.Missed, c.Cancelled, c.Feedback, c.Overall.Text(),
                c.Concerns, c.OpenConcerns,
            }));

        Sheet(wb, SheetNames[6], ["Department", "Teachers", "Classes", "Parent meetings", "Completed", "Missed", "Feedback", "Avg communication (teacher-specific)", "Concerns",
            "Resolved", "Unresolved"],
            d.Departments.Select(x => new object?[] { x.Name, x.Teachers, x.Classes, x.Meetings, x.Completed, x.Missed, x.Feedback, x.Communication.Text(), x.Concerns, x.Resolved, x.Unresolved }));

        var bookings = d.Bookings.ToDictionary(b => b.Id);
        Sheet(wb, SheetNames[7], ["Feedback reference", "Booking reference", "Event", "Class", "Date", "Submitted", "Overall", "Communication", "Organization", "Meeting helpful",
            "Comments", "Teacher-specific entries", "Review status"],
            d.Feedback.Select(x => new object?[]
            {
                x.Reference, bookings.GetValueOrDefault(x.BookingId)?.Reference, bookings.GetValueOrDefault(x.BookingId)?.EventTitle, bookings.GetValueOrDefault(x.BookingId)?.ClassName,
                bookings.GetValueOrDefault(x.BookingId)?.Date, Local(x.SubmittedAt), Rating(x.Overall), Rating(x.Communication), Rating(x.Organization), x.Helpful?.ToString(),
                x.Comments, d.TeacherFeedback.Count(t => t.FeedbackId == x.Id), x.Review.ToString(),
            }));

        Sheet(wb, SheetNames[8], ["Feedback reference", "Event", "Class", "Submitted", "Category", "Concern", "Status", "Assigned to", "Resolution", "Resolved at"],
            d.Feedback.Where(x => x.ConcernStatus is not null).Select(x => new object?[]
            {
                x.Reference, bookings.GetValueOrDefault(x.BookingId)?.EventTitle, bookings.GetValueOrDefault(x.BookingId)?.ClassName, Local(x.SubmittedAt),
                x.Category is { } cat ? PtcFeedbackRules.Label(cat) : null, x.Concern, PtcFeedbackRules.Label(x.ConcernStatus!.Value), x.AssignedTo, x.Resolution, Local(x.ResolvedAt),
            }));

        Sheet(wb, SheetNames[9], ["Event", "Class schedule", "Date", "Cover start", "Cover end", "Original teacher", "Substitute", "Lesson", "Status", "Assigned by", "Assigned at"],
            d.Substitutes.OrderBy(s => s.Date).ThenBy(s => s.Start).Select(s => new object?[]
            {
                s.EventTitle, s.ClassName, s.Date, s.Start.ToString("HH:mm", C), s.End.ToString("HH:mm", C), s.Original, s.Substitute, s.Lesson, s.Status.ToString(),
                s.AssignedBy, Local(s.AssignedAt),
            }));
        return wb;
    }
}
