using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.Data.TestData;

/// <summary>
/// Command line (Development only, against the configured database):
/// <c>dotnet run -- seed-test-data</c>, <c>dotnet run -- test-data-status</c>, <c>dotnet run -- remove-test-data</c>.
/// The app runs the command and exits; a normal start (no command) is unchanged.
/// </summary>
public static class TestDataCommand
{
    public const string Seed = "seed-test-data";
    public const string Status = "test-data-status";
    public const string Remove = "remove-test-data";

    /// <summary>The command, or null when the app is started normally.</summary>
    public static string? Parse(string[] args) => args.Length > 0 && args[0] is Seed or Status or Remove ? args[0] : null;

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<TestDataTracker>();
        services.AddSingleton<TestDataUser>();
        services.AddSingleton<ICurrentUser>(sp => sp.GetRequiredService<TestDataUser>());
    }

    public static async Task<int> RunAsync(WebApplication app, string command)
    {
        var log = Console.Out;
        if (!app.Environment.IsDevelopment())
        {
            log.WriteLine("Test data commands run only in the Development environment. Nothing was changed.");
            return 2;
        }
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        var database = db.Database.GetDbConnection().Database;
        return command switch
        {
            Seed => await SeedAsync(app, db, database, log),
            Remove => await RemoveAsync(db, database, log),
            _ => await PrintStatusAsync(db, database, log),
        };
    }

    private static async Task<int> SeedAsync(WebApplication app, SchoolDbContext db, string database, TextWriter log)
    {
        await TestDataTracker.EnsureTableAsync(db);
        log.WriteLine($"Adding test data to {database} (existing records are kept and never changed) …");
        var started = DateTime.Now;
        var tracker = app.Services.GetRequiredService<TestDataTracker>();
        var seeder = new TestDataSeeder(app.Services, tracker, app.Services.GetRequiredService<TestDataUser>(), log);
        try
        {
            await seeder.RunAsync();
        }
        catch (Exception ex)
        {
            log.WriteLine("Seeding stopped: " + ex.GetBaseException().Message);
            log.WriteLine($"{tracker.Added} rows were added in this run and are recorded as test data. Run \"{Seed}\" again to continue");
            log.WriteLine($"(the unfinished step is redone cleanly), or \"{Remove}\" to remove all test data.");
            return 1;
        }
        log.WriteLine(tracker.Added == 0
            ? "Nothing to add: all test data is already in place."
            : $"Done in {(DateTime.Now - started).TotalSeconds:0} s: {tracker.Added} rows added.");
        foreach (var note in seeder.Notes) log.WriteLine("  note: " + note);
        foreach (var s in seeder.Skipped.Take(15)) log.WriteLine("  skipped: " + s);
        if (seeder.Skipped.Count > 15) log.WriteLine($"  … {seeder.Skipped.Count - 15} more skipped");
        await PrintStatusAsync(db, database, log);
        return 0;
    }

    private static async Task<int> PrintStatusAsync(SchoolDbContext db, string database, TextWriter log)
    {
        if (!await TestDataTracker.TableExistsAsync(db))
        {
            log.WriteLine($"{database}: no test data.");
            return 0;
        }
        var rows = await TestDataTracker.ReadAsync(db);
        var generated = rows.Where(r => r.Kind == "A").ToList();
        var stages = rows.Where(r => r.Kind == "P").Select(r => r.Phase).ToList();
        log.WriteLine($"{database}: {generated.Count} generated test-data rows; finished steps: {(stages.Count == 0 ? "none" : string.Join(", ", stages))}");
        foreach (var g in generated.GroupBy(r => db.Model.FindEntityType(r.EntityType)?.GetTableName() ?? r.EntityType).OrderBy(g => g.Key))
        {
            var sql = $"SELECT COUNT(*) AS Value FROM [{g.Key}]";   // table name from the EF model
            var total = await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
            log.WriteLine($"  {g.Key,-34} {g.Count(),6} generated   {total - g.Count(),6} other");
        }
        return 0;
    }

    private static async Task<int> RemoveAsync(SchoolDbContext db, string database, TextWriter log)
    {
        if (!await TestDataTracker.TableExistsAsync(db))
        {
            log.WriteLine($"{database}: no test data recorded. Nothing was changed.");
            return 0;
        }
        var rows = await TestDataTracker.ReadAsync(db);
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var deleted = await TestDataTracker.DeleteGeneratedAsync(db, rows.Where(r => r.Kind == "A").ToList(), rows.Where(r => r.Kind == "B").ToList());
            await db.Database.ExecuteSqlRawAsync($"DROP TABLE dbo.{TestDataTracker.Table}");
            await tx.CommitAsync();
            log.WriteLine($"{database}: removed {deleted} generated test-data rows. Records that were not generated were not touched.");
            return 0;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            log.WriteLine(ex.GetBaseException().Message);
            log.WriteLine("Nothing was removed.");
            return 1;
        }
    }
}
