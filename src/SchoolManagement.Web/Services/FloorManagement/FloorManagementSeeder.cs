using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.OrgStructure;

namespace SchoolManagement.Web.Services.FloorManagement;

/// <summary>
/// The school's floor teams for 2026/2027, as given by the Royal Kings floor-management charts (names, positions, classes,
/// phone numbers exactly as written). An authorized source for the floor staff. The Social Workers are not in the source:
/// none is created. <see cref="Person.Unconfirmed"/>: the person's identity is not confirmed (no record is matched,
/// created or assigned; the floor shows the role as pending until an administrator assigns someone).
/// </summary>
public static class FloorManagementSource
{
    public sealed record Person(string Key, string Name, FloorRole Role, string Phone, string? Phone2 = null, string? Coverage = null, string[]? Classes = null,
        string? Unconfirmed = null);
    public sealed record Floor(string Name, string GradeRange, int Sort, Person[] Team);

    public static readonly Floor[] Floors =
    [
        new("Ground Floor", "PreK – G1B", 0,
        [
            // Same number as the official staff member Ms. Sara Mostafa (01096247514), but not confirmed to be her (decision 2026-11-24).
            new("sarah-mostafa", "Mrs. Sarah Mostafa", FloorRole.StageCoordinator, "201096247514", Coverage: "PreK – G1B",
                Unconfirmed: "Same phone number as Ms. Sara Mostafa (Bookstore & Student Distribution Coordinator); not confirmed to be the same person."),
            new("donia-mostafa", "Mrs. Donia Mostafa", FloorRole.FloorSupervisor, "201145309275", Coverage: "PreK – G1B"),
            new("israa-farid", "Ms. Israa Farid", FloorRole.Matron, "201278997659", Classes: ["PreK", "KG1A", "KG1B", "KG2A"]),
            new("aliaa-abo-raya", "Ms. Aliaa Abo Raya", FloorRole.Matron, "201228133625", Classes: ["KG2B", "G1A", "G1B"]),
            new("mona-nabil", "Ms. Mona Nabil", FloorRole.FloatingMatron, "201152915781", Coverage: "PreK – G1B"),
        ]),
        new("1st Floor", "G2A – G6B", 1,
        [
            new("nour-tarek", "Mr. Nour Tarek", FloorRole.StageCoordinator, "201020492629", Coverage: "G2A – G6B"),
            new("ashraf-soultan", "Mr. Ashraf Soultan", FloorRole.FloorSupervisor, "201102277491", Coverage: "G2A – G6B"),
            new("aya-abdelmoneam", "Ms. Aya Abdelmoneam", FloorRole.Matron, "201125542703", Classes: ["G2A", "G2B", "G3A"]),
            new("habiba-elsayed", "Ms. Habiba Elsayed", FloorRole.Matron, "201128908921", Classes: ["G3B", "G4A", "G4B"]),
            new("rehab-taha", "Ms. Rehab Taha", FloorRole.Matron, "201004688133", Classes: ["G5A", "G5B", "G6A", "G6B"]),
            new("hoda", "Ms. Hoda", FloorRole.FloatingMatron, "201146761018", Coverage: "G2A – G6B"),
        ]),
        new("2nd Floor", "G7A – G10B", 2,
        [
            new("passant-mamdouh", "Mrs. Passant Mamdouh", FloorRole.StageCoordinator, "201000300977", Coverage: "G7A – G10B"),
            new("mohamed-mostafa", "Mr. Mohamed Mostafa", FloorRole.FloorSupervisor, "201108387800", Coverage: "G7A – G10B"),
            new("hagar", "Ms. Hagar", FloorRole.Matron, "201119373120", Classes: ["G7A", "G7B", "G8A", "G8B"]),
            new("mayada", "Ms. Mayada", FloorRole.Matron, "201129118034", Classes: ["G9A", "G9B", "G10A", "G10B"]),
            new("farida", "Ms. Farida", FloorRole.FloatingMatron, "201151725779", Phone2: "201200110557", Coverage: "G7A – G10B"),
        ]),
    ];
}

public sealed class FloorSeedReport
{
    public List<string> Lines { get; } = [];
    public int FloorsUpdated { get; set; }
    public int FloorsCreated { get; set; }
    public int StaffReused { get; set; }
    public int StaffCreated { get; set; }
    public int AssignmentsCreated { get; set; }
    public int AlreadyAssigned { get; set; }
    public int Flagged { get; set; }
}

public interface IFloorManagementSeeder { Task<FloorSeedReport> SeedAsync(CancellationToken ct = default); }

/// <summary>
/// Records the floor teams (idempotent). Floors are found by name (existing floors keep their id, classrooms and
/// inspections; only a missing grade range is filled). A person is the existing staff record with the same phone number
/// (the source's 20… and the stored 01… are the same Egyptian number) <b>and</b> a compatible name (same surname, first
/// name spelled alike: Sarah/Sara, Soultan/Sultan, Passant/Bassant). The same number with an unrelated name, or the same
/// name with another number, is flagged and nothing is assigned. Existing phone numbers and roles are never changed.
/// </summary>
public sealed class FloorManagementSeeder(SchoolDbContext db) : IFloorManagementSeeder
{
    public const string KeyPrefix = "floor-mgmt-2027";

    public async Task<FloorSeedReport> SeedAsync(CancellationToken ct = default)
    {
        var report = new FloorSeedReport();
        var staff = await db.StaffMembers.AsNoTracking().Where(s => s.ArchivedAt == null).Select(s => new { s.Id, s.FullName, s.NormalizedContact }).ToListAsync(ct);
        var classes = await db.Classes.AsNoTracking().Select(c => new { c.Id, c.NormalizedName }).ToDictionaryAsync(c => c.NormalizedName, c => c.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var src in FloorManagementSource.Floors)
        {
            var key = TextNormalizer.NameKey(src.Name);
            var floor = await db.Floors.FirstOrDefaultAsync(f => f.NormalizedName == key, ct);
            if (floor is null)
            {
                floor = new SchoolFloor { Name = src.Name, NormalizedName = key, SortOrder = src.Sort, GradeRange = src.GradeRange, CreatedBy = "Floor Management setup" };
                db.Floors.Add(floor);
                report.FloorsCreated++;
            }
            else if (floor.GradeRange is null) { floor.GradeRange = src.GradeRange; report.FloorsUpdated++; }
            await db.SaveChangesAsync(ct);

            foreach (var p in src.Team)
            {
                var assignmentKey = $"{KeyPrefix}:{floor.Id}:{p.Role}:{p.Key}";
                if (await db.FloorStaffAssignments.AnyAsync(a => a.SourceKey == assignmentKey, ct)) { report.AlreadyAssigned++; continue; }

                var phone = Canonical(p.Phone);
                if (p.Unconfirmed is not null)
                {
                    // Never matched, created or assigned while unconfirmed; the record sharing the number is flagged.
                    if (staff.FirstOrDefault(s => Canonical(s.NormalizedContact) == phone && phone.Length > 0) is { } holder)
                        await FlagAsync(holder.Id, $"{KeyPrefix}:identity:{p.Key}",
                            $"The Floor Management chart names {p.Name} ({p.Phone}) as {Label(p.Role)} of the {src.Name}. {p.Unconfirmed} The role stays pending until an administrator confirms who she is.", ct);
                    report.Flagged++;
                    report.Lines.Add($"PENDING {p.Name} ({Label(p.Role)}, {src.Name}): {p.Unconfirmed}");
                    continue;
                }
                var person = OrgStructureSeeder.PersonKey(p.Name);
                var byPhone = staff.Where(s => Canonical(s.NormalizedContact) == phone && phone.Length > 0).ToList();
                var match = byPhone.Where(s => Compatible(OrgStructureSeeder.PersonKey(s.FullName), person)).ToList();
                int staffId;
                if (match.Count == 1)
                {
                    staffId = match[0].Id;
                    report.StaffReused++;
                    report.Lines.Add($"Reused #{staffId} {match[0].FullName} for {p.Name} ({Label(p.Role)}, {src.Name}): same phone, compatible name.");
                }
                else if (match.Count > 1 || byPhone.Count > 0)
                {
                    await FlagAsync(byPhone[0].Id, $"{KeyPrefix}:identity:{p.Key}",
                        $"Floor Management source lists {p.Name} ({p.Phone}) as {Label(p.Role)}, {src.Name}. The phone number belongs to this record but the name does not match: verify. No floor role was assigned.", ct);
                    report.Flagged++;
                    report.Lines.Add($"FLAGGED {p.Name}: phone {p.Phone} belongs to {string.Join(", ", byPhone.Select(s => $"#{s.Id} {s.FullName}"))} (names differ). Not assigned.");
                    continue;
                }
                else if (staff.Where(s => OrgStructureSeeder.PersonKey(s.FullName) == person).ToList() is { Count: > 0 } sameName)
                {
                    await FlagAsync(sameName[0].Id, $"{KeyPrefix}:identity:{p.Key}",
                        $"Floor Management source lists {p.Name} ({p.Phone}) as {Label(p.Role)}, {src.Name}; this record has the same name but another phone number. Verify; no floor role was assigned.", ct);
                    report.Flagged++;
                    report.Lines.Add($"FLAGGED {p.Name}: same name as #{sameName[0].Id} with a different phone. Not assigned.");
                    continue;
                }
                else
                {
                    var created = new Teacher
                    {
                        FullName = p.Name, NormalizedName = TextNormalizer.NameKey(p.Name), ContactNumber = p.Phone, NormalizedContact = TextNormalizer.PhoneKey(p.Phone),
                        SecondaryContactNumber = p.Phone2, JobTitle = Label(p.Role),
                    };
                    db.StaffMembers.Add(created);
                    await db.SaveChangesAsync(ct);
                    staff.Add(new { created.Id, created.FullName, created.NormalizedContact });
                    staffId = created.Id;
                    report.StaffCreated++;
                    report.Lines.Add($"Created #{staffId} {p.Name} ({Label(p.Role)}, {src.Name}){(p.Phone2 is null ? "" : $", second phone {p.Phone2}")}.");
                }

                if (await db.FloorStaffAssignments.AnyAsync(a => a.FloorId == floor.Id && a.Role == p.Role && a.StaffMemberId == staffId && a.IsCurrent, ct))
                { report.AlreadyAssigned++; continue; }
                if (p.Role is FloorRole.StageCoordinator or FloorRole.FloorSupervisor
                    && await db.FloorStaffAssignments.AnyAsync(a => a.FloorId == floor.Id && a.Role == p.Role && a.IsCurrent, ct))
                {
                    report.Lines.Add($"Not assigned {p.Name}: {src.Name} already has a current {Label(p.Role)} (not replaced).");
                    continue;
                }
                var row = new FloorStaffAssignment
                {
                    SourceKey = assignmentKey, FloorId = floor.Id, StaffMemberId = staffId, Role = p.Role, Coverage = p.Coverage,
                    StartDate = SchoolWeek.Today, AssignedBy = "Floor Management setup (2026/2027)",
                };
                foreach (var c in p.Classes ?? [])
                    if (classes.TryGetValue(TextNormalizer.ClassKey(c), out var cid)) row.Classes.Add(new FloorStaffClass { SchoolClassId = cid });
                    else report.Lines.Add($"Class {c} (for {p.Name}) does not exist; not linked.");
                db.FloorStaffAssignments.Add(row);
                await db.SaveChangesAsync(ct);
                report.AssignmentsCreated++;
            }
        }
        await tx.CommitAsync(ct);
        return report;
    }

    private async Task FlagAsync(int staffId, string key, string message, CancellationToken ct)
    {
        if (await db.StaffDataQualityFlags.AnyAsync(f => f.SourceKey == key, ct)) return;
        db.StaffDataQualityFlags.Add(new StaffDataQualityFlag { SourceKey = key, StaffMemberId = staffId, Kind = DataQualityFlagKind.IdentityReview, Message = message, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>An Egyptian mobile in one form: 2010… / 002010… / 010… → 010….</summary>
    public static string Canonical(string? phone)
    {
        var d = TextNormalizer.PhoneKey(phone);
        if (d.StartsWith("0020", StringComparison.Ordinal)) d = d[4..];
        else if (d.StartsWith("20", StringComparison.Ordinal) && d.Length == 12) d = d[2..];
        if (d.Length == 10 && d[0] == '1') d = "0" + d;
        return d;
    }

    /// <summary>Same surname and a first name spelled alike (equal, a prefix, or one or two letters apart), or a one-word name.</summary>
    public static bool Compatible(string a, string b)
    {
        var x = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var y = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x.Length > 1 && y.Length > 1 && Distance(x[^1], y[^1]) > 2) return false;
        return x[0] == y[0] || x[0].StartsWith(y[0], StringComparison.Ordinal) || y[0].StartsWith(x[0], StringComparison.Ordinal) || Distance(x[0], y[0]) <= 1;
    }

    private static int Distance(string s, string t)
    {
        var d = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;
        for (var i = 1; i <= s.Length; i++)
            for (var j = 1; j <= t.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
        return d[s.Length, t.Length];
    }

    private static string Label(FloorRole r) => FloorManagementService.Label(r);
}
