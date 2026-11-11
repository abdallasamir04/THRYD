using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record DepartmentHeadInfo(int AssignmentId, int TeacherId, string FullName, string? JobTitle, string? ContactNumber,
    DateOnly StartDate, DateTime AssignedAt, string? AssignedBy, string? Note);

public sealed record SubcategoryCount(string Subcategory, string? Subject, int Count);

public sealed class DepartmentCard
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public int SortOrder { get; init; }
    public int MemberCount { get; init; }
    public DepartmentHeadInfo? Head { get; init; }
    public IReadOnlyList<SubcategoryCount> Breakdown { get; init; } = [];
    public DepartmentSubcategories? Rules { get; init; }

    public int CountOf(string subcategory, string? subject = null) =>
        Breakdown.Where(b => b.Subcategory == subcategory && (subject is null || b.Subject == subject)).Sum(b => b.Count);
}

public sealed record DepartmentMember(int Id, string FullName, string? JobTitle, string? ContactNumber, string? Subcategory, string? Subject, bool IsHead, DateTime CreatedAt)
{
    public string SubcategoryLabel => DepartmentCatalog.Detail(Subcategory, Subject);
}

public sealed record HeadHistoryRow(int Id, int TeacherId, string FullName, DateOnly StartDate, DateOnly? EndDate, bool IsCurrent,
    string? EndReason, string? Note, string? AssignedBy, DateTime AssignedAt, string DepartmentName = "", int DepartmentId = 0);

/// <summary>A managed subcategory (or subject under one) with how many staff use it.</summary>
public sealed record SubcategoryItem(int Id, int? ParentId, string Name, bool IsActive, int SortOrder, int MemberCount, int DepartmentId = 0);

public sealed class DepartmentListQuery
{
    public string? Search { get; set; }

    /// <summary>"missing" = departments without an HOD, "assigned" = with one.</summary>
    public string? Hod { get; set; }

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Hod);
}

public sealed class DepartmentMemberQuery
{
    public string? Search { get; set; }
    public string? Subcategory { get; set; }
    public string Sort { get; set; } = "name";

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Subcategory);
}

public interface IDepartmentService
{
    Task<IReadOnlyList<DepartmentCard>> ListAsync(DepartmentListQuery query, CancellationToken ct = default);
    Task<DepartmentCard?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<DepartmentMember>> MembersAsync(int id, DepartmentMemberQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<HeadHistoryRow>> HeadHistoryAsync(int id, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, string? description, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, string? description, string? subcategoryLabel, string? subjectLabel, CancellationToken ct = default);
    Task<IReadOnlyList<HeadHistoryRow>> RecentHeadChangesAsync(int take, CancellationToken ct = default);
    Task<IReadOnlyList<SubcategoryItem>> SubcategoriesAsync(int departmentId, CancellationToken ct = default);
    Task<SubcategoryItem?> GetSubcategoryAsync(int id, CancellationToken ct = default);
    Task<(string? Subcategory, string? Subject)> LabelsAsync(int departmentId, CancellationToken ct = default);
    Task<ServiceResult> AddSubcategoryAsync(int departmentId, int? parentId, string? name, CancellationToken ct = default);
    Task<ServiceResult> UpdateSubcategoryAsync(int id, string? name, bool isActive, CancellationToken ct = default);
    Task<ServiceResult> AssignHeadAsync(int departmentId, int? teacherId, DateOnly? startDate, string? note, CancellationToken ct = default);
    Task<ServiceResult> RemoveMemberAsync(int departmentId, int staffId, CancellationToken ct = default);
}

/// <summary>
/// Academic departments, their members and their Head of Department. Rules enforced here (and by
/// the filtered unique indexes): one current HOD per department; the HOD is an academic staff
/// member (Teacher) of that department; changing the HOD ends the previous appointment.
/// </summary>
public sealed class DepartmentService(SchoolDbContext db, ICurrentUser currentUser) : IDepartmentService
{
    public static readonly DateOnly EarliestDate = new(2000, 1, 1);

    public async Task<IReadOnlyList<DepartmentCard>> ListAsync(DepartmentListQuery q, CancellationToken ct = default)
    {
        var cards = await CardsAsync(null, ct);
        var term = TextNormalizer.NameKey(q.Search);
        IEnumerable<DepartmentCard> result = cards;
        if (term.Length > 0)
            result = result.Where(c => TextNormalizer.NameKey(c.Name).Contains(term)
                || TextNormalizer.NameKey(c.Description).Contains(term)
                || (c.Head is not null && TextNormalizer.NameKey(c.Head.FullName).Contains(term)));
        if (q.Hod == "missing") result = result.Where(c => c.Head is null);
        else if (q.Hod == "assigned") result = result.Where(c => c.Head is not null);
        return result.ToList();
    }

    public async Task<DepartmentCard?> GetAsync(int id, CancellationToken ct = default) =>
        (await CardsAsync(id, ct)).FirstOrDefault();

    private async Task<List<DepartmentCard>> CardsAsync(int? id, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().Where(d => id == null || d.Id == id).OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct);
        var counts = await db.StaffMembers.AsNoTracking()
            .Where(s => s.DepartmentId != null && (id == null || s.DepartmentId == id))
            .GroupBy(s => new { s.DepartmentId, s.Subcategory, s.Subject })
            .Select(g => new { g.Key.DepartmentId, g.Key.Subcategory, g.Key.Subject, Count = g.Count() })
            .ToListAsync(ct);
        var heads = await db.DepartmentHeads.AsNoTracking()
            .Where(h => h.IsCurrent && (id == null || h.DepartmentId == id))
            .Select(h => new { h.DepartmentId, Info = new DepartmentHeadInfo(h.Id, h.TeacherId, h.Teacher.FullName, h.Teacher.JobTitle, h.Teacher.ContactNumber, h.StartDate, h.AssignedAt, h.AssignedBy, h.Note) })
            .ToDictionaryAsync(h => h.DepartmentId, h => h.Info, ct);
        var rules = await DepartmentCatalog.LoadAsync(db, id, ct);

        return depts.Select(d =>
        {
            var mine = counts.Where(c => c.DepartmentId == d.Id).ToList();
            return new DepartmentCard
            {
                Id = d.Id,
                Name = d.Name,
                Description = d.Description,
                SortOrder = d.SortOrder,
                MemberCount = mine.Sum(c => c.Count),
                Head = heads.GetValueOrDefault(d.Id),
                Breakdown = mine.Where(c => c.Subcategory != null).Select(c => new SubcategoryCount(c.Subcategory!, c.Subject, c.Count)).ToList(),
                Rules = rules.GetValueOrDefault(d.Id),
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<DepartmentMember>> MembersAsync(int id, DepartmentMemberQuery q, CancellationToken ct = default)
    {
        var query = db.Teachers.AsNoTracking().Where(t => t.DepartmentId == id);
        var term = TextNormalizer.NameKey(q.Search);
        if (term.Length > 0) query = query.Where(t => t.NormalizedName.Contains(term) || (t.JobTitle != null && t.JobTitle.Contains(q.Search!.Trim())));
        if (!string.IsNullOrWhiteSpace(q.Subcategory))
        {
            var parts = q.Subcategory.Split('|', 2);
            var sub = parts[0];
            query = query.Where(t => t.Subcategory == sub);
            if (parts.Length == 2) { var subject = parts[1]; query = query.Where(t => t.Subject == subject); }
        }

        query = q.Sort switch
        {
            "job" => query.OrderBy(t => t.JobTitle == null).ThenBy(t => t.JobTitle).ThenBy(t => t.FullName),
            "sub" => query.OrderBy(t => t.Subcategory == null).ThenBy(t => t.Subcategory).ThenBy(t => t.Subject).ThenBy(t => t.FullName),
            "added" => query.OrderByDescending(t => t.CreatedAt),
            _ => query.OrderBy(t => t.FullName),
        };

        var rows = await query.Select(t => new DepartmentMember(t.Id, t.FullName, t.JobTitle, t.ContactNumber, t.Subcategory, t.Subject,
            db.DepartmentHeads.Any(h => h.IsCurrent && h.DepartmentId == id && h.TeacherId == t.Id), t.CreatedAt)).ToListAsync(ct);
        // The HOD always leads the list.
        return rows.OrderByDescending(r => r.IsHead).ToList();
    }

    public async Task<IReadOnlyList<HeadHistoryRow>> HeadHistoryAsync(int id, CancellationToken ct = default) =>
        await db.DepartmentHeads.AsNoTracking().Where(h => h.DepartmentId == id)
            .OrderByDescending(h => h.IsCurrent).ThenByDescending(h => h.AssignedAt).ThenByDescending(h => h.Id)
            .Select(h => new HeadHistoryRow(h.Id, h.TeacherId, h.Teacher.FullName, h.StartDate, h.EndDate, h.IsCurrent, h.EndReason, h.Note, h.AssignedBy, h.AssignedAt,
                h.Department.Name, h.DepartmentId))
            .ToListAsync(ct);

    public async Task<ServiceResult> UpdateAsync(int id, string? description, CancellationToken ct = default)
    {
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return dept is null
            ? ServiceResult.Fail("", "This department no longer exists.")
            : await UpdateAsync(id, description, dept.SubcategoryLabel, dept.SubjectLabel, ct);
    }

    public async Task<ServiceResult> UpdateAsync(int id, string? description, string? subcategoryLabel, string? subjectLabel, CancellationToken ct = default)
    {
        var dept = await db.Departments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dept is null) return ServiceResult.Fail("", "This department no longer exists.");
        var result = ServiceResult.Ok(id);
        var text = description?.Trim() ?? "";
        if (text.Length > Department.DescriptionMaxLength) result.AddError("Description", $"Use {Department.DescriptionMaxLength} characters or fewer.");
        var label = TextNormalizer.CleanText(subcategoryLabel);
        var subjectText = TextNormalizer.CleanText(subjectLabel);
        if (label.Length > 50) result.AddError("SubcategoryLabel", "Use 50 characters or fewer.");
        if (subjectText.Length > 50) result.AddError("SubjectLabel", "Use 50 characters or fewer.");
        if (!result.Succeeded) return result;
        dept.Description = text.Length == 0 ? null : text;
        dept.SubcategoryLabel = label.Length == 0 ? null : label;
        dept.SubjectLabel = subjectText.Length == 0 ? null : subjectText;
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<HeadHistoryRow>> RecentHeadChangesAsync(int take, CancellationToken ct = default) =>
        await db.DepartmentHeads.AsNoTracking()
            .OrderByDescending(h => h.EndedAt ?? h.AssignedAt).ThenByDescending(h => h.Id).Take(Math.Clamp(take, 1, 200))
            .Select(h => new HeadHistoryRow(h.Id, h.TeacherId, h.Teacher.FullName, h.StartDate, h.EndDate, h.IsCurrent, h.EndReason, h.Note,
                h.AssignedBy, h.AssignedAt, h.Department.Name, h.DepartmentId))
            .ToListAsync(ct);

    // ------------------------------------------------------------------ managed subcategories (languages, WSD subdepartments, …)

    public async Task<IReadOnlyList<SubcategoryItem>> SubcategoriesAsync(int departmentId, CancellationToken ct = default)
    {
        var rows = await db.DepartmentSubcategories.AsNoTracking().Where(s => s.DepartmentId == departmentId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.ParentId, s.Name, s.IsActive, s.SortOrder, ParentName = s.Parent != null ? s.Parent.Name : null })
            .ToListAsync(ct);
        var staff = await db.Teachers.AsNoTracking().Where(t => t.DepartmentId == departmentId)
            .Select(t => new { t.Subcategory, t.Subject }).ToListAsync(ct);
        return rows.Select(r => new SubcategoryItem(r.Id, r.ParentId, r.Name, r.IsActive, r.SortOrder,
            r.ParentId is null
                ? staff.Count(s => string.Equals(s.Subcategory, r.Name, StringComparison.OrdinalIgnoreCase))
                : staff.Count(s => string.Equals(s.Subcategory, r.ParentName, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Subject, r.Name, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    public async Task<SubcategoryItem?> GetSubcategoryAsync(int id, CancellationToken ct = default) =>
        await db.DepartmentSubcategories.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new SubcategoryItem(s.Id, s.ParentId, s.Name, s.IsActive, s.SortOrder, 0, s.DepartmentId)).FirstOrDefaultAsync(ct);

    public async Task<(string? Subcategory, string? Subject)> LabelsAsync(int departmentId, CancellationToken ct = default)
    {
        var d = await db.Departments.AsNoTracking().Where(x => x.Id == departmentId).Select(x => new { x.SubcategoryLabel, x.SubjectLabel }).FirstOrDefaultAsync(ct);
        return (d?.SubcategoryLabel, d?.SubjectLabel);
    }

    public async Task<ServiceResult> AddSubcategoryAsync(int departmentId, int? parentId, string? name, CancellationToken ct = default)
    {
        if (!await db.Departments.AnyAsync(d => d.Id == departmentId, ct)) return ServiceResult.Fail("", "This department no longer exists.");
        var clean = TextNormalizer.CleanText(name);
        var check = ValidateName(clean);
        if (!check.Succeeded) return check;
        if (parentId is not null && !await db.DepartmentSubcategories.AnyAsync(s => s.Id == parentId && s.DepartmentId == departmentId && s.ParentId == null, ct))
            return ServiceResult.Fail("ParentId", "Choose a main option of this department.");
        var key = TextNormalizer.NameKey(clean);
        if (await db.DepartmentSubcategories.AnyAsync(s => s.DepartmentId == departmentId && s.ParentId == parentId && s.NormalizedName == key, ct))
            return ServiceResult.Fail("Name", $"\"{clean}\" already exists here. Activate it instead of adding it again.");

        var order = (await db.DepartmentSubcategories.Where(s => s.DepartmentId == departmentId && s.ParentId == parentId).MaxAsync(s => (int?)s.SortOrder, ct) ?? 0) + 1;
        var row = new DepartmentSubcategory { DepartmentId = departmentId, ParentId = parentId, Name = clean, NormalizedName = key, IsActive = true, SortOrder = order };
        db.DepartmentSubcategories.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("Name", $"\"{clean}\" already exists here.");
        }
        return ServiceResult.Ok(row.Id);
    }

    public async Task<ServiceResult> UpdateSubcategoryAsync(int id, string? name, bool isActive, CancellationToken ct = default)
    {
        var row = await db.DepartmentSubcategories.Include(s => s.Parent).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row is null) return ServiceResult.Fail("", "This option no longer exists.");
        var clean = TextNormalizer.CleanText(name);
        var check = ValidateName(clean);
        if (!check.Succeeded) return check;
        var key = TextNormalizer.NameKey(clean);
        if (await db.DepartmentSubcategories.AnyAsync(s => s.Id != id && s.DepartmentId == row.DepartmentId && s.ParentId == row.ParentId && s.NormalizedName == key, ct))
            return ServiceResult.Fail("Name", $"\"{clean}\" already exists here.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var old = row.Name;
        if (!string.Equals(old, clean, StringComparison.Ordinal))
        {
            // Staff and timetable records store the name: a rename carries over to them.
            if (row.ParentId is null)
            {
                await db.StaffMembers.Where(s => s.DepartmentId == row.DepartmentId && s.Subcategory == old)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Subcategory, clean), ct);
            }
            else
            {
                var parent = row.Parent!.Name;
                await db.StaffMembers.Where(s => s.DepartmentId == row.DepartmentId && s.Subcategory == parent && s.Subject == old)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Subject, clean), ct);
            }
            await db.TimetableAssignments.Where(t => t.DepartmentId == row.DepartmentId && t.Subject == old)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.Subject, clean), ct);
        }
        row.Name = clean;
        row.NormalizedName = key;
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(id);
    }

    private static ServiceResult ValidateName(string clean)
    {
        if (clean.Length == 0) return ServiceResult.Fail("Name", "Enter a name.");
        if (clean.Length > DepartmentSubcategory.NameMaxLength) return ServiceResult.Fail("Name", $"Use {DepartmentSubcategory.NameMaxLength} characters or fewer.");
        if (!TextNormalizer.ContainsLetter(clean)) return ServiceResult.Fail("Name", "The name must contain letters.");
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> AssignHeadAsync(int departmentId, int? teacherId, DateOnly? startDate, string? note, CancellationToken ct = default)
    {
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct);
        if (dept is null) return ServiceResult.Fail("", "This department no longer exists.");

        var result = ServiceResult.Ok();
        if (teacherId is not { } tid) result.AddError("TeacherId", "Choose the Head of Department.");
        if (startDate is not { } start) result.AddError("StartDate", "Enter the date the appointment starts.");
        else if (start < EarliestDate || start > SchoolWeek.Today.AddYears(1))
            result.AddError("StartDate", $"Enter a date between {Fmt(EarliestDate)} and one year from today.");
        var cleanNote = note?.Trim();
        if (cleanNote?.Length > DepartmentHeadAssignment.NoteMaxLength)
            result.AddError("Note", $"Use {DepartmentHeadAssignment.NoteMaxLength} characters or fewer.");
        if (!result.Succeeded) return result;

        // Management Team members are a separate staff type and can never be academic HODs.
        if (await db.ManagementMembers.AnyAsync(m => m.Id == teacherId, ct))
            return ServiceResult.Fail("TeacherId", "Management Team members cannot be assigned as an academic Head of Department.");
        var teacher = await db.Teachers.AsNoTracking().Where(t => t.Id == teacherId).Select(t => new { t.Id, t.FullName, t.DepartmentId }).FirstOrDefaultAsync(ct);
        if (teacher is null) return ServiceResult.Fail("TeacherId", "Choose an academic staff member from the list.");
        if (teacher.DepartmentId != departmentId)
            return ServiceResult.Fail("TeacherId", $"{teacher.FullName} is not a member of {dept.Name}. Add them to the department first.");

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var current = await db.DepartmentHeads.FirstOrDefaultAsync(h => h.DepartmentId == departmentId && h.IsCurrent, ct);
        if (current is not null)
        {
            if (current.TeacherId == teacher.Id) return ServiceResult.Fail("TeacherId", $"{teacher.FullName} is already the Head of {dept.Name}.");
            if (startDate < current.StartDate)
                return ServiceResult.Fail("StartDate", $"The new appointment cannot start before the current one ({Fmt(current.StartDate)}).");
            EndHead(current, $"Replaced by {teacher.FullName}", startDate);
        }

        try
        {
            await db.SaveChangesAsync(ct); // frees the "one current HOD" slot before the new row is added
            db.DepartmentHeads.Add(new DepartmentHeadAssignment
            {
                DepartmentId = departmentId,
                TeacherId = teacher.Id,
                IsCurrent = true,
                StartDate = startDate!.Value,
                AssignedAt = DateTime.UtcNow,
                AssignedBy = currentUser.Name,
                Note = string.IsNullOrEmpty(cleanNote) ? null : cleanNote,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "The Head of Department was changed by someone else at the same time. Reload the page and try again.");
        }
        return ServiceResult.Ok(departmentId);
    }

    public async Task<ServiceResult> RemoveMemberAsync(int departmentId, int staffId, CancellationToken ct = default)
    {
        var teacher = await db.Teachers.FirstOrDefaultAsync(t => t.Id == staffId && t.DepartmentId == departmentId, ct);
        if (teacher is null) return ServiceResult.Fail("", "This person is no longer a member of the department.");
        var heads = await db.DepartmentHeads.Where(h => h.TeacherId == staffId && h.IsCurrent).ToListAsync(ct);
        foreach (var h in heads) EndHead(h, "Removed from the department");
        (teacher.DepartmentId, teacher.Subcategory, teacher.Subject) = (null, null, null);
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(staffId);
    }

    private static string Fmt(DateOnly d) => d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Ends a current HOD appointment, keeping the row as history.</summary>
    public static void EndHead(DepartmentHeadAssignment head, string reason, DateOnly? endDate = null)
    {
        var end = endDate ?? SchoolWeek.Today;
        head.IsCurrent = false;
        head.EndedAt = DateTime.UtcNow;
        head.EndDate = end < head.StartDate ? head.StartDate : end;
        head.EndReason = reason.Length > DepartmentHeadAssignment.NoteMaxLength ? reason[..DepartmentHeadAssignment.NoteMaxLength] : reason;
    }
}
