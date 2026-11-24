using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed class TaskListQuery
{
    /// <summary>Assigned to (the executor): any staff member. Query name <c>memberId</c>, kept for existing links.</summary>
    public int? MemberId { get; set; }

    /// <summary>Submitted to / reviewer: any staff member.</summary>
    public int? ReviewerId { get; set; }

    /// <summary>The executor's department: a department id, or "management" (Management Team members).</summary>
    public string? Department { get; set; }

    /// <summary>Relative to the "Working as" staff member: assigned (to me), created (by me) or review (submitted to me).</summary>
    public string? Mine { get; set; }

    public string? Search { get; set; }

    /// <summary>todo (pending), in-progress, on-hold, completed, cancelled, open (not completed or cancelled) or overdue.</summary>
    public string? Status { get; set; }

    /// <summary>low, medium, high or critical ("urgent" still accepted).</summary>
    public string? Priority { get; set; }

    /// <summary>overdue, today, week, later or none.</summary>
    public string? Due { get; set; }

    /// <summary>
    /// Tasks page tab (each task is in at most one): overdue (open, past its due moment), today (open, due later today),
    /// upcoming (open, due after today) or completed. Empty = all tasks. See <see cref="TaskTabs"/>.
    /// </summary>
    public string? Tab { get; set; }

    /// <summary>The executor's staff category: academic, management or support (StaffCategories).</summary>
    public string? Category { get; set; }

    /// <summary>Due date range (inclusive); either end may be empty.</summary>
    public DateOnly? DueFrom { get; set; }
    public DateOnly? DueTo { get; set; }

    public string Sort { get; set; } = "due";
    public string Dir { get; set; } = "asc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public bool HasScope => ReviewerId is not null || !string.IsNullOrWhiteSpace(Department) || !string.IsNullOrWhiteSpace(Mine)
        || StaffCategories.Parse(Category) is not null;

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Status)
        || !string.IsNullOrWhiteSpace(Priority) || !string.IsNullOrWhiteSpace(Due) || DueFrom is not null || DueTo is not null || HasScope;

    /// <summary>The same filters without the tab (for the tab counts).</summary>
    public TaskListQuery WithoutTab()
    {
        var copy = (TaskListQuery)MemberwiseClone();
        copy.Tab = null;
        return copy;
    }
}

/// <summary>The Tasks page tabs. A task is in at most one of Overdue, Due Today, Upcoming and Completed.</summary>
public static class TaskTabs
{
    public static readonly (string? Key, string Label, string Icon)[] All =
    [
        (null, "All Tasks", "list"), ("overdue", "Overdue", "alert-circle"), ("today", "Due Today", "clock"),
        ("upcoming", "Upcoming", "calendar"), ("completed", "Completed", "check-circle"),
    ];

    public static string? Normalize(string? tab) => All.Any(t => t.Key == tab) ? tab : null;
}

/// <summary>Tab totals: All, Overdue, Due Today (not yet overdue), Upcoming (due after today) and Completed.</summary>
public sealed record TaskTabCounts(int All, int Overdue, int DueToday, int Upcoming, int Completed)
{
    public int Of(string? tab) => tab switch
    {
        "overdue" => Overdue, "today" => DueToday, "upcoming" => Upcoming, "completed" => Completed, _ => All,
    };
}

/// <summary>A staff member on a task (executor, reviewer or creator), from their current staff record.</summary>
public sealed record TaskPerson(int Id, string Name, string? JobTitle, string? Department, ManagementPosition? Position, bool IsManagement)
{
    /// <summary>"Science · Science Teacher", "Management Team · SQAM".</summary>
    public string Describe => string.Join(" · ", new[]
    {
        IsManagement ? "Management Team" : Department,
        IsManagement ? (Position is { } p ? ManagementPositions.Name(p) : JobTitle) : JobTitle,
    }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));

    public string Href => IsManagement ? $"/management/members/{Id}" : $"/staff/{Id}";
}

/// <summary>A staff member in a task's person picker: name, with department and job title as the note.</summary>
public sealed record TaskStaffOption(int Id, string FullName, string Note, int? DepartmentId, bool IsManagement,
    StaffCategory Category = StaffCategory.Academic, bool IsActive = true);

public sealed record TaskDepartmentOption(int Id, string Name);

public sealed record TaskRow(int Id, string Title, string? Description, string? Notes, TaskPerson AssignedTo, TaskPerson? Reviewer, TaskPerson? Creator,
    ManagementTaskStatus Status, TaskPriority Priority, DateOnly? DueDate, TimeOnly? DueTime, DateTime? CompletedAt,
    DateTime CreatedAt, DateTime UpdatedAt, string? CreatedBy, string? UpdatedBy, int AttachmentCount)
{
    public int MemberId => AssignedTo.Id;
    public string MemberName => AssignedTo.Name;

    /// <summary>The creator's current name, or the name recorded when the task was created.</summary>
    public string? CreatorName => Creator?.Name ?? CreatedBy;

    public bool IsClosed => TaskLabels.IsClosed(Status);
    public bool IsOverdue => TaskLabels.IsOverdue(Status, DueDate, DueTime, SchoolTime.Now);
    public bool IsDueToday => !IsClosed && DueDate == SchoolWeek.Today && !IsOverdue;
}

public sealed record TaskCounts(int Total, int ToDo, int InProgress, int OnHold, int Completed, int Cancelled, int Overdue, int DueToday)
{
    /// <summary>Not completed and not cancelled.</summary>
    public int Open => Total - Completed - Cancelled;
}

public sealed record TaskHistoryRow(long Id, int TaskId, string TaskTitle, TaskHistoryAction Action, ManagementTaskStatus StatusAfter,
    string Summary, DateTime ChangedAt, string? ChangedBy, int MemberId, string MemberName);

public sealed class TaskInput
{
    public int? AssignedToId { get; set; }
    public int? ReviewerId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public ManagementTaskStatus Status { get; set; } = ManagementTaskStatus.ToDo;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public DateOnly? DueDate { get; set; }
    public TimeOnly? DueTime { get; set; }

    /// <summary>
    /// New tasks created from Academic Departments: the department the task is for. The executor must be a member of it
    /// (a task's department is its executor's department, as on the Tasks page's department filter). Ignored by edits.
    /// </summary>
    public int? DepartmentId { get; set; }

    /// <summary>Uploaded files (staging keys) to attach on save.</summary>
    public IReadOnlyList<Guid> AttachmentKeys { get; set; } = [];

    /// <summary>Attachments of the task to remove on save (edit only).</summary>
    public IReadOnlyList<int> RemoveAttachmentIds { get; set; } = [];
}

public interface IManagementTaskService
{
    Task<PagedResult<TaskRow>> ListAsync(TaskListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<TaskRow>> ListAllAsync(TaskListQuery query, CancellationToken ct = default);
    Task<TaskCounts> CountsAsync(int? memberId, CancellationToken ct = default);

    /// <summary>Counts within the query's people / department scope (status, priority and due filters are ignored).</summary>
    Task<TaskCounts> ScopeCountsAsync(TaskListQuery scope, CancellationToken ct = default);

    /// <summary>Tab totals for every filter of <paramref name="query"/> except its tab.</summary>
    Task<TaskTabCounts> TabCountsAsync(TaskListQuery query, CancellationToken ct = default);
    Task<TaskRow?> GetAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(TaskInput input, CancellationToken ct = default);

    /// <summary>The checks <see cref="CreateAsync"/> makes, without saving (Import / Export → Tasks preview).</summary>
    Task<ServiceResult> ValidateNewAsync(TaskInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, TaskInput input, CancellationToken ct = default);
    Task<ServiceResult> SetStatusAsync(int id, ManagementTaskStatus status, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<TaskHistoryRow>> HistoryAsync(int? memberId, int take, CancellationToken ct = default);
    Task<IReadOnlyList<TaskHistoryRow>> TaskHistoryAsync(int taskId, CancellationToken ct = default);

    /// <summary>Every staff member (Management Team and all other staff) for the task person pickers.</summary>
    Task<IReadOnlyList<TaskStaffOption>> StaffAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaskDepartmentOption>> DepartmentsAsync(CancellationToken ct = default);

    /// <summary>Checks and stores an uploaded file until its task form is saved.</summary>
    Task<(ServiceResult Result, TaskAttachmentView? File)> StageAttachmentAsync(string? fileName, long length, Stream content, CancellationToken ct = default);
    Task<IReadOnlyList<TaskAttachmentView>> StagedAttachmentsAsync(IEnumerable<Guid> keys, CancellationToken ct = default);
    Task<IReadOnlyList<TaskAttachmentView>> AttachmentsAsync(int taskId, CancellationToken ct = default);
    Task<TaskAttachmentFile?> OpenAttachmentAsync(int taskId, int attachmentId, bool download, CancellationToken ct = default);
}

/// <summary>
/// Management Team tasks. The Management Team creates and manages them; any staff member can be the executor or the
/// reviewer (never mixed with teacher workload or the timetable). The creator is the "Working as" staff member and is
/// set once. Each change adds a row to the executor's task history; attachments are files in <see cref="ITaskFileStore"/>.
/// </summary>
public sealed class ManagementTaskService(SchoolDbContext db, ICurrentUser currentUser, ITaskFileStore? files = null) : IManagementTaskService
{
    public static readonly DateOnly EarliestDue = new(2000, 1, 1);
    public const int MaxYearsAhead = 5;

    /// <summary>Uploads never attached to a saved task are removed after this long.</summary>
    public static readonly TimeSpan StagingLifetime = TimeSpan.FromDays(1);

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

    public Task<TaskCounts> CountsAsync(int? memberId, CancellationToken ct = default) =>
        ScopeCountsAsync(new TaskListQuery { MemberId = memberId }, ct);

    public async Task<TaskCounts> ScopeCountsAsync(TaskListQuery scope, CancellationToken ct = default)
    {
        var (today, time) = Now();
        var query = Scope(scope);
        var byStatus = await query.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var overdue = await query.Where(OverduePredicate(today, time)).CountAsync(ct);
        var dueToday = await query.Where(t => t.Status != ManagementTaskStatus.Completed && t.Status != ManagementTaskStatus.Cancelled && t.DueDate == today).CountAsync(ct);
        int Of(ManagementTaskStatus s) => byStatus.GetValueOrDefault(s);
        return new TaskCounts(byStatus.Values.Sum(), Of(ManagementTaskStatus.ToDo), Of(ManagementTaskStatus.InProgress), Of(ManagementTaskStatus.OnHold),
            Of(ManagementTaskStatus.Completed), Of(ManagementTaskStatus.Cancelled), overdue, dueToday);
    }

    public async Task<TaskTabCounts> TabCountsAsync(TaskListQuery query, CancellationToken ct = default)
    {
        var (today, time) = Now();
        var all = Filter(query.WithoutTab());
        return new TaskTabCounts(
            await all.CountAsync(ct),
            await all.Where(TabPredicate("overdue", today, time)).CountAsync(ct),
            await all.Where(TabPredicate("today", today, time)).CountAsync(ct),
            await all.Where(TabPredicate("upcoming", today, time)).CountAsync(ct),
            await all.Where(TabPredicate("completed", today, time)).CountAsync(ct));
    }

    public Task<TaskRow?> GetAsync(int id, CancellationToken ct = default) =>
        Project(db.ManagementTasks.AsNoTracking().Where(t => t.Id == id)).FirstOrDefaultAsync(ct);

    public async Task<ServiceResult> CreateAsync(TaskInput input, CancellationToken ct = default)
    {
        var (check, people) = await ValidateAsync(input, null, ct);
        if (!check.Succeeded) return check;
        var staged = await StagedRowsAsync(input.AttachmentKeys, ct);
        if (AttachmentError(staged.Count, input.AttachmentKeys.Count) is { } attachError) return ServiceResult.Fail("Attachments", attachError);

        // The creator is the current account (server side, never a posted value): the signed-in user / "Working as" staff
        // member, or the Super Admin without a staff record (CreatedById stays empty). An edit never changes it.
        var creator = currentUser.AccountName();
        var task = new ManagementTask { CreatedById = currentUser.StaffId, CreatedBy = creator, UpdatedBy = creator };
        Apply(task, input);
        if (task.Status == ManagementTaskStatus.Completed) task.CompletedAt = DateTime.UtcNow;

        // The task, its files and its "created" history entry are saved together (the entry needs the new id).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ManagementTasks.Add(task);
        await db.SaveChangesAsync(ct);
        foreach (var file in staged) file.TaskId = task.Id;
        var summary = $"Task created for {people.Executor}{(people.Reviewer is null ? "" : $" · submitted to {people.Reviewer}")} · " +
                      $"{TaskLabels.Priority(task.Priority)} priority · {TaskLabels.Due(task.DueDate, task.DueTime)} · {TaskLabels.Status(task.Status)}" +
                      (staged.Count == 0 ? "" : " · " + Ui.Plural(staged.Count, "attachment", "attachments"));
        Log(task, task.AssignedToId, TaskHistoryAction.Created, summary);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(task.Id);
    }

    public async Task<ServiceResult> ValidateNewAsync(TaskInput input, CancellationToken ct = default) =>
        (await ValidateAsync(input, null, ct)).Result;

    public async Task<ServiceResult> UpdateAsync(int id, TaskInput input, CancellationToken ct = default)
    {
        var task = await db.ManagementTasks.Include(t => t.AssignedTo).Include(t => t.Reviewer).Include(t => t.Attachments)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return ServiceResult.Fail("", "This task no longer exists.");
        var (check, people) = await ValidateAsync(input, task, ct);
        if (!check.Succeeded) return check;

        var removed = task.Attachments.Where(a => input.RemoveAttachmentIds.Contains(a.Id)).ToList();
        var staged = await StagedRowsAsync(input.AttachmentKeys, ct);
        if (AttachmentError(staged.Count, input.AttachmentKeys.Count, task.Attachments.Count - removed.Count) is { } attachError)
            return ServiceResult.Fail("Attachments", attachError);

        var before = new
        {
            task.Title, task.Description, task.Notes, task.Status, task.Priority, task.DueDate, task.DueTime,
            task.AssignedToId, Executor = task.AssignedTo.FullName, task.ReviewerId, Reviewer = task.Reviewer?.FullName,
        };
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
        if (before.ReviewerId != task.ReviewerId) changes.Add($"reviewer {before.Reviewer ?? "none"} → {people.Reviewer ?? "none"}");
        if (staged.Count > 0) changes.Add("attached " + string.Join(", ", staged.Select(f => f.FileName)));
        if (removed.Count > 0) changes.Add("removed " + string.Join(", ", removed.Select(f => f.FileName)));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var file in staged) file.TaskId = task.Id;
        db.TaskAttachments.RemoveRange(removed);
        if (before.AssignedToId != task.AssignedToId)
        {
            Log(task, before.AssignedToId, TaskHistoryAction.Reassigned, $"Reassigned to {people.Executor}");
            Log(task, task.AssignedToId, TaskHistoryAction.Reassigned,
                $"Assigned from {before.Executor}{(changes.Count == 0 ? "" : " · changed " + string.Join(", ", changes))}");
        }
        else if (changes.Count > 0)
        {
            var action = changes.Count == 1 && before.Status != task.Status ? TaskHistoryAction.StatusChanged : TaskHistoryAction.Updated;
            Log(task, task.AssignedToId, action, "Changed " + string.Join(", ", changes));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        DeleteFiles(removed); // only once the rows are gone
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> SetStatusAsync(int id, ManagementTaskStatus status, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status)) return ServiceResult.Fail("Status", "Choose a status from the list.");
        var task = await db.ManagementTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return ServiceResult.Fail("", "This task no longer exists.");
        if (task.Status == status) return ServiceResult.Ok(id);
        if (status == ManagementTaskStatus.OnHold) return ServiceResult.Fail("Status", "Choose To Do, In Progress, Completed or Cancelled.");

        var before = task.Status;
        task.Status = status;
        task.UpdatedBy = currentUser.Name;
        StampCompletion(task, before);
        Log(task, task.AssignedToId, TaskHistoryAction.StatusChanged, $"Status {TaskLabels.Status(before)} → {TaskLabels.Status(status)}");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var task = await db.ManagementTasks.Include(t => t.Attachments).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return ServiceResult.Fail("", "This task no longer exists.");
        var attachments = task.Attachments.ToList();
        Log(task, task.AssignedToId, TaskHistoryAction.Deleted,
            $"Task deleted (was {TaskLabels.Status(task.Status)}){(attachments.Count == 0 ? "" : " · " + Ui.Plural(attachments.Count, "attachment", "attachments") + " removed")}");
        db.TaskAttachments.RemoveRange(attachments);
        db.ManagementTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        DeleteFiles(attachments);
        return ServiceResult.Ok(id);
    }

    public async Task<IReadOnlyList<TaskHistoryRow>> HistoryAsync(int? memberId, int take, CancellationToken ct = default) =>
        await HistoryRows(db.ManagementTaskHistory.AsNoTracking().Where(h => memberId == null || h.StaffMemberId == memberId), Math.Clamp(take, 1, 500))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskHistoryRow>> TaskHistoryAsync(int taskId, CancellationToken ct = default) =>
        await HistoryRows(db.ManagementTaskHistory.AsNoTracking().Where(h => h.TaskId == taskId), 200).ToListAsync(ct);

    private static IQueryable<TaskHistoryRow> HistoryRows(IQueryable<ManagementTaskHistory> query, int take) =>
        query.OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id).Take(take)
            .Select(h => new TaskHistoryRow(h.Id, h.TaskId, h.TaskTitle, h.Action, h.StatusAfter, h.Summary, h.ChangedAt, h.ChangedBy, h.StaffMemberId, h.StaffMember.FullName));

    public async Task<IReadOnlyList<TaskStaffOption>> StaffAsync(CancellationToken ct = default)
    {
        // Staff records only (Teachers and Management Team share one table, so nobody is listed twice; students are a
        // separate table and never appear). A contract that has ended marks the person as no longer active.
        var today = SchoolWeek.Today;
        var rows = await db.StaffMembers.AsNoTracking()
            .Select(s => new
            {
                s.Id, s.FullName, s.JobTitle, s.DepartmentId, Department = s.Department != null ? s.Department.Name : null,
                IsManagement = s is ManagementMember, Position = s is ManagementMember ? ((ManagementMember)s).Position : null,
                Active = s.ContractEndDate == null || s.ContractEndDate >= today,
            })
            .ToListAsync(ct);
        return rows
            .Select(s => new TaskStaffOption(s.Id, s.FullName, new TaskPerson(s.Id, s.FullName, s.JobTitle, s.Department, s.Position, s.IsManagement).Describe,
                s.DepartmentId, s.IsManagement, StaffCategories.Of(s.IsManagement, s.JobTitle), s.Active))
            .OrderBy(s => !s.IsManagement).ThenBy(s => s.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<TaskDepartmentOption>> DepartmentsAsync(CancellationToken ct = default) =>
        await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new TaskDepartmentOption(d.Id, d.Name)).ToListAsync(ct);

    // ------------------------------------------------------------------ attachments

    public async Task<(ServiceResult Result, TaskAttachmentView? File)> StageAttachmentAsync(string? fileName, long length, Stream content, CancellationToken ct = default)
    {
        var store = files ?? throw new InvalidOperationException("No task file store is configured.");
        var extension = TaskAttachmentRules.ExtensionOf(fileName);
        if (!TaskAttachmentRules.IsAllowed(extension))
            return (ServiceResult.Fail("File", "Attach PDF, Word, Excel, PowerPoint or PNG / JPG image files only."), null);
        if (length <= 0) return (ServiceResult.Fail("File", "This file is empty."), null);
        if (length > TaskAttachmentRules.MaxBytes)
            return (ServiceResult.Fail("File", $"Files can be at most {TaskAttachmentRules.SizeLabel(TaskAttachmentRules.MaxBytes)}."), null);

        // Read at most one byte more than allowed: the declared length is not trusted either.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > TaskAttachmentRules.MaxBytes)
                return (ServiceResult.Fail("File", $"Files can be at most {TaskAttachmentRules.SizeLabel(TaskAttachmentRules.MaxBytes)}."), null);
        }
        if (buffer.Length == 0) return (ServiceResult.Fail("File", "This file is empty."), null);
        var head = buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 4096));
        if (TaskAttachmentRules.CheckContent(extension, head, buffer) is { } invalid) return (ServiceResult.Fail("File", invalid), null);

        await SweepStagedAsync(store, ct);
        var row = new TaskAttachment
        {
            StagingKey = Guid.NewGuid(),
            FileName = TaskAttachmentRules.CleanName(fileName, extension),
            Extension = extension,
            SizeBytes = buffer.Length,
            StoredName = Guid.NewGuid().ToString("N"),
            Sha256 = TaskAttachmentRules.Sha256Hex(buffer),
            UploadedAt = DateTime.UtcNow,
            UploadedBy = currentUser.Name,
        };
        await store.SaveAsync(row.StoredName, buffer, ct);
        try
        {
            db.TaskAttachments.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            store.Delete(row.StoredName);
            throw;
        }
        return (ServiceResult.Ok(row.Id), View(row));
    }

    public async Task<IReadOnlyList<TaskAttachmentView>> StagedAttachmentsAsync(IEnumerable<Guid> keys, CancellationToken ct = default)
    {
        var list = keys.Distinct().ToList();
        if (list.Count == 0) return [];
        var rows = await db.TaskAttachments.AsNoTracking().Where(a => a.TaskId == null && list.Contains(a.StagingKey)).ToListAsync(ct);
        return rows.OrderBy(a => list.IndexOf(a.StagingKey)).Select(View).ToList();
    }

    public async Task<IReadOnlyList<TaskAttachmentView>> AttachmentsAsync(int taskId, CancellationToken ct = default) =>
        (await db.TaskAttachments.AsNoTracking().Where(a => a.TaskId == taskId).OrderBy(a => a.UploadedAt).ThenBy(a => a.Id).ToListAsync(ct))
            .Select(View).ToList();

    public async Task<TaskAttachmentFile?> OpenAttachmentAsync(int taskId, int attachmentId, bool download, CancellationToken ct = default)
    {
        // Only a file of this task, never a pending upload or another task's file.
        var row = await db.TaskAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.TaskId == taskId, ct);
        if (row is null || files?.Open(row.StoredName) is not { } stream) return null;
        return new TaskAttachmentFile(row.FileName, TaskAttachmentRules.ContentType(row.Extension), !download && TaskAttachmentRules.OpensInBrowser(row.Extension), stream);
    }

    private static TaskAttachmentView View(TaskAttachment a) =>
        new(a.Id, a.TaskId, a.StagingKey, a.FileName, a.Extension, a.SizeBytes, a.UploadedAt, a.UploadedBy);

    private async Task<List<TaskAttachment>> StagedRowsAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var list = keys.Distinct().ToList();
        return list.Count == 0 ? [] : await db.TaskAttachments.Where(a => a.TaskId == null && list.Contains(a.StagingKey)).ToListAsync(ct);
    }

    private static string? AttachmentError(int found, int requested, int existing = 0)
    {
        if (found < requested) return "An uploaded file has expired or was already used. Remove it and upload it again.";
        if (existing + found > TaskAttachmentRules.MaxPerTask) return $"A task can have at most {TaskAttachmentRules.MaxPerTask} attachments.";
        return null;
    }

    /// <summary>Uploads whose form was never saved: rows and files older than <see cref="StagingLifetime"/>.</summary>
    private async Task SweepStagedAsync(ITaskFileStore store, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - StagingLifetime;
        var stale = await db.TaskAttachments.Where(a => a.TaskId == null && a.UploadedAt < cutoff).ToListAsync(ct);
        if (stale.Count == 0) return;
        db.TaskAttachments.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        foreach (var a in stale) store.Delete(a.StoredName);
    }

    private void DeleteFiles(IEnumerable<TaskAttachment> rows)
    {
        if (files is null) return;
        foreach (var a in rows) files.Delete(a.StoredName);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(ServiceResult Result, (string Executor, string? Reviewer) People)> ValidateAsync(TaskInput input, ManagementTask? existing, CancellationToken ct)
    {
        var result = ServiceResult.Ok();
        string executor = "", reviewer = "";
        if (input.AssignedToId is not { } executorId) result.AddError("AssignedToId", "Choose who will carry out the task.");
        else
        {
            executor = await NameOfAsync(executorId, ct);
            if (executor.Length == 0) result.AddError("AssignedToId", "Choose a staff member from the list.");
            // A new assignee must still be on staff (contract not ended); an existing assignment is kept as it is.
            else if (existing?.AssignedToId != executorId && await ContractEndedAsync(executorId, ct) is { } ended)
                result.AddError("AssignedToId", $"{executor}'s contract ended on {ended.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}. Choose a current staff member.");
        }
        // A task created for a department: the department must exist and the executor must be one of its members. Checked
        // here, against the database, whatever the browser sent.
        if (existing is null && input.DepartmentId is { } deptId)
        {
            var dept = await db.Departments.AsNoTracking().Where(d => d.Id == deptId).Select(d => d.Name).FirstOrDefaultAsync(ct);
            if (dept is null) result.AddError("DepartmentId", "Choose a department from the list.");
            else if (input.AssignedToId is { } assignee && executor.Length > 0
                     && !await db.StaffMembers.AsNoTracking().AnyAsync(s => s.Id == assignee && s.DepartmentId == deptId, ct))
                result.AddError("AssignedToId", $"{executor} is not a member of the {dept} department. Choose a member of this department.");
        }
        if (input.ReviewerId is { } reviewerId)
        {
            reviewer = await NameOfAsync(reviewerId, ct);
            if (reviewer.Length == 0) result.AddError("ReviewerId", "Choose a staff member from the list.");
            else if (reviewerId == input.AssignedToId) result.AddError("ReviewerId", "Choose a reviewer other than the person carrying out the task, or leave it empty.");
        }

        var title = TextNormalizer.CleanText(input.Title);
        if (title.Length == 0) result.AddError("Title", "Enter the task title.");
        else if (title.Length > ManagementTask.TitleMaxLength) result.AddError("Title", $"Use {ManagementTask.TitleMaxLength} characters or fewer.");
        if ((input.Description?.Trim().Length ?? 0) > ManagementTask.TextMaxLength) result.AddError("Description", $"Use {ManagementTask.TextMaxLength} characters or fewer.");
        if ((input.Notes?.Trim().Length ?? 0) > ManagementTask.TextMaxLength) result.AddError("Notes", $"Use {ManagementTask.TextMaxLength} characters or fewer.");
        if (!Enum.IsDefined(input.Status) || (input.Status == ManagementTaskStatus.OnHold && existing?.Status != ManagementTaskStatus.OnHold))
            result.AddError("Status", "Choose a status from the list.");
        if (!Enum.IsDefined(input.Priority)) result.AddError("Priority", "Choose a priority from the list.");

        var today = SchoolWeek.Today;
        if (input.DueDate is { } due)
        {
            var dueChanged = existing is null || existing.DueDate != due;
            if (due < EarliestDue || due > today.AddYears(MaxYearsAhead))
                result.AddError("DueDate", $"Enter a due date between {EarliestDue.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} and {MaxYearsAhead} years from today.");
            else if (dueChanged && due < today && !TaskLabels.IsClosed(input.Status))
                result.AddError("DueDate", "The due date cannot be in the past.");
        }
        else if (input.DueTime is not null) result.AddError("DueTime", "Choose a due date for this time, or clear the time.");

        return (result, (executor, input.ReviewerId is null ? null : reviewer));
    }

    private async Task<DateOnly?> ContractEndedAsync(int staffId, CancellationToken ct)
    {
        var today = SchoolWeek.Today;
        return await db.StaffMembers.AsNoTracking().Where(s => s.Id == staffId && s.ContractEndDate != null && s.ContractEndDate < today)
            .Select(s => s.ContractEndDate).FirstOrDefaultAsync(ct);
    }

    private async Task<string> NameOfAsync(int staffId, CancellationToken ct) =>
        await db.StaffMembers.AsNoTracking().Where(s => s.Id == staffId).Select(s => s.FullName).FirstOrDefaultAsync(ct) ?? "";

    private static void Apply(ManagementTask task, TaskInput input)
    {
        string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        task.AssignedToId = input.AssignedToId!.Value;
        task.ReviewerId = input.ReviewerId;
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

    private void Log(ManagementTask task, int staffId, TaskHistoryAction action, string summary) =>
        db.ManagementTaskHistory.Add(new ManagementTaskHistory
        {
            TaskId = task.Id,
            StaffMemberId = staffId,
            TaskTitle = task.Title,
            Action = action,
            StatusAfter = task.Status,
            Summary = summary.Length > ManagementTaskHistory.SummaryMaxLength ? summary[..ManagementTaskHistory.SummaryMaxLength] : summary,
            ChangedAt = DateTime.UtcNow,
            ChangedBy = currentUser.Name,
        });

    private static (DateOnly Today, TimeOnly Time) Now()
    {
        // Due dates and times are school-local: compare with school time (Africa/Cairo).
        var now = SchoolTime.Now;
        return (DateOnly.FromDateTime(now), TimeOnly.FromDateTime(now));
    }

    private static System.Linq.Expressions.Expression<Func<ManagementTask, bool>> OverduePredicate(DateOnly today, TimeOnly time) =>
        t => t.Status != ManagementTaskStatus.Completed && t.Status != ManagementTaskStatus.Cancelled && t.DueDate != null
             && (t.DueDate < today || (t.DueDate == today && t.DueTime != null && t.DueTime < time));

    /// <summary>
    /// The Tasks page tabs. Overdue: open and past its due moment. Due Today: open, due today and not yet overdue.
    /// Upcoming: open and due after today. Completed: completed. Open tasks without a due date are only under All.
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<ManagementTask, bool>> TabPredicate(string tab, DateOnly today, TimeOnly time) => tab switch
    {
        "overdue" => OverduePredicate(today, time),
        "today" => t => t.Status != ManagementTaskStatus.Completed && t.Status != ManagementTaskStatus.Cancelled && t.DueDate == today
                        && (t.DueTime == null || t.DueTime >= time),
        "upcoming" => t => t.Status != ManagementTaskStatus.Completed && t.Status != ManagementTaskStatus.Cancelled && t.DueDate > today,
        "completed" => t => t.Status == ManagementTaskStatus.Completed,
        _ => t => true,
    };

    /// <summary>The people / department part of the filters (also the scope of the summary counts).</summary>
    private IQueryable<ManagementTask> Scope(TaskListQuery q)
    {
        var query = db.ManagementTasks.AsNoTracking();
        if (q.MemberId is { } mid) query = query.Where(t => t.AssignedToId == mid);
        if (q.ReviewerId is { } rid) query = query.Where(t => t.ReviewerId == rid);
        if (q.Department == "management") query = query.Where(t => t.AssignedTo is ManagementMember);
        else if (int.TryParse(q.Department, out var dept)) query = query.Where(t => t.AssignedTo.DepartmentId == dept);
        // Staff category (StaffCategories.Of): Management Team; otherwise a teaching job title (or none) is academic.
        query = StaffCategories.Parse(q.Category) switch
        {
            StaffCategory.Management => query.Where(t => t.AssignedTo is ManagementMember),
            StaffCategory.Academic => query.Where(t => !(t.AssignedTo is ManagementMember)
                && (t.AssignedTo.JobTitle == null || t.AssignedTo.JobTitle.Trim() == "" || t.AssignedTo.JobTitle.Contains("teacher"))),
            StaffCategory.Support => query.Where(t => !(t.AssignedTo is ManagementMember)
                && t.AssignedTo.JobTitle != null && t.AssignedTo.JobTitle.Trim() != "" && !t.AssignedTo.JobTitle.Contains("teacher")),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(q.Mine))
        {
            // Without a "Working as" staff member there is no "me": nothing matches.
            var me = currentUser.StaffId ?? -1;
            query = q.Mine switch
            {
                "assigned" => query.Where(t => t.AssignedToId == me),
                "created" => query.Where(t => t.CreatedById == me),
                "review" => query.Where(t => t.ReviewerId == me),
                _ => query,
            };
        }
        return query;
    }

    private IQueryable<ManagementTask> Filter(TaskListQuery q)
    {
        var (today, time) = Now();
        var query = Scope(q);

        var term = q.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(t => t.Title.Contains(term) || (t.Description != null && t.Description.Contains(term))
                                     || t.AssignedTo.FullName.Contains(term) || (t.Reviewer != null && t.Reviewer.FullName.Contains(term)));

        query = q.Status switch
        {
            "todo" or "pending" => query.Where(t => t.Status == ManagementTaskStatus.ToDo),
            "in-progress" => query.Where(t => t.Status == ManagementTaskStatus.InProgress),
            "on-hold" => query.Where(t => t.Status == ManagementTaskStatus.OnHold),
            "completed" => query.Where(t => t.Status == ManagementTaskStatus.Completed),
            "cancelled" => query.Where(t => t.Status == ManagementTaskStatus.Cancelled),
            "open" => query.Where(t => t.Status != ManagementTaskStatus.Completed && t.Status != ManagementTaskStatus.Cancelled),
            "overdue" => query.Where(OverduePredicate(today, time)),
            _ => query,
        };

        if (TaskLabels.ParsePriority(q.Priority) is { } priority) query = query.Where(t => t.Priority == priority);

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
        if (q.DueFrom is { } from) query = query.Where(t => t.DueDate >= from);
        if (q.DueTo is { } to) query = query.Where(t => t.DueDate <= to);
        if (TaskTabs.Normalize(q.Tab) is { } tab) query = query.Where(TabPredicate(tab, today, time));
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
            "oldest" => query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
            "title" => desc ? query.OrderByDescending(t => t.Title) : query.OrderBy(t => t.Title),
            "status" => desc ? query.OrderByDescending(t => t.Status).ThenBy(t => t.DueDate) : query.OrderBy(t => t.Status).ThenBy(t => t.DueDate),
            // Due date: completed and cancelled tasks last, tasks without a due date after dated ones.
            _ => desc
                ? query.OrderBy(t => t.Status == ManagementTaskStatus.Completed || t.Status == ManagementTaskStatus.Cancelled).ThenBy(t => t.DueDate == null).ThenByDescending(t => t.DueDate).ThenByDescending(t => t.DueTime)
                : query.OrderBy(t => t.Status == ManagementTaskStatus.Completed || t.Status == ManagementTaskStatus.Cancelled).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.DueTime).ThenByDescending(t => t.Priority),
        };
    }

    private static IQueryable<TaskRow> Project(IQueryable<ManagementTask> query) =>
        query.Select(t => new TaskRow(t.Id, t.Title, t.Description, t.Notes,
            new TaskPerson(t.AssignedTo.Id, t.AssignedTo.FullName, t.AssignedTo.JobTitle, t.AssignedTo.Department != null ? t.AssignedTo.Department.Name : null,
                t.AssignedTo is ManagementMember ? ((ManagementMember)t.AssignedTo).Position : null, t.AssignedTo is ManagementMember),
            t.Reviewer == null ? null : new TaskPerson(t.Reviewer.Id, t.Reviewer.FullName, t.Reviewer.JobTitle, t.Reviewer.Department != null ? t.Reviewer.Department.Name : null,
                t.Reviewer is ManagementMember ? ((ManagementMember)t.Reviewer).Position : null, t.Reviewer is ManagementMember),
            t.Creator == null ? null : new TaskPerson(t.Creator.Id, t.Creator.FullName, t.Creator.JobTitle, t.Creator.Department != null ? t.Creator.Department.Name : null,
                t.Creator is ManagementMember ? ((ManagementMember)t.Creator).Position : null, t.Creator is ManagementMember),
            t.Status, t.Priority, t.DueDate, t.DueTime, t.CompletedAt, t.CreatedAt, t.UpdatedAt, t.CreatedBy, t.UpdatedBy, t.Attachments.Count()));
}
