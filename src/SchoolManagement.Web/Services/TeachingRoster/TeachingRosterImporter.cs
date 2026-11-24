using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.OrgStructure;
using Row = SchoolManagement.Web.Services.TeachingRoster.TeachingStaff2027.Row;

namespace SchoolManagement.Web.Services.TeachingRoster;

public sealed class RosterImportReport
{
    public int RowsCreated { get; set; }
    public int RowsAlreadyRecorded { get; set; }
    public int BlankRows { get; set; }
    public int StaffReused { get; set; }
    public int StaffCreated { get; set; }
    public int Unresolved { get; set; }
    public int DepartmentLinks { get; set; }
    public int ReviewItemsCreated { get; set; }
    public int HomeroomAssigned { get; set; }
    public int HomeroomAlreadyAssigned { get; set; }
    public List<string> Lines { get; } = [];
}

public interface ITeachingRosterImporter
{
    /// <summary>Records Teaching Staff 2027 (idempotent: rows, staff, links and review items are added only when missing).</summary>
    Task<RosterImportReport> ImportAsync(CancellationToken ct = default);
}

/// <summary>
/// Imports <see cref="TeachingStaff2027"/>: one <see cref="TeachingRosterEntry"/> per source row (blank rows included, so
/// every row number is accounted for), its existing departments, and the staff member it belongs to.
/// <list type="bullet">
/// <item>Identity: the roster gives names only. A row is linked to an existing staff member only when exactly one staff
/// member in the row's own department has that name (the full name, or the name the source abbreviates: "Mamdouh" →
/// "Mr. Mamdouh Awaad"). Several matches → unresolved. No match: a name used on several roster rows stays unresolved (no
/// record is created until someone confirms who is who); any other name becomes a new staff record with the name exactly as
/// written and no invented details. A name that matches someone in another department is a separate person (flagged).</item>
/// <item>Homeroom rows become the KG class's Homeroom Teacher for 2026-2027 through the class teaching-team service, only
/// for a resolved person and a class without a current Homeroom Teacher (no one is replaced).</item>
/// <item>Nothing is calculated from Number of Classes, ص, م or بريك; no workload, timetable or other record is touched.</item>
/// </list>
/// </summary>
public sealed class TeachingRosterImporter(SchoolDbContext db, IClassTeamService classTeams) : ITeachingRosterImporter
{
    public async Task<RosterImportReport> ImportAsync(CancellationToken ct = default)
    {
        var report = new RosterImportReport();
        var departments = await db.Departments.AsNoTracking().ToListAsync(ct);
        var subcategories = await db.DepartmentSubcategories.AsNoTracking().Where(s => s.ParentId == null).ToListAsync(ct);
        var recorded = await db.TeachingRosterEntries.ToDictionaryAsync(e => e.SourceKey, ct);
        var reviewKeys = (await db.TeachingRosterReviewItems.Select(r => r.SourceKey).ToListAsync(ct)).ToHashSet();
        var staff = await db.StaffMembers.AsNoTracking().Select(s => new StaffRef(s.Id, s.FullName, s.DepartmentId, s.Subcategory, s is Teacher)).ToListAsync(ct);
        var nameUses = TeachingStaff2027.Rows.Where(r => !r.IsBlank).GroupBy(r => OrgStructureSeeder.PersonKey(r.Name)).ToDictionary(g => g.Key, g => g.Count());

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var row in TeachingStaff2027.Rows)
            {
                if (row.IsBlank) report.BlankRows++;
                if (recorded.ContainsKey(row.Key))
                {
                    report.RowsAlreadyRecorded++;
                    continue; // never changed once recorded (a person may have resolved it since)
                }

                var entry = new TeachingRosterEntry
                {
                    SourceKey = row.Key, SourceName = TeachingStaff2027.SourceName, AcademicYear = TeachingStaff2027.AcademicYear, RowNumber = row.No,
                    IsBlank = row.IsBlank, StaffName = row.Name, Subject = row.Subject, GradeLevels = row.Grades, NumberOfClasses = row.Classes,
                    ص = row.Sad, م = row.Meem, بريك = row.Break, CreatedAt = DateTime.UtcNow,
                };
                var reviews = new List<(RosterReviewKind Kind, string? Field, string? Source, string? Observed, string Note)>();

                if (!row.IsBlank)
                {
                    var homeroom = string.Equals(row.Subject, TeachingStaff2027.HomeRoom, StringComparison.OrdinalIgnoreCase);
                    entry.Role = homeroom ? RosterRole.HomeroomTeacher : RosterRole.SubjectTeacher;

                    // ---- departments from the subject parts (the label itself is kept as written)
                    var mapped = new List<(Department Department, DepartmentSubcategory? Sub, string Part)>();
                    if (!homeroom)
                        foreach (var part in (row.Subject ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (TeachingStaff2027.SubjectParts.TryGetValue(part, out var target)
                                && departments.FirstOrDefault(d => d.NormalizedName == TextNormalizer.NameKey(target.Department)) is { } dept)
                            {
                                var sub = target.Subcategory is null ? null
                                    : subcategories.FirstOrDefault(s => s.DepartmentId == dept.Id && s.NormalizedName == TextNormalizer.NameKey(target.Subcategory));
                                if (mapped.All(m => m.Department.Id != dept.Id)) mapped.Add((dept, sub, part));
                            }
                            else if (!TeachingStaff2027.LabelOnlyParts.Contains(part))
                                reviews.Add((RosterReviewKind.UnmappedSubject, "Subject", row.Subject, part,
                                    $"\"{part}\" in the subject \"{row.Subject}\" has no confirmed department; it is kept in the subject label only."));
                        }
                    foreach (var (dept, sub, part) in mapped)
                        entry.Departments.Add(new TeachingRosterDepartment { DepartmentId = dept.Id, DepartmentSubcategoryId = sub?.Id, SourcePart = part });
                    report.DepartmentLinks += mapped.Count;

                    if (row.Grades == "Not specified")
                        reviews.Add((RosterReviewKind.SourceValueUnclear, "Grade Levels", row.Grades, null,
                            "Grade Levels is transcribed as \"Not specified\"; kept as written — confirm whether the source cell is blank."));

                    // ---- identity
                    if (row.No == 42)
                    {
                        foreach (var (field, printed, observed) in TeachingStaff2027.Row42Corrections)
                            reviews.Add((RosterReviewKind.HandwrittenCorrection, field, printed, observed,
                                $"Handwritten correction (unconfirmed): {field} printed as \"{printed ?? "—"}\", appears to be corrected to \"{observed}\"."));
                        reviews.Add((RosterReviewKind.IdentityUnresolved, "Name", row.Name, "Ali",
                            "The name is corrected by hand; no staff identity is confirmed for this row until a person checks the source."));
                        entry.MatchNote = "Unresolved: handwritten name correction.";
                        report.Unresolved++;
                    }
                    else
                    {
                        var key = OrgStructureSeeder.PersonKey(row.Name);
                        var named = staff.Where(s => SameName(s.Name, key)).ToList();
                        List<StaffRef> inDept = homeroom ? [] : named.Where(s => mapped.Any(m => m.Department.Id == s.DepartmentId
                            && (m.Sub is null || string.Equals(m.Sub.Name, s.Subcategory, StringComparison.OrdinalIgnoreCase)))).ToList();
                        if (inDept.Count == 1)
                        {
                            entry.StaffMemberId = inDept[0].Id;
                            entry.MatchNote = $"Existing staff record #{inDept[0].Id} \"{inDept[0].Name}\": same name in the same department.";
                            report.StaffReused++;
                        }
                        else if (inDept.Count > 1)
                        {
                            entry.MatchNote = "Unresolved: several staff members of this name in the department.";
                            reviews.Add((RosterReviewKind.IdentityUnresolved, "Name", row.Name, string.Join(", ", inDept.Select(s => $"#{s.Id} {s.Name}")),
                                "Several existing staff members in the department have this name; choose the right one."));
                            report.Unresolved++;
                        }
                        else if (nameUses[key] > 1)
                        {
                            var rows = string.Join(", ", TeachingStaff2027.Rows.Where(r => !r.IsBlank && OrgStructureSeeder.PersonKey(r.Name) == key).Select(r => r.No));
                            entry.MatchNote = $"Unresolved: the name is on rows {rows}.";
                            reviews.Add((RosterReviewKind.DuplicateName, "Name", row.Name, null,
                                $"\"{row.Name}\" appears on rows {rows} and no existing record identifies the person: one person or several? No staff record is created until confirmed."));
                            report.Unresolved++;
                        }
                        else
                        {
                            var primary = mapped.FirstOrDefault();
                            StaffMember created = new Teacher
                            {
                                FullName = TextNormalizer.CleanText(row.Name), NormalizedName = TextNormalizer.NameKey(row.Name), ContactNumber = null, NormalizedContact = "",
                                DepartmentId = primary.Department?.Id, Subcategory = primary.Sub?.Name,
                            };
                            db.StaffMembers.Add(created);
                            await db.SaveChangesAsync(ct);
                            staff.Add(new StaffRef(created.Id, created.FullName, created.DepartmentId, created.Subcategory, true));
                            entry.StaffMemberId = created.Id;
                            entry.MatchNote = $"New staff record #{created.Id} (name as written in the source; no existing match).";
                            report.StaffCreated++;
                            if (named.Count > 0)
                                reviews.Add((RosterReviewKind.NameMatchesOtherDepartment, "Name", row.Name, string.Join(", ", named.Select(s => $"#{s.Id} {s.Name}")),
                                    $"The name also matches {string.Join(", ", named.Select(s => $"#{s.Id} {s.Name}"))} in another department; recorded as a separate person — merge only if confirmed."));
                        }
                    }
                }

                db.TeachingRosterEntries.Add(entry);
                await db.SaveChangesAsync(ct);
                recorded[row.Key] = entry;
                report.RowsCreated++;
                foreach (var (kind, field, source, observed, note) in reviews)
                {
                    var rk = $"{row.Key}:{kind}:{field}:{observed}";
                    if (rk.Length > TeachingRosterEntry.KeyMaxLength * 2) rk = rk[..(TeachingRosterEntry.KeyMaxLength * 2)];
                    if (!reviewKeys.Add(rk)) continue;
                    db.TeachingRosterReviewItems.Add(new TeachingRosterReviewItem
                    {
                        SourceKey = rk, TeachingRosterEntryId = entry.Id, Kind = kind, Field = field, SourceValue = source, ObservedValue = observed,
                        Note = note.Length > TeachingRosterReviewItem.NoteMaxLength ? note[..TeachingRosterReviewItem.NoteMaxLength] : note, CreatedAt = DateTime.UtcNow,
                    });
                    report.ReviewItemsCreated++;
                }
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        await AssignHomeroomsAsync(recorded, reviewKeys, report, ct);
        return report;
    }

    /// <summary>Exact name, or the name the source abbreviates (first names): "Mamdouh" ↔ "Mr. Mamdouh Awaad".</summary>
    private static bool SameName(string existing, string sourceKey)
    {
        var key = OrgStructureSeeder.PersonKey(existing);
        return sourceKey.Length > 0 && (key == sourceKey || key.StartsWith(sourceKey + " ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Homeroom rows → the KG class's Homeroom Teacher in the 2026-2027 academic year, through the class teaching-team service
    /// (its rules and history). Only a resolved person, a class whose name the source gives, and a class without a current
    /// Homeroom Teacher: an existing team is never changed (review item instead).
    /// </summary>
    private async Task AssignHomeroomsAsync(Dictionary<string, TeachingRosterEntry> entries, HashSet<string> reviewKeys, RosterImportReport report, CancellationToken ct)
    {
        var year = (await db.AcademicYears.AsNoTracking().Select(y => new { y.Id, y.Name }).ToListAsync(ct))
            .FirstOrDefault(y => y.Name.Contains("2026", StringComparison.Ordinal) && y.Name.Contains("2027", StringComparison.Ordinal));
        foreach (var row in TeachingStaff2027.Rows.Where(r => r.HomeroomClass is not null))
        {
            var entry = entries[row.Key];
            string? problem = null;
            var cls = await db.Classes.AsNoTracking().Where(c => c.NormalizedName == TextNormalizer.ClassKey(row.HomeroomClass)).Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct);
            if (entry.StaffMemberId is not { } staffId) problem = $"The person is not confirmed (see the row's identity review), so no Homeroom Teacher is recorded for {row.HomeroomClass}.";
            else if (year is null) problem = "There is no 2026-2027 academic year to record the Homeroom Teacher in.";
            else if (cls is null) problem = $"No class named {row.HomeroomClass} exists.";
            else
            {
                var team = await db.ClassTeachingAssignments.AsNoTracking().Where(a => a.SchoolClassId == cls.Id && a.AcademicYearId == year.Id && a.IsActive)
                    .Select(a => new { a.StaffMemberId, a.Role, a.StaffMember.FullName }).ToListAsync(ct);
                if (team.Any(a => a.StaffMemberId == staffId && a.Role == ClassTeachingRole.HomeroomTeacher)) { report.HomeroomAlreadyAssigned++; continue; }
                if (team.FirstOrDefault(a => a.Role == ClassTeachingRole.HomeroomTeacher) is { } current)
                    problem = $"{cls.Name} already has a Homeroom Teacher for {year.Name} ({current.FullName}); it was not replaced.";
                else if (team.Any(a => a.StaffMemberId == staffId))
                    problem = $"The person is already in {cls.Name}'s teaching team in another role; it was not changed.";
                else
                {
                    var co = team.Where(a => a.Role == ClassTeachingRole.CoTeacher).Select(a => (int?)a.StaffMemberId).FirstOrDefault();
                    var saved = await classTeams.SaveAsync(cls.Id, year.Id, staffId, co, ct);   // keeps any current Co-Teacher
                    if (saved.Succeeded) { report.HomeroomAssigned++; continue; }
                    problem = $"The class teaching-team rules refused it: {string.Join(" ", saved.Errors.Select(e => e.Value))}";
                }
            }
            var rk = $"{row.Key}:{RosterReviewKind.HomeroomNotAssigned}:Homeroom:{row.HomeroomClass}";
            if (!reviewKeys.Add(rk)) continue;
            db.TeachingRosterReviewItems.Add(new TeachingRosterReviewItem
            {
                SourceKey = rk, TeachingRosterEntryId = entry.Id, Kind = RosterReviewKind.HomeroomNotAssigned, Field = "Grade Levels", SourceValue = row.Grades,
                ObservedValue = row.HomeroomClass, Note = problem!, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            report.ReviewItemsCreated++;
        }
    }

    private sealed record StaffRef(int Id, string Name, int? DepartmentId, string? Subcategory, bool IsTeacher);
}
