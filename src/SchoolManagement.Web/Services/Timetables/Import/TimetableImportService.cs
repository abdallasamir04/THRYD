using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.Excel;

namespace SchoolManagement.Web.Services.Timetables.Import;

public interface ITimetableImportService
{
    Task<TimetableUploadPage> UploadPageAsync(int? classId, int? yearId, CancellationToken ct = default);

    /// <summary>Checks and reads the uploaded file (in memory only) into a preview form. Nothing is saved.</summary>
    Task<(TimetableImportForm? Form, string? Error)> ReadAsync(int classId, int? yearId, string fileName, long length, Stream content, CancellationToken ct = default);

    /// <summary>Applies the preview's button (add / remove a period or day…), checks everything and tries the save in a rolled-back transaction.</summary>
    Task<TimetableImportPreview> PreviewAsync(TimetableImportForm form, CancellationToken ct = default);

    /// <summary>Saves the reviewed timetable (all or nothing). Returns the import's id, or the preview with what blocks the save.</summary>
    Task<(int? ImportId, TimetableImportPreview Preview)> SaveAsync(TimetableImportForm form, CancellationToken ct = default);

    Task<TimetableImportSummary?> SummaryAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Timetables → Upload Timetable. Extraction (<see cref="TimetableExtractor"/>) only reads the file; this service
/// matches subjects and teachers to the school's records, validates the reviewed grid and saves it through the existing
/// timetable services — <see cref="IPeriodService"/> for the class's periods and <see cref="ITimetableService"/> for every
/// lesson, so the same rules apply as when a timetable is built by hand (teacher eligibility, clashes with the teacher's
/// other classes and duties, the weekly workload maximum, period overlaps). All of it runs in one transaction: the
/// preview's check runs it and rolls back; Save commits it only when nothing failed. The class's earlier lessons are
/// ended from this week (history and attendance kept), never deleted, and only after the administrator confirms.
/// </summary>
public sealed partial class TimetableImportService(SchoolDbContext db, ITimetableService timetable, IPeriodService periods,
    IClassTeamService years, ICurrentUser currentUser, ILogger<TimetableImportService> logger) : ITimetableImportService
{
    public static readonly string[] AllowedExtensions = [".xlsx", ".csv"];
    public const string Accept = ".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv";
    public const string ReplaceReason = "Replaced by an uploaded timetable";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ================================================================== step 1: upload

    public async Task<TimetableUploadPage> UploadPageAsync(int? classId, int? yearId, CancellationToken ct = default)
    {
        var classes = await db.Classes.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new UploadClassOption(c.Id, c.Name, db.TimetableAssignments.Count(t => t.SchoolClassId == c.Id && t.EndedOn == null)))
            .ToListAsync(ct);
        var all = await years.YearsAsync(ct);
        return new TimetableUploadPage
        {
            Classes = classes, Years = all,
            ClassId = classes.Any(c => c.Id == classId) ? classId : null,
            YearId = all.Any(y => y.Id == yearId) ? yearId : all.FirstOrDefault(y => y.IsCurrent)?.Id ?? all.FirstOrDefault()?.Id,
        };
    }

    public async Task<(TimetableImportForm? Form, string? Error)> ReadAsync(int classId, int? yearId, string fileName, long length, Stream content, CancellationToken ct = default)
    {
        var cls = await db.Classes.AsNoTracking().Where(c => c.Id == classId).Select(c => new { c.Name, c.IsActive }).FirstOrDefaultAsync(ct);
        if (cls is null) return (null, "Choose the class this timetable belongs to.");
        if (!cls.IsActive) return (null, $"{cls.Name} is inactive. Activate it before uploading its timetable.");
        if (yearId is not null && !await db.AcademicYears.AnyAsync(y => y.Id == yearId, ct)) return (null, "Choose the academic year.");

        // Only the name is kept (shown on the pages): never a path, and the file is never written to disk.
        var name = TextNormalizer.CleanText(Path.GetFileName(fileName.Replace('\\', '/')));
        if (name.Length > TimetableImport.FileNameMaxLength) name = name[^TimetableImport.FileNameMaxLength..];
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (length <= 0) return (null, "The file is empty.");
        if (length > SpreadsheetReader.MaxFileBytes) return (null, "The file is larger than 10 MB.");

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await content.CopyToAsync(buffer, ct);
            if (buffer.Length == 0) return (null, "The file is empty.");
            if (buffer.Length > SpreadsheetReader.MaxFileBytes) return (null, "The file is larger than 10 MB.");
            bytes = buffer.ToArray();
        }

        // The content decides, not only the name: a renamed PDF or image is recognised and explained.
        if (FileCheck.Problem(bytes, ext) is { } problem) return (null, problem);

        IReadOnlyList<SheetGrid> sheets;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            sheets = SpreadsheetReader.Read(stream, name, fillMergedCells: true);
        }
        catch (SpreadsheetReadException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Timetable upload {File} could not be read", name);
            return (null, "This file could not be read. Save it again as an Excel workbook (.xlsx) and try again.");
        }

        var classKey = TextNormalizer.ClassKey(cls.Name);
        var table = TimetableExtractor.Extract(sheets, classKey, out var error);
        if (table is null) return (null, error);

        var form = new TimetableImportForm
        {
            ClassId = classId, YearId = yearId, FileName = name, Sheet = table.Sheet, Days = table.Days.ToList(), Notes = table.Notes.ToList(),
        };
        var others = table.ClassesMentioned.Where(k => k != classKey).ToList();
        if (others.Count > 0 && !table.ClassesMentioned.Contains(classKey))
            form.Notes.Insert(0, $"The file mentions {string.Join(", ", others)}, but this timetable is being imported for {cls.Name}. Check that you chose the right class.");

        for (var p = 0; p < table.Periods.Count; p++)
        {
            var period = table.Periods[p];
            var row = new ImportPeriodRow { Label = period.Label, Start = Hm(period.Start), End = Hm(period.End) };
            foreach (var day in form.Days)
            {
                var cell = table.Cell(p, day);
                row.Cells.Add(new ImportCellRow
                {
                    Day = day, Kind = cell?.Kind ?? ImportCellKind.NoPeriod, Raw = cell?.Raw is { Length: > 0 } r ? r : null,
                    Label = cell?.Label,
                });
            }
            form.Periods.Add(row);
        }

        var vocabulary = await VocabularyAsync(ct);
        foreach (var cell in form.Periods.SelectMany(r => r.Cells).Where(c => c.Kind == ImportCellKind.Lesson))
            await ResolveAsync(cell, vocabulary, ct);
        return (form, null);
    }

    // ================================================================== steps 2–3: preview, edit, check

    public async Task<TimetableImportPreview> PreviewAsync(TimetableImportForm form, CancellationToken ct = default)
    {
        var vocabulary = await VocabularyAsync(ct);
        ApplyOp(form);
        await NormalizeAsync(form, vocabulary, ct);
        var check = Validate(form, vocabulary);
        await CountExistingAsync(form, check, ct);
        if (!check.HasErrors) await RunAsync(form, check, commit: false, ct);
        return await PreviewModelAsync(form, check, vocabulary, ct);
    }

    public async Task<(int? ImportId, TimetableImportPreview Preview)> SaveAsync(TimetableImportForm form, CancellationToken ct = default)
    {
        form.Op = "save";
        var vocabulary = await VocabularyAsync(ct);
        await NormalizeAsync(form, vocabulary, ct);
        var check = Validate(form, vocabulary);
        await CountExistingAsync(form, check, ct);
        if (check.ExistingLessons > 0 && !form.ReplaceExisting)
            check.Errors.Add($"This class already has a timetable ({Ui.Plural(check.ExistingLessons, "lesson", "lessons")}). Tick “Replace the current timetable” to replace it, or cancel.");
        int? id = null;
        if (!check.HasErrors) id = await RunAsync(form, check, commit: true, ct);
        return (id, await PreviewModelAsync(form, check, vocabulary, ct));
    }

    public async Task<TimetableImportSummary?> SummaryAsync(int id, CancellationToken ct = default)
    {
        var x = await db.TimetableImports.AsNoTracking().Where(i => i.Id == id)
            .Select(i => new { i, ClassName = i.SchoolClass.Name, YearName = i.AcademicYear != null ? i.AcademicYear.Name : null }).FirstOrDefaultAsync(ct);
        if (x is null) return null;
        TimetableImportDetails details;
        try
        {
            details = (x.i.Details is null ? null : JsonSerializer.Deserialize<TimetableImportDetails>(x.i.Details, Json)) ?? new([], []);
        }
        catch (JsonException)
        {
            details = new([], []);
        }
        return new TimetableImportSummary
        {
            Id = x.i.Id, ClassId = x.i.SchoolClassId, ClassName = x.ClassName, YearName = x.YearName, FileName = x.i.FileName, Sheet = x.i.SheetName,
            Days = x.i.Days, Periods = x.i.Periods, SubjectAssignments = x.i.SubjectAssignments, TeacherAssignments = x.i.TeacherAssignments,
            Unassigned = x.i.UnassignedTeachers, Breaks = x.i.Breaks, Warnings = x.i.Warnings, ReplacedLessons = x.i.ReplacedLessons,
            ImportedAt = x.i.ImportedAt, ImportedBy = x.i.ImportedBy, Details = details,
        };
    }

    // ================================================================== the form between steps

    private static void ApplyOp(TimetableImportForm form)
    {
        var op = form.Op ?? "";
        var colon = op.IndexOf(':');
        var verb = colon > 0 ? op[..colon] : op;
        var arg = colon > 0 && int.TryParse(op[(colon + 1)..], out var n) ? n : -1;
        switch (verb)
        {
            case "add-period":
                if (form.Periods.Count >= TimetableExtractor.MaxPeriods) break;
                var last = form.Periods.LastOrDefault();
                form.Periods.Add(new ImportPeriodRow
                {
                    Label = $"Period {form.Periods.Count + 1}", Start = last?.End,
                    Cells = form.Days.Select(d => new ImportCellRow { Day = d, Kind = ImportCellKind.Free }).ToList(),
                });
                break;
            case "remove-period" when arg >= 0 && arg < form.Periods.Count:
                form.Periods.RemoveAt(arg);
                break;
            case "break-period" when arg >= 0 && arg < form.Periods.Count:
                var row = form.Periods[arg];
                var label = TimetableExtractor.SpecialKind(row.Label) is ImportCellKind.Break or ImportCellKind.Activity ? row.Label : "Break";
                foreach (var c in row.Cells.Where(c => c.Kind != ImportCellKind.NoPeriod)) (c.Kind, c.Label) = (ImportCellKind.Break, label);
                break;
            case "add-day" when ScheduleRules.IsSchoolDay((DayOfWeek)arg) && !form.Days.Contains((DayOfWeek)arg):
                form.Days.Add((DayOfWeek)arg);
                foreach (var r in form.Periods) r.Cells.Add(new ImportCellRow { Day = (DayOfWeek)arg, Kind = ImportCellKind.Free });
                break;
            case "remove-day" when form.Days.Contains((DayOfWeek)arg) && form.Days.Count > 1:
                form.Days.Remove((DayOfWeek)arg);
                break;
        }
    }

    /// <summary>One cell per day in day order, school days only, rows in time order, and teacher matching for subjects chosen since the last check.</summary>
    private async Task NormalizeAsync(TimetableImportForm form, Vocabulary vocabulary, CancellationToken ct)
    {
        form.Days = form.Days.Where(ScheduleRules.IsSchoolDay).Distinct().OrderBy(d => d).ToList();
        form.Notes = form.Notes.Where(n => !string.IsNullOrWhiteSpace(n)).Take(20).ToList();
        form.FileName = TextNormalizer.CleanText(Path.GetFileName((form.FileName ?? "").Replace('\\', '/')));
        if (form.FileName.Length > TimetableImport.FileNameMaxLength) form.FileName = form.FileName[^TimetableImport.FileNameMaxLength..];
        form.Periods = form.Periods.Take(TimetableExtractor.MaxPeriods).ToList();
        foreach (var row in form.Periods)
        {
            row.Label = TextNormalizer.CleanText(row.Label);
            row.Start = Hm(ParseHm(row.Start)) ?? row.Start;
            row.End = Hm(ParseHm(row.End)) ?? row.End;
            row.Cells = form.Days.Select(d => row.Cells.FirstOrDefault(c => c.Day == d) ?? new ImportCellRow { Day = d, Kind = ImportCellKind.NoPeriod }).ToList();
            foreach (var cell in row.Cells)
            {
                if (!Enum.IsDefined(cell.Kind)) cell.Kind = ImportCellKind.Free;
                cell.Label = Cut(TextNormalizer.CleanText(cell.Label), TimeSlot.NameMaxLength);
                cell.Notes = Cut(TextNormalizer.CleanText(cell.Notes), TimetableAssignment.NotesMaxLength);
                if (cell.Subject is not null && (cell.ParsedSubject is not { } ps || !vocabulary.Has(ps.DepartmentId, ps.Subject))) cell.Subject = null;
                // A subject chosen (or changed) since the last check: match the file's teacher name again for it.
                // A teacher the administrator chose (not the file's suggestion) is kept; the save checks it teaches the subject.
                if (cell.Kind == ImportCellKind.Lesson && cell.Subject is not null && cell.Subject != cell.MatchedFor)
                    await MatchTeacherAsync(cell, vocabulary, keepChoice: cell.TeacherId is not null && cell.TeacherId != cell.SuggestedId, ct);
            }
        }
        // Periods in time order (the order the timetable shows them in); rows without valid times stay where they are.
        if (form.Periods.All(r => ParseHm(r.Start) is not null))
            form.Periods = form.Periods.OrderBy(r => ParseHm(r.Start)).ToList();
    }

    private static ImportCheck Validate(TimetableImportForm form, Vocabulary vocabulary)
    {
        var check = new ImportCheck();
        foreach (var note in form.Notes) check.Warnings.Add(note);
        if (form.Days.Count == 0) check.Errors.Add("The timetable has no school days. Add a day.");
        if (form.Periods.Count == 0) check.Errors.Add("The timetable has no periods. Add a period.");

        var running = new List<(int Row, TimeOnly Start, TimeOnly End, string Label, HashSet<DayOfWeek> Days)>();
        for (var i = 0; i < form.Periods.Count; i++)
        {
            var row = form.Periods[i];
            var label = Label(row, i);
            var days = row.Cells.Where(c => c.Kind != ImportCellKind.NoPeriod).Select(c => c.Day).ToHashSet();
            if (days.Count == 0)
            {
                check.RowWarning(i, $"{label} runs on no day, so it will not be saved. Remove it, or set a day's cell to a lesson, free period or break.");
                continue;
            }
            if ((row.Label ?? "").Length > TimeSlot.NameMaxLength) check.RowError(i, $"Use {TimeSlot.NameMaxLength} characters or fewer for the period name.");
            var start = ParseHm(row.Start);
            var end = ParseHm(row.End);
            if (start is null || end is null) check.RowError(i, $"Enter the start and end times of {label} (the file does not give them).");
            else if (end <= start) check.RowError(i, $"{label} must end after it starts.");
            else running.Add((i, start.Value, end.Value, label, days));

            foreach (var cell in row.Cells)
            {
                var where = $"{ScheduleRules.DayName(cell.Day)}, {label}";
                switch (cell.Kind)
                {
                    case ImportCellKind.Lesson when cell.Subject is null:
                        check.CellError(i, cell.Day, cell.Raw is null
                            ? $"{where}: choose the subject, or mark the cell as free."
                            : $"{where}: “{Cut(cell.Raw.Replace('\n', ' '), 60)}” is not a subject of the school. Choose the subject, or mark it as a free period, break or activity.");
                        break;
                    case ImportCellKind.Lesson when cell.TeacherId is null:
                        check.CellWarning(i, cell.Day, cell.Match switch
                        {
                            TeacherMatch.Ambiguous => $"{where}: several teachers match “{cell.TeacherText}”. Choose one, or leave the lesson unassigned.",
                            TeacherMatch.Ineligible => $"{where}: “{cell.TeacherText}” is not set up to teach {cell.ParsedSubject?.Subject} in the staff records. Choose a teacher, or leave it unassigned.",
                            TeacherMatch.None => $"{where}: no staff member matches “{cell.TeacherText}”. Choose the teacher, or leave it unassigned.",
                            TeacherMatch.Missing => $"{where}: the file names no teacher.",
                            _ => $"{where}: no teacher is chosen.",
                        } + " An unassigned lesson is not added to the timetable; it is listed after saving so a teacher can be assigned.");
                        break;
                    case ImportCellKind.Lesson when cell.Match == TeacherMatch.Check && cell.TeacherId == cell.SuggestedId:
                        check.CellWarning(i, cell.Day, $"{where}: “{cell.TeacherText}” was matched to {vocabulary.TeacherName(cell.TeacherId!.Value)} by part of the name. Check it is the right teacher.");
                        break;
                }
            }
        }

        // Overlapping or repeated periods on the same day; days without a period that later periods have.
        foreach (var day in form.Days)
        {
            var today = running.Where(r => r.Days.Contains(day)).OrderBy(r => r.Start).ToList();
            for (var k = 1; k < today.Count; k++)
                if (today[k].Start < today[k - 1].End)
                    check.RowError(today[k].Row, today[k].Start == today[k - 1].Start && today[k].End == today[k - 1].End
                        ? $"{today[k].Label} repeats {today[k - 1].Label} ({ScheduleRules.Times(today[k].Start, today[k].End)}) on {ScheduleRules.DayName(day)}. Remove one of them."
                        : $"{today[k].Label} overlaps {today[k - 1].Label} on {ScheduleRules.DayName(day)} ({ScheduleRules.Times(today[k - 1].Start, today[k - 1].End)} and {ScheduleRules.Times(today[k].Start, today[k].End)}). Correct the times.");
            for (var i = 0; i < form.Periods.Count; i++)
            {
                var cell = form.Periods[i].Cells.FirstOrDefault(c => c.Day == day);
                if (cell?.Kind == ImportCellKind.NoPeriod && form.Periods.Skip(i + 1).Any(r => r.Cells.Any(c => c.Day == day && c.Kind != ImportCellKind.NoPeriod)))
                    check.CellWarning(i, day, $"{ScheduleRules.DayName(day)} has no {Label(form.Periods[i], i)} but has later periods. If the period runs that day, set the cell to a lesson or a free period.");
            }
        }
        var missing = ScheduleRules.Days.Where(d => !form.Days.Contains(d)).ToList();
        if (missing.Count > 0 && form.Days.Count > 0 && !form.Notes.Any(n => n.StartsWith("No column for", StringComparison.Ordinal)))
            check.Warnings.Add($"{string.Join(", ", missing.Select(ScheduleRules.DayName))} {(missing.Count == 1 ? "has" : "have")} no periods in this timetable.");

        var lessons = form.Periods.SelectMany(r => r.Cells).Where(c => c.Kind == ImportCellKind.Lesson).ToList();
        check.Days = form.Days.Count(d => form.Periods.Any(r => r.Cells.Any(c => c.Day == d && c.Kind != ImportCellKind.NoPeriod)));
        check.Periods = form.Periods.Count(r => r.Cells.Any(c => c.Kind != ImportCellKind.NoPeriod));
        check.SubjectAssignments = lessons.Count(c => c.Subject is not null);
        check.TeacherAssignments = lessons.Count(c => c.Subject is not null && c.TeacherId is not null);
        check.Unassigned = lessons.Count(c => c.Subject is not null && c.TeacherId is null);
        check.UnmatchedTeachers = lessons.Count(c => c.TeacherId is null && !string.IsNullOrEmpty(c.TeacherText));
        check.Breaks = form.Periods.SelectMany(r => r.Cells).Count(c => c.Kind is ImportCellKind.Break or ImportCellKind.Activity);
        return check;
    }

    private async Task CountExistingAsync(TimetableImportForm form, ImportCheck check, CancellationToken ct)
    {
        var cls = await db.Classes.AsNoTracking().Where(c => c.Id == form.ClassId).Select(c => new { c.IsActive, c.Name }).FirstOrDefaultAsync(ct);
        if (cls is null) check.Errors.Add("This class no longer exists.");
        else if (!cls.IsActive) check.Errors.Add($"{cls.Name} is inactive. Activate it before saving its timetable.");
        if (form.YearId is not null && !await db.AcademicYears.AnyAsync(y => y.Id == form.YearId, ct)) check.Errors.Add("This academic year no longer exists.");
        check.ExistingLessons = await db.TimetableAssignments.CountAsync(t => t.SchoolClassId == form.ClassId && t.EndedOn == null, ct);
    }

    // ================================================================== the save (dry run or real)

    /// <summary>One period of the class to create: a row's lesson days (enabled), or its break / activity days (disabled, named).</summary>
    private sealed record SlotSpec(int Row, string? Name, TimeOnly Start, TimeOnly End, bool IsActive, int Order, IReadOnlyList<DayOfWeek> Days)
    {
        public int Mask => PeriodDays.Mask(Days.Count == ScheduleRules.Days.Length, Days);
    }

    /// <summary>
    /// Ends the class's current lessons, gives the class the uploaded periods (removing the school periods it no longer
    /// uses, for this class only) and adds every lesson that has a teacher, through the existing services, in one
    /// transaction. A dry run collects every problem and rolls back; a save commits only when there is none.
    /// </summary>
    private async Task<int?> RunAsync(TimetableImportForm form, ImportCheck check, bool commit, CancellationToken ct)
    {
        var specs = Specs(form);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // 1. The class's current lessons end from this week (history, attendance and earlier weeks are kept).
            var current = await db.TimetableAssignments.AsNoTracking().Where(t => t.SchoolClassId == form.ClassId && t.EndedOn == null)
                .Select(t => t.Id).ToListAsync(ct);
            foreach (var id in current)
            {
                var ended = await timetable.RemoveSessionAsync(id, ReplaceReason, ct);
                if (!ended.Succeeded) { check.Errors.AddRange(ended.Errors.Select(e => e.Value)); return null; }
            }

            // 2. The class's periods: keep one that is already exactly right, remove the others for this class, add the rest.
            var map = await periods.MapAsync(ct);
            var keep = new Dictionary<SlotSpec, int>();
            foreach (var spec in specs)
            {
                var same = map.ForClass(form.ClassId).FirstOrDefault(s => !keep.ContainsValue(s.Id) && s.Start == spec.Start && s.End == spec.End
                    && s.IsActive == spec.IsActive && s.Number == spec.Order && PeriodDays.Mask(PeriodDays.IsAll(s.DayMask), PeriodDays.Days(s.DayMask)) == spec.Mask
                    && string.Equals(s.Name ?? "", spec.Name ?? "", StringComparison.Ordinal));
                if (same is not null) keep[spec] = same.Id;
            }
            foreach (var old in map.ForClass(form.ClassId).Where(s => !keep.ContainsValue(s.Id)))
            {
                var removed = await periods.DeleteAsync(old.Id, PeriodScope.ThisClass, form.ClassId, endLessons: true, confirmation: null, ct);
                if (!removed.Result.Succeeded)
                {
                    check.Errors.Add($"{old.Label} cannot be removed from this class: {string.Join(" ", (removed.Plan?.Conflicts ?? []).DefaultIfEmpty(removed.Result.Errors.FirstOrDefault().Value))}");
                    return null;
                }
            }
            var slotOf = new Dictionary<(int Row, DayOfWeek Day), int>();
            foreach (var spec in specs)
            {
                if (!keep.TryGetValue(spec, out var slotId))
                {
                    var added = await periods.AddAsync(new PeriodInput(spec.Name, spec.Start, spec.End, spec.Order, spec.IsActive,
                        spec.Days.Count == ScheduleRules.Days.Length, spec.Days), PeriodScope.ThisClass, form.ClassId, null, ct);
                    if (!added.Result.Succeeded)
                    {
                        var why = added.Plan?.Conflicts is { Count: > 0 } c ? string.Join(" ", c) : string.Join(" ", added.Result.Errors.Select(e => e.Value));
                        check.RowError(spec.Row, $"{spec.Name ?? $"Period {spec.Order}"} cannot be added: {why}");
                        continue;
                    }
                    slotId = added.Result.EntityId!.Value;
                }
                foreach (var day in spec.Days) slotOf[(spec.Row, day)] = slotId;
            }
            if (check.HasErrors) return null;

            // 3. Every lesson with a subject and a teacher, through the same save as the Assign form (all its checks).
            var pending = new List<PendingLesson>();
            for (var i = 0; i < form.Periods.Count; i++)
            {
                var row = form.Periods[i];
                foreach (var cell in row.Cells.Where(c => c.Kind == ImportCellKind.Lesson && c.ParsedSubject is not null))
                {
                    var (dept, subject) = cell.ParsedSubject!.Value;
                    var slotId = slotOf[(i, cell.Day)];
                    if (cell.TeacherId is not { } teacherId)
                    {
                        pending.Add(new PendingLesson(cell.Day, slotId, Label(row, i), $"{row.Start}–{row.End}", subject,
                            await db.Departments.Where(d => d.Id == dept).Select(d => d.Name).FirstAsync(ct), cell.TeacherText));
                        continue;
                    }
                    var saved = await timetable.AssignAsync(new SessionInput(form.ClassId, cell.Day, slotId, dept, subject, teacherId, cell.Notes), ct);
                    if (!saved.Succeeded)
                        foreach (var e in saved.Errors) check.CellError(i, cell.Day, $"{ScheduleRules.DayName(cell.Day)}, {Label(row, i)}: {e.Value}");
                }
            }
            check.DryRun = true;
            if (!commit || check.HasErrors) return null;

            // 4. The record of the import (and the lessons still waiting for a teacher).
            var record = new TimetableImport
            {
                SchoolClassId = form.ClassId, AcademicYearId = form.YearId, FileName = form.FileName.Length == 0 ? "timetable" : form.FileName,
                SheetName = Cut(form.Sheet, TimetableImport.SheetMaxLength), Days = check.Days, Periods = check.Periods,
                SubjectAssignments = check.SubjectAssignments, TeacherAssignments = check.TeacherAssignments, UnassignedTeachers = pending.Count,
                Breaks = check.Breaks, Warnings = check.WarningCount, ReplacedLessons = current.Count,
                Details = JsonSerializer.Serialize(new TimetableImportDetails(pending, AllWarnings(form, check).Take(100).ToList()), Json),
                ImportedBy = currentUser.Name, ImportedAt = DateTime.UtcNow,
            };
            db.TimetableImports.Add(record);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return record.Id;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
        {
            logger.LogWarning(ex, "Timetable upload for class {ClassId} was rolled back", form.ClassId);
            check.Errors.Add("Nothing was saved: the timetable changed while it was being checked, or someone else is changing it. Check again in a moment.");
            return null;
        }
        finally
        {
            // A dry run (or a failed save) leaves nothing behind.
            if (db.Database.CurrentTransaction is not null && !(commit && check.DryRun && !check.HasErrors)) await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>The periods to create, in time order: each row's lesson days as one enabled period, its break / activity days as named disabled ones.</summary>
    private static List<SlotSpec> Specs(TimetableImportForm form)
    {
        var specs = new List<SlotSpec>();
        var rows = form.Periods.Select((r, i) => (Row: r, Index: i, Start: ParseHm(r.Start), End: ParseHm(r.End)))
            .Where(x => x.Start is not null && x.End is not null && x.Row.Cells.Any(c => c.Kind != ImportCellKind.NoPeriod)).ToList();
        // Display order: the period numbers in the file when they go up one row after another, otherwise 1, 2, 3…
        var lessonRows = rows.Where(x => x.Row.Cells.Any(c => c.Kind is ImportCellKind.Lesson or ImportCellKind.Free)).ToList();
        var fileNumbers = lessonRows.Select(x => TimetableExtractor.ParseNumber(x.Row.Label)).ToList();
        var useFile = fileNumbers.All(n => n is not null) && fileNumbers.Zip(fileNumbers.Skip(1)).All(p => p.Second > p.First);
        var order = 0;
        foreach (var x in rows)
        {
            var lessonDays = x.Row.Cells.Where(c => c.Kind is ImportCellKind.Lesson or ImportCellKind.Free).Select(c => c.Day).ToList();
            if (lessonDays.Count > 0)
            {
                order = useFile ? fileNumbers[lessonRows.FindIndex(l => l.Index == x.Index)]!.Value : order + 1;
                specs.Add(new SlotSpec(x.Index, GenericName().IsMatch(TextNormalizer.HeaderKey(x.Row.Label)) ? null : NullIfEmpty(x.Row.Label),
                    x.Start!.Value, x.End!.Value, true, Math.Clamp(order, 1, TimeSlot.MaxOrder), lessonDays));
            }
            foreach (var g in x.Row.Cells.Where(c => c.Kind is ImportCellKind.Break or ImportCellKind.Activity)
                         .GroupBy(c => NullIfEmpty(c.Label) ?? (c.Kind == ImportCellKind.Break ? "Break" : "Activity")))
                specs.Add(new SlotSpec(x.Index, Cut(g.Key, TimeSlot.NameMaxLength), x.Start!.Value, x.End!.Value, false, Math.Clamp(Math.Max(order, 1), 1, TimeSlot.MaxOrder), g.Select(c => c.Day).ToList()));
        }
        return specs;
    }

    private static IEnumerable<string> AllWarnings(TimetableImportForm form, ImportCheck check) =>
        check.Warnings.Concat(check.RowWarnings.OrderBy(r => r.Key).SelectMany(r => r.Value))
            .Concat(check.CellWarnings.OrderBy(c => c.Key, StringComparer.Ordinal).SelectMany(c => c.Value));

    // ================================================================== subjects and teachers

    private sealed class Vocabulary
    {
        public required List<SubjectGroup> Groups { get; init; }
        public required Dictionary<string, (int DepartmentId, string Subject)> Subjects { get; init; }
        public required List<(int Id, string Name, string Key, string[] Tokens)> Teachers { get; init; }
        public Dictionary<string, IReadOnlyList<(int Id, string Name)>> Eligible { get; } = [];

        public bool Has(int departmentId, string subject) => Groups.Any(g => g.DepartmentId == departmentId && g.Subjects.Contains(subject));
        public string TeacherName(int id) => Teachers.FirstOrDefault(t => t.Id == id).Name ?? "this teacher";

        public (int DepartmentId, string Subject)? Find(string? text)
        {
            var key = TextNormalizer.HeaderKey(text);
            if (key.Length == 0) return null;
            if (Subjects.TryGetValue(key, out var hit)) return hit;
            return Synonyms.TryGetValue(key, out var target) && Subjects.TryGetValue(target, out hit) ? hit : null;
        }
    }

    /// <summary>Other spellings of the school's subjects (used only when the subject exists in the school's departments).</summary>
    private static readonly Dictionary<string, string> Synonyms = BuildSynonyms();

    private async Task<Vocabulary> VocabularyAsync(CancellationToken ct)
    {
        var departments = await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var groups = new List<SubjectGroup>();
        var index = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        foreach (var d in departments)
        {
            var subjects = await timetable.SubjectsAsync(d.Id, ct);
            if (subjects.Count == 0) continue;
            groups.Add(new SubjectGroup(d.Id, d.Name, subjects));
            foreach (var s in subjects) index.TryAdd(TextNormalizer.HeaderKey(s), (d.Id, s));
        }
        var teachers = (await db.Teachers.AsNoTracking().Select(t => new { t.Id, t.FullName }).ToListAsync(ct))
            .Select(t => (t.Id, t.FullName, TextNormalizer.HeaderKey(t.FullName), NameTokens(t.FullName))).ToList();
        return new Vocabulary { Groups = groups, Subjects = index, Teachers = teachers };
    }

    private async Task<IReadOnlyList<(int Id, string Name)>> EligibleAsync(Vocabulary v, string subjectKey, CancellationToken ct)
    {
        if (v.Eligible.TryGetValue(subjectKey, out var list)) return list;
        var parsed = new ImportCellRow { Subject = subjectKey }.ParsedSubject;
        list = parsed is { } p ? await timetable.TeachersForAsync(p.DepartmentId, p.Subject, ct) : [];
        v.Eligible[subjectKey] = list;
        return list;
    }

    /// <summary>Splits a lesson cell into its subject, its teacher and any other text (a room, a group), and matches them.</summary>
    private async Task ResolveAsync(ImportCellRow cell, Vocabulary v, CancellationToken ct)
    {
        var raw = cell.Raw ?? "";
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var segments = lines.SelectMany(l => Separators().Split(l)).Select(TextNormalizer.CleanText).Where(s => s.Length > 0).ToList();
        (int, string)? subject = null;
        var rest = new List<string>();

        if (v.Find(raw.Replace('\n', ' ')) is { } whole) subject = whole;
        else if (lines.Select((l, i) => (Hit: v.Find(l), i)).FirstOrDefault(x => x.Hit is not null) is { Hit: { } lineHit } found)
        {
            subject = lineHit;
            rest = lines.Where((_, i) => i != found.i).SelectMany(l => Separators().Split(l)).Select(TextNormalizer.CleanText).Where(s => s.Length > 0).ToList();
        }
        else
        {
            // "Math Ahmed Ali", "Mr Ali - Math", "Science (Lab 2)": the longest leading words of a segment that name a subject.
            for (var s = 0; s < segments.Count && subject is null; s++)
            {
                var words = segments[s].Split(' ');
                for (var w = words.Length; w >= 1 && subject is null; w--)
                    if (v.Find(string.Join(' ', words.Take(w))) is { } hit)
                    {
                        subject = hit;
                        rest = segments.Where((_, k) => k != s).ToList();
                        if (w < words.Length) rest.Insert(0, string.Join(' ', words.Skip(w)));
                    }
            }
        }
        if (subject is { } sub) cell.Subject = ImportCellRow.SubjectKey(sub.Item1, sub.Item2);
        else rest = segments.Skip(1).ToList();

        // The teacher: the first remaining part that names a staff member; otherwise the first that looks like a name.
        var notes = new List<string>();
        foreach (var part in rest)
        {
            if (cell.TeacherText is null && !RoomPattern().IsMatch(part) && TextNormalizer.ContainsLetter(part)
                && (Candidates(part, v).Count > 0 || !rest.Any(r => r != part && Candidates(r, v).Count > 0)))
                cell.TeacherText = Cut(part, 100);
            else notes.Add(part);
        }
        cell.Notes = notes.Count == 0 ? null : Cut(string.Join(" · ", notes), TimetableAssignment.NotesMaxLength);
        await MatchTeacherAsync(cell, v, keepChoice: false, ct);
    }

    private async Task MatchTeacherAsync(ImportCellRow cell, Vocabulary v, bool keepChoice, CancellationToken ct)
    {
        if (cell.Subject is null) { cell.Match = string.IsNullOrEmpty(cell.TeacherText) ? TeacherMatch.Missing : TeacherMatch.None; return; }
        cell.MatchedFor = cell.Subject;
        if (keepChoice && cell.TeacherId is not null) return;
        cell.TeacherId = null;
        cell.SuggestedId = null;
        if (string.IsNullOrEmpty(cell.TeacherText)) { cell.Match = TeacherMatch.Missing; return; }

        var eligible = (await EligibleAsync(v, cell.Subject, ct)).Select(t => t.Id).ToHashSet();
        var all = Candidates(cell.TeacherText, v);
        var strong = all.Where(c => c.Strong).Select(c => c.Id).ToList();
        var weak = all.Select(c => c.Id).ToList();
        var strongEligible = strong.Where(eligible.Contains).ToList();
        var weakEligible = weak.Where(eligible.Contains).ToList();

        if (strongEligible.Count == 1) (cell.TeacherId, cell.Match) = (strongEligible[0], TeacherMatch.Matched);
        else if (strongEligible.Count > 1) cell.Match = TeacherMatch.Ambiguous;
        else if (strong.Count > 0) cell.Match = TeacherMatch.Ineligible;
        else if (weakEligible.Count == 1) (cell.TeacherId, cell.Match) = (weakEligible[0], TeacherMatch.Check);
        else if (weakEligible.Count > 1) cell.Match = TeacherMatch.Ambiguous;
        else cell.Match = weak.Count > 0 ? TeacherMatch.Ineligible : TeacherMatch.None;
        cell.SuggestedId = cell.TeacherId;
    }

    /// <summary>
    /// Staff whose name matches: Strong when the whole name, or every part of a name of two or more parts, matches;
    /// otherwise every teacher with that one name part (first name or surname). Titles (Mr, Ms, Miss, Dr, أ.) are ignored.
    /// </summary>
    private static List<(int Id, bool Strong)> Candidates(string text, Vocabulary v)
    {
        var tokens = NameTokens(text).Where(t => !Titles.Contains(t)).ToArray();
        if (tokens.Length == 0) return [];
        var key = string.Concat(tokens);
        var result = new List<(int, bool)>();
        foreach (var t in v.Teachers)
        {
            var all = tokens.All(x => t.Tokens.Any(u => u == x || (x.Length == 1 && u.StartsWith(x, StringComparison.Ordinal))));
            if (t.Key == key || (all && tokens.Count(x => x.Length > 1) >= 2)) result.Add((t.Id, true));
            else if (all && tokens.Any(x => x.Length > 1)) result.Add((t.Id, false));
        }
        return result;
    }

    private static string[] NameTokens(string? name) =>
        Regex.Split(TextNormalizer.NameKey(name), @"[^\p{L}\p{N}]+").Select(TextNormalizer.HeaderKey).Where(x => x.Length > 0).ToArray();

    private async Task<TimetableImportPreview> PreviewModelAsync(TimetableImportForm form, ImportCheck check, Vocabulary v, CancellationToken ct)
    {
        var teachers = new Dictionary<string, IReadOnlyList<(int Id, string Name)>>();
        foreach (var key in form.Periods.SelectMany(r => r.Cells).Where(c => c.Subject is not null).Select(c => c.Subject!).Distinct())
            teachers[key] = await EligibleAsync(v, key, ct);
        var className = await db.Classes.AsNoTracking().Where(c => c.Id == form.ClassId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "Unknown class";
        var yearName = form.YearId is null ? null : await db.AcademicYears.AsNoTracking().Where(y => y.Id == form.YearId).Select(y => y.Name).FirstOrDefaultAsync(ct);
        return new TimetableImportPreview
        {
            Form = form, Check = check, ClassName = className, YearName = yearName, Subjects = v.Groups, Teachers = teachers,
            TeacherNames = v.Teachers.ToDictionary(t => t.Id, t => t.Name),
        };
    }

    // ================================================================== helpers

    public static string Label(ImportPeriodRow row, int index) => string.IsNullOrWhiteSpace(row.Label) ? $"Period {index + 1}" : row.Label!;

    public static TimeOnly? ParseHm(string? text) =>
        TimeOnly.TryParseExact((text ?? "").Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static string? Hm(TimeOnly? t) => t?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max].TrimEnd();

    private static Dictionary<string, string> BuildSynonyms()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string target, params string[] words) { foreach (var w in words) map[TextNormalizer.HeaderKey(w)] = TextNormalizer.HeaderKey(target); }
        Add("Math", "Maths", "Mathematics", "Mathematic", "رياضيات", "الرياضيات");
        Add("English", "English Language", "English Lang", "Eng", "انجليزي", "الانجليزية", "اللغة الانجليزية", "لغة انجليزية");
        Add("Arabic", "Arabic Language", "Arb", "عربي", "العربية", "اللغة العربية", "لغة عربية");
        Add("Science", "Sciences", "General Science", "علوم", "العلوم");
        Add("PE", "P.E.", "P.E", "Physical Education", "Sport", "Sports", "Gym", "تربية رياضية", "التربية الرياضية", "العاب");
        Add("ICT", "Computer", "Computers", "Computing", "Computer Science", "IT", "حاسب", "حاسب الي", "الحاسب الالي", "كمبيوتر");
        Add("AI", "Artificial Intelligence", "ذكاء اصطناعي");
        Add("Religion", "Islamic", "Islamic Studies", "Islamic Education", "Religious Education", "RE", "R.E.", "دين", "تربية دينية", "التربية الدينية", "التربية الاسلامية", "تربية اسلامية");
        Add("Quran", "Holy Quran", "Quraan", "Koran", "Qur'an", "قران", "القران", "القران الكريم");
        Add("Christian Religion", "Christian", "Christianity", "Christian Education", "تربية مسيحية", "التربية المسيحية");
        Add("Deutsch", "German", "الماني", "الالمانية", "اللغة الالمانية");
        Add("French", "French Language", "فرنساوي", "فرنسي", "الفرنسية", "اللغة الفرنسية");
        Add("Library / Reading", "Library", "Reading", "Library and Reading", "مكتبة", "قراءة");
        Add("Art", "Arts", "Visual Art", "Visual Arts", "Art & Design", "Art and Design", "فنون", "تربية فنية", "التربية الفنية", "رسم");
        Add("Music", "تربية موسيقية", "التربية الموسيقية", "موسيقى");
        Add("Drama", "Theatre", "Theater", "مسرح", "دراما");
        Add("ASS", "Arabic Social Studies", "دراسات اجتماعية", "الدراسات الاجتماعية", "دراسات");
        Add("ESS", "English Social Studies");
        Add("Wellbeing", "Well-being", "Well being");
        Add("Character Building", "Character", "Character Education");
        return map;
    }

    [GeneratedRegex(@"\s*(?:\r?\n|[|/;•]|\s[-–—]\s|[()\[\]]|,)\s*")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^(?:room|rm|r|lab|hall|class)\s*\.?\s*\d+[a-z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex RoomPattern();

    /// <summary>Titles before a teacher's name ("Mr.", "Ms", "Miss", "أ.", "مستر"), as name-part keys: ignored when matching.</summary>
    private static readonly HashSet<string> Titles = new(
        new[] { "mr", "mrs", "ms", "miss", "mx", "dr", "prof", "sir", "mister", "madam", "teacher", "coach", "ا", "أ", "استاذ", "أستاذ", "الاستاذ", "الأستاذ", "استاذة", "أستاذة", "الاستاذة", "الأستاذة", "مستر", "مس", "ميس" }
            .Select(TextNormalizer.HeaderKey), StringComparer.Ordinal);

    /// <summary>A label that is only a period number ("1", "p1", "period1", "lesson3", "3rd", "الحصه1").</summary>
    [GeneratedRegex(@"^(?:period|lesson|p|class|الحصه|حصه)?\d{1,2}(?:st|nd|rd|th)?$")]
    private static partial Regex GenericName();
}

/// <summary>Checks the bytes of an upload against what its name says it is. Uploaded files are never executed or stored.</summary>
public static class FileCheck
{
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Gif = [0x47, 0x49, 0x46, 0x38];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0];

    private const string NoOcr = "Timetables are read from Excel (.xlsx) or CSV files. A PDF or a picture cannot be read automatically in THRYD (there is no text recognition) — export the timetable from Excel, or type it into the Excel template, and upload that.";

    public static string? Problem(byte[] bytes, string extension)
    {
        bool Starts(byte[] sig) => bytes.Length >= sig.Length && bytes.AsSpan(0, sig.Length).SequenceEqual(sig);
        if (Starts(Pdf) || extension == ".pdf") return NoOcr;
        if (Starts(Png) || Starts(Jpeg) || Starts(Gif) || extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic") return NoOcr;
        if (extension == ".xls" || (Starts(Ole) && extension != ".xlsx")) return "This is an old Excel file (.xls). Open it in Excel and save it as an Excel workbook (.xlsx), then upload that.";
        if (!TimetableImportService.AllowedExtensions.Contains(extension)) return "Upload the timetable as an Excel workbook (.xlsx) or a CSV file (.csv).";
        if (extension == ".xlsx" && !Starts(Zip)) return "This file is not a valid Excel workbook (.xlsx). It may be damaged or renamed; save it again from Excel and try again.";
        if (extension == ".csv" && (Starts(Zip) || bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).IndexOf((byte)0) >= 0 && !(bytes.Length > 1 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))))
            return "This file is not a text CSV file. Save the timetable as .xlsx or .csv and try again.";
        return null;
    }
}
