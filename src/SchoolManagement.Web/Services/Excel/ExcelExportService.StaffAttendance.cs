using ClosedXML.Excel;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Excel;

public sealed partial class ExcelExportService
{
    /// <summary>Staff Attendance over the filtered scope: summary, totals by staff member, every daily line and the correction history.</summary>
    public byte[] StaffAttendance(StaffAttendanceHistory h, string scope, string schoolName)
    {
        using var wb = new XLWorkbook();
        var info = wb.Worksheets.Add("Summary");
        SummarySheet(info, schoolName, "Staff Attendance Report",
        [
            ("Staff", scope), ("Period", $"{h.PeriodLabel} · {Period(h.From, h.To)}"), ("School days", h.SchoolDays),
            ("Expected staff-days", h.Totals.Expected), ("Present", h.Totals.Present), ("Late", h.Totals.Late), ("Absent", h.Totals.Absent),
            ("Not Recorded", h.Totals.NotRecorded),
        ]);

        var by = wb.Worksheets.Add("By staff member");
        WriteHeader(by, ["Staff Member", "Department", "Expected", "Present", "Late", "Absent", "Not Recorded", "Attendance %"]);
        var r = 2;
        foreach (var s in h.ByStaff)
        {
            by.Cell(r, 1).Value = s.Name;
            by.Cell(r, 2).Value = s.Group;
            by.Cell(r, 3).Value = s.Counts.Expected;
            by.Cell(r, 4).Value = s.Counts.Present;
            by.Cell(r, 5).Value = s.Counts.Late;
            by.Cell(r, 6).Value = s.Counts.Absent;
            by.Cell(r, 7).Value = s.Counts.NotRecorded;
            if (s.Percent is { } p) { by.Cell(r, 8).Value = p / 100; by.Cell(r, 8).Style.NumberFormat.Format = "0.0%"; }
            r++;
        }
        FinishTable(by, h.ByStaff.Count, 8);

        var ws = wb.Worksheets.Add("Daily records");
        WriteHeader(ws, ["Date", "Day", "Staff Member", "Department", "Status", "Arrival Time", "Recorded By", "Corrections"]);
        r = 2;
        foreach (var l in h.Lines)
        {
            ws.Cell(r, 1).Value = l.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "d mmm yyyy";
            ws.Cell(r, 2).Value = l.Date.DayOfWeek.ToString();
            ws.Cell(r, 3).Value = l.Name;
            ws.Cell(r, 4).Value = l.Group;
            ws.Cell(r, 5).Value = StaffAttendanceStatuses.Label(l.Status);
            ws.Cell(r, 5).Style.Font.FontColor = l.Status switch
            {
                StaffAttendanceStatus.Present => XLColor.FromHtml("#15803D"), StaffAttendanceStatus.Late => XLColor.FromHtml("#B45309"),
                StaffAttendanceStatus.Absent => XLColor.FromHtml("#B91C1C"), _ => XLColor.FromHtml("#64748B"),
            };
            if (l.ArrivalTime is { } t) { ws.Cell(r, 6).Value = t.ToTimeSpan(); ws.Cell(r, 6).Style.NumberFormat.Format = "h:mm AM/PM"; }
            ws.Cell(r, 7).Value = l.RecordedBy ?? "";
            ws.Cell(r, 8).Value = l.Corrections;
            r++;
        }
        FinishTable(ws, h.Lines.Count, 8);

        if (h.Corrections.Count > 0)
        {
            var c = wb.Worksheets.Add("Corrections");
            WriteHeader(c, ["Date", "Staff Member", "Before", "After", "Reason", "Corrected At", "Corrected By"]);
            r = 2;
            foreach (var x in h.Corrections)
            {
                c.Cell(r, 1).Value = x.Date.ToDateTime(TimeOnly.MinValue);
                c.Cell(r, 1).Style.DateFormat.Format = "d mmm yyyy";
                c.Cell(r, 2).Value = x.StaffName;
                c.Cell(r, 3).Value = x.Before;
                c.Cell(r, 4).Value = x.After;
                c.Cell(r, 5).Value = x.Reason ?? "";
                c.Cell(r, 6).Value = SchoolTime.Current.ToLocal(x.At);
                c.Cell(r, 6).Style.DateFormat.Format = "d mmm yyyy, h:mm AM/PM";
                c.Cell(r, 7).Value = x.By ?? "";
                r++;
            }
            FinishTable(c, h.Corrections.Count, 7);
        }
        return Save(wb);
    }
}
