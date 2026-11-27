using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Web.ViewModels;

public sealed class PtcEventFormModel
{
    public int? Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int AcademicYearId { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public PtcEventStatus Status { get; set; } = PtcEventStatus.Draft;
    public IReadOnlyList<AcademicYearOption> Years { get; set; } = [];
    public bool IsEdit => Id is not null;

    /// <summary>A Published event keeps its academic year and dates (only the title and description can change).</summary>
    public bool DatesLocked => Status == PtcEventStatus.Published;
}

/// <summary>One class row of "Classes &amp; dates" as posted.</summary>
public sealed class PtcClassRowInput
{
    public int ClassId { get; set; }
    public bool Include { get; set; }
    public DateOnly? Date { get; set; }
    public TimeOnly? Start { get; set; }
    public TimeOnly? End { get; set; }
}

public sealed class PtcClassesViewModel
{
    public required PtcEventDetail Event { get; init; }

    /// <summary>The form's values per class (posted values when the form is shown again with errors).</summary>
    public required IReadOnlyDictionary<int, PtcClassRowInput> Rows { get; init; }
}

public sealed class PtcScheduleFormModel
{
    public DateOnly? Date { get; set; }
    public TimeOnly? Start { get; set; }
    public TimeOnly? End { get; set; }
    public string? Notes { get; set; }
}

public sealed class PtcRoundsFormModel
{
    public TimeOnly? FirstStart { get; set; }
    public int Count { get; set; } = 1;
    public int DurationMinutes { get; set; }
    public int Capacity { get; set; }
    public int MeetingMinutes { get; set; }
    public int TransitionMinutes { get; set; }
    public int GapMinutes { get; set; }
    public bool SameDayClasses { get; set; }

    public static PtcRoundsFormModel From(PtcRoundDefaults d) => new()
    {
        DurationMinutes = d.RoundDurationMinutes, Capacity = d.Capacity, MeetingMinutes = d.MeetingDurationMinutes, TransitionMinutes = d.TransitionDurationMinutes,
    };
}

public sealed class PtcScheduleViewModel
{
    public required PtcEventDetail Event { get; init; }
    public required PtcClassRow Class { get; init; }
    public required PtcScheduleFormModel Form { get; init; }
    public required PtcRoundsFormModel Rounds { get; init; }
    public required PtcRoundDefaults Defaults { get; init; }

    /// <summary>Other classes of the event on the same date that have no Rounds yet.</summary>
    public int SameDayWithoutRounds { get; init; }


    public PtcScheduleRow Schedule => Class.Schedule!;
}

public sealed class PtcDefaultsFormModel
{
    public int Capacity { get; set; }
    public int MeetingMinutes { get; set; }
    public int TransitionMinutes { get; set; }
    public int DurationMinutes { get; set; }

    public static PtcDefaultsFormModel From(PtcRoundDefaults d) => new()
    {
        Capacity = d.Capacity, MeetingMinutes = d.MeetingDurationMinutes, TransitionMinutes = d.TransitionDurationMinutes, DurationMinutes = d.RoundDurationMinutes,
    };
}

// ============================================================ Phase 3: admin booking visibility, sharing, KG teachers

public sealed class PtcBookingsViewModel
{
    public required PtcEventDetail Event { get; init; }
    public required IReadOnlyList<PtcScheduleBookings> Schedules { get; init; }
    public int Total => Schedules.Sum(s => s.Bookings);
}

/// <summary>The event's public booking link and its WhatsApp share text (Super Admin; the link works only while Published).</summary>
public sealed record PtcShareModel(int EventId, bool Published, bool BookingsOpen, string? Url, string? Message);

// ============================================================ Phase 3: public parent portal

public sealed class PtcPortalStartModel
{
    public required PortalEvent Event { get; init; }
    public string? Phone { get; init; }
    public string? FirstName { get; init; }
    public string? Error { get; init; }
}

public sealed class PtcPortalChildrenModel
{
    public required PortalEvent Event { get; init; }
    public required IReadOnlyList<PortalChild> Children { get; init; }
}

public sealed class PtcPortalBookModel
{
    public required PortalBookingPage Page { get; init; }
    public int? Round { get; init; }
    public IReadOnlyList<string> Subjects { get; init; } = [];
    public string? Error { get; init; }
}

public sealed class PtcPortalConfirmationModel
{
    public required PortalEvent Event { get; init; }
    public required PortalConfirmation Confirmation { get; init; }
    public bool AlreadyBooked { get; init; }
}

// ============================================================ Phase 4: scheduling

public sealed class PtcSchedulingViewModel
{
    public const string DatesView = "dates", TeachersView = "teachers", ConflictsView = "conflicts", HistoryView = "history";

    public required PtcEventDetail Event { get; init; }
    public required PtcScheduleAnalysis Analysis { get; init; }
    public IReadOnlyList<PtcScheduleChangeView> History { get; init; } = [];
    public string View { get; init; } = DatesView;
    public DateOnly? Date { get; init; }
    public int? ClassId { get; init; }
    public PtcAllocationState? State { get; init; }
    public int? TeacherId { get; init; }

    /// <summary>The class schedules shown under the filters.</summary>
    public IReadOnlyList<PtcClassScheduleView> Classes => Analysis.Classes
        .Where(c => Date is null || c.Date == Date)
        .Where(c => ClassId is null || c.ClassId == ClassId)
        .Where(c => TeacherId is null || c.Teachers.Any(t => t.TeacherId == TeacherId))
        .ToList();

    public bool Shows(PtcSlotView slot) => State is null || slot.State == State;
}

// ============================================================ Phase 6: class coverage

/// <summary>The Class Coverage page: the event's coverage (computed now), filters, sort and the substitute history.</summary>
public sealed class PtcCoverageViewModel
{
    public required PtcEventDetail Event { get; init; }
    public PtcCoverageReport Report => Event.Coverage;
    public DateOnly? Date { get; init; }
    public int? ClassId { get; init; }
    public int? DepartmentId { get; init; }
    public int? TeacherId { get; init; }
    public PtcCoverageStatus? Status { get; init; }
    public int? Round { get; init; }

    /// <summary>date (default), teacher or status.</summary>
    public string Sort { get; init; } = "date";
    public IReadOnlyList<PtcSubstituteHistoryRow> History { get; init; } = [];

    public bool Filtered => Date is not null || ClassId is not null || DepartmentId is not null || TeacherId is not null || Status is not null || Round is not null;

    public IReadOnlyList<PtcCoverageConflict> Rows
    {
        get
        {
            var rows = Report.Conflicts.Where(c => (Date is null || c.Date == Date) && (ClassId is null || c.LessonClassId == ClassId)
                && (DepartmentId is null || c.DepartmentId == DepartmentId) && (TeacherId is null || c.TeacherId == TeacherId)
                && (Status is null || c.Status == Status) && (Round is null || c.RoundNumber == Round));
            return (Sort switch
            {
                "teacher" => rows.OrderBy(c => c.TeacherName).ThenBy(c => c.Date).ThenBy(c => c.LessonStart),
                // Most urgent first: uncovered, invalid, then covered.
                "status" => rows.OrderBy(c => c.Status switch { PtcCoverageStatus.Uncovered => 0, PtcCoverageStatus.Invalid => 1, PtcCoverageStatus.NeedsReview => 2, _ => 3 })
                    .ThenBy(c => c.Date).ThenBy(c => c.LessonStart),
                _ => rows.OrderBy(c => c.Date).ThenBy(c => c.LessonStart).ThenBy(c => c.TeacherName).ThenBy(c => c.RoundNumber),
            }).ToList();
        }
    }
}


// ============================================================ Phase 7: feedback and reports

public sealed class PtcFeedbackStartModel
{
    public required PtcFeedbackEvent Event { get; init; }
    public string? Phone { get; init; }
    public string? FirstName { get; init; }
    public string? Error { get; init; }
}

public sealed class PtcFeedbackChildrenModel
{
    public required PtcFeedbackEvent Event { get; init; }
    public required IReadOnlyList<PtcFeedbackChild> Children { get; init; }
}

/// <summary>The posted feedback form (strings: parsed and validated by the service).</summary>
public sealed class PtcFeedbackPost
{
    public string? Overall { get; set; }
    public string? Communication { get; set; }
    public string? Organization { get; set; }
    public string? Helpful { get; set; }
    public string? Comments { get; set; }
    public string? ConcernCategory { get; set; }
    public string? Concern { get; set; }
    public List<PtcTeacherFeedbackPost>? Teachers { get; set; }
}

public sealed class PtcTeacherFeedbackPost
{
    public string? Code { get; set; }
    public string? Communication { get; set; }
    public string? Helpful { get; set; }
    public string? Comment { get; set; }
}

public sealed class PtcFeedbackFormModel
{
    public required PtcFeedbackForm Form { get; init; }
    public PtcFeedbackPost? Post { get; init; }
    public string? Error { get; init; }
}

public sealed class PtcFeedbackListModel
{
    public required PtcFeedbackFilter Filter { get; init; }
    public required PtcFeedbackList List { get; init; }
}

public sealed class PtcEventFeedbackModel
{
    public required PtcEventDetail Event { get; init; }
    public required PtcFeedbackLinks Links { get; init; }
}
