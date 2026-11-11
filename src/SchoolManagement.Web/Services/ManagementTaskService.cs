using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed class TaskListQuery
{
    public int? MemberId { get; set; }
    public string? Search { get; set; }

    /// <summary>todo, in-progress, on-hold, completed, open (not completed) or overdue.</summary>
    public string? Status { get; set; }

    /// <summary>low, medium, high or urgent.</summary>
    public string? Priority { get; set; }

    /// <summary>overdue, today, week, later or none.</summary>
    public string? Due { get; set; }

    public string Sort { get; set; } = "due";
    public string Dir { get; set; } = "asc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Status)
        || !string.IsNullOrWhiteSpace(Priority) || !string.IsNullOrWhiteSpace(Due);
}

public sealed record TaskRow(int Id, string Title, string? Description, string? Notes, int MemberId, string MemberName, ManagementPosition? MemberPosition,
    ManagementTaskStatus Status, TaskPriority Priority, DateOnly? DueDate, TimeOnly? DueTime, DateTime? CompletedAt,
    DateTime CreatedAt, DateTime UpdatedAt, string? CreatedBy, string? UpdatedBy)
{
    public bool IsOverdue => TaskLabels.IsOverdue(Status, DueDate, DueTime, DateTime.Now);
    public bool IsDueToday => Status != ManagementTaskStatus.Completed && DueDate == SchoolWeek.Today && !IsOverdue;
}

public sealed record TaskCounts(int Total, int ToDo, int InProgress, int OnHold, int Completed, int Overdue, int DueToday)
{
    public int Open => Total - Completed;
}

public sealed record TaskHistoryRow(long Id, int TaskId, string TaskTitle, TaskHistoryAction Action, ManagementTaskStatus StatusAfter,
    string Summary, DateTime ChangedAt, string? ChangedBy, int MemberId, string MemberName);

public sealed class TaskInput
{
    public int? MemberId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public ManagementTaskStatus Status { get; set; } = ManagementTaskStatus.ToDo;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public DateOnly? DueDate { get; set; }
    public TimeOnly? DueTime { get; set; }
}

public interface IManagementTaskService
{
    Task<PagedResult<TaskRow>> ListAsync(TaskListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<TaskRow>> ListAllAsync(TaskListQuery query, CancellationToken ct = default);
    Task<TaskCounts> CountsAsync(int? memberId, CancellationToken ct = default);
    Task<TaskRow?> GetAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(TaskInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, TaskInput input, CancellationToken ct = default);
    Task<ServiceResult> SetStatusAsync(int id, ManagementTaskStatus status, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<TaskHistoryRow>> HistoryAsync(int? memberId, int take, CancellationToken ct = default);
}

/// <summary>
/// Management Team tasks. Every task belongs to one Management Team member (never to a teacher, and
/// never mixed with workload). Each change adds a row to the member's task history.
/// </summary>
public sealed class ManagementTaskService(SchoolDbContext db, ICurrentUser currentUser) : IManagementTaskService
{
    public static readonly DateOnly EarliestDue = new(2000, 1, 1);
    public const int MaxYearsAhead = 5;

    public async Task<PagedResult<TaskRow>> ListAsync(TaskListQuery q, CancellationToken ct = default)
    {
        var filtered = Filter(q);
        var total = await filtered.CountAsync(ct);
        var size = Paging.NormalizePageSize(q.PageSize);
        var page = Paging.ClampPage(q.Page, total, size);
        var items = await Project(Sort(filtered, q)).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<TaskRow> { Items = items, Page = page, PageSize = size, TotalCount = total };
    }

    /// <summary>Every task matching the filters, in the list's sort order (for the PDF download).</summary>
    public async Task<IReadOnlyList<TaskRow>> ListAllAsync(TaskListQuery q, CancellationToken ct = default) =>
        await Project(Sort(Filter(q), q)).ToListAsync(ct);

    public async Task<TaskCounts> CountsAsync(int? memberId, CancellationToken ct = default)
    {
        var (today, time) = Now();
        var query = db.ManagementTasks.AsNoTracking().Where(t => memberId == null || t.ManagementMemberId == memberId);
        var byStatus = await query.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var overdue = await query.Where(OverduePredicate(today, time)).CountAsync(ct);
        var dueToday = await query.Where(t => t.Status != ManagementTaskStatus.Completed && t.DueDate == today).CountAsync(ct);
        return new TaskCounts(byStatus.Values.Sum(), byStatus.GetValueOrDefault(ManagementTaskStatus.ToDo), byStatus.GetValueOrDefault(ManagementTaskStatus.InProgress),
            byStatus.GetValueOrDefault(ManagementTaskStatus.OnHold), byStatus.GetValueOrDefault(ManagementTaskStatus.Completed), overdue, dueToday);
    }

    public Task<TaskRow?> GetAsync(int id, CancellationToken ct = default) =>
        Project(db.ManagementTasks.AsNoTracking().Where(t => t.Id == id)).FirstOrDefaultAsync(ct);

    public async Task<ServiceResult> CreateAsync(TaskInput input, CancellationToken ct = default)
    {
        var (check, member) = await ValidateAsync(input, null, ct);
        if (!check.Succeeded) return check;

        var task = new ManagementTask { CreatedBy = currentUser.Name, UpdatedBy = currentUser.Name };
        Apply(task, input);
        if (task.Status == ManagementTaskStatus.Completed) task.CompletedAt = DateTime.UtcNow;

        // The task and its "created" history entry are saved together (the entry needs the new id).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ManagementTasks.Add(task);
        await db.SaveChangesAsync(ct);
        Log(task, task.ManagementMemberId, TaskHistoryAction.Created,
            $"Task created for {member} · {TaskLabels.Priority(task.Priority)} priority · {TaskLabels.Due(task.DueDate, task.DueTime)} · {TaskLabels.Status(task.Status)}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(task.Id);
    }

    public async Task<ServiceResult> UpdateAsync(int id, TaskInput input, CancellationToken ct = default)
    {
        var task = await db.ManagementTasks.Include(t => t.Member).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return ServiceResult.Fail("", "This task no longer exists.");
        var (check, newMember) = await ValidateAsync(input, task, ct);
        if (!check.Succeeded) return check;

        var before = new { task.Title, task.Description, task.Notes, task.Status, task.Priority, task.DueDate, task.DueTime, task.ManagementMemberId, MemberName = task.Member.FullName };
        Apply(task, input);
        task.UpdatedBy = currentUser.Name;
        StampCompletion(task, before.Status);

        var changes = new List<string>();
        if (before.Title != task.Title) changes.Add("title");
        if (before.Description != task.Description) changes.Add("description");
        if (before.Notes != task.Notes) changes.Add("notes");
        if (before.Priority != task.Priority) changes.Add($"priority {TaskLabels.Priority(before.Priority)} → {TaskLabels.Priority(task.Priority)}");
        if (before.DueDate != task.DueDate || before.DueTime != task.DueTime)
            changes.Add($"due {TaskLabels.Due(before.DueDate, before.DueTime)} → {TaskLabels.Due(task.DueDate, task.DueTime)}");
        if (before.Status != task.Status) changes.Add($"status {TaskLabels.Status(before.Status)} → {TaskLabels.Status(task.Status)}");

        if (before.ManagementMemberId != task.ManagementMemberId)
        {
            Log(task, before.ManagementMemberId, TaskHistoryAction.Reassigned, $"Reassigned to {newMember}");
            Log(task, task.ManagementMemberId, TaskHistoryAction.Reassigned,
                $"Assigned from {before.MemberName}{(changes.Count == 0 ? "" : " · changed " + string.Join(", ", changes))}");
        }
        else if (changes.Count > 0)
        {
            var action = changes.Count == 1 && before.Status != task.Status ? TaskHistoryAction.StatusChanged : TaskHistoryAction.Updated;
            Log(task, task.ManagementMemberId, action, "Changed " + string.Join(", ", changes));
        }

        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> SetStatusAsync(int id, ManagementTaskStatus status, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status)) return ServiceResult.Fail("Status", "Choose a status from the list.");
        var task = await db.ManagementTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return ServiceResult.Fail("", "This task no longer exists.");
        if (task.Status == status) return ServiceResult.Ok(id);

        var before = task.Status;
        task.Status = status;
        task.UpdatedBy = currentUser.Name;
        StampCompletion(task, before);
        Log(task, task.ManagementMemberId, TaskHistoryAction.StatusChanged, $"Status {TaskLabels.Status(before)} → {TaskLabels.Status(status)}");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var task = await db.ManagementTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return ServiceResult.Fail("", "This task no longer exists.");
        Log(task, task.ManagementMemberId, TaskHistoryAction.Deleted, $"Task deleted (was {TaskLabels.Status(task.Status)})");
        db.ManagementTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<IReadOnlyList<TaskHistoryRow>> HistoryAsync(int? memberId, int take, CancellationToken ct = default) =>
        await db.ManagementTaskHistory.AsNoTracking()
            .Where(h => memberId == null || h.ManagementMemberId == memberId)
            .OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id)
            .Take(Math.Clamp(take, 1, 500))
            .Select(h => new TaskHistoryRow(h.Id, h.TaskId, h.TaskTitle, h.Action, h.StatusAfter, h.Summary, h.ChangedAt, h.ChangedBy, h.ManagementMemberId, h.Member.FullName))
            .ToListAsync(ct);

    // ------------------------------------------------------------------ helpers

    private async Task<(ServiceResult Result, string MemberName)> ValidateAsync(TaskInput input, ManagementTask? existing, CancellationToken ct)
    {
        var result = ServiceResult.Ok();
        var member = "";
        if (input.MemberId is not { } memberId) result.AddError("MemberId", "Choose the Management Team member.");
        else
        {
            member = await db.ManagementMembers.AsNoTracking().Where(m => m.Id == memberId).Select(m => m.FullName).FirstOrDefaultAsync(ct) ?? "";
            if (member.Length == 0)
                result.AddError("MemberId", await db.Teachers.AnyAsync(t => t.Id == memberId, ct)
                    ? "Tasks can only be assigned to Management Team members, not academic staff."
                    : "Choose a Management Team member from the list.");
        }

        var title = TextNormalizer.CleanText(input.Title);
        if (title.Length == 0) result.AddError("Title", "Enter the task title.");
        else if (title.Length > ManagementTask.TitleMaxLength) result.AddError("Title", $"Use {ManagementTask.TitleMaxLength} characters or fewer.");
        if ((input.Description?.Trim().Length ?? 0) > ManagementTask.TextMaxLength) result.AddError("Description", $"Use {ManagementTask.TextMaxLength} characters or fewer.");
        if ((input.Notes?.Trim().Length ?? 0) > ManagementTask.TextMaxLength) result.AddError("Notes", $"Use {ManagementTask.TextMaxLength} characters or fewer.");
        if (!Enum.IsDefined(input.Status)) result.AddError("Status", "Choose a status from the list.");
        if (!Enum.IsDefined(input.Priority)) result.AddError("Priority", "Choose a priority from the list.");

        var today = SchoolWeek.Today;
        if (input.DueDate is { } due)
        {
            var dueChanged = existing is null || existing.DueDate != due;
            if (due < EarliestDue || due > today.AddYears(MaxYearsAhead))
                result.AddError("DueDate", $"Enter a due date between {EarliestDue.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} and {MaxYearsAhead} years from today.");
            else if (dueChanged && due < today && input.Status != ManagementTaskStatus.Completed)
                result.AddError("DueDate", "The due date cannot be in the past.");
        }
        else if (input.DueTime is not null) result.AddError("DueTime", "Choose a due date for this time, or clear the time.");

        return (result, member);
    }

    private static void Apply(ManagementTask task, TaskInput input)
    {
        string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        task.ManagementMemberId = input.MemberId!.Value;
        task.Title = TextNormalizer.CleanText(input.Title);
        task.Description = Clean(input.Description);
        task.Notes = Clean(input.Notes);
        task.Status = input.Status;
        task.Priority = input.Priority;
        task.DueDate = input.DueDate;
        task.DueTime = input.DueDate is null ? null : input.DueTime;
    }

    private static void StampCompletion(ManagementTask task, ManagementTaskStatus before)
    {
        if (task.Status == ManagementTaskStatus.Completed && before != ManagementTaskStatus.Completed) task.CompletedAt = DateTime.UtcNow;
        else if (task.Status != ManagementTaskStatus.Completed) task.CompletedAt = null;
    }

    private void Log(ManagementTask task, int memberId, TaskHistoryAction action, string summary) =>
        db.ManagementTaskHistory.Add(new ManagementTaskHistory
        {
            TaskId = task.Id,
            ManagementMemberId = memberId,
            TaskTitle = task.Title,
            Action = action,
            StatusAfter = task.Status,
            Summary = summary.Length > ManagementTaskHistory.SummaryMaxLength ? summary[..ManagementTaskHistory.SummaryMaxLength] : summary,
            ChangedAt = DateTime.UtcNow,
            ChangedBy = currentUser.Name,
        });

    private static (DateOnly Today, TimeOnly Time) Now()
    {
        var now = DateTime.Now;
        return (DateOnly.FromDateTime(now), TimeOnly.FromDateTime(now));
    }

    private static System.Linq.Expressions.Expression<Func<ManagementTask, bool>> OverduePredicate(DateOnly today, TimeOnly time) =>
        t => t.Status != ManagementTaskStatus.Completed && t.DueDate != null
             && (t.DueDate < today || (t.DueDate == today && t.DueTime != null && t.DueTime < time));

    private IQueryable<ManagementTask> Filter(TaskListQuery q)
    {
        var (today, time) = Now();
        var query = db.ManagementTasks.AsNoTracking();
        if (q.MemberId is { } mid) query = query.Where(t => t.ManagementMemberId == mid);

        var term = q.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(t => t.Title.Contains(term) || (t.Description != null && t.Description.Contains(term)) || t.Member.FullName.Contains(term));

        query = q.Status switch
        {
            "todo" => query.Where(t => t.Status == ManagementTaskStatus.ToDo),
            "in-progress" => query.Where(t => t.Status == ManagementTaskStatus.InProgress),
            "on-hold" => query.Where(t => t.Status == ManagementTaskStatus.OnHold),
            "completed" => query.Where(t => t.Status == ManagementTaskStatus.Completed),
            "open" => query.Where(t => t.Status != ManagementTaskStatus.Completed),
            "overdue" => query.Where(OverduePredicate(today, time)),
            _ => query,
        };

        if (Enum.TryParse<TaskPriority>(q.Priority, true, out var priority) && Enum.IsDefined(priority))
            query = query.Where(t => t.Priority == priority);

        var weekEnd = today.AddDays(7);
        query = q.Due switch
        {
            "overdue" => query.Where(OverduePredicate(today, time)),
            "today" => query.Where(t => t.DueDate == today),
            "week" => query.Where(t => t.DueDate >= today && t.DueDate <= weekEnd),
            "later" => query.Where(t => t.DueDate > weekEnd),
            "none" => query.Where(t => t.DueDate == null),
            _ => query,
        };
        return query;
    }

    private static IQueryable<ManagementTask> Sort(IQueryable<ManagementTask> query, TaskListQuery q)
    {
        var desc = string.Equals(q.Dir, "desc", StringComparison.OrdinalIgnoreCase);
        return (q.Sort?.ToLowerInvariant()) switch
        {
            "priority" => desc
                ? query.OrderBy(t => t.Priority).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate)
                : query.OrderByDescending(t => t.Priority).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate),
            "created" => desc ? query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id) : query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id),
            "title" => desc ? query.OrderByDescending(t => t.Title) : query.OrderBy(t => t.Title),
            "status" => desc ? query.OrderByDescending(t => t.Status).ThenBy(t => t.DueDate) : query.OrderBy(t => t.Status).ThenBy(t => t.DueDate),
            // Due date: completed tasks last, tasks without a due date after dated ones.
            _ => desc
                ? query.OrderBy(t => t.Status == ManagementTaskStatus.Completed).ThenBy(t => t.DueDate == null).ThenByDescending(t => t.DueDate).ThenByDescending(t => t.DueTime)
                : query.OrderBy(t => t.Status == ManagementTaskStatus.Completed).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.DueTime).ThenByDescending(t => t.Priority),
        };
    }

    private static IQueryable<TaskRow> Project(IQueryable<ManagementTask> query) =>
        query.Select(t => new TaskRow(t.Id, t.Title, t.Description, t.Notes, t.ManagementMemberId, t.Member.FullName, t.Member.Position,
            t.Status, t.Priority, t.DueDate, t.DueTime, t.CompletedAt, t.CreatedAt, t.UpdatedAt, t.CreatedBy, t.UpdatedBy));
}
