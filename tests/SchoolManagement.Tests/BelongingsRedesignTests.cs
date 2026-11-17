using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using UglyToad.PdfPig;

namespace SchoolManagement.Tests;

/// <summary>
/// Belongings redesign: Personal Belongings (class → students → five items per school day) and Academic Belongings (class →
/// the lesson in progress from the timetable → Book and Copy Book), their corrections, History &amp; Reports filters and the
/// PDF / Excel reports. Service tests run on a fixed Cairo clock in a 2035 quarter; HTTP tests in a 2026 quarter (real clock).
/// </summary>
[Collection("db")]
public sealed class BelongingsRedesignTests(DbFixture fx) : IAsyncLifetime
{
    private static readonly DateOnly QStart = new(2035, 1, 7), QEnd = new(2035, 3, 1); // Sunday → Thursday of week 8
    private static readonly DateOnly Monday = new(2035, 1, 15);
    /// <summary>The Jacket "Missing" button of a row, pressed (attributes as the row partial writes them).</summary>
    private static readonly Regex JacketMissingPressed = new("data-item=\"Jacket\" data-status=\"Red\"\\s+aria-pressed=\"true\"");
    private readonly List<(int ClassId, int[] Lessons)> _timetables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Removes this test's lessons and class-only periods (and the checks on them) so other tests never see them.</summary>
    public async Task DisposeAsync()
    {
        await using var db = fx.NewContext();
        foreach (var (classId, lessons) in _timetables)
        {
            var rows = await db.AcademicMaterialRecords.Where(m => m.TimetableAssignmentId != null && lessons.Contains(m.TimetableAssignmentId.Value)).Select(m => m.Id).ToListAsync();
            await db.BelongingsChanges.Where(c => c.AcademicMaterialRecordId != null && rows.Contains(c.AcademicMaterialRecordId.Value)).ExecuteDeleteAsync();
            await db.AcademicMaterialRecords.Where(m => rows.Contains(m.Id)).ExecuteDeleteAsync();
            await db.TimetableAssignments.Where(t => lessons.Contains(t.Id)).ExecuteDeleteAsync();
            await db.TimeSlots.Where(t => t.SchoolClassId == classId).ExecuteDeleteAsync();
        }
    }

    // ------------------------------------------------------------------ helpers

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
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static BelongingsDailyService Daily(SchoolDbContext db, ISchoolTimeService time) =>
        new(db, new TestUser(), DbFixture.SessionsAt(db, time), time);

    private static BelongingsHistoryService History(SchoolDbContext db, ISchoolTimeService time) =>
        new(db, new BelongingsService(db, DbFixture.Timetable(db), new TestUser()), time);

    /// <summary>A year whose first quarter has these dates (created once per test run).</summary>
    private async Task<(int YearId, int QuarterId)> QuarterAsync(DateOnly start, DateOnly end)
    {
        await using var db = fx.NewContext();
        var existing = await db.AcademicQuarters.Where(q => q.StartDate == start).Select(q => new { q.AcademicYearId, q.Id }).FirstOrDefaultAsync();
        if (existing is not null) return (existing.AcademicYearId, existing.Id);
        var svc = new BelongingsService(db, DbFixture.Timetable(db), new TestUser());
        var year = await svc.CreateYearAsync("Year " + DbFixture.Unique());
        Assert.True(year.Succeeded);
        var q1 = (await svc.YearsAsync()).Single(y => y.Id == year.EntityId).Quarters.Single(q => q.Number == 1);
        var r = await svc.UpdateQuarterAsync(q1.Id, "Quarter 1", start, end);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        return (year.EntityId!.Value, q1.Id);
    }

    private async Task<(int ClassId, string ClassName, int[] Students, string[] Names)> ClassAsync(int students)
    {
        var classId = await fx.NewClassAsync();
        var ids = new List<int>();
        var names = new List<string>();
        for (var i = 0; i < students; i++) { var s = await fx.NewStudentAsync(classId); ids.Add(s.Id); names.Add(s.Name); }
        await using var db = fx.NewContext();
        await db.Students.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        return (classId, await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync(), ids.ToArray(), names.ToArray());
    }

    private sealed record Timetable(int ClassId, int[] Students, int Math, int English, int TuesdayOnly, int EnglishSlot, int TeacherA, string TeacherAName, int TeacherB, string TeacherBName);

    /// <summary>A class with its own periods on Mondays: P1 08:00–08:45 Math (teacher A), P2 08:50–09:35 English (teacher B); and a Tuesday-only lesson.</summary>
    private async Task<Timetable> TimetableAsync(DateOnly day)
    {
        var (classId, _, students, _) = await ClassAsync(3);
        var (a, aName) = await fx.NewTeacherAsync();
        var (b, bName) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        var department = await db.Departments.Select(d => d.Id).FirstAsync();
        var slots = new[] { (1, new TimeOnly(8, 0), new TimeOnly(8, 45)), (2, new TimeOnly(8, 50), new TimeOnly(9, 35)) }
            .Select(x => new TimeSlot { Number = x.Item1, Name = $"Period {x.Item1}", StartTime = x.Item2, EndTime = x.Item3, IsActive = true, SchoolClassId = classId, ReplacesTimeSlotId = x.Item1 })
            .ToList();
        db.TimeSlots.AddRange(slots);
        await db.SaveChangesAsync();
        TimetableAssignment Lesson(TimeSlot slot, string subject, int teacher, DayOfWeek weekday = DayOfWeek.Monday) => new()
        {
            SchoolClassId = classId, Day = weekday, TimeSlotId = slot.Id, DepartmentId = department, Subject = subject,
            TeacherId = teacher, StartsOn = SchoolWeek.StartOf(day), CreatedBy = "Test",
        };
        var lessons = new[] { Lesson(slots[0], "Math", a), Lesson(slots[1], "English", b), Lesson(slots[0], "Science", a, DayOfWeek.Tuesday) };
        db.TimetableAssignments.AddRange(lessons);
        await db.SaveChangesAsync();
        _timetables.Add((classId, lessons.Select(l => l.Id).ToArray()));
        return new Timetable(classId, students, lessons[0].Id, lessons[1].Id, lessons[2].Id, slots[1].Id, a, aName, b, bName);
    }

    // ------------------------------------------------------------------ Personal Belongings

    [Fact]
    public async Task Personal_daily_checks_work_class_by_class_with_one_click_saves_and_corrections()
    {
        await QuarterAsync(QStart, QEnd);
        var (classId, className, s, _) = await ClassAsync(3);
        var at9 = CairoAt(Monday, 9, 0);
        await using var db = fx.NewContext();
        var svc = Daily(db, at9);

        // Every class appears at once with its real student count; nothing recorded yet.
        var card = (await svc.PersonalClassesAsync(Monday)).Classes.Single(c => c.ClassId == classId);
        Assert.Equal((3, 0, 3, DailyState.NotStarted), (card.Students, card.Complete, card.Pending, card.State));

        // One item: saved, the student is started but not checked (all five items are required).
        var r = await svc.SetPersonalAsync(s[0], Monday, "Jacket", ItemStatus.Green);
        Assert.True(r.Saved, r.Message);
        Assert.True(r.Row!.Started);
        Assert.False(r.Row.Complete);
        Assert.Equal((0, 3), (r.Counts!.Complete, r.Counts.Pending));
        Assert.Equal(DailyState.InProgress, (await svc.PersonalClassesAsync(Monday)).Classes.Single(c => c.ClassId == classId).State);

        foreach (var (item, status) in new[] { ("SchoolBag", ItemStatus.Red), ("PencilCase", ItemStatus.NotApplicable), ("WaterFlask", ItemStatus.Green), ("LunchBox", ItemStatus.Green) })
            r = await svc.SetPersonalAsync(s[0], Monday, item, status);
        Assert.True(r.Row!.Complete);
        Assert.Equal(new[] { "School Bag" }, r.Row.Missing);
        Assert.Equal(75.0, r.Row.Readiness.Percent); // 3 Available ÷ (3 + 1 Missing); N/A excluded
        Assert.Equal((1, 2), (r.Counts!.Complete, r.Counts.Pending));

        // The same status again changes nothing and is not a correction; a changed status is kept as a correction.
        Assert.Equal("Already saved.", (await svc.SetPersonalAsync(s[0], Monday, "Jacket", ItemStatus.Green)).Message);
        Assert.True((await svc.SetPersonalAsync(s[0], Monday, "SchoolBag", ItemStatus.Green)).Saved);
        var record = await db.BelongingsRecords.AsNoTracking().SingleAsync(x => x.StudentId == s[0] && x.Date == Monday);
        var change = await db.BelongingsChanges.AsNoTracking().SingleAsync(c => c.BelongingsRecordId == record.Id);
        Assert.Equal(("School Bag", "Missing", "Available", "Test runner"), (change.Item, change.Before, change.After, change.ChangedBy));
        Assert.Equal("Test runner", record.RecordedBy);

        // "All available" fills only the items not checked yet: a saved Missing stays Missing.
        await svc.SetPersonalAsync(s[1], Monday, "Jacket", ItemStatus.Red);
        r = await svc.PersonalAllAvailableAsync(s[1], Monday);
        Assert.True(r.Row!.Complete);
        Assert.Equal(new[] { "Jacket" }, r.Row.Missing);

        // Notes keep their earlier versions.
        await svc.SavePersonalNotesAsync(s[0], Monday, "Bag left at home");
        r = await svc.SavePersonalNotesAsync(s[0], Monday, "Bag brought by parent");
        Assert.Equal("Bag brought by parent", r.Row!.Notes);
        Assert.Equal("Bag left at home", (await db.BelongingsNoteHistory.AsNoTracking().SingleAsync(h => h.BelongingsRecordId == record.Id)).PreviousNotes);

        // One record per student and day, however many clicks.
        Assert.Equal(1, await db.BelongingsRecords.CountAsync(x => x.StudentId == s[0] && x.Date == Monday));

        // The class page: every student, counts from the records, recording allowed today.
        var page = (await svc.PersonalClassAsync(classId, Monday))!;
        Assert.Equal(className, page.ClassName);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal((2, 1, DailyState.InProgress), (page.Counts.Complete, page.Counts.Pending, page.Counts.State));
        Assert.True(page.CanRecord);

        // Refused: a future date, a weekend, a date outside every quarter.
        Assert.Contains("future", (await svc.SetPersonalAsync(s[2], Monday.AddDays(1), "Jacket", ItemStatus.Green)).Message);
        Assert.Contains("not a school day", (await Daily(db, CairoAt(new(2035, 1, 20), 10, 0)).SetPersonalAsync(s[2], new(2035, 1, 20), "Jacket", ItemStatus.Green)).Message);
        Assert.Contains("quarter", (await Daily(db, CairoAt(new(2035, 3, 12), 10, 0)).SetPersonalAsync(s[2], new(2035, 3, 12), "Jacket", ItemStatus.Green)).Message);
        Assert.False((await svc.SetPersonalAsync(s[2], Monday, "Umbrella", ItemStatus.Green)).Saved);
    }

    // ------------------------------------------------------------------ Academic Belongings

    [Fact]
    public async Task Academic_checks_open_the_lesson_in_progress_and_keep_the_lesson_as_recorded()
    {
        await QuarterAsync(QStart, QEnd);
        var t = await TimetableAsync(Monday);
        await using var db = fx.NewContext();

        // Before school: no lesson in progress, the next one is Math; nothing can be recorded for it yet.
        var early = Daily(db, CairoAt(Monday, 7, 30));
        var before = (await early.AcademicClassAsync(t.ClassId, Monday, null))!;
        Assert.Null(before.Lesson);
        Assert.Equal(t.Math, before.Day.Next!.Lesson.SessionId);
        Assert.False(before.CanRecord);
        Assert.Contains("has not started", (await early.SetAcademicAsync(t.Math, Monday, t.Students[0], "Book", ItemStatus.Green)).Message);
        Assert.Equal("No Active Lesson", (await early.AcademicClassesAsync(Monday)).Single(c => c.Card.ClassId == t.ClassId).State);

        // 9:00 Cairo: English (period 2, 8:50–9:35, teacher B) is selected by itself.
        var svc = Daily(db, CairoAt(Monday, 9, 0));
        var now = (await svc.AcademicClassAsync(t.ClassId, Monday, null))!;
        Assert.Equal(t.English, now.Lesson!.Lesson.SessionId);
        Assert.Equal(("English", 2, new TimeOnly(8, 50), new TimeOnly(9, 35), t.TeacherBName),
            (now.Lesson.Lesson.Subject, now.Lesson.Lesson.SlotNumber, now.Lesson.Lesson.Start!.Value, now.Lesson.Lesson.End!.Value, now.Lesson.Lesson.Teacher));
        Assert.Equal(LessonPhase.InProgress, now.Lesson.Phase);
        Assert.True(now.CanRecord);
        Assert.Equal(3, now.Rows.Count);
        var card = (await svc.AcademicClassesAsync(Monday)).Single(c => c.Card.ClassId == t.ClassId);
        Assert.Equal(("In Progress", t.English, 0), (card.State, card.Lesson!.Lesson.SessionId, card.Checked));

        // One click each; the student is checked when both items have a status.
        var r = await svc.SetAcademicAsync(t.English, Monday, t.Students[0], "Book", ItemStatus.Green);
        Assert.True(r.Saved, r.Message);
        Assert.False(r.Row!.Complete);
        r = await svc.SetAcademicAsync(t.English, Monday, t.Students[0], "CopyBook", ItemStatus.Red);
        Assert.True(r.Row!.Complete);
        Assert.Equal(1, r.Counts!.Complete);
        Assert.Equal(1, (await svc.AcademicClassesAsync(Monday)).Single(c => c.Card.ClassId == t.ClassId).Checked);

        // No duplicates; a changed value is a correction tied to the lesson.
        Assert.Equal("Already saved.", (await svc.SetAcademicAsync(t.English, Monday, t.Students[0], "Book", ItemStatus.Green)).Message);
        await svc.SetAcademicAsync(t.English, Monday, t.Students[0], "CopyBook", ItemStatus.Green);
        var row = await db.AcademicMaterialRecords.AsNoTracking().SingleAsync(m => m.TimetableAssignmentId == t.English && m.Record.StudentId == t.Students[0]);
        var change = await db.BelongingsChanges.AsNoTracking().SingleAsync(c => c.AcademicMaterialRecordId == row.Id);
        Assert.Equal(("Copy Book", "English · Period 2", "Missing", "Available"), (change.Item, change.Context, change.Before, change.After));

        // The record carries the lesson exactly as it was: period, times, subject, teacher.
        Assert.Equal((2, "Period 2", new TimeOnly(8, 50), new TimeOnly(9, 35), "English", t.TeacherB, t.TeacherBName),
            (row.PeriodNumber!.Value, row.PeriodName, row.LessonStart!.Value, row.LessonEnd!.Value, row.Subject, row.TeacherId!.Value, row.TeacherName));

        // A finished lesson of the day can be opened on purpose and completed; an unscheduled one (Tuesday's lesson on Monday) never.
        var math = (await svc.AcademicClassAsync(t.ClassId, Monday, t.Math))!;
        Assert.Equal(LessonPhase.Finished, math.Lesson!.Phase);
        Assert.True((await svc.SetAcademicAsync(t.Math, Monday, t.Students[1], "Book", ItemStatus.NotApplicable)).Saved);
        Assert.Contains("not on the class timetable", (await svc.SetAcademicAsync(t.TuesdayOnly, Monday, t.Students[1], "Book", ItemStatus.Green)).Message);
        Assert.Null((await svc.AcademicClassAsync(t.ClassId, Monday, t.TuesdayOnly))!.Lesson);
        Assert.Equal(1, await db.AcademicMaterialRecords.CountAsync(m => m.TimetableAssignmentId == t.English && m.Record.StudentId == t.Students[0]));

        // The timetable changes later (teacher and period times): the history still shows the lesson as recorded.
        await db.TimetableAssignments.Where(x => x.Id == t.English).ExecuteUpdateAsync(x => x.SetProperty(a => a.TeacherId, t.TeacherA));
        await db.TimeSlots.Where(x => x.Id == t.EnglishSlot).ExecuteUpdateAsync(x => x.SetProperty(a => a.StartTime, new TimeOnly(11, 0)).SetProperty(a => a.EndTime, new TimeOnly(11, 45)));
        var detail = (await History(db, CairoAt(Monday, 15, 0)).AcademicRecordAsync(row.Id))!;
        Assert.Equal((t.TeacherBName, new TimeOnly(8, 50), "Period 2"), (detail.Lesson.TeacherName, detail.Lesson.Start!.Value, detail.Lesson.Period));
        Assert.Single(detail.Students);
        Assert.Single(detail.Changes);

        // History filters: class + day, subject, teacher, period, Copy Book status.
        var history = History(db, CairoAt(Monday, 15, 0));
        var q = new BelongingsQuery { Period = "day", Date = Monday, ClassId = t.ClassId };
        var all = await history.AcademicAsync(q);
        Assert.Equal((2, 2), (all.Checks, all.Lessons));
        Assert.Single((await history.AcademicAsync(q.With(f => f.Subject = "English"))).Rows);
        Assert.Single((await history.AcademicAsync(q.With(f => f.TeacherId = t.TeacherB))).Rows);
        Assert.Single((await history.AcademicAsync(q.With(f => f.PeriodNumber = 1))).Rows);
        Assert.Single((await history.AcademicAsync(q.With(f => f.CopyBook = "None"))).Rows); // the Math check has no Copy Book yet
        Assert.Single((await history.AcademicAsync(q.With(f => f.Status = "incomplete"))).Rows);
    }

    // ------------------------------------------------------------------ History & Reports

    [Fact]
    public async Task Personal_history_filters_by_period_and_scope_and_finds_repeated_missing_items()
    {
        var (yearId, quarterId) = await QuarterAsync(QStart, QEnd);
        var (classId, _, s, names) = await ClassAsync(3);
        var days = new[] { new DateOnly(2035, 1, 15), new DateOnly(2035, 1, 16), new DateOnly(2035, 1, 22), new DateOnly(2035, 2, 5) };
        await using (var db = fx.NewContext())
        {
            foreach (var day in days)
            {
                var svc = Daily(db, CairoAt(day, 9, 0));
                foreach (var id in s) Assert.True((await svc.PersonalAllAvailableAsync(id, day)).Saved);
                // Student 1 is missing the Lunch Box on every day but the last.
                if (day != days[^1]) Assert.True((await svc.SetPersonalAsync(s[0], day, "LunchBox", ItemStatus.Red)).Saved);
            }
        }
        await using var db2 = fx.NewContext();
        var history = History(db2, CairoAt(new DateOnly(2035, 2, 6), 9, 0));
        var q = new BelongingsQuery { YearId = yearId, Period = "quarter", QuarterId = quarterId, ClassId = classId };

        var quarter = await history.PersonalAsync(q);
        Assert.Equal((12, 12, 3), (quarter.Total, quarter.Complete, quarter.WithMissing));
        Assert.Equal("Quarter 1", quarter.Scope.QuarterName);
        Assert.StartsWith("Class:", quarter.Scope.Audience);
        Assert.Equal((names[0], 3), (quarter.Repeated.Single().Name, quarter.Repeated.Single().Days));
        Assert.Equal(57 * 100 / 60, (int)quarter.Readiness.Percent!.Value); // 57 Available of 60 checked items

        Assert.Equal(6, (await history.PersonalAsync(q.With(f => { f.Period = "week"; f.Week = new DateOnly(2035, 1, 17); }))).Total);
        Assert.Equal(9, (await history.PersonalAsync(q.With(f => { f.Period = "month"; f.Month = "2035-01"; }))).Total);
        Assert.Equal(3, (await history.PersonalAsync(q.With(f => { f.Period = "day"; f.Date = days[2]; }))).Total);
        Assert.Equal(6, (await history.PersonalAsync(q.With(f => { f.Period = "range"; f.From = days[2]; f.To = days[3]; }))).Total);
        Assert.Equal(4, (await history.PersonalAsync(q.With(f => f.StudentId = s[0]))).Total);
        Assert.Equal(3, (await history.PersonalAsync(q.With(f => f.Status = "missing:LunchBox"))).Total);
        Assert.Equal(0, (await history.PersonalAsync(q.With(f => f.Status = "missing:Jacket"))).Total);

        // Paging shows a page; the rows behind the report are every matching record.
        var paged = await history.PersonalAsync(q.With(f => f.PageSize = 10));
        Assert.Equal((10, 12, 2), (paged.Page.Items.Count, paged.Rows.Count, paged.Page.TotalPages));
    }

    [Fact]
    public async Task Daily_pages_one_click_saves_history_and_reports_work_over_http()
    {
        var day = new DateOnly(2026, 3, 9); // a Monday in the past: the app runs on the real clock
        await QuarterAsync(new(2026, 3, 1), new(2026, 4, 23));
        var (classId, className, s, names) = await ClassAsync(2);
        var client = fx.Factory.CreateClient();
        var date = SchoolWeek.Iso(day);

        // Daily Checks: classes at once, no filter form to fill in first.
        var daily = WebUtility.HtmlDecode(await client.GetStringAsync($"/belongings/personal?date={date}"));
        Assert.Contains("<h1 class=\"page-title\">Personal Belongings</h1>", daily);
        Assert.Contains($"/belongings/personal/classes/{classId}?date={date}", daily);
        Assert.Contains("Daily Checks", daily);
        Assert.DoesNotContain("name=\"grade\"", daily);
        Assert.DoesNotContain("name=\"studentId\"", daily);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/belongings/personal/classes/{classId}?date={date}"));
        foreach (var n in names) Assert.Contains(n, page);
        Assert.Contains("data-bl-mark", page);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        async Task<HttpResponseMessage> Post(string url, Dictionary<string, string> form)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return await client.SendAsync(req);
        }

        // One click: saved, the row comes back redrawn with the status pressed, and the class counts.
        var res = await Post("/belongings/personal/mark", new() { ["studentId"] = s[0].ToString(), ["date"] = date, ["item"] = "Jacket", ["status"] = "Red" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using (var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()))
        {
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Matches(JacketMissingPressed, json.RootElement.GetProperty("row").GetString()!);
            Assert.Equal(2, json.RootElement.GetProperty("counts").GetProperty("students").GetInt32());
        }
        Assert.Equal(HttpStatusCode.OK, (await Post("/belongings/personal/all", new() { ["studentId"] = s[0].ToString(), ["date"] = date })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post("/belongings/personal/mark", new() { ["studentId"] = s[0].ToString(), ["date"] = date, ["item"] = "Umbrella", ["status"] = "Green" })).StatusCode);

        // Refreshed: still saved.
        var after = WebUtility.HtmlDecode(await client.GetStringAsync($"/belongings/personal/classes/{classId}?date={date}"));
        Assert.Matches(JacketMissingPressed, after);
        Assert.Matches(new Regex("data-student=\"" + s[0] + "\"[^>]*data-complete=\"true\""), after);

        // History & Reports: the class and student, the record, and both downloads with the same filters.
        var filters = $"?period=range&from={date}&to={date}&classId={classId}";
        var hist = WebUtility.HtmlDecode(await client.GetStringAsync("/belongings/personal/history" + filters));
        Assert.Contains("History & Reports", hist);
        Assert.Contains(names[0], hist);
        Assert.Contains("Download PDF", hist);
        var recordId = await fx.NewContext().BelongingsRecords.Where(r => r.StudentId == s[0] && r.Date == day).Select(r => r.Id).SingleAsync();
        var record = await client.GetAsync($"/belongings/personal/records/{recordId}");
        Assert.Equal(HttpStatusCode.OK, record.StatusCode);
        Assert.Contains("Missing", await record.Content.ReadAsStringAsync());

        var pdf = await client.GetAsync("/belongings/personal/history/pdf" + filters);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        using (var doc = PdfDocument.Open(await pdf.Content.ReadAsByteArrayAsync()))
        {
            var text = Regex.Replace(string.Concat(doc.GetPages().Select(p => p.Text)), "\\s+", "");
            Assert.Contains("PersonalBelongingsReport", text);
            Assert.Contains(Regex.Replace(names[0], "\\s+", ""), text);
            Assert.Contains(Regex.Replace(className, "\\s+", ""), text);
        }
        var xlsx = await client.GetAsync("/belongings/personal/history/excel" + filters);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);
        using (var wb = new XLWorkbook(await xlsx.Content.ReadAsStreamAsync()))
        {
            var rows = wb.Worksheet("Records").RowsUsed().Skip(1).ToList();
            Assert.Single(rows);
            Assert.Equal(names[0], rows[0].Cell(3).GetString());
            Assert.Equal("Missing", rows[0].Cell(5).GetString()); // Jacket
        }

        // Academic Belongings: classes with their lesson state, the class page, history and downloads.
        Assert.Contains(className, await client.GetStringAsync($"/belongings/academic?date={date}"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/belongings/academic/classes/{classId}?date={date}")).StatusCode);
        Assert.Contains("Academic Belongings", await client.GetStringAsync("/belongings/academic/history" + filters));
        Assert.Equal("application/pdf", (await client.GetAsync("/belongings/academic/history/pdf" + filters)).Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/belongings/academic/history/excel" + filters)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post("/belongings/academic/mark",
            new() { ["sessionId"] = "999999", ["date"] = date, ["studentId"] = s[0].ToString(), ["item"] = "Book", ["status"] = "Green" })).StatusCode);
    }
}
