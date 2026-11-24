namespace SchoolManagement.Web.Services.TeachingRoster;

/// <summary><c>dotnet run -- import-teaching-staff-2027</c>: records the roster in the configured database, prints the report, exits.</summary>
public static class TeachingRosterCommand
{
    public const string Name = "import-teaching-staff-2027";

    public static async Task<int> RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<ITeachingRosterImporter>().ImportAsync();
        Console.WriteLine($"{TeachingStaff2027.SourceName} import");
        Console.WriteLine($"  Source rows recorded:      {r.RowsCreated} (already recorded: {r.RowsAlreadyRecorded}; blank rows in source: {r.BlankRows})");
        Console.WriteLine($"  Existing staff reused:     {r.StaffReused}");
        Console.WriteLine($"  New staff records:         {r.StaffCreated}");
        Console.WriteLine($"  Identity unresolved:       {r.Unresolved}");
        Console.WriteLine($"  Department links:          {r.DepartmentLinks}");
        Console.WriteLine($"  Homeroom assigned:         {r.HomeroomAssigned} (already assigned: {r.HomeroomAlreadyAssigned})");
        Console.WriteLine($"  Review items created:      {r.ReviewItemsCreated}");
        return 0;
    }
}
