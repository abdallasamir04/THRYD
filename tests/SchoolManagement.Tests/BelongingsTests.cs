using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>Belongings: calendar rules, grade-based students, independent statuses, notes, and readiness maths.</summary>
[Collection("db")]
public partial class BelongingsTests(DbFixture fx)
{
    private const ItemStatus G = ItemStatus.Green, R = ItemStatus.Red, N = ItemStatus.NotApplicable;

    private static BelongingsService Service(Web.Data.SchoolDbContext db) => new(db, DbFixture.Timetable(db), new TestUser());

    private async Task<int> ClassIdAsync(string name)
    {
        await using var db = fx.NewContext();
        return await db.Classes.Where(c => c.Name == name).Select(c => c.Id).SingleAsync();
    }

    /// <summary>A year whose first quarter is set to the given (valid, non-overlapping) dates.</summary>
    private async Task<QuarterView> QuarterAsync(DateOnly start, DateOnly end)
    {
        await using var db = fx.NewContext();
        var svc = Service(db);
        var year = await svc.CreateYearAsync("Year " + DbFixture.Unique());
        Assert.True(year.Succeeded);
        var q1 = (await svc.YearsAsync()).Single(y => y.Id == year.EntityId).Quarters.Single(q => q.Number == 1);
        var r = await svc.UpdateQuarterAsync(q1.Id, "Quarter 1", start, end);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        return (await svc.QuarterAsync(q1.Id))!;
    }

    [Fact]
    public void Readiness_excludes_NA_and_shows_NA_when_nothing_applies()
    {
        var personal = ReadinessCounts.Of([G, G, G, R, N]);
        var academic = ReadinessCounts.Of([G, R]);
        Assert.Equal(75, personal.Percent);
        Assert.Equal(50, academic.Percent);
        Assert.Equal(66.67, new Readiness(personal, academic).Overall.Percent);
        Assert.Null(ReadinessCounts.Of([N, N, N, N, N]).Percent);
        Assert.Null(ReadinessCounts.Of([]).Percent);
        Assert.Equal("N/A", ReadinessCounts.Text(null));
        Assert.Equal(100, ReadinessCounts.Of([G, N, N, N, N]).Percent); // not 1 of 5
    }

    [Fact]
    public void Grades_come_from_existing_class_names()
    {
        Assert.Equal(13, GradeCatalog.All.Count);
        Assert.Equal(("PREK", "KG1", "KG2", "G7", "G10"), (GradeCatalog.KeyOf("PreK"), GradeCatalog.KeyOf("KG1A"), GradeCatalog.KeyOf("KG2B"), GradeCatalog.KeyOf("G7B"), GradeCatalog.KeyOf("G10A")));
        Assert.Null(GradeCatalog.KeyOf("Library"));
    }

    [Fact]
    public async Task Quarters_have_eight_school_weeks_and_invalid_dates_are_refused()
    {
        await using var db = fx.NewContext();
        var svc = Service(db);
        var year = (await svc.CreateYearAsync("Year " + DbFixture.Unique())).EntityId!.Value;
        var quarters = (await svc.YearsAsync()).Single(y => y.Id == year).Quarters;
        Assert.Equal([1, 2, 3, 4], quarters.Select(q => q.Number));
        var q2 = quarters.Single(q => q.Number == 2).Id;

        // 2031-03-02 is a Sunday; 8 weeks end on Thursday 2031-04-24.
        Assert.Contains((await svc.UpdateQuarterAsync(q2, "Q2", new(2031, 3, 7), new(2031, 4, 24))).Errors, e => e.Value.Contains("school day")); // Friday
        Assert.Contains((await svc.UpdateQuarterAsync(q2, "Q2", new(2031, 3, 2), new(2031, 5, 1))).Errors, e => e.Value.Contains("9 school week")); // one week too many
        Assert.Contains((await svc.UpdateQuarterAsync(q2, "Q2", new(2031, 3, 2), new(2031, 4, 17))).Errors, e => e.Value.Contains("7 school week"));
        // Partial first week: starting on a Tuesday is allowed; week 1 then has Tue–Thu only.
        Assert.True((await svc.UpdateQuarterAsync(q2, "Q2", new(2031, 3, 4), new(2031, 4, 24))).Succeeded);
        var weeks = (await svc.QuarterAsync(q2))!.Weeks;
        Assert.Equal(8, weeks.Count);
        Assert.Equal(3, weeks[0].Days.Count);
        Assert.All(weeks.SelectMany(w => w.Days), d => Assert.True(d.DayOfWeek is >= DayOfWeek.Sunday and <= DayOfWeek.Thursday));
        Assert.Equal(new DateOnly(2031, 4, 24), weeks[7].Days[^1]);

        // Quarters do not overlap, and come in order within the year.
        var q1 = quarters.Single(q => q.Number == 1).Id;
        Assert.Contains((await svc.UpdateQuarterAsync(q1, "Q1", new(2031, 3, 16), new(2031, 5, 8))).Errors, e => e.Value.Contains("overlap"));
        Assert.Contains((await svc.UpdateQuarterAsync(q1, "Q1", new(2031, 6, 1), new(2031, 7, 24))).Errors, e => e.Value.Contains("before"));
    }

    [Fact]
    public async Task Records_are_validated_saved_independently_and_keep_note_history()
    {
        var quarter = await QuarterAsync(new(2032, 1, 4), new(2032, 2, 26));
        var g8 = await fx.NewStudentAsync(await ClassIdAsync("G8A"));
        var other = await fx.NewStudentAsync(await ClassIdAsync("G9A"));
        var day = new DateOnly(2032, 1, 6); // Tuesday, week 1
        await using var db = fx.NewContext();
        var svc = Service(db);

        Assert.Contains(await svc.StudentsAsync("G8"), s => s.Id == g8.Id);
        Assert.DoesNotContain(await svc.StudentsAsync("G8"), s => s.Id == other.Id);

        PersonalInput Input(int student, DateOnly d, string grade, ItemStatus? jacket = G, string? notes = null) =>
            new(student, d, grade, jacket, G, R, N, G, notes);
        Assert.Contains((await svc.SavePersonalAsync(Input(other.Id, day, "G8"))).Errors, e => e.Key == "StudentId"); // wrong grade
        Assert.Contains((await svc.SavePersonalAsync(Input(g8.Id, new(2032, 1, 9), "G8"))).Errors, e => e.Key == "Date"); // Friday
        Assert.Contains((await svc.SavePersonalAsync(Input(g8.Id, new(2040, 1, 2), "G8"))).Errors, e => e.Key == "Date"); // no quarter
        Assert.Contains((await svc.SavePersonalAsync(Input(g8.Id, day, "G8", jacket: null))).Errors, e => e.Key == "Jacket");

        Assert.True((await svc.SavePersonalAsync(Input(g8.Id, day, "G8", notes: "Forgot the water flask"))).Succeeded);
        Assert.True((await svc.SavePersonalAsync(Input(g8.Id, day, "G8", jacket: R, notes: "Water flask brought back"))).Succeeded);

        // Academic materials: subject must belong to the department; subjects are separate rows; saving again updates.
        var religion = await db.Departments.Where(d => d.Name == "Religion").Select(d => d.Id).SingleAsync();
        var math = await db.Departments.Where(d => d.Name == "Math").Select(d => d.Id).SingleAsync();
        Assert.Contains((await svc.SaveMaterialAsync(new(g8.Id, day, "G8", math, "Quran", G, G))).Errors, e => e.Key == "Subject");
        Assert.True((await svc.SaveMaterialAsync(new(g8.Id, day, "G8", religion, "Quran", G, R))).Succeeded);
        Assert.True((await svc.SaveMaterialAsync(new(g8.Id, day, "G8", math, "Math", N, G))).Succeeded);
        Assert.True((await svc.SaveMaterialAsync(new(g8.Id, day, "G8", math, "math", R, G))).Succeeded); // update, not a duplicate

        await using var check = fx.NewContext();
        var record = await check.BelongingsRecords.Include(r => r.Materials).SingleAsync(r => r.StudentId == g8.Id && r.Date == day);
        Assert.Equal((R, G, R, N, G), (record.Jacket!.Value, record.SchoolBag!.Value, record.PencilCase!.Value, record.WaterFlask!.Value, record.LunchBox!.Value));
        Assert.Equal(quarter.Id, record.AcademicQuarterId);
        Assert.Equal(await ClassIdAsync("G8A"), record.SchoolClassId);
        Assert.Equal(2, record.Materials.Count);
        Assert.Equal((R, G), record.Materials.Where(m => m.Subject == "Math").Select(m => (m.Book, m.CopyBook)).Single());
        Assert.Equal((G, R), record.Materials.Where(m => m.Subject == "Quran").Select(m => (m.Book, m.CopyBook)).Single());
        Assert.Equal("Water flask brought back", record.Notes);
        Assert.Equal("Forgot the water flask", (await check.BelongingsNoteHistory.SingleAsync(h => h.BelongingsRecordId == record.Id)).PreviousNotes);

        // Day readiness: personal G,G,R,N,R... → recomputed from what was saved.
        var view = await Service(check).DayAsync(g8.Id, day);
        Assert.Equal((2, 2, 1), (view.Readiness.Personal.Green, view.Readiness.Personal.Red, view.Readiness.Personal.NotApplicable));
        Assert.Equal((2, 2), (view.Readiness.Academic.Green, view.Readiness.Academic.Red));
        Assert.Equal(50, view.Readiness.Overall.Percent);
    }

    [Fact]
    public async Task Weekly_and_quarterly_readiness_add_counts_and_ignore_missing_days()
    {
        // A past quarter (all days are due): 2025-01-05 is a Sunday.
        var quarter = await QuarterAsync(new(2025, 1, 5), new(2025, 2, 27));
        var s = await fx.NewStudentAsync(await ClassIdAsync("G9B"));
        await using var db = fx.NewContext();
        var svc = Service(db);

        // Sunday: 5 green (100%). Monday: 1 green, 1 red, 3 N/A (50%). Other days: not recorded.
        Assert.True((await svc.SavePersonalAsync(new(s.Id, new(2025, 1, 5), "G9", G, G, G, G, G, null))).Succeeded);
        Assert.True((await svc.SavePersonalAsync(new(s.Id, new(2025, 1, 6), "G9", G, R, N, N, N, null))).Succeeded);

        var week = await svc.WeekAsync(s.Id, quarter, 1);
        Assert.Equal(85.71, week.Total.Personal.Percent); // 6 / 7 — an average of 100% and 50% would wrongly give 75%
        Assert.Null(week.Total.Academic.Percent);           // nothing recorded → N/A, not 0%
        Assert.Equal((2, 3, 5), (week.RecordedDays, week.MissingDays, week.SchoolDays));
        Assert.Equal([100, 50], week.Points.Where(p => p.Recorded).Select(p => p.Readiness.Overall.Percent!.Value));

        // Week 2: one day with academic materials only.
        var history = await db.Departments.Where(d => d.Name == "ESS").Select(d => d.Id).SingleAsync();
        Assert.True((await svc.SaveMaterialAsync(new(s.Id, new(2025, 1, 13), "G9", history, "ESS", R, R))).Succeeded);

        var q = await svc.QuarterAnalyticsAsync(s.Id, quarter);
        Assert.Equal(8, q.Points.Count);
        Assert.Equal((6, 1), (q.Total.Personal.Green, q.Total.Personal.Red));
        Assert.Equal((0, 2), (q.Total.Academic.Green, q.Total.Academic.Red));
        Assert.Equal(66.67, q.Total.Overall.Percent); // 6 / (6 + 3)
        Assert.Equal((3, 40 - 3), (q.RecordedDays, q.MissingDays));
        Assert.Equal(0, q.Points[1].Readiness.Overall.Percent);

        var grade = await svc.GradeWeekAsync("G9", quarter, 1);
        Assert.Equal(85.71, grade.Single(r => r.StudentId == s.Id).Readiness.Overall.Percent);
    }

    // The Daily Checks and History & Reports pages are covered by BelongingsRedesignTests.

    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
}
