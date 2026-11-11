using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record PeChip(int SessionId, DayOfWeek Day, int SlotId, string ClassName, string TeacherName, PeFacility? Facility, string? CoTeacherName);

public sealed record PeEquipmentRow(int Id, string Name, bool IsChecked);

public sealed class PeSessionView
{
    public required SessionView Session { get; init; }
    public required SlotView Slot { get; init; }
    public string Grade { get; init; } = "";
    public int? CoTeacherId { get; init; }
    public string? CoTeacherName { get; init; }
    public PeFacility? Facility { get; init; }
    public string? Activity { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<PeEquipmentRow> Equipment { get; init; } = [];
    public DateTime? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record PeEquipmentInput(string? Name, bool Checked);

public sealed record PeDetailsInput(int? CoTeacherId, PeFacility? Facility, string? Activity, string? Notes, IReadOnlyList<PeEquipmentInput> Equipment);

public interface IPeService
{
    Task<int?> DepartmentIdAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PeChip>> WeekAsync(DateOnly week, CancellationToken ct = default);
    Task<PeSessionView?> GetAsync(int sessionId, CancellationToken ct = default);
    Task<ServiceResult> SaveDetailsAsync(int sessionId, PeDetailsInput input, CancellationToken ct = default);
    Task<HashSet<int>> BusyClassIdsAsync(DayOfWeek day, int slotId, CancellationToken ct = default);
}

/// <summary>
/// P.E. timetable: the P.E. department's timetable sessions (created, changed and removed through
/// ITimetableService, so conflicts and workload are the same everywhere) plus P.E. details —
/// co-teacher, facility, activity, equipment checklist and notes.
/// </summary>
public sealed class PeService(SchoolDbContext db, ICurrentUser currentUser) : IPeService
{
    public const int MaxEquipment = 40;

    /// <summary>The existing P.E. department (never created here).</summary>
    public async Task<int?> DepartmentIdAsync(CancellationToken ct = default) =>
        await db.Departments.AsNoTracking().Where(d => d.NormalizedName == "PE").Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PeChip>> WeekAsync(DateOnly week, CancellationToken ct = default)
    {
        var pe = await DepartmentIdAsync(ct);
        if (pe is null) return [];
        var rows = await db.TimetableAssignments.AsNoTracking().Where(t => t.DepartmentId == pe).ActiveIn(SchoolWeek.StartOf(week))
            .Select(t => new
            {
                t.Id, t.Day, t.TimeSlotId, ClassName = t.SchoolClass.Name, TeacherName = t.Teacher.FullName,
                Detail = db.PeSessionDetails.Where(d => d.TimetableAssignmentId == t.Id)
                    .Select(d => new { d.Facility, CoTeacher = d.CoTeacher != null ? d.CoTeacher.FullName : null }).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.OrderBy(r => r.ClassName)
            .Select(r => new PeChip(r.Id, r.Day, r.TimeSlotId, r.ClassName, r.TeacherName, r.Detail?.Facility, r.Detail?.CoTeacher)).ToList();
    }

    public async Task<PeSessionView?> GetAsync(int sessionId, CancellationToken ct = default)
    {
        var pe = await DepartmentIdAsync(ct);
        var s = await db.TimetableAssignments.AsNoTracking().Where(t => t.Id == sessionId && t.DepartmentId == pe)
            .Select(t => new SessionView(t.Id, t.SchoolClassId, t.SchoolClass.Name, t.Day, t.TimeSlotId, t.DepartmentId, t.Department.Name,
                t.Subject, t.TeacherId, t.Teacher.FullName, t.Notes, t.StartsOn, t.EndedOn))
            .FirstOrDefaultAsync(ct);
        if (s is null) return null;
        var slot = await db.TimeSlots.AsNoTracking().Where(x => x.Id == s.SlotId)
            .Select(x => new SlotView(x.Id, x.Number, x.Name, x.StartTime, x.EndTime, x.IsActive)).FirstAsync(ct);
        var d = await db.PeSessionDetails.AsNoTracking().Where(x => x.TimetableAssignmentId == sessionId)
            .Select(x => new
            {
                x.CoTeacherId, CoTeacher = x.CoTeacher != null ? x.CoTeacher.FullName : null, x.Facility, x.Activity, x.Notes, x.UpdatedAt, x.UpdatedBy,
                Equipment = x.Equipment.OrderBy(e => e.SortOrder).Select(e => new PeEquipmentRow(e.Id, e.Name, e.IsChecked)).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        return new PeSessionView
        {
            Session = s, Slot = slot, Grade = GradeOf(s.ClassName),
            CoTeacherId = d?.CoTeacherId, CoTeacherName = d?.CoTeacher, Facility = d?.Facility, Activity = d?.Activity, Notes = d?.Notes,
            Equipment = d?.Equipment ?? [], UpdatedAt = d?.UpdatedAt, UpdatedBy = d?.UpdatedBy,
        };
    }

    public async Task<ServiceResult> SaveDetailsAsync(int sessionId, PeDetailsInput input, CancellationToken ct = default)
    {
        var view = await GetAsync(sessionId, ct);
        if (view is null || view.Session.EndedOn is not null) return ServiceResult.Fail("", "This P.E. session is no longer on the timetable.");
        var s = view.Session;
        var result = ServiceResult.Ok(sessionId);

        if (input.Facility is { } f && !Enum.IsDefined(f)) result.AddError("Facility", "Choose KG Garden, Swimming Pool or Main Garden.");
        var activity = TextNormalizer.CleanText(input.Activity);
        if (activity.Length > PeSessionDetail.ActivityMaxLength) result.AddError("Activity", $"Use {PeSessionDetail.ActivityMaxLength} characters or fewer.");
        var notes = input.Notes?.Trim() ?? "";
        if (notes.Length > PeSessionDetail.NotesMaxLength) result.AddError("Notes", $"Use {PeSessionDetail.NotesMaxLength} characters or fewer.");
        var items = input.Equipment.Select(e => (Name: TextNormalizer.CleanText(e.Name), e.Checked)).Where(e => e.Name.Length > 0).ToList();
        if (items.Count > MaxEquipment) result.AddError("Equipment", $"Use at most {MaxEquipment} equipment items.");
        if (items.Any(e => e.Name.Length > PeEquipmentItem.NameMaxLength)) result.AddError("Equipment", $"Keep each equipment item to {PeEquipmentItem.NameMaxLength} characters.");

        if (input.CoTeacherId is { } co)
        {
            var coName = await db.Teachers.AsNoTracking().Where(t => t.Id == co).Select(t => t.FullName).FirstOrDefaultAsync(ct);
            if (coName is null) result.AddError("CoTeacherId", "Choose an academic staff member as co-teacher.");
            else if (co == s.TeacherId) result.AddError("CoTeacherId", "The co-teacher must be someone other than the assigned teacher.");
            else if (co != view.CoTeacherId)
            {
                // Busy at this time: their own sessions / periods / duties, or co-teaching another session then.
                var conflict = await ScheduleRules.TeacherConflictAsync(db, co, coName, s.Day, (await db.TimeSlots.FirstAsync(x => x.Id == s.SlotId, ct)), null, null, null, ct);
                var coTeaching = await db.PeSessionDetails.AsNoTracking()
                    .Where(d => d.CoTeacherId == co && d.TimetableAssignmentId != sessionId && d.Session.EndedOn == null && d.Session.Day == s.Day && d.Session.TimeSlotId == s.SlotId)
                    .Select(d => d.Session.SchoolClass.Name).FirstOrDefaultAsync(ct);
                if (conflict is not null) result.AddError("CoTeacherId", conflict);
                else if (coTeaching is not null) result.AddError("CoTeacherId", $"{coName} already co-teaches {coTeaching} at this time.");
            }
        }
        if (!result.Succeeded) return result;

        var detail = await db.PeSessionDetails.Include(d => d.Equipment).FirstOrDefaultAsync(d => d.TimetableAssignmentId == sessionId, ct);
        if (detail is null)
        {
            detail = new PeSessionDetail { TimetableAssignmentId = sessionId };
            db.PeSessionDetails.Add(detail);
        }
        detail.CoTeacherId = input.CoTeacherId;
        detail.Facility = input.Facility;
        detail.Activity = activity.Length == 0 ? null : activity;
        detail.Notes = notes.Length == 0 ? null : notes;
        detail.UpdatedBy = currentUser.Name;
        // The checklist is saved as submitted (order, names, ticks).
        db.PeEquipmentItems.RemoveRange(detail.Equipment);
        detail.Equipment.Clear();
        var order = 0;
        foreach (var (name, check) in items)
            detail.Equipment.Add(new PeEquipmentItem { Name = name, IsChecked = check, SortOrder = ++order });
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Classes that already have a current session (any subject) in this day + period.</summary>
    public async Task<HashSet<int>> BusyClassIdsAsync(DayOfWeek day, int slotId, CancellationToken ct = default) =>
        (await db.TimetableAssignments.AsNoTracking().Where(t => t.Day == day && t.TimeSlotId == slotId && t.EndedOn == null)
            .Select(t => t.SchoolClassId).ToListAsync(ct)).ToHashSet();

    /// <summary>"KG2A" → "KG 2", "G7B" → "Grade 7"; other names (e.g. PreK) are shown as they are.</summary>
    public static string GradeOf(string className) =>
        ClassNumbering.Parse(className) is { } p ? (p.Stage == "KG" ? $"KG {p.Number}" : $"Grade {p.Number}") : className;

    public static string FacilityName(PeFacility f) => f switch
    {
        PeFacility.KgGarden => "KG Garden",
        PeFacility.SwimmingPool => "Swimming Pool",
        _ => "Main Garden",
    };
}
