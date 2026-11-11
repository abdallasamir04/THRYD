using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>What one assignment meant for that person (not for the duty as a whole).</summary>
public enum StaffDutyOutcome
{
    /// <summary>Duty Covered and this person recorded present.</summary>
    Completed,
    /// <summary>This person was recorded absent (even if a backup covered the duty).</summary>
    Absent,
    /// <summary>Duty marked Not Covered while this person was not recorded absent.</summary>
    NotCovered,
    /// <summary>Duty Pending or In Progress.</summary>
    Open,
    /// <summary>Backup request not answered yet: not an assignment yet.</summary>
    AwaitingResponse,
    /// <summary>Backup request declined: not an attendance failure, not counted.</summary>
    Declined,
    /// <summary>Replaced or request withdrawn before the duty: not counted.</summary>
    Ended,
}

public static class StaffDutyRules
{
    /// <summary>Only people who held the duty can be evaluated: the current assigned staff, or a backup who accepted; never before the duty date.</summary>
    public static bool CanEvaluate(DutyRole role, bool isCurrent, BackupRequestStatus backup, DateOnly dutyDate) =>
        isCurrent && (role == DutyRole.Primary || backup == BackupRequestStatus.Accepted) && dutyDate <= SchoolWeek.Today;

    public static StaffDutyOutcome Outcome(DutyRole role, bool isCurrent, BackupRequestStatus backup, AttendanceStatus attendance, DutyStatus status)
    {
        if (role == DutyRole.Backup && backup == BackupRequestStatus.Rejected) return StaffDutyOutcome.Declined;
        if (!isCurrent) return StaffDutyOutcome.Ended;
        if (role == DutyRole.Backup && backup == BackupRequestStatus.Pending) return StaffDutyOutcome.AwaitingResponse;
        if (attendance == AttendanceStatus.Absent) return StaffDutyOutcome.Absent;
        return status switch
        {
            DutyStatus.Covered when attendance == AttendanceStatus.Present => StaffDutyOutcome.Completed,
            DutyStatus.NotCovered => StaffDutyOutcome.NotCovered,
            DutyStatus.Covered => StaffDutyOutcome.Open, // covered by someone else; this person's attendance not recorded
            _ => StaffDutyOutcome.Open,
        };
    }

    public static bool Counts(StaffDutyOutcome o) =>
        o is StaffDutyOutcome.Completed or StaffDutyOutcome.Absent or StaffDutyOutcome.NotCovered or StaffDutyOutcome.Open;

    public static string Label(StaffDutyOutcome o) => o switch
    {
        StaffDutyOutcome.Completed => "Covered",
        StaffDutyOutcome.Absent => "Absent",
        StaffDutyOutcome.NotCovered => "Not covered",
        StaffDutyOutcome.Open => "Pending / in progress",
        StaffDutyOutcome.AwaitingResponse => "Request awaiting answer",
        StaffDutyOutcome.Declined => "Declined backup request",
        _ => "Replaced / withdrawn",
    };

    public static string Tone(StaffDutyOutcome o) => o switch
    {
        StaffDutyOutcome.Completed => "success",
        StaffDutyOutcome.Absent or StaffDutyOutcome.NotCovered => "error",
        StaffDutyOutcome.Open or StaffDutyOutcome.AwaitingResponse => "warning",
        _ => "neutral",
    };
}

public sealed record StaffSummary(int Id, string FullName, string? JobTitle, string? ContactNumber, string StaffType);

public sealed record StaffDutyHistoryRow(
    int AssignmentId, int DutyId, DutyType DutyType, DutyComponent Component, DateOnly DutyDate, string Description,
    TimeOnly StartTime, TimeOnly EndTime, string? Location, DutyRole Role, AttendanceStatus Attendance, BackupRequestStatus BackupStatus,
    bool IsCurrent, string? EndReason, DutyStatus DutyStatus, StaffDutyOutcome Outcome, int EvaluationCount, int? LatestRating,
    bool CanEvaluate, int? WorkloadPeriods, bool CanCountInWorkload);

public sealed record FeedbackRow(
    int Id, int AssignmentId, int DutyId, DateOnly DutyDate, DutyType DutyType, DutyComponent Component, string DutyDescription,
    DutyRole Role, string StaffName, int Rating, string Feedback, string? Strengths, string? Issues, bool FollowUpRequired,
    string EvaluatorName, DateOnly EvaluationDate, DateTime CreatedAt, string? RecordedBy, bool IsLatest);

public sealed record DutyHistorySummary(int TotalAssigned, int Covered, int NotCovered, int Open, int Declined, int Awaiting, double? AverageRating, int EvaluatedAssignments)
{
    public static DutyHistorySummary From(IReadOnlyList<StaffDutyHistoryRow> rows)
    {
        var rated = rows.Where(r => r.LatestRating is not null).Select(r => r.LatestRating!.Value).ToList();
        return new DutyHistorySummary(
            rows.Count(r => StaffDutyRules.Counts(r.Outcome)),
            rows.Count(r => r.Outcome == StaffDutyOutcome.Completed),
            rows.Count(r => r.Outcome is StaffDutyOutcome.Absent or StaffDutyOutcome.NotCovered),
            rows.Count(r => r.Outcome == StaffDutyOutcome.Open),
            rows.Count(r => r.Outcome == StaffDutyOutcome.Declined),
            rows.Count(r => r.Outcome == StaffDutyOutcome.AwaitingResponse),
            rated.Count == 0 ? null : Math.Round(rated.Average(), 2),
            rated.Count);
    }
}

public sealed record StaffProfile(StaffSummary Staff, DutyHistorySummary Summary, IReadOnlyList<StaffDutyHistoryRow> Duties, IReadOnlyList<FeedbackRow> Evaluations);

public sealed record StaffDutyOverviewRow(StaffSummary Staff, DutyHistorySummary Summary);

public sealed record EvaluationContext(int AssignmentId, int StaffId, string StaffName, DutyRole Role, AttendanceStatus Attendance,
    int DutyId, DutyComponent Component, DateOnly DutyDate, string Description, TimeOnly StartTime, TimeOnly EndTime, DutyStatus DutyStatus,
    bool CanEvaluate, IReadOnlyList<FeedbackRow> Previous);

public sealed record FeedbackInput(int AssignmentId, int Rating, string Feedback, string? Strengths, string? Issues, bool FollowUpRequired, int? EvaluatorStaffId, DateOnly? EvaluationDate);

public interface IStaffDutyService
{
    Task<StaffProfile?> ProfileAsync(int staffId, CancellationToken ct = default);
    Task<IReadOnlyList<StaffDutyOverviewRow>> OverviewAsync(string? search, CancellationToken ct = default);
    Task<(double? Average, int Count)> AverageRatingAsync(CancellationToken ct = default);
    Task<EvaluationContext?> EvaluationContextAsync(int assignmentId, CancellationToken ct = default);
    Task<ServiceResult> AddFeedbackAsync(FeedbackInput input, CancellationToken ct = default);
}

public sealed class StaffDutyService(SchoolDbContext db, ICurrentUser currentUser) : IStaffDutyService
{
    public async Task<StaffProfile?> ProfileAsync(int staffId, CancellationToken ct = default)
    {
        var staff = await StaffAsync(db.StaffMembers.Where(s => s.Id == staffId), ct);
        if (staff.Count == 0) return null;
        var duties = await HistoryAsync(db.DutyStaffAssignments.Where(a => a.StaffMemberId == staffId), staff[0].StaffType == "Teacher", ct);
        var evaluations = await FeedbackAsync(db.StaffFeedback.Where(f => f.DutyStaffAssignment.StaffMemberId == staffId), ct);
        return new StaffProfile(staff[0], DutyHistorySummary.From(duties), duties, evaluations);
    }

    public async Task<IReadOnlyList<StaffDutyOverviewRow>> OverviewAsync(string? search, CancellationToken ct = default)
    {
        var query = db.StaffMembers.AsQueryable();
        var term = TextNormalizer.NameKey(search);
        if (term.Length > 0) query = query.Where(s => s.NormalizedName.Contains(term));
        var staff = await StaffAsync(query, ct);
        var ids = staff.Select(s => s.Id).ToList();
        var history = (await HistoryAsync(db.DutyStaffAssignments.Where(a => ids.Contains(a.StaffMemberId)), false, ct, includeStaffId: true));
        var byStaff = history.ToLookup(h => h.StaffId, h => h.Row);
        return staff.Select(s => new StaffDutyOverviewRow(s, DutyHistorySummary.From(byStaff[s.Id].ToList())))
            .OrderByDescending(r => r.Summary.TotalAssigned).ThenBy(r => r.Staff.FullName).ToList();
    }

    /// <summary>Average of the latest evaluation of each evaluated assignment.</summary>
    public async Task<(double? Average, int Count)> AverageRatingAsync(CancellationToken ct = default)
    {
        var latest = await db.StaffFeedback.AsNoTracking()
            .GroupBy(f => f.DutyStaffAssignmentId)
            .Select(g => g.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id).Select(f => f.Rating).First())
            .ToListAsync(ct);
        return (latest.Count == 0 ? null : Math.Round(latest.Average(), 2), latest.Count);
    }

    public async Task<EvaluationContext?> EvaluationContextAsync(int assignmentId, CancellationToken ct = default)
    {
        var a = await db.DutyStaffAssignments.AsNoTracking().Where(x => x.Id == assignmentId)
            .Select(x => new
            {
                x.Id, x.StaffMemberId, x.StaffMember.FullName, x.Role, x.Attendance, x.BackupStatus, x.IsCurrent,
                x.DutyAssignmentId, x.DutyAssignment.Component, x.DutyAssignment.DutyDate, x.DutyAssignment.Description,
                x.DutyAssignment.StartTime, x.DutyAssignment.EndTime, x.DutyAssignment.Status,
            })
            .FirstOrDefaultAsync(ct);
        if (a is null) return null;
        var previous = await FeedbackAsync(db.StaffFeedback.Where(f => f.DutyStaffAssignmentId == assignmentId), ct);
        return new EvaluationContext(a.Id, a.StaffMemberId, a.FullName, a.Role, a.Attendance, a.DutyAssignmentId, a.Component, a.DutyDate,
            a.Description, a.StartTime, a.EndTime, a.Status, StaffDutyRules.CanEvaluate(a.Role, a.IsCurrent, a.BackupStatus, a.DutyDate), previous);
    }

    public async Task<ServiceResult> AddFeedbackAsync(FeedbackInput input, CancellationToken ct = default)
    {
        var context = await db.DutyStaffAssignments.AsNoTracking().Where(a => a.Id == input.AssignmentId)
            .Select(a => new { a.StaffMemberId, a.Role, a.IsCurrent, a.BackupStatus, a.DutyAssignment.DutyDate })
            .FirstOrDefaultAsync(ct);
        if (context is null) return ServiceResult.Fail("", "This duty assignment no longer exists.");
        if (!StaffDutyRules.CanEvaluate(context.Role, context.IsCurrent, context.BackupStatus, context.DutyDate))
            return ServiceResult.Fail("", context.DutyDate > SchoolWeek.Today
                ? "This duty has not taken place yet. Evaluate it on or after the duty date."
                : "Only the assigned staff member, or a backup who accepted, can be evaluated for this duty.");

        var r = ServiceResult.Ok();
        if (input.Rating is < 1 or > 5) r.AddError("Rating", "Choose a rating from 1 to 5 stars.");
        var feedback = (input.Feedback ?? "").Trim();
        if (feedback.Length == 0) r.AddError("Feedback", "Write the feedback.");
        else if (feedback.Length > StaffDutyFeedback.FeedbackMaxLength) r.AddError("Feedback", $"Use {StaffDutyFeedback.FeedbackMaxLength} characters or fewer.");
        var strengths = (input.Strengths ?? "").Trim();
        var issues = (input.Issues ?? "").Trim();
        if (strengths.Length > StaffDutyFeedback.ListMaxLength) r.AddError("Strengths", $"Use {StaffDutyFeedback.ListMaxLength} characters or fewer.");
        if (issues.Length > StaffDutyFeedback.ListMaxLength) r.AddError("Issues", $"Use {StaffDutyFeedback.ListMaxLength} characters or fewer.");

        if (input.EvaluatorStaffId is not { } evaluatorId) r.AddError("EvaluatorStaffId", "Choose who is evaluating.");
        else if (evaluatorId == context.StaffMemberId) r.AddError("EvaluatorStaffId", "Staff members cannot evaluate themselves.");
        else if (!await db.StaffMembers.AnyAsync(s => s.Id == evaluatorId, ct)) r.AddError("EvaluatorStaffId", "Choose the evaluator from the staff list.");

        var date = input.EvaluationDate ?? SchoolWeek.Today;
        if (date < context.DutyDate) r.AddError("EvaluationDate", "The evaluation date cannot be before the duty.");
        if (date > SchoolWeek.Today) r.AddError("EvaluationDate", "The evaluation date cannot be in the future.");
        if (!r.Succeeded) return r;

        var entity = new StaffDutyFeedback
        {
            DutyStaffAssignmentId = input.AssignmentId,
            Rating = input.Rating,
            Feedback = feedback,
            Strengths = strengths.Length == 0 ? null : strengths,
            Issues = issues.Length == 0 ? null : issues,
            FollowUpRequired = input.FollowUpRequired,
            EvaluatorStaffId = input.EvaluatorStaffId!.Value,
            EvaluationDate = date,
            CreatedAt = DateTime.UtcNow,
            RecordedBy = currentUser.Name,
        };
        db.StaffFeedback.Add(entity);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(entity.Id);
    }

    // ------------------------------------------------------------------ queries

    private static async Task<List<StaffSummary>> StaffAsync(IQueryable<StaffMember> q, CancellationToken ct) =>
        await q.AsNoTracking().OrderBy(s => s.FullName)
            .Select(s => new StaffSummary(s.Id, s.FullName, s.JobTitle, s.ContactNumber, EF.Property<string>(s, "StaffType")))
            .ToListAsync(ct);

    private async Task<List<StaffDutyHistoryRow>> HistoryAsync(IQueryable<DutyStaffAssignment> q, bool isTeacher, CancellationToken ct) =>
        (await HistoryAsync(q, isTeacher, ct, includeStaffId: false)).Select(x => x.Row).ToList();

    private async Task<List<(int StaffId, StaffDutyHistoryRow Row)>> HistoryAsync(IQueryable<DutyStaffAssignment> q, bool isTeacher, CancellationToken ct, bool includeStaffId)
    {
        var rows = await q.AsNoTracking()
            .OrderByDescending(a => a.DutyAssignment.DutyDate).ThenBy(a => a.DutyAssignment.StartTime).ThenBy(a => a.Id)
            .Select(a => new
            {
                a.Id, a.StaffMemberId, a.DutyAssignmentId, a.DutyAssignment.DutyType, a.DutyAssignment.Component, a.DutyAssignment.DutyDate,
                a.DutyAssignment.Description, a.DutyAssignment.StartTime, a.DutyAssignment.EndTime, a.DutyAssignment.Location,
                a.Role, a.Attendance, a.BackupStatus, a.IsCurrent, a.EndReason, a.DutyAssignment.Status,
                Count = a.Feedback.Count,
                Latest = a.Feedback.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id).Select(f => (int?)f.Rating).FirstOrDefault(),
                Periods = db.WorkloadAssignments.Where(w => w.DutyAssignmentId == a.DutyAssignmentId && w.TeacherId == a.StaffMemberId)
                    .Select(w => (int?)w.Periods).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows.Select(a =>
        {
            var outcome = StaffDutyRules.Outcome(a.Role, a.IsCurrent, a.BackupStatus, a.Attendance, a.Status);
            var canCount = isTeacher && a.Periods is null && a.IsCurrent && a.Attendance != AttendanceStatus.Absent
                && (a.Role == DutyRole.Primary || a.BackupStatus == BackupRequestStatus.Accepted);
            return (a.StaffMemberId, new StaffDutyHistoryRow(a.Id, a.DutyAssignmentId, a.DutyType, a.Component, a.DutyDate, a.Description,
                a.StartTime, a.EndTime, a.Location, a.Role, a.Attendance, a.BackupStatus, a.IsCurrent, a.EndReason, a.Status, outcome,
                a.Count, a.Latest, StaffDutyRules.CanEvaluate(a.Role, a.IsCurrent, a.BackupStatus, a.DutyDate), a.Periods, canCount));
        }).ToList();
    }

    private static async Task<List<FeedbackRow>> FeedbackAsync(IQueryable<StaffDutyFeedback> q, CancellationToken ct)
    {
        var rows = await q.AsNoTracking()
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Select(f => new
            {
                f.Id, f.DutyStaffAssignmentId, f.DutyStaffAssignment.DutyAssignmentId, f.DutyStaffAssignment.DutyAssignment.DutyDate,
                f.DutyStaffAssignment.DutyAssignment.DutyType, f.DutyStaffAssignment.DutyAssignment.Component,
                f.DutyStaffAssignment.DutyAssignment.Description, f.DutyStaffAssignment.Role, StaffName = f.DutyStaffAssignment.StaffMember.FullName,
                f.Rating, f.Feedback, f.Strengths, f.Issues, f.FollowUpRequired, EvaluatorName = f.Evaluator.FullName,
                f.EvaluationDate, f.CreatedAt, f.RecordedBy,
            })
            .ToListAsync(ct);
        var seen = new HashSet<int>();
        return rows.Select(f => new FeedbackRow(f.Id, f.DutyStaffAssignmentId, f.DutyAssignmentId, f.DutyDate, f.DutyType, f.Component,
            f.Description, f.Role, f.StaffName, f.Rating, f.Feedback, f.Strengths, f.Issues, f.FollowUpRequired, f.EvaluatorName,
            f.EvaluationDate, f.CreatedAt, f.RecordedBy, seen.Add(f.DutyStaffAssignmentId))).ToList();
    }
}
