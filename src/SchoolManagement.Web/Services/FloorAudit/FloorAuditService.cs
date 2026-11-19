using System.Data;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Services;

/// <summary>An uploaded file for an inspection (the controller passes the request's files).</summary>
public sealed record UploadedFile(string FileName, long Length, Func<Stream> Open);

public interface IFloorAuditService
{
    Task<FloorAuditHome> HomeAsync(DateOnly date, CancellationToken ct = default);
    Task<FloorPage?> FloorAsync(int floorId, DateOnly date, int? slotId, int? round, CancellationToken ct = default);
    Task<InspectionsPage> InspectionsAsync(DateOnly date, int? slotId, int? round, int? floorId, bool pendingOnly, CancellationToken ct = default);
    Task<AuditDaySchedule> ScheduleAsync(DateOnly date, CancellationToken ct = default);
    Task<InspectionFormModel?> InspectionFormAsync(int areaId, DateOnly date, int slotId, int round, CancellationToken ct = default);
    Task<ServiceResult> SaveInspectionAsync(InspectionFormModel form, bool complete, IReadOnlyList<UploadedFile> files, CancellationToken ct = default);
    Task<InspectionDetails?> InspectionAsync(int id, CancellationToken ct = default);
    Task<AreaPage?> AreaAsync(int areaId, CancellationToken ct = default);
    Task<(InspectionAttachmentView File, Stream Content)?> AttachmentAsync(int inspectionId, int attachmentId, CancellationToken ct = default);
    Task<IReadOnlyList<FloorAreaInfo>> AreasAsync(bool activeOnly, CancellationToken ct = default);
    Task<string?> AcademicYearAsync(DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// Floor Audit: the floors, their areas and the three rounds of every timetable session. The schedule is calculated
/// from the timetable of the date (see <see cref="FloorAuditSchedule"/>); inspections are the only audit records and
/// are created by an inspector's own save — nothing is ever filled in automatically.
/// </summary>
public sealed class FloorAuditService(
    SchoolDbContext db, ICurrentUser currentUser, IFloorAuditAccessService access, ISchoolTimeService clock, ITaskFileStore files,
    IStudentMovementService movements, ISettingsService settings) : IFloorAuditService
{
    public const int MaxAttachments = 5;
    public static readonly string[] AttachmentExtensions = ["png", "jpg", "jpeg", "pdf"];
    public const string AttachmentAccept = ".png,.jpg,.jpeg,.pdf";

    public Task<AuditDaySchedule> ScheduleAsync(DateOnly date, CancellationToken ct = default) => FloorAuditSchedule.LoadAsync(db, date, clock, ct);

    public async Task<IReadOnlyList<FloorAreaInfo>> AreasAsync(bool activeOnly, CancellationToken ct = default)
    {
        var q = db.FloorAreas.AsNoTracking();
        if (activeOnly) q = q.Where(a => a.IsActive && a.Floor.IsActive);
        var rows = await q.Select(a => new
        {
            a.Id, a.FloorId, FloorName = a.Floor.Name, FloorOrder = a.Floor.SortOrder, a.Kind, a.SchoolClassId, ClassName = a.SchoolClass!.Name,
            ClassOrder = (int?)a.SchoolClass!.SortOrder, a.Name, a.FacilityType, a.Description, a.IsActive, a.SortOrder,
        }).ToListAsync(ct);
        return rows
            .OrderBy(r => r.FloorOrder).ThenBy(r => r.Kind).ThenBy(r => r.Kind == FloorAreaKind.Classroom ? r.ClassOrder ?? 0 : r.SortOrder)
            .ThenBy(r => r.Name ?? r.ClassName, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new FloorAreaInfo(r.Id, r.FloorId, r.FloorName, r.Kind, r.SchoolClassId, r.Kind == FloorAreaKind.Classroom ? r.ClassName ?? "" : r.Name ?? "",
                r.FacilityType, r.Description, r.IsActive, r.SortOrder))
            .ToList();
    }

    /// <summary>The academic year of a date: the year whose quarter contains it, else the school's current year (Settings).</summary>
    public async Task<string?> AcademicYearAsync(DateOnly date, CancellationToken ct = default) =>
        (await AcademicYearOfAsync(date, ct)).Name;

    private async Task<(int? Id, string? Name)> AcademicYearOfAsync(DateOnly date, CancellationToken ct)
    {
        var byQuarter = await db.AcademicQuarters.AsNoTracking().Where(q => q.StartDate <= date && q.EndDate >= date)
            .Select(q => new { q.AcademicYearId, q.AcademicYear.Name }).FirstOrDefaultAsync(ct);
        if (byQuarter is not null) return (byQuarter.AcademicYearId, byQuarter.Name);
        var profile = (await settings.GetProfileAsync(ct)).AcademicYear;
        if (string.IsNullOrWhiteSpace(profile)) return (null, null);
        var id = await db.AcademicYears.AsNoTracking().Where(y => y.Name == profile).Select(y => (int?)y.Id).FirstOrDefaultAsync(ct);
        return (id, profile);
    }

    private sealed record InspectionKey(int Id, int AreaId, int SlotId, int Round, FloorInspectionStatus Status, int Issues);

    private Task<List<InspectionKey>> DayInspectionsAsync(DateOnly date, CancellationToken ct) =>
        db.FloorInspections.AsNoTracking().Where(i => i.Date == date)
            .Select(i => new InspectionKey(i.Id, i.FloorAreaId, i.TimeSlotId, i.Round, i.Status, i.IssueCount)).ToListAsync(ct);

    private async Task<Dictionary<int, AreaLastInspection>> LastInspectionsAsync(IReadOnlyCollection<int> areaIds, CancellationToken ct)
    {
        if (areaIds.Count == 0) return [];
        var rows = await db.FloorAreas.AsNoTracking().Where(a => areaIds.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                Last = db.FloorInspections.Where(i => i.FloorAreaId == a.Id && i.Status == FloorInspectionStatus.Completed)
                    .OrderByDescending(i => i.CompletedAt)
                    .Select(i => new { i.Id, i.Date, i.CompletedAt, i.InspectorName, i.IssueCount, i.IssueSummary }).FirstOrDefault(),
            }).ToListAsync(ct);
        return rows.Where(r => r.Last is not null)
            .ToDictionary(r => r.Id, r => new AreaLastInspection(r.Last!.Id, r.Last.Date, r.Last.CompletedAt!.Value, r.Last.InspectorName, r.Last.IssueCount, r.Last.IssueSummary));
    }

    private static RoundProgress Progress(AuditSession s, int round, IEnumerable<FloorAreaInfo> areas, IReadOnlyCollection<InspectionKey> inspections,
        DateOnly date, DateOnly today, TimeOnly now)
    {
        var required = areas.Where(a => FloorAuditSchedule.IsRequired(a.Ref, s)).Select(a => a.Id).ToHashSet();
        var mine = inspections.Where(i => i.SlotId == s.SlotId && i.Round == round && required.Contains(i.AreaId)).ToList();
        var completed = mine.Count(i => i.Status == FloorInspectionStatus.Completed);
        var status = FloorAuditSchedule.RoundStatus(s, round, date, today, now, required.Count, completed, mine.Count);
        return new RoundProgress(round, s.RoundTime(round), status, required.Count, completed, mine.Count);
    }

    public async Task<FloorAuditHome> HomeAsync(DateOnly date, CancellationToken ct = default)
    {
        var user = await access.CurrentAsync(ct);
        var schedule = await ScheduleAsync(date, ct);
        var floors = await db.Floors.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new { f.Id, f.Name }).ToListAsync(ct);
        var areas = (await AreasAsync(true, ct)).ToList();
        var inspections = await DayInspectionsAsync(date, ct);
        var today = clock.Today;
        var now = clock.TimeOfDay;
        var session = schedule.CurrentAt(date == today ? now : date < today ? TimeOnly.MaxValue : TimeOnly.MinValue);
        var round = session is null ? 1 : AuditDaySchedule.CurrentRound(session, date, today, now);
        var last = await LastInspectionsAsync(areas.Select(a => a.Id).ToList(), ct);
        var counts = await movements.CountsAsync(date, null, ct);
        var outsideByFloor = await movements.OutsideByFloorAsync(date, ct);
        var todayDone = inspections.Where(i => i.Status == FloorInspectionStatus.Completed).ToList();

        var cards = floors.Select(f =>
        {
            var fa = areas.Where(a => a.FloorId == f.Id).ToList();
            var rounds = schedule.Active.SelectMany(s => Enumerable.Range(1, FloorAuditRounds.Count).Select(r => Progress(s, r, fa, inspections, date, today, now)))
                .Where(p => p.HasAreas).ToList();
            return new FloorCard(
                f.Id, f.Name, fa.Count(a => a.Kind == FloorAreaKind.Classroom), fa.Count(a => a.Kind == FloorAreaKind.Facility),
                session is null ? null : Progress(session, round, fa, inspections, date, today, now),
                rounds.Count(p => p.Status == AuditRoundStatus.Completed),
                rounds.Count(p => p.Status is AuditRoundStatus.Upcoming or AuditRoundStatus.Due or AuditRoundStatus.InProgress),
                rounds.Count(p => p.Status == AuditRoundStatus.Missed),
                outsideByFloor.GetValueOrDefault(f.Id),
                fa.Sum(a => last.GetValueOrDefault(a.Id)?.Issues ?? 0),
                todayDone.Count(i => fa.Any(a => a.Id == i.AreaId)));
        }).ToList();

        var pending = new List<PendingArea>();
        if (session is not null && FloorAuditSchedule.HasStarted(session, round, date, today, now))
        {
            var done = inspections.Where(i => i.SlotId == session.SlotId && i.Round == round).ToDictionary(i => i.AreaId);
            foreach (var a in areas.Where(a => FloorAuditSchedule.IsRequired(a.Ref, session)))
            {
                var i = done.GetValueOrDefault(a.Id);
                if (i?.Status == FloorInspectionStatus.Completed) continue;
                pending.Add(new PendingArea(a.Id, a.Name, a.FloorName, a.FloorId, a.TypeLabel,
                    FloorAuditSchedule.AreaStatus(true, i?.Status, i?.Issues ?? 0, true)));
            }
        }

        return new FloorAuditHome
        {
            Date = date, IsToday = date == today, Now = clock.Now, AcademicYear = await AcademicYearAsync(date, ct), Schedule = schedule,
            Session = session, Round = round,
            Overall = session is null ? null : Progress(session, round, areas, inspections, date, today, now),
            Floors = cards,
            Sessions = schedule.Sessions.Select(s => new SessionOverview(s, s.Cancelled ? []
                : Enumerable.Range(1, FloorAuditRounds.Count).Select(r => Progress(s, r, areas, inspections, date, today, now)).ToList())).ToList(),
            Movements = counts,
            RecentIssues = await BriefsAsync(db.FloorInspections.Where(i => i.Date == date && i.Status == FloorInspectionStatus.Completed && i.IssueCount > 0)
                .OrderByDescending(i => i.CompletedAt), 8, ct),
            PendingAreas = pending,
            User = user,
            OpenIssues = cards.Sum(c => c.OpenIssues),
            AreasConfigured = areas.Count,
            RoundCounts = schedule.Active.SelectMany(s => Enumerable.Range(1, FloorAuditRounds.Count).Select(r => Progress(s, r, areas, inspections, date, today, now)))
                .Where(p => p.HasAreas).GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
            ClassroomsInspected = todayDone.Select(i => i.AreaId).Distinct().Count(id => areas.Any(a => a.Id == id && a.Kind == FloorAreaKind.Classroom)),
            FacilitiesInspected = todayDone.Select(i => i.AreaId).Distinct().Count(id => areas.Any(a => a.Id == id && a.Kind == FloorAreaKind.Facility)),
        };
    }

    /// <summary>
    /// Classroom &amp; Facility Inspections: every active floor's areas for one session and round (the current ones by
    /// default), so an inspector can record audits floor by floor from one page.
    /// </summary>
    public async Task<InspectionsPage> InspectionsAsync(DateOnly date, int? slotId, int? round, int? floorId, bool pendingOnly, CancellationToken ct = default)
    {
        var floors = await db.Floors.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new ValueTuple<int, string>(f.Id, f.Name)).ToListAsync(ct);
        var pages = new List<FloorPage>();
        AuditSession? session = null;
        var r = 0;
        foreach (var (id, _) in floors)
        {
            if (floorId is { } only && only != id) continue;
            // The first floor settles the session and round (the current ones unless chosen); the others use the same.
            var page = await FloorAsync(id, date, session?.SlotId ?? slotId, session is null ? round : r, ct);
            if (page is null) continue;
            session ??= page.Session;
            r = page.Round;
            pages.Add(page);
        }
        var today = clock.Today;
        var now = clock.TimeOfDay;
        return new InspectionsPage
        {
            Date = date, IsToday = date == today, Schedule = pages.FirstOrDefault()?.Schedule ?? await ScheduleAsync(date, ct), Session = session,
            Round = r == 0 ? 1 : r, FloorId = floorId, PendingOnly = pendingOnly, Floors = pages, AllFloors = floors,
            User = await access.CurrentAsync(ct),
            Rounds = session is null ? [] : Enumerable.Range(1, FloorAuditRounds.Count).Select(x =>
            {
                var rs = pages.Select(p => p.Rounds.First(y => y.Round == x)).ToList();
                int required = rs.Sum(y => y.Required), completed = rs.Sum(y => y.Completed), started = rs.Sum(y => y.Started);
                return new RoundProgress(x, session.RoundTime(x), FloorAuditSchedule.RoundStatus(session, x, date, today, now, required, completed, started),
                    required, completed, started);
            }).ToList(),
        };
    }

    public async Task<FloorPage?> FloorAsync(int floorId, DateOnly date, int? slotId, int? round, CancellationToken ct = default)
    {
        var floor = await db.Floors.AsNoTracking().FirstOrDefaultAsync(f => f.Id == floorId, ct);
        if (floor is null) return null;
        var user = await access.CurrentAsync(ct);
        var schedule = await ScheduleAsync(date, ct);
        var today = clock.Today;
        var now = clock.TimeOfDay;
        var current = schedule.CurrentAt(date == today ? now : date < today ? TimeOnly.MaxValue : TimeOnly.MinValue);
        var session = slotId is { } sid ? schedule.Active.FirstOrDefault(s => s.SlotId == sid) ?? current : current;
        var r = round is { } rr && FloorAuditRounds.IsValid(rr) ? rr : session is null ? 1 : AuditDaySchedule.CurrentRound(session, date, today, now);
        var areas = (await AreasAsync(true, ct)).Where(a => a.FloorId == floorId).ToList();
        var inspections = await DayInspectionsAsync(date, ct);
        var last = await LastInspectionsAsync(areas.Select(a => a.Id).ToList(), ct);
        var started = session is not null && FloorAuditSchedule.HasStarted(session, r, date, today, now);

        AreaCard Card(FloorAreaInfo a)
        {
            var required = session is not null && FloorAuditSchedule.IsRequired(a.Ref, session);
            var i = session is null ? null : inspections.FirstOrDefault(x => x.AreaId == a.Id && x.SlotId == session.SlotId && x.Round == r);
            var done = session is null ? 0 : inspections.Count(x => x.AreaId == a.Id && x.SlotId == session.SlotId && x.Status == FloorInspectionStatus.Completed);
            var status = session is null ? AreaAuditStatus.NotScheduled : FloorAuditSchedule.AreaStatus(required, i?.Status, i?.Issues ?? 0, started);
            var canInspect = session is not null && (required || i is not null) && started && user.CanInspect(floorId) && user.HasIdentity;
            return new AreaCard(a, status, required, i?.Id, done, required ? FloorAuditRounds.Count : 0, last.GetValueOrDefault(a.Id), canInspect);
        }

        var floorMovements = await movements.ForFloorAsync(floorId, date, ct);
        return new FloorPage
        {
            Floor = floor,
            AllFloors = await db.Floors.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.SortOrder).Select(f => new ValueTuple<int, string>(f.Id, f.Name)).ToListAsync(ct),
            Date = date, IsToday = date == today, Now = clock.Now, AcademicYear = await AcademicYearAsync(date, ct), Schedule = schedule,
            Session = session, CurrentSession = current, Round = r,
            Rounds = session is null ? [] : Enumerable.Range(1, FloorAuditRounds.Count).Select(x => Progress(session, x, areas, inspections, date, today, now)).ToList(),
            Classrooms = areas.Where(a => a.Kind == FloorAreaKind.Classroom).Select(Card).ToList(),
            Facilities = areas.Where(a => a.Kind == FloorAreaKind.Facility).Select(Card).ToList(),
            Movements = floorMovements,
            Recent = await BriefsAsync(db.FloorInspections.Where(i => i.FloorId == floorId).OrderByDescending(i => i.UpdatedAt), 10, ct),
            User = user,
            OverdueMinutes = await movements.OverdueMinutesAsync(ct),
        };
    }

    private static readonly System.Linq.Expressions.Expression<Func<FloorAreaInspection, InspectionBrief>> BriefProjection = i => new InspectionBrief(
        i.Id, i.FloorAreaId, i.FloorId, i.Floor.Name, i.AreaName, i.AreaKind, i.Date, i.TimeSlotId, i.SessionLabel, i.SessionStart, i.Round,
        i.ScheduledTime, i.Status, i.StartedAt, i.CompletedAt, i.InspectorName, i.IssueCount, i.IssueSummary, i.Observations);

    internal static Task<List<InspectionBrief>> BriefsAsync(IQueryable<FloorAreaInspection> q, int take, CancellationToken ct) =>
        q.AsNoTracking().Take(take).Select(BriefProjection).ToListAsync(ct);

    internal static IQueryable<InspectionBrief> Briefs(IQueryable<FloorAreaInspection> q) => q.AsNoTracking().Select(BriefProjection);

    // ---------------------------------------------------------------- inspection form and save

    private async Task<List<FloorAuditItem>> ActiveItemsAsync(FloorAreaKind kind, CancellationToken ct) =>
        (await db.FloorAuditItems.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct))
        .Where(i => FloorAuditLabels.Applies(i.AppliesTo, kind)).ToList();

    public async Task<InspectionFormModel?> InspectionFormAsync(int areaId, DateOnly date, int slotId, int round, CancellationToken ct = default)
    {
        var area = (await AreasAsync(false, ct)).FirstOrDefault(a => a.Id == areaId);
        if (area is null || !FloorAuditRounds.IsValid(round)) return null;
        var schedule = await ScheduleAsync(date, ct);
        var session = schedule.Get(slotId);
        var existing = await db.FloorInspections.AsNoTracking().Include(i => i.Responses).Include(i => i.Attachments)
            .FirstOrDefaultAsync(i => i.FloorAreaId == areaId && i.Date == date && i.TimeSlotId == slotId && i.Round == round, ct);
        if (session is null && existing is null) return null;
        var user = await access.CurrentAsync(ct);
        var form = new InspectionFormModel
        {
            AreaId = areaId, Date = SchoolWeek.Iso(date), SlotId = slotId, Round = round, Area = area, Session = session,
            FloorName = area.FloorName, ScheduledTime = existing?.ScheduledTime ?? session?.RoundTime(round),
        };
        if (existing is not null)
        {
            form.InspectionId = existing.Id;
            form.Status = existing.Status;
            form.Observations = existing.Observations;
            form.StartedBy = existing.InspectorName;
            form.StartedAt = existing.StartedAt;
            form.CompletedAt = existing.CompletedAt;
            form.CompletedBy = existing.CompletedBy;
            form.FloorName = await db.Floors.Where(f => f.Id == existing.FloorId).Select(f => f.Name).FirstAsync(ct);
            form.Questions = existing.Responses.Where(r => r.ItemId is not null).OrderBy(r => r.SortOrder)
                .Select(r => new ChecklistQuestion(r.ItemId!.Value, r.ItemName, r.ItemDescription, r.ResponseType, r.IsRequired, r.Value, r.IsIssue)).ToList();
            form.Extras = existing.Responses.Where(r => r.ItemId is null).OrderBy(r => r.SortOrder)
                .Select(r => new ExtraFinding
                {
                    Kind = r.ResponseType == AuditResponseType.IssueText ? ExtraFinding.Issue : ExtraFinding.Observation, About = r.ItemDescription, Text = r.Value,
                }).ToList();
            form.Answers = form.Questions.ToDictionary(q => q.ItemId, q => q.Value);
            form.Attachments = existing.Attachments.OrderBy(a => a.UploadedAt)
                .Select(a => new InspectionAttachmentView(a.Id, a.FileName, a.Extension, a.SizeBytes, a.UploadedAt, a.UploadedBy)).ToList();
        }
        else
        {
            form.Questions = (await ActiveItemsAsync(area.Kind, ct))
                .Select(i => new ChecklistQuestion(i.Id, i.Name, i.Description, i.ResponseType, i.IsRequired, null, false)).ToList();
        }

        var (readOnly, reason) = await CanEditAsync(user, area, session, date, round, existing?.Status, existing?.FloorId, ct);
        form.ReadOnly = readOnly;
        form.ReadOnlyReason = reason;
        form.IsCorrection = !readOnly && existing?.Status == FloorInspectionStatus.Completed;
        form.InspectorName = existing?.InspectorName ?? user.Name;
        return form;
    }

    /// <summary>Whether the current user may save this inspection now, and why not.</summary>
    private async Task<(bool ReadOnly, string? Reason)> CanEditAsync(FloorAuditUser user, FloorAreaInfo area, AuditSession? session, DateOnly date, int round,
        FloorInspectionStatus? status, int? inspectionFloorId, CancellationToken ct)
    {
        await Task.CompletedTask;
        var floorId = inspectionFloorId ?? area.FloorId;
        if (!user.HasIdentity) return (true, "Choose who you are under “Working as” (the panel at the top of the Floor Audit pages) to record inspections: every inspection is recorded with its inspector.");
        if (!user.CanInspect(floorId)) return (true, "You are not an inspector of this floor. A Floor Audit manager can give you access.");
        if (status == FloorInspectionStatus.Completed)
            return user.IsManager ? (false, null) : (true, "This inspection is completed. Only a Floor Audit manager can correct it.");
        if (status == FloorInspectionStatus.InProgress) return (false, null);
        // A new inspection.
        if (session is null) return (true, "This session is not on the timetable of this date.");
        if (session.Cancelled) return (true, "This session is cancelled on this date: it has no audit rounds.");
        if (!area.IsActive) return (true, "This area is inactive: it is not inspected.");
        if (!FloorAuditSchedule.IsRequired(area.Ref, session)) return (true, "This area is not inspected in this session (no lesson for its class, or a period of another class).");
        var today = clock.Today;
        if (date > today) return (true, "This date has not come yet.");
        if (!FloorAuditSchedule.HasStarted(session, round, date, today, clock.TimeOfDay))
            return (true, $"{FloorAuditRounds.Label(round)} starts at {FloorAuditLabels.Time(session.RoundTime(round))}.");
        return (false, null);
    }

    public async Task<ServiceResult> SaveInspectionAsync(InspectionFormModel form, bool complete, IReadOnlyList<UploadedFile> uploads, CancellationToken ct = default)
    {
        if (SchoolWeekParse(form.Date) is not { } date) return ServiceResult.Fail("", "The date is not valid.");
        if (!FloorAuditRounds.IsValid(form.Round)) return ServiceResult.Fail("", "Choose round 1, 2 or 3.");
        var area = (await AreasAsync(false, ct)).FirstOrDefault(a => a.Id == form.AreaId);
        if (area is null) return ServiceResult.Fail("", "This area no longer exists.");
        var user = await access.CurrentAsync(ct);
        var schedule = await ScheduleAsync(date, ct);
        var session = schedule.Get(form.SlotId);

        var observations = form.Observations?.Trim();
        if (observations?.Length > FloorAreaInspection.ObservationsMaxLength)
            return ServiceResult.Fail("Observations", $"Use {FloorAreaInspection.ObservationsMaxLength} characters or fewer.");

        // Files are checked before anything is written.
        var checkedFiles = new List<(UploadedFile File, string Ext, MemoryStream Data)>();
        try
        {
            var result = new ServiceResult();
            foreach (var f in uploads.Where(u => u.Length > 0))
            {
                var ext = TaskAttachmentRules.ExtensionOf(f.FileName);
                if (!AttachmentExtensions.Contains(ext)) { result.AddError("Photos", $"{f.FileName}: attach a photo (PNG or JPG) or a PDF."); continue; }
                if (f.Length > TaskAttachmentRules.MaxBytes) { result.AddError("Photos", $"{f.FileName} is larger than {TaskAttachmentRules.SizeLabel(TaskAttachmentRules.MaxBytes)}."); continue; }
                var ms = new MemoryStream();
                await using (var s = f.Open()) await s.CopyToAsync(ms, ct);
                var head = ms.GetBuffer().AsSpan(0, (int)Math.Min(ms.Length, 1024));
                if (TaskAttachmentRules.CheckContent(ext, head, ms) is { } bad) { result.AddError("Photos", $"{f.FileName}: {bad}"); ms.Dispose(); continue; }
                checkedFiles.Add((f, ext, ms));
            }
            if (!result.Succeeded) return result;

            // The unique index (area, date, session, round) decides between two people starting the same inspection.
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var inspection = await db.FloorInspections.Include(i => i.Responses).Include(i => i.Attachments)
                .FirstOrDefaultAsync(i => i.FloorAreaId == area.Id && i.Date == date && i.TimeSlotId == form.SlotId && i.Round == form.Round, ct);
            var (readOnly, reason) = await CanEditAsync(user, area, session, date, form.Round, inspection?.Status, inspection?.FloorId, ct);
            if (readOnly) return ServiceResult.Fail("", reason!);
            var correcting = inspection?.Status == FloorInspectionStatus.Completed;
            var correctionReason = TextNormalizer.CleanText(form.Reason);
            if (correcting && correctionReason.Length == 0) return ServiceResult.Fail("Reason", "Enter the reason for correcting a completed inspection.");
            if (correcting && !complete) return ServiceResult.Fail("", "A completed inspection stays completed: save the correction with “Save correction”.");

            var isNew = inspection is null;
            if (inspection is null)
            {
                var year = await AcademicYearOfAsync(date, ct);
                inspection = new FloorAreaInspection
                {
                    FloorAreaId = area.Id, FloorId = area.FloorId, AreaName = Trunc(area.Name, FloorAreaInspection.AreaNameMaxLength), AreaKind = area.Kind,
                    SchoolClassId = area.ClassId, AcademicYearId = year.Id, Date = date, TimeSlotId = session!.SlotId,
                    SessionLabel = Trunc(session.Label, FloorAreaInspection.SessionLabelMaxLength), SessionStart = session.Start, Round = form.Round,
                    ScheduledTime = session.RoundTime(form.Round), Status = FloorInspectionStatus.InProgress, StartedAt = clock.UtcNow,
                    InspectorStaffId = user.StaffId, InspectorName = Trunc(user.Name, FloorAreaInspection.PersonMaxLength),
                };
                // The checklist as it is now: later edits to the items never change this inspection.
                var order = 0;
                foreach (var item in await ActiveItemsAsync(area.Kind, ct))
                    inspection.Responses.Add(new FloorInspectionResponse
                    {
                        ItemId = item.Id, ItemName = item.Name, ItemDescription = item.Description, ResponseType = item.ResponseType,
                        IsRequired = item.IsRequired, SortOrder = ++order,
                    });
                db.FloorInspections.Add(inspection);
            }

            if (inspection.Attachments.Count + checkedFiles.Count > MaxAttachments)
            {
                db.ChangeTracker.Clear();
                return ServiceResult.Fail("Photos", $"An inspection can have at most {MaxAttachments} attachments.");
            }

            // Answers.
            var errors = new ServiceResult();
            var before = correcting ? Describe(inspection) : null;
            foreach (var r in inspection.Responses)
            {
                var raw = r.ItemId is { } id ? form.Answers.GetValueOrDefault(id) : r.Value;
                var (value, error) = AuditResponses.Clean(r.ResponseType, raw);
                if (error is not null) { errors.AddError($"Answers[{r.ItemId}]", $"{r.ItemName}: {error}"); continue; }
                r.Value = value;
                r.IsIssue = AuditResponses.IsIssue(r.ResponseType, value);
                if (complete && r.IsRequired && value is null) errors.AddError($"Answers[{r.ItemId}]", $"{r.ItemName} is required to complete the inspection.");
            }
            // Additional observations / issues ("+ Add Observation / Issue"): replaced as a whole on every save.
            var extras = (form.Extras ?? []).Select(e => new { Kind = e.Kind?.Trim().ToLowerInvariant(), About = TextNormalizer.CleanText(e.About), Text = e.Text?.Trim() ?? "" })
                .Where(e => e.Text.Length > 0).ToList();
            if (extras.Count > ExtraFinding.MaxPerInspection) errors.AddError("Extras", $"Add at most {ExtraFinding.MaxPerInspection} additional observations or issues.");
            foreach (var e in extras)
            {
                if (e.Kind is not (ExtraFinding.Observation or ExtraFinding.Issue)) errors.AddError("Extras", "Choose Observation or Issue for each additional finding.");
                if (e.Text.Length > FloorInspectionResponse.ValueMaxLength) errors.AddError("Extras", $"Keep each additional finding to {FloorInspectionResponse.ValueMaxLength} characters.");
            }
            if (errors.Succeeded)
            {
                foreach (var old in inspection.Responses.Where(r => r.ItemId is null).ToList())
                {
                    inspection.Responses.Remove(old);
                    if (old.Id != 0) db.FloorInspectionResponses.Remove(old);
                }
                var extraOrder = 1000;
                foreach (var e in extras)
                {
                    var issue = e.Kind == ExtraFinding.Issue;
                    var label = e.About.Length == 0 ? (issue ? "Additional issue" : "Additional observation") : $"{e.About} — {(issue ? "issue" : "observation")}";
                    inspection.Responses.Add(new FloorInspectionResponse
                    {
                        ItemId = null, ItemName = Trunc(label, FloorAuditItem.NameMaxLength),
                        ItemDescription = e.About.Length == 0 ? null : Trunc(e.About, FloorAuditItem.DescriptionMaxLength),
                        ResponseType = issue ? AuditResponseType.IssueText : AuditResponseType.Text, IsRequired = false, SortOrder = ++extraOrder, Value = e.Text, IsIssue = issue,
                    });
                }
            }
            if (!errors.Succeeded)
            {
                // Nothing of a refused save stays attached to the context (a new inspection, changed answers).
                db.ChangeTracker.Clear();
                return errors;
            }
            inspection.Observations = string.IsNullOrEmpty(observations) ? null : observations;
            var issues = inspection.Responses.Where(r => r.IsIssue).OrderBy(r => r.SortOrder).ToList();
            inspection.IssueCount = issues.Count;
            inspection.IssueSummary = issues.Count == 0 ? null
                : Trunc(string.Join("; ", issues.Select(i => $"{i.ItemName}: {i.Value}")), FloorAreaInspection.IssueSummaryMaxLength);
            inspection.UpdatedBy = user.Name;
            if (complete && !correcting)
            {
                inspection.Status = FloorInspectionStatus.Completed;
                inspection.CompletedAt = clock.UtcNow;
                inspection.CompletedBy = Trunc(user.Name, FloorAreaInspection.PersonMaxLength);
            }

            var stored = new List<string>();
            foreach (var (f, ext, data) in checkedFiles)
            {
                var name = Guid.NewGuid().ToString("N");
                await files.SaveAsync(name, data, ct);
                stored.Add(name);
                inspection.Attachments.Add(new FloorInspectionAttachment
                {
                    StoredName = name, FileName = TaskAttachmentRules.CleanName(f.FileName, ext), Extension = ext, SizeBytes = data.Length,
                    UploadedAt = clock.UtcNow, UploadedBy = user.Name,
                });
            }

            try
            {
                await db.SaveChangesAsync(ct);
                var what = $"{inspection.AreaName} · {inspection.SessionLabel} · {FloorAuditRounds.Label(inspection.Round)} ({SchoolWeek.Iso(date)})";
                var action = correcting ? "Corrected" : complete ? "Completed" : isNew ? "Started" : "Saved";
                var text = correcting
                    ? $"{what}: corrected. Reason: {correctionReason}. Before: {before}. After: {Describe(inspection)}."
                    : $"{what}: {action.ToLowerInvariant()}{(inspection.IssueCount > 0 ? $" with {Ui.Plural(inspection.IssueCount, "issue", "issues")} ({inspection.IssueSummary})" : "")}" +
                      (checkedFiles.Count > 0 ? $"; {Ui.Plural(checkedFiles.Count, "attachment", "attachments")} added" : "") + ".";
                FloorAuditLog.Add(db, currentUser, "Inspection", inspection.Id, inspection.FloorId, action, text);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                foreach (var n in stored) files.Delete(n);
                return ServiceResult.Fail("", "This area was just saved for this round by someone else. Reload to see their inspection.");
            }
            catch
            {
                foreach (var n in stored) files.Delete(n);
                throw;
            }
            return ServiceResult.Ok(inspection.Id);
        }
        finally
        {
            foreach (var (_, _, data) in checkedFiles) data.Dispose();
        }
    }

    private static string Describe(FloorAreaInspection i) =>
        string.Join("; ", i.Responses.OrderBy(r => r.SortOrder).Select(r => $"{r.ItemName}: {r.Value ?? "—"}")) +
        (string.IsNullOrEmpty(i.Observations) ? "" : $"; Observations: {i.Observations}");

    private static string Trunc(string? s, int max) => s is null ? "" : s.Length > max ? s[..max] : s;

    private static DateOnly? SchoolWeekParse(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    // ---------------------------------------------------------------- details, area page, attachments

    public async Task<InspectionDetails?> InspectionAsync(int id, CancellationToken ct = default)
    {
        var brief = await Briefs(db.FloorInspections.Where(i => i.Id == id)).FirstOrDefaultAsync(ct);
        if (brief is null) return null;
        var raw = await db.FloorInspections.AsNoTracking().Where(i => i.Id == id)
            .Select(i => new { i.CompletedBy, Year = i.AcademicYear!.Name }).FirstAsync(ct);
        var user = await access.CurrentAsync(ct);
        return new InspectionDetails
        {
            Inspection = brief,
            AcademicYear = raw.Year,
            CompletedBy = raw.CompletedBy,
            Responses = await db.FloorInspectionResponses.AsNoTracking().Where(r => r.InspectionId == id).OrderBy(r => r.SortOrder)
                .Select(r => new InspectionResponseView(r.ItemName, r.ItemDescription, r.ResponseType, r.IsRequired, r.Value, r.IsIssue)).ToListAsync(ct),
            Attachments = await db.FloorInspectionAttachments.AsNoTracking().Where(a => a.InspectionId == id).OrderBy(a => a.UploadedAt)
                .Select(a => new InspectionAttachmentView(a.Id, a.FileName, a.Extension, a.SizeBytes, a.UploadedAt, a.UploadedBy)).ToListAsync(ct),
            Log = await LogAsync("Inspection", id, ct),
            CanContinue = brief.Status == FloorInspectionStatus.InProgress && user.HasIdentity && user.CanInspect(brief.FloorId),
            CanCorrect = brief.Status == FloorInspectionStatus.Completed && user.IsManager && user.HasIdentity,
        };
    }

    internal Task<List<AuditLogView>> LogAsync(string entity, int id, CancellationToken ct) =>
        db.FloorAuditLog.AsNoTracking().Where(l => l.Entity == entity && l.EntityId == id).OrderByDescending(l => l.At).ThenByDescending(l => l.Id)
            .Select(l => new AuditLogView(l.Entity, l.EntityId, l.Action, l.Text, l.At, l.By)).ToListAsync(ct);

    public async Task<AreaPage?> AreaAsync(int areaId, CancellationToken ct = default)
    {
        var area = (await AreasAsync(false, ct)).FirstOrDefault(a => a.Id == areaId);
        if (area is null) return null;
        var last = await LastInspectionsAsync([areaId], ct);
        var history = await Briefs(db.FloorInspections.Where(i => i.FloorAreaId == areaId).OrderByDescending(i => i.Date).ThenByDescending(i => i.SessionStart)
            .ThenByDescending(i => i.Round)).Take(200).ToListAsync(ct);
        // Floors the area has been inspected on (it may have moved): first inspection on each.
        var changes = await db.FloorInspections.AsNoTracking().Where(i => i.FloorAreaId == areaId)
            .GroupBy(i => i.Floor.Name).Select(g => new { Floor = g.Key, Since = g.Min(i => i.StartedAt) }).ToListAsync(ct);
        var user = await access.CurrentAsync(ct);
        var today = clock.Today;
        var now = clock.TimeOfDay;
        var schedule = await ScheduleAsync(today, ct);
        // The latest session whose round has started and that this area takes part in; else the next one.
        var session = schedule.Active.Where(x => FloorAuditSchedule.IsRequired(area.Ref, x) && x.RoundTime(1) <= now).LastOrDefault()
                      ?? schedule.Active.FirstOrDefault(x => FloorAuditSchedule.IsRequired(area.Ref, x));
        var round = session is null ? 1 : AuditDaySchedule.CurrentRound(session, today, today, now);
        string? note = null;
        AreaAuditStatus? status = null;
        if (session is null) note = schedule.IsSchoolDay ? "This area has no audit round today (no timetable session it takes part in)." : "Today is not a school day: there are no audit rounds.";
        else
        {
            var started = FloorAuditSchedule.HasStarted(session, round, today, today, now);
            var existing = await db.FloorInspections.AsNoTracking().Where(i => i.FloorAreaId == areaId && i.Date == today && i.TimeSlotId == session.SlotId && i.Round == round)
                .Select(i => new { i.Status, i.IssueCount }).FirstOrDefaultAsync(ct);
            status = FloorAuditSchedule.AreaStatus(true, existing?.Status, existing?.IssueCount ?? 0, started);
            var (readOnly, reason) = await CanEditAsync(user, area, session, today, round, existing?.Status, null, ct);
            note = readOnly ? reason : null;
        }
        return new AreaPage
        {
            Area = area, Last = last.GetValueOrDefault(areaId), History = history,
            Log = await LogAsync("Area", areaId, ct),
            FloorChanges = changes.OrderBy(c => c.Since).Select(c => (c.Floor, c.Since)).ToList(),
            User = user, Session = session, Round = round, CanRecord = session is not null && note is null, RecordNote = note, RoundStatus = status,
        };
    }

    public async Task<(InspectionAttachmentView File, Stream Content)?> AttachmentAsync(int inspectionId, int attachmentId, CancellationToken ct = default)
    {
        var a = await db.FloorInspectionAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId && x.InspectionId == inspectionId, ct);
        if (a is null) return null;
        var stream = files.Open(a.StoredName);
        return stream is null ? null : (new InspectionAttachmentView(a.Id, a.FileName, a.Extension, a.SizeBytes, a.UploadedAt, a.UploadedBy), stream);
    }
}
