using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Morning duty workflow against SQL Server.</summary>
[Collection("db")]
public class DutyTests(DbFixture fx)
{
    private static readonly DateOnly Day = new(2030, 2, 3);

    private static DutyInput Input(DutyComponent component, int primary, AttendanceStatus attendance = AttendanceStatus.NotRecorded,
        int? backup = null, IReadOnlyList<int>? classes = null) =>
        new(component, Day, "Gate supervision", new TimeOnly(7, 15), new TimeOnly(8, 0), "Main gate",
            primary, attendance, backup, null, classes ?? []);

    private async Task<DutyRow> GetAsync(int id)
    {
        await using var db = fx.NewContext();
        return (await DbFixture.Duties(db).GetAsync(id))!;
    }

    [Fact]
    public async Task Students_arrival_duty_is_covered_when_the_assigned_staff_is_present()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        var created = await duties.CreateAsync(Input(DutyComponent.StudentsArrival, primary));
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Value)));
        var id = created.EntityId!.Value;
        Assert.Equal(CoverageState.AttendanceNotRecorded, (await GetAsync(id)).Coverage);

        Assert.True((await duties.SetPrimaryAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);
        Assert.Equal(CoverageState.CoveredByAssigned, (await GetAsync(id)).Coverage);
    }

    [Fact]
    public async Task Absent_staff_requires_a_different_backup()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);

        var noBackup = await duties.CreateAsync(Input(DutyComponent.StudentsArrival, primary, AttendanceStatus.Absent));
        Assert.False(noBackup.Succeeded);
        Assert.Contains(noBackup.Errors, e => e.Key == "BackupStaffId");

        var same = await duties.CreateAsync(Input(DutyComponent.StudentsArrival, primary, AttendanceStatus.Absent, backup: primary));
        Assert.False(same.Succeeded);
        Assert.Contains(same.Errors, e => e.Value.Contains("different person"));

        // Quick "absent" without a backup is refused too.
        var created = await duties.CreateAsync(Input(DutyComponent.StudentsArrival, primary));
        Assert.False((await duties.SetPrimaryAttendanceAsync(created.EntityId!.Value, AttendanceStatus.Absent)).Succeeded);
    }

    [Fact]
    public async Task Backup_accepting_is_not_attendance_and_not_coverage()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var (backup, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        var id = (await duties.CreateAsync(Input(DutyComponent.StudentsArrival, primary, AttendanceStatus.Absent, backup))).EntityId!.Value;

        var d = await GetAsync(id);
        Assert.Equal(BackupRequestStatus.Pending, d.BackupStatus);
        Assert.Equal(CoverageState.AwaitingBackupResponse, d.Coverage);

        // Attendance cannot be recorded before the backup accepts.
        Assert.False((await duties.SetBackupAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);

        Assert.True((await duties.RecordBackupResponseAsync(id, accepted: true, "Confirmed by phone")).Succeeded);
        d = await GetAsync(id);
        Assert.Equal(BackupRequestStatus.Accepted, d.BackupStatus);
        Assert.Equal(AttendanceStatus.NotRecorded, d.BackupAttendance); // accepting ≠ present
        Assert.False(Coverage.IsCovered(d.Coverage));

        Assert.True((await duties.SetBackupAttendanceAsync(id, AttendanceStatus.Absent)).Succeeded);
        Assert.Equal(CoverageState.BackupAbsent, (await GetAsync(id)).Coverage);

        Assert.True((await duties.SetBackupAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);
        Assert.Equal(CoverageState.CoveredByBackup, (await GetAsync(id)).Coverage);

        // A second answer is refused.
        Assert.False((await duties.RecordBackupResponseAsync(id, accepted: false, null)).Succeeded);

        var history = await duties.BackupHistoryAsync(id);
        Assert.Equal([BackupResponseKind.Accepted, BackupResponseKind.Requested], history.Select(h => h.Response));
    }

    [Fact]
    public async Task Declined_backup_leaves_the_duty_uncovered_until_another_backup_is_chosen()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var (first, _) = await fx.NewTeacherAsync();
        var (second, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        var id = (await duties.CreateAsync(Input(DutyComponent.StudentsArrival, primary, AttendanceStatus.Absent, first))).EntityId!.Value;

        Assert.True((await duties.RecordBackupResponseAsync(id, accepted: false, "Teaching P1")).Succeeded);
        Assert.Equal(CoverageState.BackupRejected, (await GetAsync(id)).Coverage);

        Assert.True((await duties.UpdateAsync(id, Input(DutyComponent.StudentsArrival, primary, AttendanceStatus.Absent, second))).Succeeded);
        var d = await GetAsync(id);
        Assert.Equal(second, d.BackupStaffId);
        Assert.Equal(BackupRequestStatus.Pending, d.BackupStatus);
        Assert.Equal(CoverageState.AwaitingBackupResponse, d.Coverage);
    }

    [Fact]
    public async Task In_class_morning_needs_classes_from_the_classes_table()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);

        Assert.False((await duties.CreateAsync(Input(DutyComponent.InClassMorning, primary))).Succeeded);
        Assert.False((await duties.CreateAsync(Input(DutyComponent.InClassMorning, primary, classes: [99999]))).Succeeded);

        var ok = await duties.CreateAsync(Input(DutyComponent.InClassMorning, primary, AttendanceStatus.Present, classes: [6, 7]));
        Assert.True(ok.Succeeded);
        var d = await GetAsync(ok.EntityId!.Value);
        Assert.Equal(["G1A", "G1B"], d.ClassNames);
        Assert.Equal(CoverageState.CoveredByAssigned, d.Coverage);

        // The same backup workflow applies.
        var (backup, _) = await fx.NewTeacherAsync();
        Assert.True((await duties.UpdateAsync(d.Id, Input(DutyComponent.InClassMorning, primary, AttendanceStatus.Absent, backup, [6]))).Succeeded);
        Assert.Equal(CoverageState.AwaitingBackupResponse, (await GetAsync(d.Id)).Coverage);
    }

    [Fact]
    public async Task Morning_assembly_records_the_general_fields()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var r = await DbFixture.Duties(db).CreateAsync(Input(DutyComponent.MorningAssembly, primary));
        Assert.True(r.Succeeded);
        Assert.Empty((await GetAsync(r.EntityId!.Value)).ClassNames);
    }

    [Fact]
    public async Task Duty_periods_link_to_a_duty_once_and_count_as_duty()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var dutyId = (await DbFixture.Duties(db).CreateAsync(Input(DutyComponent.StudentsArrival, primary))).EntityId!.Value;
        var workload = DbFixture.Workload(db);
        var input = new WorkloadInput(primary, Day, WorkloadCategory.Duty, 1, null, dutyId, null);

        Assert.True((await workload.CreateAsync(input)).Succeeded);
        Assert.False((await workload.CreateAsync(input)).Succeeded); // same duty twice
        var s = (await workload.SummaryAsync(primary, Day))!;
        Assert.Equal((1, 1), (s.Breakdown.Duty, s.Total));

        // A teacher who is not on the duty cannot have it counted.
        var (other, _) = await fx.NewTeacherAsync();
        Assert.False((await workload.CreateAsync(input with { TeacherId = other })).Succeeded);

        // The duty cannot be deleted while it is counted in a workload.
        Assert.False((await DbFixture.Duties(db).DeleteAsync(dutyId)).Succeeded);
    }
}
