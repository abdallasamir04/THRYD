using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.FloorManagement;

// ================================================================== access

public sealed record FloorRoleRef(int FloorId, FloorRole Role);

/// <summary>
/// What the person working in this request may do in Floor Management, from real relationships only (never names):
/// the Super Admin; the School Consultant (the current holder of the organizational position "School Consultant");
/// Floor Management administrators (staff given the Floor Audit "Manager" role); and the person's current floor roles.
/// Staff without any of these have no access.
/// </summary>
public sealed record FloorUser(int? StaffId, string? Name, bool IsSuperAdmin, bool IsConsultant, bool IsFloorAdmin, IReadOnlyList<FloorRoleRef> Roles)
{
    public bool IsAdmin => IsSuperAdmin || IsConsultant || IsFloorAdmin;
    public bool IsStageCoordinator => Roles.Any(r => r.Role == FloorRole.StageCoordinator);

    /// <summary>Administrators and every Stage Coordinator see all floors (the coordinators review across floors).</summary>
    public bool SeesAllFloors => IsAdmin || IsStageCoordinator;
    public bool HasAccess => SeesAllFloors || Roles.Count > 0;
    public bool Has(int floorId, FloorRole role) => Roles.Any(r => r.FloorId == floorId && r.Role == role);
    public bool IsOnFloor(int floorId) => Roles.Any(r => r.FloorId == floorId);
    public bool CanView(int floorId) => SeesAllFloors || IsOnFloor(floorId);

    /// <summary>Add floors, edit a floor and its team: administrators; a floor's own Stage Coordinator for its team.</summary>
    public bool CanAddFloor => IsAdmin;
    public bool CanManageTeam(int floorId) => IsAdmin || Has(floorId, FloorRole.StageCoordinator);

    /// <summary>Floor leads: follow up the floor's tickets.</summary>
    public bool LeadsFloor(int floorId) => IsAdmin || Has(floorId, FloorRole.StageCoordinator) || Has(floorId, FloorRole.FloorSupervisor);
    public bool CanSubmit(int floorId) => IsAdmin || IsOnFloor(floorId);
}

// ================================================================== read models

public sealed record FloorPerson(int AssignmentId, int StaffId, string Name, string? Phone, string? Phone2, bool IsManagement, FloorRole Role,
    string? Coverage, IReadOnlyList<string> Classes, DateOnly Since)
{
    public string ProfileHref => OrgLabels.ProfileHref(StaffId, IsManagement);

    /// <summary>The grades the person covers, written as the source does: "G7A – G7B – G8A – G8B" (classes) or the coverage.</summary>
    public string Scope => Classes.Count > 0 ? string.Join(" – ", Classes) : Coverage ?? "";
}

/// <summary>A floor role the source names whose person is not confirmed: shown as pending, never assigned automatically.</summary>
public sealed record FloorPendingEntry(FloorRole Role, string Name, string Phone, string? Coverage, string Reason);

/// <summary>One assignment of the floor's team, current or ended (the floor's history).</summary>
public sealed record FloorHistoryRow(int AssignmentId, int StaffId, string Name, FloorRole Role, string Scope, bool IsCurrent, DateOnly Start, DateOnly? End,
    string? EndReason, string? AssignedBy);

/// <summary>An assignment being edited (grade range / classes only; the person and role are changed by a new assignment).</summary>
public sealed record FloorAssignmentEdit(int Id, int FloorId, string FloorName, string StaffName, FloorRole Role, string? Coverage, IReadOnlyList<int> ClassIds);

public sealed record FloorTeamCard(int Id, string Name, string? GradeRange, bool IsActive, int SortOrder, IReadOnlyList<FloorPerson> Team, int OpenReports, int OpenTickets)
{
    public FloorPerson? Lead(FloorRole r) => Team.FirstOrDefault(p => p.Role == r);
    public IReadOnlyList<FloorPerson> Of(FloorRole r) => Team.Where(p => p.Role == r).ToList();

    /// <summary>Roles the source names with an unconfirmed person (e.g. a Stage Coordinator awaiting identity confirmation).</summary>
    public IReadOnlyList<FloorPendingEntry> Pending { get; init; } = [];
    public FloorPendingEntry? PendingFor(FloorRole r) => Pending.FirstOrDefault(p => p.Role == r);
}

public sealed record ConsultantRef(int StaffId, string Name, bool IsManagement);

public sealed record FloorReportRow(int Id, int FloorId, string Floor, string Title, FloorReportCategory Category, FloorReportStatus Status,
    FloorReportRecipient Recipient, string? RecipientName, string? RelatedName, string? CreatedBy, DateTime OccurredAt, DateTime UpdatedAt);

public sealed record CaseEvent(FloorCaseAction Action, string Status, string? Detail, string? Note, string? By, DateTime At);

public sealed record FloorReportDetail(FloorReportRow Row, string Description, int? RelatedStaffId, int? RecipientStaffId, int? CreatedByStaffId,
    IReadOnlyList<CaseEvent> Events);

public sealed record FloorTicketRow(int Id, string Number, int FloorId, string Floor, int StudentId, string Student, string ClassName,
    FloorTicketCategory Category, TaskPriority Priority, FloorTicketStatus Status, int? SocialWorkerId, string? SocialWorker, string? CreatedBy, DateTime CreatedAt);

public sealed record FloorTicketDetail(FloorTicketRow Row, string Description, string? ResolutionNotes, IReadOnlyList<CaseEvent> Events);

public sealed record Option(int Id, string Name, string? Note = null);

public sealed class FloorInput
{
    public string Name { get; set; } = "";
    public string? GradeRange { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class FloorAssignInput
{
    public int StaffMemberId { get; set; }
    public FloorRole Role { get; set; }
    public string? Coverage { get; set; }
    public List<int> ClassIds { get; set; } = [];
}

public sealed class FloorReportInput
{
    public int FloorId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public FloorReportCategory Category { get; set; } = FloorReportCategory.DailyOperations;
    public int? RelatedStaffId { get; set; }
    public DateTime? OccurredAt { get; set; }
    public FloorReportRecipient Recipient { get; set; } = FloorReportRecipient.FloorSupervisor;
}

public sealed class FloorTicketInput
{
    public int FloorId { get; set; }
    public int StudentId { get; set; }
    public FloorTicketCategory Category { get; set; } = FloorTicketCategory.Discipline;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public string Description { get; set; } = "";
}

public interface IFloorManagementService
{
    Task<FloorUser> CurrentAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FloorTeamCard>> FloorsAsync(bool managedOnly, CancellationToken ct = default);
    Task<FloorTeamCard?> FloorAsync(int id, CancellationToken ct = default);
    Task<ConsultantRef?> ConsultantAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Option>> StaffOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Option>> ClassOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Option>> StudentOptionsAsync(CancellationToken ct = default);

    Task<ServiceResult> SaveFloorAsync(int? id, FloorInput input, CancellationToken ct = default);
    Task<ServiceResult> AssignAsync(int floorId, FloorAssignInput input, CancellationToken ct = default);
    Task<ServiceResult> EndAssignmentAsync(int assignmentId, string? reason, CancellationToken ct = default);

    /// <summary>The floor's team assignments, current and ended, newest first.</summary>
    Task<IReadOnlyList<FloorHistoryRow>> HistoryAsync(int floorId, CancellationToken ct = default);
    Task<FloorAssignmentEdit?> AssignmentAsync(int assignmentId, CancellationToken ct = default);

    /// <summary>Changes a current assignment's grade range / classes (who and which role stay the same).</summary>
    Task<ServiceResult> UpdateAssignmentAsync(int assignmentId, string? coverage, IReadOnlyList<int> classIds, CancellationToken ct = default);

    /// <summary>Whom this person may address a report to on the floor (their reporting line, and the escalation route).</summary>
    Task<IReadOnlyList<FloorReportRecipient>> RecipientsAsync(int floorId, CancellationToken ct = default);
    Task<IReadOnlyList<FloorReportRow>> ReportsAsync(CancellationToken ct = default);
    Task<FloorReportDetail?> ReportAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> SubmitReportAsync(FloorReportInput input, CancellationToken ct = default);
    Task<ServiceResult> ReviewReportAsync(int id, FloorReportStatus status, string? note, CancellationToken ct = default);
    Task<ServiceResult> EscalateReportAsync(int id, string? note, CancellationToken ct = default);
    Task<bool> CanReviewAsync(FloorReportDetail report, CancellationToken ct = default);

    Task<IReadOnlyList<FloorTicketRow>> TicketsAsync(CancellationToken ct = default);
    Task<FloorTicketDetail?> TicketAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> OpenTicketAsync(FloorTicketInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateTicketAsync(int id, FloorTicketStatus status, int? socialWorkerId, string? resolution, string? note, CancellationToken ct = default);
    Task<bool> CanFollowUpAsync(FloorTicketRow ticket, CancellationToken ct = default);
}

/// <summary>
/// Floor Management: floors and their teams, reports along the reporting lines (Matron / Floating Matron → Floor Supervisor →
/// Stage Coordinator → School Consultant; a report is never addressed to the person it concerns, so a Floating Matron
/// escalates an issue about the Floor Supervisor straight to the Stage Coordinator), and Social Worker tickets. Every
/// permission is checked here, server side; nothing is resolved automatically; every change is in the history.
/// </summary>
public sealed class FloorManagementService(SchoolDbContext db, ICurrentUser currentUser) : IFloorManagementService
{
    public const string ConsultantPositionKey = "pos-consultant";
    private FloorUser? _user;

    // ================================================================== access

    public async Task<FloorUser> CurrentAsync(CancellationToken ct = default)
    {
        if (_user is not null) return _user;
        var staffId = currentUser.StaffId;
        var roles = staffId is { } id
            ? (await db.FloorStaffAssignments.AsNoTracking().Where(a => a.StaffMemberId == id && a.IsCurrent && a.Floor.IsActive)
                .Select(a => new { a.FloorId, a.Role }).ToListAsync(ct)).Select(r => new FloorRoleRef(r.FloorId, r.Role)).ToList()
            : [];
        var consultant = staffId is { } c && await db.StaffPositionAssignments.AsNoTracking()
            .AnyAsync(a => a.StaffMemberId == c && a.IsCurrent && a.Position.SourceKey == ConsultantPositionKey, ct);
        var admin = staffId is { } m && await db.FloorAuditAccess.AsNoTracking()
            .AnyAsync(a => a.StaffMemberId == m && a.EndedAt == null && a.Role == FloorAuditRole.Manager, ct);
        return _user = new FloorUser(staffId, currentUser.AccountName(), currentUser.IsSuperAdmin, consultant, admin, roles);
    }

    private async Task<ServiceResult?> DenyUnlessAsync(Func<FloorUser, bool> allowed, CancellationToken ct) =>
        allowed(await CurrentAsync(ct)) ? null : ServiceResult.Fail("", "You do not have permission for this in Floor Management.");

    // ================================================================== floors and teams

    public async Task<IReadOnlyList<FloorTeamCard>> FloorsAsync(bool managedOnly, CancellationToken ct = default)
    {
        var floors = await db.Floors.AsNoTracking().Where(f => !managedOnly || f.GradeRange != null).OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(ct);
        var ids = floors.Select(f => f.Id).ToList();
        var team = (await TeamAsync(ids, ct)).ToLookup(p => p.Floor, p => p.Person);
        var reports = await db.FloorReports.AsNoTracking().Where(r => ids.Contains(r.FloorId) && r.Status != FloorReportStatus.Resolved && r.Status != FloorReportStatus.Closed)
            .GroupBy(r => r.FloorId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var tickets = await db.FloorTickets.AsNoTracking().Where(t => ids.Contains(t.FloorId) && t.Status != FloorTicketStatus.Resolved && t.Status != FloorTicketStatus.Closed)
            .GroupBy(t => t.FloorId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return floors.Select(f =>
        {
            var members = team[f.Id].ToList();
            // Source roles whose person is not confirmed, while nobody holds that role on the floor.
            var pending = FloorManagementSource.Floors.Where(s => TextNormalizer.NameKey(s.Name) == f.NormalizedName)
                .SelectMany(s => s.Team).Where(p => p.Unconfirmed is not null && members.All(m => m.Role != p.Role))
                .Select(p => new FloorPendingEntry(p.Role, p.Name, p.Phone, p.Coverage, p.Unconfirmed!)).ToList();
            return new FloorTeamCard(f.Id, f.Name, f.GradeRange, f.IsActive, f.SortOrder, members, reports.GetValueOrDefault(f.Id), tickets.GetValueOrDefault(f.Id))
            {
                Pending = pending,
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<FloorHistoryRow>> HistoryAsync(int floorId, CancellationToken ct = default)
    {
        if (!(await CurrentAsync(ct)).CanView(floorId)) return [];
        var rows = await db.FloorStaffAssignments.AsNoTracking().Where(a => a.FloorId == floorId)
            .OrderByDescending(a => a.IsCurrent).ThenByDescending(a => a.EndDate).ThenByDescending(a => a.StartDate).ThenBy(a => a.Role)
            .Select(a => new
            {
                a.Id, a.StaffMemberId, a.StaffMember.FullName, a.Role, a.Coverage, a.IsCurrent, a.StartDate, a.EndDate, a.EndReason, a.AssignedBy,
                Classes = a.Classes.OrderBy(c => c.SchoolClass.SortOrder).Select(c => c.SchoolClass.Name).ToList(),
            }).ToListAsync(ct);
        return rows.Select(r => new FloorHistoryRow(r.Id, r.StaffMemberId, r.FullName, r.Role, r.Classes.Count > 0 ? string.Join(" – ", r.Classes) : r.Coverage ?? "",
            r.IsCurrent, r.StartDate, r.EndDate, r.EndReason, r.AssignedBy)).ToList();
    }

    public async Task<FloorAssignmentEdit?> AssignmentAsync(int assignmentId, CancellationToken ct = default)
    {
        var a = await db.FloorStaffAssignments.AsNoTracking().Where(x => x.Id == assignmentId && x.IsCurrent)
            .Select(x => new { x.Id, x.FloorId, Floor = x.Floor.Name, x.StaffMember.FullName, x.Role, x.Coverage, Classes = x.Classes.Select(c => c.SchoolClassId).ToList() })
            .FirstOrDefaultAsync(ct);
        return a is null ? null : new FloorAssignmentEdit(a.Id, a.FloorId, a.Floor, a.FullName, a.Role, a.Coverage, a.Classes);
    }

    public async Task<ServiceResult> UpdateAssignmentAsync(int assignmentId, string? coverage, IReadOnlyList<int> classIds, CancellationToken ct = default)
    {
        var row = await db.FloorStaffAssignments.Include(a => a.Classes).FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (row is null || !row.IsCurrent) return ServiceResult.Fail("", "This assignment has ended.");
        if (await DenyUnlessAsync(u => u.CanManageTeam(row.FloorId), ct) is { } denied) return denied;
        var text = TextNormalizer.CleanText(coverage);
        if (text.Length > FloorStaffAssignment.CoverageMaxLength) return ServiceResult.Fail("Coverage", $"Use {FloorStaffAssignment.CoverageMaxLength} characters or fewer.");
        var ids = classIds.Distinct().ToList();
        if (ids.Count > 0 && await db.Classes.CountAsync(c => ids.Contains(c.Id), ct) != ids.Count) return ServiceResult.Fail("ClassIds", "Choose classes from the list.");
        if (row.Role == FloorRole.Matron && ids.Count == 0) return ServiceResult.Fail("ClassIds", "Choose the grades / classes the Matron is assigned to.");
        row.Coverage = text.Length == 0 ? null : text;
        row.Classes.Clear();
        foreach (var c in ids) row.Classes.Add(new FloorStaffClass { SchoolClassId = c });
        row.Note = $"Grade range updated by {currentUser.AccountName() ?? "an administrator"} on {SchoolWeek.Today:yyyy-MM-dd}.";
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(row.Id);
    }

    public async Task<FloorTeamCard?> FloorAsync(int id, CancellationToken ct = default)
    {
        var all = await FloorsAsync(false, ct);
        return all.FirstOrDefault(f => f.Id == id);
    }

    private async Task<List<(int Floor, FloorPerson Person)>> TeamAsync(List<int> floorIds, CancellationToken ct)
    {
        var rows = await db.FloorStaffAssignments.AsNoTracking().Where(a => a.IsCurrent && floorIds.Contains(a.FloorId))
            .Select(a => new
            {
                a.Id, a.FloorId, a.StaffMemberId, a.StaffMember.FullName, a.StaffMember.ContactNumber, a.StaffMember.SecondaryContactNumber,
                IsManagement = a.StaffMember is ManagementMember, a.Role, a.Coverage, a.StartDate,
                Classes = a.Classes.OrderBy(c => c.SchoolClass.SortOrder).Select(c => c.SchoolClass.Name).ToList(),
            }).ToListAsync(ct);
        return rows.OrderBy(r => r.Role).ThenBy(r => r.FullName)
            .Select(r => (r.FloorId, new FloorPerson(r.Id, r.StaffMemberId, r.FullName, r.ContactNumber, r.SecondaryContactNumber, r.IsManagement, r.Role,
                r.Coverage, r.Classes, r.StartDate))).ToList();
    }

    public async Task<ConsultantRef?> ConsultantAsync(CancellationToken ct = default) =>
        await db.StaffPositionAssignments.AsNoTracking().Where(a => a.IsCurrent && a.Position.SourceKey == ConsultantPositionKey)
            .OrderByDescending(a => a.IsPrimary).Select(a => new ConsultantRef(a.StaffMemberId, a.StaffMember.FullName, a.StaffMember is ManagementMember))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Option>> StaffOptionsAsync(CancellationToken ct = default) =>
        (await db.StaffMembers.AsNoTracking().Where(s => s.ArchivedAt == null).OrderBy(s => s.FullName).Select(s => new { s.Id, s.FullName, s.ContactNumber, s.JobTitle }).ToListAsync(ct))
        .Select(s => new Option(s.Id, s.FullName, string.Join(" · ", new[] { s.JobTitle, s.ContactNumber }.Where(x => !string.IsNullOrWhiteSpace(x))))).ToList();

    public async Task<IReadOnlyList<Option>> ClassOptionsAsync(CancellationToken ct = default) =>
        (await db.Classes.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
        .Select(c => new Option(c.Id, c.Name)).ToList();

    public async Task<IReadOnlyList<Option>> StudentOptionsAsync(CancellationToken ct = default) =>
        (await db.Students.AsNoTracking().OrderBy(s => s.SchoolClass.SortOrder).ThenBy(s => s.FullName).Select(s => new { s.Id, s.FullName, Class = s.SchoolClass.Name }).ToListAsync(ct))
        .Select(s => new Option(s.Id, s.FullName, s.Class)).ToList();

    public async Task<ServiceResult> SaveFloorAsync(int? id, FloorInput input, CancellationToken ct = default)
    {
        if (await DenyUnlessAsync(u => u.CanAddFloor, ct) is { } denied) return denied;
        var name = TextNormalizer.CleanText(input.Name);
        var range = TextNormalizer.CleanText(input.GradeRange);
        if (name.Length == 0) return ServiceResult.Fail("Name", "Enter the floor name.");
        if (name.Length > SchoolFloor.NameMaxLength) return ServiceResult.Fail("Name", $"Use {SchoolFloor.NameMaxLength} characters or fewer.");
        if (range.Length == 0) return ServiceResult.Fail("GradeRange", "Enter the grades the floor's team is responsible for (e.g. PreK – G1B).");
        if (range.Length > FloorStaffAssignment.CoverageMaxLength) return ServiceResult.Fail("GradeRange", $"Use {FloorStaffAssignment.CoverageMaxLength} characters or fewer.");
        if (input.SortOrder is < 0 or > 99) return ServiceResult.Fail("SortOrder", "Use an order between 0 and 99.");
        var key = TextNormalizer.NameKey(name);
        if (await db.Floors.AnyAsync(f => f.NormalizedName == key && f.Id != id, ct)) return ServiceResult.Fail("Name", $"{name} already exists.");
        var floor = id is null ? new SchoolFloor { CreatedBy = currentUser.AccountName() } : await db.Floors.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (floor is null) return ServiceResult.Fail("", "This floor no longer exists.");
        (floor.Name, floor.NormalizedName, floor.GradeRange, floor.SortOrder, floor.IsActive, floor.UpdatedBy) = (name, key, range, input.SortOrder, input.IsActive, currentUser.AccountName());
        if (id is null) db.Floors.Add(floor);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(floor.Id);
    }

    public async Task<ServiceResult> AssignAsync(int floorId, FloorAssignInput input, CancellationToken ct = default)
    {
        if (await DenyUnlessAsync(u => u.CanManageTeam(floorId), ct) is { } denied) return denied;
        if (!await db.Floors.AnyAsync(f => f.Id == floorId, ct)) return ServiceResult.Fail("", "This floor no longer exists.");
        if (!Enum.IsDefined(input.Role)) return ServiceResult.Fail("Role", "Choose a role.");
        var name = await db.StaffMembers.Where(s => s.Id == input.StaffMemberId && s.ArchivedAt == null).Select(s => s.FullName).FirstOrDefaultAsync(ct);
        if (name is null) return ServiceResult.Fail("StaffMemberId", "Choose a staff member from the list.");
        var coverage = TextNormalizer.CleanText(input.Coverage);
        if (coverage.Length > FloorStaffAssignment.CoverageMaxLength) return ServiceResult.Fail("Coverage", $"Use {FloorStaffAssignment.CoverageMaxLength} characters or fewer.");
        var classIds = input.ClassIds.Distinct().ToList();
        if (classIds.Count > 0 && await db.Classes.CountAsync(c => classIds.Contains(c.Id), ct) != classIds.Count)
            return ServiceResult.Fail("ClassIds", "Choose classes from the list.");
        if (input.Role == FloorRole.Matron && classIds.Count == 0) return ServiceResult.Fail("ClassIds", "Choose the grades / classes the Matron is assigned to.");
        if (await db.FloorStaffAssignments.AnyAsync(a => a.FloorId == floorId && a.StaffMemberId == input.StaffMemberId && a.Role == input.Role && a.IsCurrent, ct))
            return ServiceResult.Fail("StaffMemberId", $"{name} already has this role on the floor.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One Stage Coordinator, one Floor Supervisor and one Floating Matron per floor: the previous holder's assignment ends (kept as history).
        if (input.Role is FloorRole.StageCoordinator or FloorRole.FloorSupervisor or FloorRole.FloatingMatron)
        {
            foreach (var old in await db.FloorStaffAssignments.Where(a => a.FloorId == floorId && a.Role == input.Role && a.IsCurrent).ToListAsync(ct))
                End(old, $"Replaced by {name}");
            await db.SaveChangesAsync(ct);
        }
        var row = new FloorStaffAssignment
        {
            FloorId = floorId, StaffMemberId = input.StaffMemberId, Role = input.Role, Coverage = coverage.Length == 0 ? null : coverage,
            StartDate = SchoolWeek.Today, AssignedBy = currentUser.AccountName(),
        };
        foreach (var c in classIds) row.Classes.Add(new FloorStaffClass { SchoolClassId = c });
        db.FloorStaffAssignments.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(row.Id);
    }

    public async Task<ServiceResult> EndAssignmentAsync(int assignmentId, string? reason, CancellationToken ct = default)
    {
        var row = await db.FloorStaffAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (row is null || !row.IsCurrent) return ServiceResult.Fail("", "This assignment has already ended.");
        if (await DenyUnlessAsync(u => u.CanManageTeam(row.FloorId), ct) is { } denied) return denied;
        End(row, TextNormalizer.CleanText(reason) is { Length: > 0 } r ? r : "Assignment ended");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(row.Id);
    }

    private void End(FloorStaffAssignment row, string reason)
    {
        row.IsCurrent = false;
        row.EndDate = SchoolWeek.Today < row.StartDate ? row.StartDate : SchoolWeek.Today;
        row.EndReason = reason.Length > FloorStaffAssignment.NoteMaxLength ? reason[..FloorStaffAssignment.NoteMaxLength] : reason;
    }

    // ================================================================== reports and escalations

    public async Task<IReadOnlyList<FloorReportRecipient>> RecipientsAsync(int floorId, CancellationToken ct = default)
    {
        var u = await CurrentAsync(ct);
        if (u.IsAdmin) return Enum.GetValues<FloorReportRecipient>();
        var set = new List<FloorReportRecipient>();
        if (u.Has(floorId, FloorRole.Matron)) set.Add(FloorReportRecipient.FloorSupervisor);
        // The Floating Matron reports to the Floor Supervisor, and escalates straight to the Stage Coordinator when needed.
        if (u.Has(floorId, FloorRole.FloatingMatron)) set.AddRange([FloorReportRecipient.FloorSupervisor, FloorReportRecipient.StageCoordinator]);
        if (u.Has(floorId, FloorRole.SocialWorker)) set.AddRange([FloorReportRecipient.FloorSupervisor, FloorReportRecipient.StageCoordinator]);
        if (u.Has(floorId, FloorRole.FloorSupervisor)) set.Add(FloorReportRecipient.StageCoordinator);
        if (u.Has(floorId, FloorRole.StageCoordinator)) set.Add(FloorReportRecipient.SchoolConsultant);
        return set.Distinct().OrderBy(r => r).ToList();
    }

    /// <summary>The person who currently holds the recipient role for the floor.</summary>
    private async Task<int?> HolderAsync(int floorId, FloorReportRecipient recipient, CancellationToken ct) => recipient switch
    {
        FloorReportRecipient.FloorSupervisor => await db.FloorStaffAssignments.Where(a => a.FloorId == floorId && a.Role == FloorRole.FloorSupervisor && a.IsCurrent).Select(a => (int?)a.StaffMemberId).FirstOrDefaultAsync(ct),
        FloorReportRecipient.StageCoordinator => await db.FloorStaffAssignments.Where(a => a.FloorId == floorId && a.Role == FloorRole.StageCoordinator && a.IsCurrent).Select(a => (int?)a.StaffMemberId).FirstOrDefaultAsync(ct),
        _ => (await ConsultantAsync(ct))?.StaffId,
    };

    public async Task<ServiceResult> SubmitReportAsync(FloorReportInput input, CancellationToken ct = default)
    {
        var u = await CurrentAsync(ct);
        if (!u.CanSubmit(input.FloorId)) return ServiceResult.Fail("", "Only the floor's team (and administrators) can submit reports for this floor.");
        if (u.StaffId is null && !u.IsSuperAdmin) return ServiceResult.Fail("", "Choose who you are under “Working as” to submit a report.");
        var title = TextNormalizer.CleanText(input.Title);
        var text = (input.Description ?? "").Trim();
        if (title.Length == 0) return ServiceResult.Fail("Title", "Enter a title.");
        if (title.Length > FloorReport.TitleMaxLength) return ServiceResult.Fail("Title", $"Use {FloorReport.TitleMaxLength} characters or fewer.");
        if (text.Length == 0) return ServiceResult.Fail("Description", "Describe what happened.");
        if (text.Length > FloorReport.TextMaxLength) return ServiceResult.Fail("Description", $"Use {FloorReport.TextMaxLength} characters or fewer.");
        if (!Enum.IsDefined(input.Category)) return ServiceResult.Fail("Category", "Choose a category.");
        if (!(await RecipientsAsync(input.FloorId, ct)).Contains(input.Recipient))
            return ServiceResult.Fail("Recipient", "Choose someone on your reporting line.");
        if (input.RelatedStaffId is { } rel && !await db.StaffMembers.AnyAsync(s => s.Id == rel, ct)) return ServiceResult.Fail("RelatedStaffId", "Choose the employee from the list.");
        if (input.RelatedStaffId == u.StaffId && u.StaffId is not null) return ServiceResult.Fail("RelatedStaffId", "A report cannot concern its own author.");
        var occurred = input.OccurredAt ?? SchoolTime.Now;
        if (occurred > SchoolTime.Now.AddMinutes(5)) return ServiceResult.Fail("OccurredAt", "The date and time cannot be in the future.");

        var holder = await HolderAsync(input.FloorId, input.Recipient, ct);
        if (holder is not null && holder == input.RelatedStaffId)
            return ServiceResult.Fail("Recipient", "This report concerns the person it would be sent to. Send it to the next level (the Stage Coordinator) instead.");

        var report = new FloorReport
        {
            FloorId = input.FloorId, Title = title, Description = text, Category = input.Category, RelatedStaffId = input.RelatedStaffId,
            OccurredAt = occurred, Recipient = input.Recipient, RecipientStaffId = holder, CreatedByStaffId = u.StaffId, CreatedBy = u.Name,
        };
        report.Events.Add(Event(report, FloorCaseAction.Created, holder is null ? $"Sent to the {Label(input.Recipient)} (no current holder)." : null, u));
        db.FloorReports.Add(report);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(report.Id);
    }

    public async Task<IReadOnlyList<FloorReportRow>> ReportsAsync(CancellationToken ct = default)
    {
        var u = await CurrentAsync(ct);
        if (!u.HasAccess) return [];
        var floors = u.Roles.Select(r => r.FloorId).Distinct().ToList();
        var me = u.StaffId ?? -1;
        var q = db.FloorReports.AsNoTracking().AsQueryable();
        if (!u.SeesAllFloors)
            q = q.Where(r => r.CreatedByStaffId == me || r.RecipientStaffId == me || r.Events.Any(e => e.RecipientStaffIdAfter == me)
                             || (floors.Contains(r.FloorId) && db.FloorStaffAssignments.Any(a => a.FloorId == r.FloorId && a.StaffMemberId == me && a.IsCurrent && a.Role == FloorRole.FloorSupervisor)));
        // A report about someone is not shown to that person (unless it was addressed to them, which the rules prevent).
        q = q.Where(r => r.RelatedStaffId != me || r.CreatedByStaffId == me);
        // Sort on the entity's own column, then project (ordering by a member of the projected record cannot be translated).
        return await Rows(q.OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Id)).ToListAsync(ct);
    }

    private static IQueryable<FloorReportRow> Rows(IQueryable<FloorReport> q) =>
        q.Select(r => new FloorReportRow(r.Id, r.FloorId, r.Floor.Name, r.Title, r.Category, r.Status, r.Recipient,
            r.RecipientStaff != null ? r.RecipientStaff.FullName : null, r.RelatedStaff != null ? r.RelatedStaff.FullName : null, r.CreatedBy, r.OccurredAt, r.UpdatedAt));

    public async Task<FloorReportDetail?> ReportAsync(int id, CancellationToken ct = default)
    {
        var row = (await ReportsAsync(ct)).FirstOrDefault(r => r.Id == id);
        if (row is null) return null;
        var r = await db.FloorReports.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Description, x.RelatedStaffId, x.RecipientStaffId, x.CreatedByStaffId }).FirstAsync(ct);
        var names = await db.StaffMembers.AsNoTracking().Select(s => new { s.Id, s.FullName }).ToDictionaryAsync(s => s.Id, s => s.FullName, ct);
        var events = (await db.FloorReportEvents.AsNoTracking().Where(e => e.FloorReportId == id).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(ct))
            .Select(e => new CaseEvent(e.Action, Label(e.StatusAfter),
                e.RecipientAfter is { } to ? $"To {Label(to)}{(e.RecipientStaffIdAfter is { } p && names.TryGetValue(p, out var n) ? $": {n}" : "")}" : null,
                e.Note, e.By, e.At)).ToList();
        return new FloorReportDetail(row, r.Description, r.RelatedStaffId, r.RecipientStaffId, r.CreatedByStaffId, events);
    }

    /// <summary>The current recipient, anyone above them on the floor's line, and administrators — never the person concerned.</summary>
    public async Task<bool> CanReviewAsync(FloorReportDetail report, CancellationToken ct = default)
    {
        var u = await CurrentAsync(ct);
        if (u.StaffId is { } me && me == report.RelatedStaffId) return false;
        if (u.IsAdmin) return true;
        if (u.StaffId is { } id && id == report.RecipientStaffId) return true;
        return report.Row.Recipient == FloorReportRecipient.FloorSupervisor && u.Has(report.Row.FloorId, FloorRole.StageCoordinator);
    }

    public async Task<ServiceResult> ReviewReportAsync(int id, FloorReportStatus status, string? note, CancellationToken ct = default)
    {
        var detail = await ReportAsync(id, ct);
        if (detail is null) return ServiceResult.Fail("", "This report does not exist or you cannot open it.");
        if (!await CanReviewAsync(detail, ct)) return ServiceResult.Fail("", "Only the report's current reviewer (or someone above them) can update it.");
        if (status is not (FloorReportStatus.UnderReview or FloorReportStatus.Resolved or FloorReportStatus.Closed))
            return ServiceResult.Fail("Status", "Choose Under review, Resolved or Closed.");
        var text = TextNormalizer.CleanText(note);
        if (status != FloorReportStatus.UnderReview && text.Length == 0) return ServiceResult.Fail("Note", "Add a note saying how the report was resolved or why it is closed.");
        if (text.Length > FloorReportEvent.NoteMaxLength) return ServiceResult.Fail("Note", $"Use {FloorReportEvent.NoteMaxLength} characters or fewer.");
        var report = await db.FloorReports.FirstAsync(r => r.Id == id, ct);
        report.Status = status;
        db.FloorReportEvents.Add(Event(report, FloorCaseAction.StatusChanged, text.Length == 0 ? null : text, await CurrentAsync(ct)));
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    /// <summary>Passes the report to the next level: Floor Supervisor → Stage Coordinator → School Consultant (never to the person concerned).</summary>
    public async Task<ServiceResult> EscalateReportAsync(int id, string? note, CancellationToken ct = default)
    {
        var detail = await ReportAsync(id, ct);
        if (detail is null) return ServiceResult.Fail("", "This report does not exist or you cannot open it.");
        var u = await CurrentAsync(ct);
        var author = u.StaffId is { } me && me == detail.CreatedByStaffId;
        if (!author && !await CanReviewAsync(detail, ct)) return ServiceResult.Fail("", "Only the report's author or its reviewer can escalate it.");
        if (detail.Row.Recipient == FloorReportRecipient.SchoolConsultant) return ServiceResult.Fail("", "The report is already with the School Consultant, the top of the line.");
        var next = detail.Row.Recipient + 1;
        var holder = await HolderAsync(detail.Row.FloorId, next, ct);
        if (holder is not null && holder == detail.RelatedStaffId && next < FloorReportRecipient.SchoolConsultant)
        {
            next++;
            holder = await HolderAsync(detail.Row.FloorId, next, ct);
        }
        var report = await db.FloorReports.FirstAsync(r => r.Id == id, ct);
        (report.Recipient, report.RecipientStaffId, report.Status) = (next, holder, FloorReportStatus.Escalated);
        var text = TextNormalizer.CleanText(note);
        db.FloorReportEvents.Add(Event(report, FloorCaseAction.Escalated, text.Length == 0 ? null : text[..Math.Min(text.Length, FloorReportEvent.NoteMaxLength)], u));
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    private static FloorReportEvent Event(FloorReport r, FloorCaseAction action, string? note, FloorUser u) => new()
    {
        Report = r, Action = action, StatusAfter = r.Status, RecipientAfter = r.Recipient, RecipientStaffIdAfter = r.RecipientStaffId,
        Note = note, ByStaffId = u.StaffId, By = u.Name, At = DateTime.UtcNow,
    };

    // ================================================================== Social Worker tickets

    public async Task<ServiceResult> OpenTicketAsync(FloorTicketInput input, CancellationToken ct = default)
    {
        var u = await CurrentAsync(ct);
        if (!u.CanSubmit(input.FloorId) && !u.SeesAllFloors) return ServiceResult.Fail("", "Only the floor's team (and administrators) can open tickets for this floor.");
        if (u.StaffId is null && !u.IsSuperAdmin) return ServiceResult.Fail("", "Choose who you are under “Working as” to open a ticket.");
        var student = await db.Students.AsNoTracking().Where(s => s.Id == input.StudentId).Select(s => new { s.Id, s.SchoolClassId }).FirstOrDefaultAsync(ct);
        if (student is null) return ServiceResult.Fail("StudentId", "Choose the student from the list.");
        if (!await db.Floors.AnyAsync(f => f.Id == input.FloorId && f.IsActive, ct)) return ServiceResult.Fail("FloorId", "Choose a floor.");
        if (!Enum.IsDefined(input.Category)) return ServiceResult.Fail("Category", "Choose a category.");
        if (!Enum.IsDefined(input.Priority)) return ServiceResult.Fail("Priority", "Choose a priority.");
        var text = (input.Description ?? "").Trim();
        if (text.Length == 0) return ServiceResult.Fail("Description", "Describe the case.");
        if (text.Length > FloorTicket.TextMaxLength) return ServiceResult.Fail("Description", $"Use {FloorTicket.TextMaxLength} characters or fewer.");

        // The floor's Social Worker takes the ticket when there is exactly one; otherwise it stays open until assigned.
        var workers = await db.FloorStaffAssignments.Where(a => a.FloorId == input.FloorId && a.Role == FloorRole.SocialWorker && a.IsCurrent).Select(a => a.StaffMemberId).ToListAsync(ct);
        var ticket = new FloorTicket
        {
            StudentId = student.Id, FloorId = input.FloorId, SchoolClassId = student.SchoolClassId, Category = input.Category, Priority = input.Priority,
            Description = text, AssignedSocialWorkerId = workers.Count == 1 ? workers[0] : null,
            Status = workers.Count == 1 ? FloorTicketStatus.Assigned : FloorTicketStatus.Open, CreatedByStaffId = u.StaffId, CreatedBy = u.Name, Number = "FT-NEW",
        };
        ticket.Events.Add(TicketEvent(ticket, FloorCaseAction.Created, workers.Count == 0 ? "No Social Worker is assigned to this floor yet." : null, u));
        db.FloorTickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        ticket.Number = $"FT-{ticket.Id:00000}";
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(ticket.Id);
    }

    public async Task<IReadOnlyList<FloorTicketRow>> TicketsAsync(CancellationToken ct = default)
    {
        var u = await CurrentAsync(ct);
        if (!u.HasAccess) return [];
        var floors = u.Roles.Select(r => r.FloorId).Distinct().ToList();
        var me = u.StaffId ?? -1;
        var q = db.FloorTickets.AsNoTracking().AsQueryable();
        if (!u.SeesAllFloors) q = q.Where(t => floors.Contains(t.FloorId) || t.AssignedSocialWorkerId == me || t.CreatedByStaffId == me);
        return await q.OrderByDescending(t => t.UpdatedAt)
            .Select(t => new FloorTicketRow(t.Id, t.Number, t.FloorId, t.Floor.Name, t.StudentId, t.Student.FullName, t.SchoolClass.Name, t.Category, t.Priority, t.Status,
                t.AssignedSocialWorkerId, t.AssignedSocialWorker != null ? t.AssignedSocialWorker.FullName : null, t.CreatedBy, t.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<FloorTicketDetail?> TicketAsync(int id, CancellationToken ct = default)
    {
        var row = (await TicketsAsync(ct)).FirstOrDefault(t => t.Id == id);
        if (row is null) return null;
        var t = await db.FloorTickets.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Description, x.ResolutionNotes }).FirstAsync(ct);
        var names = await db.StaffMembers.AsNoTracking().Select(s => new { s.Id, s.FullName }).ToDictionaryAsync(s => s.Id, s => s.FullName, ct);
        var events = (await db.FloorTicketEvents.AsNoTracking().Where(e => e.FloorTicketId == id).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(ct))
            .Select(e => new CaseEvent(e.Action, Label(e.StatusAfter), e.AssignedAfterId is { } a && names.TryGetValue(a, out var n) ? $"Social Worker: {n}" : null, e.Note, e.By, e.At))
            .ToList();
        return new FloorTicketDetail(row, t.Description, t.ResolutionNotes, events);
    }

    /// <summary>The assigned Social Worker, the floor's Stage Coordinator and Floor Supervisor, and administrators.</summary>
    public async Task<bool> CanFollowUpAsync(FloorTicketRow ticket, CancellationToken ct = default)
    {
        var u = await CurrentAsync(ct);
        return u.LeadsFloor(ticket.FloorId) || (u.StaffId is { } me && me == ticket.SocialWorkerId);
    }

    public async Task<ServiceResult> UpdateTicketAsync(int id, FloorTicketStatus status, int? socialWorkerId, string? resolution, string? note, CancellationToken ct = default)
    {
        var detail = await TicketAsync(id, ct);
        if (detail is null) return ServiceResult.Fail("", "This ticket does not exist or you cannot open it.");
        if (!await CanFollowUpAsync(detail.Row, ct)) return ServiceResult.Fail("", "Only the assigned Social Worker and the floor's leads can update this ticket.");
        if (!Enum.IsDefined(status)) return ServiceResult.Fail("Status", "Choose a status.");
        if (socialWorkerId is { } sw && !await db.FloorStaffAssignments.AnyAsync(a => a.FloorId == detail.Row.FloorId && a.StaffMemberId == sw && a.Role == FloorRole.SocialWorker && a.IsCurrent, ct))
            return ServiceResult.Fail("SocialWorkerId", "Choose one of the floor's Social Workers.");
        var res = (resolution ?? "").Trim();
        if (status is FloorTicketStatus.Resolved or FloorTicketStatus.Closed && res.Length == 0)
            return ServiceResult.Fail("Resolution", "Add resolution notes before resolving or closing the ticket.");
        if (res.Length > FloorTicket.TextMaxLength) return ServiceResult.Fail("Resolution", $"Use {FloorTicket.TextMaxLength} characters or fewer.");
        var text = TextNormalizer.CleanText(note);
        if (text.Length > FloorReportEvent.NoteMaxLength) return ServiceResult.Fail("Note", $"Use {FloorReportEvent.NoteMaxLength} characters or fewer.");

        var t = await db.FloorTickets.FirstAsync(x => x.Id == id, ct);
        var u = await CurrentAsync(ct);
        if (socialWorkerId != t.AssignedSocialWorkerId && socialWorkerId is not null)
        {
            t.AssignedSocialWorkerId = socialWorkerId;
            db.FloorTicketEvents.Add(TicketEvent(t, FloorCaseAction.Assigned, null, u));
        }
        t.Status = status;
        if (res.Length > 0) t.ResolutionNotes = res;
        db.FloorTicketEvents.Add(TicketEvent(t, status == FloorTicketStatus.Escalated ? FloorCaseAction.Forwarded : FloorCaseAction.StatusChanged, text.Length == 0 ? null : text, u));
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    private static FloorTicketEvent TicketEvent(FloorTicket t, FloorCaseAction action, string? note, FloorUser u) => new()
    {
        Ticket = t, Action = action, StatusAfter = t.Status, AssignedAfterId = t.AssignedSocialWorkerId, Note = note, ByStaffId = u.StaffId, By = u.Name, At = DateTime.UtcNow,
    };

    // ================================================================== labels

    public static string Label(FloorRole r) => r switch
    {
        FloorRole.StageCoordinator => "Stage Coordinator",
        FloorRole.FloorSupervisor => "Floor Supervisor",
        FloorRole.Matron => "Matron",
        FloorRole.FloatingMatron => "Floating Matron",
        FloorRole.SocialWorker => "Floor Social Worker",
        _ => r.ToString(),
    };

    public static string Label(FloorReportRecipient r) => r switch
    {
        FloorReportRecipient.FloorSupervisor => "Floor Supervisor",
        FloorReportRecipient.StageCoordinator => "Stage Coordinator",
        _ => "School Consultant",
    };

    public static string Label(FloorReportStatus s) => s == FloorReportStatus.UnderReview ? "Under review" : s.ToString();
    public static string Label(FloorTicketStatus s) => s == FloorTicketStatus.InProgress ? "In progress" : s.ToString();
    public static string Label(FloorReportCategory c) => c switch
    {
        FloorReportCategory.DailyOperations => "Daily operations",
        FloorReportCategory.StudentSafety => "Student safety",
        FloorReportCategory.StaffConcern => "Staff concern",
        _ => c.ToString(),
    };
    public static string Label(FloorTicketCategory c) => c switch
    {
        FloorTicketCategory.BehavioralConcern => "Behavioral concern",
        FloorTicketCategory.StudentSupport => "Student support",
        _ => c.ToString(),
    };
}
