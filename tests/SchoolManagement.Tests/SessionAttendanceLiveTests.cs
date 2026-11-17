using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Session Attendance driven by the timetable and the school clock (Africa/Cairo): the lesson in progress is detected,
/// one click records Present / Late / Absent, School Attendance decides who can be marked (school-absent students are locked,
/// a missing school record is Not Recorded — never absent), "present at school, absent from the lesson" raises one alert at
/// once and corrections clear it, and the reports keep school absences apart from lesson absences.
/// Each test uses its own class with class-only timed periods, on 2025 dates no other test uses.
/// </summary>
[Collection("db")]
public partial class SessionAttendanceLiveTests(DbFixture fx) : IAsyncLifetime
{
    private readonly List<Setup> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Removes this test's timetable fixtures from the test database (its lessons, their attendance, alerts and the class-only
    /// periods), so other tests' timetable views and exports never see them.
    /// </summary>
    public async Task DisposeAsync()
    {
        await using var db = fx.NewContext();
        foreach (var c in _created)
        {
            int[] lessons = [c.Math, c.English, c.Science];
            await db.AttendanceAlertEvents.Where(e => e.Alert.TimetableAssignmentId != null && lessons.Contains(e.Alert.TimetableAssignmentId.Value)).ExecuteDeleteAsync();
            await db.AttendanceAlerts.Where(a => a.TimetableAssignmentId != null && lessons.Contains(a.TimetableAssignmentId.Value)).ExecuteDeleteAsync();
            await db.SessionAttendanceCorrections.Where(x => lessons.Contains(x.Record.TimetableAssignmentId)).ExecuteDeleteAsync();
            await db.SessionAttendance.Where(x => lessons.Contains(x.TimetableAssignmentId)).ExecuteDeleteAsync();
            await db.SessionCancellations.Where(x => lessons.Contains(x.TimetableAssignmentId)).ExecuteDeleteAsync();
            await db.TimetableAssignments.Where(t => lessons.Contains(t.Id)).ExecuteDeleteAsync();
            await db.TimeSlots.Where(t => t.SchoolClassId == c.ClassId).ExecuteDeleteAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    /// <summary>A school clock stopped at a Cairo wall time (the UTC offset from the time zone rules: +2 in winter, +3 in summer).</summary>
    private static SchoolTimeService CairoAt(DateOnly day, int h, int m)
    {
        var local = day.ToDateTime(new TimeOnly(h, m));
        var offset = TimeZoneInfo.FindSystemTimeZoneById(SchoolTimeService.DefaultTimeZoneId).GetUtcOffset(local);
        return new(new FixedClock(new DateTimeOffset(local, offset)), SchoolTimeService.DefaultTimeZoneId);
    }

    private static readonly DateOnly Winter = new(2025, 2, 10), Summer = new(2025, 7, 14); // Mondays

    private sealed record Setup(int ClassId, string ClassName, int[] Students, int Math, int English, int Science, string MathTeacher);

    /// <summary>
    /// A class with three students and its own periods: P1 08:00–08:45 Math, P2 08:50–09:35 English, P3 10:00–10:45 Science (Mondays).
    /// </summary>
    private async Task<Setup> ClassAsync(DateOnly day)
    {
        var classId = await fx.NewClassAsync();
        var students = new List<int>();
        for (var i = 0; i < 3; i++) students.Add((await fx.NewStudentAsync(classId)).Id);
        var (mathTeacher, mathName) = await fx.NewTeacherAsync();
        var (otherTeacher, _) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        await db.Students.Where(s => students.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var department = await db.Departments.Select(d => d.Id).FirstAsync();
        var slots = new[] { (1, new TimeOnly(8, 0), new TimeOnly(8, 45)), (2, new TimeOnly(8, 50), new TimeOnly(9, 35)), (3, new TimeOnly(10, 0), new TimeOnly(10, 45)) }
            // Class-only periods replacing school periods 1–3 for this class (as Timetables → Periods stores them).
            .Select(x => new TimeSlot { Number = x.Item1, Name = $"Period {x.Item1}", StartTime = x.Item2, EndTime = x.Item3, IsActive = true, SchoolClassId = classId, ReplacesTimeSlotId = x.Item1 })
            .ToList();
        db.TimeSlots.AddRange(slots);
        await db.SaveChangesAsync();
        TimetableAssignment Lesson(TimeSlot slot, string subject, int teacher) => new()
        {
            SchoolClassId = classId, Day = DayOfWeek.Monday, TimeSlotId = slot.Id, DepartmentId = department, Subject = subject,
            TeacherId = teacher, StartsOn = SchoolWeek.StartOf(day), CreatedBy = "Test",
        };
        var lessons = new[] { Lesson(slots[0], "Math", mathTeacher), Lesson(slots[1], "English", otherTeacher), Lesson(slots[2], "Science", otherTeacher) };
        db.TimetableAssignments.AddRange(lessons);
        await db.SaveChangesAsync();
        var name = await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync();
        var setup = new Setup(classId, name, students.ToArray(), lessons[0].Id, lessons[1].Id, lessons[2].Id, mathName);
        _created.Add(setup);
        return setup;
    }

    private static async Task SchoolAsync(SchoolDbContext db, DateOnly day, params ArrivalEntry[] entries)
    {
        var results = await DbFixture.SchoolAttendance(db).RecordAsync(day, entries);
        Assert.All(results, r => Assert.True(r.Saved, r.Error));
    }

    private static ArrivalEntry AtSchool(int id, int h = 7, int m = 30) => new(id, ArrivalStatus.Present, new TimeOnly(h, m), null, null, null, false, null);
    private static ArrivalEntry SchoolAbsent(int id) => new(id, ArrivalStatus.Absent, null, null, null, null, false, null);

    // ------------------------------------------------------------------ 1, 2, 12, 13 — automatic lesson detection

    [Theory]
    [InlineData(2025, 2, 10)] // winter, UTC+02:00
    [InlineData(2025, 7, 14)] // summer (daylight saving), UTC+03:00
    public async Task The_lesson_in_progress_is_found_from_the_timetable_and_the_Cairo_clock(int y, int mo, int d)
    {
        var day = new DateOnly(y, mo, d);
        var c = await ClassAsync(day);
        await using var db = fx.NewContext();

        // Before school: nothing in progress, the next lesson is Math at 8:00.
        var early = (await DbFixture.SessionsAt(db, CairoAt(day, 7, 0)).ClassDayAsync(c.ClassId, day, null))!;
        Assert.Null(early.Current);
        Assert.Equal(c.Math, early.Next!.Lesson.SessionId);
        Assert.All(early.Lessons, l => Assert.Equal(LessonPhase.Upcoming, l.Phase));

        // 8:20: Math is in progress — period, subject, teacher and times from the timetable.
        var during = (await DbFixture.SessionsAt(db, CairoAt(day, 8, 20)).ClassDayAsync(c.ClassId, day, null))!;
        var now = during.Current!.Lesson;
        Assert.Equal((c.Math, "Math", "Period 1", "8:00–8:45 AM", c.MathTeacher), (now.SessionId, now.Subject, now.Period, now.Times12, now.Teacher));
        Assert.Equal(during.Current, during.Selected); // opens by itself
        Assert.Equal([LessonPhase.InProgress, LessonPhase.Upcoming, LessonPhase.Upcoming], during.Lessons.Select(l => l.Phase));
        Assert.Equal(c.English, during.Next!.Lesson.SessionId);

        // 8:45 exactly: Math has ended; 9:50 (break): nothing in progress, next Science at 10:00.
        Assert.Equal(LessonPhase.Finished, (await DbFixture.SessionsAt(db, CairoAt(day, 8, 45)).ClassDayAsync(c.ClassId, day, null))!.Lessons[0].Phase);
        var gap = (await DbFixture.SessionsAt(db, CairoAt(day, 9, 50)).ClassDayAsync(c.ClassId, day, null))!;
        Assert.Null(gap.Current);
        Assert.Equal(("Science", new TimeOnly(10, 0)), (gap.Next!.Lesson.Subject, gap.Next.Lesson.Start!.Value));

        // After school: everything finished, no next lesson; a finished lesson can still be opened.
        var after = (await DbFixture.SessionsAt(db, CairoAt(day, 13, 0)).ClassDayAsync(c.ClassId, day, c.English))!;
        Assert.Equal((null, null), (after.Current, after.Next));
        Assert.Equal(c.English, after.Selected!.Lesson.SessionId);
        Assert.All(after.Lessons, l => Assert.Equal(LessonPhase.Finished, l.Phase));

        // Never an unscheduled lesson: another weekday has none, and a lesson of another day cannot be opened.
        Assert.Empty((await DbFixture.SessionsAt(db, CairoAt(day.AddDays(1), 9, 0)).ClassDayAsync(c.ClassId, day.AddDays(1), null))!.Lessons);
        Assert.Null((await DbFixture.SessionsAt(db, CairoAt(day.AddDays(1), 9, 0)).ClassDayAsync(c.ClassId, day.AddDays(1), c.Math))!.Selected);
    }

    // ------------------------------------------------------------------ 3–9 — one click, School Attendance, alerts

    [Fact]
    public async Task One_click_marks_follow_school_attendance_and_raise_one_alert_that_corrections_clear()
    {
        var day = Winter;
        var c = await ClassAsync(day);
        var (present, absentFromSchool, unrecorded) = (c.Students[0], c.Students[1], c.Students[2]);
        await using var db = fx.NewContext();
        await SchoolAsync(db, day, AtSchool(present), SchoolAbsent(absentFromSchool));
        var at0820 = DbFixture.SessionsAt(db, CairoAt(day, 8, 20));

        // Register: the school status decides the controls.
        var register = (await at0820.LiveRegisterAsync(c.Math, day))!;
        var rows = register.Rows.ToDictionary(r => r.StudentId);
        Assert.Equal((SchoolPresence.Present, false), (rows[present].School.Presence, rows[present].Locked));
        Assert.Equal((SchoolPresence.Absent, true), (rows[absentFromSchool].School.Presence, rows[absentFromSchool].Locked));
        Assert.Equal((SchoolPresence.NotRecorded, false), (rows[unrecorded].School.Presence, rows[unrecorded].Locked)); // never assumed absent
        Assert.Equal((3, 1, 2), (register.Counts.Students, register.Counts.SchoolAbsent, register.Counts.NotRecorded));

        // Present, then Late (a correction with before / after).
        var p = await at0820.MarkAsync(c.Math, day, present, SessionAttendanceStatus.Present);
        Assert.True(p.Saved, p.Message);
        Assert.Equal((SessionAttendanceStatus.Present, 1), (p.Row!.Status!.Value, p.Counts!.Present));
        var l = await at0820.MarkAsync(c.Math, day, present, SessionAttendanceStatus.Late);
        Assert.Equal((SessionAttendanceStatus.Late, 1, 0), (l.Row!.Status!.Value, l.Counts!.Late, l.Counts.Present));
        Assert.Equal(1, await db.SessionAttendanceCorrections.CountAsync(x => x.Record.StudentId == present));
        Assert.Null(l.AlertText);

        // Absent while at school: the alert is raised with the save, naming the lesson.
        var a = await at0820.MarkAsync(c.Math, day, present, SessionAttendanceStatus.Absent);
        Assert.True(a.Saved, a.Message);
        Assert.NotNull(a.AlertText);
        var studentName = rows[present].Name;
        Assert.Contains($"{studentName} was recorded as Present at School on Mon 10 Feb 2025 (arrived 7:30 AM) but was marked Absent in Math — {c.ClassName}, Period 1 (8:00–8:45 AM), teacher {c.MathTeacher}.", a.AlertText);
        Assert.True(a.Row!.Alert);
        Assert.Equal(1, await LessonAlertsAsync(db, present, c.Math, day));

        // No duplicate: the same click again changes nothing.
        var again = await at0820.MarkAsync(c.Math, day, present, SessionAttendanceStatus.Absent);
        Assert.True(again.Saved);
        Assert.Equal(1, await LessonAlertsAsync(db, present, c.Math, day));
        Assert.Equal(1, await db.SessionAttendance.CountAsync(x => x.StudentId == present && x.TimetableAssignmentId == c.Math && x.SessionDate == day));

        // Correction to Present clears the alert (history kept); Absent again reopens the same alert.
        Assert.True((await at0820.MarkAsync(c.Math, day, present, SessionAttendanceStatus.Present)).Saved);
        var alert = await db.AttendanceAlerts.AsNoTracking().Include(x => x.Events).SingleAsync(x => x.StudentId == present && x.TimetableAssignmentId == c.Math);
        Assert.Equal(AttendanceAlertStatus.Cleared, alert.Status);
        Assert.Contains(alert.Events, e => e.Kind == AttendanceAlertEventKind.Cleared);
        Assert.True((await at0820.MarkAsync(c.Math, day, present, SessionAttendanceStatus.Absent)).Saved);
        Assert.Equal(AttendanceAlertStatus.Open, (await db.AttendanceAlerts.AsNoTracking().SingleAsync(x => x.Id == alert.Id)).Status);
        Assert.Equal(1, await LessonAlertsAsync(db, present, c.Math, day));

        // School-absent: locked on the server too (one click and the batch save), no record, no alert.
        var locked = await at0820.MarkAsync(c.Math, day, absentFromSchool, SessionAttendanceStatus.Present);
        Assert.False(locked.Saved);
        Assert.Contains("absent from school", locked.Message);
        Assert.False((await at0820.SaveAsync(c.Math, day, [new SessionEntry(absentFromSchool, SessionAttendanceStatus.Absent, null)], null)).Succeeded);
        Assert.False(await db.SessionAttendance.AnyAsync(x => x.StudentId == absentFromSchool));

        // No school record: lesson attendance can be taken, but an absence raises no alert (not known to be at school).
        var u = await at0820.MarkAsync(c.Math, day, unrecorded, SessionAttendanceStatus.Absent);
        Assert.True(u.Saved, u.Message);
        Assert.Null(u.AlertText);
        Assert.Equal(0, await LessonAlertsAsync(db, unrecorded, c.Math, day));

        // A lesson that has not started (English at 8:50) is refused at 8:20; it opens at 8:50.
        var early = await at0820.MarkAsync(c.English, day, present, SessionAttendanceStatus.Present);
        Assert.False(early.Saved);
        Assert.Contains("has not started yet", early.Message);
        Assert.True((await DbFixture.SessionsAt(db, CairoAt(day, 8, 50)).MarkAsync(c.English, day, present, SessionAttendanceStatus.Present)).Saved);

        // A lesson that is not on that day's timetable is refused.
        Assert.False((await DbFixture.SessionsAt(db, CairoAt(day.AddDays(1), 9, 0)).MarkAsync(c.Math, day.AddDays(1), present, SessionAttendanceStatus.Present)).Saved);
    }

    [Fact]
    public async Task After_the_0930_cutoff_a_student_with_no_arrival_is_School_Absent_and_locked()
    {
        var day = Summer;
        var c = await ClassAsync(day);
        await using var db = fx.NewContext();
        await SchoolAsync(db, day, AtSchool(c.Students[0]));
        var options = new AttendanceOptions(true, new DateOnly(2025, 1, 1));

        // 9:00: no school record yet — Not Recorded, not locked.
        var before = (await DbFixture.SessionsAt(db, CairoAt(day, 9, 0), options).LiveRegisterAsync(c.English, day))!;
        Assert.Equal(SchoolPresence.NotRecorded, before.Rows.Single(r => r.StudentId == c.Students[1]).School.Presence);

        // 10:10: past the cutoff the School Attendance rule makes them Absent — locked here too.
        var after = DbFixture.SessionsAt(db, CairoAt(day, 10, 10), options);
        var row = (await after.LiveRegisterAsync(c.Science, day))!.Rows.Single(r => r.StudentId == c.Students[1]);
        Assert.Equal((SchoolPresence.Absent, true, true), (row.School.Presence, row.School.Automatic, row.Locked));
        Assert.False((await after.MarkAsync(c.Science, day, c.Students[1], SessionAttendanceStatus.Present)).Saved);
        Assert.True((await after.MarkAsync(c.Science, day, c.Students[0], SessionAttendanceStatus.Present)).Saved);
    }

    private static Task<int> LessonAlertsAsync(SchoolDbContext db, int student, int session, DateOnly day) =>
        db.AttendanceAlerts.CountAsync(a => a.StudentId == student && a.Type == AttendanceAlertType.AbsentFromSession && a.TimetableAssignmentId == session && a.FirstDate == day);

    // ------------------------------------------------------------------ 10, 11 — history, reports, exports

    [Fact]
    public async Task Reports_keep_school_absence_apart_from_lesson_absence()
    {
        var day = new DateOnly(2025, 2, 17); // Monday
        var c = await ClassAsync(day);
        var (present, absentFromSchool, unrecorded) = (c.Students[0], c.Students[1], c.Students[2]);
        await using var db = fx.NewContext();
        await SchoolAsync(db, day, AtSchool(present), SchoolAbsent(absentFromSchool));
        var svc = DbFixture.SessionsAt(db, CairoAt(day, 12, 0));
        Assert.True((await svc.MarkAsync(c.Math, day, present, SessionAttendanceStatus.Absent)).Saved);
        Assert.True((await svc.MarkAsync(c.English, day, present, SessionAttendanceStatus.Absent)).Saved);
        Assert.True((await svc.MarkAsync(c.Science, day, present, SessionAttendanceStatus.Late)).Saved);
        Assert.True((await svc.MarkAsync(c.Math, day, unrecorded, SessionAttendanceStatus.Present)).Saved);

        var cls = await svc.ReportAsync(new SessionReportQuery("class", null, c.ClassId, null, day, day));
        Assert.Equal(3, cls.Lessons);
        // 9 student-lessons: present → 2 absent at school + 1 late; school-absent → 3 School Absent; unrecorded → 1 present + 2 not recorded.
        Assert.Equal((1, 1, 2, 2, 3, 2), (cls.Tally.Present, cls.Tally.Late, cls.Tally.Absent, cls.Tally.AbsentAtSchool, cls.Tally.SchoolAbsent, cls.Tally.NotRecorded));
        Assert.Equal(present, cls.Repeated.Single().StudentId); // two lesson absences while at school
        Assert.All(cls.Lines, x => Assert.Equal(SessionKind.Absent, x.Kind));

        var student = await svc.ReportAsync(new SessionReportQuery("student", absentFromSchool, null, null, day, day));
        Assert.Equal(3, student.Lines.Count);
        Assert.All(student.Lines, x => Assert.Equal(SessionKind.SchoolAbsent, x.Kind));

        var lesson = await svc.ReportAsync(new SessionReportQuery("lesson", null, c.ClassId, c.Math, day, day));
        Assert.Equal([SessionKind.Absent, SessionKind.SchoolAbsent, SessionKind.Present],
            c.Students.Select(id => lesson.Lines.Single(x => x.StudentId == id).Kind));

        var school = await svc.ReportAsync(new SessionReportQuery("school", null, null, null, day, day));
        Assert.Contains(school.ByClass, g => g.Label == c.ClassName && g.Tally.AbsentAtSchool == 2);

        // Pages and PDFs.
        var client = fx.Factory.CreateClient();
        foreach (var q in new[] { $"mode=student&studentId={present}&from=2025-02-17&to=2025-02-17", $"mode=class&classId={c.ClassId}&from=2025-02-17&to=2025-02-17",
                     $"mode=lesson&classId={c.ClassId}&lesson={c.Math}&date=2025-02-17", "mode=school&from=2025-02-17&to=2025-02-17" })
        {
            var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/attendance/sessions/reports?{q}"));
            Assert.Contains("School Absent", page);
            var pdf = await client.GetAsync($"/attendance/sessions/reports/pdf?{q}");
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        }
    }

    // ------------------------------------------------------------------ pages and the one-click endpoint

    [Fact]
    public async Task Pages_show_classes_the_lesson_panel_and_the_one_click_endpoint_saves_with_the_token()
    {
        var day = new DateOnly(2025, 2, 24); // Monday (past: every lesson finished)
        var c = await ClassAsync(day);
        await using (var db = fx.NewContext()) await SchoolAsync(db, day, AtSchool(c.Students[0]), SchoolAbsent(c.Students[1]));
        var client = fx.Factory.CreateClient();

        var picker = WebUtility.HtmlDecode(await client.GetStringAsync("/attendance/sessions?date=2025-02-24"));
        Assert.Contains($"/attendance/sessions/class/{c.ClassId}?date=2025-02-24", picker);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/attendance/sessions/class/{c.ClassId}?date=2025-02-24&lesson={c.Math}"));
        foreach (var part in new[] { "Math", "Period 1", "8:00–8:45 AM", c.MathTeacher, "Finished", "School Absent", "School: Not Recorded", "data-mark=\"Absent\"" })
            Assert.Contains(part, page);
        Assert.DoesNotContain("name=\"slotId\"", page); // nothing chosen by hand that the timetable gives
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        HttpRequestMessage Mark(int student, string status, bool withToken = true)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, $"/attendance/sessions/{c.Math}/mark")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["studentId"] = student.ToString(), ["status"] = status, ["date"] = "2025-02-24" }),
            };
            r.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (withToken) r.Headers.Add("RequestVerificationToken", token);
            return r;
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Mark(c.Students[0], "Absent", withToken: false))).StatusCode);
        var saved = await client.SendAsync(Mark(c.Students[0], "Absent"));
        var body = await saved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Contains("\"alerted\":true", body);
        Assert.Contains("was marked Absent in Math", body);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Mark(c.Students[1], "Present"))).StatusCode); // school absent: locked
    }
}
