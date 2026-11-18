namespace SchoolManagement.Web.Services;

/// <summary>
/// Stores the automatic staff absences without anyone opening a page: every minute, each school day past its cutoff (from
/// the first automatic day) gets an Absent record for every expected staff member with no record. Idempotent: existing
/// records are never changed and nobody is recorded twice. Off with StaffAttendance:AutoFinalize=false (and in tests).
/// </summary>
public sealed class StaffAttendanceFinalizer(IServiceScopeFactory scopes, ILogger<StaffAttendanceFinalizer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var created = await scope.ServiceProvider.GetRequiredService<IStaffAttendanceService>().FinalizeDueAsync(stoppingToken);
                if (created > 0) logger.LogInformation("Recorded {Count} automatic staff absences (no attendance by the cutoff).", created);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Automatic staff absences could not be recorded; retrying in a minute."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
