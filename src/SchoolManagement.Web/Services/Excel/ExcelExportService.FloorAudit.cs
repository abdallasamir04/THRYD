using ClosedXML.Excel;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Services.Excel;

/// <summary>Floor Audit exports: inspections with their recorded answers, and student movements. Same filters as the page.</summary>
public sealed partial class ExcelExportService
{
    public byte[] FloorAudit(IReadOnlyList<(InspectionBrief Inspection, IReadOnlyList<InspectionResponseView> Responses)> rows, string filterSummary, string schoolName)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Inspections");
        WriteHeader(ws, ["Date", "Floor", "Area", "Type", "Session", "Session start", "Round", "Scheduled", "Started", "Completed", "Minutes after schedule",
            "Status", "Inspector", "Issues", "Issues found", "Observations"]);
        var r = 2;
        foreach (var (i, _) in rows)
        {
            ws.Cell(r, 1).Value = i.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 2).Value = i.FloorName;
            ws.Cell(r, 3).Value = i.AreaName;
            ws.Cell(r, 4).Value = FloorAuditLabels.Kind(i.Kind);
            ws.Cell(r, 5).Value = i.SessionLabel;
            ws.Cell(r, 6).Value = i.SessionStart.ToString("HH:mm");
            ws.Cell(r, 7).Value = i.Round;
            ws.Cell(r, 8).Value = i.ScheduledTime.ToString("HH:mm");
            ws.Cell(r, 9).Value = SchoolTime.Current.ToLocal(i.StartedAt).ToString("HH:mm");
            ws.Cell(r, 10).Value = i.CompletedAt is { } c ? SchoolTime.Current.ToLocal(c).ToString("HH:mm") : "";
            if (i.LateMinutes is { } late) ws.Cell(r, 11).Value = late;
            ws.Cell(r, 12).Value = FloorAuditLabels.Area(i.AreaStatus);
            ws.Cell(r, 13).Value = i.Inspector ?? "";
            ws.Cell(r, 14).Value = i.Issues;
            ws.Cell(r, 15).Value = i.IssueSummary ?? "";
            ws.Cell(r, 16).Value = i.Observations ?? "";
            r++;
        }
        FinishTable(ws, rows.Count, 16);

        var answers = wb.Worksheets.Add("Checklist answers");
        WriteHeader(answers, ["Date", "Floor", "Area", "Session", "Round", "Item", "Response type", "Required", "Answer", "Issue"]);
        r = 2;
        foreach (var (i, responses) in rows)
            foreach (var a in responses)
            {
                answers.Cell(r, 1).Value = i.Date.ToDateTime(TimeOnly.MinValue);
                answers.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
                answers.Cell(r, 2).Value = i.FloorName;
                answers.Cell(r, 3).Value = i.AreaName;
                answers.Cell(r, 4).Value = i.SessionLabel;
                answers.Cell(r, 5).Value = i.Round;
                answers.Cell(r, 6).Value = a.ItemName;
                answers.Cell(r, 7).Value = AuditResponses.Label(a.Type);
                answers.Cell(r, 8).Value = a.Required ? "Yes" : "No";
                answers.Cell(r, 9).Value = a.Value ?? "";
                answers.Cell(r, 10).Value = a.IsIssue ? "Issue" : "";
                r++;
            }
        FinishTable(answers, r - 2, 10);
        AddInfoSheet(wb, $"Floor Audit Report · {schoolName}", rows.Count, filterSummary);
        return Save(wb);
    }

    public byte[] StudentMovements(IReadOnlyList<MovementRow> rows, MovementCounts counts, string filterSummary, string schoolName)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Movements");
        WriteHeader(ws, ["Date", "Student", "Class", "From", "From floor", "To", "To floor", "Custom destination", "Exit time", "Return time",
            "Minutes outside", "Status", "Recorded by", "Returned by", "Notes", "Void reason"]);
        var r = 2;
        foreach (var m in rows)
        {
            ws.Cell(r, 1).Value = m.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 2).Value = m.StudentName;
            ws.Cell(r, 3).Value = m.ClassName;
            ws.Cell(r, 4).Value = m.FromName ?? m.ClassName;
            ws.Cell(r, 5).Value = m.FromFloor ?? "";
            ws.Cell(r, 6).Value = m.ToName;
            ws.Cell(r, 7).Value = m.ToFloor ?? "";
            ws.Cell(r, 8).Value = m.Custom ? "Yes" : "";
            ws.Cell(r, 9).Value = SchoolTime.Current.ToLocal(m.ExitAt).ToString("HH:mm");
            ws.Cell(r, 10).Value = m.ReturnAt is { } back ? SchoolTime.Current.ToLocal(back).ToString("HH:mm") : "";
            // An open movement has no final duration: only a returned one is exported with minutes.
            if (m.State == MovementState.Returned) ws.Cell(r, 11).Value = (int)Math.Floor(m.Duration.TotalMinutes);
            ws.Cell(r, 12).Value = m.IsOpen ? $"{FloorAuditLabels.Movement(m.State)} (not returned)" : FloorAuditLabels.Movement(m.State);
            ws.Cell(r, 13).Value = m.RecordedBy ?? "";
            ws.Cell(r, 14).Value = m.ReturnedBy ?? "";
            ws.Cell(r, 15).Value = m.Notes ?? "";
            ws.Cell(r, 16).Value = m.VoidReason ?? "";
            r++;
        }
        FinishTable(ws, rows.Count, 16);

        var dest = wb.Worksheets.Add("By destination");
        WriteHeader(dest, ["Destination", "Floor", "Movements", "Outside now"]);
        r = 2;
        foreach (var d in counts.ByDestination)
        {
            dest.Cell(r, 1).Value = d.Name;
            dest.Cell(r, 2).Value = d.Floor ?? "";
            dest.Cell(r, 3).Value = d.Total;
            dest.Cell(r, 4).Value = d.Outside;
            r++;
        }
        FinishTable(dest, counts.ByDestination.Count, 4);

        AddInfoSheet(wb, $"Student Movement Report · {schoolName}", rows.Count, filterSummary);
        var info = wb.Worksheet("About");
        info.Cell(7, 1).Value = "Movements";
        info.Cell(7, 2).Value = counts.Total;
        info.Cell(8, 1).Value = "Outside";
        info.Cell(8, 2).Value = counts.Outside;
        info.Cell(9, 1).Value = "Returned";
        info.Cell(9, 2).Value = counts.Returned;
        info.Cell(10, 1).Value = "Overdue";
        info.Cell(10, 2).Value = counts.Overdue;
        info.Cell(11, 1).Value = "Average outside";
        info.Cell(11, 2).Value = counts.AverageOutside is { } avg ? FloorAuditLabels.Duration(avg) : "—";
        info.Range(7, 1, 11, 1).Style.Font.Bold = true;
        return Save(wb);
    }
}
