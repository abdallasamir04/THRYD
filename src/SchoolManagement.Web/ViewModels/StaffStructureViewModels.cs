using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

// ---------- Landing pages (Staff → Academic Staff / Management Team) ----------

/// <summary>A large navigation card on a landing page. The whole card is the link.</summary>
public sealed record HubCard(string Href, string Icon, string Title, string Description, string? Meta = null, string Tone = "violet");

public sealed class HubViewModel
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required IReadOnlyList<(string Label, string? Href)> Crumbs { get; init; }
    public required IReadOnlyList<HubCard> Cards { get; init; }
    public bool Large { get; init; }

    /// <summary>Extra layout modifier for the card grid, e.g. "three" → hub--three (three large cards in a row).</summary>
    public string? Variant { get; init; }

    /// <summary>Sidebar item to highlight.</summary>
    public string Nav { get; init; } = "Staff";
    public IReadOnlyList<(string Label, string Href, string Icon)> Extra { get; init; } = [];
}

// ---------- Academic departments ----------

public sealed class DepartmentIndexViewModel
{
    public required DepartmentListQuery Query { get; init; }
    public required IReadOnlyList<DepartmentCard> Departments { get; init; }
    public required int TotalDepartments { get; init; }
    public required int WithHead { get; init; }
    public required int TotalMembers { get; init; }
}

public sealed class DepartmentDetailsViewModel
{
    public required DepartmentCard Department { get; init; }
    public required DepartmentMemberQuery Query { get; init; }
    public required IReadOnlyList<DepartmentMember> Members { get; init; }
    public required IReadOnlyList<HeadHistoryRow> HeadHistory { get; init; }

    /// <summary>The P.E. module, shown under Department Staff on the PE department page only (null elsewhere).</summary>
    public PePageViewModel? Pe { get; init; }
}

public sealed class DepartmentEditModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [Display(Name = "Description")]
    [StringLength(Department.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    [Display(Name = "Subcategory label")]
    [StringLength(50, ErrorMessage = "Use {1} characters or fewer.")]
    public string? SubcategoryLabel { get; set; }

    [Display(Name = "Subject label")]
    [StringLength(50, ErrorMessage = "Use {1} characters or fewer.")]
    public string? SubjectLabel { get; set; }
}

/// <summary>Departments → Add department. The same details as Edit, plus the name (fixed after creation).</summary>
public sealed class DepartmentCreateModel
{
    [Display(Name = "Department name")]
    [Required(ErrorMessage = "Enter the department name.")]
    [StringLength(Department.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Name { get; set; }

    [Display(Name = "Description")]
    [StringLength(Department.DescriptionMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    [Display(Name = "Subcategory label")]
    [StringLength(50, ErrorMessage = "Use {1} characters or fewer.")]
    public string? SubcategoryLabel { get; set; }

    [Display(Name = "Subject label")]
    [StringLength(50, ErrorMessage = "Use {1} characters or fewer.")]
    public string? SubjectLabel { get; set; }
}

public sealed class HeadsViewModel
{
    public required IReadOnlyList<DepartmentCard> Departments { get; init; }
    public required IReadOnlyList<HeadHistoryRow> Recent { get; init; }
    public string? Filter { get; init; }
}

public sealed class SubcategoriesViewModel
{
    public required DepartmentCard Department { get; init; }
    public required IReadOnlyList<SubcategoryItem> Items { get; init; }
    public string ItemNoun => Department.Name == "Second Language" ? "language" : "option";
}

public sealed class SubcategoryFormModel
{
    public int? Id { get; set; }
    public int DepartmentId { get; set; }
    public int? ParentId { get; set; }

    [Display(Name = "Name")]
    [Required(ErrorMessage = "Enter a name.")]
    [StringLength(DepartmentSubcategory.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Name { get; set; } = "";

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    public string DepartmentName { get; set; } = "";
    public string Noun { get; set; } = "option";
    public IReadOnlyList<SubcategoryItem> Parents { get; set; } = [];
    public bool IsEdit => Id is > 0;
}

public sealed class AssignHeadModel
{
    public int DepartmentId { get; set; }

    [Display(Name = "Head of Department")]
    [Required(ErrorMessage = "Choose the Head of Department.")]
    public int? TeacherId { get; set; }

    [Display(Name = "Start date")]
    [Required(ErrorMessage = "Enter the date the appointment starts.")]
    public DateOnly? StartDate { get; set; }

    [Display(Name = "Note")]
    [StringLength(DepartmentHeadAssignment.NoteMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Note { get; set; }

    public DepartmentCard? Department { get; set; }
    public IReadOnlyList<DepartmentMember> Candidates { get; set; } = [];
}

// ---------- All School Staff ----------

public sealed class StaffDirectoryViewModel
{
    public required StaffDirectoryQuery Query { get; init; }
    public required StaffDirectory Directory { get; init; }
}

// ---------- Management Team ----------

/// <summary>
/// Staff → Management Team → School Leadership: one section with the members and the seven positions. It reads the
/// same services as the former Team Members and Management Positions pages.
/// </summary>
public sealed class LeadershipViewModel
{
    /// <summary>Values of the former <c>?tab=</c> links, still accepted (they redirect to #members / #positions).</summary>
    public const string MembersTab = "members";
    public const string PositionsTab = "positions";

    public required IReadOnlyList<ManagementMemberRow> AllMembers { get; init; }
    public required IReadOnlyList<PositionSlot> Positions { get; init; }
    public required TaskCounts Tasks { get; init; }

    /// <summary>Members: the search / position filter result.</summary>
    public IReadOnlyList<ManagementMemberRow> Members { get; init; } = [];
    public string? Search { get; init; }
    public string? Position { get; init; }

    /// <summary>Positions: all, filled or vacant.</summary>
    public string? Status { get; init; }
    public IReadOnlyList<PositionHistoryRow> History { get; init; } = [];

    public int Filled => Positions.Count(p => p.Holder is not null);
    public int Vacant => Positions.Count - Filled;
    public int Unpositioned => AllMembers.Count(m => m.Position is null);
}

/// <summary>School Leadership → one position: its holder, the holder's tasks and the position's appointment history.</summary>
public sealed class LeadershipPositionViewModel
{
    public required PositionSlot Slot { get; init; }
    public required IReadOnlyList<PositionHistoryRow> History { get; init; }

    /// <summary>The current holder's tasks (null while the position is vacant).</summary>
    public TaskIndexViewModel? Tasks { get; init; }
    public ManagementPosition Position => Slot.Position;
    public string Name => ManagementPositions.Name(Slot.Position);
}

public sealed class AssignPositionModel
{
    public ManagementPosition Position { get; set; }

    [Display(Name = "Member")]
    public int? MemberId { get; set; }

    [Display(Name = "Start date")]
    [Required(ErrorMessage = "Enter the date the appointment starts.")]
    public DateOnly? StartDate { get; set; }

    public ManagementMemberRow? Current { get; set; }
    public IReadOnlyList<ManagementMemberOption> Members { get; set; } = [];
}

public sealed class ManagementMemberFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Full name")]
    [Required(ErrorMessage = "Enter the member's full name.")]
    [StringLength(StaffMember.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    [Remote("CheckDuplicate", "Management", AdditionalFields = "ContactNumber,Id", ErrorMessage = "This person is already on the Management Team.")]
    public string FullName { get; set; } = "";

    [Display(Name = "Position")]
    public ManagementPosition? Position { get; set; }

    [Display(Name = "Contact number")]
    [StringLength(StaffMember.ContactMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    [RegularExpression(@"^\s*\+?[0-9٠-٩۰-۹][0-9٠-٩۰-۹ ()\-.]*[0-9٠-٩۰-۹]\s*$",
        ErrorMessage = "Use digits, spaces or dashes, with an optional + at the start.")]
    public string? ContactNumber { get; set; }

    [Display(Name = "Email")]
    [StringLength(ManagementMember.EmailMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string? Email { get; set; }

    [Display(Name = "Appointed on")]
    public DateOnly? AppointedOn { get; set; }

    /// <summary>Positions already held by someone else (position → holder name).</summary>
    public IReadOnlyDictionary<ManagementPosition, string> Holders { get; set; } = new Dictionary<ManagementPosition, string>();
    public bool IsEdit => Id is > 0;

    public ManagementMemberInput ToInput() => new()
    {
        FullName = FullName, Position = Position, ContactNumber = ContactNumber, Email = Email, AppointedOn = AppointedOn,
    };
}

public sealed class ManagementProfileViewModel
{
    public required ManagementMemberRow Member { get; init; }
    public required TaskCounts Counts { get; init; }
    public required TaskIndexViewModel Tasks { get; init; }
    public required IReadOnlyList<TaskHistoryRow> History { get; init; }
    public IReadOnlyList<PositionHistoryRow> Positions { get; init; } = [];
}

// ---------- Management Team tasks ----------

public sealed class TaskIndexViewModel
{
    public required TaskListQuery Query { get; init; }
    public required PagedResult<TaskRow> Result { get; init; }
    public required TaskCounts Counts { get; init; }

    /// <summary>Every staff member (for the Assigned to / Submitted to filters on the Tasks page).</summary>
    public IReadOnlyList<TaskStaffOption> Staff { get; init; } = [];
    public IReadOnlyList<TaskDepartmentOption> Departments { get; init; } = [];

    /// <summary>The "Working as" staff member (for Assigned to me / Created by me), if one is chosen.</summary>
    public int? MeId { get; init; }

    /// <summary>The list's own URL (the Tasks page, or a member's profile) — used by filters, chips and paging.</summary>
    public string BaseUrl { get; init; } = "/management/tasks";

    /// <summary>True on a member's profile: the member column and filter are not shown.</summary>
    public bool ForMember => Query.MemberId is not null && BaseUrl != "/management/tasks";
}

/// <summary>New / edit task form: task information, assignment (creator, executor, reviewer), schedule, attachments, notes.</summary>
public sealed class TaskFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Assigned to")]
    [Required(ErrorMessage = "Choose who will carry out the task.")]
    public int? AssignedToId { get; set; }

    [Display(Name = "Submitted to / Reviewer")]
    public int? ReviewerId { get; set; }

    [Display(Name = "Title")]
    [Required(ErrorMessage = "Enter the task title.")]
    [StringLength(ManagementTask.TitleMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Title { get; set; } = "";

    [Display(Name = "Description / Instructions")]
    [StringLength(ManagementTask.TextMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Description { get; set; }

    [Display(Name = "Notes")]
    [StringLength(ManagementTask.TextMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    [Display(Name = "Status")]
    public ManagementTaskStatus Status { get; set; } = ManagementTaskStatus.ToDo;

    [Display(Name = "Priority")]
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    [Display(Name = "Due date")]
    public DateOnly? DueDate { get; set; }

    [Display(Name = "Due time")]
    public TimeOnly? DueTime { get; set; }

    /// <summary>Files uploaded in this form and not saved yet (their staging keys).</summary>
    public List<Guid> AttachmentKeys { get; set; } = [];

    /// <summary>Edit: saved attachments the user removed.</summary>
    public List<int> RemoveAttachmentIds { get; set; } = [];

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public IReadOnlyList<TaskStaffOption> Staff { get; set; } = [];

    /// <summary>Saved attachments (edit) and uploads waiting for this form, shown in the attachment list.</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public IReadOnlyList<TaskAttachmentView> Attachments { get; set; } = [];
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public IReadOnlyList<TaskAttachmentView> Staged { get; set; } = [];

    /// <summary>Created by: the saved creator (edit) or the "Working as" staff member who is creating it.</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public TaskPerson? Creator { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public string? CreatorName { get; set; }

    /// <summary>Edit: the status the task had when the form opened (On Hold is only offered to such a task).</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public ManagementTaskStatus? SavedStatus { get; set; }

    public bool IsEdit => Id is > 0;

    public TaskInput ToInput() => new()
    {
        AssignedToId = AssignedToId, ReviewerId = ReviewerId, Title = Title, Description = Description, Notes = Notes,
        Status = Status, Priority = Priority, DueDate = DueDate, DueTime = DueTime,
        AttachmentKeys = AttachmentKeys, RemoveAttachmentIds = RemoveAttachmentIds,
    };
}

/// <summary>One task: everything the executor needs, with its attachments and history.</summary>
public sealed class TaskDetailsViewModel
{
    public required TaskRow Task { get; init; }
    public required IReadOnlyList<TaskAttachmentView> Attachments { get; init; }
    public required IReadOnlyList<TaskHistoryRow> History { get; init; }
    public string? Remaining { get; init; }
}
