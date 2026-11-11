using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Import;

namespace SchoolManagement.Tests;

/// <summary>
/// A real SQL Server test database (SchoolManagementDb_Tests), deleted and re-created by the app's
/// own startup migration at the start of each test run. The development database is never touched.
/// </summary>
public sealed class DbFixture : IAsyncLifetime
{
    public const string ConnectionString =
        "Server=.;Database=SchoolManagementDb_Tests;Trusted_Connection=True;TrustServerCertificate=True";

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__SchoolDb", ConnectionString);
        await using (var db = NewContext()) await db.Database.EnsureDeletedAsync();
        Factory = new WebApplicationFactory<Program>();
        _ = Factory.Server; // starts the app: applies both migrations to the empty test database
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();

    public SchoolDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlServer(ConnectionString).Options);

    public static WorkloadService Workload(SchoolDbContext db) => new(db, new NoUser(), NullLogger<WorkloadService>.Instance);
    public static DutyService Duties(SchoolDbContext db) => new(db, new NoUser());
    public static TeacherService Teachers(SchoolDbContext db) => new(db);
    public static StudentService Students(SchoolDbContext db) => new(db);
    public static ClassService Classes(SchoolDbContext db) => new(db);
    public static ImportService Import(SchoolDbContext db, IMemoryCache cache) => new(db, cache, NullLogger<ImportService>.Instance);
    public static SettingsService Settings(SchoolDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));
    public static StaffDutyService StaffDuty(SchoolDbContext db) => new(db, new NoUser());
    public static ArrivalService Arrivals(SchoolDbContext db) => new(db, Settings(db), new NoUser());
    public static TimetableService Timetable(SchoolDbContext db) => new(db, Workload(db), new NoUser());
    public static DepartmentService Departments(SchoolDbContext db) => new(db, new NoUser());
    public static ManagementService Management(SchoolDbContext db) => new(db, new NoUser());

    /// <summary>Creates a student with a unique name in the given class.</summary>
    public async Task<(int Id, string Name)> NewStudentAsync(int classId)
    {
        await using var db = NewContext();
        var name = "Student " + Unique();
        var result = await Students(db).CreateAsync(name, classId);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Value)));
        return (result.EntityId!.Value, name);
    }

    /// <summary>Creates a new active class (so a test can count exactly its own students).</summary>
    public async Task<int> NewClassAsync()
    {
        await using var db = NewContext();
        var result = await Classes(db).CreateAsync("T" + Unique()[..8], null, true);
        Assert.True(result.Succeeded);
        return result.EntityId!.Value;
    }

    /// <summary>Creates a teacher with a unique name and returns (id, name).</summary>
    public async Task<(int Id, string Name)> NewTeacherAsync(string? jobTitle = "Test Teacher")
    {
        await using var db = NewContext();
        var name = "Teacher " + Unique();
        var result = await Teachers(db).CreateAsync(name, jobTitle, null);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Value)));
        return (result.EntityId!.Value, name);
    }

    /// <summary>A unique, letters-only suffix (names must contain letters; digits are fine too).</summary>
    public static string Unique() => new string(Guid.NewGuid().ToString("N").Take(10).Select(c => char.IsDigit(c) ? (char)('g' + (c - '0')) : c).ToArray());

    private sealed class NoUser : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<DbFixture>;
