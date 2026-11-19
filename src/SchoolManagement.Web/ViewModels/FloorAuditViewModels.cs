using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

/// <summary>An area with what the pages show about it (classroom name = its class's name).</summary>
public sealed record FloorAreaInfo(
    int Id, int FloorId, string FloorName, FloorAreaKind Kind, int? ClassId, string Name, FacilityType? FacilityType, string? Description,
    bool IsActive, int SortOrder)
{
    public AuditAreaRef Ref => new(Id, FloorId, Kind, ClassId, IsActive);
    public string TypeLabel => Kind == FloorAreaKind.Classroom ? "Classroom" : FloorAuditLabels.Facility(FacilityType);
    public string Icon => Kind == FloorAreaKind.Classroom ? "door" : FloorAuditLabels.FacilityIcon(FacilityType);
}

/// <summary>The latest completed inspection of an area (any date): when, by whom, and its issues (the area's open issues).</summary>
public sealed record AreaLastInspection(int Id, DateOnly Date, DateTime CompletedAt, string? Inspector, int Issues, string? IssueSummary);

/// <summary>An inspection as lists show it.</summary>
public sealed record InspectionBrief(
    int Id, int AreaId, int FloorId, string FloorName, string AreaName, FloorAreaKind Kind, DateOnly Date, int SlotId, string SessionLabel,
    TimeOnly SessionStart, int Round, TimeOnly ScheduledTime, FloorInspectionStatus Status, DateTime StartedAt, DateTime? CompletedAt,
    string? Inspector, int Issues, string? IssueSummary, string? Observations)
{
    public bool IsCompleted => Status == FloorInspectionStatus.Completed;
    public AreaAuditStatus AreaStatus => FloorAuditSchedule.AreaStatus(true, Status, Issues, true);

    /// <summary>Minutes between the scheduled time and the actual completion (negative = early).</summary>
    public int? LateMinutes => CompletedAt is { } at
        ? (int)Math.Round((SchoolTime.Current.ToLocal(at) - Date.ToDateTime(ScheduledTime)).TotalMinutes)
        : null;
}

/// <summary>Progress of one round (school-wide or one floor).</summary>
public sealed record RoundProgress(int Round, TimeOnly Time, AuditRoundStatus Status, int Required, int Completed, int Started)
{
    public int Pending => Math.Max(0, Required - Completed);
    public int Percent => Required == 0 ? 0 : (int)Math.Round(Completed * 100.0 / Required);
    public bool HasAreas => Required > 0;
}

public sealed record SessionOverview(AuditSession Session, IReadOnlyList<RoundProgress> Rounds);

/// <summary>A floor card on the main page.</summary>
public sealed record FloorCard(
    int Id, string Name, int Classrooms, int Facilities, RoundProgress? Current, int RoundsCompleted, int RoundsPending, int RoundsMissed,
    int StudentsOutside, int OpenIssues, int InspectionsToday);

public sealed record PendingArea(int AreaId, string AreaName, string FloorName, int FloorId, string TypeLabel, AreaAuditStatus Status);

public sealed class FloorAuditHome
{
    public required DateOnly Date { get; init; }
    public required bool IsToday { get; init; }
    public required DateTime Now { get; init; }
    public string? AcademicYear { get; init; }
    public required AuditDaySchedule Schedule { get; init; }
    public AuditSession? Session { get; init; }
    public int Round { get; init; }
    public RoundProgress? Overall { get; init; }
    public required IReadOnlyList<FloorCard> Floors { get; init; }
    public required IReadOnlyList<SessionOverview> Sessions { get; init; }
    public required MovementCounts Movements { get; init; }
    public required IReadOnlyList<InspectionBrief> RecentIssues { get; init; }
    public required IReadOnlyList<PendingArea> PendingAreas { get; init; }
    public required FloorAuditUser User { get; init; }
    public int OpenIssues { get; init; }
    public int AreasConfigured { get; init; }

    /// <summary>The day's rounds (school-wide, with areas to inspect) by status.</summary>
    public IReadOnlyDictionary<AuditRoundStatus, int> RoundCounts { get; init; } = new Dictionary<AuditRoundStatus, int>();
    public int ClassroomsInspected { get; init; }
    public int FacilitiesInspected { get; init; }
}

/// <summary>Classroom &amp; Facility Inspections: every floor's areas for one session and round, to record audits from.</summary>
public sealed class InspectionsPage
{
    public required DateOnly Date { get; init; }
    public required bool IsToday { get; init; }
    public required AuditDaySchedule Schedule { get; init; }
    public AuditSession? Session { get; init; }
    public int Round { get; init; }
    public int? FloorId { get; init; }
    public bool PendingOnly { get; init; }
    public required IReadOnlyList<FloorPage> Floors { get; init; }
    public required IReadOnlyList<(int Id, string Name)> AllFloors { get; init; }
    public required FloorAuditUser User { get; init; }
    public IReadOnlyList<RoundProgress> Rounds { get; init; } = [];
}

/// <summary>An area card on a floor page, for the selected session and round.</summary>
public sealed record AreaCard(
    FloorAreaInfo Area, AreaAuditStatus Status, bool Required, int? InspectionId, int RoundsDone, int RoundsRequired,
    AreaLastInspection? Last, bool CanInspect)
{
    public int OpenIssues => Last?.Issues ?? 0;
}

public sealed class FloorPage
{
    public required SchoolFloor Floor { get; init; }
    public required IReadOnlyList<(int Id, string Name)> AllFloors { get; init; }
    public required DateOnly Date { get; init; }
    public required bool IsToday { get; init; }
    public required DateTime Now { get; init; }
    public string? AcademicYear { get; init; }
    public required AuditDaySchedule Schedule { get; init; }
    public AuditSession? Session { get; init; }
    public AuditSession? CurrentSession { get; init; }
    public int Round { get; init; }
    public IReadOnlyList<RoundProgress> Rounds { get; init; } = [];
    public RoundProgress? Progress => Rounds.FirstOrDefault(r => r.Round == Round);
    public required IReadOnlyList<AreaCard> Classrooms { get; init; }
    public required IReadOnlyList<AreaCard> Facilities { get; init; }
    public required IReadOnlyList<MovementRow> Movements { get; init; }
    public required IReadOnlyList<InspectionBrief> Recent { get; init; }
    public required FloorAuditUser User { get; init; }
    public int OverdueMinutes { get; init; }
}

/// <summary>The inspection form: an area in one round of one session, with its checklist.</summary>
public sealed class InspectionFormModel
{
    public int AreaId { get; set; }
    public string? Date { get; set; }
    public int SlotId { get; set; }
    public int Round { get; set; }
    public string? Observations { get; set; }

    /// <summary>Answers by checklist item id.</summary>
    public Dictionary<int, string?> Answers { get; set; } = [];

    /// <summary>Required for a manager correcting a completed inspection.</summary>
    public string? Reason { get; set; }

    /// <summary>Findings beyond the checklist ("+ Add Observation / Issue"), stored with the inspection's answers.</summary>
    public List<ExtraFinding> Extras { get; set; } = [];

    // ---- shown, not posted ----
    public FloorAreaInfo? Area { get; set; }
    public AuditSession? Session { get; set; }
    public int? InspectionId { get; set; }
    public FloorInspectionStatus? Status { get; set; }
    public IReadOnlyList<ChecklistQuestion> Questions { get; set; } = [];
    public IReadOnlyList<InspectionAttachmentView> Attachments { get; set; } = [];
    public string? StartedBy { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public TimeOnly? ScheduledTime { get; set; }
    public bool ReadOnly { get; set; }
    public string? ReadOnlyReason { get; set; }
    public bool IsCorrection { get; set; }
    public string? FloorName { get; set; }

    /// <summary>Who the inspection is (or will be) recorded under.</summary>
    public string? InspectorName { get; set; }
}

/// <summary>An additional observation or issue recorded with an inspection, optionally about one checklist item.</summary>
public sealed class ExtraFinding
{
    public const string Observation = "observation";
    public const string Issue = "issue";
    public const int MaxPerInspection = 20;

    public string? Kind { get; set; } = Observation;

    /// <summary>The checklist item it is about (its name), or empty for a general finding.</summary>
    public string? About { get; set; }
    public string? Text { get; set; }
    public bool IsIssue => Kind == Issue;
}

/// <summary>A checklist question: from the snapshot of a started inspection, or from the current items for a new one.</summary>
public sealed record ChecklistQuestion(int ItemId, string Name, string? Description, AuditResponseType Type, bool Required, string? Value, bool IsIssue)
{
    public IReadOnlyList<string> Choices => AuditResponses.Choices(Type);
}

public sealed record InspectionAttachmentView(int Id, string FileName, string Extension, long SizeBytes, DateTime UploadedAt, string? UploadedBy)
{
    public string Size => TaskAttachmentRules.SizeLabel(SizeBytes);
    public bool OpensInBrowser => TaskAttachmentRules.OpensInBrowser(Extension);
}

public sealed record InspectionResponseView(string ItemName, string? Description, AuditResponseType Type, bool Required, string? Value, bool IsIssue);

public sealed record AuditLogView(string Entity, int? EntityId, string Action, string Text, DateTime At, string? By);

public sealed class InspectionDetails
{
    public required InspectionBrief Inspection { get; init; }
    public string? AcademicYear { get; init; }
    public required IReadOnlyList<InspectionResponseView> Responses { get; init; }
    public required IReadOnlyList<InspectionAttachmentView> Attachments { get; init; }
    public required IReadOnlyList<AuditLogView> Log { get; init; }
    public string? CompletedBy { get; init; }
    public bool CanContinue { get; init; }
    public bool CanCorrect { get; init; }
}

public sealed class AreaPage
{
    public required FloorAreaInfo Area { get; init; }
    public AreaLastInspection? Last { get; init; }
    public required IReadOnlyList<InspectionBrief> History { get; init; }
    public required IReadOnlyList<AuditLogView> Log { get; init; }
    public required IReadOnlyList<(string Floor, DateTime Since)> FloorChanges { get; init; }
    public required FloorAuditUser User { get; init; }

    /// <summary>Today's current session and round for this area (Record Audit), or why it cannot be inspected now.</summary>
    public AuditSession? Session { get; init; }
    public int Round { get; init; }
    public bool CanRecord { get; init; }
    public string? RecordNote { get; init; }
    public AreaAuditStatus? RoundStatus { get; init; }
}

// ---------------------------------------------------------------- Student movements

public sealed record MovementRow(
    int Id, int StudentId, string StudentName, int ClassId, string ClassName, string? FromName, int? FromFloorId, string? FromFloor,
    string ToName, int? ToFloorId, string? ToFloor, bool Custom, DateOnly Date, DateTime ExitAt, DateTime? ReturnAt, MovementState State,
    TimeSpan Duration, string? RecordedBy, string? ReturnedBy, string? Notes, string? VoidReason, string? VoidedBy)
{
    public bool IsOpen => State is MovementState.Outside or MovementState.Overdue;
}

public sealed record DestinationCount(string Name, string? Floor, int Total, int Outside);

public sealed record MovementCounts(int Total, int Outside, int Returned, int Overdue, IReadOnlyList<DestinationCount> ByDestination, TimeSpan? AverageOutside)
{
    public static readonly MovementCounts Empty = new(0, 0, 0, 0, [], null);
}

/// <summary>
/// Student Access filters. <see cref="History"/> false: the live board (every open movement, any date);
/// true: closed movements (returned or voided) between <see cref="From"/> and <see cref="To"/>.
/// </summary>
public sealed record MovementFilter(
    DateOnly From, DateOnly To, int? FloorId = null, int? ClassId = null, string? Student = null, string? Destination = null, string? Status = null,
    int Page = 1, int PageSize = 50, bool History = false)
{
    public bool HasFilters => FloorId is not null || ClassId is not null || !string.IsNullOrWhiteSpace(Student) || !string.IsNullOrWhiteSpace(Destination)
                              || !string.IsNullOrWhiteSpace(Status);
}

public sealed class MovementPage
{
    public required MovementFilter Filter { get; init; }
    public required PagedResult<MovementRow> Result { get; init; }
    public required MovementCounts Counts { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Floors { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Classes { get; init; }
    public required IReadOnlyList<string> Destinations { get; init; }
    public required FloorAuditUser User { get; init; }
    public int OverdueMinutes { get; init; }
    public bool IsToday => Filter.From == Filter.To && Filter.From == SchoolTime.Current.Today;

    /// <summary>Today's figures (live board header).</summary>
    public MovementCounts Today { get; init; } = MovementCounts.Empty;
}

public sealed class MovementFormModel
{
    public int? StudentId { get; set; }
    public int? FromAreaId { get; set; }
    public int? ToAreaId { get; set; }
    public bool Custom { get; set; }
    public string? CustomDestination { get; set; }

    /// <summary>"HH:mm" school time today; empty = now.</summary>
    public string? ExitTime { get; set; }
    public string? Notes { get; set; }
    public int? Floor { get; set; }

    // ---- shown ----
    public string? RecordedBy { get; set; }
    public IReadOnlyList<MovementStudentOption> Students { get; set; } = [];
    public IReadOnlyList<FloorAreaInfo> Areas { get; set; } = [];
    public MovementRow? Existing { get; set; }
}

public sealed record MovementStudentOption(int Id, string Name, int ClassId, string ClassName, int? AreaId, string? FloorName, bool Outside);

// ---------------------------------------------------------------- History and reports

public sealed record InspectionFilter(
    DateOnly From, DateOnly To, int? FloorId = null, int? AreaId = null, int? SlotId = null, int? Round = null, int? InspectorId = null,
    string? Status = null, int Page = 1, int PageSize = 50)
{
    public bool HasFilters => FloorId is not null || AreaId is not null || SlotId is not null || Round is not null || InspectorId is not null
                              || !string.IsNullOrWhiteSpace(Status);
}

public sealed class InspectionHistoryPage
{
    public required InspectionFilter Filter { get; init; }
    public required PagedResult<InspectionBrief> Result { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Floors { get; init; }
    public required IReadOnlyList<FloorAreaInfo> Areas { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Sessions { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Inspectors { get; init; }
    public int Completed { get; init; }
    public int InProgress { get; init; }
    public int WithIssues { get; init; }
}

// ---------------------------------------------------------------- Manage

public sealed record FloorSummary(int Id, string Name, int SortOrder, bool IsActive, int Classrooms, int Facilities, int Inactive);

public sealed record AuditItemView(
    int Id, string Name, string? Description, AuditItemScope AppliesTo, AuditResponseType Type, bool Required, bool Active, bool IsDefault, int SortOrder,
    int TimesUsed);

public sealed record AccessView(int Id, int StaffId, string StaffName, string? JobTitle, FloorAuditRole Role, string? Floor, bool Movements, DateTime GrantedAt, string? GrantedBy);

public sealed class ManagePage
{
    public required string Tab { get; init; }
    public required IReadOnlyList<FloorSummary> Floors { get; init; }
    public required IReadOnlyList<FloorAreaInfo> Areas { get; init; }
    public required IReadOnlyList<(int Id, string Name)> UnassignedClasses { get; init; }
    public required IReadOnlyList<AuditItemView> Items { get; init; }
    public required IReadOnlyList<AccessView> Access { get; init; }
    public required IReadOnlyList<string> ManagementTeam { get; init; }
    public required IReadOnlyList<AuditLogView> Log { get; init; }
    public required int OverdueMinutes { get; init; }
    public required FloorAuditUser User { get; init; }
}

public sealed class FloorForm
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public int? SortOrder { get; set; }
}

public sealed class FacilityForm
{
    public int? Id { get; set; }
    public int? FloorId { get; set; }
    public string? Name { get; set; }
    public FacilityType? FacilityType { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public IReadOnlyList<(int Id, string Name)> Floors { get; set; } = [];
}

public sealed class ClassroomForm
{
    public int? AreaId { get; set; }
    public int? FloorId { get; set; }
    public List<int> ClassIds { get; set; } = [];
    public string? ClassName { get; set; }
    public IReadOnlyList<(int Id, string Name)> Floors { get; set; } = [];
    public IReadOnlyList<(int Id, string Name)> Classes { get; set; } = [];
}

public sealed class AuditItemForm
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public AuditItemScope AppliesTo { get; set; } = AuditItemScope.Both;
    public AuditResponseType ResponseType { get; set; } = AuditResponseType.GoodNeedsAttention;
    public bool IsRequired { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int? SortOrder { get; set; }
    public int TimesUsed { get; set; }
}

public sealed class AccessForm
{
    public int? StaffId { get; set; }
    public FloorAuditRole Role { get; set; } = FloorAuditRole.Inspector;
    public int? FloorId { get; set; }
    public bool CanRecordMovements { get; set; } = true;
    public IReadOnlyList<(int Id, string Name, string? Note)> Staff { get; set; } = [];
    public IReadOnlyList<(int Id, string Name)> Floors { get; set; } = [];
}
