using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Staff Attendance: every active staff member (teachers and management), one record per person per school day, arrival
/// times only when actually recorded, Not Recorded never counted as Absent, and corrections that keep their history.
/// </summary>
[Collection("db")]
public sealed class StaffAttendanceTests(DbFixture fx)
{
    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private static SchoolTimeService CairoAt(DateOnly day, int h, int m)
    {
        var local = day.ToDateTime(new TimeOnly(h, m));
        var offset = TimeZoneInfo.FindSystemTimeZoneById(SchoolTimeService.DefaultTimeZoneId).GetUtcOffset(local);
        return new(new FixedClock(new DateTimeOffset(local, offset)), SchoolTimeService.DefaultTimeZoneId);
    }

    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Attendance tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static StaffAttendanceService Service(SchoolDbContext db, ISchoolTimeService time, StaffAttendanceOptions? options = null) =>
        new(db, new TestUser(), DbFixture.Settings(db), new BelongingsService(db, DbFixture.Timetable(db), new TestUser()), time, options);

    /// <summary>A past school day (not a weekend or holiday), the same for every test in a run; <paramref name="after"/> skips school days.</summary>
    private async Task<DateOnly> SchoolDayAsync(int after = 0)
    {
        await using var db = fx.NewContext();
        var days = await SchoolDays.LoadAsync(db, default);
        var d = new DateOnly(2025, 3, 2);
        while (!days.IsSchoolDay(d)) d = d.AddDays(1);
        for (var i = 0; i < after; i++)
        {
            d = d.AddDays(1);
            while (!days.IsSchoolDay(d)) d = d.AddDays(1);
        }
        return d;
    }

    private static DateTimeOffset At(DateOnly day, int h, int m, int s)
    {
        var local = day.ToDateTime(new TimeOnly(h, m, s));
        return new DateTimeOffset(local, TimeZoneInfo.FindSystemTimeZoneById(SchoolTimeService.DefaultTimeZoneId).GetUtcOffset(local));
    }

    private static SchoolTimeService CairoAt(DateOnly day, int h, int m, int s) =>
        new(new FixedClock(At(day, h, m, s)), SchoolTimeService.DefaultTimeZoneId);

    private static StaffEmploymentInput Job(TimeOnly start, DateOnly? contractStart = null, DateOnly? contractEnd = null,
        EmploymentType type = EmploymentType.FullTime, params DayOfWeek[] days) => new()
    {
        ContractStartDate = contractStart ?? new DateOnly(2020, 1, 1), ContractEndDate = contractEnd, EmploymentType = type, WorkingDays = days,
        WorkingHoursType = WorkingHoursType.Fixed, WorkStart = start, WorkEnd = new TimeOnly(15, 0),
    };

    private async Task<int> TeacherAsync(StaffEmploymentInput job)
    {
        var (id, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        Assert.True((await new StaffEmploymentService(db).SaveAsync(id, job)).Succeeded);
        return id;
    }

    private async Task<int> ManagerAsync(StaffEmploymentInput job)
    {
        await using var db = fx.NewContext();
        var r = await DbFixture.Management(db).CreateAsync(new ManagementMemberInput { FullName = "Manager " + DbFixture.Unique() });
        Assert.True(r.Succeeded);
        Assert.True((await new StaffEmploymentService(db).SaveAsync(r.EntityId!.Value, job)).Succeeded);
        return r.EntityId!.Value;
    }

    [Fact]
    public async Task One_click_records_once_with_the_school_time_and_Late_follows_the_start_time()
    {
        var day = await SchoolDayAsync();
        var teacher = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var manager = await ManagerAsync(Job(new TimeOnly(7, 30)));
        var waiting = await TeacherAsync(Job(new TimeOnly(8, 0)));
        await using var db = fx.NewContext();
        var svc = Service(db, CairoAt(day, 7, 40));

        // 07:40: before the teacher's 08:00 start Late does not apply; after the manager's 07:30 start it does.
        Assert.False((await svc.MarkAsync(teacher, day, StaffAttendanceStatus.Late)).Saved);
        var present = await svc.MarkAsync(teacher, day, StaffAttendanceStatus.Present);
        Assert.True(present.Saved, present.Message);
        Assert.Equal(new TimeOnly(7, 40), present.Row!.ArrivalTime);           // the server's time, never typed in
        Assert.True((await svc.MarkAsync(manager, day, StaffAttendanceStatus.Late)).Saved);

        // A second click (or another device) never records the person again.
        var again = await svc.MarkAsync(teacher, day, StaffAttendanceStatus.Absent);
        Assert.False(again.Saved);
        Assert.Contains("already recorded", again.Message);
        Assert.Equal(1, await db.StaffAttendance.CountAsync(r => r.StaffMemberId == teacher && r.Date == day));

        // The database refuses a duplicate for the same person and date too.
        db.StaffAttendance.Add(new StaffAttendanceRecord { StaffMemberId = teacher, Date = day, Status = StaffAttendanceStatus.Absent });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Teachers and management are both on the register; the one not yet recorded is Not Recorded, never Absent.
        var page = await svc.DayAsync(day, new StaffAttendanceFilter());
        var mine = page.Rows.Where(r => r.StaffId == teacher || r.StaffId == manager || r.StaffId == waiting).ToDictionary(r => r.StaffId);
        Assert.Equal(3, mine.Count);
        Assert.True(mine[manager].IsManagement);
        Assert.Equal(StaffAttendanceStatus.Present, mine[teacher].Status);
        Assert.Equal(StaffAttendanceStatus.Late, mine[manager].Status);
        Assert.Null(mine[waiting].Status);
        Assert.Null(mine[waiting].ArrivalTime);

        // The day's counts are the records of the expected staff, each once; Not Recorded = expected without a record.
        var counts = await svc.CountsAsync(day);
        var records = await db.StaffAttendance.Where(r => r.Date == day).ToListAsync();
        Assert.Equal(page.Counts, counts);
        Assert.Equal(records.Count(r => r.Status == StaffAttendanceStatus.Absent), counts.Absent);
        Assert.Equal(counts.Expected - records.Count, counts.NotRecorded);
        Assert.True(counts.NotRecorded >= 1);
    }

    [Fact]
    public async Task Corrections_need_a_reason_and_keep_the_earlier_record()
    {
        var day = await SchoolDayAsync();
        var teacher = await TeacherAsync(Job(new TimeOnly(8, 0)));
        await using var db = fx.NewContext();
        var svc = Service(db, CairoAt(day, 7, 50));

        // Correct never creates a record (no manual time entry in the normal workflow): one click does.
        var nothing = await svc.CorrectAsync(teacher, day, StaffAttendanceStatus.Present, new TimeOnly(7, 30), "Typed in");
        Assert.False(nothing.Saved);
        Assert.Contains("use Present, Late or Absent", nothing.Message);
        Assert.False(await db.StaffAttendance.AnyAsync(r => r.StaffMemberId == teacher && r.Date == day));
        Assert.True((await svc.MarkAsync(teacher, day, StaffAttendanceStatus.Present)).Saved);
        Assert.False((await svc.CorrectAsync(teacher, day, StaffAttendanceStatus.Late, null, "No time")).Saved);               // no time invented
        Assert.False((await svc.CorrectAsync(teacher, day, StaffAttendanceStatus.Late, new TimeOnly(9, 0), "Later")).Saved);  // later than now

        // Changing a saved record needs a reason; the before / after, reason and author are kept.
        Assert.False((await svc.CorrectAsync(teacher, day, StaffAttendanceStatus.Absent, null, null)).Saved);
        var fixedUp = await svc.CorrectAsync(teacher, day, StaffAttendanceStatus.Absent, new TimeOnly(7, 50), "Recorded for the wrong person");
        Assert.True(fixedUp.Saved, fixedUp.Message);
        var record = await db.StaffAttendance.Include(r => r.Corrections).AsNoTracking().SingleAsync(r => r.StaffMemberId == teacher && r.Date == day);
        Assert.Equal(StaffAttendanceStatus.Absent, record.Status);
        Assert.Null(record.ArrivalTime);                               // Absent never has an arrival time
        var c = Assert.Single(record.Corrections);
        Assert.Equal(("Present · 7:50 AM", "Absent", "Recorded for the wrong person", "Attendance tester"), (c.Before, c.After, c.Reason, c.CorrectedBy));

        // History for that person and day: the line and its correction.
        var h = await svc.HistoryAsync(new StaffAttendanceQuery { Period = "day", Date = day, StaffId = teacher });
        var line = Assert.Single(h.Lines);
        Assert.Equal((StaffAttendanceStatus?)StaffAttendanceStatus.Absent, line.Status);
        Assert.Equal(1, line.Corrections);
        Assert.Single(h.Corrections);
        Assert.Equal(new StaffAttendanceCounts(1, 0, 0, 1), h.Totals);
    }

    [Fact]
    public async Task Only_active_staff_working_that_day_are_expected()
    {
        var day = await SchoolDayAsync();
        var ended = await TeacherAsync(Job(new TimeOnly(8, 0), new DateOnly(2020, 1, 1), day.AddDays(-1)));
        var notStarted = await TeacherAsync(Job(new TimeOnly(8, 0), day.AddDays(1)));
        var otherDays = await TeacherAsync(Job(new TimeOnly(8, 0), type: EmploymentType.PartTime, days: [day.AddDays(1).DayOfWeek]));
        var thisDay = await TeacherAsync(Job(new TimeOnly(8, 0), type: EmploymentType.PartTime, days: [day.DayOfWeek]));
        await using var db = fx.NewContext();
        var svc = Service(db, CairoAt(day, 7, 45));

        var ids = (await svc.DayAsync(day, new StaffAttendanceFilter())).Rows.Select(r => r.StaffId).ToHashSet();
        Assert.DoesNotContain(ended, ids);
        Assert.DoesNotContain(notStarted, ids);
        Assert.DoesNotContain(otherDays, ids);
        Assert.Contains(thisDay, ids);
        var refused = await svc.MarkAsync(ended, day, StaffAttendanceStatus.Present);
        Assert.False(refused.Saved);
        Assert.Contains("not expected", refused.Message);

        // Not a school day, or a future date: nothing can be recorded.
        var days = await SchoolDays.LoadAsync(db, default);
        var weekend = day;
        while (days.IsSchoolDay(weekend)) weekend = weekend.AddDays(1);
        Assert.False((await Service(db, CairoAt(weekend, 8, 0)).MarkAsync(thisDay, weekend, StaffAttendanceStatus.Present)).Saved);
        Assert.False((await svc.CorrectAsync(thisDay, day.AddDays(1), StaffAttendanceStatus.Absent, null, null)).Saved);
        Assert.Equal(StaffAttendanceCounts.Zero, await svc.CountsAsync(weekend));
    }

    [Fact]
    public async Task Every_staff_category_is_included_and_can_be_filtered()
    {
        var day = await SchoolDayAsync();
        var teacher = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var manager = await ManagerAsync(Job(new TimeOnly(8, 0)));
        var (librarian, _) = await fx.NewTeacherAsync("Librarian");          // a non-teaching job title: administrative & support
        await using (var setup = fx.NewContext())
            Assert.True((await new StaffEmploymentService(setup).SaveAsync(librarian, Job(new TimeOnly(8, 0)))).Succeeded);
        await using var db = fx.NewContext();
        var svc = Service(db, CairoAt(day, 7, 45));

        var all = (await svc.DayAsync(day, new StaffAttendanceFilter())).Rows.ToDictionary(r => r.StaffId);
        Assert.Equal(StaffCategory.Academic, all[teacher].Category);
        Assert.Equal(StaffCategory.Management, all[manager].Category);
        Assert.Equal(StaffCategory.Support, all[librarian].Category);

        foreach (var (slug, id) in new[] { ("academic", teacher), ("management", manager), ("support", librarian) })
        {
            var rows = (await svc.DayAsync(day, new StaffAttendanceFilter(Category: slug))).Rows;
            Assert.Contains(rows, r => r.StaffId == id);
            Assert.All(rows, r => Assert.Equal(slug, StaffCategories.Slug(r.Category)));
            var history = await svc.HistoryAsync(new StaffAttendanceQuery { Period = "day", Date = day, Category = slug });
            Assert.Contains(history.Lines, l => l.StaffId == id);
            Assert.All(history.ByStaff, t => Assert.Equal(slug, StaffCategories.Slug(all.TryGetValue(t.StaffId, out var r) ? r.Category : StaffCategory.Academic)));
        }
        // Filters never change the day's totals.
        Assert.Equal((await svc.DayAsync(day, new StaffAttendanceFilter())).Counts, (await svc.DayAsync(day, new StaffAttendanceFilter(Category: "management"))).Counts);
    }

    [Fact]
    public async Task Recording_closes_after_the_0930_minute_on_the_server()
    {
        var day = await SchoolDayAsync(after: 1);
        var atCutoff = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var lastSecond = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var tooLate = await TeacherAsync(Job(new TimeOnly(8, 0)));
        await using var db = fx.NewContext();

        // 09:30:00 and 09:30:59 are still "09:30": Present is saved with the server's exact time.
        var r1 = await Service(db, CairoAt(day, 9, 30, 0)).MarkAsync(atCutoff, day, StaffAttendanceStatus.Present);
        Assert.True(r1.Saved, r1.Message);
        Assert.Equal(new TimeOnly(9, 30, 0), r1.Row!.ArrivalTime);
        var r2 = await Service(db, CairoAt(day, 9, 30, 59)).MarkAsync(lastSecond, day, StaffAttendanceStatus.Late);
        Assert.True(r2.Saved, r2.Message);
        Assert.Equal(new TimeOnly(9, 30, 59), r2.Row!.ArrivalTime);

        // 09:31:00: closed for Present, Late and Absent alike (a page left open cannot get past it).
        var after = Service(db, CairoAt(day, 9, 31, 0));
        foreach (var status in new[] { StaffAttendanceStatus.Present, StaffAttendanceStatus.Late, StaffAttendanceStatus.Absent })
        {
            var refused = await after.MarkAsync(tooLate, day, status);
            Assert.False(refused.Saved);
            Assert.True(refused.Closed);
        }
        Assert.False(await db.StaffAttendance.AnyAsync(r => r.StaffMemberId == tooLate && r.Date == day));

        // Another day's page cannot be used either (only today, on the server's clock).
        Assert.False((await Service(db, CairoAt(day.AddDays(1), 8, 0, 0)).MarkAsync(tooLate, day, StaffAttendanceStatus.Present)).Saved);
    }

    [Fact]
    public async Task After_the_cutoff_unrecorded_staff_become_Absent_once_and_saved_records_are_kept()
    {
        var day = await SchoolDayAsync(after: 2);
        var present = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var late = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var absent = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var missing = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var ended = await TeacherAsync(Job(new TimeOnly(8, 0), new DateOnly(2020, 1, 1), day.AddDays(-1)));   // inactive that day
        var auto = new StaffAttendanceOptions(AutomaticAbsences: true, From: day);
        await using var db = fx.NewContext();

        var morning = Service(db, CairoAt(day, 8, 45, 10), auto);
        Assert.True((await morning.MarkAsync(present, day, StaffAttendanceStatus.Present)).Saved);
        Assert.True((await morning.MarkAsync(late, day, StaffAttendanceStatus.Late)).Saved);
        Assert.True((await morning.MarkAsync(absent, day, StaffAttendanceStatus.Absent)).Saved);

        // Before and exactly at 09:30 nobody is made Absent: no record = Not Recorded.
        foreach (var (h, m, sec) in new[] { (9, 0, 0), (9, 30, 0), (9, 30, 59) })
        {
            var open = Service(db, CairoAt(day, h, m, sec), auto);
            Assert.False((await open.FinalizeAsync(day)).Succeeded);
            var page = await open.DayAsync(day, new StaffAttendanceFilter());               // reading the page does not finalize either
            Assert.True(page.RecordingOpen);
            Assert.Null(page.Rows.Single(r => r.StaffId == missing).Status);
        }
        Assert.False(await db.StaffAttendance.AnyAsync(r => r.StaffMemberId == missing && r.Date == day));

        // 09:31: the background run stores Absent for everyone still unrecorded, and nobody else.
        var closed = Service(db, CairoAt(day, 9, 31, 0), auto);
        var created = await closed.FinalizeDueAsync();
        Assert.True(created >= 1);
        var rows = await db.StaffAttendance.AsNoTracking().Where(r => r.Date == day).ToListAsync();
        var byStaff = rows.ToDictionary(r => r.StaffMemberId);                              // ToDictionary: one record per person
        Assert.Equal((StaffAttendanceStatus.Absent, (TimeOnly?)null, StaffAttendanceService.AutomaticRecorder),
            (byStaff[missing].Status, byStaff[missing].ArrivalTime, byStaff[missing].RecordedBy));
        Assert.Equal((StaffAttendanceStatus.Present, (TimeOnly?)new TimeOnly(8, 45, 10), "Attendance tester"),
            (byStaff[present].Status, byStaff[present].ArrivalTime, byStaff[present].RecordedBy));
        Assert.Equal(StaffAttendanceStatus.Late, byStaff[late].Status);
        Assert.Equal("Attendance tester", byStaff[absent].RecordedBy);                     // the manual absence is not replaced
        Assert.False(byStaff.ContainsKey(ended));                                           // inactive staff are not expected

        // Idempotent: running again (background, page, dashboard) never adds or changes anything.
        Assert.Equal(0, await closed.FinalizeDueAsync());
        Assert.Equal(0, (await closed.FinalizeAsync(day)).EntityId);
        await closed.DayAsync(day, new StaffAttendanceFilter());
        await closed.TodayAsync(day);
        Assert.Equal(rows.Count, await db.StaffAttendance.CountAsync(r => r.Date == day));

        // The page, filters and counts: nobody left Not Recorded; the remaining staff are Absent.
        var after = await closed.DayAsync(day, new StaffAttendanceFilter());
        Assert.False(after.RecordingOpen);
        Assert.Equal(0, after.Counts.NotRecorded);
        Assert.Empty((await closed.DayAsync(day, new StaffAttendanceFilter(Status: "none"))).Rows);
        Assert.Contains((await closed.DayAsync(day, new StaffAttendanceFilter(Status: "absent"))).Rows, r => r.StaffId == missing);
        Assert.Equal(after.Counts, (await closed.TodayAsync(day)).Counts);

        // Lazy finalization: someone added to the day later is made Absent as soon as the page is read.
        var latecomer = await TeacherAsync(Job(new TimeOnly(8, 0)));
        var reread = await closed.DayAsync(day, new StaffAttendanceFilter());
        Assert.Equal(StaffAttendanceStatus.Absent, reread.Rows.Single(r => r.StaffId == latecomer).Status);

        // An automatic absence can be corrected by an administrator, with a reason and the history kept.
        Assert.True((await closed.CorrectAsync(missing, day, StaffAttendanceStatus.Late, new TimeOnly(9, 20), "Arrived late; badge not scanned")).Saved);
        var corrected = await db.StaffAttendance.Include(r => r.Corrections).AsNoTracking().SingleAsync(r => r.StaffMemberId == missing && r.Date == day);
        Assert.Equal(("Absent", "Late · 9:20 AM"), (corrected.Corrections.Single().Before, corrected.Corrections.Single().After));
        Assert.Equal(0, await closed.FinalizeDueAsync());                                   // the corrected record stays corrected
        Assert.Equal(StaffAttendanceStatus.Late, (await db.StaffAttendance.AsNoTracking().SingleAsync(r => r.StaffMemberId == missing && r.Date == day)).Status);

        // With automatic absences off, nothing is ever stored.
        var off = Service(db, CairoAt(day, 15, 0, 0));
        var stranger = await TeacherAsync(Job(new TimeOnly(8, 0)));
        await off.DayAsync(day, new StaffAttendanceFilter());
        Assert.Equal(0, await off.FinalizeDueAsync());
        Assert.False(await db.StaffAttendance.AnyAsync(r => r.StaffMemberId == stranger && r.Date == day));
    }

    [Fact]
    public async Task Automatic_absences_start_on_the_first_day_they_run_before_the_cutoff()
    {
        var day = await SchoolDayAsync(after: 3);
        await using var db = fx.NewContext();
        async Task Forget() => await db.Settings.Where(s => s.Key == StaffAttendanceService.AutoFinalizeFromKey).ExecuteDeleteAsync();
        var auto = new StaffAttendanceOptions(AutomaticAbsences: true);
        try
        {
            // Switched on in the afternoon: that day is not marked Absent (nobody could have recorded it); from the next day on.
            await Forget();
            var teacher = await TeacherAsync(Job(new TimeOnly(8, 0)));
            Assert.Equal(0, await Service(db, CairoAt(day, 15, 0, 0), auto).FinalizeDueAsync());
            Assert.False(await db.StaffAttendance.AnyAsync(r => r.StaffMemberId == teacher && r.Date == day));
            Assert.Equal(SchoolWeek.Iso(day.AddDays(1)), await db.Settings.Where(s => s.Key == StaffAttendanceService.AutoFinalizeFromKey).Select(s => s.Value).SingleAsync());

            // Switched on in the morning: that day counts.
            await Forget();
            Assert.Equal(0, await Service(db, CairoAt(day, 8, 0, 0), auto).FinalizeDueAsync());
            Assert.Equal(SchoolWeek.Iso(day), await db.Settings.Where(s => s.Key == StaffAttendanceService.AutoFinalizeFromKey).Select(s => s.Value).SingleAsync());
            Assert.True(await Service(db, CairoAt(day, 9, 31, 0), auto).FinalizeDueAsync() >= 1);
            Assert.Equal(StaffAttendanceStatus.Absent, (await db.StaffAttendance.AsNoTracking().SingleAsync(r => r.StaffMemberId == teacher && r.Date == day)).Status);
        }
        finally { await Forget(); }
    }

    [Fact]
    public void Employment_rules_are_checked_before_saving()
    {
        var svc = new StaffEmploymentService(null!);
        StaffEmploymentInput Valid() => Job(new TimeOnly(8, 0));
        string[] Errors(StaffEmploymentInput x) => svc.Validate(x).Errors.Select(e => e.Key).ToArray();

        Assert.Empty(Errors(Valid()));
        Assert.Contains("ContractStartDate", Errors(new StaffEmploymentInput { EmploymentType = EmploymentType.FullTime, WorkingHoursType = WorkingHoursType.Fixed, WorkStart = new(8, 0), WorkEnd = new(15, 0) }));
        var x = Valid(); x.ContractEndDate = new DateOnly(2019, 12, 31);
        Assert.Contains("ContractEndDate", Errors(x));
        x = Valid(); x.ContractEndDate = new DateOnly(2020, 2, 1); x.ProbationDays = 90;
        Assert.Contains("ProbationDays", Errors(x));                   // probation cannot outlast the contract
        x.ProbationDays = 30;                                          // 1 Jan – 30 Jan: within the contract
        Assert.Empty(Errors(x));
        x.ProbationStartDate = new DateOnly(2020, 1, 3);               // 3 Jan – 1 Feb: the contract's last day
        Assert.Empty(Errors(x));
        x.ProbationStartDate = new DateOnly(2020, 1, 4);               // would end 2 Feb
        Assert.Contains("ProbationDays", Errors(x));
        x = Valid(); x.ProbationDays = 60; x.ProbationStartDate = new DateOnly(2019, 12, 31);
        Assert.Contains("ProbationStartDate", Errors(x));              // not before the contract starts
        x = Valid(); x.ProbationDays = 45;
        Assert.Contains("ProbationDays", Errors(x));                   // only 30, 60 or 90 days
        x = Valid(); x.ProbationDays = 90;
        Assert.Empty(Errors(x));
        Assert.Equal(new DateOnly(2020, 3, 30), StaffEmploymentService.ProbationEnd(new DateOnly(2020, 1, 1), 90)); // both days counted
        x = Valid(); x.EmploymentType = EmploymentType.PartTime;
        Assert.Contains("WorkingDays", Errors(x));                     // part-time needs its weekdays
        x.WorkingDays = [DayOfWeek.Sunday, DayOfWeek.Tuesday];
        Assert.Empty(Errors(x));
        x = Valid(); x.WorkingHoursType = WorkingHoursType.Flexible; x.WorkStart = new(7, 30); x.WorkEnd = new(12, 0);
        Assert.Contains("WorkStart", Errors(x));                       // flexible hours stay within 08:00–15:00
        x.WorkStart = new(9, 0); x.WorkEnd = new(15, 30);
        Assert.Contains("WorkStart", Errors(x));
        x.WorkEnd = new(14, 0);
        Assert.Empty(Errors(x));
        x.WorkEnd = new(9, 0);
        Assert.Contains("WorkEnd", Errors(x));                         // start before end
    }
}
