using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Pure rules: no database.</summary>
public class RulesTests
{
    [Theory]
    [InlineData(0, WorkloadStatus.Available)]
    [InlineData(19, WorkloadStatus.Available)]
    [InlineData(20, WorkloadStatus.NearCapacity)]
    [InlineData(23, WorkloadStatus.NearCapacity)]
    [InlineData(24, WorkloadStatus.Full)]
    [InlineData(25, WorkloadStatus.OverLimit)]
    public void Workload_status_bands(int total, WorkloadStatus expected) =>
        Assert.Equal(expected, WorkloadRules.StatusOf(total));

    [Fact]
    public void Breakdown_counts_each_category_once()
    {
        var b = WorkloadBreakdown.From([
            (WorkloadCategory.Class, 10), (WorkloadCategory.Class, 2), (WorkloadCategory.Substitution, 4),
            (WorkloadCategory.Duty, 5), (WorkloadCategory.Office, 3)]);
        Assert.Equal(12, b.Class);
        Assert.Equal(4, b.Substitution);
        Assert.Equal(5, b.Duty);
        Assert.Equal(3, b.Office);
        Assert.Equal(24, b.Total);
    }

    [Theory]
    [InlineData("2026-11-08", "2026-11-08")] // Sunday
    [InlineData("2026-11-12", "2026-11-08")] // Thursday
    [InlineData("2026-11-14", "2026-11-08")] // Saturday
    [InlineData("2026-11-15", "2026-11-15")] // next Sunday
    public void School_week_starts_on_sunday(string day, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), SchoolWeek.StartOf(DateOnly.Parse(day)));

    [Fact]
    public void Coverage_reflects_attendance_not_selection_or_acceptance()
    {
        Assert.Equal(CoverageState.AttendanceNotRecorded, Coverage.Of(AttendanceStatus.NotRecorded, null, BackupRequestStatus.None, AttendanceStatus.NotRecorded));
        Assert.Equal(CoverageState.CoveredByAssigned, Coverage.Of(AttendanceStatus.Present, null, BackupRequestStatus.None, AttendanceStatus.NotRecorded));
        Assert.Equal(CoverageState.NeedsBackup, Coverage.Of(AttendanceStatus.Absent, null, BackupRequestStatus.None, AttendanceStatus.NotRecorded));
        Assert.Equal(CoverageState.AwaitingBackupResponse, Coverage.Of(AttendanceStatus.Absent, 5, BackupRequestStatus.Pending, AttendanceStatus.NotRecorded));
        Assert.Equal(CoverageState.BackupRejected, Coverage.Of(AttendanceStatus.Absent, 5, BackupRequestStatus.Rejected, AttendanceStatus.NotRecorded));
        Assert.Equal(CoverageState.AwaitingBackupAttendance, Coverage.Of(AttendanceStatus.Absent, 5, BackupRequestStatus.Accepted, AttendanceStatus.NotRecorded));
        Assert.Equal(CoverageState.CoveredByBackup, Coverage.Of(AttendanceStatus.Absent, 5, BackupRequestStatus.Accepted, AttendanceStatus.Present));
        Assert.Equal(CoverageState.BackupAbsent, Coverage.Of(AttendanceStatus.Absent, 5, BackupRequestStatus.Accepted, AttendanceStatus.Absent));

        Assert.False(Coverage.IsCovered(CoverageState.AwaitingBackupResponse));
        Assert.False(Coverage.IsCovered(CoverageState.AwaitingBackupAttendance));
    }

    [Fact]
    public void Morning_duty_has_exactly_three_components()
    {
        Assert.Equal(3, DutyComponents.Morning.Count);
        Assert.Equal([DutyComponent.StudentsArrival, DutyComponent.InClassMorning, DutyComponent.MorningAssembly],
            DutyComponents.Morning.Select(m => m.Component));
        Assert.True(DutyComponents.Get(DutyComponent.InClassMorning).HasClasses);
        Assert.True(DutyComponents.Get(DutyComponent.MorningAssembly).DetailsPending);
    }

    [Theory]
    [InlineData("kg 1-a", "KG1A")]
    [InlineData("Grade 3B", "G3B")]
    [InlineData("Pre-K", "PREK")]
    public void Class_keys_ignore_spacing_case_and_separators(string input, string key) =>
        Assert.Equal(key, TextNormalizer.ClassKey(input));

    [Fact]
    public void Arabic_name_variants_match()
    {
        Assert.Equal(TextNormalizer.NameKey("أحمد  عليّ"), TextNormalizer.NameKey("احمد علي"));
        Assert.Equal("01001234567", TextNormalizer.PhoneKey("٠١٠ ٠١٢٣ ٤٥٦٧"));
        Assert.True(TextNormalizer.IsValidPhone("+20 100 123 4567"));
        Assert.False(TextNormalizer.IsValidPhone("12ab"));
    }
}
