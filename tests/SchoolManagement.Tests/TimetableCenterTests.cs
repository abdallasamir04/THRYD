using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using UglyToad.PdfPig;

namespace SchoolManagement.Tests;

/// <summary>
/// Timetables Center: every timetable type is listed, filters depend on the category, and downloads give one
/// PDF or a ZIP of separate PDFs in category / department folders — read from the existing timetable without changing it.
/// </summary>
[Collection("db")]
public class TimetableCenterTests(DbFixture fx)
{
    private const int Arabic = 1;

    private sealed record Setup(int ClassId, string ClassName, int TeacherA, string NameA, int TeacherB, string NameB);

    /// <summary>A new class taught Arabic by two new Arabic teachers this week (through the normal timetable service).</summary>
    private async Task<Setup> ArabicLessonsAsync()
    {
        var classId = await fx.NewClassAsync();
        await using var db = fx.NewContext();
        var teachers = DbFixture.Teachers(db);
        var a = await teachers.CreateAsync("Arabic Teacher " + DbFixture.Unique(), "Teacher", null, Arabic, null, null);
        var b = await teachers.CreateAsync("Arabic Teacher " + DbFixture.Unique(), "Teacher", null, Arabic, null, null);
        Assert.True(a.Succeeded && b.Succeeded);
        var tt = DbFixture.Timetable(db);
        Assert.True((await tt.AssignAsync(new SessionInput(classId, DayOfWeek.Sunday, 1, Arabic, "Arabic", a.EntityId, null))).Succeeded);
        Assert.True((await tt.AssignAsync(new SessionInput(classId, DayOfWeek.Monday, 2, Arabic, "Arabic", b.EntityId, null))).Succeeded);
        var className = await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync();
        var names = await db.Teachers.Where(t => t.Id == a.EntityId || t.Id == b.EntityId).ToDictionaryAsync(t => t.Id, t => t.FullName);
        return new Setup(classId, className, a.EntityId!.Value, names[a.EntityId!.Value], b.EntityId!.Value, names[b.EntityId!.Value]);
    }

    private static async Task<(string Type, string FileName, byte[] Bytes)> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}");
        return (response.Content.Headers.ContentType?.MediaType ?? "", response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "",
            await response.Content.ReadAsByteArrayAsync());
    }

    private static string PdfText(byte[] bytes)
    {
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        using var doc = PdfDocument.Open(bytes);
        Assert.All(doc.GetPages(), p => Assert.True(p.Width > p.Height)); // landscape
        return Regex.Replace(string.Concat(doc.GetPages().Select(p => p.Text)), @"\s+", "");
    }

    private static List<string> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", "");

    [Fact]
    public async Task Every_existing_timetable_type_is_listed_with_category_dependent_filters()
    {
        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync("/timetables");
        foreach (var title in new[] { "Class Timetables", "Teacher Timetables", "Subject Timetables", "Department Timetables", "School-wide Timetable", "P.E. Timetable", "Duty Rotas", "Bell Schedule" })
            Assert.Contains($"<span class=\"hub-card__title\">{title}</span>", page);
        Assert.Contains("href=\"/timetables\" aria-current=\"page\"", page[..page.IndexOf("</aside>", StringComparison.Ordinal)]);

        // Filters follow the category.
        var teacher = await client.GetStringAsync("/timetables?category=teacher");
        foreach (var f in new[] { "name=\"departmentId\"", "name=\"subject\"", "name=\"teacherId\"", "name=\"yearId\"", "name=\"from\"", "name=\"day\"" }) Assert.Contains(f, teacher);
        Assert.DoesNotContain("name=\"classId\"", teacher);
        var cls = await client.GetStringAsync("/timetables?category=class");
        Assert.Contains("name=\"classId\"", cls);
        Assert.DoesNotContain("name=\"teacherId\"", cls);
        var bells = await client.GetStringAsync("/timetables?category=bells");
        Assert.DoesNotContain("name=\"departmentId\"", bells);
        Assert.DoesNotContain("name=\"day\"", bells);
    }

    [Fact]
    public async Task Arabic_teachers_subjects_and_department_download_individually_or_together()
    {
        var s = await ArabicLessonsAsync();
        var client = fx.Factory.CreateClient();
        int before;
        await using (var db = fx.NewContext()) before = await db.TimetableAssignments.CountAsync();

        // The Arabic filter lists both teachers.
        var list = WebUtility.HtmlDecode(await client.GetStringAsync($"/timetables?category=teacher&departmentId={Arabic}"));
        Assert.Contains(s.NameA, list);
        Assert.Contains(s.NameB, list);
        Assert.Contains("All Arabic schedules (ZIP)", list);

        // 1. One teacher → one PDF.
        var one = await GetAsync(client, $"/timetables/download?category=teacher&departmentId={Arabic}&ids={s.TeacherA}");
        Assert.Equal("application/pdf", one.Type);
        var text = PdfText(one.Bytes);
        Assert.Contains(Squash("Teacher Timetable"), text);
        Assert.Contains(Squash(s.NameA), text);
        Assert.Contains(Squash(s.ClassName), text);
        Assert.Contains("Exported", text);

        // 2. All Arabic teachers → ZIP with a PDF each under Teacher Timetables/Arabic/.
        var all = await GetAsync(client, $"/timetables/download?category=teacher&departmentId={Arabic}");
        Assert.Equal("application/zip", all.Type);
        Assert.EndsWith(".zip", all.FileName);
        var entries = Entries(all.Bytes);
        Assert.All(entries, e => Assert.StartsWith("Teacher Timetables/Arabic/", e));
        Assert.Contains(entries, e => e.EndsWith(Web.Services.Pdf.PdfFileName.From(["Teacher Timetable", s.NameA])));
        Assert.Contains(entries, e => e.EndsWith(Web.Services.Pdf.PdfFileName.From(["Teacher Timetable", s.NameB])));

        // 3. Arabic subject across all classes → the subject PDF lists our class with both teachers.
        var subject = await GetAsync(client, $"/timetables/download?category=subject&departmentId={Arabic}&subject=Arabic");
        Assert.Equal("application/pdf", subject.Type);
        var subjectText = PdfText(subject.Bytes);
        foreach (var v in new[] { "Subject Timetable", "Subject: Arabic", s.ClassName, s.NameA, s.NameB }) Assert.Contains(Squash(v), subjectText);

        // 4. All Arabic schedules: department + subject + teacher timetables in one ZIP.
        var pack = Entries((await GetAsync(client, $"/timetables/department?departmentId={Arabic}")).Bytes);
        Assert.Contains("Department Timetables/Department_Timetable_Arabic.pdf", pack);
        Assert.Contains(pack, e => e.StartsWith("Subject Timetables/") && e.Contains("Arabic"));
        Assert.Contains(pack, e => e == "Teacher Timetables/" + Web.Services.Pdf.PdfFileName.From(["Teacher Timetable", s.NameA]));
        Assert.DoesNotContain(pack, e => e.StartsWith("Class Timetables/"));

        // The department PDF has a page per school day with the Arabic teachers.
        var dept = await GetAsync(client, $"/timetables/download?category=department&departmentId={Arabic}");
        var deptText = PdfText(dept.Bytes);
        using (var doc = PdfDocument.Open(dept.Bytes)) Assert.True(doc.NumberOfPages >= 5);
        Assert.Contains(Squash(s.NameA), deptText);

        // Nothing in the timetable changed.
        await using (var db = fx.NewContext()) Assert.Equal(before, await db.TimetableAssignments.CountAsync());
    }

    [Fact]
    public async Task Class_and_department_categories_download_all_and_the_whole_school_includes_every_type()
    {
        var s = await ArabicLessonsAsync();
        var client = fx.Factory.CreateClient();

        // One class.
        var cls = await GetAsync(client, $"/timetables/download?category=class&classId={s.ClassId}");
        Assert.Equal("application/pdf", cls.Type);
        Assert.Contains(Squash($"Class: {s.ClassName}"), PdfText(cls.Bytes));

        // All classes: one PDF per listed class.
        var classCount = Regex.Matches(await client.GetStringAsync("/timetables?category=class"), "data-tt-item").Count;
        var classes = Entries((await GetAsync(client, "/timetables/download-all?category=class")).Bytes);
        Assert.Equal(classCount, classes.Count);
        Assert.All(classes, e => Assert.StartsWith("Class Timetables/", e));
        Assert.Contains("Class Timetables/" + Web.Services.Pdf.PdfFileName.From(["Class Timetable", s.ClassName]), classes);

        // All departments.
        var depts = Entries((await GetAsync(client, "/timetables/download-all?category=department")).Bytes);
        Assert.Contains("Department Timetables/Department_Timetable_Arabic.pdf", depts);

        // Entire school: every registered timetable type is in the ZIP, P.E. included.
        var school = Entries((await GetAsync(client, "/timetables/download-all?category=all")).Bytes);
        foreach (var folder in new[] { "Class Timetables/", "Teacher Timetables/", "Subject Timetables/", "Department Timetables/", "School Timetable/", "PE Timetable/", "Duty Rotas/", "Bell Schedule/" })
            Assert.Contains(school, e => e.StartsWith(folder));
        Assert.Equal(school.Count, school.Distinct().Count()); // separate, uniquely named PDFs
    }

    [Fact]
    public async Task Day_filter_and_week_range_shape_the_downloads()
    {
        var s = await ArabicLessonsAsync();
        var client = fx.Factory.CreateClient();
        var week = SchoolWeek.Current;

        // Only Sunday: the class PDF shows Sunday and not Monday.
        var sunday = PdfText((await GetAsync(client, $"/timetables/download?category=class&classId={s.ClassId}&day=sunday")).Bytes);
        Assert.Contains("Sunday", sunday);
        Assert.DoesNotContain("Monday", sunday);

        // Two weeks → one PDF per week in a ZIP.
        var from = SchoolWeek.Iso(week);
        var to = SchoolWeek.Iso(week.AddDays(8));
        var two = await GetAsync(client, $"/timetables/download?category=class&classId={s.ClassId}&from={from}&to={to}");
        Assert.Equal("application/zip", two.Type);
        Assert.Equal(2, Entries(two.Bytes).Count);

        // School-wide timetable: one page per day.
        var schoolPdf = await GetAsync(client, "/timetables/download?category=school");
        using var doc = PdfDocument.Open(schoolPdf.Bytes);
        Assert.True(doc.NumberOfPages >= 5);

        // Nothing selected → a THRYD warning, no file.
        var none = await fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .GetAsync("/timetables/download?category=teacher&ids=999999999");
        Assert.Equal(HttpStatusCode.Redirect, none.StatusCode);
    }
}
