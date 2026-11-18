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
/// <summary>Add Department: the name and, optionally, the HOD. Description and labels are edited later on the department's page.</summary>
public sealed class DepartmentCreateModel
{
    [Display(Name = "Department Name")]
    [Required(ErrorMessage = "Enter the department name.")]
    [StringLength(Department.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Name { get; set; }

    [Display(Name = "HOD (Optional)")]
    public int? HeadTeacherId { get; set; }

    public IReadOnlyList<HodCandidate> Candidates { get; set; } = [];
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
    public int Unpositioned => AllMembers.Count(m => !m.HasPosition);
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

    /// <summary>The Position dropdown's value (see <see cref="PositionChoices"/>): a leadership or school-added position.</summary>
    public string? PositionChoice { get; set; }

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
    public IReadOnlyList<CustomPositionOption> CustomPositions { get; set; } = [];
    public bool IsEdit => Id is > 0;

    /// <summary>The input, or null when the chosen position is not one the form offers.</summary>
    public ManagementMemberInput? ToInput()
    {
        var input = new ManagementMemberInput { FullName = FullName, ContactNumber = ContactNumber, Email = Email, AppointedOn = AppointedOn };
        return PositionChoices.Apply(PositionChoice, Position, null, input) ? input : null;
    }
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

    /// <summary>The central Tasks page (Daily Operations → Tasks): tabs, totals, staff category and due-date range.</summary>
    public bool IsGlobal => BaseUrl == "/tasks";

    /// <summary>Tasks page: tab totals with the current filters, and the school-wide totals shown at the top.</summary>
    public TaskTabCounts? Tabs { get; init; }
    public TaskTabCounts? Totals { get; init; }

    /// <summary>True on a member's profile: the member column and filter are not shown.</summary>
    public bool ForMember => Query.MemberId is not null && BaseUrl is not ("/management/tasks" or "/tasks");
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

    /// <summary>
    /// Add Task from Academic Departments: the form asks for a department and offers only its members as the executor
    /// (the reviewer can still be anyone). <see cref="DepartmentLocked"/>: opened from a department's own page, which
    /// fixes the department. The server checks the department and the executor's membership on every save.
    /// </summary>
    public bool DepartmentMode { get; set; }
    public bool DepartmentLocked { get; set; }

    [Display(Name = "Department")]
    public int? DepartmentId { get; set; }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public IReadOnlyList<TaskDepartmentOption> Departments { get; set; } = [];

    /// <summary>The executor options: every staff member, or in department mode only the chosen department's members.</summary>
    public IReadOnlyList<TaskStaffOption> Assignees => DepartmentMode ? Staff.Where(s => DepartmentId is { } d && s.DepartmentId == d).ToList() : Staff;

    public string? DepartmentName => Departments.FirstOrDefault(d => d.Id == DepartmentId)?.Name;

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
        Status = Status, Priority = Priority, DueDate = DueDate, DueTime = DueTime, DepartmentId = DepartmentId,
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

/// <summary>The options of a Position dropdown: the seven leadership positions, the school-added ones and, optionally, + Add New Position.</summary>
public sealed record PositionOptions(string? Selected, IReadOnlyDictionary<ManagementPosition, string> Holders, IReadOnlyList<CustomPositionOption> Custom, bool AllowNew);

// ---------- Add Staff (one form for Academic staff and the Management Team) ----------

public enum NewStaffCategory { Academic = 1, Management = 2 }

/// <summary>
/// Staff → Add Staff Member. Academic staff are saved through the existing teacher rules (department, subcategory,
/// subject); Management Team members through the existing Management Team rules (position, email). Both then get their
/// contract, probation, employment type and working hours (StaffEmploymentService). Only the chosen category's fields apply.
/// </summary>
public sealed class StaffCreateModel
{
    [Display(Name = "Staff category")]
    [Required(ErrorMessage = "Choose Academic or Management.")]
    public NewStaffCategory? Category { get; set; }

    [Display(Name = "Full name")]
    [Required(ErrorMessage = "Enter the staff member's full name.")]
    [StringLength(StaffMember.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string FullName { get; set; } = "";

    [Display(Name = "Contact number")]
    [StringLength(StaffMember.ContactMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    [RegularExpression(@"^\s*\+?[0-9٠-٩۰-۹][0-9٠-٩۰-۹ ()\-.]*[0-9٠-٩۰-۹]\s*$",
        ErrorMessage = "Use digits, spaces or dashes, with an optional + at the start.")]
    public string? ContactNumber { get; set; }

    // Academic
    public int? DepartmentId { get; set; }
    [StringLength(50)] public string? Subcategory { get; set; }
    [StringLength(50)] public string? Subject { get; set; }
    [Display(Name = "Job title / Position")]
    [StringLength(StaffMember.JobTitleMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? JobTitle { get; set; }

    // Management: a leadership position, a school-added one, or + Add New Position (PositionChoice, see PositionChoices).
    public ManagementPosition? Position { get; set; }
    public string? PositionChoice { get; set; }
    [Display(Name = "New Position Title")]
    [StringLength(ManagementCustomPosition.TitleMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? NewPositionTitle { get; set; }
    [Display(Name = "Email")]
    [StringLength(ManagementMember.EmailMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string? Email { get; set; }
    public DateOnly? AppointedOn { get; set; }

    // Contract
    [Display(Name = "Contract start date")]
    public DateOnly? ContractStartDate { get; set; }
    [Display(Name = "Contract end date")]
    public DateOnly? ContractEndDate { get; set; }
    /// <summary>30, 60 or 90; empty = no probation period.</summary>
    [Display(Name = "Probation period")]
    public int? ProbationDays { get; set; }
    /// <summary>Empty = the contract start date.</summary>
    [Display(Name = "Probation start date")]
    public DateOnly? ProbationStartDate { get; set; }

    // Employment type and working hours
    [Display(Name = "Employment type")]
    public EmploymentType? EmploymentType { get; set; }
    public List<DayOfWeek> WorkingDays { get; set; } = [];
    [Display(Name = "Working hours")]
    public WorkingHoursType? WorkingHoursType { get; set; }
    /// <summary>"HH:mm".</summary>
    public string? WorkStart { get; set; }
    public string? WorkEnd { get; set; }

    /// <summary>The form was opened as a page (not in the modal): after saving, go to the new profile.</summary>
    public bool FullPage { get; set; }

    // Options
    public IReadOnlyList<Department> Departments { get; set; } = [];
    public IReadOnlyList<JobTitleCount> JobTitleSuggestions { get; set; } = [];
    public IReadOnlyDictionary<ManagementPosition, string> Holders { get; set; } = new Dictionary<ManagementPosition, string>();
    public IReadOnlyList<CustomPositionOption> CustomPositions { get; set; } = [];

    public static TimeOnly? Time(string? value) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;

    public SchoolManagement.Web.Services.StaffEmploymentInput Employment() => new()
    {
        ContractStartDate = ContractStartDate, ContractEndDate = ContractEndDate, ProbationDays = ProbationDays, ProbationStartDate = ProbationStartDate,
        EmploymentType = EmploymentType, WorkingDays = WorkingDays, WorkingHoursType = WorkingHoursType, WorkStart = Time(WorkStart), WorkEnd = Time(WorkEnd),
    };
}
