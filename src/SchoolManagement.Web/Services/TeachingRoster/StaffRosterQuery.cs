using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.TeachingRoster;

/// <summary>One Teaching Staff 2027 row of a person, exactly as the source gives it (null = blank cell).</summary>
public sealed record StaffRosterRow(int RowNumber, string Source, string AcademicYear, string? Name, string? Subject, string? GradeLevels, int? NumberOfClasses,
    int? Sad, int? Meem, int? Break, RosterRole? Role, IReadOnlyList<string> Departments);

/// <summary>An open review item of a roster row.</summary>
public sealed record RosterReviewRow(int Id, int RowNumber, string? Name, string? Subject, RosterReviewKind Kind, string? Field, string? SourceValue,
    string? ObservedValue, string Note, int? StaffId, string? StaffName, bool IsResolved, string? Resolution);

/// <summary>An open data-quality flag on a staff record.</summary>
public sealed record StaffFlagRow(int Id, int StaffId, string StaffName, string? Phone, DataQualityFlagKind Kind, string Message);

/// <summary>A staff record that is not current staff, and how much history still refers to it.</summary>
public sealed record ArchivedStaffRow(int Id, string FullName, string? Phone, string? JobTitle, DateTime ArchivedAt, string? Reason);

public sealed record StaffDataReview(IReadOnlyList<StaffFlagRow> Flags, IReadOnlyList<RosterReviewRow> RosterReviews, IReadOnlyList<int> UnlinkedRows,
    IReadOnlyList<int> BlankRows, IReadOnlyList<ArchivedStaffRow> Archived, int ResolvedRosterReviews);

public interface IStaffRosterQuery
{
    /// <summary>The person's Teaching Staff 2027 rows (source values as written), by row number.</summary>
    Task<IReadOnlyList<StaffRosterRow>> ForStaffAsync(int staffId, CancellationToken ct = default);

    /// <summary>Roster rows per staff member, for directory badges.</summary>
    Task<ILookup<int, StaffRosterRow>> ByStaffAsync(CancellationToken ct = default);

    /// <summary>Everything about the staff data that still needs a person's decision.</summary>
    Task<StaffDataReview> ReviewAsync(CancellationToken ct = default);

    /// <summary>Number of open items (flags on current staff + open roster review items).</summary>
    Task<int> OpenCountAsync(CancellationToken ct = default);

    /// <summary>The person's rows and, when the record is not current staff, when and why it was archived.</summary>
    Task<StaffRosterProfile> ProfileAsync(int staffId, CancellationToken ct = default);
}

/// <summary>What a staff profile shows from the official sources: the person's roster rows and the record's archive state.</summary>
public sealed record StaffRosterProfile(IReadOnlyList<StaffRosterRow> Rows, DateTime? ArchivedAt, string? ArchiveReason)
{
    public static readonly StaffRosterProfile Empty = new([], null, null);
}

public sealed class StaffRosterQuery(SchoolDbContext db) : IStaffRosterQuery
{
    public async Task<IReadOnlyList<StaffRosterRow>> ForStaffAsync(int staffId, CancellationToken ct = default) =>
        (await Rows(db.TeachingRosterEntries.AsNoTracking().Where(e => e.StaffMemberId == staffId), ct)).Select(r => r.Row).ToList();

    public async Task<ILookup<int, StaffRosterRow>> ByStaffAsync(CancellationToken ct = default) =>
        (await Rows(db.TeachingRosterEntries.AsNoTracking().Where(e => e.StaffMemberId != null), ct)).ToLookup(r => r.StaffId!.Value, r => r.Row);

    private static async Task<List<(int? StaffId, StaffRosterRow Row)>> Rows(IQueryable<TeachingRosterEntry> q, CancellationToken ct) =>
        (await q.OrderBy(e => e.RowNumber)
            .Select(e => new
            {
                e.StaffMemberId, e.RowNumber, e.SourceName, e.AcademicYear, e.StaffName, e.Subject, e.GradeLevels, e.NumberOfClasses, e.ص, e.م, e.بريك, e.Role,
                Departments = e.Departments.Select(d => d.Subcategory != null ? d.Department.Name + " / " + d.Subcategory.Name : d.Department.Name).ToList(),
            }).ToListAsync(ct))
        .Select(e => (e.StaffMemberId, new StaffRosterRow(e.RowNumber, e.SourceName, e.AcademicYear, e.StaffName, e.Subject, e.GradeLevels, e.NumberOfClasses,
            e.ص, e.م, e.بريك, e.Role, e.Departments)))
        .ToList();

    public async Task<StaffDataReview> ReviewAsync(CancellationToken ct = default)
    {
        var flags = await db.StaffDataQualityFlags.AsNoTracking().Where(f => !f.IsResolved && f.StaffMember.ArchivedAt == null)
            .OrderBy(f => f.Kind).ThenBy(f => f.StaffMember.FullName)
            .Select(f => new StaffFlagRow(f.Id, f.StaffMemberId, f.StaffMember.FullName, f.StaffMember.ContactNumber, f.Kind, f.Message)).ToListAsync(ct);
        var reviews = await db.TeachingRosterReviewItems.AsNoTracking().Where(i => !i.IsResolved)
            .OrderBy(i => i.Entry.RowNumber).ThenBy(i => i.Kind)
            .Select(i => new RosterReviewRow(i.Id, i.Entry.RowNumber, i.Entry.StaffName, i.Entry.Subject, i.Kind, i.Field, i.SourceValue, i.ObservedValue, i.Note,
                i.Entry.StaffMemberId, i.Entry.StaffMember != null ? i.Entry.StaffMember.FullName : null, i.IsResolved, i.Resolution)).ToListAsync(ct);
        var rows = await db.TeachingRosterEntries.AsNoTracking().Where(e => e.SourceName == TeachingStaff2027.SourceName)
            .Select(e => new { e.RowNumber, e.IsBlank, e.StaffMemberId }).ToListAsync(ct);
        var archived = await db.StaffMembers.AsNoTracking().Where(s => s.ArchivedAt != null).OrderBy(s => s.FullName)
            .Select(s => new ArchivedStaffRow(s.Id, s.FullName, s.ContactNumber, s.JobTitle, s.ArchivedAt!.Value, s.ArchiveReason)).ToListAsync(ct);
        return new StaffDataReview(flags, reviews,
            rows.Where(r => !r.IsBlank && r.StaffMemberId is null).Select(r => r.RowNumber).OrderBy(n => n).ToList(),
            rows.Where(r => r.IsBlank).Select(r => r.RowNumber).OrderBy(n => n).ToList(),
            archived,
            await db.TeachingRosterReviewItems.CountAsync(i => i.IsResolved, ct));
    }

    public async Task<StaffRosterProfile> ProfileAsync(int staffId, CancellationToken ct = default)
    {
        var archive = await db.StaffMembers.AsNoTracking().Where(s => s.Id == staffId).Select(s => new { s.ArchivedAt, s.ArchiveReason }).FirstOrDefaultAsync(ct);
        return new StaffRosterProfile(await ForStaffAsync(staffId, ct), archive?.ArchivedAt, archive?.ArchiveReason);
    }

    public async Task<int> OpenCountAsync(CancellationToken ct = default) =>
        await db.StaffDataQualityFlags.CountAsync(f => !f.IsResolved && f.StaffMember.ArchivedAt == null, ct)
        + await db.TeachingRosterReviewItems.CountAsync(i => !i.IsResolved, ct);
}
