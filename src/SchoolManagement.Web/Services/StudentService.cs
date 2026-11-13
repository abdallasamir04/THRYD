using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed class StudentListQuery
{
    public string? Search { get; set; }
    public int? ClassId { get; set; }
    public string Sort { get; set; } = "name";
    public string Dir { get; set; } = "asc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || ClassId is not null;
}

public sealed record StudentRow(int Id, string FullName, int ClassId, string ClassName, bool ClassActive, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record StudentTransferHistoryRow(int Id, int StudentId, string StudentName, int FromClassId, string FromClassName, int ToClassId, string ToClassName, DateTime TransferredAt, string? TransferredBy);

public interface IStudentService
{
    Task<int> CountAsync(CancellationToken ct = default);
    Task<PagedResult<StudentRow>> ListAsync(StudentListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<StudentRow>> ListAllAsync(StudentListQuery query, CancellationToken ct = default);
    Task<StudentRow?> GetAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(string fullName, int classId, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, string fullName, int classId, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
    Task<StudentRow?> FindDuplicateAsync(string fullName, int classId, int? exceptId, CancellationToken ct = default);
    Task<ServiceResult> TransferClassAsync(int studentId, int targetClassId, string? operatorName, CancellationToken ct = default);
    Task<IReadOnlyList<StudentTransferHistoryRow>> GetTransferHistoryAsync(int studentId, CancellationToken ct = default);
}

public sealed class StudentService(SchoolDbContext db) : IStudentService
{
    public Task<int> CountAsync(CancellationToken ct = default) => db.Students.CountAsync(ct);

    public async Task<PagedResult<StudentRow>> ListAsync(StudentListQuery q, CancellationToken ct = default)
    {
        var filtered = Filter(q);
        var total = await filtered.CountAsync(ct);
        var size = Paging.NormalizePageSize(q.PageSize);
        var page = Paging.ClampPage(q.Page, total, size);

        var items = await Project(Sort(filtered, q)).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<StudentRow> { Items = items, Page = page, PageSize = size, TotalCount = total };
    }

    public async Task<IReadOnlyList<StudentRow>> ListAllAsync(StudentListQuery q, CancellationToken ct = default) =>
        await Project(Sort(Filter(q), q)).ToListAsync(ct);

    public Task<StudentRow?> GetAsync(int id, CancellationToken ct = default) =>
        Project(db.Students.AsNoTracking().Where(s => s.Id == id)).FirstOrDefaultAsync(ct);

    public async Task<ServiceResult> CreateAsync(string fullName, int classId, CancellationToken ct = default)
    {
        var check = await ValidateAsync(fullName, classId, null, null, ct);
        if (!check.Succeeded) return check;

        var entity = new Student
        {
            FullName = TextNormalizer.CleanText(fullName),
            NormalizedName = TextNormalizer.NameKey(fullName),
            SchoolClassId = classId,
        };
        db.Students.Add(entity);
        if (!await TrySaveAsync(ct)) return Duplicate();
        return ServiceResult.Ok(entity.Id);
    }

    public async Task<ServiceResult> UpdateAsync(int id, string fullName, int classId, CancellationToken ct = default)
    {
        var entity = await db.Students.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This student no longer exists.");

        var check = await ValidateAsync(fullName, classId, id, entity.SchoolClassId, ct);
        if (!check.Succeeded) return check;

        entity.FullName = TextNormalizer.CleanText(fullName);
        entity.NormalizedName = TextNormalizer.NameKey(fullName);
        entity.SchoolClassId = classId;
        if (!await TrySaveAsync(ct)) return Duplicate();
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        // Arrival history is kept: a student with arrival records is not deleted.
        var arrivals = await db.StudentArrivals.CountAsync(a => a.StudentId == id, ct);
        if (arrivals > 0)
            return ServiceResult.Fail("", $"This student has {arrivals} arrival record(s), which are kept as attendance history. Move them to another class instead of deleting.");
        var lessons = await db.SessionAttendance.CountAsync(r => r.StudentId == id, ct);
        if (lessons > 0)
            return ServiceResult.Fail("", $"This student has {lessons} lesson attendance record(s), which are kept as attendance history. Move them to another class instead of deleting.");
        var incidents = await db.IncidentStudents.CountAsync(i => i.StudentId == id, ct);
        if (incidents > 0)
            return ServiceResult.Fail("", $"This student is named in {incidents} incident report(s), which are kept as confidential records. Move them to another class instead of deleting.");
        var belongings = await db.BelongingsRecords.CountAsync(b => b.StudentId == id, ct);
        if (belongings > 0)
            return ServiceResult.Fail("", $"This student has {belongings} belongings record(s), which are kept as history. Move them to another class instead of deleting.");

        var deleted = await db.Students.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? ServiceResult.Fail("", "This student no longer exists.") : ServiceResult.Ok(id);
    }

    public Task<StudentRow?> FindDuplicateAsync(string fullName, int classId, int? exceptId, CancellationToken ct = default)
    {
        var key = TextNormalizer.NameKey(fullName);
        return Project(db.Students.AsNoTracking()
                .Where(s => s.NormalizedName == key && s.SchoolClassId == classId && s.Id != exceptId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ServiceResult> TransferClassAsync(int studentId, int targetClassId, string? operatorName, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var student = await db.Students.Include(s => s.SchoolClass).FirstOrDefaultAsync(s => s.Id == studentId, ct);
        if (student is null) return ServiceResult.Fail("", "This student no longer exists.");

        if (student.SchoolClassId == targetClassId)
            return ServiceResult.Fail("TargetClassId", "The student is already in this class.");

        var target = await db.Classes.FirstOrDefaultAsync(c => c.Id == targetClassId, ct);
        if (target is null || !target.IsActive)
            return ServiceResult.Fail("TargetClassId", "Choose an active class to transfer the student to.");

        var targetCount = await db.Students.CountAsync(s => s.SchoolClassId == targetClassId, ct);
        if (targetCount >= target.Capacity)
            return ServiceResult.Fail("TargetClassId", "This class is full. You cannot transfer this student to this class.");

        var hasDuplicate = await db.Students.AnyAsync(s => s.SchoolClassId == targetClassId && s.NormalizedName == student.NormalizedName, ct);
        if (hasDuplicate)
            return ServiceResult.Fail("TargetClassId", $"{target.Name} already has a student named \"{student.FullName}\".");

        var fromClassId = student.SchoolClassId;
        student.SchoolClassId = targetClassId;

        db.StudentTransfers.Add(new StudentTransferHistory
        {
            StudentId = student.Id,
            FromClassId = fromClassId,
            ToClassId = targetClassId,
            TransferredAt = DateTime.UtcNow,
            TransferredBy = operatorName
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(student.Id);
    }

    public async Task<IReadOnlyList<StudentTransferHistoryRow>> GetTransferHistoryAsync(int studentId, CancellationToken ct = default)
    {
        return await db.StudentTransfers.AsNoTracking()
            .Where(t => t.StudentId == studentId)
            .OrderByDescending(t => t.TransferredAt)
            .Select(t => new StudentTransferHistoryRow(t.Id, t.StudentId, t.Student.FullName, t.FromClassId, t.FromClass.Name, t.ToClassId, t.ToClass.Name, t.TransferredAt, t.TransferredBy))
            .ToListAsync(ct);
    }

    private async Task<ServiceResult> ValidateAsync(string fullName, int classId, int? exceptId, int? currentClassId, CancellationToken ct)
    {
        var result = ServiceResult.Ok();
        var name = TextNormalizer.CleanText(fullName);
        if (name.Length == 0) result.AddError("FullName", "Enter the student's full name.");
        else if (name.Length > Student.NameMaxLength) result.AddError("FullName", $"Use {Student.NameMaxLength} characters or fewer.");
        else if (!TextNormalizer.ContainsLetter(name)) result.AddError("FullName", "The name must contain letters.");

        var cls = await db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId, ct);
        if (cls is null) result.AddError("SchoolClassId", "Choose a class from the list.");
        // An inactive class can be kept by a student already in it, but nobody new is placed there.
        else if (!cls.IsActive && cls.Id != currentClassId) result.AddError("SchoolClassId", $"{cls.Name} is inactive. Choose an active class.");
        else if (cls.Id != currentClassId)
        {
            var count = await db.Students.CountAsync(s => s.SchoolClassId == classId && s.Id != (exceptId ?? 0), ct);
            if (count >= cls.Capacity)
            {
                // Model-level as well, so the message is visible even when the form shows no class dropdown.
                result.AddError("", $"{cls.Name} is full ({count}/{cls.Capacity} students). Increase its capacity on the Classes page or choose another class.");
            }
        }

        if (!result.Succeeded) return result;

        var duplicate = await FindDuplicateAsync(name, classId, exceptId, ct);
        if (duplicate is not null) return Duplicate(duplicate.FullName, duplicate.ClassName);
        return result;
    }

    private static ServiceResult Duplicate(string? name = null, string? className = null) =>
        ServiceResult.Fail("FullName", name is null
            ? "A student with this name is already in this class."
            : $"\"{name}\" is already in {className}.");

    private IQueryable<Student> Filter(StudentListQuery q)
    {
        var query = db.Students.AsNoTracking();
        var term = TextNormalizer.NameKey(q.Search);
        if (term.Length > 0) query = query.Where(s => s.NormalizedName.Contains(term));
        if (q.ClassId is { } classId) query = query.Where(s => s.SchoolClassId == classId);
        return query;
    }

    private static IQueryable<Student> Sort(IQueryable<Student> query, StudentListQuery q)
    {
        var desc = string.Equals(q.Dir, "desc", StringComparison.OrdinalIgnoreCase);
        return (q.Sort?.ToLowerInvariant()) switch
        {
            "class" => desc
                ? query.OrderByDescending(s => s.SchoolClass.SortOrder).ThenByDescending(s => s.SchoolClass.Name).ThenBy(s => s.FullName)
                : query.OrderBy(s => s.SchoolClass.SortOrder).ThenBy(s => s.SchoolClass.Name).ThenBy(s => s.FullName),
            "created" => desc
                ? query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
                : query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id),
            _ => desc
                ? query.OrderByDescending(s => s.FullName).ThenByDescending(s => s.Id)
                : query.OrderBy(s => s.FullName).ThenBy(s => s.Id),
        };
    }

    private static IQueryable<StudentRow> Project(IQueryable<Student> query) =>
        query.Select(s => new StudentRow(s.Id, s.FullName, s.SchoolClassId, s.SchoolClass.Name, s.SchoolClass.IsActive, s.CreatedAt, s.UpdatedAt));

    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
