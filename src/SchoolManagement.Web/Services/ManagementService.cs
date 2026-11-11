using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record ManagementMemberRow(int Id, string FullName, ManagementPosition? Position, string? ContactNumber, string? Email,
    DateOnly? AppointedOn, DateTime CreatedAt, int OpenTasks, int OverdueTasks, int CompletedTasks)
{
    public string PositionName => ManagementPositions.Name(Position);
}

public sealed record PositionSlot(ManagementPosition Position, ManagementMemberRow? Holder, DateOnly? Since = null);

public sealed record PositionHistoryRow(int Id, ManagementPosition Position, int MemberId, string MemberName, DateOnly StartDate, DateOnly? EndDate,
    bool IsCurrent, string? EndReason, string? AssignedBy, DateTime AssignedAt);

public sealed record ManagementMemberOption(int Id, string FullName, ManagementPosition? Position);

public sealed class ManagementMemberInput
{
    public string FullName { get; set; } = "";
    public ManagementPosition? Position { get; set; }
    public string? ContactNumber { get; set; }
    public string? Email { get; set; }
    public DateOnly? AppointedOn { get; set; }
}

public interface IManagementService
{
    Task<int> CountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ManagementMemberRow>> ListAsync(string? search, CancellationToken ct = default);
    Task<IReadOnlyList<PositionSlot>> PositionsAsync(CancellationToken ct = default);
    Task<ManagementMemberRow?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<ManagementMemberOption>> OptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<ManagementPosition, string>> PositionHoldersAsync(int? exceptMemberId, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(ManagementMemberInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, ManagementMemberInput input, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
    Task<ManagementMemberRow?> FindDuplicateAsync(string fullName, string? contactNumber, int? exceptId, CancellationToken ct = default);
    Task<IReadOnlyList<PositionHistoryRow>> PositionHistoryAsync(int? memberId, CancellationToken ct = default);
    Task<ServiceResult> ReassignPositionAsync(ManagementPosition position, int? memberId, DateOnly? startDate, CancellationToken ct = default);
}

/// <summary>
/// Management Team members ("Management" staff records). They are kept apart from academic staff:
/// they have no department, cannot be HODs, and only they can receive Management Team tasks.
/// </summary>
public sealed class ManagementService(SchoolDbContext db, ICurrentUser? currentUser = null) : IManagementService
{
    public static readonly DateOnly EarliestAppointment = new(1980, 1, 1);

    public Task<int> CountAsync(CancellationToken ct = default) => db.ManagementMembers.CountAsync(ct);

    public async Task<IReadOnlyList<ManagementMemberRow>> ListAsync(string? search, CancellationToken ct = default)
    {
        var query = db.ManagementMembers.AsNoTracking();
        var term = TextNormalizer.NameKey(search);
        if (term.Length > 0) query = query.Where(m => m.NormalizedName.Contains(term));
        var rows = await Project(query).ToListAsync(ct);
        // Position order (Director first), then members without a position by name.
        return rows.OrderBy(r => r.Position is null).ThenBy(r => r.Position).ThenBy(r => r.FullName).ToList();
    }

    public async Task<IReadOnlyList<PositionSlot>> PositionsAsync(CancellationToken ct = default)
    {
        var holders = await Project(db.ManagementMembers.AsNoTracking().Where(m => m.Position != null)).ToListAsync(ct);
        var since = await db.PositionAssignments.AsNoTracking().Where(a => a.IsCurrent).ToDictionaryAsync(a => a.Position, a => a.StartDate, ct);
        return ManagementPositions.All.Select(p => new PositionSlot(p, holders.FirstOrDefault(h => h.Position == p), since.TryGetValue(p, out var s) ? s : null)).ToList();
    }

    public async Task<IReadOnlyList<PositionHistoryRow>> PositionHistoryAsync(int? memberId, CancellationToken ct = default) =>
        await db.PositionAssignments.AsNoTracking().Where(a => memberId == null || a.ManagementMemberId == memberId)
            .OrderByDescending(a => a.IsCurrent).ThenByDescending(a => a.AssignedAt).ThenByDescending(a => a.Id)
            .Select(a => new PositionHistoryRow(a.Id, a.Position, a.ManagementMemberId, a.Member.FullName, a.StartDate, a.EndDate, a.IsCurrent, a.EndReason, a.AssignedBy, a.AssignedAt))
            .ToListAsync(ct);

    /// <summary>
    /// Gives a position to a member (or leaves it vacant when <paramref name="memberId"/> is null). The previous
    /// holder loses the position and the member leaves any other position; every change is kept as history.
    /// </summary>
    public async Task<ServiceResult> ReassignPositionAsync(ManagementPosition position, int? memberId, DateOnly? startDate, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(position)) return ServiceResult.Fail("Position", "Choose a position from the list.");
        var start = startDate ?? SchoolWeek.Today;
        if (start < EarliestAppointment || start > SchoolWeek.Today.AddYears(1))
            return ServiceResult.Fail("StartDate", "Enter a start date between 1980 and one year from today.");

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var holder = await db.ManagementMembers.FirstOrDefaultAsync(m => m.Position == position, ct);
        ManagementMember? member = null;
        if (memberId is not null)
        {
            member = await db.ManagementMembers.FirstOrDefaultAsync(m => m.Id == memberId, ct);
            if (member is null) return ServiceResult.Fail("MemberId", "Choose a Management Team member from the list.");
            if (holder?.Id == member.Id) return ServiceResult.Fail("MemberId", $"{member.FullName} already holds this position.");
        }
        else if (holder is null) return ServiceResult.Fail("MemberId", "Choose the member who takes this position.");

        try
        {
            if (holder is not null)
                await ChangePositionAsync(holder, null, start, member is null ? "Position left vacant" : $"Replaced by {member.FullName}", ct);
            if (member is not null)
                await ChangePositionAsync(member, position, start, $"Moved to {ManagementPositions.Name(position)}", ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This position was changed by someone else at the same time. Reload and try again.");
        }
        return ServiceResult.Ok(member?.Id);
    }

    /// <summary>Ends the member's current position row (if any), then records the new one. Saves in that order (unique "current" indexes).</summary>
    private async Task ChangePositionAsync(ManagementMember member, ManagementPosition? newPosition, DateOnly start, string endReason, CancellationToken ct)
    {
        var current = await db.PositionAssignments.Where(a => a.ManagementMemberId == member.Id && a.IsCurrent).ToListAsync(ct);
        foreach (var row in current)
        {
            row.IsCurrent = false;
            row.EndedAt = DateTime.UtcNow;
            row.EndDate = start < row.StartDate ? row.StartDate : start;
            row.EndReason = endReason.Length > ManagementPositionAssignment.ReasonMaxLength ? endReason[..ManagementPositionAssignment.ReasonMaxLength] : endReason;
        }
        member.Position = newPosition;
        member.JobTitle = newPosition is { } p ? ManagementPositions.Name(p) : "Management Team";
        await db.SaveChangesAsync(ct);
        if (newPosition is { } pos)
        {
            db.PositionAssignments.Add(new ManagementPositionAssignment
            {
                ManagementMemberId = member.Id, Position = pos, IsCurrent = true, StartDate = start,
                AssignedAt = DateTime.UtcNow, AssignedBy = currentUser?.Name,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    public Task<ManagementMemberRow?> GetAsync(int id, CancellationToken ct = default) =>
        Project(db.ManagementMembers.AsNoTracking().Where(m => m.Id == id)).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ManagementMemberOption>> OptionsAsync(CancellationToken ct = default) =>
        (await db.ManagementMembers.AsNoTracking().Select(m => new ManagementMemberOption(m.Id, m.FullName, m.Position)).ToListAsync(ct))
            .OrderBy(m => m.Position is null).ThenBy(m => m.Position).ThenBy(m => m.FullName).ToList();

    public async Task<IReadOnlyDictionary<ManagementPosition, string>> PositionHoldersAsync(int? exceptMemberId, CancellationToken ct = default) =>
        await db.ManagementMembers.AsNoTracking().Where(m => m.Position != null && m.Id != exceptMemberId)
            .ToDictionaryAsync(m => m.Position!.Value, m => m.FullName, ct);

    public async Task<ServiceResult> CreateAsync(ManagementMemberInput input, CancellationToken ct = default)
    {
        var check = await ValidateAsync(input, null, ct);
        if (!check.Succeeded) return check;
        var entity = new ManagementMember();
        Apply(entity, input);
        var position = entity.Position;
        entity.Position = null; // recorded below together with its history row
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ManagementMembers.Add(entity);
        var saved = await TrySaveAsync(ct);
        if (saved is not null) return saved;
        if (position is not null)
        {
            try { await ChangePositionAsync(entity, position, input.AppointedOn ?? SchoolWeek.Today, "", ct); }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                return ServiceResult.Fail("Position", "This position was just given to someone else. Reload and try again.");
            }
        }
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(entity.Id);
    }

    public async Task<ServiceResult> UpdateAsync(int id, ManagementMemberInput input, CancellationToken ct = default)
    {
        var entity = await db.ManagementMembers.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This Management Team member no longer exists.");
        var check = await ValidateAsync(input, id, ct);
        if (!check.Succeeded) return check;
        var oldPosition = entity.Position;
        Apply(entity, input);
        var newPosition = entity.Position;
        entity.Position = oldPosition;
        entity.JobTitle = oldPosition is { } op ? ManagementPositions.Name(op) : "Management Team";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var saved = await TrySaveAsync(ct);
        if (saved is not null) return saved;
        if (oldPosition != newPosition)
        {
            try
            {
                await ChangePositionAsync(entity, newPosition, SchoolWeek.Today,
                    newPosition is { } np ? $"Moved to {ManagementPositions.Name(np)}" : "Position removed", ct);
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                return ServiceResult.Fail("Position", "This position was just given to someone else. Reload and try again.");
            }
        }
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        if (!await db.ManagementMembers.AnyAsync(m => m.Id == id, ct)) return ServiceResult.Fail("", "This Management Team member no longer exists.");
        var tasks = await db.ManagementTasks.CountAsync(t => t.ManagementMemberId == id, ct);
        if (tasks > 0)
            return ServiceResult.Fail("", $"This member has {tasks} task(s). Reassign or delete them on the Tasks page before deleting the member.");
        var history = await db.DutyAssignments.CountAsync(d => d.PrimaryStaffId == id || d.BackupStaffId == id, ct)
            + await db.DutyBackupResponses.CountAsync(r => r.StaffMemberId == id, ct)
            + await db.DutyStaffAssignments.CountAsync(a => a.StaffMemberId == id, ct)
            + await db.StaffFeedback.CountAsync(f => f.EvaluatorStaffId == id, ct)
            + await db.StudentArrivals.CountAsync(a => a.ReceivedByStaffId == id, ct);
        if (history > 0) return ServiceResult.Fail("", "This member appears in duty, evaluation or student arrival records, so the record is kept for history.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ManagementTaskHistory.Where(h => h.ManagementMemberId == id).ExecuteDeleteAsync(ct);
        await db.ManagementMembers.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    public Task<ManagementMemberRow?> FindDuplicateAsync(string fullName, string? contactNumber, int? exceptId, CancellationToken ct = default)
    {
        var key = TextNormalizer.NameKey(fullName);
        var phone = TextNormalizer.PhoneKey(contactNumber);
        return Project(db.ManagementMembers.AsNoTracking()
                .Where(m => m.NormalizedName == key && m.Id != exceptId)
                .Where(m => phone == "" || m.NormalizedContact == "" || m.NormalizedContact == phone))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<ServiceResult> ValidateAsync(ManagementMemberInput input, int? exceptId, CancellationToken ct)
    {
        var result = ServiceResult.Ok();
        var name = TextNormalizer.CleanText(input.FullName);
        if (name.Length == 0) result.AddError("FullName", "Enter the member's full name.");
        else if (name.Length > StaffMember.NameMaxLength) result.AddError("FullName", $"Use {StaffMember.NameMaxLength} characters or fewer.");
        else if (!TextNormalizer.ContainsLetter(name)) result.AddError("FullName", "The name must contain letters.");

        if (input.Position is { } p && !Enum.IsDefined(p)) result.AddError("Position", "Choose a position from the list.");

        var phone = TextNormalizer.CleanPhone(input.ContactNumber);
        if (phone.Length > StaffMember.ContactMaxLength) result.AddError("ContactNumber", $"Use {StaffMember.ContactMaxLength} characters or fewer.");
        else if (!TextNormalizer.IsValidPhone(phone))
            result.AddError("ContactNumber", "Enter a valid phone number: digits, spaces or dashes, optional + at the start (6–15 digits).");

        var email = input.Email?.Trim() ?? "";
        if (email.Length > ManagementMember.EmailMaxLength) result.AddError("Email", $"Use {ManagementMember.EmailMaxLength} characters or fewer.");
        else if (email.Length > 0 && !IsEmail(email)) result.AddError("Email", "Enter a valid email address, e.g. name@school.edu.eg.");

        if (input.AppointedOn is { } d && (d < EarliestAppointment || d > SchoolWeek.Today.AddYears(1)))
            result.AddError("AppointedOn", "Enter an appointment date between 1980 and one year from today.");

        if (!result.Succeeded) return result;

        if (input.Position is { } pos)
        {
            var holder = await db.ManagementMembers.AsNoTracking().Where(m => m.Position == pos && m.Id != exceptId).Select(m => m.FullName).FirstOrDefaultAsync(ct);
            if (holder is not null)
                return ServiceResult.Fail("Position", $"{holder} already holds the {ManagementPositions.Name(pos)} position. Change their position first.");
        }

        var duplicate = await FindDuplicateAsync(name, phone, exceptId, ct);
        if (duplicate is not null)
            return ServiceResult.Fail("FullName", $"\"{duplicate.FullName}\" is already on the Management Team{(duplicate.ContactNumber is null ? "" : $" ({duplicate.ContactNumber})")}. Edit that record instead.");
        return result;
    }

    private static bool IsEmail(string value) =>
        MailAddress.TryCreate(value, out var address) && address.Address == value && value.Contains('.', StringComparison.Ordinal) && !value.Contains(' ');

    private static void Apply(ManagementMember entity, ManagementMemberInput input)
    {
        var phone = TextNormalizer.CleanPhone(input.ContactNumber);
        var email = input.Email?.Trim() ?? "";
        entity.FullName = TextNormalizer.CleanText(input.FullName);
        entity.NormalizedName = TextNormalizer.NameKey(input.FullName);
        entity.Position = input.Position;
        // The job title mirrors the position so staff lists elsewhere (duties, evaluations) show it.
        entity.JobTitle = input.Position is { } p ? ManagementPositions.Name(p) : "Management Team";
        entity.ContactNumber = phone.Length == 0 ? null : phone;
        entity.NormalizedContact = TextNormalizer.PhoneKey(phone);
        entity.Email = email.Length == 0 ? null : email;
        entity.AppointedOn = input.AppointedOn;
        // Never part of an academic department (also enforced by a check constraint).
        (entity.DepartmentId, entity.Subcategory, entity.Subject) = (null, null, null);
    }

    private IQueryable<ManagementMemberRow> Project(IQueryable<ManagementMember> query)
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);
        return query.Select(m => new ManagementMemberRow(m.Id, m.FullName, m.Position, m.ContactNumber, m.Email, m.AppointedOn, m.CreatedAt,
            db.ManagementTasks.Count(t => t.ManagementMemberId == m.Id && t.Status != ManagementTaskStatus.Completed),
            db.ManagementTasks.Count(t => t.ManagementMemberId == m.Id && t.Status != ManagementTaskStatus.Completed && t.DueDate != null
                && (t.DueDate < today || (t.DueDate == today && t.DueTime != null && t.DueTime < time))),
            db.ManagementTasks.Count(t => t.ManagementMemberId == m.Id && t.Status == ManagementTaskStatus.Completed)));
    }

    /// <summary>Saves; a unique-index clash (two people saving the same position or name at once) becomes a form error.</summary>
    private async Task<ServiceResult?> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "This position or person was saved by someone else at the same time. Reload and try again.");
        }
    }
}
