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
    public bool CanEdit { get; init; }
    public bool HasFilters => Query != new IncidentListQuery();
}

public sealed class IncidentPageViewModel
{
    public required IncidentDetails Incident { get; init; }
    public required string SchoolName { get; init; }

    /// <summary>Changes need a named author ("Working as") and an open record.</summary>
    public bool Identified { get; init; }
    public bool CanEdit => Identified && !Incident.IsClosed;
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
