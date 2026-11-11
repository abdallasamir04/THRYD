using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <param name="Grades">Short grade line shown under the title (Midday sections), e.g. "Grades 4–6".</param>
public sealed record DutyComponentInfo(DutyType Type, DutyComponent Component, string Slug, string Name, string Summary, string Icon, bool HasClasses, bool DetailsPending, string? Grades = null);

/// <summary>Duty types and their components. Names and behaviour only; schedules are entered by the school.</summary>
public static class DutyComponents
{
    public static readonly IReadOnlyList<DutyComponentInfo> Morning =
    [
        new(DutyType.Morning, DutyComponent.StudentsArrival, "students-arrival", "Students Arrival Check",
            "Staff on duty while students arrive at school.", "users", false, false),
        new(DutyType.Morning, DutyComponent.InClassMorning, "in-class-morning", "In-Class Morning",
            "Staff supervising classes before the first lesson.", "door", true, false),
        new(DutyType.Morning, DutyComponent.MorningAssembly, "morning-assembly", "Morning Assembly (Line) / Proadcast",
            "Assembly line-up and proadcast duty.", "activity", false, true),
    ];

    public static readonly IReadOnlyList<DutyComponentInfo> Midday =
    [
        // Youngest first. Slugs (URLs) and stored component values are unchanged.
        new(DutyType.Midday, DutyComponent.PreKKg1Break, "prek-kg1", "Pre-K / KG1",
            "Break supervision for Pre-K and KG1.", "school", false, false, "Grades Pre-K and KG1"),
        new(DutyType.Midday, DutyComponent.Kg2Break, "kg2", "KG2",
            "Break supervision for KG2.", "school", false, false, "Grade KG2"),
        new(DutyType.Midday, DutyComponent.LowerElementaryBreak, "lower-elementary", "Lower Elementary",
            "Break supervision for grades 1–3.", "users", false, false, "Grades 1–3"),
        new(DutyType.Midday, DutyComponent.UpperElementaryBreak, "upper-elementary", "Upper Elementary",
            "Break supervision for grades 4–6.", "users", false, false, "Grades 4–6"),
        new(DutyType.Midday, DutyComponent.MiddleHighBreak, "middle-high-break", "Middle / High School Break",
            "Break supervision for grades 7–10.", "users", false, false, "Grades 7–10"),
    ];

    /// <summary>Card text for the Duties landing page.</summary>
    public static (string Icon, string Description) TypeCard(DutyType t) => t switch
    {
        DutyType.Midday => ("sun", "Break supervision by stage: Pre-K / KG1, KG2, Lower and Upper Elementary, Middle / High School."),
        DutyType.Dismissal => ("door", "End-of-day duties: students leaving by bus and pickup at the gates."),
        _ => ("shield-check", "Students arrival check, in-class morning and the morning assembly line-up."),
    };

    public static readonly IReadOnlyList<DutyComponentInfo> Dismissal =
    [
        new(DutyType.Dismissal, DutyComponent.DismissalBus, "bus", "Bus",
            "Staff seeing students onto the school buses. Gate and bus details go in location and notes.", "door", false, false),
        new(DutyType.Dismissal, DutyComponent.DismissalPickup, "pickup", "Pickup",
            "Staff handing students over to parents at pickup. Gate details go in location and notes.", "user", false, false),
    ];

    public static readonly DutyType[] Types = [DutyType.Morning, DutyType.Midday, DutyType.Dismissal];

    /// <summary>Every component of every duty type.</summary>
    public static IReadOnlyList<DutyComponentInfo> All { get; } = [.. Morning, .. Midday, .. Dismissal];

    public static IReadOnlyList<DutyComponentInfo> ForType(DutyType t) => t switch
    {
        DutyType.Midday => Midday,
        DutyType.Dismissal => Dismissal,
        _ => Morning,
    };

    public static DutyComponentInfo Get(DutyComponent c) => All.First(m => m.Component == c);
    public static DutyComponentInfo? FromSlug(string? slug) => All.FirstOrDefault(m => m.Slug == slug);
    public static string Name(DutyComponent c) => All.FirstOrDefault(m => m.Component == c)?.Name ?? c.ToString();
    public static string TypeName(DutyType t) => t switch { DutyType.Midday => "Midday Duty", DutyType.Dismissal => "Dismissal Duty", _ => "Morning Duty" };
    public static string TypeSlug(DutyType t) => t.ToString().ToLowerInvariant();
    public static DutyType TypeFromSlug(string? slug) => Types.FirstOrDefault(t => TypeSlug(t) == slug?.ToLowerInvariant(), DutyType.Morning);

    /// <summary>The component's page, e.g. /duties/midday/kg2.</summary>
    public static string Href(DutyComponentInfo info) => $"/duties/{TypeSlug(info.Type)}/{info.Slug}";
}

/// <summary>Who was there, from recorded attendance only (a selected or accepting backup is not coverage).</summary>
public enum CoverageState
{
    AttendanceNotRecorded,
    CoveredByAssigned,
    NeedsBackup,
    AwaitingBackupResponse,
    BackupRejected,
    AwaitingBackupAttendance,
    CoveredByBackup,
    BackupAbsent,
}

public static class Coverage
{
    public static CoverageState Of(AttendanceStatus primary, int? backupId, BackupRequestStatus backup, AttendanceStatus backupAttendance) =>
        primary switch
        {
            AttendanceStatus.Present => CoverageState.CoveredByAssigned,
            AttendanceStatus.NotRecorded => CoverageState.AttendanceNotRecorded,
            _ when backupId is null => CoverageState.NeedsBackup,
            _ => backup switch
            {
                BackupRequestStatus.Pending => CoverageState.AwaitingBackupResponse,
                BackupRequestStatus.Rejected => CoverageState.BackupRejected,
                BackupRequestStatus.Accepted => backupAttendance switch
                {
                    AttendanceStatus.Present => CoverageState.CoveredByBackup,
                    AttendanceStatus.Absent => CoverageState.BackupAbsent,
                    _ => CoverageState.AwaitingBackupAttendance,
                },
                _ => CoverageState.NeedsBackup,
            },
        };

    /// <summary>Someone on duty is recorded present (the assigned staff, or an accepted backup).</summary>
    public static bool IsCovered(CoverageState s) => s is CoverageState.CoveredByAssigned or CoverageState.CoveredByBackup;

    public static bool NeedsAction(CoverageState s) =>
        s is CoverageState.NeedsBackup or CoverageState.BackupRejected or CoverageState.BackupAbsent;

    public static string Label(CoverageState s) => s switch
    {
        CoverageState.AttendanceNotRecorded => "Attendance not recorded",
        CoverageState.CoveredByAssigned => "Assigned staff present",
        CoverageState.NeedsBackup => "Absent · needs backup",
        CoverageState.AwaitingBackupResponse => "Waiting for backup to respond",
        CoverageState.BackupRejected => "Backup declined · choose another",
        CoverageState.AwaitingBackupAttendance => "Backup accepted · attendance not recorded",
        CoverageState.CoveredByBackup => "Backup present",
        _ => "Backup absent",
    };

    public static string Tone(CoverageState s) => s switch
    {
        CoverageState.CoveredByAssigned or CoverageState.CoveredByBackup => "success",
        CoverageState.AwaitingBackupResponse or CoverageState.AwaitingBackupAttendance => "warning",
        CoverageState.AttendanceNotRecorded => "neutral",
        _ => "error",
    };
}

/// <summary>The four operational statuses and the rule that links them to recorded attendance.</summary>
public static class DutyStatuses
{
    public static readonly DutyStatus[] All = [DutyStatus.Pending, DutyStatus.InProgress, DutyStatus.Covered, DutyStatus.NotCovered];

    public static string Label(DutyStatus s) => s switch
    {
        DutyStatus.Pending => "Pending",
        DutyStatus.InProgress => "In Progress",
        DutyStatus.Covered => "Covered",
        _ => "Not Covered",
    };

    public static string Tone(DutyStatus s) => s switch
    {
        DutyStatus.Covered => "success",
        DutyStatus.InProgress => "info",
        DutyStatus.NotCovered => "error",
        _ => "neutral",
    };

    public static string Slug(DutyStatus s) => s switch
    {
        DutyStatus.InProgress => "in-progress",
        DutyStatus.NotCovered => "not-covered",
        _ => s.ToString().ToLowerInvariant(),
    };

    public static DutyStatus? FromSlug(string? slug) => All.Cast<DutyStatus?>().FirstOrDefault(s => Slug(s!.Value) == slug);

    /// <summary>Covered and In Progress need someone recorded present; assignment alone is never coverage.</summary>
    public static string? Problem(DutyStatus status, CoverageState coverage) => status switch
    {
        DutyStatus.Covered when !Coverage.IsCovered(coverage) =>
            "A duty can be marked Covered only when the assigned staff member, or a backup who accepted, is recorded as present.",
        DutyStatus.InProgress when !Coverage.IsCovered(coverage) =>
            "A duty can be In Progress only when someone on duty is recorded as present.",
        _ => null,
    };
}

public sealed record StaffOption(int Id, string FullName, string? JobTitle, string StaffType);

public sealed record DutyRow(
    int Id, DutyType DutyType, DutyComponent Component, DateOnly DutyDate, string Description, TimeOnly StartTime, TimeOnly EndTime, string? Location,
    int PrimaryStaffId, string PrimaryName, AttendanceStatus PrimaryAttendance,
    int? BackupStaffId, string? BackupName, BackupRequestStatus BackupStatus, AttendanceStatus BackupAttendance,
    string? Notes, IReadOnlyList<string> ClassNames, IReadOnlyList<int> ClassIds, DateTime CreatedAt, DateTime UpdatedAt,
    DutyStatus Status, string? AuditNotes, string? IssuesEncountered, bool FollowUpRequired, string? CreatedBy, string? UpdatedBy)
{
    public CoverageState Coverage => Services.Coverage.Of(PrimaryAttendance, BackupStaffId, BackupStatus, BackupAttendance);
}

public sealed record BackupResponseRow(BackupResponseKind Response, string StaffName, string? Note, DateTime RecordedAt, string? RecordedBy);

public sealed record DutyAuditRow(long Id, DutyAuditAction Action, DutyStatus StatusAfter, string Summary, DateTime ChangedAt, string? ChangedBy);

public sealed record DutyStaffRow(
    int Id, int StaffId, string StaffName, DutyRole Role, AttendanceStatus Attendance, BackupRequestStatus BackupStatus,
    bool IsCurrent, string? EndReason, DateTime AssignedAt, int EvaluationCount, int? LatestRating, bool CanEvaluate);

public sealed record DutyComponentSummary(DutyComponentInfo Info, int Total, int Pending, int InProgress, int Covered, int NotCovered, int NeedsStaffAction);

public sealed record DutyInput(
    DutyComponent Component, DateOnly Date, string Description, TimeOnly? StartTime, TimeOnly? EndTime, string? Location,
    int? PrimaryStaffId, AttendanceStatus PrimaryAttendance, int? BackupStaffId, string? Notes, IReadOnlyList<int> ClassIds);

public sealed class DutyAuditQuery
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Type { get; set; }
    public string? Component { get; set; }
    public string? Location { get; set; }
    public int? StaffId { get; set; }
    public string? Status { get; set; }
    public bool FollowUp { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public bool HasFilters => !string.IsNullOrWhiteSpace(From) || !string.IsNullOrWhiteSpace(To) || !string.IsNullOrWhiteSpace(Type)
        || !string.IsNullOrWhiteSpace(Component) || !string.IsNullOrWhiteSpace(Location) || StaffId is not null
        || !string.IsNullOrWhiteSpace(Status) || FollowUp;
}

public interface IDutyService
{
    Task<IReadOnlyList<DutyComponentSummary>> MorningSummaryAsync(DateOnly date, CancellationToken ct = default);
    Task<IReadOnlyList<DutyComponentSummary>> SummaryAsync(DutyType type, DateOnly date, CancellationToken ct = default);
    Task<IReadOnlyList<DutyRow>> ListAsync(DutyComponent component, DateOnly date, CancellationToken ct = default);
    Task<PagedResult<DutyRow>> AuditListAsync(DutyAuditQuery query, CancellationToken ct = default);
    Task<IReadOnlyDictionary<DutyStatus, int>> StatusCountsAsync(DateOnly date, CancellationToken ct = default);
    Task<DutyRow?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<BackupResponseRow>> BackupHistoryAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<DutyAuditRow>> AuditTrailAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<DutyStaffRow>> StaffAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<StaffOption>> StaffOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> LocationsAsync(CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(DutyInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, DutyInput input, CancellationToken ct = default);
    Task<ServiceResult> SetPrimaryAttendanceAsync(int id, AttendanceStatus status, CancellationToken ct = default);
    Task<ServiceResult> RecordBackupResponseAsync(int id, bool accepted, string? note, CancellationToken ct = default);
    Task<ServiceResult> SetBackupAttendanceAsync(int id, AttendanceStatus status, CancellationToken ct = default);
    Task<ServiceResult> SetStatusAsync(int id, DutyStatus status, CancellationToken ct = default);
    Task<ServiceResult> UpdateAuditAsync(int id, DutyStatus status, string? auditNotes, string? issues, bool followUp, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Duty sessions: assigned staff → attendance → (if absent) backup → backup accepts or declines →
/// backup attendance, plus the operational status (Pending / In Progress / Covered / Not Covered).
/// Every change is validated here, mirrored into DutyStaffAssignments (one row per person and role)
/// and written to the DutyAudits trail.
/// </summary>
public sealed class DutyService(SchoolDbContext db, ICurrentUser currentUser) : IDutyService
{
    // ------------------------------------------------------------------ reads

    public Task<IReadOnlyList<DutyComponentSummary>> MorningSummaryAsync(DateOnly date, CancellationToken ct = default) =>
        SummaryAsync(DutyType.Morning, date, ct);

    public async Task<IReadOnlyList<DutyComponentSummary>> SummaryAsync(DutyType type, DateOnly date, CancellationToken ct = default)
    {
        var rows = await db.DutyAssignments.AsNoTracking().Where(d => d.DutyDate == date)
            .Select(d => new { d.Component, d.Status, d.PrimaryAttendance, d.BackupStaffId, d.BackupStatus, d.BackupAttendance })
            .ToListAsync(ct);
        return DutyComponents.ForType(type).Select(info =>
        {
            var mine = rows.Where(r => r.Component == info.Component).ToList();
            return new DutyComponentSummary(info, mine.Count,
                mine.Count(r => r.Status == DutyStatus.Pending),
                mine.Count(r => r.Status == DutyStatus.InProgress),
                mine.Count(r => r.Status == DutyStatus.Covered),
                mine.Count(r => r.Status == DutyStatus.NotCovered),
                mine.Count(r => Coverage.NeedsAction(Coverage.Of(r.PrimaryAttendance, r.BackupStaffId, r.BackupStatus, r.BackupAttendance))));
        }).ToList();
    }

    public async Task<IReadOnlyList<DutyRow>> ListAsync(DutyComponent component, DateOnly date, CancellationToken ct = default) =>
        await Project(db.DutyAssignments.AsNoTracking().Where(d => d.Component == component && d.DutyDate == date)
            .OrderBy(d => d.StartTime).ThenBy(d => d.Id)).ToListAsync(ct);

    public async Task<PagedResult<DutyRow>> AuditListAsync(DutyAuditQuery q, CancellationToken ct = default)
    {
        var query = db.DutyAssignments.AsNoTracking();
        if (TryDate(q.From) is { } from) query = query.Where(d => d.DutyDate >= from);
        if (TryDate(q.To) is { } to) query = query.Where(d => d.DutyDate <= to);
        if (Enum.TryParse<DutyType>(q.Type, true, out var type)) query = query.Where(d => d.DutyType == type);
        if (DutyComponents.FromSlug(q.Component) is { } component) query = query.Where(d => d.Component == component.Component);
        var location = TextNormalizer.CleanText(q.Location);
        if (location.Length > 0) query = query.Where(d => d.Location != null && d.Location.Contains(location));
        if (q.StaffId is { } staffId) query = query.Where(d => d.StaffAssignments.Any(s => s.StaffMemberId == staffId));
        if (DutyStatuses.FromSlug(q.Status) is { } status) query = query.Where(d => d.Status == status);
        if (q.FollowUp) query = query.Where(d => d.FollowUpRequired);

        var total = await query.CountAsync(ct);
        var size = Paging.NormalizePageSize(q.PageSize);
        var page = Paging.ClampPage(q.Page, total, size);
        var items = await Project(query.OrderByDescending(d => d.DutyDate).ThenBy(d => d.StartTime).ThenBy(d => d.Id))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<DutyRow> { Items = items, Page = page, PageSize = size, TotalCount = total };
    }

    public async Task<IReadOnlyDictionary<DutyStatus, int>> StatusCountsAsync(DateOnly date, CancellationToken ct = default)
    {
        var counts = await db.DutyAssignments.AsNoTracking().Where(d => d.DutyDate == date)
            .GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return DutyStatuses.All.ToDictionary(s => s, s => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0);
    }

    public Task<DutyRow?> GetAsync(int id, CancellationToken ct = default) =>
        Project(db.DutyAssignments.AsNoTracking().Where(d => d.Id == id)).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<BackupResponseRow>> BackupHistoryAsync(int id, CancellationToken ct = default) =>
        await db.DutyBackupResponses.AsNoTracking().Where(r => r.DutyAssignmentId == id)
            .OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.Id)
            .Select(r => new BackupResponseRow(r.Response, r.StaffMember.FullName, r.Note, r.RecordedAt, r.RecordedBy))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DutyAuditRow>> AuditTrailAsync(int id, CancellationToken ct = default) =>
        await db.DutyAudits.AsNoTracking().Where(a => a.DutyAssignmentId == id)
            .OrderByDescending(a => a.ChangedAt).ThenByDescending(a => a.Id)
            .Select(a => new DutyAuditRow(a.Id, a.Action, a.StatusAfter, a.Summary, a.ChangedAt, a.ChangedBy))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DutyStaffRow>> StaffAsync(int id, CancellationToken ct = default)
    {
        var dutyDate = await db.DutyAssignments.Where(d => d.Id == id).Select(d => (DateOnly?)d.DutyDate).FirstOrDefaultAsync(ct);
        if (dutyDate is null) return [];
        var rows = await db.DutyStaffAssignments.AsNoTracking().Where(s => s.DutyAssignmentId == id)
            .OrderBy(s => s.Role).ThenByDescending(s => s.IsCurrent).ThenBy(s => s.AssignedAt)
            .Select(s => new
            {
                s.Id, s.StaffMemberId, s.StaffMember.FullName, s.Role, s.Attendance, s.BackupStatus, s.IsCurrent, s.EndReason, s.AssignedAt,
                Count = s.Feedback.Count,
                Latest = s.Feedback.OrderByDescending(f => f.CreatedAt).Select(f => (int?)f.Rating).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.Select(s => new DutyStaffRow(s.Id, s.StaffMemberId, s.FullName, s.Role, s.Attendance, s.BackupStatus, s.IsCurrent,
            s.EndReason, s.AssignedAt, s.Count, s.Latest,
            StaffDutyRules.CanEvaluate(s.Role, s.IsCurrent, s.BackupStatus, dutyDate.Value))).ToList();
    }

    /// <summary>All staff members (teachers today; other staff types will appear here too).</summary>
    public async Task<IReadOnlyList<StaffOption>> StaffOptionsAsync(CancellationToken ct = default) =>
        await db.StaffMembers.AsNoTracking().OrderBy(s => s.FullName)
            .Select(s => new StaffOption(s.Id, s.FullName, s.JobTitle, EF.Property<string>(s, "StaffType")))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> LocationsAsync(CancellationToken ct = default) =>
        await db.DutyAssignments.AsNoTracking().Where(d => d.Location != null && d.Location != "")
            .Select(d => d.Location!).Distinct().OrderBy(l => l).ToListAsync(ct);

    // ------------------------------------------------------------------ writes

    public async Task<ServiceResult> CreateAsync(DutyInput input, CancellationToken ct = default)
    {
        var check = await ValidateAsync(input, null, ct);
        if (!check.Succeeded) return check;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var info = DutyComponents.Get(input.Component);
        var entity = new DutyAssignment
        {
            DutyType = info.Type,
            Component = input.Component,
            DutyDate = input.Date,
            Status = DutyStatus.Pending,
            CreatedBy = currentUser.Name,
            UpdatedBy = currentUser.Name,
        };
        Apply(entity, input);
        foreach (var classId in ClassIds(input)) entity.Classes.Add(new DutyAssignmentClass { SchoolClassId = classId });
        ApplyBackup(entity, input.BackupStaffId);
        db.DutyAssignments.Add(entity);
        await db.SaveChangesAsync(ct);

        await SyncStaffAsync(entity, ct);
        Audit(entity, DutyAuditAction.Created, $"Created: {entity.Description} ({Times(entity)}), assigned to {await NameAsync(entity.PrimaryStaffId, ct)}.");
        if (entity.BackupStaffId is { } b) Audit(entity, DutyAuditAction.BackupRequested, $"Backup requested: {await NameAsync(b, ct)} (Pending).");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(entity.Id);
    }

    public async Task<ServiceResult> UpdateAsync(int id, DutyInput input, CancellationToken ct = default)
    {
        var entity = await db.DutyAssignments.Include(d => d.Classes).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This duty no longer exists.");
        input = input with { Component = entity.Component }; // a duty never moves between components
        var check = await ValidateAsync(input, entity, ct);
        if (!check.Succeeded) return check;

        var newCoverage = Coverage.Of(input.PrimaryAttendance, input.BackupStaffId,
            input.BackupStaffId == entity.BackupStaffId ? entity.BackupStatus : BackupRequestStatus.Pending,
            input.BackupStaffId == entity.BackupStaffId ? entity.BackupAttendance : AttendanceStatus.NotRecorded);
        if (StatusGuard(entity.Status, newCoverage) is { } statusProblem) return ServiceResult.Fail("PrimaryAttendance", statusProblem);

        // Someone whose Duty periods count this duty must stay on it as present.
        if (entity.PrimaryStaffId != input.PrimaryStaffId || input.PrimaryAttendance == AttendanceStatus.Absent)
            if (await WorkloadProblemAsync(entity.Id, entity.PrimaryStaffId, ct) is { } p) return ServiceResult.Fail("PrimaryStaffId", p);
        if (entity.BackupStaffId is { } oldBackup && oldBackup != input.BackupStaffId)
            if (await WorkloadProblemAsync(entity.Id, oldBackup, ct) is { } p) return ServiceResult.Fail("BackupStaffId", p);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var before = (entity.PrimaryStaffId, entity.PrimaryAttendance, entity.BackupStaffId);
        Apply(entity, input);
        entity.DutyDate = input.Date;
        entity.UpdatedBy = currentUser.Name;
        var wanted = ClassIds(input).ToHashSet();
        foreach (var link in entity.Classes.Where(c => !wanted.Contains(c.SchoolClassId)).ToList()) entity.Classes.Remove(link);
        foreach (var classId in wanted.Where(w => entity.Classes.All(c => c.SchoolClassId != w)))
            entity.Classes.Add(new DutyAssignmentClass { SchoolClassId = classId });
        ApplyBackup(entity, input.BackupStaffId);
        await db.SaveChangesAsync(ct);
        await SyncStaffAsync(entity, ct);

        var changes = new List<string> { "Details updated" };
        if (before.PrimaryStaffId != entity.PrimaryStaffId) changes.Add($"assigned staff → {await NameAsync(entity.PrimaryStaffId, ct)}");
        if (before.PrimaryAttendance != entity.PrimaryAttendance) changes.Add($"attendance → {Att(entity.PrimaryAttendance)}");
        Audit(entity, DutyAuditAction.Updated, string.Join("; ", changes) + ".");
        if (before.BackupStaffId != entity.BackupStaffId)
            Audit(entity, DutyAuditAction.BackupRequested, entity.BackupStaffId is { } b
                ? $"Backup requested: {await NameAsync(b, ct)} (Pending)."
                : "Backup request withdrawn.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> SetPrimaryAttendanceAsync(int id, AttendanceStatus status, CancellationToken ct = default)
    {
        var entity = await db.DutyAssignments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This duty no longer exists.");
        if (!Enum.IsDefined(status)) return ServiceResult.Fail("", "Unknown attendance value.");
        // Absent needs a backup, which is chosen in the duty form (it opens with Absent selected).
        if (status == AttendanceStatus.Absent && entity.BackupStaffId is null)
            return ServiceResult.Fail("BackupStaffId", "The assigned staff member is absent: choose a backup staff member.");

        var newBackup = status == AttendanceStatus.Absent ? entity.BackupStaffId : null;
        var coverage = Coverage.Of(status, newBackup, entity.BackupStatus, entity.BackupAttendance);
        if (StatusGuard(entity.Status, coverage) is { } problem) return ServiceResult.Fail("", problem);
        if (status == AttendanceStatus.Absent && await WorkloadProblemAsync(id, entity.PrimaryStaffId, ct) is { } wp) return ServiceResult.Fail("", wp);
        if (newBackup is null && entity.BackupStaffId is { } ob && await WorkloadProblemAsync(id, ob, ct) is { } wb) return ServiceResult.Fail("", wb);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        entity.PrimaryAttendance = status;
        if (status != AttendanceStatus.Absent) ApplyBackup(entity, null);
        entity.UpdatedBy = currentUser.Name;
        await db.SaveChangesAsync(ct);
        await SyncStaffAsync(entity, ct);
        Audit(entity, DutyAuditAction.AttendanceRecorded, $"{await NameAsync(entity.PrimaryStaffId, ct)} recorded {Att(status)}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> RecordBackupResponseAsync(int id, bool accepted, string? note, CancellationToken ct = default)
    {
        var entity = await db.DutyAssignments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This duty no longer exists.");
        if (entity.BackupStaffId is null) return ServiceResult.Fail("", "No backup has been asked for this duty.");
        if (entity.BackupStatus != BackupRequestStatus.Pending)
            return ServiceResult.Fail("", $"The backup has already {(entity.BackupStatus == BackupRequestStatus.Accepted ? "accepted" : "declined")}. To ask someone else, edit the duty and choose another backup.");

        var cleanNote = TextNormalizer.CleanText(note);
        if (cleanNote.Length > DutyBackupResponse.NoteMaxLength) return ServiceResult.Fail("Note", $"Use {DutyBackupResponse.NoteMaxLength} characters or fewer.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Accepting is not attending: backup attendance stays "not recorded" until it is recorded.
        entity.BackupStatus = accepted ? BackupRequestStatus.Accepted : BackupRequestStatus.Rejected;
        entity.BackupAttendance = AttendanceStatus.NotRecorded;
        entity.UpdatedBy = currentUser.Name;
        AddResponse(entity, entity.BackupStaffId.Value, accepted ? BackupResponseKind.Accepted : BackupResponseKind.Rejected, cleanNote);
        await db.SaveChangesAsync(ct);
        await SyncStaffAsync(entity, ct);
        Audit(entity, DutyAuditAction.BackupResponded,
            $"{await NameAsync(entity.BackupStaffId.Value, ct)} {(accepted ? "accepted" : "declined")} the backup request{(cleanNote.Length > 0 ? $" ({cleanNote})" : "")}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> SetBackupAttendanceAsync(int id, AttendanceStatus status, CancellationToken ct = default)
    {
        var entity = await db.DutyAssignments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This duty no longer exists.");
        if (!Enum.IsDefined(status)) return ServiceResult.Fail("", "Unknown attendance value.");
        if (entity.BackupStaffId is null || entity.BackupStatus != BackupRequestStatus.Accepted)
            return ServiceResult.Fail("", "Backup attendance can be recorded only after the backup has accepted.");
        var coverage = Coverage.Of(entity.PrimaryAttendance, entity.BackupStaffId, entity.BackupStatus, status);
        if (StatusGuard(entity.Status, coverage) is { } problem) return ServiceResult.Fail("", problem);
        if (status == AttendanceStatus.Absent && await WorkloadProblemAsync(id, entity.BackupStaffId.Value, ct) is { } wp) return ServiceResult.Fail("", wp);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        entity.BackupAttendance = status;
        entity.UpdatedBy = currentUser.Name;
        await db.SaveChangesAsync(ct);
        await SyncStaffAsync(entity, ct);
        Audit(entity, DutyAuditAction.BackupAttendanceRecorded, $"Backup {await NameAsync(entity.BackupStaffId.Value, ct)} recorded {Att(status)}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> SetStatusAsync(int id, DutyStatus status, CancellationToken ct = default)
    {
        var entity = await db.DutyAssignments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This duty no longer exists.");
        return await ApplyStatusAsync(entity, status, null, ct);
    }

    public async Task<ServiceResult> UpdateAuditAsync(int id, DutyStatus status, string? auditNotes, string? issues, bool followUp, CancellationToken ct = default)
    {
        var entity = await db.DutyAssignments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This duty no longer exists.");
        var notes = (auditNotes ?? "").Trim();
        var problems = (issues ?? "").Trim();
        if (notes.Length > DutyAssignment.NotesMaxLength) return ServiceResult.Fail("AuditNotes", $"Use {DutyAssignment.NotesMaxLength} characters or fewer.");
        if (problems.Length > DutyAssignment.NotesMaxLength) return ServiceResult.Fail("IssuesEncountered", $"Use {DutyAssignment.NotesMaxLength} characters or fewer.");
        return await ApplyStatusAsync(entity, status, (notes, problems, followUp), ct);
    }

    private async Task<ServiceResult> ApplyStatusAsync(DutyAssignment entity, DutyStatus status, (string Notes, string Issues, bool FollowUp)? audit, CancellationToken ct)
    {
        if (!Enum.IsDefined(status)) return ServiceResult.Fail("Status", "Choose Pending, In Progress, Covered or Not Covered.");
        var coverage = Coverage.Of(entity.PrimaryAttendance, entity.BackupStaffId, entity.BackupStatus, entity.BackupAttendance);
        if (DutyStatuses.Problem(status, coverage) is { } problem) return ServiceResult.Fail("Status", problem);

        var statusChanged = entity.Status != status;
        entity.Status = status;
        entity.UpdatedBy = currentUser.Name;
        if (statusChanged) Audit(entity, DutyAuditAction.StatusChanged, $"Status → {DutyStatuses.Label(status)}.");
        if (audit is { } a)
        {
            var changed = (entity.AuditNotes ?? "") != a.Notes || (entity.IssuesEncountered ?? "") != a.Issues || entity.FollowUpRequired != a.FollowUp;
            entity.AuditNotes = a.Notes.Length == 0 ? null : a.Notes;
            entity.IssuesEncountered = a.Issues.Length == 0 ? null : a.Issues;
            entity.FollowUpRequired = a.FollowUp;
            if (changed) Audit(entity, DutyAuditAction.AuditNotesUpdated,
                $"Audit notes updated{(a.Issues.Length > 0 ? "; issues recorded" : "")}{(a.FollowUp ? "; follow-up required" : "")}.");
        }
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(entity.Id);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await db.DutyAssignments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This duty no longer exists.");
        var linked = await db.WorkloadAssignments.CountAsync(w => w.DutyAssignmentId == id, ct);
        if (linked > 0)
            return ServiceResult.Fail("", $"This duty is counted in {linked} teacher workload assignment(s). Remove those Duty periods first.");
        var evaluations = await db.StaffFeedback.CountAsync(f => f.DutyStaffAssignment.DutyAssignmentId == id, ct);
        if (evaluations > 0)
            return ServiceResult.Fail("", $"This duty has {evaluations} staff evaluation(s), which are kept as history. Mark it Not Covered or add audit notes instead.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Audit(entity, DutyAuditAction.Deleted, $"Deleted: {entity.Description} ({Times(entity)}).");
        await db.SaveChangesAsync(ct);
        await db.DutyAssignments.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    // ------------------------------------------------------------------ rules

    private static string? StatusGuard(DutyStatus status, CoverageState newCoverage) =>
        status is DutyStatus.Covered or DutyStatus.InProgress && !Coverage.IsCovered(newCoverage)
            ? $"This duty is marked {DutyStatuses.Label(status)}, which needs someone recorded present. Change the duty status first."
            : null;

    private async Task<string?> WorkloadProblemAsync(int dutyId, int staffId, CancellationToken ct)
    {
        var counted = await db.WorkloadAssignments.AnyAsync(w => w.DutyAssignmentId == dutyId && w.TeacherId == staffId, ct);
        return counted
            ? $"This duty is counted in {await NameAsync(staffId, ct)}'s weekly workload. Remove those Duty periods first."
            : null;
    }

    private async Task<ServiceResult> ValidateAsync(DutyInput input, DutyAssignment? existing, CancellationToken ct)
    {
        var r = ServiceResult.Ok();
        var info = DutyComponents.All.FirstOrDefault(m => m.Component == input.Component);
        if (info is null) return ServiceResult.Fail("", "Unknown duty component.");

        var description = TextNormalizer.CleanText(input.Description);
        if (description.Length == 0) r.AddError("Description", "Describe the duty.");
        else if (description.Length > DutyAssignment.DescriptionMaxLength) r.AddError("Description", $"Use {DutyAssignment.DescriptionMaxLength} characters or fewer.");
        if (TextNormalizer.CleanText(input.Location).Length > DutyAssignment.LocationMaxLength) r.AddError("Location", $"Use {DutyAssignment.LocationMaxLength} characters or fewer.");
        if (TextNormalizer.CleanText(input.Notes).Length > DutyAssignment.NotesMaxLength) r.AddError("Notes", $"Use {DutyAssignment.NotesMaxLength} characters or fewer.");

        if (input.StartTime is null) r.AddError("StartTime", "Enter the start time.");
        if (input.EndTime is null) r.AddError("EndTime", "Enter the end time.");
        if (input.StartTime is { } s && input.EndTime is { } e && e <= s) r.AddError("EndTime", "The end time must be after the start time.");
        if (!Enum.IsDefined(input.PrimaryAttendance)) r.AddError("PrimaryAttendance", "Choose Present, Absent or Not recorded.");

        if (input.PrimaryStaffId is null) r.AddError("PrimaryStaffId", "Choose the assigned staff member.");
        else if (!await db.StaffMembers.AnyAsync(m => m.Id == input.PrimaryStaffId, ct)) r.AddError("PrimaryStaffId", "Choose a staff member from the list.");

        if (input.BackupStaffId is { } backupId)
        {
            if (backupId == input.PrimaryStaffId) r.AddError("BackupStaffId", "The backup must be a different person from the assigned staff member.");
            else if (!await db.StaffMembers.AnyAsync(m => m.Id == backupId, ct)) r.AddError("BackupStaffId", "Choose a staff member from the list.");
            if (input.PrimaryAttendance != AttendanceStatus.Absent)
                r.AddError("BackupStaffId", "A backup is only needed when the assigned staff member is absent.");
        }
        else if (input.PrimaryAttendance == AttendanceStatus.Absent)
        {
            r.AddError("BackupStaffId", "The assigned staff member is absent: choose a backup staff member.");
        }

        if (info.HasClasses)
        {
            var ids = input.ClassIds.Distinct().ToList();
            if (ids.Count == 0) r.AddError("ClassIds", "Choose the class or classes this duty covers.");
            else
            {
                var current = existing?.Classes.Select(c => c.SchoolClassId).ToHashSet() ?? [];
                var found = await db.Classes.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
                if (found.Count != ids.Count) r.AddError("ClassIds", "Choose classes from the list.");
                var inactive = found.Where(c => !c.IsActive && !current.Contains(c.Id)).Select(c => c.Name).ToList();
                if (inactive.Count > 0) r.AddError("ClassIds", $"Inactive class(es) cannot be added: {string.Join(", ", inactive)}.");
            }
        }

        return r;
    }

    private static IEnumerable<int> ClassIds(DutyInput input) =>
        DutyComponents.Get(input.Component).HasClasses ? input.ClassIds.Distinct() : [];

    private static void Apply(DutyAssignment entity, DutyInput input)
    {
        entity.Description = TextNormalizer.CleanText(input.Description);
        entity.StartTime = input.StartTime!.Value;
        entity.EndTime = input.EndTime!.Value;
        var location = TextNormalizer.CleanText(input.Location);
        entity.Location = location.Length == 0 ? null : location;
        var notes = (input.Notes ?? "").Trim();
        entity.Notes = notes.Length == 0 ? null : notes;
        entity.PrimaryStaffId = input.PrimaryStaffId!.Value;
        entity.PrimaryAttendance = input.PrimaryAttendance;
    }

    /// <summary>A new or different backup starts as a Pending request with no attendance; removing one is logged.</summary>
    private void ApplyBackup(DutyAssignment entity, int? backupId)
    {
        if (entity.BackupStaffId == backupId) return;
        if (entity.BackupStaffId is { } previous && entity.BackupStatus != BackupRequestStatus.Rejected)
            AddResponse(entity, previous, BackupResponseKind.Withdrawn, "Backup request withdrawn.");

        entity.BackupStaffId = backupId;
        entity.BackupAttendance = AttendanceStatus.NotRecorded;
        entity.BackupStatus = backupId is null ? BackupRequestStatus.None : BackupRequestStatus.Pending;
        if (backupId is { } id) AddResponse(entity, id, BackupResponseKind.Requested, null);
    }

    /// <summary>
    /// Mirrors the duty's current primary and backup into DutyStaffAssignments. A person who is
    /// replaced keeps their row (IsCurrent = false) so their history and evaluations stay intact.
    /// Ended rows are saved before new ones are added (the "one current row per role" index).
    /// </summary>
    private async Task SyncStaffAsync(DutyAssignment d, CancellationToken ct)
    {
        var rows = await db.DutyStaffAssignments.Where(s => s.DutyAssignmentId == d.Id && s.IsCurrent).ToListAsync(ct);
        var primary = rows.FirstOrDefault(r => r.Role == DutyRole.Primary);
        var backup = rows.FirstOrDefault(r => r.Role == DutyRole.Backup);
        var now = DateTime.UtcNow;
        var ended = false;

        if (primary is not null && primary.StaffMemberId != d.PrimaryStaffId)
        {
            End(primary, "Replaced as assigned staff", now);
            ended = true;
            primary = null;
        }
        if (backup is not null && backup.StaffMemberId != d.BackupStaffId)
        {
            End(backup, backup.BackupStatus == BackupRequestStatus.Rejected ? "Declined the request"
                : d.BackupStaffId is null ? "Backup no longer needed" : "Replaced by another backup", now);
            ended = true;
            backup = null;
        }
        if (ended) await db.SaveChangesAsync(ct);

        if (primary is null)
            db.DutyStaffAssignments.Add(new DutyStaffAssignment
            {
                DutyAssignmentId = d.Id, StaffMemberId = d.PrimaryStaffId, Role = DutyRole.Primary,
                Attendance = d.PrimaryAttendance, AssignedAt = now, AssignedBy = currentUser.Name,
            });
        else primary.Attendance = d.PrimaryAttendance;

        if (d.BackupStaffId is { } backupId)
        {
            if (backup is null)
                db.DutyStaffAssignments.Add(new DutyStaffAssignment
                {
                    DutyAssignmentId = d.Id, StaffMemberId = backupId, Role = DutyRole.Backup,
                    BackupStatus = d.BackupStatus, Attendance = d.BackupAttendance, AssignedAt = now, AssignedBy = currentUser.Name,
                });
            else
            {
                backup.BackupStatus = d.BackupStatus;
                backup.Attendance = d.BackupAttendance;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static void End(DutyStaffAssignment row, string reason, DateTime now)
    {
        row.IsCurrent = false;
        row.EndedAt = now;
        row.EndReason = reason;
    }

    private void AddResponse(DutyAssignment entity, int staffId, BackupResponseKind kind, string? note)
    {
        var response = new DutyBackupResponse
        {
            StaffMemberId = staffId,
            Response = kind,
            Note = string.IsNullOrEmpty(note) ? null : note,
            RecordedAt = DateTime.UtcNow,
            RecordedBy = currentUser.Name,
        };
        if (entity.Id == 0) entity.BackupResponses.Add(response);
        else
        {
            response.DutyAssignmentId = entity.Id;
            db.DutyBackupResponses.Add(response);
        }
    }

    private void Audit(DutyAssignment d, DutyAuditAction action, string summary) =>
        db.DutyAudits.Add(new DutyAudit
        {
            DutyAssignmentId = d.Id,
            DutyType = d.DutyType,
            Component = d.Component,
            DutyDate = d.DutyDate,
            Description = d.Description,
            Action = action,
            StatusAfter = d.Status,
            Summary = summary.Length > DutyAudit.SummaryMaxLength ? summary[..DutyAudit.SummaryMaxLength] : summary,
            ChangedAt = DateTime.UtcNow,
            ChangedBy = currentUser.Name,
        });

    private async Task<string> NameAsync(int staffId, CancellationToken ct) =>
        await db.StaffMembers.Where(s => s.Id == staffId).Select(s => s.FullName).FirstOrDefaultAsync(ct) ?? $"Staff #{staffId}";

    private static string Att(AttendanceStatus a) => a == AttendanceStatus.NotRecorded ? "attendance not recorded" : a.ToString().ToLowerInvariant();

    private static string Times(DutyAssignment d) =>
        $"{d.DutyDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} {d.StartTime:HH\\:mm}–{d.EndTime:HH\\:mm}";

    private static DateOnly? TryDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static IQueryable<DutyRow> Project(IQueryable<DutyAssignment> q) =>
        q.Select(d => new DutyRow(d.Id, d.DutyType, d.Component, d.DutyDate, d.Description, d.StartTime, d.EndTime, d.Location,
            d.PrimaryStaffId, d.PrimaryStaff.FullName, d.PrimaryAttendance,
            d.BackupStaffId, d.BackupStaff != null ? d.BackupStaff.FullName : null, d.BackupStatus, d.BackupAttendance,
            d.Notes,
            d.Classes.OrderBy(c => c.SchoolClass.SortOrder).Select(c => c.SchoolClass.Name).ToList(),
            d.Classes.Select(c => c.SchoolClassId).ToList(),
            d.CreatedAt, d.UpdatedAt,
            d.Status, d.AuditNotes, d.IssuesEncountered, d.FollowUpRequired, d.CreatedBy, d.UpdatedBy));
}
