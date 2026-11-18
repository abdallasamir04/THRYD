using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;

namespace SchoolManagement.Tests;

/// <summary>
/// Classes page → Download Summary (school / stage / class summary) and Download Students (one A4 portrait page per class
/// with # and Student Name only), both from the same roster.
/// </summary>
[Collection("db")]
public sealed partial class ClassRosterReportTests(DbFixture fx)
{
    private sealed class TestUser : ICurrentUser
    {
        public string? Name => "Report tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private static ClassRosterService Service(Web.Data.SchoolDbContext db) => new(db, new ClassTeamService(db, new TestUser()));

    private static (int Pages, HashSet<string> Sizes) PdfPages(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        return (PageObject().Matches(text).Count, MediaBox().Matches(text).Select(m => $"{m.Groups[1].Value}x{m.Groups[2].Value}").ToHashSet());
    }

    [Fact]
    public async Task Every_class_and_student_appears_once_under_its_class()
    {
        var classId = await fx.NewClassAsync();
        var (student, studentName) = await fx.NewStudentAsync(classId);
        await using var db = fx.NewContext();
        var roster = await Service(db).LoadAsync();

        var classes = roster.AllClasses.ToList();
        Assert.Equal(await db.Classes.CountAsync(), classes.Count);
        Assert.Equal(classes.Count, classes.Select(c => c.Id).Distinct().Count());
        var students = classes.SelectMany(c => c.Students.Select(s => (ClassId: c.Id, s.Id))).ToList();
        Assert.Equal(await db.Students.CountAsync(), students.Count);
        Assert.Equal(students.Count, students.Select(s => s.Id).Distinct().Count());
        var actual = await db.Students.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.SchoolClassId);
        Assert.All(students, s => Assert.Equal(actual[s.Id], s.ClassId));
        Assert.Contains(classes.Single(c => c.Id == classId).Students, s => s.Id == student && s.FullName == studentName);
        var capacities = await db.Classes.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Capacity);
        Assert.All(classes, c => Assert.Equal(capacities[c.Id], c.Capacity));
        Assert.All(classes, c => Assert.Equal(c.Students.OrderBy(s => s.FullName, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id).Select(s => s.Id), c.Students.Select(s => s.Id)));
        foreach (var stage in roster.Stages)
            foreach (var g in stage.Groups)
                Assert.All(g.Classes, c => Assert.Equal(g.Title, ClassStages.Locate(c.Name)!.Value.Group.Title));
        Assert.All(roster.Other, c => Assert.Null(ClassStages.Locate(c.Name)));
    }

    [Fact]
    public async Task Students_report_has_one_portrait_page_per_class_with_only_number_and_name()
    {
        await using var db = fx.NewContext();
        var teams = new ClassTeamService(db, new TestUser());
        var year = await teams.CurrentYearAsync();
        // A KG2 class of this test only, with a Homeroom Teacher and an Arabic-named student (removed at the end).
        var created = await DbFixture.Classes(db).CreateInGradeAsync("KG", 2, DbFixture.Unique()[..3].ToUpperInvariant(), 20, true);
        Assert.True(created.Succeeded);
        var kg = created.EntityId!.Value;
        const string arabic = "أحمد محمد عبد الله";
        try
        {
            var (teacher, teacherName) = await fx.NewTeacherAsync();
            if (year is not null) Assert.True((await teams.SaveAsync(kg, year.Id, teacher, null)).Succeeded);
            db.Students.Add(new Student { FullName = arabic, NormalizedName = arabic, SchoolClassId = kg });
            // A very large class (61 students) must still fit on its one page (scaled to fit, never split).
            for (var i = 1; i <= 60; i++)
                db.Students.Add(new Student { FullName = $"Large Class Student {i:00}", NormalizedName = $"LARGE CLASS STUDENT {i:00}", SchoolClassId = kg });
            await db.SaveChangesAsync();

            var roster = await Service(db).LoadAsync();
            var report = ClassRosterReports.Students(roster, "Test School");
            var placed = ClassRosterReports.Placed(roster).ToList();

            // One dedicated portrait page per class, in the Classes pages' order.
            Assert.True(report.Portrait);
            Assert.Empty(report.Sections);
            Assert.Equal(roster.AllClasses.Count(), report.Pages!.Count);
            Assert.Equal(placed.Select(x => $"Class {x.Class.Name}"), report.Pages.Select(p => p.Subtitle));

            foreach (var (page, c) in report.Pages.Zip(placed.Select(x => x.Class)))
            {
                var table = Assert.Single(page.Sections.OfType<PdfTable>());
                // Exactly # (the row number, 1, 2, 3…) and Student Name; no Student ID column or value anywhere.
                Assert.True(table.RowNumbers);
                Assert.Equal(["Student Name"], table.Columns.Select(col => col.Header));
                Assert.Equal(c.Students.Select(s => s.FullName), table.Rows.Select(r => Assert.Single(r).Text));
                var fields = Assert.Single(page.Sections.OfType<PdfFields>());
                Assert.DoesNotContain(fields.Items, f => f.Label.Contains("ID", StringComparison.Ordinal));
                var values = fields.Items.ToDictionary(f => f.Label, f => f.Value.Text);
                Assert.Equal((c.Students.Count.ToString(), c.Capacity.ToString(), Math.Max(0, c.Capacity - c.Students.Count).ToString()),
                    (values["Enrolled students"], values["Class capacity"], values["Available places"]));
                Assert.Equal(c.Homeroom ?? "Not assigned", values["Homeroom Teacher"]);
                Assert.Equal(c.CoTeacher ?? "Not assigned", values["Co-Teacher"]);
            }
            // Every student exactly once across the pages.
            Assert.Equal(await db.Students.CountAsync(), report.Pages.Sum(p => p.Sections.OfType<PdfTable>().Single().Rows.Count));

            var name = roster.AllClasses.Single(c => c.Id == kg).Name;
            var mine = report.Pages.Single(p => p.Subtitle == $"Class {name}");
            var info = mine.Sections.OfType<PdfFields>().Single().Items.ToDictionary(f => f.Label, f => f.Value.Text);
            if (year is not null) Assert.Equal(teacherName, info["Homeroom Teacher"]);
            Assert.Equal(("KG Stages", "KG2", "Not assigned"), (info["Stage"], info["Grade"], info["Co-Teacher"]));
            var rows = mine.Sections.OfType<PdfTable>().Single().Rows;
            Assert.Equal(61, rows.Count);
            Assert.Contains(rows, r => r[0].Text == arabic);

            // The real PDF: A4 portrait (595 × 842 pt), exactly one page per class — none split, none shared.
            var pdf = await fx.Factory.CreateClient().GetAsync("/classes/students-report");
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
            var bytes = await pdf.Content.ReadAsByteArrayAsync();
            var (pages, sizes) = PdfPages(bytes);
            Assert.Equal(await db.Classes.CountAsync(), pages);
            Assert.Equal(["595x842"], sizes);

            // Read back: page i holds class i — its heading and every one of its students — and nothing about student ids.
            static string Squash(string s) => Regex.Replace(s, @"\s+", "");
            using var doc = UglyToad.PdfPig.PdfDocument.Open(bytes);
            var texts = doc.GetPages().Select(p => Squash(p.Text)).ToList();
            var fresh = ClassRosterReports.Placed(await Service(db).LoadAsync()).Select(x => x.Class).ToList();
            Assert.Equal(fresh.Count, texts.Count);
            for (var i = 0; i < texts.Count; i++)
            {
                Assert.Contains(Squash($"Class {fresh[i].Name}"), texts[i]);
                Assert.Contains(fresh[i].Students.Count == 0 ? "Nostudentsareenrolledinthisclass." : "#StudentName", texts[i]);
                Assert.DoesNotContain("StudentID", texts[i]);
                Assert.Contains(Squash($"Page {i + 1} of {texts.Count}"), texts[i]);
                // Latin names are compared as text (PdfPig returns Arabic in visual order; its rendering is checked on screen).
                foreach (var s in fresh[i].Students.Where(s => !IsArabic(s.FullName)))
                    Assert.Contains(Squash(s.FullName), texts[i]);
                // Another class's students are never on this page.
                var here = fresh[i].Students.Select(s => Squash(s.FullName)).ToList();
                foreach (var other in fresh.Where((_, j) => j != i).SelectMany(c => c.Students).Where(s => !IsArabic(s.FullName)))
                {
                    var otherName = Squash(other.FullName);
                    if (!here.Any(h => h.Contains(otherName, StringComparison.Ordinal))) Assert.DoesNotContain(otherName, texts[i]);
                }
            }
        }
        finally
        {
            await db.Students.Where(s => s.SchoolClassId == kg).ExecuteDeleteAsync();
            await db.ClassTeachingHistory.Where(h => h.SchoolClassId == kg).ExecuteDeleteAsync();
            await db.ClassTeachingAssignments.Where(a => a.SchoolClassId == kg).ExecuteDeleteAsync();
            await db.Classes.Where(c => c.Id == kg).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Summary_report_has_school_stage_and_class_figures_in_portrait()
    {
        await using var db = fx.NewContext();
        var roster = await Service(db).LoadAsync();
        var report = ClassRosterReports.Summary(roster, "Test School", null, null);
        var all = roster.AllClasses.ToList();

        Assert.True(report.Portrait);
        var stats = report.Sections.OfType<PdfSummary>().Single().Items.ToDictionary(i => i.Label, i => i.Value);
        Assert.Equal(all.Count.ToString(), stats["Classes"]);
        Assert.Equal(all.Count(c => c.IsActive).ToString(), stats["Active classes"]);
        Assert.Equal(all.Count(c => !c.IsActive).ToString(), stats["Inactive classes"]);
        Assert.Equal((await db.Students.CountAsync()).ToString(), stats["Students enrolled"]);
        Assert.Equal((await db.Classes.SumAsync(c => c.Capacity)).ToString(), stats["Total capacity"]);
        Assert.Equal(all.Sum(c => Math.Max(0, c.Capacity - c.Students.Count)).ToString(), stats["Available places"]);

        var stages = report.Sections.OfType<PdfTable>().Single(t => t.Title == "Academic stages");
        Assert.Equal(roster.Stages.Sum(s => s.Groups.Count) + (roster.Other.Count > 0 ? 1 : 0), stages.Rows.Count);
        Assert.Contains("Coordinator", stages.Columns.Select(c => c.Header));
        // Every class once in the class summaries, with its teachers and coordinator columns.
        var summaries = report.Sections.OfType<PdfTable>().Where(t => t.Title?.StartsWith("Class summary", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(all.Select(c => c.Name).Order(), summaries.SelectMany(t => t.Rows.Select(r => r[0].Text)).Order());
        Assert.All(summaries, t => Assert.Equal(["Class", "Grade", "Students", "Capacity", "Available", "Status", "Homeroom Teacher", "Co-Teacher", "Coordinator"],
            t.Columns.Select(c => c.Header)));

        var pdf = await fx.Factory.CreateClient().GetAsync("/classes/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["595x842"], PdfPages(await pdf.Content.ReadAsByteArrayAsync()).Sizes);
    }

    [Fact]
    public async Task Classes_page_offers_four_independent_downloads_only_there()
    {
        var client = fx.Factory.CreateClient();
        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/classes"));
        var start = html.IndexOf("class-downloads", StringComparison.Ordinal);
        var group = html[start..html.IndexOf("</div>", start, StringComparison.Ordinal)];
        // Summary PDF, Summary Excel, Students PDF, Students Excel — in that order, each with its own endpoint and label.
        var links = Regex.Matches(group, "href=\"([^\"]+)\"[^>]*aria-label=\"([^\"]+)\"").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.Equal(
            [("/classes/pdf", "Download Summary PDF"), ("/classes/summary-excel", "Download Summary Excel"),
             ("/classes/students-report", "Download Students PDF"), ("/classes/students-excel", "Download Students Excel")], links);
        Assert.Equal(2, Regex.Matches(group, "dl-pdf").Count);
        Assert.Equal(2, Regex.Matches(group, "dl-xlsx").Count);
        Assert.True(html.IndexOf("All classes", StringComparison.Ordinal) < start);
        foreach (var url in new[] { "/students", "/classes/stages/kg", "/classes/stages/elementary", "/classes/stages/high", "/classes/all" })
        {
            var page = await client.GetStringAsync(url);
            foreach (var endpoint in new[] { "/classes/students-report", "/classes/summary-excel", "/classes/students-excel" })
                Assert.DoesNotContain(endpoint, page);
        }
    }

    private static bool IsArabic(string s) => s.Any(ch => ch is >= '؀' and <= 'ۿ');

    [GeneratedRegex(@"/Type\s*/Page(?!s)")]
    private static partial Regex PageObject();

    [GeneratedRegex(@"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]")]
    private static partial Regex MediaBox();
}
