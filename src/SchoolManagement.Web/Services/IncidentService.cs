using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

// ------------------------------------------------------------------ views and inputs

public sealed record IncidentListQuery(
    string? Search = null, string? Category = null, string? Department = null, IncidentStatus? Status = null, IncidentSeverity? Severity = null,
    DateOnly? From = null, DateOnly? To = null, string? Closure = null, int? StudentId = null);

public sealed record IncidentRow(
    int Id, string Reference, string Category, DateOnly Date, string Department, IncidentSeverity Severity, IncidentStatus Status, bool IsClosed,
    IReadOnlyList<(int StudentId, string Name, IncidentRole Role)> Students, DateTime UpdatedAt);

public sealed record IncidentStudentView(int Id, int StudentId, string Name, int ClassId, string ClassName, IncidentRole Role, string? RoleOther, string Involvement, int SortOrder)
{
    public string GradeSection => IncidentLabels.GradeSection(ClassName);
    public string RoleLabel => IncidentLabels.Role(Role, RoleOther);
}

public sealed record TimelineView(int Id, int SortOrder, string TimePeriod, TimeOnly? Time, string Location,
    IReadOnlyList<(int IncidentStudentId, string Name)> Present, string? OthersPresent, string Account)
{
    public string Period => Time is { } t ? $"{TimePeriod} · {t.ToString("HH:mm", CultureInfo.InvariantCulture)}" : TimePeriod;

    public string PresentText
    {
        get
        {
            var all = Present.Select(p => p.Name).ToList();
            if (!string.IsNullOrWhiteSpace(OthersPresent)) all.Add(OthersPresent!);
            return all.Count == 0 ? "—" : string.Join(", ", all);
        }
    }
}

public sealed record ActionView(int Id, int SortOrder, string ActionItem, string Finding, string? Details, IncidentActionStatus Status, DateTime UpdatedAt, string? UpdatedBy);

public sealed record IncidentHistoryView(string Action, string? Details, DateTime At, string? By);

public sealed record IncidentDetails(
    int Id, string Reference, string Category, DateOnly Date, string Department, IncidentSeverity Severity, IncidentStatus Status,
    int? PreparedById, string? PreparedByName, string? SignOffSchool, DateOnly? ReportDate, int? ReviewedById, string? ReviewedByName,
    IncidentApproval Approval, string? ApprovalNotes, bool IsClosed, DateTime? ClosedAt, string? ClosedBy,
    string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<IncidentStudentView> Students, IReadOnlyList<TimelineView> Timeline, IReadOnlyList<ActionView> Actions, IReadOnlyList<IncidentHistoryView> History)
{
    /// <summary>What still has to be done before the record can be closed (empty = ready to close).</summary>
    public IReadOnlyList<string> ClosureBlockers => IncidentRules.Blockers(this);
}

public sealed record IncidentHeaderInput(string? Category, DateOnly? Date, string? Department, IncidentSeverity? Severity, IncidentStatus? Status);
public sealed record IncidentStudentInput(int? StudentId, IncidentRole? Role, string? RoleOther, string? Involvement);
public sealed record TimelineInput(string? TimePeriod, TimeOnly? Time, string? Location, IReadOnlyList<int> PresentIds, string? OthersPresent, string? Account);
public sealed record IncidentActionInput(string? ActionItem, string? Finding, string? Details, IncidentActionStatus? Status);
public sealed record SignOffInput(int? PreparedById, string? School, DateOnly? ReportDate, int? ReviewedById, IncidentApproval? Approval, string? ApprovalNotes);

public sealed record IncidentSuggestions(IReadOnlyList<string> Categories, IReadOnlyList<string> Departments, IReadOnlyList<string> ActionItems, IReadOnlyList<string> Periods, IReadOnlyList<string> Locations);

public interface IIncidentService
{
    Task<IReadOnlyList<IncidentRow>> ListAsync(IncidentListQuery query, CancellationToken ct = default);
    Task<IncidentDetails?> GetAsync(int id, CancellationToken ct = default);
    Task<IncidentSuggestions> SuggestionsAsync(CancellationToken ct = default);

    Task<ServiceResult> CreateAsync(IncidentHeaderInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateHeaderAsync(int id, IncidentHeaderInput input, CancellationToken ct = default);

    Task<ServiceResult> AddStudentAsync(int id, IncidentStudentInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateStudentAsync(int id, int rowId, IncidentStudentInput input, CancellationToken ct = default);
    Task<ServiceResult> RemoveStudentAsync(int id, int rowId, CancellationToken ct = default);

    Task<ServiceResult> AddTimelineAsync(int id, TimelineInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateTimelineAsync(int id, int entryId, TimelineInput input, CancellationToken ct = default);
    Task<ServiceResult> DeleteTimelineAsync(int id, int entryId, CancellationToken ct = default);
    Task<ServiceResult> MoveTimelineAsync(int id, int entryId, int direction, CancellationToken ct = default);

    Task<ServiceResult> AddActionAsync(int id, IncidentActionInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateActionAsync(int id, int actionId, IncidentActionInput input, CancellationToken ct = default);
    Task<ServiceResult> SetActionStatusAsync(int id, int actionId, IncidentActionStatus status, CancellationToken ct = default);
    Task<ServiceResult> DeleteActionAsync(int id, int actionId, CancellationToken ct = default);

    Task<ServiceResult> SaveSignOffAsync(int id, SignOffInput input, CancellationToken ct = default);
    Task<ServiceResult> CloseAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> ReopenAsync(int id, string? reason, CancellationToken ct = default);

    /// <summary>Records that the official PDF was exported (confidential document leaving the system).</summary>
    Task LogExportAsync(int id, CancellationToken ct = default);
}

public static class IncidentLabels
{
    public static readonly IncidentSeverity[] Severities = [IncidentSeverity.Level1, IncidentSeverity.Level2, IncidentSeverity.Level3, IncidentSeverity.Level4];
    public static readonly IncidentStatus[] Statuses = [IncidentStatus.Reported, IncidentStatus.UnderInvestigation, IncidentStatus.UnderMonitoring, IncidentStatus.Resolved];
    public static readonly IncidentRole[] Roles = [IncidentRole.ReportedConduct, IncidentRole.AffectedStudent, IncidentRole.InterveningStudent, IncidentRole.Witness, IncidentRole.Other];
    public static readonly IncidentActionStatus[] ActionStatuses =
        [IncidentActionStatus.Pending, IncidentActionStatus.InProgress, IncidentActionStatus.Completed, IncidentActionStatus.IssuedAndLogged, IncidentActionStatus.Resolved];
    public static readonly IncidentApproval[] Approvals = [IncidentApproval.Pending, IncidentApproval.Approved, IncidentApproval.ApprovedWithConditions, IncidentApproval.NotApproved];

    public static string Severity(IncidentSeverity s) => s switch
    {
        IncidentSeverity.Level1 => "Level 1 (Guidance & Monitoring)",
        IncidentSeverity.Level2 => "Level 2 (Restorative Intervention)",
        IncidentSeverity.Level3 => "Level 3 (Formal Disciplinary Action)",
        IncidentSeverity.Level4 => "Level 4 (Serious / Safeguarding Referral)",
        _ => s.ToString(),
    };

    public static string SeverityShort(IncidentSeverity s) => $"Level {(int)s}";

    public static string Status(IncidentStatus s) => s switch
    {
        IncidentStatus.Reported => "Reported",
        IncidentStatus.UnderInvestigation => "Under Investigation",
        IncidentStatus.UnderMonitoring => "Under Monitoring",
        IncidentStatus.Resolved => "Resolved",
        _ => s.ToString(),
    };

    public static string Role(IncidentRole r, string? other = null) => r switch
    {
        IncidentRole.ReportedConduct => "Reported Conduct",
        IncidentRole.AffectedStudent => "Affected Student",
        IncidentRole.InterveningStudent => "Intervening Student",
        IncidentRole.Witness => "Witness",
        _ => string.IsNullOrWhiteSpace(other) ? "Other" : $"Other: {other}",
    };

    public static string ActionStatus(IncidentActionStatus s) => s switch
    {
        IncidentActionStatus.Pending => "Pending",
        IncidentActionStatus.InProgress => "In Progress",
        IncidentActionStatus.Completed => "Completed",
        IncidentActionStatus.IssuedAndLogged => "Issued & Logged",
        IncidentActionStatus.Resolved => "Resolved",
        _ => s.ToString(),
    };

    public static string Approval(IncidentApproval a) => a switch
    {
        IncidentApproval.Pending => "Pending approval",
        IncidentApproval.Approved => "Approved & endorsed",
        IncidentApproval.ApprovedWithConditions => "Approved with conditions",
        IncidentApproval.NotApproved => "Not approved",
        _ => a.ToString(),
    };

    public static string SeverityBadge(IncidentSeverity s) => s switch
    {
        IncidentSeverity.Level1 => "badge--primary",
        IncidentSeverity.Level2 => "badge--warning",
        _ => "badge--error",
    };

    public static string StatusBadge(IncidentStatus s) => s switch
    {
        IncidentStatus.Resolved => "badge--success",
        IncidentStatus.UnderMonitoring => "badge--primary",
        IncidentStatus.UnderInvestigation => "badge--warning",
        _ => "badge--neutral",
    };

    public static string ActionBadge(IncidentActionStatus s) => s switch
    {
        IncidentActionStatus.Pending => "badge--neutral",
        IncidentActionStatus.InProgress => "badge--warning",
        _ => "badge--success",
    };

    public static bool IsFinal(IncidentActionStatus s) => s is IncidentActionStatus.Completed or IncidentActionStatus.IssuedAndLogged or IncidentActionStatus.Resolved;

    /// <summary>"G5A" → "Grade 5 · Section A"; "KG1B" → "KG1 · Section B"; anything else as it is.</summary>
    public static string GradeSection(string className)
    {
        var grade = GradeCatalog.KeyOf(className) is { } key ? GradeCatalog.Label(key) : null;
        var section = ClassNumbering.Parse(className)?.Section;
        if (grade is null) return className;
        if (!string.IsNullOrEmpty(section)) return $"{grade} · Section {section}";
        // "PreK" is the grade itself; any other single-section class name is kept next to the grade.
        static string Key(string x) => new(x.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return Key(grade) == Key(className) ? grade : $"{grade} · {className}";
    }
}

public static class IncidentRules
{
    /// <summary>The record stays open until every one of these is done.</summary>
    public static IReadOnlyList<string> Blockers(IncidentDetails d)
    {
        var list = new List<string>();
        if (d.Students.Count == 0) list.Add("Add the students involved.");
        if (d.Timeline.Count == 0) list.Add("Add the timeline of events.");
        if (d.Actions.Count == 0) list.Add("Record the investigation findings and administrative actions.");
        else if (d.Actions.Any(a => !IncidentLabels.IsFinal(a.Status)))
            list.Add("Complete every action (status Completed, Issued & Logged or Resolved).");
        if (d.Status is not (IncidentStatus.Resolved or IncidentStatus.UnderMonitoring)) list.Add("Set the incident status to Resolved or Under Monitoring.");
        if (d.PreparedById is null) list.Add("Sign-off: choose who prepared the report.");
        if (d.ReportDate is null) list.Add("Sign-off: enter the date of the report.");
        if (d.ReviewedById is null) list.Add("Sign-off: choose who reviewed the report.");
        if (d.Approval is not (IncidentApproval.Approved or IncidentApproval.ApprovedWithConditions)) list.Add("Sign-off: the report needs administrative approval.");
        return list;
    }
}

/// <summary>
/// Incident reports. Every change needs a named author ("Working as") and is written to the incident's history;
/// a closed record is read-only until it is reopened with a reason. Student records are only read.
/// </summary>
public sealed class IncidentService(SchoolDbContext db, ICurrentUser currentUser, ISettingsService settings) : IIncidentService
{
    public const string NoAuthor = "Choose who you are working as (top right) before changing incident reports — every change is recorded under that name.";

    // ------------------------------------------------------------------ reading

    public async Task<IReadOnlyList<IncidentRow>> ListAsync(IncidentListQuery q, CancellationToken ct = default)
    {
        var query = db.Incidents.AsNoTracking();
        var term = TextNormalizer.CleanText(q.Search);
        if (term.Length > 0)
        {
            var key = TextNormalizer.NameKey(term);
            query = query.Where(i => i.ReferenceId.Contains(term) || i.Category.Contains(term) || i.ReportingDepartment.Contains(term)
                || (key.Length > 0 && i.Students.Any(s => s.Student.NormalizedName.Contains(key))));
        }
        if (!string.IsNullOrWhiteSpace(q.Category)) query = query.Where(i => i.Category == q.Category);
        if (!string.IsNullOrWhiteSpace(q.Department)) query = query.Where(i => i.ReportingDepartment == q.Department);
        if (q.Status is { } st) query = query.Where(i => i.Status == st);
        if (q.Severity is { } sv) query = query.Where(i => i.Severity == sv);
        if (q.From is { } f) query = query.Where(i => i.IncidentDate >= f);
        if (q.To is { } t) query = query.Where(i => i.IncidentDate <= t);
        if (q.Closure == "open") query = query.Where(i => !i.IsClosed);
        else if (q.Closure == "closed") query = query.Where(i => i.IsClosed);
        if (q.StudentId is { } sid) query = query.Where(i => i.Students.Any(s => s.StudentId == sid));

        var rows = await query.OrderByDescending(i => i.IncidentDate).ThenByDescending(i => i.Id).Take(500)
            .Select(i => new
            {
                i.Id, i.ReferenceId, i.Category, i.IncidentDate, i.ReportingDepartment, i.Severity, i.Status, i.IsClosed, i.UpdatedAt,
                Students = i.Students.OrderBy(s => s.SortOrder).Select(s => new { s.StudentId, s.Student.FullName, s.Role }).ToList(),
            }).ToListAsync(ct);
        return rows.Select(r => new IncidentRow(r.Id, r.ReferenceId, r.Category, r.IncidentDate, r.ReportingDepartment, r.Severity, r.Status, r.IsClosed,
            r.Students.Select(s => (s.StudentId, s.FullName, s.Role)).ToList(), r.UpdatedAt)).ToList();
    }

    public async Task<IncidentDetails?> GetAsync(int id, CancellationToken ct = default)
    {
        var i = await db.Incidents.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.ReferenceId, x.Category, x.IncidentDate, x.ReportingDepartment, x.Severity, x.Status,
            x.PreparedByStaffId, PreparedBy = x.PreparedBy != null ? x.PreparedBy.FullName : null, x.SignOffSchool, x.ReportDate,
            x.ReviewedByStaffId, ReviewedBy = x.ReviewedBy != null ? x.ReviewedBy.FullName : null, x.Approval, x.ApprovalNotes,
            x.IsClosed, x.ClosedAt, x.ClosedBy, x.CreatedBy, x.CreatedAt, x.UpdatedAt,
        }).FirstOrDefaultAsync(ct);
        if (i is null) return null;

        var students = await db.IncidentStudents.AsNoTracking().Where(s => s.IncidentId == id).OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => new IncidentStudentView(s.Id, s.StudentId, s.Student.FullName, s.SchoolClassId, s.SchoolClass.Name, s.Role, s.RoleOther, s.Involvement, s.SortOrder))
            .ToListAsync(ct);
        var names = students.ToDictionary(s => s.Id, s => s.Name);
        var entries = await db.IncidentTimeline.AsNoTracking().Where(t => t.IncidentId == id).OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.SortOrder, t.TimePeriod, t.Time, t.Location, t.OthersPresent, t.Account, Present = t.Present.Select(p => p.IncidentStudentId).ToList() })
            .ToListAsync(ct);
        var timeline = entries.Select(t => new TimelineView(t.Id, t.SortOrder, t.TimePeriod, t.Time, t.Location,
            t.Present.Where(names.ContainsKey).OrderBy(p => students.FindIndex(s => s.Id == p)).Select(p => (p, names[p])).ToList(), t.OthersPresent, t.Account)).ToList();
        var actions = await db.IncidentActions.AsNoTracking().Where(a => a.IncidentId == id).OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .Select(a => new ActionView(a.Id, a.SortOrder, a.ActionItem, a.Finding, a.Details, a.Status, a.UpdatedAt, a.UpdatedBy)).ToListAsync(ct);
        var history = await db.IncidentHistoryEntries.AsNoTracking().Where(h => h.IncidentId == id).OrderByDescending(h => h.At).ThenByDescending(h => h.Id)
            .Select(h => new IncidentHistoryView(h.Action, h.Details, h.At, h.By)).ToListAsync(ct);

        return new IncidentDetails(i.Id, i.ReferenceId, i.Category, i.IncidentDate, i.ReportingDepartment, i.Severity, i.Status,
            i.PreparedByStaffId, i.PreparedBy, i.SignOffSchool, i.ReportDate, i.ReviewedByStaffId, i.ReviewedBy, i.Approval, i.ApprovalNotes,
            i.IsClosed, i.ClosedAt, i.ClosedBy, i.CreatedBy, i.CreatedAt, i.UpdatedAt, students, timeline, actions, history);
    }

    public async Task<IncidentSuggestions> SuggestionsAsync(CancellationToken ct = default)
    {
        static IReadOnlyList<string> Merge(IEnumerable<string> a, IEnumerable<string> b) =>
            a.Concat(b).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var categories = await db.Incidents.Select(i => i.Category).Distinct().ToListAsync(ct);
        var departments = await db.Incidents.Select(i => i.ReportingDepartment).Distinct().ToListAsync(ct);
        var schoolDepartments = await db.Departments.OrderBy(d => d.SortOrder).Select(d => d.Name).ToListAsync(ct);
        var items = await db.IncidentActions.Select(a => a.ActionItem).Distinct().ToListAsync(ct);
        var periods = await db.IncidentTimeline.Select(t => t.TimePeriod).Distinct().ToListAsync(ct);
        var locations = await db.IncidentTimeline.Select(t => t.Location).Distinct().ToListAsync(ct);
        return new IncidentSuggestions(
            Merge(categories, ["Physical Aggression & Verbal Misconduct"]),
            Merge(departments.Concat(schoolDepartments), ["Discipline Department"]),
            Merge(items, ["Individual Accounts Inquiry", "Formal Verbal Warning", "Behavioral & Conflict Guidance", "Restorative Apologies Exchanged"]),
            Merge(periods, ["Morning Line", "Break Time", "Escalation"]),
            Merge(locations, ["Assembly & Classroom", "School Yard / Corridor", "Discipline Office"]));
    }

    // ------------------------------------------------------------------ A. header

    public async Task<ServiceResult> CreateAsync(IncidentHeaderInput input, CancellationToken ct = default)
    {
        if (currentUser.Name is null) return ServiceResult.Fail("", NoAuthor);
        var result = ValidateHeader(input);
        if (!result.Succeeded) return result;
        var profile = await settings.GetProfileAsync(ct);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var incident = new Incident
            {
                Category = Clean(input.Category)!, IncidentDate = input.Date!.Value, ReportingDepartment = Clean(input.Department)!,
                Severity = input.Severity!.Value, Status = input.Status!.Value, ReferenceId = await NextReferenceAsync(input.Date!.Value.Year, ct),
                SignOffSchool = profile.SchoolName, PreparedByStaffId = currentUser.StaffId, CreatedBy = currentUser.Name, UpdatedBy = currentUser.Name,
            };
            incident.History.Add(History("Report created",
                $"{incident.ReferenceId} · {incident.Category} · {incident.IncidentDate:yyyy-MM-dd} · {IncidentLabels.Severity(incident.Severity)} · {IncidentLabels.Status(incident.Status)}"));
            db.Incidents.Add(incident);
            try
            {
                await db.SaveChangesAsync(ct);
                return ServiceResult.Ok(incident.Id);
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear(); // another report took this reference a moment ago: take the next one
            }
        }
        return ServiceResult.Fail("", "A reference number could not be generated. Please try again.");
    }

    /// <summary>INC-{year}-{0001}: one more than the highest number used for that year.</summary>
    private async Task<string> NextReferenceAsync(int year, CancellationToken ct)
    {
        var prefix = $"INC-{year}-";
        var used = await db.Incidents.Where(i => i.ReferenceId.StartsWith(prefix)).Select(i => i.ReferenceId).ToListAsync(ct);
        var next = used.Select(r => int.TryParse(r[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        return $"{prefix}{next:0000}";
    }

    public async Task<ServiceResult> UpdateHeaderAsync(int id, IncidentHeaderInput input, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var result = ValidateHeader(input);
        if (!result.Succeeded) return result;

        var changes = new List<string>();
        void Track<T>(string label, T before, T after, Func<T, string> show)
        {
            if (!EqualityComparer<T>.Default.Equals(before, after)) changes.Add($"{label}: {show(before)} → {show(after)}");
        }
        Track("Category", incident.Category, Clean(input.Category)!, x => x);
        Track("Date", incident.IncidentDate, input.Date!.Value, x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Track("Department", incident.ReportingDepartment, Clean(input.Department)!, x => x);
        Track("Severity", incident.Severity, input.Severity!.Value, IncidentLabels.Severity);
        Track("Status", incident.Status, input.Status!.Value, IncidentLabels.Status);
        if (changes.Count == 0) return ServiceResult.Fail("", "Nothing was changed.");

        (incident.Category, incident.IncidentDate, incident.ReportingDepartment, incident.Severity, incident.Status) =
            (Clean(input.Category)!, input.Date!.Value, Clean(input.Department)!, input.Severity!.Value, input.Status!.Value);
        return await SaveAsync(incident, "Incident information updated", string.Join("; ", changes), ct);
    }

    private static ServiceResult ValidateHeader(IncidentHeaderInput i)
    {
        var r = ServiceResult.Ok();
        Required(r, "Category", i.Category, Incident.CategoryMaxLength, "Enter the incident category.");
        Required(r, "Department", i.Department, Incident.DepartmentMaxLength, "Enter the reporting department.");
        if (i.Date is null) r.AddError("Date", "Enter the date of the incident.");
        else if (i.Date > SchoolWeek.Today) r.AddError("Date", "The incident date cannot be in the future.");
        if (i.Severity is null || !Enum.IsDefined(i.Severity.Value)) r.AddError("Severity", "Choose the severity level.");
        if (i.Status is null || !Enum.IsDefined(i.Status.Value)) r.AddError("Status", "Choose the incident status.");
        return r;
    }

    // ------------------------------------------------------------------ B. students involved

    public async Task<ServiceResult> AddStudentAsync(int id, IncidentStudentInput input, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var result = ValidateStudent(input);
        if (!result.Succeeded) return result;
        var student = await db.Students.AsNoTracking().Where(s => s.Id == input.StudentId).Select(s => new { s.Id, s.FullName, s.SchoolClassId, ClassName = s.SchoolClass.Name }).FirstOrDefaultAsync(ct);
        if (student is null) return ServiceResult.Fail("StudentId", "Choose a student from the list.");
        if (await db.IncidentStudents.AnyAsync(s => s.IncidentId == id && s.StudentId == student.Id, ct))
            return ServiceResult.Fail("StudentId", $"{student.FullName} is already in this report. Edit their row instead.");

        var order = (await db.IncidentStudents.Where(s => s.IncidentId == id).MaxAsync(s => (int?)s.SortOrder, ct) ?? 0) + 1;
        db.IncidentStudents.Add(new IncidentStudent
        {
            IncidentId = id, StudentId = student.Id, SchoolClassId = student.SchoolClassId, Role = input.Role!.Value,
            RoleOther = input.Role == IncidentRole.Other ? Clean(input.RoleOther) : null, Involvement = Clean(input.Involvement)!, SortOrder = order,
        });
        return await SaveAsync(incident, "Student added", $"{student.FullName} ({student.ClassName}) · {IncidentLabels.Role(input.Role!.Value, Clean(input.RoleOther))}", ct);
    }

    public async Task<ServiceResult> UpdateStudentAsync(int id, int rowId, IncidentStudentInput input, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var row = await db.IncidentStudents.Include(s => s.Student).FirstOrDefaultAsync(s => s.Id == rowId && s.IncidentId == id, ct);
        if (row is null) return ServiceResult.Fail("", "This student is no longer in the report.");
        var result = ValidateStudent(input with { StudentId = row.StudentId });
        if (!result.Succeeded) return result;
        var before = $"{IncidentLabels.Role(row.Role, row.RoleOther)} · {Short(row.Involvement)}";
        (row.Role, row.RoleOther, row.Involvement) = (input.Role!.Value, input.Role == IncidentRole.Other ? Clean(input.RoleOther) : null, Clean(input.Involvement)!);
        var after = $"{IncidentLabels.Role(row.Role, row.RoleOther)} · {Short(row.Involvement)}";
        if (before == after && db.Entry(row).State == EntityState.Unchanged) return ServiceResult.Fail("", "Nothing was changed.");
        return await SaveAsync(incident, "Student involvement updated", $"{row.Student.FullName}: {before} → {after}", ct);
    }

    public async Task<ServiceResult> RemoveStudentAsync(int id, int rowId, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var row = await db.IncidentStudents.Include(s => s.Student).FirstOrDefaultAsync(s => s.Id == rowId && s.IncidentId == id, ct);
        if (row is null) return ServiceResult.Fail("", "This student is no longer in the report.");
        // Removed from the events they were listed in, then from the report (the student record itself is untouched).
        db.IncidentTimelineStudents.RemoveRange(await db.IncidentTimelineStudents.Where(p => p.IncidentStudentId == rowId).ToListAsync(ct));
        db.IncidentStudents.Remove(row);
        return await SaveAsync(incident, "Student removed", $"{row.Student.FullName} · {IncidentLabels.Role(row.Role, row.RoleOther)}", ct);
    }

    private static ServiceResult ValidateStudent(IncidentStudentInput i)
    {
        var r = ServiceResult.Ok();
        if (i.StudentId is null) r.AddError("StudentId", "Choose the student.");
        if (i.Role is null || !Enum.IsDefined(i.Role.Value)) r.AddError("Role", "Choose the student's role.");
        else if (i.Role == IncidentRole.Other) Required(r, "RoleOther", i.RoleOther, IncidentStudent.RoleOtherMaxLength, "Describe the role.");
        Required(r, "Involvement", i.Involvement, IncidentStudent.InvolvementMaxLength, "Describe this student's conduct and involvement.");
        return r;
    }

    // ------------------------------------------------------------------ C. timeline

    public async Task<ServiceResult> AddTimelineAsync(int id, TimelineInput input, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var (result, present) = await ValidateTimelineAsync(id, input, ct);
        if (!result.Succeeded) return result;
        var entries = await db.IncidentTimeline.Where(t => t.IncidentId == id).OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        var entry = new IncidentTimelineEntry
        {
            IncidentId = id, TimePeriod = Clean(input.TimePeriod)!, Time = input.Time, Location = Clean(input.Location)!,
            OthersPresent = Clean(input.OthersPresent), Account = Clean(input.Account)!,
        };
        foreach (var p in present) entry.Present.Add(new IncidentTimelineStudent { IncidentStudentId = p });
        db.IncidentTimeline.Add(entry);
        Place(entries, entry);
        return await SaveAsync(incident, "Timeline event added", $"{entry.TimePeriod} · {entry.Location}", ct);
    }

    public async Task<ServiceResult> UpdateTimelineAsync(int id, int entryId, TimelineInput input, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var entries = await db.IncidentTimeline.Include(t => t.Present).Where(t => t.IncidentId == id).OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        var entry = entries.FirstOrDefault(t => t.Id == entryId);
        if (entry is null) return ServiceResult.Fail("", "This timeline event no longer exists.");
        var (result, present) = await ValidateTimelineAsync(id, input, ct);
        if (!result.Succeeded) return result;

        var timeChanged = entry.Time != input.Time;
        (entry.TimePeriod, entry.Time, entry.Location, entry.OthersPresent, entry.Account) =
            (Clean(input.TimePeriod)!, input.Time, Clean(input.Location)!, Clean(input.OthersPresent), Clean(input.Account)!);
        foreach (var p in entry.Present.Where(p => !present.Contains(p.IncidentStudentId)).ToList()) entry.Present.Remove(p);
        foreach (var p in present.Where(p => entry.Present.All(x => x.IncidentStudentId != p))) entry.Present.Add(new IncidentTimelineStudent { IncidentStudentId = p });
        if (timeChanged && entry.Time is not null)
        {
            entries.Remove(entry);
            Place(entries, entry);
        }
        return await SaveAsync(incident, "Timeline event updated", $"{entry.TimePeriod} · {entry.Location}", ct);
    }

    public async Task<ServiceResult> DeleteTimelineAsync(int id, int entryId, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var entries = await db.IncidentTimeline.Where(t => t.IncidentId == id).OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        var entry = entries.FirstOrDefault(t => t.Id == entryId);
        if (entry is null) return ServiceResult.Fail("", "This timeline event no longer exists.");
        db.IncidentTimeline.Remove(entry);
        entries.Remove(entry);
        Renumber(entries);
        return await SaveAsync(incident, "Timeline event deleted", $"{entry.TimePeriod} · {entry.Location} · {Short(entry.Account)}", ct);
    }

    public async Task<ServiceResult> MoveTimelineAsync(int id, int entryId, int direction, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var entries = await db.IncidentTimeline.Where(t => t.IncidentId == id).OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        var index = entries.FindIndex(t => t.Id == entryId);
        if (index < 0) return ServiceResult.Fail("", "This timeline event no longer exists.");
        var target = index + Math.Sign(direction);
        if (direction == 0 || target < 0 || target >= entries.Count) return ServiceResult.Fail("", "This event cannot move further.");
        var other = entries[target];
        // Times are kept chronological: an event cannot be moved past one with a later / earlier time.
        // Moving up past an earlier-timed event (or down past a later one) would break the order.
        if (entries[index].Time is { } a && other.Time is { } b && (direction < 0 ? a > b : a < b))
            return ServiceResult.Fail("", $"The events are in time order ({b.ToString("HH:mm", CultureInfo.InvariantCulture)} and {a.ToString("HH:mm", CultureInfo.InvariantCulture)}). Change the time to move it.");
        (entries[index], entries[target]) = (other, entries[index]);
        Renumber(entries);
        return await SaveAsync(incident, "Timeline reordered", $"{entries[target].TimePeriod} moved {(direction < 0 ? "up" : "down")}", ct);
    }

    /// <summary>New / re-timed entries go before the first later-timed entry (events without a time keep their place).</summary>
    private static void Place(List<IncidentTimelineEntry> entries, IncidentTimelineEntry entry)
    {
        var at = entry.Time is { } time ? entries.FindIndex(e => e.Time is { } t && t > time) : -1;
        if (at < 0) entries.Add(entry); else entries.Insert(at, entry);
        Renumber(entries);
    }

    private static void Renumber(List<IncidentTimelineEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++) entries[i].SortOrder = i + 1;
    }

    private async Task<(ServiceResult, List<int>)> ValidateTimelineAsync(int id, TimelineInput i, CancellationToken ct)
    {
        var r = ServiceResult.Ok();
        Required(r, "TimePeriod", i.TimePeriod, IncidentTimelineEntry.PeriodMaxLength, "Enter the time or period (e.g. Break Time).");
        Required(r, "Location", i.Location, IncidentTimelineEntry.LocationMaxLength, "Enter where it happened.");
        Required(r, "Account", i.Account, IncidentTimelineEntry.AccountMaxLength, "Write the factual account of what happened.");
        if (Clean(i.OthersPresent)?.Length > IncidentTimelineEntry.OthersMaxLength) r.AddError("OthersPresent", $"Use {IncidentTimelineEntry.OthersMaxLength} characters or fewer.");
        var involved = await db.IncidentStudents.Where(s => s.IncidentId == id).Select(s => s.Id).ToListAsync(ct);
        var present = i.PresentIds.Distinct().ToList();
        if (present.Any(p => !involved.Contains(p))) r.AddError("PresentIds", "Choose students present from the students involved.");
        if (present.Count == 0 && string.IsNullOrWhiteSpace(i.OthersPresent)) r.AddError("PresentIds", "Tick the students present, or name who else was there.");
        return (r, present);
    }

    // ------------------------------------------------------------------ D. findings & actions

    public async Task<ServiceResult> AddActionAsync(int id, IncidentActionInput input, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var result = ValidateAction(input);
        if (!result.Succeeded) return result;
        var order = (await db.IncidentActions.Where(a => a.IncidentId == id).MaxAsync(a => (int?)a.SortOrder, ct) ?? 0) + 1;
        var action = new IncidentAction
        {
            IncidentId = id, SortOrder = order, ActionItem = Clean(input.ActionItem)!, Finding = Clean(input.Finding)!, Details = Clean(input.Details),
            Status = input.Status!.Value, UpdatedAt = DateTime.UtcNow, UpdatedBy = currentUser.Name,
        };
        db.IncidentActions.Add(action);
        return await SaveAsync(incident, "Action added", $"{action.ActionItem} · {IncidentLabels.ActionStatus(action.Status)}", ct);
    }

    public async Task<ServiceResult> UpdateActionAsync(int id, int actionId, IncidentActionInput input, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var action = await db.IncidentActions.FirstOrDefaultAsync(a => a.Id == actionId && a.IncidentId == id, ct);
        if (action is null) return ServiceResult.Fail("", "This action no longer exists.");
        var result = ValidateAction(input);
        if (!result.Succeeded) return result;
        var before = action.Status;
        (action.ActionItem, action.Finding, action.Details, action.Status, action.UpdatedAt, action.UpdatedBy) =
            (Clean(input.ActionItem)!, Clean(input.Finding)!, Clean(input.Details), input.Status!.Value, DateTime.UtcNow, currentUser.Name);
        if (db.Entry(action).Properties.Where(p => p.Metadata.Name is not (nameof(IncidentAction.UpdatedAt) or nameof(IncidentAction.UpdatedBy))).All(p => !p.IsModified))
            return ServiceResult.Fail("", "Nothing was changed.");
        return await SaveAsync(incident, "Action updated",
            action.ActionItem + (before == action.Status ? "" : $" · status {IncidentLabels.ActionStatus(before)} → {IncidentLabels.ActionStatus(action.Status)}"), ct);
    }

    public async Task<ServiceResult> SetActionStatusAsync(int id, int actionId, IncidentActionStatus status, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var action = await db.IncidentActions.FirstOrDefaultAsync(a => a.Id == actionId && a.IncidentId == id, ct);
        if (action is null) return ServiceResult.Fail("", "This action no longer exists.");
        if (!Enum.IsDefined(status)) return ServiceResult.Fail("Status", "Choose a status.");
        if (action.Status == status) return ServiceResult.Fail("", "The action already has this status.");
        var before = action.Status;
        (action.Status, action.UpdatedAt, action.UpdatedBy) = (status, DateTime.UtcNow, currentUser.Name);
        return await SaveAsync(incident, "Action status changed", $"{action.ActionItem}: {IncidentLabels.ActionStatus(before)} → {IncidentLabels.ActionStatus(status)}", ct);
    }

    public async Task<ServiceResult> DeleteActionAsync(int id, int actionId, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var action = await db.IncidentActions.FirstOrDefaultAsync(a => a.Id == actionId && a.IncidentId == id, ct);
        if (action is null) return ServiceResult.Fail("", "This action no longer exists.");
        db.IncidentActions.Remove(action);
        return await SaveAsync(incident, "Action deleted", $"{action.ActionItem} · {Short(action.Finding)}", ct);
    }

    private static ServiceResult ValidateAction(IncidentActionInput i)
    {
        var r = ServiceResult.Ok();
        Required(r, "ActionItem", i.ActionItem, IncidentAction.ItemMaxLength, "Enter the action item.");
        Required(r, "Finding", i.Finding, IncidentAction.TextMaxLength, "Describe the investigation finding and action.");
        if (Clean(i.Details)?.Length > IncidentAction.TextMaxLength) r.AddError("Details", $"Use {IncidentAction.TextMaxLength} characters or fewer.");
        if (i.Status is null || !Enum.IsDefined(i.Status.Value)) r.AddError("Status", "Choose the status.");
        return r;
    }

    // ------------------------------------------------------------------ E. sign-off & closure

    public async Task<ServiceResult> SaveSignOffAsync(int id, SignOffInput input, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var r = ServiceResult.Ok();
        if (input.PreparedById is { } p && !await db.StaffMembers.AnyAsync(s => s.Id == p, ct)) r.AddError("PreparedById", "Choose a staff member from the list.");
        if (input.ReviewedById is { } rv && !await db.StaffMembers.AnyAsync(s => s.Id == rv, ct)) r.AddError("ReviewedById", "Choose a staff member from the list.");
        if (Clean(input.School)?.Length > Incident.NameMaxLength) r.AddError("School", $"Use {Incident.NameMaxLength} characters or fewer.");
        if (input.ReportDate is { } d && d < incident.IncidentDate) r.AddError("ReportDate", "The report date cannot be before the incident.");
        if (input.Approval is null || !Enum.IsDefined(input.Approval.Value)) r.AddError("Approval", "Choose the approval status.");
        if (Clean(input.ApprovalNotes)?.Length > Incident.NotesMaxLength) r.AddError("ApprovalNotes", $"Use {Incident.NotesMaxLength} characters or fewer.");
        if (!r.Succeeded) return r;

        (incident.PreparedByStaffId, incident.SignOffSchool, incident.ReportDate, incident.ReviewedByStaffId, incident.Approval, incident.ApprovalNotes) =
            (input.PreparedById, Clean(input.School), input.ReportDate, input.ReviewedById, input.Approval!.Value, Clean(input.ApprovalNotes));
        if (db.Entry(incident).Properties.All(x => !x.IsModified)) return ServiceResult.Fail("", "Nothing was changed.");
        var names = await db.StaffMembers.Where(s => s.Id == input.PreparedById || s.Id == input.ReviewedById).ToDictionaryAsync(s => s.Id, s => s.FullName, ct);
        return await SaveAsync(incident, "Sign-off saved",
            $"Prepared by {Name(names, input.PreparedById)} · reviewed by {Name(names, input.ReviewedById)} · {IncidentLabels.Approval(incident.Approval)}" +
            (incident.ReportDate is { } rd ? $" · report date {rd:yyyy-MM-dd}" : ""), ct);
    }

    public async Task<ServiceResult> CloseAsync(int id, CancellationToken ct = default)
    {
        var (incident, error) = await EditableAsync(id, ct);
        if (incident is null) return error!;
        var details = (await GetAsync(id, ct))!;
        if (details.ClosureBlockers.Count > 0) return ServiceResult.Fail("", "The record cannot be closed yet: " + string.Join(" ", details.ClosureBlockers));
        (incident.IsClosed, incident.ClosedAt, incident.ClosedBy) = (true, DateTime.UtcNow, currentUser.Name);
        return await SaveAsync(incident, "Record closed", $"{IncidentLabels.Status(incident.Status)} · {IncidentLabels.Approval(incident.Approval)}", ct);
    }

    public async Task<ServiceResult> ReopenAsync(int id, string? reason, CancellationToken ct = default)
    {
        if (currentUser.Name is null) return ServiceResult.Fail("", NoAuthor);
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return ServiceResult.Fail("", "This incident report no longer exists.");
        if (!incident.IsClosed) return ServiceResult.Fail("", "The record is already open.");
        var why = Clean(reason);
        if (why is null) return ServiceResult.Fail("Reason", "Explain why the closed record is being reopened.");
        if (why.Length > IncidentHistory.DetailsMaxLength) return ServiceResult.Fail("Reason", $"Use {IncidentHistory.DetailsMaxLength} characters or fewer.");
        (incident.IsClosed, incident.ClosedAt, incident.ClosedBy) = (false, null, null);
        return await SaveAsync(incident, "Record reopened", why, ct);
    }

    public async Task LogExportAsync(int id, CancellationToken ct = default)
    {
        db.IncidentHistoryEntries.Add(new IncidentHistory { IncidentId = id, Action = "PDF exported", Details = "Official incident report downloaded.", At = DateTime.UtcNow, By = currentUser.Name ?? "Not identified" });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The tracked incident when it may be changed: it exists, it is open, and the author is known.</summary>
    private async Task<(Incident?, ServiceResult?)> EditableAsync(int id, CancellationToken ct)
    {
        if (currentUser.Name is null) return (null, ServiceResult.Fail("", NoAuthor));
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return (null, ServiceResult.Fail("", "This incident report no longer exists."));
        if (incident.IsClosed) return (null, ServiceResult.Fail("", "This record is closed. Reopen it (with a reason) to make changes."));
        return (incident, null);
    }

    private async Task<ServiceResult> SaveAsync(Incident incident, string action, string? details, CancellationToken ct)
    {
        incident.UpdatedBy = currentUser.Name;
        incident.UpdatedAt = DateTime.UtcNow;
        db.Entry(incident).Property(x => x.UpdatedAt).IsModified = true;
        db.IncidentHistoryEntries.Add(History(action, details, incident.Id));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This was just changed by someone else. Refresh the page.");
        }
        return ServiceResult.Ok(incident.Id);
    }

    private IncidentHistory History(string action, string? details, int incidentId = 0) => new()
    {
        IncidentId = incidentId, Action = action, At = DateTime.UtcNow, By = currentUser.Name,
        Details = details is null ? null : details.Length > IncidentHistory.DetailsMaxLength ? details[..IncidentHistory.DetailsMaxLength] : details,
    };

    private static void Required(ServiceResult r, string field, string? value, int max, string message)
    {
        var clean = Clean(value);
        if (clean is null) r.AddError(field, message);
        else if (clean.Length > max) r.AddError(field, $"Use {max} characters or fewer.");
    }

    /// <summary>Trimmed text (line breaks kept for long accounts); null when empty.</summary>
    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var lines = s.Replace("\r\n", "\n").Split('\n').Select(TextNormalizer.CleanText);
        var text = string.Join("\n", lines).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string Short(string s) => s.Length > 80 ? s[..80] + "…" : s;

    private static string Name(Dictionary<int, string> names, int? id) => id is { } i && names.TryGetValue(i, out var n) ? n : "—";
}
