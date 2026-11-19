using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>A Morning Revision record as shown in lists: the session it belongs to, the class, the teacher, the lesson and the times.</summary>
public sealed record RevisionRow(int Id, int DutyAssignmentId, string Session, DateOnly Date, int ClassId, string ClassName, int TeacherId, string TeacherName,
    string Lesson, TimeOnly? EntryTime, TimeOnly? ExitTime, DateTime UpdatedAt, string? UpdatedBy);

/// <summary>A Morning Revision duty session on a date (a coverage point of the Sub-Duty) that records can belong to.</summary>
public sealed record RevisionSessionOption(int Id, string Label);

/// <summary>An eligible teacher (academic staff whose contract has not ended by the session's date).</summary>
public sealed record RevisionTeacherOption(int Id, string FullName, string? Note);

public sealed record RevisionInput(int? DutyAssignmentId, int? SchoolClassId, int? TeacherId, string? Lesson, TimeOnly? EntryTime, TimeOnly? ExitTime);

/// <summary>The choices of the Morning Revision record form for a date.</summary>
public sealed record RevisionFormOptions(IReadOnlyList<RevisionSessionOption> Sessions, IReadOnlyList<ClassOption> Classes, IReadOnlyList<RevisionTeacherOption> Teachers);

/// <summary>
/// Morning Revision records (<see cref="MorningRevisionRecord"/>): which teacher revised which lesson with which class, and
/// when they entered and left. Each record belongs to a Morning Revision duty session on a date; the class and the teacher
/// are existing records. Every change is written to the session's audit trail; a record never changes the session's
/// coverage (that still comes from the staff attendance at its coverage points).
/// </summary>
public sealed partial class DutyService
{
    public const DutyComponent RevisionComponent = DutyComponent.InClassMorning;

    public async Task<IReadOnlyList<RevisionRow>> RevisionsAsync(DateOnly date, CancellationToken ct = default) =>
        await Revisions(db.MorningRevisions.Where(r => r.DutyAssignment.DutyDate == date)).ToListAsync(ct);

    public async Task<IReadOnlyList<RevisionRow>> RevisionsForDutyAsync(int dutyAssignmentId, CancellationToken ct = default) =>
        await Revisions(db.MorningRevisions.Where(r => r.DutyAssignmentId == dutyAssignmentId)).ToListAsync(ct);

    public Task<RevisionRow?> GetRevisionAsync(int id, CancellationToken ct = default) =>
        Revisions(db.MorningRevisions.Where(r => r.Id == id)).FirstOrDefaultAsync(ct);

    public Task<int> RevisionCountAsync(DateOnly date, CancellationToken ct = default) =>
        db.MorningRevisions.CountAsync(r => r.DutyAssignment.DutyDate == date, ct);

    public async Task<RevisionFormOptions> RevisionOptionsAsync(DateOnly date, CancellationToken ct = default)
    {
        var sessions = (await db.DutyAssignments.AsNoTracking()
                .Where(d => d.Component == RevisionComponent && d.DutyDate == date)
                .OrderBy(d => d.StartTime).ThenBy(d => d.Id)
                .Select(d => new { d.Id, d.Description, d.Location, d.StartTime, d.EndTime, Staff = d.PrimaryStaff.FullName })
                .ToListAsync(ct))
            .Select(d => new RevisionSessionOption(d.Id, $"{d.Description}{(d.Location is null ? "" : " · " + d.Location)} · {ScheduleRules.Times(d.StartTime, d.EndTime)} · {d.Staff}"))
            .ToList();
        // Every existing class (inactive ones stay listed: a past session may have been in one).
        var classes = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new ClassOption(c.Id, c.Name, c.IsActive, c.Students.Count, c.Capacity)).ToListAsync(ct);
        return new RevisionFormOptions(sessions, classes, await EligibleTeachersAsync(date, ct));
    }

    public async Task<ServiceResult> SaveRevisionAsync(int? id, RevisionInput input, CancellationToken ct = default)
    {
        var result = ServiceResult.Ok();
        var lesson = TextNormalizer.CleanText(input.Lesson);
        if (input.DutyAssignmentId is null) result.AddError("DutyAssignmentId", "Choose the Morning Revision session.");
        if (input.SchoolClassId is null) result.AddError("SchoolClassId", "Choose the class.");
        if (input.TeacherId is null) result.AddError("TeacherId", "Choose the teacher.");
        if (lesson.Length == 0) result.AddError("Lesson", "Enter the lesson reviewed, e.g. Mathematics – Fractions.");
        else if (lesson.Length > MorningRevisionRecord.LessonMaxLength) result.AddError("Lesson", $"Use {MorningRevisionRecord.LessonMaxLength} characters or fewer.");
        if (input.EntryTime is { } inT && input.ExitTime is { } outT && outT <= inT) result.AddError("ExitTime", "The exit time must be later than the entry time.");
        if (!result.Succeeded) return result;

        var session = await db.DutyAssignments.FirstOrDefaultAsync(d => d.Id == input.DutyAssignmentId, ct);
        if (session is null || session.Component != RevisionComponent)
            return ServiceResult.Fail("DutyAssignmentId", $"Choose a {DutyComponents.Name(RevisionComponent)} session of this day.");
        var cls = await db.Classes.AsNoTracking().Where(c => c.Id == input.SchoolClassId).Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct);
        if (cls is null) return ServiceResult.Fail("SchoolClassId", "This class no longer exists. Choose an existing class.");
        var teacher = (await EligibleTeachersAsync(session.DutyDate, ct)).FirstOrDefault(t => t.Id == input.TeacherId);
        if (teacher is null) return ServiceResult.Fail("TeacherId", "Choose a teacher from the academic staff (whose contract has not ended).");

        MorningRevisionRecord? record = null;
        if (id is not null)
        {
            record = await db.MorningRevisions.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (record is null) return ServiceResult.Fail("", "This revision record no longer exists.");
        }

        // The same teacher cannot be recorded twice for overlapping times on the same day (one class at a time), and the
        // same teacher, class and lesson are recorded once per session.
        var sameDay = await db.MorningRevisions.AsNoTracking()
            .Where(r => r.Id != (id ?? 0) && r.TeacherId == teacher.Id && r.DutyAssignment.DutyDate == session.DutyDate)
            .Select(r => new { r.DutyAssignmentId, r.SchoolClassId, ClassName = r.SchoolClass.Name, r.Lesson, r.EntryTime, r.ExitTime }).ToListAsync(ct);
        if (sameDay.FirstOrDefault(r => r.DutyAssignmentId == session.Id && r.SchoolClassId == cls.Id && string.Equals(r.Lesson, lesson, StringComparison.OrdinalIgnoreCase)) is { } twin)
            return ServiceResult.Fail("", $"{teacher.FullName}'s revision of “{twin.Lesson}” in {cls.Name} is already recorded for this session.");
        if (input.EntryTime is { } s && input.ExitTime is { } e
            && sameDay.FirstOrDefault(r => r.EntryTime is { } rs && r.ExitTime is { } re && rs < e && s < re) is { } clash)
            return ServiceResult.Fail("", $"{teacher.FullName} is already recorded in {clash.ClassName} from {Hm(clash.EntryTime)} to {Hm(clash.ExitTime)}, which overlaps these times.");

        var what = $"{cls.Name} · {teacher.FullName} · {lesson} · {Hm(input.EntryTime)}–{Hm(input.ExitTime)}";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (record is null)
        {
            record = new MorningRevisionRecord
            {
                DutyAssignmentId = session.Id, SchoolClassId = cls.Id, TeacherId = teacher.Id, Lesson = lesson,
                EntryTime = input.EntryTime, ExitTime = input.ExitTime, CreatedBy = currentUser.Name, UpdatedBy = currentUser.Name,
            };
            db.MorningRevisions.Add(record);
            Audit(session, DutyAuditAction.RevisionAdded, $"Revision record added: {what}.");
        }
        else
        {
            var before = $"{await ClassNameAsync(record.SchoolClassId, ct)} · {await StaffNameAsync(record.TeacherId, ct)} · {record.Lesson} · {Hm(record.EntryTime)}–{Hm(record.ExitTime)}";
            var moved = record.DutyAssignmentId != session.Id;
            (record.DutyAssignmentId, record.SchoolClassId, record.TeacherId, record.Lesson, record.EntryTime, record.ExitTime, record.UpdatedBy) =
                (session.Id, cls.Id, teacher.Id, lesson, input.EntryTime, input.ExitTime, currentUser.Name);
            if (before == what && !moved) return ServiceResult.Ok(record.Id);
            Audit(session, DutyAuditAction.RevisionUpdated, $"Revision record changed: {before} → {what}.");
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(record.Id);
    }

    public async Task<ServiceResult> DeleteRevisionAsync(int id, CancellationToken ct = default)
    {
        var record = await db.MorningRevisions.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null) return ServiceResult.Fail("", "This revision record no longer exists.");
        var session = await db.DutyAssignments.FirstAsync(d => d.Id == record.DutyAssignmentId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Audit(session, DutyAuditAction.RevisionRemoved,
            $"Revision record removed: {await ClassNameAsync(record.SchoolClassId, ct)} · {await StaffNameAsync(record.TeacherId, ct)} · {record.Lesson} · {Hm(record.EntryTime)}–{Hm(record.ExitTime)}.");
        db.MorningRevisions.Remove(record);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    /// <summary>Academic staff (not the Management Team; a teaching job title or none, as on the staff lists) whose contract has not ended by the date.</summary>
    private async Task<IReadOnlyList<RevisionTeacherOption>> EligibleTeachersAsync(DateOnly date, CancellationToken ct) =>
        (await db.StaffMembers.AsNoTracking().Where(s => !(s is ManagementMember) && (s.ContractEndDate == null || s.ContractEndDate >= date))
            .Select(s => new { s.Id, s.FullName, s.JobTitle, Department = s.Department != null ? s.Department.Name : null }).ToListAsync(ct))
        .Where(s => StaffCategories.Of(false, s.JobTitle) == StaffCategory.Academic)
        .OrderBy(s => s.FullName, StringComparer.CurrentCultureIgnoreCase)
        .Select(s => new RevisionTeacherOption(s.Id, s.FullName, string.Join(" · ", new[] { s.Department, s.JobTitle }.Where(x => !string.IsNullOrWhiteSpace(x)))))
        .ToList();

    private static IQueryable<RevisionRow> Revisions(IQueryable<MorningRevisionRecord> q) =>
        q.AsNoTracking()
            .OrderBy(r => r.DutyAssignment.DutyDate).ThenBy(r => r.EntryTime == null).ThenBy(r => r.EntryTime).ThenBy(r => r.SchoolClass.SortOrder).ThenBy(r => r.Id)
            .Select(r => new RevisionRow(r.Id, r.DutyAssignmentId, r.DutyAssignment.Description, r.DutyAssignment.DutyDate, r.SchoolClassId, r.SchoolClass.Name,
                r.TeacherId, r.Teacher.FullName, r.Lesson, r.EntryTime, r.ExitTime, r.UpdatedAt, r.UpdatedBy));

    private Task<string> ClassNameAsync(int id, CancellationToken ct) => db.Classes.Where(c => c.Id == id).Select(c => c.Name).FirstAsync(ct);
    private Task<string> StaffNameAsync(int id, CancellationToken ct) => db.StaffMembers.Where(s => s.Id == id).Select(s => s.FullName).FirstAsync(ct);

    /// <summary>A recorded time, or "Not recorded" (never an invented time).</summary>
    public static string Hm(TimeOnly? t) => t is { } v ? v.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture) : "Not recorded";
}
