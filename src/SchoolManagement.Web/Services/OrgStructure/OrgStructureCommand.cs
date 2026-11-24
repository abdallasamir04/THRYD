namespace SchoolManagement.Web.Services.OrgStructure;

/// <summary>
/// <c>dotnet run -- seed-org-structure</c>: records the official organizational structure in the configured database
/// (idempotent: only what is missing is created), prints the reconciliation report and exits. Exit code 0 = done,
/// 2 = done with conflicts that need a person's decision.
/// </summary>
public static class OrgStructureCommand
{
    public const string Name = "seed-org-structure";

    public static async Task<int> RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var report = await scope.ServiceProvider.GetRequiredService<IOrgStructureSeeder>().SeedAsync();
        Console.WriteLine("Organizational structure import");
        Console.WriteLine($"  Tiers created:        {report.TiersCreated}");
        Console.WriteLine($"  Units created:        {report.UnitsCreated} (department links: {report.DepartmentLinksCreated}, departments created: {report.DepartmentsCreated})");
        Console.WriteLine($"  Positions created:    {report.PositionsCreated}");
        Console.WriteLine($"  Staff created:        {report.StaffCreated}");
        Console.WriteLine($"  Staff reused:         {report.StaffReused}");
        Console.WriteLine($"  Assignments created:  {report.AssignmentsCreated} (already recorded: {report.AssignmentsAlreadyRecorded})");
        Console.WriteLine($"  Review flags created: {report.FlagsCreated}");
        Console.WriteLine("People:");
        foreach (var p in report.People)
            Console.WriteLine($"  [{p.Outcome,-8}] #{p.StaffId?.ToString() ?? "-",-5} {p.Name,-28} {p.Mobile}{(p.Note is null ? "" : "  — " + p.Note)}");
        foreach (var n in report.Notes) Console.WriteLine("Note: " + n);
        foreach (var c in report.Conflicts) Console.WriteLine("CONFLICT: " + c);
        return report.Conflicts.Count > 0 ? 2 : 0;
    }
}
