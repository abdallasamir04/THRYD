using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.TeachingRoster;

namespace SchoolManagement.Web.Services.OrgStructure;

/// <summary>An archived (or removed) staff record and what still refers to it.</summary>
public sealed record ArchivedStaffLine(int Id, string Name, string? Phone, string Outcome, IReadOnlyList<string> EndedRoles, IReadOnlyDictionary<string, int> References);

public sealed class OfficialStaffReport
{
    public int StaffBefore { get; set; }
    public int ActiveBefore { get; set; }
    public int StaffAfter { get; set; }
    public int ActiveAfter { get; set; }
    public OrgSeedReport? Seed { get; set; }
    public RosterImportReport Roster { get; } = new();
    public List<string> RowsLinked { get; } = [];
    public List<string> Flags { get; } = [];
    public List<string> ReviewsResolved { get; } = [];
    public List<ArchivedStaffLine> Archived { get; } = [];
    public HashSet<int> Allowlist { get; } = [];
    public List<string> Checks { get; } = [];
    public List<string> Failures { get; } = [];
    public bool ArchiveSkipped { get; set; }
}

public interface IOfficialStaffReconciler
{
    /// <summary>
    /// Reconciles the staff database with the authorized sources (the Royal Kings staff document, recorded in
    /// <see cref="OrgStructureSource"/>, <see cref="TeachingStaff2027"/> and the floor-management charts): records what is missing, links the people both
    /// sources name, archives every record neither source supports (keeping its history), then validates the result.
    /// Idempotent: a second run changes nothing.
    /// </summary>
    Task<OfficialStaffReport> RunAsync(CancellationToken ct = default);
}

public sealed class OfficialStaffReconciler(SchoolDbContext db, IOrgStructureSeeder seeder, ITeachingRosterImporter roster, IClassTeamService classTeams)
    : IOfficialStaffReconciler
{
    public const string By = "Official staff reconciliation";
    public const string ArchiveReason = "Not in the official 2026/2027 staff sources (Royal Kings staff document · Teaching Staff 2027). Kept as a record for its history.";
    private const string EndReason = "Archived: not in the official 2026/2027 staff sources";

    /// <summary>Spellings of one first name that the sources use for possibly the same person (never merged automatically).</summary>
    private static readonly Dictionary<string, string> Variants = new(StringComparer.Ordinal)
    {
        ["HOUDA"] = "HUDA", ["NORHAN"] = "NOURHAN", ["ZINAB"] = "ZAINAB", ["MAKAK"] = "MALAK", ["SARITHA"] = "SARITA",
    };

    public async Task<OfficialStaffReport> RunAsync(CancellationToken ct = default)
    {
        var report = new OfficialStaffReport
        {
            StaffBefore = await db.StaffMembers.CountAsync(ct),
            ActiveBefore = await db.StaffMembers.CountAsync(s => s.ArchivedAt == null, ct),
        };

        // 1. Teaching Staff 2027: rows not yet recorded, then recorded rows brought in line with the corrected transcription.
        var imported = await roster.ImportAsync(ct);
        report.Roster.RowsCreated = imported.RowsCreated;
        report.Roster.StaffCreated = imported.StaffCreated;
        await roster.SyncRecordedRowsAsync(report.Roster, ct);

        // 2. The Royal Kings staff document (management, operations, department leadership, Homeroom, Co-Teachers).
        report.Seed = await seeder.SeedAsync(ct);

        // 3. Teaching Staff rows the sources confirm belong to a staff-document person.
        await LinkRosterRowsAsync(report, ct);

        // 4. First-name rows that may be a Homeroom Teacher / Co-Teacher of the staff document: flagged, never merged.
        await FlagPossibleIdentitiesAsync(report, ct);
        await AnnotateNourTarekAsync(report, ct);

        // 5. The allowlist: every person traceable to one of the two sources.
        foreach (var p in report.Seed.People.Where(p => p.StaffId is not null && p.Outcome != SeedOutcome.Conflict)) report.Allowlist.Add(p.StaffId!.Value);
        foreach (var id in await db.TeachingRosterEntries.Where(e => e.SourceName == TeachingStaff2027.SourceName && e.StaffMemberId != null)
                     .Select(e => e.StaffMemberId!.Value).Distinct().ToListAsync(ct))
            report.Allowlist.Add(id);
        // The Royal Kings floor-management charts (authorized 2026-11-24): the floor teams recorded from them.
        var floorKey = FloorManagement.FloorManagementSeeder.KeyPrefix + ":";
        foreach (var id in await db.FloorStaffAssignments.Where(a => a.IsCurrent && a.SourceKey != null && a.SourceKey.StartsWith(floorKey))
                     .Select(a => a.StaffMemberId).Distinct().ToListAsync(ct))
            report.Allowlist.Add(id);

        // 6. Everyone else stops being current staff. Not while a source person is unreconciled (they might be one of them).
        if (report.Seed.Conflicts.Count > 0)
        {
            report.ArchiveSkipped = true;
            report.Failures.Add("Archiving skipped: the staff document has unreconciled people (see conflicts). Resolve them and run again.");
        }
        else await ArchiveUnsupportedAsync(report, ct);

        // 7. Homeroom classes of the Home Room rows (a class freed by an archived record can now be recorded).
        await roster.AssignHomeroomsAsync(report.Roster, ct);

        // 8. Review items whose question no longer exists (the other record is not current staff).
        await ResolveStaleReviewsAsync(report, ct);

        await ValidateAsync(report, ct);
        report.StaffAfter = await db.StaffMembers.CountAsync(ct);
        report.ActiveAfter = await db.StaffMembers.CountAsync(s => s.ArchivedAt == null, ct);
        return report;
    }

    // ------------------------------------------------------------------ 3. roster rows of staff-document people

    private async Task LinkRosterRowsAsync(OfficialStaffReport report, CancellationToken ct)
    {
        foreach (var person in OrgStructureSource.People.Where(p => p.RosterRows is { Length: > 0 }))
        {
            if (report.Seed!.People.FirstOrDefault(p => p.Key == person.Key && p.Outcome != SeedOutcome.Conflict)?.StaffId is not { } staffId) continue;
            var role = OrgStructureSource.Assignments.Where(a => a.PersonKey == person.Key)
                .Select(a => OrgStructureSource.Positions.First(p => p.Key == a.PositionKey).Title).FirstOrDefault() ?? "staff member";
            var entries = await db.TeachingRosterEntries.Include(e => e.ReviewItems)
                .Where(e => e.SourceName == TeachingStaff2027.SourceName && person.RosterRows!.Contains(e.RowNumber)).ToListAsync(ct);
            foreach (var entry in entries)
            {
                if (entry.StaffMemberId == staffId) continue;
                if (entry.StaffMemberId is { } other)
                {
                    report.Failures.Add($"Row {entry.RowNumber} is linked to #{other}, but the staff document's {person.Name} is #{staffId}; not changed.");
                    continue;
                }
                entry.StaffMemberId = staffId;
                entry.MatchNote = $"Staff record #{staffId} \"{person.Name}\": the official staff document lists {person.Name} ({person.Mobile}) as {role} — same name and role.";
                foreach (var item in entry.ReviewItems.Where(i => !i.IsResolved && i.Kind is RosterReviewKind.DuplicateName or RosterReviewKind.IdentityUnresolved))
                    Resolve(item, $"Row {entry.RowNumber} is {person.Name} ({person.Mobile}), {role} in the official staff document (same name and role).", report);
                report.RowsLinked.Add($"Row {entry.RowNumber} \"{entry.StaffName}\" → #{staffId} {person.Name} ({person.Mobile})");
            }
            await db.SaveChangesAsync(ct);

            // Other rows of the same name stay unresolved; their review says which row is now identified.
            var nameKey = OrgStructureSeeder.PersonKey(person.Name);
            var others = await db.TeachingRosterEntries.Include(e => e.ReviewItems)
                .Where(e => e.SourceName == TeachingStaff2027.SourceName && e.StaffMemberId == null && !person.RosterRows!.Contains(e.RowNumber)).ToListAsync(ct);
            foreach (var entry in others.Where(e => OrgStructureSeeder.PersonKey(e.StaffName) == nameKey))
            foreach (var item in entry.ReviewItems.Where(i => !i.IsResolved && i.Kind == RosterReviewKind.DuplicateName))
            {
                var note = $"\"{entry.StaffName}\" is also on row(s) {string.Join(", ", person.RosterRows!)}, which the official staff document identifies as {person.Name} ({person.Mobile}, {role}). " +
                           $"Whether row {entry.RowNumber} ({entry.Subject}) is the same person is not confirmed: no staff record is linked until a person decides.";
                if (item.Note != note) item.Note = Clip(note, TeachingRosterReviewItem.NoteMaxLength);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    // ------------------------------------------------------------------ 4. possible identities (flags only)

    private async Task FlagPossibleIdentitiesAsync(OfficialStaffReport report, CancellationToken ct)
    {
        var tier7 = OrgStructureSource.Assignments.Where(a => a.PositionKey is "pos-homeroom" or "pos-co-teacher").ToList();
        var entries = await db.TeachingRosterEntries.Include(e => e.ReviewItems).Include(e => e.StaffMember)
            .Where(e => e.SourceName == TeachingStaff2027.SourceName && e.StaffName != null).ToListAsync(ct);
        foreach (var a in tier7)
        {
            var person = OrgStructureSource.People.First(p => p.Key == a.PersonKey);
            var tokens = OrgStructureSeeder.PersonKey(person.Name).Split(' ');
            if (tokens.Length < 2) continue;                       // single names are matched by their roster rows
            var role = OrgStructureSource.Positions.First(p => p.Key == a.PositionKey).Title;
            var staffId = report.Seed!.People.FirstOrDefault(p => p.Key == person.Key)?.StaffId;
            foreach (var entry in entries)
            {
                var key = OrgStructureSeeder.PersonKey(entry.StaffName);
                if (key.Contains(' ') || (key != tokens[0] && Variants.GetValueOrDefault(key) != tokens[0])) continue;
                if (person.RosterRows?.Contains(entry.RowNumber) == true || entry.StaffMemberId == staffId) continue;
                // A row already matched to a full-name record (by its department, e.g. "Habiba" → German HOD Habiba Essam) is not in doubt.
                if (entry.StaffMember is { } matched && OrgStructureSeeder.PersonKey(matched.FullName).Contains(' ')) continue;
                var spelling = key == tokens[0] ? "" : $" (spelled \"{entry.StaffName}\" in the roster)";
                var text = $"Teaching Staff 2027 row {entry.RowNumber} \"{entry.StaffName}\" ({entry.Subject}, {entry.GradeLevels}) may be {person.Name}{spelling}, " +
                           $"{role} in the official staff document ({person.Mobile}). They are kept as separate records; merge only if confirmed.";
                if (entry.StaffMember is { } linked)
                {
                    if (await AddFlagAsync(linked.Id, DataQualityFlagKind.IdentityReview, $"flag:possible-identity:{person.Key}:row-{entry.RowNumber}", text, ct))
                        report.Flags.Add($"#{linked.Id} {linked.FullName}: {text}");
                }
                else foreach (var item in entry.ReviewItems.Where(i => !i.IsResolved && i.Kind is RosterReviewKind.DuplicateName or RosterReviewKind.IdentityUnresolved))
                {
                    var add = $" Possible match: {person.Name}, {role} ({person.Mobile}) — not confirmed.";
                    if (item.Note.Contains(person.Name, StringComparison.Ordinal)) continue;
                    item.Note = Clip(item.Note + add, TeachingRosterReviewItem.NoteMaxLength);
                    report.Flags.Add($"Row {entry.RowNumber} \"{entry.StaffName}\" review: possible match {person.Name}");
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Nour Tarek's title stays unassigned; the flag says what another (unauthorized) record suggests.</summary>
    private async Task AnnotateNourTarekAsync(OfficialStaffReport report, CancellationToken ct)
    {
        if (report.Seed!.People.FirstOrDefault(p => p.Key == "p-nour-tarek")?.StaffId is not { } id) return;
        var flag = await db.StaffDataQualityFlags.FirstOrDefaultAsync(f => f.SourceKey == "flag:pending:p-nour-tarek" && !f.IsResolved, ct);
        if (flag is null) return;
        var floor = await db.FloorStaffAssignments.AsNoTracking().Where(a => a.StaffMemberId == id && a.IsCurrent)
            .Select(a => new { a.Role, Floor = a.Floor.Name }).ToListAsync(ct);
        if (floor.Count == 0) return;
        var hint = $" Floor Management records (not an authorized source for the staff roster) list him as {string.Join(", ", floor.Select(f => $"{Words(f.Role.ToString())} — {f.Floor}"))}; not used as his position.";
        if (flag.Message.Contains("Floor Management", StringComparison.Ordinal)) return;
        flag.Message = Clip(flag.Message + hint, StaffDataQualityFlag.MessageMaxLength);
        await db.SaveChangesAsync(ct);
        report.Flags.Add($"#{id} Mr. Nour Tarek: title unreadable — flag kept open;{hint}");
    }

    // ------------------------------------------------------------------ 6. archive

    private async Task ArchiveUnsupportedAsync(OfficialStaffReport report, CancellationToken ct)
    {
        var outside = await db.StaffMembers.Where(s => !report.Allowlist.Contains(s.Id)).OrderBy(s => s.Id).ToListAsync(ct);
        if (outside.Count == 0) return;
        var references = await ReferencesAsync(ct);
        var today = SchoolWeek.Today;
        var now = DateTime.UtcNow;

        foreach (var s in outside)
        {
            var refs = references.TryGetValue(s.Id, out var r) ? r : new Dictionary<string, int>();
            if (s.ArchivedAt is not null)
            {
                report.Archived.Add(new(s.Id, s.FullName, s.ContactNumber, "Already archived", [], refs));
                continue;
            }
            if (refs.Count == 0)
            {
                // Outside both sources and referenced by nothing at all: removed.
                db.StaffMembers.Remove(s);
                await db.SaveChangesAsync(ct);
                report.Archived.Add(new(s.Id, s.FullName, s.ContactNumber, "Removed (unreferenced)", [], refs));
                continue;
            }

            var ended = new List<string>();
            // KG class teaching teams: through the class teaching-team service (its history log).
            foreach (var a in await db.ClassTeachingAssignments.AsNoTracking().Where(a => a.StaffMemberId == s.Id && a.IsActive)
                         .Select(a => new { a.Id, a.SchoolClassId, a.Role, Class = a.SchoolClass.Name, Year = a.AcademicYear.Name }).ToListAsync(ct))
            {
                var removed = await classTeams.RemoveAsync(a.SchoolClassId, a.Id, ct);
                if (removed.Succeeded) ended.Add($"{(a.Role == ClassTeachingRole.HomeroomTeacher ? "Homeroom Teacher" : "Co-Teacher")} of {a.Class} ({a.Year})");
                else report.Failures.Add($"#{s.Id} {s.FullName}: could not end {a.Role} of {a.Class}: {string.Join(" ", removed.Errors.Select(e => e.Value))}");
            }
            foreach (var h in await db.DepartmentHeads.Include(h => h.Department).Where(h => h.TeacherId == s.Id && h.IsCurrent).ToListAsync(ct))
            {
                (h.IsCurrent, h.EndDate, h.EndedAt, h.EndReason) = (false, today < h.StartDate ? h.StartDate : today, now, EndReason);
                ended.Add($"Head of {h.Department.Name}");
            }
            foreach (var p in await db.PositionAssignments.Where(p => p.ManagementMemberId == s.Id && p.IsCurrent).ToListAsync(ct))
            {
                (p.IsCurrent, p.EndDate, p.EndedAt, p.EndReason) = (false, today < p.StartDate ? p.StartDate : today, now, EndReason);
                ended.Add($"Management position {ManagementPositions.Name(p.Position)}");
            }
            if (s is ManagementMember m && (m.Position is not null || m.CustomPositionId is not null))
            {
                if (m.CustomPositionId is not null) ended.Add("Management custom position");
                (m.Position, m.CustomPositionId) = (null, null);
            }
            foreach (var p in await db.StaffPositionAssignments.Include(p => p.Position).Where(p => p.StaffMemberId == s.Id && p.IsCurrent).ToListAsync(ct))
            {
                (p.IsCurrent, p.EndDate, p.EndReason, p.UpdatedAt) = (false, today < p.StartDate ? p.StartDate : today, EndReason, now);
                ended.Add($"Position {p.Position.Title}");
            }
            foreach (var f in await db.FloorStaffAssignments.Include(f => f.Floor).Where(f => f.StaffMemberId == s.Id && f.IsCurrent).ToListAsync(ct))
            {
                (f.IsCurrent, f.EndDate, f.EndReason, f.UpdatedAt) = (false, today < f.StartDate ? f.StartDate : today, EndReason, now);
                ended.Add($"{Words(f.Role.ToString())} — {f.Floor.Name}");
            }
            foreach (var f in await db.FloorAuditAccess.Where(f => f.StaffMemberId == s.Id && f.EndedAt == null).ToListAsync(ct))
            {
                (f.EndedAt, f.EndedBy) = (now, By);
                ended.Add($"Floor Audit access ({f.Role})");
            }
            (s.ArchivedAt, s.ArchiveReason, s.UpdatedAt) = (now, ArchiveReason, now);
            await db.SaveChangesAsync(ct);
            report.Archived.Add(new(s.Id, s.FullName, s.ContactNumber, "Archived (history kept)", ended, refs));
        }
    }

    /// <summary>
    /// Rows referring to each staff member, per table.column: every foreign key to the staff table (all staff kinds) plus the
    /// staff-id columns kept without a foreign key (audit logs, movements).
    /// </summary>
    private async Task<Dictionary<int, Dictionary<string, int>>> ReferencesAsync(CancellationToken ct)
    {
        var columns = new HashSet<(string Table, string Column)>();
        var staffType = db.Model.FindEntityType(typeof(StaffMember))!;
        foreach (var fk in staffType.GetDerivedTypesInclusive().SelectMany(t => t.GetDeclaredReferencingForeignKeys()))
            if (fk.DeclaringEntityType.GetTableName() is { } table)
                columns.Add((table, fk.Properties[0].GetColumnName(StoreObjectIdentifier.Table(table, fk.DeclaringEntityType.GetSchema()))!));
        foreach (var et in db.Model.GetEntityTypes())
        {
            if (et.GetTableName() is not { } table || et.ClrType.IsAssignableTo(typeof(StaffMember))) continue;
            foreach (var p in et.GetProperties().Where(p => (p.ClrType == typeof(int) || p.ClrType == typeof(int?)) && !p.IsPrimaryKey() && !p.IsForeignKey()
                         && (p.Name.EndsWith("StaffId", StringComparison.Ordinal) || p.Name.EndsWith("TeacherId", StringComparison.Ordinal) || p.Name.EndsWith("StaffIdAfter", StringComparison.Ordinal))))
                columns.Add((table, p.GetColumnName(StoreObjectIdentifier.Table(table, et.GetSchema()))!));
        }
        var result = new Dictionary<int, Dictionary<string, int>>();
        foreach (var (table, column) in columns.OrderBy(c => c.Table).ThenBy(c => c.Column))
        {
            var sql = $"SELECT [{column}] AS StaffId, COUNT(*) AS N FROM [{table}] WHERE [{column}] IS NOT NULL GROUP BY [{column}]";
            foreach (var row in await db.Database.SqlQueryRaw<RefCount>(sql).ToListAsync(ct))
            {
                if (!result.TryGetValue(row.StaffId, out var map)) result[row.StaffId] = map = new Dictionary<string, int>();
                map[$"{table}.{column}"] = row.N;
            }
        }
        return result;
    }

    private sealed class RefCount
    {
        public int StaffId { get; set; }
        public int N { get; set; }
    }

    // ------------------------------------------------------------------ 8. stale reviews

    private async Task ResolveStaleReviewsAsync(OfficialStaffReport report, CancellationToken ct)
    {
        var archived = (await db.StaffMembers.Where(s => s.ArchivedAt != null).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        foreach (var item in await db.TeachingRosterReviewItems.Where(i => !i.IsResolved && i.Kind == RosterReviewKind.NameMatchesOtherDepartment).ToListAsync(ct))
        {
            var ids = Regex.Matches(item.ObservedValue ?? "", @"#(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
            if (ids.Count > 0 && ids.All(archived.Contains))
                Resolve(item, $"The other record ({item.ObservedValue}) is not current staff: it is not in the official sources and was archived. Nothing to merge.", report);
        }
        foreach (var flag in await db.StaffDataQualityFlags.Where(f => !f.IsResolved && archived.Contains(f.StaffMemberId)).ToListAsync(ct))
        {
            (flag.IsResolved, flag.ResolvedAt, flag.ResolvedBy) = (true, DateTime.UtcNow, By);
            report.ReviewsResolved.Add($"Flag on archived record #{flag.StaffMemberId}: {flag.Message}");
        }
        await db.SaveChangesAsync(ct);
    }

    private static void Resolve(TeachingRosterReviewItem item, string resolution, OfficialStaffReport report)
    {
        (item.IsResolved, item.ResolvedAt, item.Resolution) = (true, DateTime.UtcNow, Clip(resolution, TeachingRosterReviewItem.ResolutionMaxLength));
        report.ReviewsResolved.Add($"{item.SourceKey}: {resolution}");
    }

    private async Task<bool> AddFlagAsync(int staffId, DataQualityFlagKind kind, string key, string message, CancellationToken ct)
    {
        if (await db.StaffDataQualityFlags.AnyAsync(f => f.SourceKey == key, ct)) return false;
        db.StaffDataQualityFlags.Add(new StaffDataQualityFlag
        {
            SourceKey = key, StaffMemberId = staffId, Kind = kind, CreatedAt = DateTime.UtcNow, Message = Clip(message, StaffDataQualityFlag.MessageMaxLength),
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>"StageCoordinator" → "Stage Coordinator".</summary>
    private static string Words(string pascal) => Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ");

    // ------------------------------------------------------------------ 9. validation

    private async Task ValidateAsync(OfficialStaffReport report, CancellationToken ct)
    {
        void Check(bool ok, string what) => (ok ? report.Checks : report.Failures).Add(what);

        var active = await db.StaffMembers.AsNoTracking().Where(s => s.ArchivedAt == null)
            .Select(s => new { s.Id, s.FullName, s.ContactNumber, s.NormalizedContact }).ToListAsync(ct);
        var activeIds = active.Select(a => a.Id).ToHashSet();
        var extra = active.Where(a => !report.Allowlist.Contains(a.Id)).ToList();
        Check(extra.Count == 0, extra.Count == 0 ? $"Active staff = allowlist ({activeIds.Count} people)"
            : $"Active staff outside the allowlist: {string.Join(", ", extra.Select(e => $"#{e.Id} {e.FullName}"))}");
        var archivedAllowed = report.Allowlist.Where(id => !activeIds.Contains(id)).ToList();
        Check(archivedAllowed.Count == 0, archivedAllowed.Count == 0 ? "No authorized person is archived" : $"Authorized but not active: {string.Join(", ", archivedAllowed)}");

        // Every staff-document person: present, active, phone exactly as the source writes it.
        var missing = new List<string>();
        foreach (var p in OrgStructureSource.People)
        {
            var hit = active.Where(a => a.NormalizedContact == TextNormalizer.PhoneKey(p.Mobile) && OrgStructureSeeder.PersonKey(a.FullName) == OrgStructureSeeder.PersonKey(p.Name)).ToList();
            if (hit.Count != 1 || hit[0].ContactNumber != p.Mobile) missing.Add($"{p.Name} {p.Mobile} (matches: {hit.Count})");
        }
        Check(missing.Count == 0, missing.Count == 0 ? $"All {OrgStructureSource.People.Length} staff-document entries resolve to exactly one active record with the source phone"
            : "Staff-document entries not resolved exactly: " + string.Join("; ", missing));

        var expectedPeople = OrgStructureSource.People.GroupBy(p => (OrgStructureSeeder.PersonKey(p.Name), TextNormalizer.PhoneKey(p.Mobile))).Count();
        var resolvedPeople = report.Seed!.People.Where(p => p.StaffId is not null).Select(p => p.StaffId).Distinct().Count();
        Check(expectedPeople == resolvedPeople, $"Staff-document identities: {expectedPeople} distinct name+phone → {resolvedPeople} staff records");

        var mohamedAli = OrgStructureSource.People.Where(p => p.Name == "Mr. Mohamed Ali").Select(p => report.Seed.People.First(x => x.Key == p.Key).StaffId).Distinct().Count();
        Check(mohamedAli == 2, $"The two Mohamed Ali entries are {mohamedAli} distinct records");
        var shared = active.Count(a => a.NormalizedContact == "01025172837");
        Check(shared == 2, $"01025172837 is held by {shared} separate records (Mahmoud Brakat, Mohamed Abdelrasol)");

        var dupPositions = await db.StaffPositionAssignments.Where(a => a.IsCurrent).GroupBy(a => new { a.StaffMemberId, a.StaffPositionId }).CountAsync(g => g.Count() > 1, ct);
        Check(dupPositions == 0, $"Duplicate current position assignments: {dupPositions}");
        var expectedAssignments = OrgStructureSource.Assignments.Length;
        var current = await db.StaffPositionAssignments.CountAsync(a => a.IsCurrent && a.SourceKey != null && a.SourceKey.StartsWith("asg:"), ct);
        Check(current == expectedAssignments, $"Source position assignments current: {current} of {expectedAssignments}");

        var rows = await db.TeachingRosterEntries.AsNoTracking().Where(e => e.SourceName == TeachingStaff2027.SourceName)
            .Select(e => new { e.RowNumber, e.IsBlank, e.StaffMemberId, e.StaffName }).ToListAsync(ct);
        Check(rows.Select(r => r.RowNumber).OrderBy(n => n).SequenceEqual(Enumerable.Range(1, 64)), $"Teaching Staff 2027 rows recorded: {rows.Count} (1–64 each once)");
        Check(rows.Where(r => r.IsBlank || r.StaffName is null).All(r => r.StaffMemberId is null),
            $"Rows without a name create no staff: {string.Join(", ", rows.Where(r => r.IsBlank || r.StaffName is null).Select(r => r.RowNumber))}");
        Check(rows.Single(r => r.RowNumber == 42).StaffMemberId is null && await db.TeachingRosterReviewItems.AnyAsync(i => i.Entry.RowNumber == 42 && !i.IsResolved, ct),
            "Row 42 stays unlinked with open review items");
        var notActive = rows.Where(r => r.StaffMemberId is { } id && !activeIds.Contains(id)).Select(r => r.RowNumber).ToList();
        Check(notActive.Count == 0, notActive.Count == 0 ? "Every linked roster row points at an active record" : $"Roster rows linked to inactive records: {string.Join(", ", notActive)}");

        var cells = await db.TeachingRosterEntries.AsNoTracking().Where(e => e.SourceName == TeachingStaff2027.SourceName).ToListAsync(ct);
        var cellDiffs = TeachingStaff2027.Rows.Where(r => cells.First(c => c.RowNumber == r.No) is var c
            && (c.StaffName != r.Name || c.Subject != r.Subject || c.GradeLevels != r.Grades || c.NumberOfClasses != r.Classes || c.ص != r.Sad || c.م != r.Meem || c.بريك != r.Break))
            .Select(r => r.No).ToList();
        Check(cellDiffs.Count == 0, cellDiffs.Count == 0 ? "All 64 rows' cells (incl. ص, م, بريك) equal the transcription" : $"Rows whose cells differ from the transcription: {string.Join(", ", cellDiffs)}");

        var homerooms = await db.ClassTeachingAssignments.AsNoTracking().Where(a => a.IsActive && a.Role == ClassTeachingRole.HomeroomTeacher)
            .Select(a => new { Class = a.SchoolClass.Name, a.StaffMember.FullName, Archived = a.StaffMember.ArchivedAt != null }).ToListAsync(ct);
        Check(homerooms.All(h => !h.Archived), $"Current Homeroom Teachers: {string.Join(", ", homerooms.OrderBy(h => h.Class).Select(h => $"{h.Class}={h.FullName}"))}");
        var archivedRoles = await db.ClassTeachingAssignments.CountAsync(a => a.IsActive && a.StaffMember.ArchivedAt != null, ct)
            + await db.DepartmentHeads.CountAsync(h => h.IsCurrent && h.Teacher.ArchivedAt != null, ct)
            + await db.StaffPositionAssignments.CountAsync(a => a.IsCurrent && a.StaffMember.ArchivedAt != null, ct)
            + await db.FloorStaffAssignments.CountAsync(a => a.IsCurrent && a.StaffMember.ArchivedAt != null, ct)
            + await db.ManagementMembers.CountAsync(m => m.ArchivedAt != null && (m.Position != null || m.CustomPositionId != null), ct);
        Check(archivedRoles == 0, $"Current roles held by archived records: {archivedRoles}");
    }
}

/// <summary><c>dotnet run -- reconcile-official-staff</c>: runs <see cref="IOfficialStaffReconciler"/>, prints the report, exits (1 = a check failed).</summary>
public static class OfficialStaffCommand
{
    public const string Name = "reconcile-official-staff";

    public static async Task<int> RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<IOfficialStaffReconciler>().RunAsync();
        var s = r.Seed!;
        Console.WriteLine("Official staff reconciliation");
        Console.WriteLine($"  Staff records: {r.StaffBefore} before ({r.ActiveBefore} current) → {r.StaffAfter} after ({r.ActiveAfter} current); allowlist {r.Allowlist.Count}");
        Console.WriteLine($"  Staff document: tiers +{s.TiersCreated}, units +{s.UnitsCreated}, positions +{s.PositionsCreated}, people created {s.StaffCreated}, reused {s.StaffReused}, " +
                          $"assignments +{s.AssignmentsCreated} (already {s.AssignmentsAlreadyRecorded}), flags +{s.FlagsCreated}");
        foreach (var p in s.People.Where(p => p.Outcome != SeedOutcome.Reused || p.Note?.Contains("filled") == true))
            Console.WriteLine($"    [{p.Outcome}] #{p.StaffId} {p.Name} {p.Mobile}{(p.Note is null ? "" : " — " + p.Note)}");
        foreach (var c in s.Conflicts) Console.WriteLine("    CONFLICT: " + c);
        Console.WriteLine($"  Teaching Staff 2027: rows created {r.Roster.RowsCreated}, rows corrected {r.Roster.RowsCorrected}, department links +{r.Roster.DepartmentLinks}, " +
                          $"reviews +{r.Roster.ReviewItemsCreated} / resolved {r.Roster.ReviewItemsResolved}, homeroom assigned {r.Roster.HomeroomAssigned} (already {r.Roster.HomeroomAlreadyAssigned})");
        foreach (var l in r.Roster.Lines) Console.WriteLine("    " + l);
        foreach (var l in r.RowsLinked) Console.WriteLine("    Linked: " + l);
        foreach (var l in r.Flags) Console.WriteLine("    Review: " + l);
        foreach (var l in r.ReviewsResolved) Console.WriteLine("    Resolved: " + l);
        var changed = r.Archived.Where(a => a.Outcome != "Already archived").ToList();
        Console.WriteLine($"  Outside the sources: {r.Archived.Count} ({changed.Count} archived/removed now)");
        foreach (var a in changed)
            Console.WriteLine($"    #{a.Id} {a.Name}: {a.Outcome}; ended: {(a.EndedRoles.Count == 0 ? "—" : string.Join(", ", a.EndedRoles))}; " +
                              $"refs: {string.Join(", ", a.References.Select(kv => $"{kv.Key}={kv.Value}"))}");
        foreach (var c in r.Checks) Console.WriteLine("  OK   " + c);
        foreach (var f in r.Failures) Console.WriteLine("  FAIL " + f);
        return r.Failures.Count == 0 ? 0 : 1;
    }
}
