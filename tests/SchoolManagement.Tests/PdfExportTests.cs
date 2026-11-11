using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Pdf;
using UglyToad.PdfPig;

namespace SchoolManagement.Tests;

/// <summary>
/// The shared PDF downloads, through the running app: each is a landscape PDF with the THRYD header, a
/// title built from the record, and the real (filtered, all-pages) data. PdfPig reads the files back.
/// Set PDF_PREVIEW_DIR to also save the downloaded files there for a visual check.
/// </summary>
[Collection("db")]
public class PdfExportTests(DbFixture fx)
{
    private sealed record Pdf(byte[] Bytes, string FileName, int Pages, bool Landscape, string Text);

    private async Task<Pdf> DownloadAsync(HttpClient client, string url, string? preview = null)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}");
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        if (preview is not null && Environment.GetEnvironmentVariable("PDF_PREVIEW_DIR") is { Length: > 0 } dir)
            await File.WriteAllBytesAsync(Path.Combine(dir, preview + ".pdf"), bytes);

        using var doc = PdfDocument.Open(bytes);
        var pages = doc.GetPages().ToList();
        return new Pdf(bytes, response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "", doc.NumberOfPages,
            pages.All(p => p.Width > p.Height), Squash(string.Concat(pages.Select(p => p.Text))));
    }

    /// <summary>PdfPig returns the page text without reliable spaces, so comparisons ignore whitespace.</summary>
    private static string Squash(string s) => Regex.Replace(s, @"\s+", "");

    private static void Has(Pdf pdf, params string[] values)
    {
        foreach (var v in values) Assert.True(pdf.Text.Contains(Squash(v)), $"\"{v}\" not found in {pdf.FileName}");
    }

    [Fact]
    public void File_names_are_built_from_the_record_and_safe()
    {
        Assert.Equal("Teacher_Timetable_Taha_Fawzy.pdf", PdfFileName.From(["Teacher Timetable", "Taha Fawzy"]));
        Assert.Equal("Class_Timetable_G10A.pdf", PdfFileName.From(["Class Timetable", "G10A"]));
        Assert.Equal("Department_Report_Library_Reading.pdf", PdfFileName.From(["Department Report", "Library / Reading"]));
        Assert.Equal("etc_passwd.pdf", PdfFileName.From(["../../etc/passwd", null, "  "]));
        Assert.Equal("Student_Attendance_أحمد_علي.pdf", PdfFileName.From(["Student Attendance", "أحمد علي"]));
        Assert.Equal("THRYD_Report.pdf", PdfFileName.From([null, "///"]));
    }

    [Fact]
    public async Task Teacher_and_class_timetables_download_with_their_own_title_and_sessions()
    {
        var name = "Teacher " + DbFixture.Unique();
        var classId = await fx.NewClassAsync();
        int teacher, math;
        string className;
        await using (var db = fx.NewContext())
        {
            math = await db.Departments.Where(d => d.NormalizedName == "MATH").Select(d => d.Id).SingleAsync();
            teacher = (await DbFixture.Teachers(db).CreateAsync(name, "Math Teacher", null, math, null, null)).EntityId!.Value;
            className = await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync();
            var slot = await db.TimeSlots.OrderBy(s => s.Number).Select(s => s.Id).FirstAsync();
            var session = await DbFixture.Timetable(db).AssignAsync(new SessionInput(classId, DayOfWeek.Monday, slot, math, "Math", teacher, null));
            Assert.True(session.Succeeded, string.Join(" ", session.Errors.Select(e => e.Value)));
        }

        var client = fx.Factory.CreateClient();
        var page = await client.GetStringAsync($"/timetable/teachers/{teacher}");
        Assert.Contains($"data-download=\"/timetable/teachers/{teacher}/pdf\"", page);
        Assert.Contains("Download Timetable", page);

        var t = await DownloadAsync(client, $"/timetable/teachers/{teacher}/pdf", "teacher-timetable");
        Assert.True(t.Landscape);
        Assert.Equal($"Teacher_Timetable_{name.Replace(' ', '_')}.pdf", t.FileName);
        Has(t, "Teacher Timetable", $"Teacher Name: {name}", "Week", "Exported", className, "Math", "Sunday", "Thursday", "Page 1 of");
        for (var p = 1; p <= 10; p++) Has(t, $"Period {p}");

        var c = await DownloadAsync(client, $"/timetable/classes/{classId}/pdf", "class-timetable");
        Assert.True(c.Landscape);
        Assert.Equal($"Class_Timetable_{className}.pdf", c.FileName);
        Has(c, "Class Timetable", $"Class: {className}", name, "Math", "Period 10");
    }

    [Fact]
    public async Task PE_timetable_downloads_from_the_PE_department_page()
    {
        var client = fx.Factory.CreateClient();
        int pe;
        await using (var db = fx.NewContext()) pe = await db.Departments.Where(d => d.NormalizedName == "PE").Select(d => d.Id).SingleAsync();

        var page = await client.GetStringAsync($"/staff/departments/{pe}?date=2026-11-18");
        var section = page[page.IndexOf("<section class=\"dept-pe\"", StringComparison.Ordinal)..];
        Assert.Contains("data-download=\"/pe/pdf\"", section);
        Assert.Contains("Download Timetable", section);
        Assert.Contains($"data-download=\"/staff/departments/{pe}/pdf\"", page); // Department Staff → Download Report

        var pdf = await DownloadAsync(client, "/pe/pdf?date=2026-11-18", "pe-timetable");
        Assert.True(pdf.Landscape);
        Assert.Equal("PE_Timetable_2026-11-15.pdf", pdf.FileName);
        Has(pdf, "P.E. Timetable", "Physical Education", "15–19 Nov 2026", "KG Garden", "Swimming Pool", "Period 1", "Period 10", "Sunday", "Thursday");

        var dept = await DownloadAsync(client, $"/staff/departments/{pe}/pdf", "department-report");
        Assert.Equal("Department_Report_PE.pdf", dept.FileName);
        Has(dept, "Department Report", "Department: PE", "Department staff", "HOD history");
    }

    [Fact]
    public async Task Student_attendance_exports_the_class_and_tab_including_Arabic_names()
    {
        var classId = await fx.NewClassAsync();
        var arabic = "أحمد محمود " + DbFixture.Unique();
        int recorded, missing;
        string className;
        await using (var db = fx.NewContext())
        {
            recorded = (await DbFixture.Students(db).CreateAsync(arabic, classId)).EntityId!.Value;
            missing = (await DbFixture.Students(db).CreateAsync("Student " + DbFixture.Unique(), classId)).EntityId!.Value;
            className = await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync();
            var saved = await DbFixture.Arrivals(db).RecordAsync(SchoolWeek.Today,
                [new(recorded, ArrivalStatus.Present, new TimeOnly(7, 20), ArrivalMethod.SchoolBus, "Bus Bay", null, false, "On the 7:00 bus")]);
            Assert.True(saved.Single().Saved, saved.Single().Error);
        }
        var missingName = await NameOfStudentAsync(missing);
        var client = fx.Factory.CreateClient();
        Assert.Contains("data-download=\"/arrivals/pdf\"", await client.GetStringAsync($"/arrivals?classId={classId}"));

        var all = await DownloadAsync(client, $"/arrivals/pdf?classId={classId}", "attendance");
        Assert.True(all.Landscape);
        Assert.Equal($"Student_Attendance_{className}_{SchoolWeek.Iso(SchoolWeek.Today)}.pdf", all.FileName);
        Has(all, "Student Attendance", $"Class: {className}", "Present", "07:20", "School bus", "On the 7:00 bus", missingName, "Not recorded");
        // The Arabic name is embedded as Arabic text (shaped with the bundled Noto Sans Arabic font).
        Assert.Contains(all.Text, ch => ch is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﻿');

        // The "Not recorded" tab exports only the students without a record.
        var tab = await DownloadAsync(client, $"/arrivals/pdf?classId={classId}&show=missing");
        Has(tab, "Showing: Not recorded", missingName);
        Assert.DoesNotContain("Onthe7:00bus", tab.Text);
    }

    [Fact]
    public async Task Duty_audit_exports_every_matching_duty_not_just_the_visible_page()
    {
        var (staff, staffName) = await fx.NewTeacherAsync();
        var day = SchoolWeek.StartOf(new DateOnly(2032, 5, 12));
        var token = DbFixture.Unique();
        await using (var db = fx.NewContext())
        {
            var duties = DbFixture.Duties(db);
            for (var i = 0; i < 30; i++)
            {
                var start = new TimeOnly(6, 0).AddMinutes(i * 15);
                var r = await duties.CreateAsync(new(DutyComponent.StudentsArrival, day, $"Gate {token} {i:00}", start, start.AddMinutes(10), "Main gate",
                    staff, AttendanceStatus.NotRecorded, null, null, []));
                Assert.True(r.Succeeded, string.Join(" ", r.Errors.Select(e => e.Value)));
            }
        }

        var client = fx.Factory.CreateClient();
        Assert.Contains("data-download=\"/duties/audit/pdf\"", await client.GetStringAsync("/duties/audit"));
        // The page shows 25 per page; the download has all 30.
        var pdf = await DownloadAsync(client, $"/duties/audit/pdf?from={SchoolWeek.Iso(day)}&to={SchoolWeek.Iso(day)}&staffId={staff}&page=1&pageSize=25", "duty-audit");
        Assert.True(pdf.Landscape);
        Assert.True(pdf.Pages >= 2);
        Has(pdf, "Duty Audit Report", day.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture), $"Staff: {staffName}", "Duty sessions: 30");
        for (var i = 0; i < 30; i++) Has(pdf, $"Gate {token} {i:00}");
        Has(pdf, $"Page {pdf.Pages} of {pdf.Pages}");
    }

    [Fact]
    public async Task Every_download_button_returns_a_landscape_pdf()
    {
        var (teacher, _) = await fx.NewTeacherAsync();
        var classId = await fx.NewClassAsync();
        var client = fx.Factory.CreateClient();
        foreach (var (page, export) in new[]
        {
            ("/staff?view=duties", "/staff/duty-records/pdf"),
            ("/workload", "/workload/pdf"),
            ($"/workload/teacher/{teacher}", $"/workload/teacher/{teacher}/pdf"),
            ("/staff/departments/heads", "/staff/departments/heads/pdf"),
            ("/staff/members", "/staff/members/pdf"),
            ("/students", "/students/pdf"),
            ("/classes", "/classes/pdf"),
            ("/management/members", "/management/members/pdf"),
            ("/management/tasks", "/management/tasks/pdf"),
            ("/pe", "/pe/pdf"),
            ($"/timetable/classes/{classId}", $"/timetable/classes/{classId}/pdf"),
        })
        {
            Assert.Contains($"data-download=\"{export}\"", await client.GetStringAsync(page));
            var pdf = await DownloadAsync(client, export);
            Assert.True(pdf.Landscape, export);
            Assert.True(pdf.Pages >= 1, export);
            Assert.EndsWith(".pdf", pdf.FileName);
            Has(pdf, "THRYD", "Exported", "Page 1 of");
        }

        // Filters carry over: the staff list filtered by a search finds just that person.
        var only = await DownloadAsync(client, "/staff/members/pdf?search=" + Uri.EscapeDataString((await NameOfTeacherAsync(teacher))));
        Has(only, "Staff members: 1");
    }

    private async Task<string> NameOfStudentAsync(int id)
    {
        await using var db = fx.NewContext();
        return await db.Students.Where(s => s.Id == id).Select(s => s.FullName).SingleAsync();
    }

    private async Task<string> NameOfTeacherAsync(int id)
    {
        await using var db = fx.NewContext();
        return await db.Teachers.Where(s => s.Id == id).Select(s => s.FullName).SingleAsync();
    }
}
