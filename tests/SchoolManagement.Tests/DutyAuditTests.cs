using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Duty statuses, separate primary/backup assignments, evaluations and the workload link.</summary>
[Collection("db")]
public class DutyAuditTests(DbFixture fx)
{
    /// <summary>A past school day, so duties can be evaluated.</summary>
    private static readonly DateOnly Past = SchoolWeek.Today.AddDays(-3);

    private static DutyInput Input(int primary, AttendanceStatus attendance = AttendanceStatus.NotRecorded, int? backup = null, DateOnly? day = null) =>
        new(DutyComponent.StudentsArrival, day ?? Past, "Gate supervision", new TimeOnly(7, 15), new TimeOnly(8, 0), "Main gate",
            primary, attendance, backup, null, []);

    private async Task<DutyRow> GetAsync(int id)
    {
        await using var db = fx.NewContext();
        return (await DbFixture.Duties(db).GetAsync(id))!;
    }

    private async Task<int> NewDutyAsync(DutyInput input)
    {
        await using var db = fx.NewContext();
        var r = await DbFixture.Duties(db).CreateAsync(input);
        Assert.True(r.Succeeded, string.Join(" ", r.Errors.Select(e => e.Value)));
        return r.EntityId!.Value;
    }

    [Fact]
    public async Task All_four_statuses_work_and_covered_needs_confirmed_presence()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var id = await NewDutyAsync(Input(primary));
        Assert.Equal(DutyStatus.Pending, (await GetAsync(id)).Status);

        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        // Assigned is not covered: nobody recorded present yet.
        Assert.False((await duties.SetStatusAsync(id, DutyStatus.Covered)).Succeeded);
        Assert.False((await duties.SetStatusAsync(id, DutyStatus.InProgress)).Succeeded);
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.NotCovered)).Succeeded);
        Assert.Equal(DutyStatus.NotCovered, (await GetAsync(id)).Status);
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.Pending)).Succeeded);

        Assert.True((await duties.SetPrimaryAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.InProgress)).Succeeded);
        Assert.Equal(DutyStatus.InProgress, (await GetAsync(id)).Status);
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.Covered)).Succeeded);
        Assert.Equal(DutyStatus.Covered, (await GetAsync(id)).Status);
    }

    [Fact]
    public async Task Status_is_independent_of_attendance()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var (backup, _) = await fx.NewTeacherAsync();
        var id = await NewDutyAsync(Input(primary));
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);

        // Recording attendance does not change the status…
        Assert.True((await duties.SetPrimaryAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);
        var d = await GetAsync(id);
        Assert.Equal((AttendanceStatus.Present, DutyStatus.Pending), (d.PrimaryAttendance, d.Status));

        // …and setting the status does not change attendance.
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.Covered)).Succeeded);
        Assert.Equal(AttendanceStatus.Present, (await GetAsync(id)).PrimaryAttendance);

        // A Covered duty cannot silently lose its only present person.
        var absent = await duties.UpdateAsync(id, Input(primary, AttendanceStatus.Absent, backup));
        Assert.False(absent.Succeeded);
        d = await GetAsync(id);
        Assert.Equal((AttendanceStatus.Present, DutyStatus.Covered), (d.PrimaryAttendance, d.Status));

        // A backup accepting is not coverage either.
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.Pending)).Succeeded);
        Assert.True((await duties.UpdateAsync(id, Input(primary, AttendanceStatus.Absent, backup))).Succeeded);
        Assert.True((await duties.RecordBackupResponseAsync(id, true, null)).Succeeded);
        Assert.False((await duties.SetStatusAsync(id, DutyStatus.Covered)).Succeeded);
        Assert.True((await duties.SetBackupAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.Covered)).Succeeded);
    }

    [Fact]
    public async Task Primary_and_backup_assignments_are_tracked_separately()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var (declined, _) = await fx.NewTeacherAsync();
        var (accepted, _) = await fx.NewTeacherAsync();
        var id = await NewDutyAsync(Input(primary, AttendanceStatus.Absent, declined));
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        Assert.True((await duties.RecordBackupResponseAsync(id, false, "Teaching")).Succeeded);
        Assert.True((await duties.UpdateAsync(id, Input(primary, AttendanceStatus.Absent, accepted))).Succeeded);
        Assert.True((await duties.RecordBackupResponseAsync(id, true, null)).Succeeded);

        var staff = await duties.StaffAsync(id);
        Assert.Equal(3, staff.Count);
        var p = staff.Single(s => s.StaffId == primary);
        var d = staff.Single(s => s.StaffId == declined);
        var a = staff.Single(s => s.StaffId == accepted);
        Assert.Equal((DutyRole.Primary, true, AttendanceStatus.Absent), (p.Role, p.IsCurrent, p.Attendance));
        Assert.Equal((DutyRole.Backup, false, BackupRequestStatus.Rejected), (d.Role, d.IsCurrent, d.BackupStatus));
        Assert.Equal((DutyRole.Backup, true, BackupRequestStatus.Accepted), (a.Role, a.IsCurrent, a.BackupStatus));

        // Declined is not an attendance failure: not counted as an assigned duty at all.
        var declinedProfile = (await DbFixture.StaffDuty(db).ProfileAsync(declined))!;
        Assert.Equal(StaffDutyOutcome.Declined, declinedProfile.Duties.Single().Outcome);
        Assert.Equal((0, 0, 1), (declinedProfile.Summary.TotalAssigned, declinedProfile.Summary.NotCovered, declinedProfile.Summary.Declined));

        // The absent primary did not complete the duty, even once the backup covers it.
        Assert.True((await duties.SetBackupAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.Covered)).Succeeded);
        var primaryProfile = (await DbFixture.StaffDuty(db).ProfileAsync(primary))!;
        Assert.Equal(StaffDutyOutcome.Absent, primaryProfile.Duties.Single().Outcome);
        Assert.Equal((1, 0, 1), (primaryProfile.Summary.TotalAssigned, primaryProfile.Summary.Covered, primaryProfile.Summary.NotCovered));
        var backupProfile = (await DbFixture.StaffDuty(db).ProfileAsync(accepted))!;
        Assert.Equal(StaffDutyOutcome.Completed, backupProfile.Duties.Single().Outcome);
    }

    [Fact]
    public async Task Each_assigned_person_gets_an_individual_evaluation_and_history_is_preserved()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var (backup, _) = await fx.NewTeacherAsync();
        var (supervisor, _) = await fx.NewTeacherAsync();
        var (declined, _) = await fx.NewTeacherAsync();
        var id = await NewDutyAsync(Input(primary, AttendanceStatus.Absent, declined));
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        Assert.True((await duties.RecordBackupResponseAsync(id, false, null)).Succeeded);
        Assert.True((await duties.UpdateAsync(id, Input(primary, AttendanceStatus.Absent, backup))).Succeeded);
        Assert.True((await duties.RecordBackupResponseAsync(id, true, null)).Succeeded);
        Assert.True((await duties.SetBackupAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);

        var rows = await duties.StaffAsync(id);
        var primaryRow = rows.Single(r => r.StaffId == primary);
        var backupRow = rows.Single(r => r.StaffId == backup);
        var declinedRow = rows.Single(r => r.StaffId == declined);
        Assert.True(primaryRow.CanEvaluate);
        Assert.True(backupRow.CanEvaluate);
        Assert.False(declinedRow.CanEvaluate);

        var staffDuty = DbFixture.StaffDuty(db);
        FeedbackInput Feedback(int assignment, int rating, int evaluator, string text) =>
            new(assignment, rating, text, "Punctual", null, false, evaluator, null);

        Assert.True((await staffDuty.AddFeedbackAsync(Feedback(primaryRow.Id, 2, supervisor, "Absent without notice"))).Succeeded);
        Assert.True((await staffDuty.AddFeedbackAsync(Feedback(backupRow.Id, 5, supervisor, "Covered at short notice"))).Succeeded);
        Assert.False((await staffDuty.AddFeedbackAsync(Feedback(declinedRow.Id, 3, supervisor, "x"))).Succeeded);   // declined backup
        Assert.False((await staffDuty.AddFeedbackAsync(Feedback(backupRow.Id, 4, backup, "Self"))).Succeeded);       // self-evaluation
        Assert.False((await staffDuty.AddFeedbackAsync(Feedback(backupRow.Id, 6, supervisor, "x"))).Succeeded);      // rating out of range

        // A second evaluation is added, never overwriting the first.
        Assert.True((await staffDuty.AddFeedbackAsync(Feedback(primaryRow.Id, 4, supervisor, "Absence was approved later"))).Succeeded);
        var profile = (await staffDuty.ProfileAsync(primary))!;
        Assert.Equal(2, profile.Evaluations.Count);
        Assert.Equal(["Absence was approved later", "Absent without notice"], profile.Evaluations.Select(e => e.Feedback));
        Assert.True(profile.Evaluations[0].IsLatest);
        Assert.False(profile.Evaluations[1].IsLatest);
        Assert.Equal(4.0, profile.Summary.AverageRating); // latest evaluation of the one evaluated duty
        Assert.Equal(StaffDutyOutcome.Absent, profile.Duties.Single().Outcome); // evaluation does not change the outcome

        Assert.Equal(5.0, (await staffDuty.ProfileAsync(backup))!.Summary.AverageRating);

        // A duty with evaluations is kept.
        Assert.False((await duties.DeleteAsync(id)).Succeeded);
    }

    [Fact]
    public async Task Future_duties_cannot_be_evaluated()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var (supervisor, _) = await fx.NewTeacherAsync();
        var id = await NewDutyAsync(Input(primary, day: SchoolWeek.Today.AddDays(5)));
        await using var db = fx.NewContext();
        var row = (await DbFixture.Duties(db).StaffAsync(id)).Single();
        Assert.False(row.CanEvaluate);
        var r = await DbFixture.StaffDuty(db).AddFeedbackAsync(new(row.Id, 4, "Early", null, null, false, supervisor, null));
        Assert.False(r.Succeeded);
    }

    [Fact]
    public async Task Every_change_is_in_the_audit_trail_which_survives_deletion()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var id = await NewDutyAsync(Input(primary));
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        Assert.True((await duties.SetPrimaryAttendanceAsync(id, AttendanceStatus.Present)).Succeeded);
        Assert.True((await duties.UpdateAuditAsync(id, DutyStatus.Covered, "Smooth morning", "Gate opened late", true)).Succeeded);

        var d = await GetAsync(id);
        Assert.Equal((DutyStatus.Covered, "Smooth morning", "Gate opened late", true), (d.Status, d.AuditNotes, d.IssuesEncountered, d.FollowUpRequired));
        Assert.Equal(("Test runner", "Test runner"), (d.CreatedBy, d.UpdatedBy));

        var trail = await duties.AuditTrailAsync(id);
        Assert.Contains(trail, a => a.Action == DutyAuditAction.Created);
        Assert.Contains(trail, a => a.Action == DutyAuditAction.AttendanceRecorded);
        Assert.Contains(trail, a => a.Action == DutyAuditAction.StatusChanged && a.StatusAfter == DutyStatus.Covered);
        Assert.Contains(trail, a => a.Action == DutyAuditAction.AuditNotesUpdated);

        Assert.True((await duties.DeleteAsync(id)).Succeeded);
        Assert.Contains(await duties.AuditTrailAsync(id), a => a.Action == DutyAuditAction.Deleted);
    }

    [Fact]
    public async Task Audit_list_filters_by_status_staff_component_location_and_date()
    {
        var (primary, _) = await fx.NewTeacherAsync();
        var day = new DateOnly(2031, 5, 4);
        var id = await NewDutyAsync(Input(primary, day: day) with { Location = "North gate " + DbFixture.Unique() });
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        Assert.True((await duties.SetStatusAsync(id, DutyStatus.NotCovered)).Succeeded);
        var location = (await GetAsync(id)).Location!;

        async Task<int> Count(DutyAuditQuery q) => (await duties.AuditListAsync(q)).TotalCount;
        var iso = SchoolWeek.Iso(day);
        Assert.Equal(1, await Count(new() { StaffId = primary }));
        Assert.Equal(1, await Count(new() { StaffId = primary, Status = "not-covered", From = iso, To = iso, Component = "students-arrival", Type = "Morning" }));
        Assert.Equal(0, await Count(new() { StaffId = primary, Status = "covered" }));
        Assert.Equal(0, await Count(new() { StaffId = primary, Component = "in-class-morning" }));
        Assert.Equal(1, await Count(new() { Location = location }));
        Assert.Equal(0, await Count(new() { StaffId = primary, From = SchoolWeek.Iso(day.AddDays(1)) }));
    }

    [Fact]
    public async Task Duty_workload_respects_attendance_the_24_limit_and_counts_once()
    {
        var (teacher, _) = await fx.NewTeacherAsync();
        var (other, _) = await fx.NewTeacherAsync();
        var week = SchoolWeek.StartOf(Past);
        var present = await NewDutyAsync(Input(teacher));
        var absent = await NewDutyAsync(Input(teacher, AttendanceStatus.Absent, other));
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        var workload = DbFixture.Workload(db);
        Assert.True((await duties.SetPrimaryAttendanceAsync(present, AttendanceStatus.Present)).Succeeded);

        // Absent on the duty → not countable.
        Assert.False((await workload.CreateAsync(new(teacher, week, WorkloadCategory.Duty, 1, null, absent, null))).Succeeded);
        Assert.DoesNotContain(await workload.DutyOptionsAsync(teacher, week, null), o => o.Id == absent);

        // Fill the week to 24, then the duty no longer fits.
        Assert.True((await workload.CreateAsync(new(teacher, week, WorkloadCategory.Office, 24, null, null, null))).Succeeded);
        var full = await workload.CreateAsync(new(teacher, week, WorkloadCategory.Duty, 1, null, present, null));
        Assert.False(full.Succeeded);
        Assert.Contains("24/24", full.Errors[0].Value);

        // Make room: the duty counts once, in the Duty category.
        var office = (await workload.AssignmentsAsync(teacher, week)).Single();
        Assert.True((await workload.UpdateAsync(office.Id, new(teacher, week, WorkloadCategory.Office, 23, null, null, null))).Succeeded);
        Assert.True((await workload.CreateAsync(new(teacher, week, WorkloadCategory.Duty, 1, null, present, null))).Succeeded);
        Assert.False((await workload.CreateAsync(new(teacher, week, WorkloadCategory.Duty, 1, null, present, null))).Succeeded);
        var s = (await workload.SummaryAsync(teacher, week))!;
        Assert.Equal((1, 23, 24, WorkloadStatus.Full), (s.Breakdown.Duty, s.Breakdown.Office, s.Total, s.Status));

        // The profile shows it as counted; marking the teacher absent now is refused.
        var row = (await DbFixture.StaffDuty(db).ProfileAsync(teacher))!.Duties.Single(d => d.DutyId == present);
        Assert.Equal(1, row.WorkloadPeriods);
        Assert.False(row.CanCountInWorkload);
        Assert.False((await duties.UpdateAsync(present, Input(teacher, AttendanceStatus.Absent, other))).Succeeded);
    }

    [Fact]
    public async Task Status_counts_match_the_database()
    {
        var day = new DateOnly(2031, 6, 1);
        var (primary, _) = await fx.NewTeacherAsync();
        var a = await NewDutyAsync(Input(primary, day: day));
        var b = await NewDutyAsync(Input(primary, day: day));
        await NewDutyAsync(Input(primary, day: day));
        await using var db = fx.NewContext();
        var duties = DbFixture.Duties(db);
        Assert.True((await duties.SetStatusAsync(a, DutyStatus.NotCovered)).Succeeded);
        Assert.True((await duties.SetPrimaryAttendanceAsync(b, AttendanceStatus.Present)).Succeeded);
        Assert.True((await duties.SetStatusAsync(b, DutyStatus.InProgress)).Succeeded);

        var counts = await duties.StatusCountsAsync(day);
        foreach (var status in DutyStatuses.All)
            Assert.Equal(await db.DutyAssignments.CountAsync(d => d.DutyDate == day && d.Status == status), counts[status]);
        Assert.Equal((1, 1, 1, 0), (counts[DutyStatus.Pending], counts[DutyStatus.InProgress], counts[DutyStatus.NotCovered], counts[DutyStatus.Covered]));
    }
}
