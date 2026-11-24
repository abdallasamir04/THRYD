using System.Net;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Time-based duty scheduling and coverage: Main Duty and Sub-Duty time ranges, PENDING / IN PROGRESS / COVERED / NOT COVERED
/// from the Cairo clock and the recorded attendance, required vs optional, exact uncovered areas, reasons with history,
/// versioned schedules (earlier days keep theirs), departments and the pages. Uses 2033 dates (after every other test's dates)
/// and removes its schedules and added Sub-Duties afterwards.
/// </summary>
[Collection("db")]
public class DutySchedulingTests(DbFixture fx) : IAsyncLifetime
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

    private static void Ok(ServiceResult r) => Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Removes this test's schedules, added Sub-Duties, reports and the duty records of its 2033 days.</summary>
    public async Task DisposeAsync()
    {
        await using var db = fx.NewContext();
        var from = new DateOnly(2033, 1, 1);
        await db.DutyCoverageReports.Where(r => r.DutyDate >= from).ExecuteDeleteAsync();
        var ids = await db.DutyAssignments.Where(d => d.DutyDate >= from).Select(d => d.Id).ToListAsync();
        await db.StaffFeedback.Where(f => ids.Contains(f.DutyStaffAssignment.DutyAssignmentId)).ExecuteDeleteAsync();
        await db.DutyStaffAssignments.Where(s => ids.Contains(s.DutyAssignmentId)).ExecuteDeleteAsync();
        await db.DutyAssignments.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync();
        await db.DutySectionSchedules.ExecuteDeleteAsync();
        await db.DutyMainSchedules.ExecuteDeleteAsync();
        await db.DutySections.Where(s => s.Id > 100).ExecuteDeleteAsync();
        await db.DutySections.Where(s => s.RetiredOn != null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RetiredOn, (DateOnly?)null));
        await DutyCatalog.LoadAsync(db);
    }

    private static readonly DateOnly Day = new(2033, 3, 6); // Sunday

    private static SectionInput Sub(DateOnly from, int sh, int sm, int eh, int em, bool required = true, int? dept = null, string? name = null) =>
        new(name, null, from, new TimeOnly(sh, sm), new TimeOnly(eh, em), required, dept, 0);

    /// <summary>
    /// Morning Duty 07:15–08:00: Students Arrival Check 07:15–07:45, In-Class Morning 07:15–07:45, Morning Assembly 07:45–08:00
    /// (required), plus an optional "Support Activity" 07:20–07:50.
    /// </summary>
    private async Task<DutyComponent> MorningAsync(SchoolDbContext db, DateOnly from)
    {
        var board = DbFixture.DutyBoard(db, CairoAt(from, 6, 0));
        Ok(await board.SaveMainScheduleAsync(DutyType.Morning, from, new TimeOnly(7, 15), new TimeOnly(8, 0), 0));
        Ok(await board.SaveSectionAsync(DutyComponent.StudentsArrival, Sub(from, 7, 15, 7, 45, dept: 1)));
        Ok(await board.SaveSectionAsync(DutyComponent.InClassMorning, Sub(from, 7, 15, 7, 45, dept: 2)));
        Ok(await board.SaveSectionAsync(DutyComponent.MorningAssembly, Sub(from, 7, 45, 8, 0, dept: 1)));
        var added = await board.AddSectionAsync(DutyType.Morning, Sub(from, 7, 20, 7, 50, required: false, name: "Support Activity " + DbFixture.Unique()));
        Ok(added);
        return (DutyComponent)added.EntityId!.Value;
    }

    private async Task<int> PointAsync(SchoolDbContext db, DutyComponent c, DateOnly day, int sh, int sm, int eh, int em, AttendanceStatus att, string location, List<int>? classes = null)
    {
        var (staff, _) = await fx.NewTeacherAsync();
        var r = await DbFixture.Duties(db).CreateAsync(new DutyInput(c, day, "Coverage " + location, new TimeOnly(sh, sm), new TimeOnly(eh, em), location, staff, att, null, null, classes ?? []));
        Ok(r);
        return r.EntityId!.Value;
    }

    private async Task<List<int>> ClassAsync() => [await fx.NewClassAsync()];

    // ------------------------------------------------------------------ examples 1–6

    [Fact]
    public async Task Statuses_follow_the_clock_and_the_recorded_coverage_examples_1_to_6()
    {
        await using var db = fx.NewContext();
        var support = await MorningAsync(db, Day);
        await PointAsync(db, DutyComponent.StudentsArrival, Day, 7, 15, 7, 45, AttendanceStatus.Present, "Main gate");
        var inClass = await PointAsync(db, DutyComponent.InClassMorning, Day, 7, 15, 7, 45, AttendanceStatus.Present, "KG building", await ClassAsync());
        await PointAsync(db, DutyComponent.MorningAssembly, Day, 7, 45, 8, 0, AttendanceStatus.Present, "Courtyard");
        await PointAsync(db, support, Day, 7, 20, 7, 50, AttendanceStatus.NotRecorded, "Library");

        async Task<MainDutyView> At(int h, int m) => (await DbFixture.DutyBoard(db, CairoAt(Day, h, m)).DayAsync(Day)).Get(DutyType.Morning);
        DutyStatus S(MainDutyView v, DutyComponent c) => v.Subs.Single(s => s.Info.Component == c).Status;

        // 1 — 07:00: everything Pending.
        var m = await At(7, 0);
        Assert.Equal((DutyStatus.Pending, "7:15–8:00 AM"), (m.Status, m.Times));
        Assert.All(m.Scheduled_, s => Assert.Equal(DutyStatus.Pending, s.Status));

        // 2 — 07:30: the duty and the first two Sub-Duties In Progress, the assembly still Pending (independent times).
        m = await At(7, 30);
        Assert.Equal((DutyStatus.InProgress, DutyStatus.InProgress, DutyStatus.InProgress, DutyStatus.Pending),
            (m.Status, S(m, DutyComponent.StudentsArrival), S(m, DutyComponent.InClassMorning), S(m, DutyComponent.MorningAssembly)));

        // A Sub-Duty with coverage recorded is still In Progress until its own end (07:44).
        Assert.Equal(DutyStatus.InProgress, S(await At(7, 44), DutyComponent.StudentsArrival));

        // 3 — 07:50: the first two ended with coverage recorded → Covered; the duty stays In Progress until 08:00.
        m = await At(7, 50);
        Assert.Equal((DutyStatus.InProgress, DutyStatus.Covered, DutyStatus.Covered, DutyStatus.InProgress),
            (m.Status, S(m, DutyComponent.StudentsArrival), S(m, DutyComponent.InClassMorning), S(m, DutyComponent.MorningAssembly)));
        Assert.Equal(DutyStatus.InProgress, (await At(7, 59)).Status);

        // 4 + 6 — 08:05: all required covered → Covered, although the optional Support Activity was Not Covered.
        m = await At(8, 5);
        Assert.Equal((DutyStatus.Covered, DutyStatus.NotCovered), (m.Status, S(m, support)));
        Assert.Equal((3, 3, 0), (m.Required.Count, m.RequiredCovered, m.RequiredNotCovered));
        Assert.Equal("Coverage was not recorded.", m.Subs.Single(s => s.Info.Component == support).Points.Single().Reason);

        // 5 — In-Class Morning without recorded coverage: Not Covered, so the duty is Not Covered (2 covered, 1 not) and the report names it.
        Ok(await DbFixture.Duties(db).SetPrimaryAttendanceAsync(inClass, AttendanceStatus.NotRecorded));
        m = await At(8, 5);
        Assert.Equal((DutyStatus.NotCovered, 2, 1), (m.Status, m.RequiredCovered, m.RequiredNotCovered));
        var notCovered = m.Scheduled_.Where(s => s.Status == DutyStatus.NotCovered && s.Required).ToList();
        Assert.Equal(DutyComponent.InClassMorning, notCovered.Single().Info.Component);
        Assert.Contains("KG building", SchoolManagement.Web.Services.Pdf.DutyCoverageReports.Gaps(notCovered.Single()).Single());
        Assert.Contains("Coverage was not recorded.", SchoolManagement.Web.Services.Pdf.DutyCoverageReports.Gaps(notCovered.Single()).Single());

        // The dashboard-level counts come from the same calculation.
        var sections = await new DutyService(db, new TestUser(), DbFixture.DutyBoard(db, CairoAt(Day, 8, 5))).SectionCoverageAsync(Day);
        Assert.Equal((2, 2), (sections[DutyType.Morning].Covered, sections[DutyType.Morning].NotCovered)); // Support (optional) + In-Class
    }

    [Fact]
    public async Task The_exact_uncovered_area_is_named_and_reasons_never_count_as_coverage()
    {
        var day = Day.AddDays(1); // Monday
        await using var db = fx.NewContext();
        var board0 = DbFixture.DutyBoard(db, CairoAt(day, 6, 0));
        Ok(await board0.SaveMainScheduleAsync(DutyType.Dismissal, day, new TimeOnly(14, 0), new TimeOnly(15, 0), 0));
        Ok(await board0.SaveSectionAsync(DutyComponent.DismissalPickup, Sub(day, 14, 0, 15, 0)));
        Ok(await board0.SaveSectionAsync(DutyComponent.DismissalBus, Sub(day, 14, 0, 15, 0)));
        await PointAsync(db, DutyComponent.DismissalPickup, day, 14, 0, 15, 0, AttendanceStatus.Present, "Gate A");
        var gateB = await PointAsync(db, DutyComponent.DismissalPickup, day, 14, 0, 15, 0, AttendanceStatus.NotRecorded, "Gate B");
        await PointAsync(db, DutyComponent.DismissalBus, day, 14, 0, 15, 0, AttendanceStatus.Present, "Bus area");

        var after = DbFixture.DutyBoard(db, CairoAt(day, 15, 30));
        var m = (await after.DayAsync(day)).Get(DutyType.Dismissal);
        var pickup = m.Subs.Single(s => s.Info.Component == DutyComponent.DismissalPickup);
        Assert.Equal((DutyStatus.NotCovered, DutyStatus.NotCovered, DutyStatus.Covered),
            (m.Status, pickup.Status, m.Subs.Single(s => s.Info.Component == DutyComponent.DismissalBus).Status));
        Assert.Equal("Gate B", pickup.UncoveredPoints.Single().Location); // not "Dismissal Duty was not covered"

        // A reason explains it; it never makes it covered. Edits and withdrawals keep every version.
        Ok(await after.ReportUncoveredAsync(new CoverageReportInput(day, DutyComponent.DismissalPickup, gateB, null, DutyUncoveredReason.StaffAbsent, "Called in sick", null)));
        Assert.False((await after.ReportUncoveredAsync(new CoverageReportInput(day, DutyComponent.DismissalPickup, gateB, null, DutyUncoveredReason.Other, null, null))).Succeeded); // one current report per point
        pickup = (await after.DayAsync(day)).Get(DutyType.Dismissal).Subs.Single(s => s.Info.Component == DutyComponent.DismissalPickup);
        var report = pickup.UncoveredPoints.Single().Report!;
        Assert.Equal((DutyStatus.NotCovered, "Gate B", DutyUncoveredReason.StaffAbsent, "Test runner"), (pickup.Status, report.Area, report.Reason, report.RecordedBy));
        Ok(await after.ReportUncoveredAsync(new CoverageReportInput(day, DutyComponent.DismissalPickup, gateB, null, DutyUncoveredReason.NoReplacement, "No one free", report.Id)));
        var history = await after.ReportHistoryAsync(day, DutyComponent.DismissalPickup);
        Assert.Equal(2, history.Count);
        Assert.Equal(DutyUncoveredReason.NoReplacement, history.Single(h => h.Current).Reason);

        // A report on an area of a covered Sub-Duty makes it Not Covered (the area was not covered).
        Ok(await after.ReportUncoveredAsync(new CoverageReportInput(day, DutyComponent.DismissalBus, null, "Bus 4 bay", DutyUncoveredReason.AreaUnattended, null, null)));
        var bus = (await after.DayAsync(day)).Get(DutyType.Dismissal).Subs.Single(s => s.Info.Component == DutyComponent.DismissalBus);
        Assert.Equal((DutyStatus.NotCovered, "Bus 4 bay"), (bus.Status, bus.AreaReports.Single().Area));
        Ok(await after.WithdrawReportAsync(bus.AreaReports.Single().Id));
        Assert.Equal(DutyStatus.Covered, (await after.DayAsync(day)).Get(DutyType.Dismissal).Subs.Single(s => s.Info.Component == DutyComponent.DismissalBus).Status);
        Assert.Equal(2, (await after.ReportHistoryAsync(day, DutyComponent.DismissalBus)).Count); // the report and its withdrawal

        // Nothing can be reported for a day that has not happened.
        Assert.False((await DbFixture.DutyBoard(db, CairoAt(day, 9, 0)).ReportUncoveredAsync(new CoverageReportInput(day.AddDays(1), DutyComponent.DismissalBus, null, "x", DutyUncoveredReason.Other, null, null))).Succeeded);
    }

    // ------------------------------------------------------------------ schedules, history, validation

    [Fact]
    public async Task Schedule_changes_apply_from_a_date_and_earlier_days_keep_theirs()
    {
        var first = Day.AddDays(7); // Sunday
        var later = first.AddDays(7);
        await using var db = fx.NewContext();
        var board = DbFixture.DutyBoard(db, CairoAt(first, 6, 0));
        Ok(await board.SaveMainScheduleAsync(DutyType.Midday, first, new TimeOnly(10, 0), new TimeOnly(13, 0), 0));
        Ok(await board.SaveSectionAsync(DutyComponent.Kg2Break, Sub(first, 10, 0, 10, 20)));

        // Validation: end before start, outside the duty's range, a past effective date.
        Assert.False((await board.SaveSectionAsync(DutyComponent.Kg2Break, Sub(first, 10, 30, 10, 20))).Succeeded);
        Assert.False((await board.SaveSectionAsync(DutyComponent.Kg2Break, Sub(first, 9, 30, 10, 20))).Succeeded);
        Assert.False((await board.SaveSectionAsync(DutyComponent.Kg2Break, Sub(first.AddDays(-1), 10, 0, 10, 20))).Succeeded);
        Assert.False((await board.SaveMainScheduleAsync(DutyType.Midday, first, new TimeOnly(10, 30), new TimeOnly(13, 0), 0)).Succeeded); // KG2 would fall outside

        // A coverage point must fall within its Sub-Duty's time on that day.
        var (staff, _) = await fx.NewTeacherAsync();
        Assert.False((await DbFixture.Duties(db).CreateAsync(new DutyInput(DutyComponent.Kg2Break, first, "Late", new TimeOnly(10, 10), new TimeOnly(10, 40), null, staff, AttendanceStatus.Present, null, null, []))).Succeeded);
        await PointAsync(db, DutyComponent.Kg2Break, first, 10, 0, 10, 20, AttendanceStatus.Present, "KG yard");

        // From `later` the break moves to 12:00–12:30 and becomes optional; the first day keeps 10:00–10:20 and its result.
        Ok(await DbFixture.DutyBoard(db, CairoAt(first, 14, 0)).SaveSectionAsync(DutyComponent.Kg2Break, Sub(later, 12, 0, 12, 30, required: false)));
        var then = (await DbFixture.DutyBoard(db, CairoAt(later, 14, 0)).DayAsync(first)).Get(DutyType.Midday).Subs.Single(s => s.Info.Component == DutyComponent.Kg2Break);
        Assert.Equal(("10:00–10:20 AM", true, DutyStatus.Covered), (then.Times, then.Required, then.Status));
        var next = (await DbFixture.DutyBoard(db, CairoAt(later, 8, 0)).DayAsync(later)).Get(DutyType.Midday).Subs.Single(s => s.Info.Component == DutyComponent.Kg2Break);
        Assert.Equal(("12:00–12:30 PM", false, DutyStatus.Pending), (next.Times, next.Required, next.Status));
        Assert.Equal(2, (await board.ScheduleAsync(DutyType.Midday)).Sections.Single(s => s.Info.Component == DutyComponent.Kg2Break).History.Count);
    }

    [Fact]
    public async Task Sub_duties_can_be_added_and_removed_without_losing_records_and_departments_are_dynamic()
    {
        var day = Day.AddDays(2); // Tuesday
        await using var db = fx.NewContext();
        var board = DbFixture.DutyBoard(db, CairoAt(day, 6, 0));
        Ok(await board.SaveMainScheduleAsync(DutyType.Morning, day, new TimeOnly(7, 0), new TimeOnly(8, 0), 0));
        var added = await board.AddSectionAsync(DutyType.Morning, Sub(day, 7, 0, 7, 30, dept: 3, name: "Car park " + DbFixture.Unique()));
        Ok(added);
        var code = (DutyComponent)added.EntityId!.Value;
        Assert.True(DutyComponents.Exists(code));
        await PointAsync(db, code, day, 7, 0, 7, 30, AttendanceStatus.NotRecorded, "Car park");

        // Departments: the department named in the schedule, with its status from its required responsibilities.
        var depts = await DbFixture.DutyBoard(db, CairoAt(day, 9, 0)).DepartmentsAsync(day);
        var math = depts.Single(d => d.DepartmentId == 3);
        Assert.Equal((DutyStatus.NotCovered, code), (math.Status, math.Subs.Single().Sub.Info.Component));

        // Removing it from the next day keeps the day's records and result; it is not scheduled afterwards.
        Assert.False((await DbFixture.DutyBoard(db, CairoAt(day, 9, 0)).RetireSectionAsync(code, day)).Succeeded); // has records that day
        Ok(await DbFixture.DutyBoard(db, CairoAt(day, 9, 0)).RetireSectionAsync(code, day.AddDays(1)));
        var kept = (await DbFixture.DutyBoard(db, CairoAt(day, 9, 0)).DayAsync(day)).Get(DutyType.Morning).Subs.Single(s => s.Info.Component == code);
        Assert.Equal((true, DutyStatus.NotCovered), (kept.Scheduled, kept.Status));
        Assert.DoesNotContain((await DbFixture.DutyBoard(db, CairoAt(day, 9, 0)).DayAsync(day.AddDays(1))).Get(DutyType.Morning).Subs, s => s.Info.Component == code);
    }

    // ------------------------------------------------------------------ pages

    [Fact]
    public async Task Pages_show_the_schedule_status_and_coverage_report()
    {
        var client = fx.Factory.CreateClient();
        foreach (var url in new[] { "/duties", "/duties/board", "/duties?type=morning", "/duties?type=dismissal&date=2024-05-14", "/duties/morning/students-arrival",
                     "/duties/morning/schedule", "/duties/departments", "/duties/sections/new?type=midday", "/duties/sections/1/edit",
                     "/duties/report-uncovered?component=students-arrival" })
        {
            var r = await client.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} → {(int)r.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.OK, (await (await fx.DutyAuditClientAsync()).GetAsync("/duties/audit")).StatusCode);
        var hub = WebUtility.HtmlDecode(await client.GetStringAsync("/duties/board"));
        foreach (var part in new[] { "Morning Duty", "Midday Duty", "Dismissal Duty", "Activities", "data-duty-live" }) Assert.Contains(part, hub);
        var morning = WebUtility.HtmlDecode(await client.GetStringAsync("/duties?type=morning"));
        Assert.Contains("Coverage", morning);
        Assert.DoesNotContain("status-pill", await client.GetStringAsync("/duties/morning/students-arrival")); // no manual status
        var pdf = await client.GetAsync("/duties/morning/report.pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
    }

    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
}
