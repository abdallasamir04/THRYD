using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Import;

namespace SchoolManagement.Tests;

/// <summary>
/// Import / Export → Tasks, through the running app: the third card, the template, upload (.xlsx and .csv), column matching,
/// a preview that saves nothing and reports every invalid row with its row number, confirm creating ordinary tasks with the
/// Tasks module's own rules (never for students), duplicates skipped, and the Excel / CSV export. Students and Teachers keep
/// their own pages, templates and import.
/// </summary>
[Collection("db")]
public sealed partial class TaskImportExportTests(DbFixture fx)
{
    private sealed class User : ICurrentUser
    {
        public string? Name => "Import tester";
        public int? StaffId => null;
        public bool IsVerified => false;
    }

    private HttpClient Client() => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    private static string Token(string html) => TokenPattern().Match(html).Groups[1].Value;
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    internal sealed record Row(int Number, string Status, string Text);

    /// <summary>Upload → match columns (as detected) → returns the import's token.</summary>
    internal static async Task<Guid> UploadAndMatchAsync(HttpClient client, string kind, string fileName, byte[] bytes)
    {
        var page = await client.GetStringAsync($"/import-export?kind={kind}");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Token(page)), "__RequestVerificationToken" },
            { new StringContent(kind), "kind" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".csv") ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", fileName);
        var upload = await client.PostAsync("/import-export/upload", form);
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        var location = upload.Headers.Location!.OriginalString;
        Assert.Matches("^/import-export/[0-9a-f-]{36}/columns$", location);
        var token = Guid.Parse(location.Split('/')[2]);

        var map = await client.GetStringAsync(location);
        string Selected(string selectName) =>
            Regex.Match(map, $"name=\"{Regex.Escape(selectName)}\"[^>]*>(?:(?!</select>).)*?<option value=\"(-?\\d+)\" selected", RegexOptions.Singleline).Groups[1].Value;
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", Token(map)), new("intent", ""),
            new("sheetIndex", Selected("sheetIndex")), new("headerIndex", Selected("headerIndex")),
        };
        foreach (var f in ImportDefinitions.Fields(Enum.Parse<ImportKind>(kind)))
            fields.Add(new($"map[{f.Key}]", Selected($"map[{f.Key}]") is { Length: > 0 } col ? col : ""));
        var mapped = await client.PostAsync(location, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, mapped.StatusCode);
        Assert.EndsWith("/preview", mapped.Headers.Location!.OriginalString);
        return token;
    }

    internal static async Task<(string Html, List<Row> Rows)> PreviewAsync(HttpClient client, Guid token)
    {
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/import-export/{token}/preview"));
        var rows = RowPattern().Matches(html).Select(m => new Row(int.Parse(m.Groups[1].Value),
            Regex.Match(m.Groups[2].Value, "badge--dot\">(Ready|Duplicate|Invalid)<").Groups[1].Value, m.Groups[2].Value)).ToList();
        return (html, rows);
    }

    internal static async Task ConfirmAsync(HttpClient client, Guid token, string previewHtml)
    {
        var r = await client.PostAsync($"/import-export/{token}/confirm",
            new FormUrlEncodedContent([new("__RequestVerificationToken", Token(previewHtml))]));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.EndsWith("/result", r.Headers.Location!.OriginalString);
    }

    private async Task<int> StaffAsync(string name, string department, string phone, DateOnly? contractEnd = null)
    {
        await using var db = fx.NewContext();
        var id = (await DbFixture.Teachers(db).CreateAsync(name, department + " Teacher", phone)).EntityId!.Value;
        var dept = await db.Departments.Where(d => d.Name == department).Select(d => d.Id).SingleAsync();
        await db.StaffMembers.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, dept).SetProperty(x => x.ContractEndDate, contractEnd));
        return id;
    }

    [Fact]
    public async Task Tasks_import_validates_every_row_saves_only_on_confirm_and_creates_ordinary_tasks()
    {
        var stamp = DbFixture.Unique();
        var arabicName = "مها سمير " + stamp;
        var arabic = await StaffAsync(arabicName, "Arabic", "0100" + Random.Shared.Next(1000000, 9999999));
        var englishName = "Import English " + stamp;
        var english = await StaffAsync(englishName, "English", "0101" + Random.Shared.Next(1000000, 9999999));
        var twinName = "Import Twin " + stamp;
        await StaffAsync(twinName, "Math", "0102" + Random.Shared.Next(1000000, 9999999));
        var twinScience = await StaffAsync(twinName, "Science", "0103" + Random.Shared.Next(1000000, 9999999));
        var formerName = "Import Former " + stamp;
        await StaffAsync(formerName, "Art", "0104" + Random.Shared.Next(1000000, 9999999), SchoolWeek.Today.AddDays(-5));
        var (_, studentName) = await fx.NewStudentAsync(await fx.NewClassAsync());

        var client = Client();

        // ---------- The page: a third card with the task count, its own import and export sections.
        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/import-export?kind=Tasks"));
        Assert.Equal(4, Regex.Matches(page, "<a class=\"kind-tab\"").Count);           // Students, Teachers, Tasks, Classes
        Assert.Contains("<a class=\"kind-tab\" href=\"/import-export?kind=Tasks\" aria-current=\"true\">", page);
        int total;
        await using (var db = fx.NewContext()) total = await db.ManagementTasks.CountAsync();
        Assert.Contains($">Tasks</span><br /><span class=\"kind-tab__meta\">{total:N0} task", page);
        Assert.Contains("Import tasks", page);
        Assert.Contains("href=\"/import-export/template/Tasks\"", page);
        Assert.Contains("href=\"/import-export/tasks/export?format=xlsx\"", page);
        Assert.Contains("href=\"/import-export/tasks/export?format=csv\"", page);
        Assert.DoesNotContain("All students (", page); // the Students / Teachers export card belongs to those cards

        // ---------- The template: the task columns, pick lists, and the example kept off the Tasks sheet.
        var template = await client.GetAsync("/import-export/template/Tasks");
        Assert.Equal("tasks-import-template.xlsx", template.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using (var book = new XLWorkbook(new MemoryStream(await template.Content.ReadAsByteArrayAsync())))
        {
            Assert.Equal(["Tasks", "Staff", "Departments", "Lists", "Instructions"], book.Worksheets.Select(w => w.Name));
            var sheet = book.Worksheet("Tasks");
            Assert.Equal(["Task Title", "Description", "Assigned To", "Department", "Priority", "Status", "Due Date", "Due Time", "Reviewer", "Notes"],
                sheet.Row(1).CellsUsed().Select(c => c.GetString()));
            Assert.Equal(1, sheet.LastRowUsed()!.RowNumber());                   // no data rows to import by accident
            Assert.True(sheet.DataValidations.Count() >= 4);
            var staffNames = book.Worksheet("Staff").Column(1).CellsUsed().Skip(1).Select(c => c.GetString()).ToList();
            Assert.Contains(arabicName, staffNames);
            Assert.DoesNotContain(studentName, staffNames);                       // students are never offered
            Assert.DoesNotContain(formerName, staffNames);                        // nor staff whose contract ended
            Assert.Contains("Example row", book.Worksheet("Instructions").CellsUsed().Select(c => c.GetString()).First(s => s.StartsWith("Example")));
        }

        // ---------- An .xlsx with other column names (matched automatically), real Excel dates and times, and every kind of error.
        var due = SchoolWeek.Today.AddDays(5);
        var later = SchoolWeek.Today.AddDays(10);
        byte[] file;
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("My tasks");
            string[] head = ["المهمة", "Details", "Assignee", "Dept", "Importance", "State", "Deadline", "Deadline Time", "Approver", "Comments"];
            for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
            object?[][] rows =
            [
                /*  2 */ ["تحضير اختبار اللغة العربية " + stamp, "Two versions.\nWith the answer key.", arabicName, "Arabic", "High", "To Do", due.ToDateTime(TimeOnly.MinValue), new TimeSpan(14, 30, 0), englishName, "ملاحظة"],
                /*  3 */ ["Check reading logs " + stamp, null, englishName, null, null, "In Progress", later.ToString("dd/MM/yyyy"), "9:15 AM", null, null],
                /*  4 */ [null, "No title", englishName, null, null, null, null, null, null, null],
                /*  5 */ ["For a student " + stamp, null, studentName, null, null, null, null, null, null, null],
                /*  6 */ ["Nobody " + stamp, null, "No Such Person " + stamp, null, null, null, null, null, null, null],
                /*  7 */ ["Bad priority " + stamp, null, englishName, null, "Very high", null, null, null, null, null],
                /*  8 */ ["On hold " + stamp, null, englishName, null, null, "On Hold", null, null, null, null],
                /*  9 */ ["Bad date " + stamp, null, englishName, null, null, null, "soon", null, null, null],
                /* 10 */ ["Past date " + stamp, null, englishName, null, null, "To Do", SchoolWeek.Today.AddDays(-3).ToDateTime(TimeOnly.MinValue), null, null, null],
                /* 11 */ ["Wrong department " + stamp, null, arabicName, "English", null, null, null, null, null, null],
                /* 12 */ ["Ambiguous " + stamp, null, twinName, null, null, null, null, null, null, null],
                /* 13 */ ["Twin in Science " + stamp, null, twinName, "Science", "critical", "completed", null, null, null, null],
                /* 14 */ ["تحضير اختبار اللغة العربية " + stamp, "again", arabicName, "Arabic", "High", "To Do", due.ToDateTime(TimeOnly.MinValue), null, null, null],
                /* 15 */ ["Former staff " + stamp, null, formerName, null, null, null, null, null, null, null],
                /* 16 */ ["Self review " + stamp, null, englishName, null, null, null, null, null, englishName, null],
                /* 17 */ ["Bad department " + stamp, null, englishName, "Astronomy", null, null, null, null, null, null],
            ];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    switch (rows[r][c])
                    {
                        case null: break;
                        case DateTime d: ws.Cell(r + 2, c + 1).Value = d; ws.Cell(r + 2, c + 1).Style.DateFormat.Format = "m/d/yyyy"; break; // a US display format
                        case TimeSpan t: ws.Cell(r + 2, c + 1).Value = t; ws.Cell(r + 2, c + 1).Style.DateFormat.Format = "h:mm AM/PM"; break;
                        case var v: ws.Cell(r + 2, c + 1).Value = v.ToString(); break;
                    }
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            file = ms.ToArray();
        }

        int before;
        await using (var db = fx.NewContext()) before = await db.ManagementTasks.CountAsync();
        var token = await UploadAndMatchAsync(client, "Tasks", "tasks.xlsx", file);
        var (html, preview) = await PreviewAsync(client, token);
        Assert.Contains("Nothing has been saved yet.", html);
        string StatusOf(int n) => preview.Single(r => r.Number == n).Status;
        string TextOf(int n) => preview.Single(r => r.Number == n).Text;
        Assert.Equal(16, preview.Count);
        Assert.Equal(["Ready", "Ready"], new[] { StatusOf(2), StatusOf(3) });
        Assert.Equal("Ready", StatusOf(13));
        Assert.Contains("Arabic · Arabic Teacher", TextOf(2));                            // the staff record the name matched
        foreach (var (row, message) in new (int, string)[]
                 {
                     (4, "The task title is missing."),
                     (5, $"\"{studentName}\" is a student. Tasks can only be given to staff members."),
                     (6, "no staff member is named"),
                     (7, "Priority \"Very high\" is not one of Low, Medium, High or Critical."),
                     (8, "On Hold is not available for new tasks."),
                     (9, "Due date \"soon\" is not a date."),
                     (10, "The due date cannot be in the past."),                              // the Tasks module's own rule
                     (11, $"{arabicName} is not a member of the English department."),
                     (12, $"2 staff members are named \"{twinName}\". Add the Department column"),
                     (15, "contract ended"),
                     (16, "Choose a reviewer other than the person carrying out the task"),
                     (17, "Department \"Astronomy\" does not exist."),
                 })
        {
            Assert.Equal("Invalid", StatusOf(row));
            Assert.Contains(message, TextOf(row));
        }
        Assert.Equal("Duplicate", StatusOf(14));
        Assert.Contains("Same task as row 2 of this file.", TextOf(14));
        await using (var db = fx.NewContext()) Assert.Equal(before, await db.ManagementTasks.CountAsync()); // nothing saved yet

        // The report of rows that will not be imported, with row numbers and reasons.
        using (var report = new XLWorkbook(new MemoryStream(await client.GetByteArrayAsync($"/import-export/{token}/report"))))
        {
            var rows = report.Worksheet("Rows not imported").RowsUsed().Skip(1).Select(r => r.Cell(1).GetValue<int>()).ToList();
            Assert.Equal([4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 15, 16, 17], rows);
        }

        // ---------- Confirm: three ordinary tasks, created by the Tasks module (history, counts, Tasks page).
        await ConfirmAsync(client, token, html);
        await ConfirmAsync(client, token, html); // a second confirm returns the first result, importing nothing more
        var result = WebUtility.HtmlDecode(await client.GetStringAsync($"/import-export/{token}/result"));
        Assert.Matches("summary-tile__value num\">3</span><span class=\"summary-tile__label\">.*?Imported", result);
        Assert.Contains("href=\"/tasks\"", result);
        await using (var db = fx.NewContext())
        {
            Assert.Equal(before + 3, await db.ManagementTasks.CountAsync());
            var a = await db.ManagementTasks.AsNoTracking().SingleAsync(t => t.Title == "تحضير اختبار اللغة العربية " + stamp);
            Assert.Equal((arabic, english, TaskPriority.High, ManagementTaskStatus.ToDo, due, new TimeOnly(14, 30)),
                (a.AssignedToId, a.ReviewerId!.Value, a.Priority, a.Status, a.DueDate!.Value, a.DueTime!.Value));
            Assert.Equal("Two versions.\nWith the answer key.", a.Description);         // line breaks kept
            Assert.Equal("ملاحظة", a.Notes);
            var b = await db.ManagementTasks.AsNoTracking().SingleAsync(t => t.Title == "Check reading logs " + stamp);
            Assert.Equal((english, TaskPriority.Medium, ManagementTaskStatus.InProgress, later, new TimeOnly(9, 15)),
                (b.AssignedToId, b.Priority, b.Status, b.DueDate!.Value, b.DueTime!.Value));
            var c = await db.ManagementTasks.AsNoTracking().SingleAsync(t => t.Title == "Twin in Science " + stamp);
            Assert.Equal((twinScience, TaskPriority.Critical, ManagementTaskStatus.Completed), (c.AssignedToId, c.Priority, c.Status));
            Assert.NotNull(c.CompletedAt);
            // Each has its "created" history entry, and counts in the staff member's tasks.
            Assert.Equal(3, await db.ManagementTaskHistory.CountAsync(h => (h.TaskId == a.Id || h.TaskId == b.Id || h.TaskId == c.Id) && h.Action == TaskHistoryAction.Created));
            Assert.Equal(1, (await new ManagementTaskService(db, new User()).CountsAsync(arabic)).Open);
            Assert.Equal(1, (await new ManagementTaskService(db, new User()).CountsAsync(twinScience)).Completed);
        }
        var tasksPage = WebUtility.HtmlDecode(await client.GetStringAsync("/tasks?search=" + Uri.EscapeDataString(stamp)));
        foreach (var title in new[] { "تحضير اختبار اللغة العربية " + stamp, "Check reading logs " + stamp, "Twin in Science " + stamp })
            Assert.Single(Regex.Matches(tasksPage, Regex.Escape(title) + "</a>"));
        // …and in the assignee's own task list (the Tasks page filtered to that staff member).
        var mine = WebUtility.HtmlDecode(await client.GetStringAsync($"/tasks?memberId={english}&search={Uri.EscapeDataString(stamp)}"));
        Assert.Contains($"Check reading logs {stamp}", mine);
        Assert.DoesNotContain($"Twin in Science {stamp}", mine);

        // ---------- The same file again: the three tasks now exist, so they are duplicates — nothing is added twice.
        var again = await UploadAndMatchAsync(client, "Tasks", "tasks.xlsx", file);
        var (_, second) = await PreviewAsync(client, again);
        Assert.All(new[] { 2, 3, 13 }, n => Assert.Equal("Duplicate", second.Single(r => r.Number == n).Status));
        Assert.Contains("already has a task with this title", second.Single(r => r.Number == 2).Text);
        Assert.DoesNotContain(second, r => r.Status == "Ready");

        // ---------- CSV: text dates (day first) and headers in English.
        var csvTitle = "CSV task " + stamp;
        var csv = "Task Title,Assigned To,Priority,Due Date,Due Time\r\n" + $"\"{csvTitle}, with a comma\",{englishName},low,{later:dd/MM/yyyy},16:00\r\n";
        var csvToken = await UploadAndMatchAsync(client, "Tasks", "tasks.csv", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)]);
        var (csvHtml, csvRows) = await PreviewAsync(client, csvToken);
        Assert.Equal("Ready", Assert.Single(csvRows).Status);
        await ConfirmAsync(client, csvToken, csvHtml);
        await using (var db = fx.NewContext())
        {
            var t = await db.ManagementTasks.AsNoTracking().SingleAsync(x => x.Title == csvTitle + ", with a comma");
            Assert.Equal((english, TaskPriority.Low, later, new TimeOnly(16, 0)), (t.AssignedToId, t.Priority, t.DueDate!.Value, t.DueTime!.Value));
        }
    }

    [Fact]
    public async Task Tasks_export_has_every_task_in_excel_and_csv_without_ids()
    {
        var stamp = DbFixture.Unique();
        var name = "مريم خالد " + stamp;
        var staff = await StaffAsync(name, "Arabic", "0105" + Random.Shared.Next(1000000, 9999999));
        var title = "مراجعة الخطة " + stamp;
        var formula = "=HYPERLINK(\"x\") " + stamp;
        await using (var db = fx.NewContext())
        {
            var tasks = new ManagementTaskService(db, new User());
            Assert.True((await tasks.CreateAsync(new TaskInput { Title = title, Description = "سطر أول\nSecond line", AssignedToId = staff, Priority = TaskPriority.High,
                DueDate = SchoolWeek.Today.AddDays(3), DueTime = new TimeOnly(8, 0) })).Succeeded);
            Assert.True((await tasks.CreateAsync(new TaskInput { Title = formula, AssignedToId = staff, Status = ManagementTaskStatus.Completed })).Succeeded);
        }
        var client = Client();
        int total;
        await using (var db = fx.NewContext()) total = await db.ManagementTasks.CountAsync();

        var xlsx = await client.GetAsync("/import-export/tasks/export?format=xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^THRYD_Tasks_\d{4}-\d{2}-\d{2}\.xlsx$", xlsx.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using (var book = new XLWorkbook(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync())))
        {
            var ws = book.Worksheet("Tasks");
            Assert.Equal(ExcelExportService.TaskExportHeaders, ws.Row(1).CellsUsed().Select(c => c.GetString()));
            var rows = ws.RowsUsed().Skip(1).ToList();
            Assert.Equal(total, rows.Count);                                                   // every task, all statuses
            Assert.Equal(Enumerable.Range(1, total).Select(i => i.ToString()), rows.Select(r => r.Cell(1).GetString())); // serials, not ids
            var row = rows.Single(r => r.Cell(2).GetString() == title);                      // Arabic exactly as stored
            Assert.Equal("سطر أول\nSecond line", row.Cell(3).GetString());
            Assert.Equal((name, "Arabic", "High", "To Do"), (row.Cell(4).GetString(), row.Cell(5).GetString(), row.Cell(6).GetString(), row.Cell(7).GetString()));
            Assert.Equal(SchoolWeek.Today.AddDays(3).ToDateTime(TimeOnly.MinValue), row.Cell(8).GetDateTime());
            Assert.Equal(new TimeSpan(8, 0, 0), row.Cell(9).GetTimeSpan());
            Assert.Equal(XLDataType.DateTime, row.Cell(12).DataType);                        // created date
            Assert.True(row.Cell(14).IsEmpty());                                             // not completed
            var done = rows.Single(r => r.Cell(2).GetString() == formula);
            Assert.False(done.Cell(2).HasFormula);                                           // text, never run as a formula
            Assert.Equal("Completed", done.Cell(7).GetString());
            Assert.False(done.Cell(14).IsEmpty());
        }

        var csv = await client.GetAsync("/import-export/tasks/export?format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);                             // UTF-8 BOM: Excel keeps Arabic
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith(string.Join(",", ExcelExportService.TaskExportHeaders) + "\r\n", text);
        Assert.Contains($"{title},\"سطر أول\nSecond line\",{name},Arabic,High,To Do,{Iso(SchoolWeek.Today.AddDays(3))},08:00,", text);
        Assert.Contains($"\"'=HYPERLINK(\"\"x\"\") {stamp}\"", text);                       // formula neutralised
        // The CSV reads back with the app's own reader: one row per task.
        var grid = SpreadsheetReader.Read(new MemoryStream(bytes), "tasks.csv").Single();
        Assert.Equal(total + 1, grid.Rows.Count);
    }

    [Fact]
    public async Task Students_and_Teachers_keep_their_templates_and_import()
    {
        var client = Client();
        foreach (var (kind, current) in new[] { ("Students", "Students"), ("Teachers", "Teachers") })
        {
            var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/import-export?kind={kind}"));
            Assert.Contains($"<a class=\"kind-tab\" href=\"/import-export?kind={current}\" aria-current=\"true\">", page);
            Assert.Contains("All students (", page);                                       // their export card is unchanged
            Assert.DoesNotContain("tasks/export", page);
        }
        using (var s = new XLWorkbook(new MemoryStream(await client.GetByteArrayAsync("/import-export/template/Students"))))
            Assert.Equal(["Student Name", "Class"], s.Worksheet("Students").Row(1).CellsUsed().Select(c => c.GetString()));
        using (var t = new XLWorkbook(new MemoryStream(await client.GetByteArrayAsync("/import-export/template/Teachers"))))
            Assert.Equal(["Teacher Name", "Job Title", "Contact Number"], t.Worksheet("Teachers").Row(1).CellsUsed().Select(c => c.GetString()));

        // A teacher CSV goes through the same upload → columns → preview → confirm as before.
        var name = "Imported Teacher " + DbFixture.Unique();
        var phone = "0109" + Random.Shared.Next(1000000, 9999999);
        var token = await UploadAndMatchAsync(client, "Teachers", "teachers.csv", Encoding.UTF8.GetBytes($"Teacher Name,Job Title,Contact Number\r\n{name},Music Teacher,{phone}\r\n"));
        var (html, rows) = await PreviewAsync(client, token);
        Assert.Equal("Ready", Assert.Single(rows).Status);
        await ConfirmAsync(client, token, html);
        await using var db = fx.NewContext();
        var teacher = await db.Teachers.AsNoTracking().SingleAsync(x => x.FullName == name);
        Assert.Equal(("Music Teacher", phone), (teacher.JobTitle, teacher.ContactNumber));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("<td class=\"col-num\"[^>]*>(\\d+)</td>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex RowPattern();
}
