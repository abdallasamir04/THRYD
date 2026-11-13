using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Two-level attendance: School Attendance (existing arrival records) + Session Attendance (lessons from the
/// class timetable), the 3-consecutive-school-day absence alert and the present-at-school / absent-from-lesson
/// alert. Each test uses its own class, students, teacher and dates (2025), so tests stay independent.
/// </summary>
[Collection("db")]
public class AttendanceTests(DbFixture fx)
{
    private const int MathDepartment = 3;

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    private static ArrivalEntry Present(int id, int h = 7, int m = 30) => new(id, ArrivalStatus.Present, new TimeOnly(h, m), ArrivalMethod.SchoolBus, null, null, false, null);
    private static ArrivalEntry Absent(int id) => new(id, ArrivalStatus.Absent, null, null, null, null, false, null);

    /// <summary>A class with students enrolled long before the test dates.</summary>
    private async Task<(int ClassId, int[] Students)> ClassWithStudentsAsync(int count)
    {
        var classId = await fx.NewClassAsync();
        var ids = new List<int>();
        for (var i = 0; i < count; i++) ids.Add((await fx.NewStudentAsync(classId)).Id);
        await using var db = fx.NewContext();
        await db.Students.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        return (classId, ids.ToArray());
    }

    /// <summary>A weekly timetable session taught since <paramref name="since"/> (inserted like the timetable stores it).</summary>
    private async Task<int> LessonAsync(int classId, DayOfWeek day, int slot, string subject, DateOnly since)
    {
        var teacher = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var row = new TimetableAssignment
        {
            SchoolClassId = classId, Day = day, TimeSlotId = slot, DepartmentId = MathDepartment, Subject = subject,
            TeacherId = teacher.Id, StartsOn = SchoolWeek.StartOf(since), CreatedBy = "Test",
        };
        db.TimetableAssignments.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task RecordSchoolAsync(DateOnly date, params ArrivalEntry[] entries)
    {
        await using var db = fx.NewContext();
        var results = await DbFixture.SchoolAttendance(db).RecordAsync(date, entries);
        Assert.All(results, r => Assert.True(r.Saved, r.Error));
    }

    private async Task<int> SchoolRecordIdAsync(int studentId, DateOnly date)
    {
        await using var db = fx.NewContext();
        return await db.StudentArrivals.Where(a => a.StudentId == studentId && a.ArrivalDate == date).Select(a => a.Id).SingleAsync();
    }

    private async Task CorrectSchoolAsync(int studentId, DateOnly date, ArrivalEntry entry)
    {
        var id = await SchoolRecordIdAsync(studentId, date);
        await using var db = fx.NewContext();
        var r = await DbFixture.SchoolAttendance(db).CorrectAsync(id, entry, "Test correction");
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
    }

    private async Task<List<AttendanceAlert>> AlertsAsync(int studentId)
    {
        await using var db = fx.NewContext();
        return await db.AttendanceAlerts.AsNoTracking().Include(a => a.Events).Where(a => a.StudentId == studentId).OrderBy(a => a.Id).ToListAsync();
    }

    private async Task<ServiceResult> SaveLessonAsync(int lesson, DateOnly date, string? reason, params SessionEntry[] entries)
    {
        await using var db = fx.NewContext();
        return await DbFixture.Sessions(db).SaveAsync(lesson, date, entries, reason);
    }

    // ================================================================== School Attendance + 3 consecutive absences

    [Fact]
    public async Task Three_consecutive_school_day_absences_raise_one_alert_that_follows_corrections()
    {
        var (_, s) = await ClassWithStudentsAsync(1);
        var st = s[0];
        await RecordSchoolAsync(D("2025-03-02"), Absent(st));
        await RecordSchoolAsync(D("2025-03-03"), Absent(st));
        Assert.Empty(await AlertsAsync(st)); // two days: no alert yet

        await RecordSchoolAsync(D("2025-03-04"), Absent(st));
        var alert = Assert.Single(await AlertsAsync(st));
        Assert.Equal((AttendanceAlertType.ConsecutiveAbsence, AttendanceAlertStatus.Open, 3), (alert.Type, alert.Status, alert.DayCount));
        Assert.Equal("2025-03-02,2025-03-03,2025-03-04", alert.AbsenceDates);

        // A fourth day extends the same alert (no duplicate).
        await RecordSchoolAsync(D("2025-03-05"), Absent(st));
        alert = Assert.Single(await AlertsAsync(st));
        Assert.Equal((4, D("2025-03-05")), (alert.DayCount, alert.LastDate));
        Assert.Contains(alert.Events, e => e.Kind == AttendanceAlertEventKind.Updated);

        // Correcting day 2 to Present leaves 1 + 2 absences: the alert is cleared, not deleted.
        await CorrectSchoolAsync(st, D("2025-03-03"), Present(st));
        alert = Assert.Single(await AlertsAsync(st));
        Assert.Equal(AttendanceAlertStatus.Cleared, alert.Status);
        Assert.Contains(alert.Events, e => e.Kind == AttendanceAlertEventKind.Cleared);

        // Corrected back: the same alert comes back.
        await CorrectSchoolAsync(st, D("2025-03-03"), Absent(st));
        alert = Assert.Single(await AlertsAsync(st));
        Assert.Equal((AttendanceAlertStatus.Open, 4), (alert.Status, alert.DayCount));
        Assert.Contains(alert.Events, e => e.Kind == AttendanceAlertEventKind.Reopened);

        // The existing correction audit still records every change.
        await using var db = fx.NewContext();
        Assert.Equal(2, await db.StudentArrivalCorrections.CountAsync(c => c.Record.StudentId == st));
    }

    [Fact]
    public async Task Unrecorded_days_are_not_absences_and_break_the_streak()
    {
        var (_, s) = await ClassWithStudentsAsync(2);
        // Sunday absent, Monday NOT recorded, Tuesday + Wednesday absent → never three in a row.
        await RecordSchoolAsync(D("2025-03-09"), Absent(s[0]));
        await RecordSchoolAsync(D("2025-03-11"), Absent(s[0]));
        await RecordSchoolAsync(D("2025-03-12"), Absent(s[0]));
        Assert.Empty(await AlertsAsync(s[0]));

        // A Present day in between breaks it too.
        await RecordSchoolAsync(D("2025-03-09"), Absent(s[1]));
        await RecordSchoolAsync(D("2025-03-10"), Present(s[1]));
        await RecordSchoolAsync(D("2025-03-11"), Absent(s[1]));
        await RecordSchoolAsync(D("2025-03-12"), Absent(s[1]));
        Assert.Empty(await AlertsAsync(s[1]));
    }

    [Fact]
    public async Task Weekends_and_school_holidays_are_skipped_when_counting()
    {
        var (_, s) = await ClassWithStudentsAsync(2);
        // Wednesday, Thursday, (Friday + Saturday weekend), Sunday.
        foreach (var d in new[] { "2025-03-19", "2025-03-20", "2025-03-23" }) await RecordSchoolAsync(D(d), Absent(s[0]));
        var weekend = Assert.Single(await AlertsAsync(s[0]));
        Assert.Equal("2025-03-19,2025-03-20,2025-03-23", weekend.AbsenceDates);

        // Thursday absent, Sunday is a holiday (nobody recorded), Monday + Tuesday absent.
        foreach (var d in new[] { "2025-04-03", "2025-04-07", "2025-04-08" }) await RecordSchoolAsync(D(d), Absent(s[1]));
        Assert.Empty(await AlertsAsync(s[1])); // the unrecorded Sunday breaks it while it is a school day

        int holidayId;
        await using (var db = fx.NewContext())
        {
            var add = await DbFixture.Calendar(db).AddHolidayAsync("Test holiday " + DbFixture.Unique(), D("2025-04-06"), null);
            Assert.True(add.Succeeded, string.Join("; ", add.Errors.Select(e => e.Value)));
            holidayId = add.EntityId!.Value;
            // Overlapping holidays are refused.
            Assert.False((await DbFixture.Calendar(db).AddHolidayAsync("Overlap", D("2025-04-05"), D("2025-04-06"))).Succeeded);
        }
        var holiday = Assert.Single(await AlertsAsync(s[1]));
        Assert.Equal(("2025-04-03,2025-04-07,2025-04-08", AttendanceAlertStatus.Open), (holiday.AbsenceDates, holiday.Status));

        await using (var db = fx.NewContext()) Assert.True((await DbFixture.Calendar(db).RemoveHolidayAsync(holidayId)).Succeeded);
        Assert.Equal(AttendanceAlertStatus.Cleared, Assert.Single(await AlertsAsync(s[1])).Status);
    }

    [Fact]
    public async Task Staff_follow_up_and_resolve_alerts_with_history()
    {
        var (_, s) = await ClassWithStudentsAsync(1);
        foreach (var d in new[] { "2025-02-02", "2025-02-03", "2025-02-04" }) await RecordSchoolAsync(D(d), Absent(s[0]));
        var id = Assert.Single(await AlertsAsync(s[0])).Id;

        await using (var db = fx.NewContext())
        {
            var svc = DbFixture.Alerts(db);
            Assert.False((await svc.FollowUpAsync(id, "  ")).Succeeded);           // a note is required
            Assert.True((await svc.FollowUpAsync(id, "Called the father: flu.")).Succeeded);
            Assert.True((await svc.ResolveAsync(id, "Back at school with a medical note.")).Succeeded);
            Assert.False((await svc.FollowUpAsync(id, "Late note")).Succeeded);   // closed
            var details = await svc.GetAsync(id);
            Assert.Equal(AttendanceAlertStatus.Resolved, details!.Alert.Status);
            Assert.Equal([AttendanceAlertEventKind.Resolved, AttendanceAlertEventKind.FollowUp, AttendanceAlertEventKind.Created], details.Events.Select(e => e.Kind));
            Assert.Contains(details.Alert.ClassName, (await svc.ListAsync(new AlertFilter(State: "resolved"))).Where(a => a.Id == id).Select(a => a.ClassName));
        }

        // A resolved streak that continues is reopened (the same alert).
        await RecordSchoolAsync(D("2025-02-05"), Absent(s[0]));
        var alert = Assert.Single(await AlertsAsync(s[0]));
        Assert.Equal((AttendanceAlertStatus.Open, 4), (alert.Status, alert.DayCount));
    }

    // ================================================================== Session Attendance from the timetable

    [Fact]
    public async Task Lessons_come_from_the_class_timetable_and_missing_records_stay_pending()
    {
        var (classId, s) = await ClassWithStudentsAsync(3);
        var monday = D("2025-05-05");
        var math = await LessonAsync(classId, DayOfWeek.Monday, 2, "Math", monday);
        await LessonAsync(classId, DayOfWeek.Tuesday, 2, "Science", monday); // another weekday: not on Monday

        await using var db = fx.NewContext();
        var day = await DbFixture.Sessions(db).DayAsync(new SessionFilter(monday, ClassId: classId));
        var row = Assert.Single(day.Lessons);
        Assert.Equal((math, "Math", classId, DayOfWeek.Monday), (row.Lesson.SessionId, row.Lesson.Subject, row.Lesson.ClassId, row.Lesson.Date.DayOfWeek));
        Assert.StartsWith("Teacher ", row.Lesson.TeacherName);
        Assert.Equal((3, 0, 3, 0), (row.Counts.Expected, row.Counts.Recorded, row.Counts.Pending, row.Counts.Absent)); // not recorded ≠ absent

        Assert.True((await SaveLessonAsync(math, monday, null, new SessionEntry(s[0], SessionAttendanceStatus.Present, null),
            new SessionEntry(s[1], SessionAttendanceStatus.Late, "Bus"), new SessionEntry(s[2], null, null))).Succeeded);
        var register = (await DbFixture.Sessions(db).RegisterAsync(math, monday))!;
        Assert.Equal((3, 2, 1, 1, 1), (register.Counts.Expected, register.Counts.Recorded, register.Counts.Pending, register.Counts.Present, register.Counts.Late));
        Assert.Null(register.Rows.Single(r => r.StudentId == s[2]).Status); // still Not recorded
        Assert.Equal(100, register.Counts.Percent); // present + late, nobody absent

        // Weekends and holidays have no lessons; a lesson cannot be recorded on another weekday.
        Assert.Empty((await DbFixture.Sessions(db).DayAsync(new SessionFilter(D("2025-05-09"), ClassId: classId))).Lessons);
        Assert.False((await SaveLessonAsync(math, D("2025-05-06"), null, new SessionEntry(s[0], SessionAttendanceStatus.Present, null))).Succeeded);

        // Future lessons cannot be recorded.
        var future = SchoolWeek.Today.AddDays(7 + ((int)DayOfWeek.Monday - (int)SchoolWeek.Today.DayOfWeek + 7) % 7);
        var refused = await SaveLessonAsync(math, future, null, new SessionEntry(s[0], SessionAttendanceStatus.Present, null));
        Assert.Contains(refused.Errors, e => e.Value.Contains("future"));
    }

    [Fact]
    public async Task Changes_to_lesson_records_are_kept_as_corrections()
    {
        var (classId, s) = await ClassWithStudentsAsync(1);
        var monday = D("2025-05-12");
        var lesson = await LessonAsync(classId, DayOfWeek.Monday, 3, "Math", monday);
        Assert.True((await SaveLessonAsync(lesson, monday, null, new SessionEntry(s[0], SessionAttendanceStatus.Absent, null))).Succeeded);
        Assert.False((await SaveLessonAsync(lesson, monday, null, new SessionEntry(s[0], SessionAttendanceStatus.Absent, null))).Succeeded); // nothing changed
        Assert.True((await SaveLessonAsync(lesson, monday, "Came from the clinic", new SessionEntry(s[0], SessionAttendanceStatus.Late, "10:05"))).Succeeded);

        await using var db = fx.NewContext();
        var record = await db.SessionAttendance.Include(r => r.Corrections).SingleAsync(r => r.StudentId == s[0]);
        Assert.Equal(SessionAttendanceStatus.Late, record.Status);
        var c = Assert.Single(record.Corrections);
        Assert.Equal(("Absent", "Late · notes: 10:05", "Came from the clinic"), (c.Before, c.After, c.Reason));

        // Unique per student, lesson and date at the database level too.
        db.SessionAttendance.Add(new SessionAttendanceRecord { StudentId = s[0], TimetableAssignmentId = lesson, SessionDate = monday, SchoolClassId = classId, Status = SessionAttendanceStatus.Present });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ================================================================== Present at school, absent from a lesson

    [Fact]
    public async Task Present_at_school_but_absent_from_a_lesson_raises_one_alert_and_follows_corrections()
    {
        var (classId, s) = await ClassWithStudentsAsync(3);
        var day = D("2025-06-02");
        var math = await LessonAsync(classId, DayOfWeek.Monday, 1, "Math", day);
        var english = await LessonAsync(classId, DayOfWeek.Monday, 2, "English", day);
        var science = await LessonAsync(classId, DayOfWeek.Monday, 3, "Science", day);
        var (present, absentFromSchool, notRecorded) = (s[0], s[1], s[2]);
        await RecordSchoolAsync(day, Present(present), Absent(absentFromSchool));

        // The example: school Present, Math Present, English Absent, Science Present.
        Assert.True((await SaveLessonAsync(math, day, null, new SessionEntry(present, SessionAttendanceStatus.Present, null))).Succeeded);
        Assert.True((await SaveLessonAsync(english, day, null,
            new SessionEntry(present, SessionAttendanceStatus.Absent, null),
            new SessionEntry(absentFromSchool, SessionAttendanceStatus.Absent, null),    // absent from school → no lesson alert
            new SessionEntry(notRecorded, SessionAttendanceStatus.Absent, null))).Succeeded); // school not recorded → no alert
        Assert.True((await SaveLessonAsync(science, day, null, new SessionEntry(present, SessionAttendanceStatus.Present, null))).Succeeded);

        var alert = Assert.Single(await AlertsAsync(present));
        Assert.Equal((AttendanceAlertType.AbsentFromSession, english, day, AttendanceAlertStatus.Open), (alert.Type, alert.TimetableAssignmentId, alert.FirstDate, alert.Status));
        Assert.Empty(await AlertsAsync(absentFromSchool));
        Assert.Empty(await AlertsAsync(notRecorded));

        await using (var db = fx.NewContext())
        {
            var row = (await DbFixture.Alerts(db).GetAsync(alert.Id))!.Alert;
            Assert.Equal(("English", 1, classId), (row.Subject, row.DayCount, row.ClassId));
            Assert.Equal(2, row.SlotNumber);
            Assert.StartsWith("Teacher ", row.TeacherName);
        }

        // Saving again does not duplicate; correcting the lesson to Present clears it, Absent again reopens it.
        Assert.True((await SaveLessonAsync(english, day, null, new SessionEntry(present, SessionAttendanceStatus.Present, null))).Succeeded);
        Assert.Equal(AttendanceAlertStatus.Cleared, Assert.Single(await AlertsAsync(present)).Status);
        Assert.True((await SaveLessonAsync(english, day, null, new SessionEntry(present, SessionAttendanceStatus.Absent, null))).Succeeded);
        Assert.Equal(AttendanceAlertStatus.Open, Assert.Single(await AlertsAsync(present)).Status);

        // School attendance corrected to Absent → no longer "present at school": cleared.
        await CorrectSchoolAsync(present, day, Absent(present));
        Assert.Equal(AttendanceAlertStatus.Cleared, Assert.Single(await AlertsAsync(present)).Status);
        await CorrectSchoolAsync(present, day, Present(present));
        Assert.Equal(AttendanceAlertStatus.Open, Assert.Single(await AlertsAsync(present)).Status);

        // A cancelled lesson is not expected: its alert is cleared, attendance cannot be recorded, restore brings it back.
        await using (var db = fx.NewContext())
        {
            var svc = DbFixture.Sessions(db);
            Assert.False((await svc.CancelAsync(english, day, " ")).Succeeded); // reason required
            Assert.True((await svc.CancelAsync(english, day, "School trip")).Succeeded);
        }
        Assert.Equal(AttendanceAlertStatus.Cleared, Assert.Single(await AlertsAsync(present)).Status);
        Assert.Contains((await SaveLessonAsync(english, day, null, new SessionEntry(present, SessionAttendanceStatus.Late, null))).Errors, e => e.Value.Contains("cancelled"));
        await using (var db = fx.NewContext())
        {
            var dayView = await DbFixture.Sessions(db).DayAsync(new SessionFilter(day, ClassId: classId));
            Assert.True(dayView.Lessons.Single(l => l.Lesson.SessionId == english).Lesson.Cancelled);
            Assert.Equal(2 * 3, dayView.Totals.Expected); // the cancelled lesson is not expected
            Assert.True((await DbFixture.Sessions(db).RestoreAsync(english, day)).Succeeded);
        }
        var back = Assert.Single(await AlertsAsync(present));
        Assert.Equal(AttendanceAlertStatus.Open, back.Status);

        // A full re-check changes nothing and never duplicates.
        await using (var db = fx.NewContext()) await DbFixture.Alerts(db).RecheckAsync(D("2025-06-01"), D("2025-06-05"));
        Assert.Single(await AlertsAsync(present));
        await using (var db = fx.NewContext())
            Assert.Equal(1, await db.AttendanceAlerts.CountAsync(a => a.Key == back.Key));
    }

    [Fact]
    public async Task A_student_with_lesson_records_is_not_deleted()
    {
        var (classId, s) = await ClassWithStudentsAsync(1);
        var day = D("2025-06-09");
        var lesson = await LessonAsync(classId, DayOfWeek.Monday, 4, "Math", day);
        Assert.True((await SaveLessonAsync(lesson, day, null, new SessionEntry(s[0], SessionAttendanceStatus.Present, null))).Succeeded);
        await using var db = fx.NewContext();
        var r = await DbFixture.Students(db).DeleteAsync(s[0]);
        Assert.False(r.Succeeded);
        Assert.Contains("lesson attendance", r.Errors.Single().Value);
    }

    // ================================================================== pages

    [Fact]
    public async Task Attendance_pages_show_both_sections_registers_and_alerts()
    {
        var (classId, s) = await ClassWithStudentsAsync(2);
        var day = D("2025-06-16");
        var lesson = await LessonAsync(classId, DayOfWeek.Monday, 5, "Math", day);
        await RecordSchoolAsync(day, Present(s[0]));
        var client = fx.Factory.CreateClient();

        var hub = WebUtility.HtmlDecode(await client.GetStringAsync("/attendance?date=2025-06-16"));
        Assert.Equal(2, Regex.Matches(hub, "class=\"hub-card ").Count);
        Assert.Contains("href=\"/arrivals?date=2025-06-16\"", hub);
        Assert.Contains("href=\"/attendance/sessions?date=2025-06-16\"", hub);
        foreach (var label in new[] { "Total students", "Not recorded", "Expected sessions", "Pending", "3 consecutive absences · pending" }) Assert.Contains(label, hub);

        var list = WebUtility.HtmlDecode(await client.GetStringAsync($"/attendance/sessions?date=2025-06-16&classId={classId}"));
        Assert.Contains($"/attendance/sessions/{lesson}?date=2025-06-16", list);
        foreach (var filter in new[] { "name=\"classId\"", "name=\"slotId\"", "name=\"subject\"", "name=\"teacherId\"", "name=\"studentId\"" }) Assert.Contains(filter, list);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/attendance/sessions/{lesson}?date=2025-06-16"));
        foreach (var status in new[] { "value=\"Present\"", "value=\"Late\"", "value=\"Absent\"", "value=\"Excused\"" }) Assert.Contains(status, page);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        var req = new HttpRequestMessage(HttpMethod.Post, $"/attendance/sessions/{lesson}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["Date"] = "2025-06-16",
                ["Entries[0].StudentId"] = s[0].ToString(), ["Entries[0].Status"] = "Absent",
                ["Entries[1].StudentId"] = s[1].ToString(),
            }),
        };
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var saved = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Contains("Lesson attendance saved (1 student)", await saved.Content.ReadAsStringAsync());

        var alerts = WebUtility.HtmlDecode(await client.GetStringAsync($"/attendance/alerts?type=lessons&classId={classId}"));
        Assert.Contains("Present at school, absent from Math", alerts);
        var alertId = (await AlertsAsync(s[0])).Single().Id;
        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/attendance/alerts/{alertId}"));
        foreach (var part in new[] { "Contact", "Teacher", "Add follow-up", "Resolve" }) Assert.Contains(part, details);

        Assert.Contains("School holidays", await client.GetStringAsync("/attendance/holidays"));
        var school = await client.GetStringAsync("/arrivals?date=2025-06-16");
        Assert.Contains("data-arrival-table", school); // the existing checklist is unchanged
    }
}
