using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

// ---------- Students → Behavioural / Discipline → Incident ----------

public sealed class IncidentListViewModel
{
    public required IReadOnlyList<IncidentRow> Incidents { get; init; }
    public required IncidentListQuery Query { get; init; }
    public required IncidentSuggestions Suggestions { get; init; }
    public bool HasFilters => Query != new IncidentListQuery();
}

public sealed class IncidentPageViewModel
{
    public required IncidentDetails Incident { get; init; }
    public required string SchoolName { get; init; }

    /// <summary>An open record can be changed (by anyone; "Working as" is not required).</summary>
    public bool CanEdit => !Incident.IsClosed;
}

public sealed class StudentIncidentsViewModel
{
    public required int StudentId { get; init; }
    public required string StudentName { get; init; }
    public required string ClassName { get; init; }
    public required IReadOnlyList<IncidentRow> Incidents { get; init; }
}

/// <summary>A(new / edit): the report header.</summary>
public sealed class IncidentHeaderForm
{
    public int? Id { get; set; }
    public string? Reference { get; set; }
    public string? Category { get; set; }
    public DateOnly? Date { get; set; }
    public string? Department { get; set; }
    public IncidentSeverity? Severity { get; set; }
    public IncidentStatus? Status { get; set; }
    public IncidentSuggestions? Suggestions { get; set; }

    public IncidentHeaderInput ToInput() => new(Category, Date, Department, Severity, Status);
}

/// <summary>B: a student involved.</summary>
public sealed class IncidentStudentForm
{
    public int IncidentId { get; set; }
    public int? RowId { get; set; }
    public int? StudentId { get; set; }
    public IncidentRole? Role { get; set; }
    public string? RoleOther { get; set; }
    public string? Involvement { get; set; }

    public string? Reference { get; set; }
    public string? StudentName { get; set; }
    public IReadOnlyList<StudentPick> Students { get; set; } = [];

    public IncidentStudentInput ToInput() => new(StudentId, Role, RoleOther, Involvement);
}

/// <summary>C: a timeline event.</summary>
public sealed class TimelineForm
{
    public int IncidentId { get; set; }
    public int? EntryId { get; set; }
    public string? TimePeriod { get; set; }
    public TimeOnly? Time { get; set; }
    public string? Location { get; set; }
    public List<int> PresentIds { get; set; } = [];
    public string? OthersPresent { get; set; }
    public string? Account { get; set; }

    public string? Reference { get; set; }
    public IReadOnlyList<IncidentStudentView> Involved { get; set; } = [];
    public IncidentSuggestions? Suggestions { get; set; }

    public TimelineInput ToInput() => new(TimePeriod, Time, Location, PresentIds, OthersPresent, Account);
}

/// <summary>D: an investigation finding / administrative action.</summary>
public sealed class IncidentActionForm
{
    public int IncidentId { get; set; }
    public int? ActionId { get; set; }
    public string? ActionItem { get; set; }
    public string? Finding { get; set; }
    public string? Details { get; set; }
    public IncidentActionStatus? Status { get; set; }

    public string? Reference { get; set; }
    public IncidentSuggestions? Suggestions { get; set; }

    public IncidentActionInput ToInput() => new(ActionItem, Finding, Details, Status);
}

/// <summary>E: administrative sign-off.</summary>
public sealed class SignOffForm
{
    public int IncidentId { get; set; }
    public int? PreparedById { get; set; }
    public string? School { get; set; }
    public DateOnly? ReportDate { get; set; }
    public int? ReviewedById { get; set; }
    public IncidentApproval? Approval { get; set; }
    public string? ApprovalNotes { get; set; }

    public string? Reference { get; set; }
    public IReadOnlyList<StaffOption> Staff { get; set; } = [];

    public SignOffInput ToInput() => new(PreparedById, School, ReportDate, ReviewedById, Approval, ApprovalNotes);
}

public sealed class ReopenForm
{
    public int IncidentId { get; set; }
    public string? Reason { get; set; }
    public string? Reference { get; set; }
}

// ---------- the complete report: Add Report / Edit Report page ----------

/// <summary>One repeating entry of the report form: <paramref name="Key"/> is its index, or "__i__" in the template for new entries.</summary>
public sealed record ReportEntry<T>(T Entry, string Key, IncidentReportForm Form)
{
    public bool IsTemplate => Key == "__i__";
}

public sealed class ReportStudentEntry
{
    /// <summary>Display only: the class recorded on the report for this student (grade &amp; section as reported).</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public string? RecordedClass { get; set; }
    public int? RowId { get; set; }
    public int? StudentId { get; set; }
    public IncidentRole? Role { get; set; } = IncidentRole.ReportedConduct;
    public string? RoleOther { get; set; }
    public string? Involvement { get; set; }
}

public sealed class ReportTimelineEntry
{
    public int? EntryId { get; set; }
    public string? TimePeriod { get; set; }
    public TimeOnly? Time { get; set; }
    public string? Location { get; set; }

    /// <summary>Student Directory ids of the students present (they must be listed in Students Involved).</summary>
    public List<int> PresentStudentIds { get; set; } = [];
    public string? OthersPresent { get; set; }
    public string? Account { get; set; }
}

public sealed class ReportActionEntry
{
    public int? ActionId { get; set; }
    public string? ActionItem { get; set; }
    public string? Finding { get; set; }
    public string? Details { get; set; }
    public IncidentActionStatus? Status { get; set; } = IncidentActionStatus.Pending;
}

/// <summary>
/// The Add Report / Edit Report page: the five sections of the official report in one form, saved together
/// (Save Draft = may be incomplete; Save Report = checked in full).
/// </summary>
public sealed class IncidentReportForm
{
    public int? Id { get; set; }

    /// <summary>Random key of this form: a second submit of the same new report opens the first one instead of creating a copy.</summary>
    public Guid SubmissionKey { get; set; } = Guid.NewGuid();

    // 1. Incident information
    public string? Category { get; set; }
    public DateOnly? Date { get; set; }
    public string? Department { get; set; }
    public IncidentSeverity? Severity { get; set; } = IncidentSeverity.Level1;
    public IncidentStatus? Status { get; set; } = IncidentStatus.Reported;

    // 2–4. repeating entries
    public List<ReportStudentEntry> Students { get; set; } = [];
    public List<ReportTimelineEntry> Timeline { get; set; } = [];
    public List<ReportActionEntry> Actions { get; set; } = [];

    // 5. Administrative sign-off & record closure
    /// <summary>"staff" (a staff member) or "text" (a department or person written in).</summary>
    public string PreparedByMode { get; set; } = "text";
    public int? PreparedById { get; set; }
    public string? PreparedByText { get; set; }
    public string? School { get; set; }
    public DateOnly? ReportDate { get; set; }
    public int? ReviewedById { get; set; }
    public IncidentApproval? Approval { get; set; } = IncidentApproval.Pending;
    public string? ApprovalNotes { get; set; }

    // ---- display only
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public string? Reference { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public bool IsDraft { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public string SchoolName { get; set; } = "";
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public IReadOnlyList<StudentPick> StudentList { get; set; } = [];
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public IReadOnlyList<StaffOption> Staff { get; set; } = [];
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public IncidentSuggestions? Suggestions { get; set; }

    public bool IsEdit => Id is > 0;

    public IncidentReportInput ToInput() => new(
        Category, Date, Department, Severity, Status,
        Students.Select(s => new ReportStudentInput(s.RowId, s.StudentId, s.Role, s.RoleOther, s.Involvement)).ToList(),
        Timeline.Select(t => new ReportTimelineInput(t.EntryId, t.TimePeriod, t.Time, t.Location, t.PresentStudentIds, t.OthersPresent, t.Account)).ToList(),
        Actions.Select(a => new ReportActionInput(a.ActionId, a.ActionItem, a.Finding, a.Details, a.Status)).ToList(),
        PreparedByMode == "staff" ? PreparedById : null, PreparedByMode == "staff" ? null : PreparedByText,
        School, ReportDate, ReviewedById, Approval, ApprovalNotes);

    public static IncidentReportForm From(IncidentDetails d) => new()
    {
        Id = d.Id, Reference = d.Reference, IsDraft = d.IsDraft,
        Category = d.Category, Date = d.Date, Department = d.Department, Severity = d.Severity, Status = d.Status,
        Students = d.Students.Select(s => new ReportStudentEntry { RowId = s.Id, StudentId = s.StudentId, Role = s.Role, RoleOther = s.RoleOther, Involvement = s.Involvement, RecordedClass = s.ClassName }).ToList(),
        Timeline = d.Timeline.Select(t => new ReportTimelineEntry
        {
            EntryId = t.Id, TimePeriod = t.TimePeriod, Time = t.Time, Location = t.Location, OthersPresent = t.OthersPresent, Account = t.Account,
            PresentStudentIds = t.Present.Select(p => d.Students.First(s => s.Id == p.IncidentStudentId).StudentId).ToList(),
        }).ToList(),
        Actions = d.Actions.Select(a => new ReportActionEntry { ActionId = a.Id, ActionItem = a.ActionItem, Finding = a.Finding, Details = a.Details, Status = a.Status }).ToList(),
        PreparedByMode = d.PreparedById is not null ? "staff" : "text", PreparedById = d.PreparedById, PreparedByText = d.PreparedByText,
        School = d.SignOffSchool, ReportDate = d.ReportDate, ReviewedById = d.ReviewedById, Approval = d.Approval, ApprovalNotes = d.ApprovalNotes,
    };
}
