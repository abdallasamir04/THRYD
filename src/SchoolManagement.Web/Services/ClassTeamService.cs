using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>An academic year from the existing AcademicYears table, with its span (first quarter start → last quarter end).</summary>
public sealed record AcademicYearOption(int Id, string Name, DateOnly? Start, DateOnly? End, bool IsCurrent);

public sealed record TeamMemberView(int AssignmentId, int StaffId, string Name, string? JobTitle, ClassTeachingRole Role, DateTime AssignedAt, string? AssignedBy);

public sealed record EndedAssignmentView(string Name, ClassTeachingRole Role, DateTime AssignedAt, DateTime? EndedAt, string? EndReason, string? EndedBy);

public sealed record TeamHistoryView(DateTime At, string Year, string StaffName, ClassTeachingOperation Operation, ClassTeachingRole? PreviousRole, ClassTeachingRole? NewRole, string? Details, string? By);

/// <summary>A KG class's teaching team for one academic year, plus its ended assignments and change history.</summary>
public sealed class ClassTeamView
{
    public required int ClassId { get; init; }
    public required string ClassName { get; init; }
    public required AcademicYearOption Year { get; init; }
    public TeamMemberView? Homeroom { get; init; }

    /// <summary>The class's Co-Teacher (one per class and year).</summary>
    public TeamMemberView? CoTeacher => CoTeachers.FirstOrDefault();

    /// <summary>
    /// Every active Co-Teacher, earliest first. More than one is only possible for data saved before the one-Co-Teacher
    /// rule (<see cref="HasExtraCoTeachers"/>): those are shown and kept until someone chooses which one stays.
    /// </summary>
    public IReadOnlyList<TeamMemberView> CoTeachers { get; init; } = [];
    public bool HasExtraCoTeachers => CoTeachers.Count > 1;
    public IReadOnlyList<EndedAssignmentView> Ended { get; init; } = [];
    public IReadOnlyList<TeamHistoryView> History { get; init; } = [];
}

/// <summary>One row of a staff member's KG Class Assignments (Staff Profile).</summary>
public sealed record StaffClassAssignmentView(
    int ClassId, string ClassName, int YearId, string Year, ClassTeachingRole Role, bool IsActive, DateTime AssignedAt, DateTime? EndedAt);

public static class ClassTeachingRoles
{
    public static string Label(ClassTeachingRole r) => r == ClassTeachingRole.HomeroomTeacher ? "Homeroom Teacher" : "Co-Teacher";

    public static string Label(ClassTeachingOperation o) => o switch
    {
        ClassTeachingOperation.Assigned => "Assigned",
        ClassTeachingOperation.Removed => "Removed",
        ClassTeachingOperation.Replaced => "Replaced",
        _ => "Role changed",
    };
}

public interface IClassTeamService
{
    /// <summary>Every academic year (newest first) with the current one flagged.</summary>
    Task<IReadOnlyList<AcademicYearOption>> YearsAsync(CancellationToken ct = default);

    /// <summary>
    /// The current academic year: the one whose quarters span today (school timezone); otherwise the latest year that has
    /// started; otherwise the newest year. Null only when no academic year exists.
    /// </summary>
    Task<AcademicYearOption?> CurrentYearAsync(CancellationToken ct = default);

    /// <summary>The class's team for the year; null when the class or year does not exist or the class is not in the KG Stage.</summary>
    Task<ClassTeamView?> TeamAsync(int classId, int yearId, CancellationToken ct = default);

    /// <summary>Staff who can be assigned (existing staff records), by name.</summary>
    Task<IReadOnlyList<StaffOption>> StaffOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Save Changes: makes the active team for the class and year exactly one Homeroom Teacher (<paramref name="homeroomId"/>)
    /// and one Co-Teacher (<paramref name="coTeacherId"/>); either may be empty. Assignments that stay are untouched; a replaced
    /// or removed one is ended (kept in the history), never deleted.
    /// </summary>
    Task<ServiceResult> SaveAsync(int classId, int yearId, int? homeroomId, int? coTeacherId, CancellationToken ct = default);

    /// <summary>Remove Assignment: ends this one active assignment only.</summary>
    Task<ServiceResult> RemoveAsync(int classId, int assignmentId, CancellationToken ct = default);

    /// <summary>The staff member's KG class assignments (active and ended), newest academic year first.</summary>
    Task<IReadOnlyList<StaffClassAssignmentView>> ForStaffAsync(int staffId, CancellationToken ct = default);
}

/// <summary>
/// KG class teaching teams (Pre-K, KG1, KG2 only): one Homeroom Teacher and one Co-Teacher per class and academic year.
/// Every rule is checked here; the database indexes back up "one Homeroom Teacher", "one Co-Teacher" and "one role per
/// person". Nothing here changes timetable, P.E. or workload data.
/// </summary>
public sealed class ClassTeamService(SchoolDbContext db, ICurrentUser currentUser, ISchoolTimeService? schoolTime = null) : IClassTeamService
{
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<AcademicYearOption>> YearsAsync(CancellationToken ct = default)
    {
        var years = await db.AcademicYears.AsNoTracking()
            .Select(y => new { y.Id, y.Name, Start = y.Quarters.Min(q => q.StartDate), End = y.Quarters.Max(q => q.EndDate) })
            .ToListAsync(ct);
        var current = Current(years.Select(y => (y.Id, y.Start, y.End)).ToList());
        return years.OrderByDescending(y => y.Start ?? DateOnly.MinValue).ThenByDescending(y => y.Name, StringComparer.OrdinalIgnoreCase)
            .Select(y => new AcademicYearOption(y.Id, y.Name, y.Start, y.End, y.Id == current)).ToList();
    }

    public async Task<AcademicYearOption?> CurrentYearAsync(CancellationToken ct = default) =>
        (await YearsAsync(ct)).FirstOrDefault(y => y.IsCurrent);

    private int? Current(IReadOnlyList<(int Id, DateOnly? Start, DateOnly? End)> years)
    {
        var today = _time.Today;
        return years.Where(y => y.Start <= today && today <= y.End).Select(y => (int?)y.Id).FirstOrDefault()
            ?? years.Where(y => y.Start <= today).OrderByDescending(y => y.Start).Select(y => (int?)y.Id).FirstOrDefault()
            ?? years.OrderByDescending(y => y.Id).Select(y => (int?)y.Id).FirstOrDefault();
    }

    public async Task<ClassTeamView?> TeamAsync(int classId, int yearId, CancellationToken ct = default)
    {
        var cls = await db.Classes.AsNoTracking().Where(c => c.Id == classId).Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct);
        if (cls is null || !ClassStages.IsKgStage(cls.Name)) return null;
        var year = (await YearsAsync(ct)).FirstOrDefault(y => y.Id == yearId);
        if (year is null) return null;

        var rows = await db.ClassTeachingAssignments.AsNoTracking().Where(a => a.SchoolClassId == classId && a.AcademicYearId == yearId)
            .Select(a => new
            {
                a.Id, a.StaffMemberId, a.StaffMember.FullName, a.StaffMember.JobTitle, a.Role, a.IsActive,
                a.AssignedAt, a.AssignedBy, a.EndedAt, a.EndReason, a.EndedBy,
            })
            .ToListAsync(ct);
        var active = rows.Where(r => r.IsActive)
            .Select(r => new TeamMemberView(r.Id, r.StaffMemberId, r.FullName, r.JobTitle, r.Role, r.AssignedAt, r.AssignedBy)).ToList();
        var history = await db.ClassTeachingHistory.AsNoTracking().Where(h => h.SchoolClassId == classId)
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Id).Take(100)
            .Select(h => new TeamHistoryView(h.At, h.AcademicYear.Name, h.StaffMember.FullName, h.Operation, h.PreviousRole, h.NewRole, h.Details, h.By))
            .ToListAsync(ct);
        return new ClassTeamView
        {
            ClassId = cls.Id,
            ClassName = cls.Name,
            Year = year,
            Homeroom = active.FirstOrDefault(m => m.Role == ClassTeachingRole.HomeroomTeacher),
            CoTeachers = active.Where(m => m.Role == ClassTeachingRole.CoTeacher).OrderBy(m => m.AssignedAt).ThenBy(m => m.AssignmentId).ToList(),
            Ended = rows.Where(r => !r.IsActive).OrderByDescending(r => r.EndedAt)
                .Select(r => new EndedAssignmentView(r.FullName, r.Role, r.AssignedAt, r.EndedAt, r.EndReason, r.EndedBy)).ToList(),
            History = history,
        };
    }

    public async Task<IReadOnlyList<StaffOption>> StaffOptionsAsync(CancellationToken ct = default) =>
        await db.StaffMembers.AsNoTracking().OrderBy(s => s.FullName)
            .Select(s => new StaffOption(s.Id, s.FullName, s.JobTitle, EF.Property<string>(s, "StaffType")))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<StaffClassAssignmentView>> ForStaffAsync(int staffId, CancellationToken ct = default)
    {
        var rows = await db.ClassTeachingAssignments.AsNoTracking().Where(a => a.StaffMemberId == staffId)
            .Select(a => new
            {
                a.SchoolClassId, ClassName = a.SchoolClass.Name, ClassOrder = a.SchoolClass.SortOrder, a.AcademicYearId, Year = a.AcademicYear.Name,
                YearStart = a.AcademicYear.Quarters.Min(q => q.StartDate), a.Role, a.IsActive, a.AssignedAt, a.EndedAt,
            })
            .ToListAsync(ct);
        return rows.OrderByDescending(r => r.YearStart ?? DateOnly.MinValue).ThenByDescending(r => r.Year, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ClassOrder).ThenBy(r => r.ClassName).ThenByDescending(r => r.IsActive).ThenByDescending(r => r.AssignedAt)
            .Select(r => new StaffClassAssignmentView(r.SchoolClassId, r.ClassName, r.AcademicYearId, r.Year, r.Role, r.IsActive, r.AssignedAt, r.EndedAt))
            .ToList();
    }

    // ------------------------------------------------------------------ writes

    public async Task<ServiceResult> RemoveAsync(int classId, int assignmentId, CancellationToken ct = default)
    {
        var row = await db.ClassTeachingAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId && a.SchoolClassId == classId, ct);
        if (row is null || !row.IsActive) return ServiceResult.Fail("", "This assignment has already been removed.");
        var className = await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).FirstAsync(ct);
        if (!ClassStages.IsKgStage(className)) return NotKg(className);

        var now = _time.UtcNow;
        End(row, now, "Removed");
        Log(row.SchoolClassId, row.AcademicYearId, row.StaffMemberId, ClassTeachingOperation.Removed, row.Role, null, null, now);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(assignmentId);
    }

    public async Task<ServiceResult> SaveAsync(int classId, int yearId, int? homeroomId, int? coTeacherId, CancellationToken ct = default)
    {
        var (problem, team) = await LoadAsync(classId, yearId, ct);
        if (problem is not null) return problem;

        // ---- validate the requested team: one Homeroom Teacher and one Co-Teacher, never the same person
        if (homeroomId is { } h && coTeacherId == h)
            return ServiceResult.Fail("CoTeacherId", $"{await NameAsync(h, ct)} is chosen as Homeroom Teacher and as Co-Teacher. One staff member cannot hold both roles in the same class and academic year.");
        var wanted = new List<(int Staff, ClassTeachingRole Role)>();
        if (homeroomId is { } hr) wanted.Add((hr, ClassTeachingRole.HomeroomTeacher));
        if (coTeacherId is { } co) wanted.Add((co, ClassTeachingRole.CoTeacher));
        var ids = wanted.Select(w => w.Staff).ToList();
        if (ids.Count > 0 && await db.StaffMembers.CountAsync(s => ids.Contains(s.Id), ct) != ids.Count)
            return ServiceResult.Fail("", "Choose staff members from the list.");

        // ---- diff against the active team: unchanged rows stay as they are
        var keep = team.Where(a => wanted.Contains((a.StaffMemberId, a.Role))).ToList();
        var ending = team.Except(keep).ToList();
        var adding = wanted.Where(w => !team.Any(a => a.StaffMemberId == w.Staff && a.Role == w.Role)).ToList();
        if (ending.Count == 0 && adding.Count == 0) return ServiceResult.Fail("", "Nothing was changed.");

        var now = _time.UtcNow;
        var incoming = adding.ToDictionary(a => a.Role, a => a.Staff);
        // The Co-Teacher who stays when a class still has several from before the one-Co-Teacher rule.
        var keptCo = keep.Where(k => k.Role == ClassTeachingRole.CoTeacher).Select(k => (int?)k.StaffMemberId).FirstOrDefault();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var old in ending)
        {
            var next = wanted.Where(w => w.Staff == old.StaffMemberId).Select(w => (ClassTeachingRole?)w.Role).FirstOrDefault();
            if (next is { } role)
            {
                End(old, now, $"Role changed to {ClassTeachingRoles.Label(role)}");
                Log(classId, yearId, old.StaffMemberId, ClassTeachingOperation.RoleChanged, old.Role, role, null, now);
            }
            else if (incoming.TryGetValue(old.Role, out var by))
            {
                // The same for both roles: the outgoing teacher's assignment is ended and kept, the replacement logged.
                var name = await NameAsync(by, ct);
                End(old, now, $"Replaced by {name}");
                Log(classId, yearId, old.StaffMemberId, ClassTeachingOperation.Replaced, old.Role, null, $"Replaced as {ClassTeachingRoles.Label(old.Role)} by {name}", now);
            }
            else if (old.Role == ClassTeachingRole.CoTeacher && keptCo is { } kept)
            {
                var name = await NameAsync(kept, ct);
                End(old, now, $"One Co-Teacher per class: {name} kept");
                Log(classId, yearId, old.StaffMemberId, ClassTeachingOperation.Removed, old.Role, null, $"A class has one Co-Teacher; {name} was kept", now);
            }
            else
            {
                End(old, now, "Removed");
                Log(classId, yearId, old.StaffMemberId, ClassTeachingOperation.Removed, old.Role, null, null, now);
            }
        }
        // Ended rows are saved before new ones are added (the filtered unique indexes cover active rows only).
        await db.SaveChangesAsync(ct);

        foreach (var (staff, role) in adding)
        {
            db.ClassTeachingAssignments.Add(new ClassTeachingAssignment
            {
                SchoolClassId = classId, AcademicYearId = yearId, StaffMemberId = staff, Role = role,
                IsActive = true, AssignedAt = now, AssignedBy = currentUser.Name,
            });
            if (!ending.Any(e => e.StaffMemberId == staff)) // a role change is already logged above
                Log(classId, yearId, staff, ClassTeachingOperation.Assigned, null, role, null, now);
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "The teaching team was changed by someone else at the same time. Reload the page and try again.");
        }
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(classId);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Checks the class (exists, KG Stage) and year, and loads the tracked active assignments.</summary>
    private async Task<(ServiceResult? Problem, List<ClassTeachingAssignment> Team)> LoadAsync(int classId, int yearId, CancellationToken ct)
    {
        var className = await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        if (className is null) return (ServiceResult.Fail("", "This class no longer exists."), []);
        if (!ClassStages.IsKgStage(className)) return (NotKg(className), []);
        if (!await db.AcademicYears.AnyAsync(y => y.Id == yearId, ct)) return (ServiceResult.Fail("", "Choose an existing academic year."), []);
        var team = await db.ClassTeachingAssignments.Where(a => a.SchoolClassId == classId && a.AcademicYearId == yearId && a.IsActive).ToListAsync(ct);
        return (null, team);
    }

    private static ServiceResult NotKg(string className) =>
        ServiceResult.Fail("", $"{className} is not a KG Stage class. Teaching teams are managed for Pre-K, KG1 and KG2 classes only.");

    private void End(ClassTeachingAssignment row, DateTime now, string reason)
    {
        row.IsActive = false;
        row.EndedAt = now;
        row.EndedBy = currentUser.Name;
        row.EndReason = reason.Length > ClassTeachingAssignment.EndReasonMaxLength ? reason[..ClassTeachingAssignment.EndReasonMaxLength] : reason;
    }

    private void Log(int classId, int yearId, int staffId, ClassTeachingOperation op, ClassTeachingRole? before, ClassTeachingRole? after, string? details, DateTime now) =>
        db.ClassTeachingHistory.Add(new ClassTeachingHistory
        {
            SchoolClassId = classId, AcademicYearId = yearId, StaffMemberId = staffId, Operation = op,
            PreviousRole = before, NewRole = after, At = now, By = currentUser.Name,
            Details = details is null ? null : details.Length > ClassTeachingHistory.DetailsMaxLength ? details[..ClassTeachingHistory.DetailsMaxLength] : details,
        });

    private async Task<string> NameAsync(int staffId, CancellationToken ct) =>
        await db.StaffMembers.Where(s => s.Id == staffId).Select(s => s.FullName).FirstOrDefaultAsync(ct) ?? $"Staff #{staffId}";
}
