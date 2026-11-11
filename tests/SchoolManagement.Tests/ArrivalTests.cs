using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Student arrival records: uniqueness, the configurable deadline, corrections and statistics.</summary>
[Collection("db")]
public class ArrivalTests(DbFixture fx)
{
    private static readonly DateOnly Today = SchoolWeek.Today;

    private static ArrivalEntry Present(int studentId, int h, int m, int s = 0, ArrivalMethod? method = ArrivalMethod.SchoolBus) =>
        new(studentId, ArrivalStatus.Present, new TimeOnly(h, m, s), method, "Main gate", null, false, null);

    private static ArrivalEntry Absent(int studentId) => new(studentId, ArrivalStatus.Absent, null, null, null, null, false, "Sick");

    private async Task SetDeadlineAsync(int h, int m)
    {
        await using var db = fx.NewContext();
        Assert.True((await DbFixture.Settings(db).SaveArrivalDeadlineAsync(new TimeOnly(h, m))).Succeeded);
    }

    private async Task<IReadOnlyList<ArrivalSaveResult>> RecordAsync(DateOnly date, params ArrivalEntry[] entries)
    {
        await using var db = fx.NewContext();
        return await DbFixture.Arrivals(db).RecordAsync(date, entries);
    }

    [Fact]
    public async Task One_record_per_student_per_day()
    {
        var classId = await fx.NewClassAsync();
        var (student, _) = await fx.NewStudentAsync(classId);
        Assert.True((await RecordAsync(Today, Present(student, 7, 30))).Single().Saved);

        var again = (await RecordAsync(Today, Absent(student))).Single();
        Assert.False(again.Saved);
        Assert.Contains("Already recorded", again.Error);

        // The database refuses a duplicate even outside the service.
        await using var db = fx.NewContext();
        db.StudentArrivals.Add(new StudentArrivalRecord { StudentId = student, SchoolClassId = classId, ArrivalDate = Today, Status = ArrivalStatus.Absent });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // Another day is a new record.
        Assert.True((await RecordAsync(Today.AddDays(-1), Present(student, 7, 20))).Single().Saved);
        Assert.False((await RecordAsync(Today.AddDays(1), Present(student, 7, 20))).Single().Saved); // future date
    }

    [Fact]
    public async Task Late_status_uses_the_configured_deadline_and_keeps_the_exact_time()
    {
        await SetDeadlineAsync(7, 45);
        var classId = await fx.NewClassAsync();
        var (onTime, _) = await fx.NewStudentAsync(classId);
        var (late, _) = await fx.NewStudentAsync(classId);
        var results = await RecordAsync(Today, Present(onTime, 7, 45, 0), Present(late, 7, 45, 1));
        Assert.All(results, r => Assert.True(r.Saved, r.Error));

        await using var db = fx.NewContext();
        var rows = await db.StudentArrivals.AsNoTracking().Where(r => r.StudentId == onTime || r.StudentId == late).ToListAsync();
        var a = rows.Single(r => r.StudentId == onTime);
        var b = rows.Single(r => r.StudentId == late);
        Assert.Equal((LateStatus.OnTime, new TimeOnly(7, 45, 0), new TimeOnly(7, 45)), (a.LateStatus!.Value, a.ArrivalTime!.Value, a.DeadlineUsed!.Value));
        Assert.Equal((LateStatus.Late, new TimeOnly(7, 45, 1)), (b.LateStatus!.Value, b.ArrivalTime!.Value));

        // Changing the deadline does not rewrite history…
        await SetDeadlineAsync(8, 0);
        Assert.Equal(LateStatus.Late, (await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.Id == b.Id)).LateStatus);

        // …a correction re-evaluates with the current deadline and is audited.
        var corrected = await DbFixture.Arrivals(db).CorrectAsync(b.Id, Present(late, 7, 45, 1), "Re-check after deadline change");
        Assert.True(corrected.Succeeded, string.Join(" ", corrected.Errors.Select(e => e.Value)));
        var after = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.Id == b.Id);
        Assert.Equal((LateStatus.OnTime, new TimeOnly(8, 0)), (after.LateStatus!.Value, after.DeadlineUsed!.Value));
        var trail = await db.StudentArrivalCorrections.AsNoTracking().SingleAsync(c => c.StudentArrivalRecordId == b.Id);
        Assert.Contains("late", trail.Before);
        Assert.Contains("on time", trail.After);
        Assert.Equal("Re-check after deadline change", trail.Reason);
        Assert.Equal("Test runner", trail.CorrectedBy);

        await SetDeadlineAsync(7, 45);
    }

    [Fact]
    public async Task Corrections_need_a_reason_and_a_real_change()
    {
        var classId = await fx.NewClassAsync();
        var (student, _) = await fx.NewStudentAsync(classId);
        Assert.True((await RecordAsync(Today, Present(student, 7, 10))).Single().Saved);
        await using var db = fx.NewContext();
        var id = (await db.StudentArrivals.SingleAsync(r => r.StudentId == student)).Id;
        var arrivals = DbFixture.Arrivals(db);

        Assert.False((await arrivals.CorrectAsync(id, Absent(student), "")).Succeeded);
        Assert.False((await arrivals.CorrectAsync(id, Present(student, 7, 10), "No change")).Succeeded);
        Assert.True((await arrivals.CorrectAsync(id, Absent(student), "Was at home; entered by mistake")).Succeeded);
        var record = await db.StudentArrivals.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal((ArrivalStatus.Absent, (TimeOnly?)null, (LateStatus?)null), (record.Status, record.ArrivalTime, record.LateStatus));
        Assert.Equal(1, await db.StudentArrivalCorrections.CountAsync(c => c.StudentArrivalRecordId == id));
    }

    [Fact]
    public async Task Missing_records_are_not_counted_as_absences()
    {
        await SetDeadlineAsync(7, 45);
        var classId = await fx.NewClassAsync();
        var (present, _) = await fx.NewStudentAsync(classId);
        var (absent, _) = await fx.NewStudentAsync(classId);
        await fx.NewStudentAsync(classId);   // never recorded
        await RecordAsync(Today, Present(present, 7, 50), Absent(absent));

        await using var db = fx.NewContext();
        var checklist = await DbFixture.Arrivals(db).ChecklistAsync(Today, classId);
        var s = checklist.Summary;
        Assert.Equal((3, 2, 1, 1, 1, 0, 1), (s.Students, s.Recorded, s.Present, s.Absent, s.Late, s.OnTime, s.NotRecorded));
        Assert.Equal(0.0, s.OnTimePercent);
        Assert.Single(checklist.Rows, r => !r.IsRecorded);
    }

    [Fact]
    public async Task Student_history_summarises_recorded_days_only()
    {
        await SetDeadlineAsync(7, 45);
        var classId = await fx.NewClassAsync();
        var (student, _) = await fx.NewStudentAsync(classId);
        var (receiver, receiverName) = await fx.NewTeacherAsync();
        await RecordAsync(Today.AddDays(-3), Present(student, 7, 30) with { ReceivedByStaffId = receiver, ReceivingConfirmed = true });
        await RecordAsync(Today.AddDays(-2), Present(student, 7, 55, 0, ArrivalMethod.PrivateCar));
        await RecordAsync(Today.AddDays(-1), Absent(student));
        // Today has no record and must not appear anywhere in the statistics.

        await using var db = fx.NewContext();
        var h = await DbFixture.Arrivals(db).StudentHistoryAsync(student, null, null, newestFirst: false);
        Assert.Equal([Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1)], h.Records.Select(r => r.Date));
        var s = h.Summary;
        Assert.Equal((3, 2, 1, 1, 1), (s.Recorded, s.Present, s.Absent, s.Late, s.OnTime));
        Assert.Equal(50.0, s.OnTimePercent);
        Assert.Contains(h.Methods, m => m.Label == "Private car" && m.Count == 1);
        Assert.Contains(h.Receivers, r => r.Name == receiverName && r.Count == 1);

        var filtered = await DbFixture.Arrivals(db).StudentHistoryAsync(student, Today.AddDays(-2), Today.AddDays(-2), true);
        Assert.Equal(1, filtered.Summary.Recorded);

        // A student with history is kept.
        Assert.False((await DbFixture.Students(db).DeleteAsync(student)).Succeeded);
    }

    [Fact]
    public async Task Day_summary_and_average_rating_match_the_database()
    {
        await using var db = fx.NewContext();
        var summary = await DbFixture.Arrivals(db).DaySummaryAsync(Today);
        var records = await db.StudentArrivals.AsNoTracking().Where(r => r.ArrivalDate == Today).ToListAsync();
        var students = (await db.Students.AsNoTracking().Select(s => s.CreatedAt).ToListAsync())
            .Count(c => DateOnly.FromDateTime(c.ToLocalTime()) <= Today);
        Assert.Equal(students, summary.Students);
        Assert.Equal(records.Count, summary.Recorded);
        Assert.Equal(records.Count(r => r.Status == ArrivalStatus.Present), summary.Present);
        Assert.Equal(records.Count(r => r.Status == ArrivalStatus.Absent), summary.Absent);
        Assert.Equal(records.Count(r => r.LateStatus == LateStatus.Late), summary.Late);
        Assert.Equal(students - records.Count, summary.NotRecorded);

        var (average, count) = await DbFixture.StaffDuty(db).AverageRatingAsync();
        var latest = (await db.StaffFeedback.AsNoTracking().ToListAsync())
            .GroupBy(f => f.DutyStaffAssignmentId)
            .Select(g => g.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id).First().Rating).ToList();
        Assert.Equal(latest.Count, count);
        Assert.Equal(latest.Count == 0 ? null : Math.Round(latest.Average(), 2), average);
    }
}
