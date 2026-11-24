using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.FloorManagement;

namespace SchoolManagement.Web.ViewModels;

public sealed class FloorDashboardViewModel
{
    public required FloorUser User { get; init; }
    public required IReadOnlyList<FloorTeamCard> Floors { get; init; }
    /// <summary>Active floors without a management grade range yet (administrators only), e.g. floors used only for inspections.</summary>
    public IReadOnlyList<FloorTeamCard> Unconfigured { get; init; } = [];
    public ConsultantRef? Consultant { get; init; }
    public int OpenReports { get; init; }
    public int OpenTickets { get; init; }
}

public sealed class FloorDetailsViewModel
{
    public required FloorUser User { get; init; }
    public required FloorTeamCard Floor { get; init; }
    public ConsultantRef? Consultant { get; init; }
}

public sealed class FloorFormModel
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string? GradeRange { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    // New floor only: its first team members (Matrons are added on the floor page, with their classes).
    public int? StageCoordinatorId { get; set; }
    public int? FloorSupervisorId { get; set; }
    public int? FloatingMatronId { get; set; }
    public int? SocialWorkerId { get; set; }

    public IReadOnlyList<Option> Staff { get; set; } = [];
    public bool IsEdit => Id is not null;
}

public sealed class FloorAssignFormModel
{
    public int FloorId { get; set; }
    public string FloorName { get; set; } = "";
    public int StaffMemberId { get; set; }
    public FloorRole Role { get; set; } = FloorRole.Matron;
    public string? Coverage { get; set; }
    public List<int> ClassIds { get; set; } = [];
    public IReadOnlyList<Option> Staff { get; set; } = [];
    public IReadOnlyList<Option> Classes { get; set; } = [];
}

public sealed class FloorReportsViewModel
{
    public required FloorUser User { get; init; }
    public required IReadOnlyList<FloorReportRow> Reports { get; init; }
    public IReadOnlyList<FloorTeamCard> Floors { get; init; } = [];
}

public sealed class FloorReportFormModel
{
    public int FloorId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public FloorReportCategory Category { get; set; } = FloorReportCategory.DailyOperations;
    public int? RelatedStaffId { get; set; }
    public DateTime? OccurredAt { get; set; }
    public FloorReportRecipient Recipient { get; set; } = FloorReportRecipient.FloorSupervisor;

    public IReadOnlyList<FloorTeamCard> Floors { get; set; } = [];
    public IReadOnlyList<FloorReportRecipient> Recipients { get; set; } = [];
    public IReadOnlyList<Option> Staff { get; set; } = [];
}

public sealed class FloorReportViewModel
{
    public required FloorReportDetail Report { get; init; }
    public bool CanReview { get; init; }
    public bool CanEscalate { get; init; }
}

public sealed class FloorTicketsViewModel
{
    public required FloorUser User { get; init; }
    public required IReadOnlyList<FloorTicketRow> Tickets { get; init; }
}

public sealed class FloorTicketFormModel
{
    public int FloorId { get; set; }
    public int StudentId { get; set; }
    public FloorTicketCategory Category { get; set; } = FloorTicketCategory.Discipline;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public string Description { get; set; } = "";
    public IReadOnlyList<FloorTeamCard> Floors { get; set; } = [];
    public IReadOnlyList<Option> Students { get; set; } = [];
}

public sealed class FloorTicketViewModel
{
    public required FloorTicketDetail Ticket { get; init; }
    public bool CanFollowUp { get; init; }
    public IReadOnlyList<FloorPerson> SocialWorkers { get; init; } = [];
}
