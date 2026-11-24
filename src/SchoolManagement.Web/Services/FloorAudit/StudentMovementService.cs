using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Services;

public interface IStudentMovementService
{
    Task<MovementCounts> CountsAsync(DateOnly date, int? floorId, CancellationToken ct = default);
    Task<Dictionary<int, int>> OutsideByFloorAsync(DateOnly date, CancellationToken ct = default);
    Task<IReadOnlyList<MovementRow>> ForFloorAsync(int floorId, DateOnly date, CancellationToken ct = default);
    Task<MovementPage> PageAsync(MovementFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<MovementRow>> ExportRowsAsync(MovementFilter filter, CancellationToken ct = default);
    Task<MovementRow?> RowAsync(int id, CancellationToken ct = default);
    Task<MovementRow?> OpenForStudentAsync(int studentId, CancellationToken ct = default);
    Task<MovementFormModel> FormAsync(MovementFormModel? posted, CancellationToken ct = default);
    Task<ServiceResult> RecordAsync(MovementFormModel form, CancellationToken ct = default);
    Task<ServiceResult> ReturnAsync(int id, string? time, CancellationToken ct = default);
    Task<ServiceResult> VoidAsync(int id, string? reason, CancellationToken ct = default);
    Task<int> OverdueMinutesAsync(CancellationToken ct = default);
    Task<ServiceResult> SaveOverdueMinutesAsync(int? minutes, CancellationToken ct = default);
}

/// <summary>
/// Student Access: students leaving class and coming back. The exit and return are the actual times recorded by staff
/// (the return is never filled in automatically); an open movement shows the time elapsed so far. A student has at most
/// one open movement. Nothing here changes attendance: a student outside the classroom is not absent from school.
/// </summary>
public sealed class StudentMovementService(SchoolDbContext db, ICurrentUser currentUser, IFloorAuditAccessService access, ISchoolTimeService clock)
    : IStudentMovementService
{
    public const string OverdueKey = "FloorAudit.OverdueMinutes";
    public const int DefaultOverdueMinutes = 10;
    public const int MaxOverdueMinutes = 240;
    public const int ExportLimit = 5000;

    public async Task<int> OverdueMinutesAsync(CancellationToken ct = default)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == OverdueKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is > 0 and <= MaxOverdueMinutes ? m : DefaultOverdueMinutes;
    }

    public async Task<ServiceResult> SaveOverdueMinutesAsync(int? minutes, CancellationToken ct = default)
    {
        var user = await access.CurrentAsync(ct);
        if (!user.CanManage) return ServiceResult.Fail("", "Only a Floor Management administrator can change this setting.");
        if (minutes is not { } m || m < 1 || m > MaxOverdueMinutes)
            return ServiceResult.Fail("OverdueMinutes", $"Enter a number of minutes from 1 to {MaxOverdueMinutes}.");
        var before = await OverdueMinutesAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == OverdueKey, ct);
        if (row is null) db.Settings.Add(new AppSetting { Key = OverdueKey, Value = m.ToString(CultureInfo.InvariantCulture) });
        else row.Value = m.ToString(CultureInfo.InvariantCulture);
        FloorAuditLog.Add(db, currentUser, "Settings", null, null, "Changed", $"Overdue threshold for students outside class: {before} → {m} minutes.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    /// <summary>Movements of a date (not voided), plus — on today — movements still open from earlier days.</summary>
    private IQueryable<StudentMovement> OfDay(DateOnly date)
    {
        var today = clock.Today;
        return db.StudentMovements.AsNoTracking().Where(m => m.VoidedAt == null
            && (m.Date == date || (date == today && m.Date < date && m.ReturnAt == null)));
    }

    private static MovementState State(DateTime exit, DateTime? ret, DateTime? voided, DateTime nowUtc, int overdue) =>
        voided is not null ? MovementState.Voided
        : ret is not null ? MovementState.Returned
        : (nowUtc - exit).TotalMinutes > overdue ? MovementState.Overdue
        : MovementState.Outside;

    private async Task<List<MovementRow>> RowsAsync(IQueryable<StudentMovement> q, CancellationToken ct)
    {
        var overdue = await OverdueMinutesAsync(ct);
        var now = clock.UtcNow;
        var rows = await q.Select(m => new
        {
            m.Id, m.StudentId, Student = m.Student.FullName, m.FromClassId, ClassName = m.FromClass.Name,
            FromName = m.FromArea == null ? null : (m.FromArea.Kind == FloorAreaKind.Classroom ? m.FromArea.SchoolClass!.Name : m.FromArea.Name),
            m.FromFloorId, FromFloor = m.FromFloor!.Name, m.ToName, m.ToFloorId, ToFloor = m.ToFloor!.Name, m.IsCustomDestination, m.Date,
            m.ExitAt, m.ReturnAt, m.VoidedAt, m.RecordedBy, m.ReturnedBy, m.Notes, m.VoidReason, m.VoidedBy,
        }).ToListAsync(ct);
        return rows.Select(m =>
        {
            var state = State(m.ExitAt, m.ReturnAt, m.VoidedAt, now, overdue);
            var end = m.ReturnAt ?? (m.VoidedAt ?? now);
            return new MovementRow(m.Id, m.StudentId, m.Student, m.FromClassId, m.ClassName, m.FromName, m.FromFloorId, m.FromFloor, m.ToName,
                m.ToFloorId, m.ToFloor, m.IsCustomDestination, m.Date, m.ExitAt, m.ReturnAt, state, end - m.ExitAt, m.RecordedBy, m.ReturnedBy, m.Notes,
                m.VoidReason, m.VoidedBy);
        }).ToList();
    }

    /// <summary>
    /// Summary figures. With a <paramref name="date"/> (a single day) the totals count that day's movements, while the
    /// open figures also include students still outside from earlier days; without one, every row counts.
    /// </summary>
    private static MovementCounts Count(IReadOnlyList<MovementRow> rows, DateOnly? date)
    {
        bool InTotal(MovementRow r) => date is not { } d || r.Date == d;
        var ofDate = rows.Where(InTotal).ToList();
        var returned = rows.Where(r => r.State == MovementState.Returned).ToList();
        var byDest = rows.GroupBy(r => (r.ToName, r.ToFloor))
            .Select(g => new DestinationCount(g.Key.ToName, g.Key.ToFloor, g.Count(InTotal), g.Count(r => r.IsOpen)))
            .OrderByDescending(d => d.Outside).ThenByDescending(d => d.Total).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new MovementCounts(
            ofDate.Count, rows.Count(r => r.IsOpen), returned.Count, rows.Count(r => r.State == MovementState.Overdue), byDest,
            returned.Count == 0 ? null : TimeSpan.FromTicks((long)returned.Average(r => r.Duration.Ticks)));
    }

    public async Task<MovementCounts> CountsAsync(DateOnly date, int? floorId, CancellationToken ct = default)
    {
        var q = OfDay(date);
        if (floorId is { } f) q = q.Where(m => m.FromFloorId == f || m.ToFloorId == f);
        return Count(await RowsAsync(q, ct), date);
    }

    public async Task<Dictionary<int, int>> OutsideByFloorAsync(DateOnly date, CancellationToken ct = default) =>
        await OfDay(date).Where(m => m.ReturnAt == null && m.FromFloorId != null)
            .GroupBy(m => m.FromFloorId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

    public async Task<IReadOnlyList<MovementRow>> ForFloorAsync(int floorId, DateOnly date, CancellationToken ct = default) =>
        (await RowsAsync(OfDay(date).Where(m => m.FromFloorId == floorId || m.ToFloorId == floorId), ct))
        .OrderByDescending(r => r.IsOpen).ThenByDescending(r => r.ExitAt).ToList();

    public async Task<MovementRow?> RowAsync(int id, CancellationToken ct = default) =>
        (await RowsAsync(db.StudentMovements.AsNoTracking().Where(m => m.Id == id), ct)).FirstOrDefault();

    /// <summary>
    /// The live board: every open movement (any date — a student not marked returned stays outside); the history: closed
    /// movements (returned or voided) in the date range.
    /// </summary>
    private IQueryable<StudentMovement> Filtered(MovementFilter f)
    {
        var q = f.History
            ? db.StudentMovements.AsNoTracking().Where(m => m.Date >= f.From && m.Date <= f.To && (m.ReturnAt != null || m.VoidedAt != null))
            : db.StudentMovements.AsNoTracking().Where(m => m.ReturnAt == null && m.VoidedAt == null);
        if (f.FloorId is { } floor) q = q.Where(m => m.FromFloorId == floor || m.ToFloorId == floor);
        if (f.ClassId is { } c) q = q.Where(m => m.FromClassId == c);
        if (!string.IsNullOrWhiteSpace(f.Student))
        {
            var s = f.Student.Trim();
            q = q.Where(m => m.Student.FullName.Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(f.Destination)) q = q.Where(m => m.ToName == f.Destination);
        // "overdue" is narrowed further by the threshold (PageAsync / ExportRowsAsync).
        return f.Status switch
        {
            "outside" or "overdue" => q.Where(m => m.ReturnAt == null && m.VoidedAt == null),
            "returned" => q.Where(m => m.ReturnAt != null && m.VoidedAt == null),
            "voided" => q.Where(m => m.VoidedAt != null),
            _ => q.Where(m => m.VoidedAt == null),
        };
    }

    public async Task<MovementPage> PageAsync(MovementFilter filter, CancellationToken ct = default)
    {
        var overdue = await OverdueMinutesAsync(ct);
        var q = Filtered(filter);
        if (filter.Status == "overdue")
        {
            var cutoff = clock.UtcNow.AddMinutes(-overdue);
            q = q.Where(m => m.ExitAt < cutoff);
        }
        var total = await q.CountAsync(ct);
        var size = Paging.NormalizePageSize(filter.PageSize);
        var page = Paging.ClampPage(filter.Page, total, size);
        var items = (await RowsAsync(q.OrderBy(m => m.ReturnAt == null && m.VoidedAt == null ? 0 : 1).ThenByDescending(m => m.ExitAt)
            .Skip((page - 1) * size).Take(size), ct));
        // Summary figures cover the whole filtered range (all pages).
        var countsRows = await RowsAsync(Filtered(filter with { Status = null }), ct);
        var counts = Count(countsRows, filter.History ? null : clock.Today);
        return new MovementPage
        {
            Filter = filter with { Page = page, PageSize = size },
            Result = new PagedResult<MovementRow> { Items = items, Page = page, PageSize = size, TotalCount = total },
            Counts = counts,
            Floors = await db.Floors.AsNoTracking().OrderBy(f => f.SortOrder).Select(f => new ValueTuple<int, string>(f.Id, f.Name)).ToListAsync(ct),
            Classes = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new ValueTuple<int, string>(c.Id, c.Name)).ToListAsync(ct),
            Destinations = await (filter.History ? db.StudentMovements.AsNoTracking().Where(m => m.Date >= filter.From && m.Date <= filter.To)
                    : db.StudentMovements.AsNoTracking().Where(m => m.ReturnAt == null && m.VoidedAt == null))
                .Select(m => m.ToName).Distinct().OrderBy(n => n).ToListAsync(ct),
            User = await access.CurrentAsync(ct),
            OverdueMinutes = overdue,
            Today = await CountsAsync(clock.Today, null, ct),
        };
    }

    public async Task<IReadOnlyList<MovementRow>> ExportRowsAsync(MovementFilter filter, CancellationToken ct = default)
    {
        var overdue = await OverdueMinutesAsync(ct);
        var q = Filtered(filter);
        if (filter.Status == "overdue")
        {
            var cutoff = clock.UtcNow.AddMinutes(-overdue);
            q = q.Where(m => m.ExitAt < cutoff);
        }
        return await RowsAsync(q.OrderBy(m => m.Date).ThenBy(m => m.ExitAt).Take(ExportLimit), ct);
    }

    // ---------------------------------------------------------------- recording

    public async Task<MovementFormModel> FormAsync(MovementFormModel? posted, CancellationToken ct = default)
    {
        var form = posted ?? new MovementFormModel();
        var areas = await db.FloorAreas.AsNoTracking().Where(a => a.IsActive && a.Floor.IsActive)
            .Select(a => new
            {
                a.Id, a.FloorId, Floor = a.Floor.Name, FloorOrder = a.Floor.SortOrder, a.Kind, a.SchoolClassId, ClassName = a.SchoolClass!.Name,
                ClassOrder = (int?)a.SchoolClass!.SortOrder, a.Name, a.FacilityType, a.Description, a.SortOrder,
            }).ToListAsync(ct);
        form.Areas = areas.OrderBy(a => a.FloorOrder).ThenByDescending(a => a.Kind).ThenBy(a => a.Kind == FloorAreaKind.Classroom ? a.ClassOrder ?? 0 : a.SortOrder)
            .ThenBy(a => a.Name ?? a.ClassName)
            .Select(a => new FloorAreaInfo(a.Id, a.FloorId, a.Floor, a.Kind, a.SchoolClassId, a.Kind == FloorAreaKind.Classroom ? a.ClassName ?? "" : a.Name ?? "",
                a.FacilityType, a.Description, true, a.SortOrder)).ToList();
        var rooms = form.Areas.Where(a => a.Kind == FloorAreaKind.Classroom && a.ClassId is not null).ToDictionary(a => a.ClassId!.Value);
        var open = (await db.StudentMovements.AsNoTracking().Where(m => m.ReturnAt == null && m.VoidedAt == null).Select(m => m.StudentId).ToListAsync(ct)).ToHashSet();
        var students = await db.Students.AsNoTracking().Where(s => s.SchoolClass.IsActive)
            .OrderBy(s => s.FullName).Select(s => new { s.Id, s.FullName, s.SchoolClassId, ClassName = s.SchoolClass.Name }).ToListAsync(ct);
        form.Students = students.Select(s =>
        {
            var room = rooms.GetValueOrDefault(s.SchoolClassId);
            return new MovementStudentOption(s.Id, s.FullName, s.SchoolClassId, s.ClassName, room?.Id, room?.FloorName, open.Contains(s.Id));
        }).ToList();
        return form;
    }

    private DateTime? ParseTimeToday(string? value, out string? error)
    {
        error = null;
        var text = TextNormalizer.CleanText(value);
        if (text.Length == 0) return null;
        if (!TimeOnly.TryParseExact(text, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            error = "Enter the time as HH:mm.";
            return null;
        }
        return clock.ToUtc(clock.Today, t);
    }

    public async Task<ServiceResult> RecordAsync(MovementFormModel form, CancellationToken ct = default)
    {
        var user = await access.CurrentAsync(ct);
        if (!user.HasIdentity) return ServiceResult.Fail("", "Choose who you are under “Working as” (the panel at the top of the Floor Management pages) to record student movements.");
        if (form.StudentId is not { } studentId) return ServiceResult.Fail("StudentId", "Choose the student.");
        var student = await db.Students.AsNoTracking().Where(s => s.Id == studentId)
            .Select(s => new { s.Id, s.FullName, s.SchoolClassId, ClassName = s.SchoolClass.Name, s.SchoolClass.IsActive }).FirstOrDefaultAsync(ct);
        if (student is null) return ServiceResult.Fail("StudentId", "This student no longer exists.");
        if (!student.IsActive) return ServiceResult.Fail("StudentId", $"{student.ClassName} is not an active class.");

        // From: the classroom the student leaves — their class's room by default; another classroom when confirmed.
        var ownRoom = await db.FloorAreas.AsNoTracking().Where(a => a.SchoolClassId == student.SchoolClassId && a.IsActive)
            .Select(a => new { a.Id, a.FloorId }).FirstOrDefaultAsync(ct);
        var fromAreaId = form.FromAreaId ?? ownRoom?.Id;
        int? fromFloorId = null;
        if (fromAreaId is { } fid)
        {
            var from = await db.FloorAreas.AsNoTracking().Where(a => a.Id == fid && a.IsActive && a.Kind == FloorAreaKind.Classroom)
                .Select(a => new { a.Id, a.FloorId }).FirstOrDefaultAsync(ct);
            if (from is null) return ServiceResult.Fail("FromAreaId", "Choose the classroom the student is leaving.");
            fromFloorId = from.FloorId;
        }
        if (!user.CanRecordMovements(fromFloorId))
            return ServiceResult.Fail("", "You are not allowed to record student movements for this floor. A Floor Management administrator can give you access.");

        // To: a configured area, or a clearly named custom destination.
        string toName;
        int? toAreaId = null, toFloorId = null;
        var custom = TextNormalizer.CleanText(form.CustomDestination);
        if (form.Custom)
        {
            if (custom.Length == 0) return ServiceResult.Fail("CustomDestination", "Enter where the student is going.");
            if (custom.Length > StudentMovement.DestinationMaxLength) return ServiceResult.Fail("CustomDestination", $"Use {StudentMovement.DestinationMaxLength} characters or fewer.");
            toName = custom;
        }
        else
        {
            if (form.ToAreaId is not { } tid) return ServiceResult.Fail("ToAreaId", "Choose where the student is going.");
            var to = await db.FloorAreas.AsNoTracking().Where(a => a.Id == tid && a.IsActive && a.Floor.IsActive)
                .Select(a => new { a.Id, a.FloorId, a.Kind, a.Name, ClassName = a.SchoolClass!.Name }).FirstOrDefaultAsync(ct);
            if (to is null) return ServiceResult.Fail("ToAreaId", "This destination is no longer available. Choose another.");
            if (to.Id == fromAreaId) return ServiceResult.Fail("ToAreaId", "The destination is the classroom the student is leaving.");
            toAreaId = to.Id;
            toFloorId = to.FloorId;
            toName = to.Kind == FloorAreaKind.Classroom ? $"Classroom {to.ClassName}" : to.Name!;
        }

        var nowUtc = clock.UtcNow;
        var exit = ParseTimeToday(form.ExitTime, out var exitError);
        if (exitError is not null) return ServiceResult.Fail("ExitTime", exitError);
        var exitAt = exit ?? nowUtc;
        if (exitAt > nowUtc.AddMinutes(1)) return ServiceResult.Fail("ExitTime", "The exit time cannot be in the future.");

        var notes = TextNormalizer.CleanText(form.Notes);
        if (notes.Length > StudentMovement.NotesMaxLength) return ServiceResult.Fail("Notes", $"Use {StudentMovement.NotesMaxLength} characters or fewer.");

        if (await OpenOfAsync(studentId, ct) is { } existing)
            return ServiceResult.Fail("StudentId", OpenMessage(existing));

        var m = new StudentMovement
        {
            StudentId = studentId, Date = clock.LocalDate(exitAt), FromClassId = student.SchoolClassId, FromAreaId = fromAreaId, FromFloorId = fromFloorId,
            ToAreaId = toAreaId, ToFloorId = toFloorId, ToName = toName, IsCustomDestination = form.Custom, ExitAt = exitAt,
            Notes = notes.Length == 0 ? null : notes, RecordedByStaffId = user.StaffId, RecordedBy = user.Name,
        };
        db.StudentMovements.Add(m);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(m).State = EntityState.Detached;
            return await OpenOfAsync(studentId, ct) is { } race
                ? ServiceResult.Fail("StudentId", OpenMessage(race))
                : ServiceResult.Fail("", "The movement could not be saved. Please try again.");
        }
        FloorAuditLog.Add(db, currentUser, "Movement", m.Id, fromFloorId, "Recorded",
            $"{student.FullName} ({student.ClassName}) left for {toName}{(form.Custom ? " (custom destination)" : "")} at {FloorAuditLabels.Time(exitAt)}.");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(m.Id);
    }

    public Task<MovementRow?> OpenForStudentAsync(int studentId, CancellationToken ct = default) => OpenOfAsync(studentId, ct);

    private async Task<MovementRow?> OpenOfAsync(int studentId, CancellationToken ct) =>
        (await RowsAsync(db.StudentMovements.AsNoTracking().Where(m => m.StudentId == studentId && m.ReturnAt == null && m.VoidedAt == null), ct)).FirstOrDefault();

    private static string OpenMessage(MovementRow r) =>
        $"{r.StudentName} is already outside: left for {r.ToName} at {FloorAuditLabels.Time(r.ExitAt)}" +
        $"{(r.Date != SchoolTime.Current.Today ? $" on {r.Date:d MMM}" : "")} (recorded by {r.RecordedBy ?? "—"}). Mark them returned first" +
        " — or, if that movement was recorded by mistake, a Floor Management administrator can void it.";

    public async Task<ServiceResult> ReturnAsync(int id, string? time, CancellationToken ct = default)
    {
        var user = await access.CurrentAsync(ct);
        if (!user.HasIdentity) return ServiceResult.Fail("", "Choose who you are under “Working as” (the panel at the top of the Floor Management pages) to mark students returned.");
        var m = await db.StudentMovements.Include(x => x.Student).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return ServiceResult.Fail("", "This movement no longer exists.");
        if (!user.CanRecordMovements(m.FromFloorId)) return ServiceResult.Fail("", "You are not allowed to mark students returned for this floor.");
        if (m.VoidedAt is not null) return ServiceResult.Fail("", "This movement was voided.");
        if (m.ReturnAt is not null) return ServiceResult.Fail("", $"{m.Student.FullName} was already marked returned at {FloorAuditLabels.Time(m.ReturnAt.Value)}.");
        var nowUtc = clock.UtcNow;
        var at = ParseTimeToday(time, out var error);
        if (error is not null) return ServiceResult.Fail("time", error);
        var returnAt = at ?? nowUtc;
        if (returnAt > nowUtc.AddMinutes(1)) return ServiceResult.Fail("time", "The return time cannot be in the future.");
        if (returnAt <= m.ExitAt) return ServiceResult.Fail("time", $"The return time must be after the exit time ({FloorAuditLabels.Time(m.ExitAt)}).");
        m.ReturnAt = returnAt;
        m.ReturnedByStaffId = user.StaffId;
        m.ReturnedBy = user.Name;
        FloorAuditLog.Add(db, currentUser, "Movement", m.Id, m.FromFloorId, "Returned",
            $"{m.Student.FullName} returned from {m.ToName} at {FloorAuditLabels.Time(returnAt)} ({FloorAuditLabels.Duration(returnAt - m.ExitAt)} outside).");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(m.Id);
    }

    public async Task<ServiceResult> VoidAsync(int id, string? reason, CancellationToken ct = default)
    {
        var user = await access.CurrentAsync(ct);
        if (!user.CanAdminister) return ServiceResult.Fail("", "Only a Floor Management administrator can void a movement.");
        var m = await db.StudentMovements.Include(x => x.Student).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return ServiceResult.Fail("", "This movement no longer exists.");
        if (m.VoidedAt is not null) return ServiceResult.Fail("", "This movement is already voided.");
        var why = TextNormalizer.CleanText(reason);
        if (why.Length == 0) return ServiceResult.Fail("reason", "Enter why this movement is voided.");
        if (why.Length > StudentMovement.NotesMaxLength) return ServiceResult.Fail("reason", $"Use {StudentMovement.NotesMaxLength} characters or fewer.");
        m.VoidedAt = clock.UtcNow;
        m.VoidedBy = user.Name;
        m.VoidReason = why;
        FloorAuditLog.Add(db, currentUser, "Movement", m.Id, m.FromFloorId, "Voided", $"{m.Student.FullName} → {m.ToName} ({FloorAuditLabels.Time(m.ExitAt)}) voided: {why}");
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(m.Id);
    }
}
