using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.OrgStructure;

public enum SeedOutcome { Created, Reused, Conflict }

/// <summary>What the import did with one person of the source.</summary>
public sealed record OrgSeedPerson(string Key, string Name, string Mobile, int? StaffId, SeedOutcome Outcome, string? Note);

public sealed class OrgSeedReport
{
    public int TiersCreated { get; set; }
    public int UnitsCreated { get; set; }
    public int DepartmentLinksCreated { get; set; }
    public int DepartmentsCreated { get; set; }
    public int PositionsCreated { get; set; }
    public int AssignmentsCreated { get; set; }
    public int AssignmentsAlreadyRecorded { get; set; }
    public int FlagsCreated { get; set; }
    public List<OrgSeedPerson> People { get; } = [];
    public List<string> Conflicts { get; } = [];
    public List<string> Notes { get; } = [];

    public int StaffCreated => People.Count(p => p.Outcome == SeedOutcome.Created);
    public int StaffReused => People.Count(p => p.Outcome == SeedOutcome.Reused);
    public bool ChangedAnything => TiersCreated + UnitsCreated + DepartmentLinksCreated + DepartmentsCreated + PositionsCreated + AssignmentsCreated + FlagsCreated + StaffCreated > 0;
}

public interface IOrgStructureSeeder
{
    /// <summary>
    /// Records the official structure (<see cref="OrgStructureSource"/>): creates what is missing and never changes or
    /// removes anything already recorded. Safe to run any number of times.
    /// </summary>
    Task<OrgSeedReport> SeedAsync(CancellationToken ct = default);
}

/// <summary>
/// Idempotent import of the official organizational structure.
/// <list type="number">
/// <item>Tiers, units and positions are found by their source key (or, the first time, by name) and created only when missing.</item>
/// <item>A person is the existing staff record with the same phone number <b>and</b> the same name (ignoring Mr./Ms./Dr.).
/// The same number with a different name is a different person: a new record is created and both are flagged (shared number).
/// The same name with a different number is a different person (never merged on the name alone). The same name on a record
/// without a phone number is uncertain: nothing is created and the record is flagged for review.</item>
/// <item>Assignments are created once (source key); an assignment later ended or changed by an administrator is never re-created.</item>
/// </list>
/// Existing staff details (name, phone, department) are never overwritten.
/// </summary>
public sealed class OrgStructureSeeder(SchoolDbContext db, ICurrentUser? currentUser = null) : IOrgStructureSeeder
{
    public const string AssignmentNote = "Recorded from the official organizational structure (2026/2027).";

    private static readonly HashSet<string> Honorifics = ["MR", "MRS", "MS", "MISS", "DR"];

    /// <summary>Name key without honorifics and punctuation: "Dr. Rania Moh. Hijazi" → "RANIA MOH HIJAZI".</summary>
    public static string PersonKey(string? name) =>
        string.Join(' ', TextNormalizer.NameKey(name).Replace('.', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Honorifics.Contains(t)));

    public async Task<OrgSeedReport> SeedAsync(CancellationToken ct = default)
    {
        var report = new OrgSeedReport();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var tiers = await SeedTiersAsync(report, ct);
        var units = await SeedUnitsAsync(tiers, report, ct);
        var positions = await SeedPositionsAsync(units, report, ct);
        var people = await ReconcilePeopleAsync(report, ct);
        await SeedAssignmentsAsync(people, positions, report, ct);
        await SeedFlagsAsync(people, report, ct);

        await tx.CommitAsync(ct);
        return report;
    }

    // ------------------------------------------------------------------ tiers, units, positions

    private async Task<Dictionary<string, OrgTier>> SeedTiersAsync(OrgSeedReport report, CancellationToken ct)
    {
        var all = await db.OrgTiers.ToListAsync(ct);
        var map = new Dictionary<string, OrgTier>();
        foreach (var s in OrgStructureSource.Tiers)
        {
            var key = TextNormalizer.NameKey(s.Name);
            var tier = all.FirstOrDefault(t => t.SourceKey == s.Key) ?? all.FirstOrDefault(t => t.SourceKey is null && t.NormalizedName == key);
            if (tier is null)
            {
                tier = new OrgTier { SourceKey = s.Key, Name = s.Name, NormalizedName = key, Description = s.Description, Section = s.Section, SortOrder = s.Sort };
                db.OrgTiers.Add(tier);
                all.Add(tier);
                report.TiersCreated++;
            }
            else tier.SourceKey ??= s.Key;
            map[s.Key] = tier;
        }
        await db.SaveChangesAsync(ct);
        return map;
    }

    private async Task<Dictionary<string, OrgUnit>> SeedUnitsAsync(Dictionary<string, OrgTier> tiers, OrgSeedReport report, CancellationToken ct)
    {
        var all = await db.OrgUnits.Include(u => u.Departments).ToListAsync(ct);
        var departments = await db.Departments.ToListAsync(ct);
        var subcategories = await db.DepartmentSubcategories.Where(s => s.ParentId == null).ToListAsync(ct);
        var map = new Dictionary<string, OrgUnit>();
        foreach (var s in OrgStructureSource.Units)
        {
            var tier = tiers[s.TierKey];
            var key = TextNormalizer.NameKey(s.Name);
            var unit = all.FirstOrDefault(u => u.SourceKey == s.Key) ?? all.FirstOrDefault(u => u.SourceKey is null && u.OrgTierId == tier.Id && u.NormalizedName == key);
            if (unit is null)
            {
                unit = new OrgUnit { SourceKey = s.Key, Name = s.Name, NormalizedName = key, Description = s.Description, Tier = tier, SortOrder = s.Sort };
                db.OrgUnits.Add(unit);
                all.Add(unit);
                report.UnitsCreated++;
            }
            else unit.SourceKey ??= s.Key;

            foreach (var spec in s.Departments ?? [])
            {
                var (department, subcategory) = ResolveDepartment(spec, departments, subcategories, report);
                if (department is null) continue;
                if (unit.Departments.Any(l => l.Department == department || (department.Id != 0 && l.DepartmentId == department.Id))) continue;
                unit.Departments.Add(new OrgUnitDepartment { Department = department, Subcategory = subcategory });
                report.DepartmentLinksCreated++;
            }
            map[s.Key] = unit;
        }
        await db.SaveChangesAsync(ct);
        return map;
    }

    /// <summary>
    /// "English" → the English department; "Second Language/French" → Second Language with its French subcategory, else a
    /// department named French. A department is created only when it genuinely does not exist in either form.
    /// </summary>
    private (Department? Department, DepartmentSubcategory? Subcategory) ResolveDepartment(string spec, List<Department> departments,
        List<DepartmentSubcategory> subcategories, OrgSeedReport report)
    {
        var parts = spec.Split('/', 2);
        var department = departments.FirstOrDefault(d => d.NormalizedName == TextNormalizer.NameKey(parts[0]));
        if (parts.Length == 1)
        {
            if (department is not null) return (department, null);
            return (CreateDepartment(parts[0], departments, report), null);
        }
        var subKey = TextNormalizer.NameKey(parts[1]);
        var sub = department is null ? null : subcategories.FirstOrDefault(s => s.DepartmentId == department.Id && s.NormalizedName == subKey);
        if (department is not null && sub is not null) return (department, sub);
        // The language is not a subcategory of the department: use (or, if truly missing, create) a department of its own.
        var own = departments.FirstOrDefault(d => d.NormalizedName == subKey) ?? CreateDepartment(parts[1], departments, report);
        return (own, null);
    }

    private Department CreateDepartment(string name, List<Department> departments, OrgSeedReport report)
    {
        var department = new Department
        {
            Name = name, NormalizedName = TextNormalizer.NameKey(name), SortOrder = departments.Count == 0 ? 1 : departments.Max(d => d.SortOrder) + 1,
            Description = "Added by the organizational structure import (not found among the existing departments).",
        };
        db.Departments.Add(department);
        departments.Add(department);
        report.DepartmentsCreated++;
        report.Notes.Add($"Department \"{name}\" did not exist in any form and was created.");
        return department;
    }

    private async Task<Dictionary<string, StaffPosition>> SeedPositionsAsync(Dictionary<string, OrgUnit> units, OrgSeedReport report, CancellationToken ct)
    {
        var all = await db.StaffPositions.ToListAsync(ct);
        var map = new Dictionary<string, StaffPosition>();
        foreach (var s in OrgStructureSource.Positions)
        {
            var unit = units[s.UnitKey];
            var key = TextNormalizer.NameKey(s.Title);
            var position = all.FirstOrDefault(p => p.SourceKey == s.Key)
                ?? all.FirstOrDefault(p => p.SourceKey is null && p.OrgUnitId == unit.Id && p.NormalizedTitle == key && p.GradeRange == s.GradeRange);
            if (position is null)
            {
                position = new StaffPosition { SourceKey = s.Key, Title = s.Title, NormalizedTitle = key, Unit = unit, RoleType = s.Role, GradeRange = s.GradeRange, SortOrder = s.Sort };
                db.StaffPositions.Add(position);
                all.Add(position);
                report.PositionsCreated++;
            }
            else position.SourceKey ??= s.Key;
            map[s.Key] = position;
        }
        await db.SaveChangesAsync(ct);
        return map;
    }

    // ------------------------------------------------------------------ people

    private async Task<Dictionary<string, StaffMember>> ReconcilePeopleAsync(OrgSeedReport report, CancellationToken ct)
    {
        var staff = await db.StaffMembers.ToListAsync(ct);
        var departments = await db.Departments.ToListAsync(ct);
        var subcategories = await db.DepartmentSubcategories.Where(s => s.ParentId == null).ToListAsync(ct);
        var resolved = new Dictionary<string, StaffMember>();

        foreach (var p in OrgStructureSource.People)
        {
            var phone = TextNormalizer.PhoneKey(p.Mobile);
            var person = PersonKey(p.Name);
            var byPhone = staff.Where(s => s.NormalizedContact == phone).ToList();
            var same = byPhone.Where(s => PersonKey(s.FullName) == person).ToList();
            if (same.Count == 1)
            {
                resolved[p.Key] = same[0];
                report.People.Add(new(p.Key, p.Name, p.Mobile, same[0].Id == 0 ? null : same[0].Id, SeedOutcome.Reused,
                    same[0].Id == 0 ? "Matched another source row with the same name and phone" : $"Existing record #{same[0].Id} (same name and phone)"));
                continue;
            }
            if (same.Count > 1)
            {
                var ids = string.Join(", ", same.Select(s => "#" + s.Id));
                report.Conflicts.Add($"{p.Name} ({p.Mobile}): several existing records have this name and phone ({ids}). Nothing was assigned; merge or correct them first.");
                report.People.Add(new(p.Key, p.Name, p.Mobile, null, SeedOutcome.Conflict, $"Several records match ({ids})"));
                continue;
            }
            // The sources confirm this person is the staff member of certain Teaching Staff 2027 rows (same name, same role):
            // reuse that record. Its phone is filled only when empty; a different phone is never overwritten.
            if (p.RosterRows is { Length: > 0 } rows)
            {
                var linked = await db.TeachingRosterEntries.Where(e => e.SourceName == TeachingRoster.TeachingStaff2027.SourceName
                        && rows.Contains(e.RowNumber) && e.StaffMemberId != null)
                    .Select(e => e.StaffMemberId!.Value).Distinct().ToListAsync(ct);
                if (linked.Count == 1 && staff.FirstOrDefault(s => s.Id == linked[0]) is { } rosterStaff && PersonKey(rosterStaff.FullName) == person)
                {
                    if (rosterStaff.NormalizedContact.Length == 0)
                    {
                        rosterStaff.ContactNumber = TextNormalizer.CleanPhone(p.Mobile);
                        rosterStaff.NormalizedContact = phone;
                        await db.SaveChangesAsync(ct);
                        resolved[p.Key] = rosterStaff;
                        report.People.Add(new(p.Key, p.Name, p.Mobile, rosterStaff.Id, SeedOutcome.Reused,
                            $"Existing record #{rosterStaff.Id} of Teaching Staff 2027 row(s) {string.Join(", ", rows)} (the sources name the same person); phone filled from the source"));
                        continue;
                    }
                    report.Conflicts.Add($"{p.Name} ({p.Mobile}): the record #{rosterStaff.Id} of Teaching Staff 2027 row(s) {string.Join(", ", rows)} has another phone ({rosterStaff.ContactNumber}). Nothing was changed or assigned.");
                    report.People.Add(new(p.Key, p.Name, p.Mobile, null, SeedOutcome.Conflict, $"Phone differs from record #{rosterStaff.Id}"));
                    await AddFlagAsync(rosterStaff, DataQualityFlagKind.IdentityReview, $"flag:identity:{p.Key}:{rosterStaff.Id}",
                        $"The official staff document lists {p.Name} with {p.Mobile}; this record (Teaching Staff 2027 row {string.Join(", ", rows)}) has {rosterStaff.ContactNumber}. Confirm the correct number.", report, ct);
                    continue;
                }
                // No row is linked to anyone yet: a new record is created below; the reconciliation links the rows to it.
            }

            // A record with the same name but no phone number may or may not be this person: never merged, never duplicated.
            var uncertain = staff.Where(s => s.NormalizedContact == "" && PersonKey(s.FullName) == person).ToList();
            if (uncertain.Count > 0)
            {
                var ids = string.Join(", ", uncertain.Select(s => "#" + s.Id));
                report.Conflicts.Add($"{p.Name} ({p.Mobile}): existing record(s) {ids} have the same name but no phone number. Confirm whether it is the same person; nothing was created or assigned.");
                report.People.Add(new(p.Key, p.Name, p.Mobile, null, SeedOutcome.Conflict, $"Same name without a phone ({ids})"));
                foreach (var u in uncertain.Where(u => u.Id != 0))
                    await AddFlagAsync(u, DataQualityFlagKind.IdentityReview, $"flag:identity:{p.Key}:{u.Id}",
                        $"The official structure lists {p.Name} ({p.Mobile}). This record has the same name but no phone number — confirm whether it is the same person.", report, ct);
                continue;
            }

            var created = Create(p, departments, subcategories);
            staff.Add(created);
            db.StaffMembers.Add(created);
            await db.SaveChangesAsync(ct);
            resolved[p.Key] = created;
            var note = byPhone.Count > 0
                ? $"Phone shared with {string.Join(", ", byPhone.Select(s => s.FullName))} — kept as a separate person"
                : null;
            report.People.Add(new(p.Key, p.Name, p.Mobile, created.Id, SeedOutcome.Created, note));
        }
        return resolved;
    }

    private static StaffMember Create(OrgStructureSource.Person p, List<Department> departments, List<DepartmentSubcategory> subcategories)
    {
        StaffMember s = p.Kind == OrgStructureSource.PersonKind.Teacher ? new Teacher() : new ManagementMember();
        s.FullName = p.Name;
        s.NormalizedName = TextNormalizer.NameKey(p.Name);
        s.ContactNumber = TextNormalizer.CleanPhone(p.Mobile);
        s.NormalizedContact = TextNormalizer.PhoneKey(p.Mobile);
        if (p.Kind == OrgStructureSource.PersonKind.Teacher && p.Department is { } spec)
        {
            var parts = spec.Split('/', 2);
            var department = departments.FirstOrDefault(d => d.NormalizedName == TextNormalizer.NameKey(parts[0]));
            if (parts.Length == 2)
            {
                var sub = department is null ? null : subcategories.FirstOrDefault(c => c.DepartmentId == department.Id && c.NormalizedName == TextNormalizer.NameKey(parts[1]));
                if (sub is not null) (s.DepartmentId, s.Subcategory) = (department!.Id, sub.Name);
                else s.DepartmentId = departments.FirstOrDefault(d => d.NormalizedName == TextNormalizer.NameKey(parts[1]))?.Id;
            }
            else s.DepartmentId = department?.Id;
        }
        return s;
    }

    // ------------------------------------------------------------------ assignments and flags

    private async Task SeedAssignmentsAsync(Dictionary<string, StaffMember> people, Dictionary<string, StaffPosition> positions, OrgSeedReport report, CancellationToken ct)
    {
        var recorded = (await db.StaffPositionAssignments.Where(a => a.SourceKey != null).Select(a => a.SourceKey!).ToListAsync(ct)).ToHashSet();
        var created = new HashSet<int>();
        // Primary positions first, so each person's first recorded position is the one the source lists first.
        foreach (var s in OrgStructureSource.Assignments.OrderByDescending(a => a.Primary))
        {
            if (!people.TryGetValue(s.PersonKey, out var staff))
            {
                report.Notes.Add($"Not assigned: {s.PersonKey} → {s.PositionKey} (the person could not be reconciled).");
                continue;
            }
            if (recorded.Contains(s.Key)) { report.AssignmentsAlreadyRecorded++; continue; }
            var position = positions[s.PositionKey];
            var current = await db.StaffPositionAssignments.Where(a => a.StaffMemberId == staff.Id && a.IsCurrent).ToListAsync(ct);
            if (current.Any(a => a.StaffPositionId == position.Id))
            {
                report.AssignmentsAlreadyRecorded++; // already held (recorded by hand)
                continue;
            }
            var primary = current.Count == 0 || (s.Primary && !current.Any(a => a.IsPrimary));
            db.StaffPositionAssignments.Add(new StaffPositionAssignment
            {
                SourceKey = s.Key, StaffMemberId = staff.Id, StaffPositionId = position.Id, IsCurrent = true, IsPrimary = primary,
                StartDate = SchoolWeek.Today, Notes = AssignmentNote, AssignedBy = currentUser?.Name ?? "Official structure import",
            });
            await db.SaveChangesAsync(ct);
            report.AssignmentsCreated++;
            created.Add(staff.Id);
        }

        // The job title other modules show mirrors the primary position — for new records always; an existing record's own
        // job title is never overwritten (only filled when empty).
        var sync = new OrgStructureService(db, TrustedOrgStructureAccess.Instance);
        var newStaff = report.People.Where(p => p.Outcome == SeedOutcome.Created && p.StaffId is not null).Select(p => p.StaffId!.Value).ToHashSet();
        foreach (var staff in people.Values.Distinct())
            if (newStaff.Contains(staff.Id) || (created.Contains(staff.Id) && string.IsNullOrWhiteSpace(staff.JobTitle)))
                await sync.SyncJobTitleAsync(staff.Id, ct);
    }

    private async Task SeedFlagsAsync(Dictionary<string, StaffMember> people, OrgSeedReport report, CancellationToken ct)
    {
        foreach (var pending in OrgStructureSource.PendingConfirmation)
            if (people.TryGetValue(pending.PersonKey, out var staff))
                await AddFlagAsync(staff, DataQualityFlagKind.PositionRequiresConfirmation, $"flag:pending:{pending.PersonKey}", pending.Message, report, ct);

        // A phone number the source gives to two different people: both records are kept and flagged for verification.
        var staffByPhone = (await db.StaffMembers.Where(s => s.NormalizedContact != "").Select(s => new { s.Id, s.FullName, s.ContactNumber, s.NormalizedContact }).ToListAsync(ct))
            .ToLookup(s => s.NormalizedContact);
        foreach (var staff in people.Values.Distinct())
        {
            var others = staffByPhone[staff.NormalizedContact].Where(o => o.Id != staff.Id && PersonKey(o.FullName) != PersonKey(staff.FullName)).ToList();
            if (others.Count == 0) continue;
            var names = string.Join(", ", others.Select(o => o.FullName));
            await AddFlagAsync(staff, DataQualityFlagKind.SharedPhoneNumber, $"flag:shared-phone:{staff.NormalizedContact}:{staff.Id}",
                $"The phone number {staff.ContactNumber} is also recorded for {names}. The records are kept separate; verify which number is correct.", report, ct);
        }
    }

    private async Task AddFlagAsync(StaffMember staff, DataQualityFlagKind kind, string key, string message, OrgSeedReport report, CancellationToken ct)
    {
        if (await db.StaffDataQualityFlags.AnyAsync(f => f.SourceKey == key, ct)) return;
        db.StaffDataQualityFlags.Add(new StaffDataQualityFlag
        {
            SourceKey = key, StaffMemberId = staff.Id, Kind = kind, CreatedAt = DateTime.UtcNow,
            Message = message.Length > StaffDataQualityFlag.MessageMaxLength ? message[..StaffDataQualityFlag.MessageMaxLength] : message,
        });
        await db.SaveChangesAsync(ct);
        report.FlagsCreated++;
    }
}
