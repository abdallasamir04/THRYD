using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Timetables.Import;

namespace SchoolManagement.Tests;

/// <summary>
/// Timetables → Upload Timetable end to end: the entry points, file checks, extraction into an editable preview that
/// saves nothing, teacher matching, conflict detection by a rolled-back dry run, saving through the existing timetable
/// services, the summary, and replacing a class's timetable only after confirmation (history kept).
/// </summary>
[Collection("db")]
public class TimetableUploadTests(DbFixture fx)
{
    private const int Arabic = 1, English = 2, Math = 3, Science = 4, Ict = 10;
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private HttpClient Client() => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<int> TeacherAsync(string name, int department, string? sub = null)
    {
        await using var db = fx.NewContext();
        var r = await DbFixture.Teachers(db).CreateAsync(name, "Teacher", null, department, sub, null);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        return r.EntityId!.Value;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, int? classId, byte[] bytes, string fileName, int? yearId = null)
    {
        var token = Token.Match(await client.GetStringAsync("/timetables/upload")).Groups[1].Value;
        var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(classId?.ToString() ?? ""), "classId" },
        };
        if (yearId is not null) content.Add(new StringContent(yearId.ToString()!), "yearId");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        return await client.PostAsync("/timetables/upload", content);
    }

    /// <summary>The values the preview form would post (inputs, checked boxes, each select's chosen option), like a browser.</summary>
    private static List<KeyValuePair<string, string>> Fields(string html)
    {
        var start = html.IndexOf("action=\"/timetables/upload/preview\"", StringComparison.Ordinal);
        Assert.True(start > 0, "No preview form");
        var form = html[start..html.IndexOf("</form>", start, StringComparison.Ordinal)];
        string? Attr(string tag, string name) => Regex.Match(tag, $"\\s{name}=\"([^\"]*)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match m in Regex.Matches(form, "<input\\b[^>]*>"))
        {
            var tag = m.Value;
            if (Attr(tag, "name") is not { } name) continue;
            if (Attr(tag, "type") == "checkbox" && !tag.Contains(" checked")) continue;
            fields.Add(new(name, Attr(tag, "value") ?? ""));
        }
        foreach (Match m in Regex.Matches(form, "<select\\b([^>]*)>(.*?)</select>", RegexOptions.Singleline))
        {
            if (Attr(m.Groups[1].Value, "name") is not { } name || m.Groups[1].Value.Contains(" disabled")) continue;
            var options = Regex.Matches(m.Groups[2].Value, "<option\\b[^>]*>").Select(o => o.Value).ToList();
            var chosen = options.FirstOrDefault(o => o.Contains(" selected")) ?? options.FirstOrDefault();
            fields.Add(new(name, chosen is null ? "" : Attr(chosen, "value") ?? ""));
        }
        return fields;
    }

    private static void Set(List<KeyValuePair<string, string>> fields, string name, string value)
    {
        fields.RemoveAll(f => f.Key == name);
        fields.Add(new(name, value));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, List<KeyValuePair<string, string>> fields, string op)
    {
        var all = fields.Where(f => f.Key != "Op").Append(new("Op", op)).ToList();
        return client.PostAsync("/timetables/upload/preview", new FormUrlEncodedContent(all));
    }

    /// <summary>The form field prefix of the cell in a period row (by its label) on a day.</summary>
    private static string CellName(List<KeyValuePair<string, string>> fields, string periodLabel, DayOfWeek day)
    {
        var row = Regex.Match(fields.Single(f => f.Key.EndsWith(".Label") && !f.Key.Contains("Cells") && f.Value == periodLabel).Key, @"Periods\[(\d+)\]").Groups[1].Value;
        var cell = fields.Single(f => f.Key.StartsWith($"Periods[{row}].Cells[") && f.Key.EndsWith(".Day") && f.Value == day.ToString()).Key;
        return cell[..^".Day".Length];
    }

    private async Task<(int Slots, int Lessons, int Imports)> CountsAsync()
    {
        await using var db = fx.NewContext();
        return (await db.TimeSlots.CountAsync(), await db.TimetableAssignments.CountAsync(), await db.TimetableImports.CountAsync());
    }

    [Fact]
    public async Task Upload_Timetable_is_on_the_Timetables_page_and_on_each_class_timetable()
    {
        var client = Client();
        Assert.Contains("<a class=\"btn btn--secondary\" href=\"/timetables/upload\">", await client.GetStringAsync("/timetables"));
        var cls = await fx.NewClassAsync();
        Assert.Contains($"href=\"/timetables/upload?classId={cls}\"", await client.GetStringAsync($"/timetable/classes/{cls}"));
        var page = await client.GetStringAsync($"/timetables/upload?classId={cls}");
        Assert.Contains("Upload Timetable", page);
        Assert.Matches($"<option value=\"{cls}\" selected=\"selected\"", page);       // the class is preselected
        Assert.Contains("name=\"yearId\"", page);
    }

    [Fact]
    public async Task Invalid_files_are_refused_with_a_clear_message_and_nothing_is_saved()
    {
        var client = Client();
        var cls = await fx.NewClassAsync();
        var before = await CountsAsync();
        async Task Refused(int? classId, byte[] bytes, string name, string message)
        {
            var response = await UploadAsync(client, classId, bytes, name);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains(message, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        }
        var good = TimetableExtractorTests.Workbook(("S", [["", "Sunday", "Monday"], ["1", "Math", "English"]], []));
        await Refused(null, good, "t.xlsx", "Choose the class this timetable belongs to.");
        await Refused(cls, [], "t.xlsx", "The file is empty.");
        await Refused(cls, "%PDF-1.7 fake"u8.ToArray(), "timetable.pdf", "A PDF or a picture cannot be read automatically");
        await Refused(cls, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], "scan.jpg", "A PDF or a picture cannot be read automatically");
        await Refused(cls, "not a workbook"u8.ToArray(), "renamed.xlsx", "not a valid Excel workbook");
        await Refused(cls, "x"u8.ToArray(), "notes.docx", "Excel workbook (.xlsx) or a CSV file");
        await Refused(cls, TimetableExtractorTests.Workbook(("List", [["Name", "Class"], ["Ali", "G1A"]], [])), "list.xlsx", "No timetable was found");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task A_timetable_is_read_reviewed_corrected_and_saved_as_the_class_timetable()
    {
        var u = DbFixture.Unique();
        var math = await TeacherAsync($"Hana{u} Noor{u}", Math);
        var english = await TeacherAsync($"Omar{u} Saleh{u}", English);
        var cls = await fx.NewClassAsync();
        var className = await NameAsync(cls);
        var client = Client();
        var before = await CountsAsync();

        var file = TimetableExtractorTests.Workbook((className, [
            ["Period", "Time", "Sunday", "Monday", "Tuesday"],
            ["1", "08:00 - 08:45", $"Math\nMs Hana{u} Noor{u}", $"English - Mr Omar{u}", "Arabic"],
            ["2", "08:45 - 09:30", "Robotics", "Free", $"Science\nMr Nobody{u}"],
            [null, "09:30 - 09:50", "Break", null, null],
            ["3", "09:50 - 10:35", "ICT (Room 4)", "Morning Assembly", $"Math / Hana{u} Noor{u}"],
        ], ["C4:E4"]));

        // Upload: a preview of what was read. Nothing is saved.
        var response = await UploadAsync(client, cls, file, "Timetable G1A.xlsx");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains($"Review the timetable of {className}", html);
        Assert.Contains("Timetable G1A.xlsx", html);
        Assert.Contains("“Robotics” is not a subject of the school", html);                    // unrecognised subject: blocks saving
        Assert.Contains($"“Mr Omar{u}” was matched to Omar{u} Saleh{u} by part of the name", html); // a one-name match to check
        Assert.Contains($"no staff member matches “Mr Nobody{u}”", html);                       // unmatched teacher: flagged, unassigned
        Assert.Matches("type=\"submit\" name=\"Op\" value=\"save\" disabled", html);
        Assert.Equal(before, await CountsAsync());

        var fields = Fields(html);
        var sunday1 = CellName(fields, "1", DayOfWeek.Sunday);
        Assert.Equal(ImportCellRow.SubjectKey(Math, "Math"), fields.Single(f => f.Key == sunday1 + ".Subject").Value);
        Assert.Equal(math.ToString(), fields.Single(f => f.Key == sunday1 + ".TeacherId").Value);
        Assert.Equal(english.ToString(), fields.Single(f => f.Key == CellName(fields, "1", DayOfWeek.Monday) + ".TeacherId").Value);
        Assert.Equal("Room 4", fields.Single(f => f.Key == CellName(fields, "3", DayOfWeek.Sunday) + ".Notes").Value);
        Assert.Equal(((int)ImportCellKind.Activity).ToString(), fields.Single(f => f.Key == CellName(fields, "3", DayOfWeek.Monday) + ".Kind").Value);

        // Correct it: "Robotics" is really Science, taught by the Math teacher's colleague → leave it unassigned; check again.
        Set(fields, CellName(fields, "2", DayOfWeek.Sunday) + ".Subject", ImportCellRow.SubjectKey(Science, "Science"));
        response = await PostAsync(client, fields, "check");
        html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("is not a subject of the school", html);
        Assert.Contains("Ready to save.", html);
        Assert.Equal(before, await CountsAsync());                                              // the check (a dry run) saved nothing

        // Save.
        response = await PostAsync(client, Fields(html), "save");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var summaryUrl = response.Headers.Location!.OriginalString;
        Assert.Matches(@"^/timetables/upload/\d+$", summaryUrl);

        await using (var db = fx.NewContext())
        {
            var lessons = await db.TimetableAssignments.AsNoTracking().Where(t => t.SchoolClassId == cls && t.EndedOn == null)
                .Select(t => new { t.Day, t.Subject, t.TeacherId, t.Notes, t.TimeSlot.StartTime, t.TimeSlot.Name }).ToListAsync();
            Assert.Equal(3, lessons.Count);   // Math ×2 and English: the lessons with a teacher
            Assert.Contains(lessons, l => l.Day == DayOfWeek.Sunday && l.Subject == "Math" && l.TeacherId == math && l.StartTime == new TimeOnly(8, 0));
            Assert.Contains(lessons, l => l.Day == DayOfWeek.Tuesday && l.Subject == "Math" && l.TeacherId == math && l.StartTime == new TimeOnly(9, 50));
            Assert.Contains(lessons, l => l.Day == DayOfWeek.Monday && l.Subject == "English" && l.TeacherId == english);

            // The class's own periods with the file's times; the break and the assembly as named, disabled periods.
            var map = await DbFixture.Periods(db).MapAsync();
            var own = map.ForClass(cls);
            Assert.All(own, s => Assert.Equal(cls, s.ClassId));                                // no school-wide period is left in this class
            Assert.Equal([new TimeOnly(8, 0), new TimeOnly(8, 45), new TimeOnly(9, 50)], own.Where(s => s.IsActive).Select(s => s.Start!.Value).Order());
            Assert.Contains(own, s => !s.IsActive && s.Name == "Break" && s.Start == new TimeOnly(9, 30) && s.AllDays == false);
            Assert.Contains(own, s => !s.IsActive && s.Name == "Morning Assembly" && s.AppliesOn(DayOfWeek.Monday) && !s.AppliesOn(DayOfWeek.Sunday));
            Assert.Equal(DayOfWeek.Sunday, PeriodDays.Days(own.Single(s => s.IsActive && s.Start == new TimeOnly(9, 50)).DayMask).First());

            var import = await db.TimetableImports.AsNoTracking().SingleAsync(i => i.SchoolClassId == cls);
            Assert.Equal(("Timetable G1A.xlsx", 3, 4, 7, 3, 4), (import.FileName, import.Days, import.Periods, import.SubjectAssignments, import.TeacherAssignments, import.UnassignedTeachers)); // Arabic, Science ×2 and ICT wait for a teacher
            // Other classes are untouched.
            Assert.Equal(before.Lessons + 3, await db.TimetableAssignments.CountAsync());
        }

        // The summary: the counts, the lessons waiting for a teacher (each with an Assign link), and the way to the timetable.
        var summary = WebUtility.HtmlDecode(await client.GetStringAsync(summaryUrl));
        Assert.Contains($"Timetable of {className} imported", summary);
        Assert.Contains("Lessons waiting for a teacher", summary);
        Assert.Contains($"Mr Nobody{u}", summary);
        Assert.Matches($"/timetable/assign\\?classId={cls}&day=Tuesday&slotId=\\d+", summary);
        Assert.Contains($"href=\"/timetable/classes/{cls}\"", summary);

        // The saved timetable is the class's ordinary timetable.
        var timetable = WebUtility.HtmlDecode(await client.GetStringAsync($"/timetable/classes/{cls}"));
        Assert.Contains($"Hana{u} Noor{u}", timetable);
        Assert.Contains("Break", timetable);
        Assert.Contains("08:00", timetable);
    }

    [Fact]
    public async Task A_class_timetable_is_replaced_only_after_confirmation_and_its_history_is_kept()
    {
        var u = DbFixture.Unique();
        var math = await TeacherAsync($"Rana{u} Adel{u}", Math);
        var arabic = await TeacherAsync($"Samir{u} Fawzy{u}", Arabic);
        var cls = await fx.NewClassAsync();
        var client = Client();
        byte[] File(string sunday) => TimetableExtractorTests.Workbook(("T", [
            ["", "Time", "Sunday", "Monday"],
            ["1", "08:00-08:45", sunday, $"Arabic - Samir{u} Fawzy{u}"],
            ["2", "08:45-09:30", $"Math - Rana{u} Adel{u}", "Free"],
        ], []));

        // First upload: the class has no timetable, so it saves without asking.
        var html = WebUtility.HtmlDecode(await (await UploadAsync(client, cls, File($"Math - Rana{u} Adel{u}"), "first.xlsx")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("already has a timetable", html);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, Fields(html), "save")).StatusCode);
        List<int> first;
        await using (var db = fx.NewContext())
            first = await db.TimetableAssignments.Where(t => t.SchoolClassId == cls && t.EndedOn == null).Select(t => t.Id).ToListAsync();
        Assert.Equal(3, first.Count);

        // A replacement: the preview warns; saving without ticking "Replace" changes nothing.
        html = WebUtility.HtmlDecode(await (await UploadAsync(client, cls, File($"Arabic - Samir{u} Fawzy{u}"), "second.xlsx")).Content.ReadAsStringAsync());
        Assert.Contains("already has a timetable (3 lessons)", html);
        var fields = Fields(html);
        Assert.DoesNotContain(fields, f => f.Key == "ReplaceExisting");
        var refused = await PostAsync(client, fields, "save");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Tick “Replace the current timetable” to replace it", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        await using (var db = fx.NewContext())
        {
            Assert.Equal(first.Order(), await db.TimetableAssignments.Where(t => t.SchoolClassId == cls && t.EndedOn == null).Select(t => t.Id).OrderBy(i => i).ToListAsync());
            Assert.Equal(1, await db.TimetableImports.CountAsync(i => i.SchoolClassId == cls));
        }

        // Confirmed: the earlier lessons end (kept as history with the reason), the new ones start.
        Set(fields, "ReplaceExisting", "true");
        var saved = await PostAsync(client, fields, "save");
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await using (var db = fx.NewContext())
        {
            var old = await db.TimetableAssignments.AsNoTracking().Where(t => first.Contains(t.Id)).ToListAsync();
            Assert.All(old, t => Assert.Equal((SchoolWeek.Current, TimetableImportService.ReplaceReason), (t.EndedOn!.Value, t.EndReason!)));
            var now = await db.TimetableAssignments.AsNoTracking().Where(t => t.SchoolClassId == cls && t.EndedOn == null).ToListAsync();
            Assert.Equal(3, now.Count);
            Assert.Contains(now, t => t.Day == DayOfWeek.Sunday && t.Subject == "Arabic" && t.TeacherId == arabic);
            var import = await db.TimetableImports.AsNoTracking().OrderBy(i => i.Id).LastAsync(i => i.SchoolClassId == cls);
            Assert.Equal(3, import.ReplacedLessons);
        }
        Assert.Contains("replacing 3 earlier lessons", WebUtility.HtmlDecode(await client.GetStringAsync(saved.Headers.Location!.OriginalString)));
    }

    [Fact]
    public async Task A_teacher_busy_in_another_class_at_that_time_is_reported_before_saving()
    {
        var u = DbFixture.Unique();
        var teacher = await TeacherAsync($"Laila{u} Hamed{u}", Math);
        var other = await fx.NewClassAsync();
        var cls = await fx.NewClassAsync();
        await using (var db = fx.NewContext())
        {
            // The other class has its own Sunday period 07:30–08:15 where the teacher teaches Math.
            var added = await DbFixture.Periods(db).AddAsync(new PeriodInput("Early " + u[..6], new(7, 30), new(8, 15), 1, true, false, [DayOfWeek.Sunday]), PeriodScope.ThisClass, other, null);
            Assert.True(added.Result.Succeeded, string.Join(" ", added.Result.Errors.Select(e => e.Value)));
            Assert.True((await DbFixture.Timetable(db).AssignAsync(new SessionInput(other, DayOfWeek.Sunday, added.Result.EntityId!.Value, Math, "Math", teacher, null))).Succeeded);
        }
        var before = await CountsAsync();
        var client = Client();
        var file = TimetableExtractorTests.Workbook(("T", [
            ["", "Time", "Sunday", "Monday"],
            ["1", "08:00-08:45", $"Math - Laila{u} Hamed{u}", $"Math - Laila{u} Hamed{u}"],
        ], []));
        var html = WebUtility.HtmlDecode(await (await UploadAsync(client, cls, file, "clash.xlsx")).Content.ReadAsStringAsync());
        Assert.Contains($"Sunday, 1: Laila{u} Hamed{u} already teaches", html);                   // the clash, on its cell
        Assert.DoesNotContain("Monday, 1: Laila", html);                                          // Monday is free for her
        Assert.Matches("type=\"submit\" name=\"Op\" value=\"save\" disabled", html);
        Assert.Equal(before, await CountsAsync());

        // Saving anyway is refused and nothing is written.
        var refused = await PostAsync(client, Fields(html), "save");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Equal(before, await CountsAsync());
    }

    private async Task<string> NameAsync(int classId)
    {
        await using var db = fx.NewContext();
        return await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync();
    }
}
