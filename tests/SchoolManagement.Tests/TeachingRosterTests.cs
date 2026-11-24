using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.TeachingRoster;

namespace SchoolManagement.Tests;

/// <summary>Teaching Staff 2027 import on the test database: every source row kept exactly, ambiguity flagged, idempotent.</summary>
[Collection("db")]
public class TeachingRosterTests(DbFixture fx)
{
    private sealed class NoUser : ICurrentUser
    {
        public string? Name => null;
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static TeachingRosterImporter Importer(Web.Data.SchoolDbContext db) => new(db, new ClassTeamService(db, new NoUser()));

    [Fact]
    public async Task The_roster_is_recorded_exactly_flagged_where_unsure_and_never_duplicated()
    {
        await using (var db = fx.NewContext()) await Importer(db).ImportAsync();

        await using var check = fx.NewContext();
        var rows = await check.TeachingRosterEntries.Include(e => e.Departments).ThenInclude(d => d.Department).Include(e => e.ReviewItems)
            .Where(e => e.SourceName == TeachingStaff2027.SourceName).OrderBy(e => e.RowNumber).ToListAsync();

        // 1. Every source row number, once.
        Assert.Equal(Enumerable.Range(1, 64), rows.Select(r => r.RowNumber));
        Assert.Equal([31, 49, 51, 55], rows.Where(r => r.IsBlank).Select(r => r.RowNumber));
        Assert.All(rows.Where(r => r.IsBlank), r => Assert.True(r.StaffMemberId is null && r.StaffName is null && r.Departments.Count == 0));

        // 2. Values exactly as written; blank = null, zero = zero; the academic year and source name on every row.
        foreach (var src in TeachingStaff2027.Rows)
        {
            var r = rows.Single(x => x.RowNumber == src.No);
            Assert.Equal((src.Name, src.Subject, src.Grades, src.Classes, src.Sad, src.Meem, src.Break), (r.StaffName, r.Subject, r.GradeLevels, r.NumberOfClasses, r.ص, r.م, r.بريك));
            Assert.Equal("2026/2027", r.AcademicYear);
        }
        var manar = rows.Single(r => r.RowNumber == 27);
        Assert.Equal(0, manar.NumberOfClasses);
        Assert.Null(manar.ص);
        Assert.Equal(5, manar.م);
        Assert.Equal("Pre – G1-2", rows.Single(r => r.RowNumber == 25).GradeLevels);
        Assert.Equal(2, rows.Single(r => r.RowNumber == 1).بريك);

        // 3. Row 42: printed values kept, every handwritten observation a review item, no identity.
        var r42 = rows.Single(r => r.RowNumber == 42);
        Assert.Equal(("Mostafa", "G10", (int?)null), (r42.StaffName, r42.GradeLevels, r42.NumberOfClasses));
        Assert.Null(r42.StaffMemberId);
        Assert.Contains(r42.ReviewItems, i => i.Kind == RosterReviewKind.HandwrittenCorrection && i.Field == "Name" && i.SourceValue == "Mostafa" && i.ObservedValue == "Ali");
        Assert.Contains(r42.ReviewItems, i => i.Kind == RosterReviewKind.HandwrittenCorrection && i.Field == "Grade Levels" && i.SourceValue == "G10" && i.ObservedValue == "G8");
        Assert.Contains(r42.ReviewItems, i => i.Kind == RosterReviewKind.HandwrittenCorrection && i.Field == "Number of Classes" && i.ObservedValue == "28");
        Assert.Contains(r42.ReviewItems, i => i.Kind == RosterReviewKind.IdentityUnresolved);

        // 4. Departments: existing ones only; combined subjects get both; F / G → Second Language; Home Room → none.
        string[] Depts(int no) => rows.Single(r => r.RowNumber == no).Departments.Select(d => d.Department.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Arabic", "Religion"], Depts(40));
        Assert.Equal(["ASS", "ESS"], Depts(34));
        Assert.Equal(["Second Language"], Depts(24));
        Assert.Equal(["Science"], Depts(13));
        Assert.Empty(Depts(60));
        Assert.Contains(rows.Single(r => r.RowNumber == 33).ReviewItems, i => i.Kind == RosterReviewKind.UnmappedSubject && i.ObservedValue == "CBG3");
        Assert.Equal(rows.Single(r => r.RowNumber == 60).Role, RosterRole.HomeroomTeacher);

        // 5. Identity: the two Mohamed Ali rows are never one record; names used twice stay unresolved unless identified.
        var ali = rows.Where(r => r.RowNumber is 2 or 29).Select(r => r.StaffMemberId).ToList();
        Assert.True(ali[0] is null || ali[1] is null || ali[0] != ali[1]);
        foreach (var no in new[] { 38, 53, 52, 61 })
            Assert.True(rows.Single(r => r.RowNumber == no).StaffMemberId is null
                || rows.Single(r => r.RowNumber == no).MatchNote!.StartsWith("Existing", StringComparison.Ordinal));
        // New records carry the source name and invent nothing.
        var newIds = rows.Where(r => r.MatchNote != null && r.MatchNote.StartsWith("New", StringComparison.Ordinal)).Select(r => r.StaffMemberId!.Value).ToList();
        var created = await check.StaffMembers.Where(s => newIds.Contains(s.Id)).ToListAsync();
        Assert.NotEmpty(created);
        Assert.All(created, s => Assert.True(s.ContactNumber is null && s.NormalizedContact == "" && s.JobTitle is null));

        // 6. Idempotent: a second run adds nothing.
        int Count() => check.TeachingRosterEntries.Count() + check.TeachingRosterReviewItems.Count() + check.StaffMembers.Count() + check.ClassTeachingAssignments.Count();
        var before = Count();
        await using (var db = fx.NewContext())
        {
            var again = await Importer(db).ImportAsync();
            Assert.Equal((0, 0, 0, 0, 64), (again.RowsCreated, again.StaffCreated, again.ReviewItemsCreated, again.HomeroomAssigned, again.RowsAlreadyRecorded));
        }
        Assert.Equal(before, Count());
    }
}
