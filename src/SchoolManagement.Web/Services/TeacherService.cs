using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Services;

public sealed class TeacherListQuery
{
    /// <summary>Value of the job-title filter that selects teachers without a job title.</summary>
    public const string NoJobTitle = "__none";

    public string? Search { get; set; }
    public string? JobTitle { get; set; }
    public int? DepartmentId { get; set; }
    public string? Department { get; set; }
    public string? Subcategory { get; set; }
    public string Sort { get; set; } = "name";
    public string Dir { get; set; } = "asc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(JobTitle) || DepartmentId is not null || !string.IsNullOrWhiteSpace(Department) || !string.IsNullOrWhiteSpace(Subcategory);
}

public sealed record TeacherRow(int Id, string FullName, string? JobTitle, string? ContactNumber, DateTime CreatedAt, DateTime UpdatedAt, int? DepartmentId = null, string? DepartmentName = null, string? Subcategory = null, string? Subject = null, bool IsHead = false)
{
    /// <summary>"Islamic · Quran" (empty when the department has no subcategories).</summary>
    public string SubcategoryLabel => DepartmentCatalog.Detail(Subcategory, Subject);
}

public sealed record JobTitleCount(string JobTitle, int Count);

public interface ITeacherService
{
    Task<int> CountAsync(CancellationToken ct = default);
    Task<PagedResult<TeacherRow>> ListAsync(TeacherListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<TeacherRow>> ListAllAsync(TeacherListQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<JobTitleCount>> JobTitlesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DepartmentSummary>> DepartmentSummariesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Department>> DepartmentsAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, DepartmentSubcategories>> SubcategoryRulesAsync(CancellationToken ct = default);
    Task<TeacherRow?> GetAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(string fullName, string? jobTitle, string? contactNumber, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(string fullName, string? jobTitle, string? contactNumber, int? departmentId, string? subcategory, CancellationToken ct = default);
    Task<ServiceResult> CreateAsync(string fullName, string? jobTitle, string? contactNumber, int? departmentId, string? subcategory, string? subject, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, string fullName, string? jobTitle, string? contactNumber, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, string fullName, string? jobTitle, string? contactNumber, int? departmentId, string? subcategory, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, string fullName, string? jobTitle, string? contactNumber, int? departmentId, string? subcategory, string? subject, CancellationToken ct = default);
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);
    Task<TeacherRow?> FindDuplicateAsync(string fullName, string? contactNumber, int? exceptId, CancellationToken ct = default);
}

/// <summary>
/// Teachers are the "Teacher" kind of <see cref="StaffMember"/>. Duplicate rule: the same name with
/// the same contact number, or the same name where either record has no number.
/// </summary>
public sealed class TeacherService(SchoolDbContext db) : ITeacherService
{
    public Task<int> CountAsync(CancellationToken ct = default) => db.Teachers.CountAsync(ct);

    public async Task<PagedResult<TeacherRow>> ListAsync(TeacherListQuery q, CancellationToken ct = default)
    {
        var filtered = Filter(q);
        var total = await filtered.CountAsync(ct);
        var size = Paging.NormalizePageSize(q.PageSize);
        var page = Paging.ClampPage(q.Page, total, size);
        var items = await Project(Sort(filtered, q)).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<TeacherRow> { Items = items, Page = page, PageSize = size, TotalCount = total };
    }

    public async Task<IReadOnlyList<TeacherRow>> ListAllAsync(TeacherListQuery q, CancellationToken ct = default) =>
        await Project(Sort(Filter(q), q)).ToListAsync(ct);

    public async Task<IReadOnlyList<JobTitleCount>> JobTitlesAsync(CancellationToken ct = default) =>
        await db.Teachers.AsNoTracking()
            .Where(t => t.JobTitle != null && t.JobTitle != "")
            .GroupBy(t => t.JobTitle!)
            .OrderBy(g => g.Key) // sort before projecting: EF cannot order by a record's property
            .Select(g => new JobTitleCount(g.Key, g.Count()))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DepartmentSummary>> DepartmentSummariesAsync(CancellationToken ct = default)
    {
        var depts = await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder).ToListAsync(ct);
        var counts = await db.StaffMembers.AsNoTracking().Where(s => s.DepartmentId != null)
            .GroupBy(s => s.DepartmentId!.Value).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return depts.Select(d => new DepartmentSummary
        {
            Id = d.Id,
            Name = d.Name,
            Subcategories = d.Subcategories,
            MemberCount = counts.GetValueOrDefault(d.Id, 0)
        }).ToList();
    }

    public async Task<IReadOnlyList<Department>> DepartmentsAsync(CancellationToken ct = default) =>
        await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<int, DepartmentSubcategories>> SubcategoryRulesAsync(CancellationToken ct = default) =>
        await DepartmentCatalog.LoadAsync(db, null, ct);

    public Task<TeacherRow?> GetAsync(int id, CancellationToken ct = default) =>
        Project(db.Teachers.AsNoTracking().Where(t => t.Id == id)).FirstOrDefaultAsync(ct);

    public Task<ServiceResult> CreateAsync(string fullName, string? jobTitle, string? contactNumber, CancellationToken ct = default) =>
        CreateAsync(fullName, jobTitle, contactNumber, null, null, ct);

    public Task<ServiceResult> CreateAsync(string fullName, string? jobTitle, string? contactNumber, int? departmentId, string? subcategory, CancellationToken ct = default) =>
        CreateAsync(fullName, jobTitle, contactNumber, departmentId, subcategory, null, ct);

    public async Task<ServiceResult> CreateAsync(string fullName, string? jobTitle, string? contactNumber, int? departmentId, string? subcategory, string? subject, CancellationToken ct = default)
    {
        var check = await ValidateAsync(fullName, jobTitle, contactNumber, null, ct);
        var placement = await PlacementAsync(departmentId, subcategory, subject, null, ct);
        foreach (var (field, message) in placement.Errors) check.AddError(field, message);
        if (!check.Succeeded) return check;

        var entity = new Teacher();
        Apply(entity, fullName, jobTitle, contactNumber);
        (entity.DepartmentId, entity.Subcategory, entity.Subject) = (departmentId, placement.Subcategory, placement.Subject);
        db.Teachers.Add(entity);
        if (!await TrySaveAsync(ct)) return Duplicate();
        return ServiceResult.Ok(entity.Id);
    }

    public Task<ServiceResult> UpdateAsync(int id, string fullName, string? jobTitle, string? contactNumber, CancellationToken ct = default) =>
        UpdateAsync(id, fullName, jobTitle, contactNumber, null, null, null, ct);

    public Task<ServiceResult> UpdateAsync(int id, string fullName, string? jobTitle, string? contactNumber, int? departmentId, string? subcategory, CancellationToken ct = default) =>
        UpdateAsync(id, fullName, jobTitle, contactNumber, departmentId, subcategory, null, ct);

    public async Task<ServiceResult> UpdateAsync(int id, string fullName, string? jobTitle, string? contactNumber, int? departmentId, string? subcategory, string? subject, CancellationToken ct = default)
    {
        var entity = await db.Teachers.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null) return ServiceResult.Fail("", "This teacher no longer exists.");

        var check = await ValidateAsync(fullName, jobTitle, contactNumber, id, ct);
        var placement = await PlacementAsync(departmentId, subcategory, subject, entity, ct);
        foreach (var (field, message) in placement.Errors) check.AddError(field, message);
        if (!check.Succeeded) return check;

        // Leaving a department ends a Head of Department appointment there (the history row is kept).
        if (entity.DepartmentId != departmentId)
        {
            var heads = await db.DepartmentHeads.Where(h => h.TeacherId == id && h.IsCurrent).ToListAsync(ct);
            foreach (var h in heads) DepartmentService.EndHead(h, "Left the department");
        }

        Apply(entity, fullName, jobTitle, contactNumber);
        (entity.DepartmentId, entity.Subcategory, entity.Subject) = (departmentId, placement.Subcategory, placement.Subject);
        if (!await TrySaveAsync(ct)) return Duplicate();
        return ServiceResult.Ok(id);
    }

    /// <summary>
    /// Department + subcategory rules (DepartmentSubcategories). Unknown departments are refused. A member
    /// keeps a value that has since been deactivated, as long as it is not changed.
    /// </summary>
    private async Task<(string? Subcategory, string? Subject, List<KeyValuePair<string, string>> Errors)> PlacementAsync(
        int? departmentId, string? subcategory, string? subject, StaffMember? existing, CancellationToken ct)
    {
        if (departmentId is null) return (null, null, []);
        var dept = await db.Departments.AsNoTracking().Where(d => d.Id == departmentId).Select(d => new { d.Name }).FirstOrDefaultAsync(ct);
        if (dept is null) return (null, null, [new("DepartmentId", "Choose a department from the list.")]);
        if (existing is not null && existing.DepartmentId == departmentId && existing.Subcategory is not null
            && string.Equals(existing.Subcategory, subcategory?.Trim(), StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(subject) || string.Equals(existing.Subject, subject.Trim(), StringComparison.OrdinalIgnoreCase)))
            return (existing.Subcategory, existing.Subject, []);
        return DepartmentCatalog.Normalize(dept.Name, await DepartmentCatalog.ForAsync(db, departmentId.Value, ct), subcategory, subject);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var headOf = await db.DepartmentHeads.Where(h => h.TeacherId == id && h.IsCurrent).Select(h => h.Department.Name).FirstOrDefaultAsync(ct);
        if (headOf is not null)
            return ServiceResult.Fail("", $"This teacher is the current Head of {headOf}. Assign a new HOD on the department page before deleting.");

        var sessions = await db.TimetableAssignments.CountAsync(t => t.TeacherId == id, ct);
        if (sessions > 0)
            return ServiceResult.Fail("", $"This teacher has {sessions} timetable session(s) (current or past). Remove them from the timetable; the record is kept for history.");

        // Duty and workload records keep their history: a teacher who appears in them is not deleted.
        var workload = await db.WorkloadAssignments.CountAsync(w => w.TeacherId == id, ct);
        var duties = await db.DutyAssignments.CountAsync(d => d.PrimaryStaffId == id || d.BackupStaffId == id, ct)
            + await db.DutyBackupResponses.CountAsync(r => r.StaffMemberId == id, ct)
            + await db.DutyStaffAssignments.CountAsync(a => a.StaffMemberId == id, ct)
            + await db.StaffFeedback.CountAsync(f => f.EvaluatorStaffId == id, ct)
            + await db.StudentArrivals.CountAsync(a => a.ReceivedByStaffId == id, ct);
        if (workload > 0 || duties > 0)
            return ServiceResult.Fail("", workload > 0
                ? $"This teacher has {workload} workload assignment(s). Remove them on the Teacher Workload page before deleting."
                : "This teacher appears in duty, evaluation or student arrival records, so the record is kept for history.");

        var deleted = await db.Teachers.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? ServiceResult.Fail("", "This teacher no longer exists.") : ServiceResult.Ok(id);
    }

    public Task<TeacherRow?> FindDuplicateAsync(string fullName, string? contactNumber, int? exceptId, CancellationToken ct = default)
    {
        var key = TextNormalizer.NameKey(fullName);
        var phone = TextNormalizer.PhoneKey(contactNumber);
        return Project(db.Teachers.AsNoTracking()
                .Where(t => t.NormalizedName == key && t.Id != exceptId)
                .Where(t => phone == "" || t.NormalizedContact == "" || t.NormalizedContact == phone))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<ServiceResult> ValidateAsync(string fullName, string? jobTitle, string? contactNumber, int? exceptId, CancellationToken ct)
    {
        var result = ServiceResult.Ok();
        var name = TextNormalizer.CleanText(fullName);
        if (name.Length == 0) result.AddError("FullName", "Enter the teacher's full name.");
        else if (name.Length > StaffMember.NameMaxLength) result.AddError("FullName", $"Use {StaffMember.NameMaxLength} characters or fewer.");
        else if (!TextNormalizer.ContainsLetter(name)) result.AddError("FullName", "The name must contain letters.");

        if (TextNormalizer.CleanText(jobTitle).Length > StaffMember.JobTitleMaxLength)
            result.AddError("JobTitle", $"Use {StaffMember.JobTitleMaxLength} characters or fewer.");

        var phone = TextNormalizer.CleanPhone(contactNumber);
        if (phone.Length > StaffMember.ContactMaxLength) result.AddError("ContactNumber", $"Use {StaffMember.ContactMaxLength} characters or fewer.");
        else if (!TextNormalizer.IsValidPhone(phone))
            result.AddError("ContactNumber", "Enter a valid phone number: digits, spaces or dashes, optional + at the start (6–15 digits).");

        if (!result.Succeeded) return result;

        var duplicate = await FindDuplicateAsync(name, phone, exceptId, ct);
        if (duplicate is not null) return Duplicate(duplicate);
        return result;
    }

    private static void Apply(Teacher entity, string fullName, string? jobTitle, string? contactNumber)
    {
        var title = TextNormalizer.CleanText(jobTitle);
        var phone = TextNormalizer.CleanPhone(contactNumber);
        entity.FullName = TextNormalizer.CleanText(fullName);
        entity.NormalizedName = TextNormalizer.NameKey(fullName);
        entity.JobTitle = title.Length == 0 ? null : title;
        entity.ContactNumber = phone.Length == 0 ? null : phone;
        entity.NormalizedContact = TextNormalizer.PhoneKey(phone);
    }

    private static ServiceResult Duplicate(TeacherRow? existing = null) =>
        ServiceResult.Fail("FullName", existing is null
            ? "A teacher with this name and contact number already exists."
            : $"\"{existing.FullName}\" already exists{(existing.ContactNumber is null ? "" : $" ({existing.ContactNumber})")}. Edit that record instead.");

    private IQueryable<Teacher> Filter(TeacherListQuery q)
    {
        var query = db.Teachers.AsNoTracking();
        var term = TextNormalizer.NameKey(q.Search);
        if (term.Length > 0)
        {
            var digits = TextNormalizer.PhoneKey(q.Search);
            // A search with digits and no letters looks up contact numbers instead of names.
            query = digits.Length >= 3 && !TextNormalizer.ContainsLetter(term)
                ? query.Where(t => t.NormalizedContact.Contains(digits))
                : query.Where(t => t.NormalizedName.Contains(term));
        }

        if (q.JobTitle == TeacherListQuery.NoJobTitle) query = query.Where(t => t.JobTitle == null || t.JobTitle == "");
        else if (!string.IsNullOrWhiteSpace(q.JobTitle)) query = query.Where(t => t.JobTitle == q.JobTitle);
        if (q.DepartmentId is { } did) query = query.Where(t => t.DepartmentId == did);
        else if (!string.IsNullOrWhiteSpace(q.Department)) query = query.Where(t => t.Department != null && t.Department.Name == q.Department);
        if (!string.IsNullOrWhiteSpace(q.Subcategory)) query = query.Where(t => t.Subcategory == q.Subcategory);
        return query;
    }

    private static IQueryable<Teacher> Sort(IQueryable<Teacher> query, TeacherListQuery q)
    {
        var desc = string.Equals(q.Dir, "desc", StringComparison.OrdinalIgnoreCase);
        return (q.Sort?.ToLowerInvariant()) switch
        {
            "job" => desc
                ? query.OrderByDescending(t => t.JobTitle).ThenBy(t => t.FullName)
                : query.OrderBy(t => t.JobTitle == null).ThenBy(t => t.JobTitle).ThenBy(t => t.FullName),
            "created" => desc
                ? query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
            _ => desc
                ? query.OrderByDescending(t => t.FullName).ThenByDescending(t => t.Id)
                : query.OrderBy(t => t.FullName).ThenBy(t => t.Id),
        };
    }

    private static IQueryable<TeacherRow> Project(IQueryable<Teacher> query) =>
        query.Select(t => new TeacherRow(t.Id, t.FullName, t.JobTitle, t.ContactNumber, t.CreatedAt, t.UpdatedAt, t.DepartmentId,
            t.Department != null ? t.Department.Name : null, t.Subcategory, t.Subject,
            t.DepartmentId != null && t.Department!.HeadAssignments.Any(h => h.IsCurrent && h.TeacherId == t.Id)));

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
