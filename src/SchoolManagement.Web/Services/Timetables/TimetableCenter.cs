using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.Pdf;

namespace SchoolManagement.Web.Services.Timetables;

// ============================================================================================
// Timetables Center. Every timetable of the school is an ITimetableSource registered in DI
// (Program.cs). The center lists whatever sources are registered, so a future timetable type is
// one new class + one registration — the page, filters, downloads and ZIP need no change.
// Sources only READ the existing data (timetable sessions, substitutions, P.E. details, duties,
// time slots) through the existing services; nothing is copied or changed.
// ============================================================================================

/// <summary>The filters a timetable type understands (the page shows only these for that type).</summary>
[Flags]
public enum TimetableFilters
{
    None = 0,
    Department = 1,
    Subject = 2,
    Teacher = 4,
    Class = 8,
    Day = 16,
}

/// <summary>What the user selected. Weeks come from the academic year and the date range.</summary>
public sealed record TimetableScope(DateOnly Week, int? DepartmentId = null, string? Subject = null, int? TeacherId = null, int? ClassId = null);

/// <summary>One downloadable timetable (a class, a teacher, a subject…). <see cref="Group"/> becomes a folder in a ZIP.</summary>
public sealed record TimetableItem(string Category, string Id, string Name, string? Detail, string? Group, string? PageUrl);

public interface ITimetableSource
{
    /// <summary>Stable key used in URLs (e.g. "teacher").</summary>
    string Key { get; }
    string Title { get; }
    string Description { get; }
    string Icon { get; }

    /// <summary>Top-level folder in ZIP downloads.</summary>
    string Folder { get; }
    int Order { get; }
    TimetableFilters Filters { get; }

    Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope scope, CancellationToken ct);

    /// <summary>The PDF of one item for one week (only the given days); null when the item no longer exists.</summary>
    Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct);
}

/// <summary>Lessons of a week read from the timetable (active sessions + substitutes in their slots). Shared by the sources.</summary>
public sealed class TimetableData(SchoolDbContext db, ITimetableService timetable)
{
    public Task<IReadOnlyList<SlotView>> SlotsAsync(CancellationToken ct) => timetable.SlotsAsync(ct);

    public IQueryable<TimetableAssignment> Active(DateOnly week) => db.TimetableAssignments.AsNoTracking().ActiveIn(SchoolWeek.StartOf(week));

    public async Task<List<WeekLesson>> LessonsAsync(DateOnly week, Func<IQueryable<TimetableAssignment>, IQueryable<TimetableAssignment>>? filter, CancellationToken ct)
    {
        week = SchoolWeek.StartOf(week);
        var q = Active(week);
        if (filter is not null) q = filter(q);
        var rows = await q.Select(t => new
        {
            t.Id, t.Day, t.TimeSlotId, t.SchoolClassId, ClassName = t.SchoolClass.Name, t.SchoolClass.SortOrder, t.DepartmentId,
            Department = t.Department.Name, t.Subject, t.TeacherId, Teacher = t.Teacher.FullName,
        }).ToListAsync(ct);
        var subs = (await db.WorkloadAssignments.AsNoTracking()
                .Where(w => w.Category == WorkloadCategory.Substitution && w.WeekStart == week && w.Day != null && w.TimeSlotId != null && w.SchoolClassId != null)
                .Select(w => new { Day = w.Day!.Value, SlotId = w.TimeSlotId!.Value, ClassId = w.SchoolClassId!.Value, w.Teacher.FullName }).ToListAsync(ct))
            .GroupBy(w => (w.Day, w.SlotId, w.ClassId)).ToDictionary(g => g.Key, g => g.First().FullName);
        return rows.Select(r => new WeekLesson(r.Id, r.Day, r.TimeSlotId, r.SchoolClassId, r.ClassName, r.SortOrder, r.DepartmentId, r.Department,
                r.Subject, r.TeacherId, r.Teacher, subs.GetValueOrDefault((r.Day, r.TimeSlotId, r.SchoolClassId))))
            .ToList();
    }
}

// ============================================================================================ sources

/// <summary>Class timetables (Timetable → Classes): the same grid and PDF as the class page.</summary>
public sealed class ClassTimetableSource(ITimetableService timetable) : ITimetableSource
{
    public string Key => "class";
    public string Title => "Class Timetables";
    public string Description => "Each class's weekly timetable: subject and teacher in every period, with this week's substitutes.";
    public string Icon => "door";
    public string Folder => "Class Timetables";
    public int Order => 10;
    public TimetableFilters Filters => TimetableFilters.Class | TimetableFilters.Day;

    public async Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope s, CancellationToken ct) =>
        (await timetable.ClassesAsync(ct))
            .Where(c => (c.IsActive || c.Sessions > 0) && (s.ClassId is null || c.Id == s.ClassId))
            .Select(c => new TimetableItem(Key, c.Id.ToString(CultureInfo.InvariantCulture), c.Name,
                $"{Ui.Plural(c.Sessions, "session", "sessions")} a week · {Ui.Plural(c.Students, "student", "students")}{(c.IsActive ? "" : " · inactive")}",
                null, $"/timetable/classes/{c.Id}?week={SchoolWeek.Iso(s.Week)}"))
            .ToList();

    public async Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct)
    {
        if (!int.TryParse(id, out var classId)) return null;
        var cls = (await timetable.ClassesAsync(ct)).FirstOrDefault(c => c.Id == classId);
        if (cls is null) return null;
        var grid = await timetable.ClassGridAsync(classId, week, ct);
        var details = $"{Ui.Plural(grid.Sessions.Count, "teaching session", "teaching sessions")} a week · {Ui.Plural(cls.Students, "student", "students")}{(cls.IsActive ? "" : " · inactive class")}";
        return PdfReports.ClassTimetable(grid, cls.Name, details, days);
    }
}

/// <summary>Teacher timetables (Timetable → Teachers): lessons, substitutions, office hours, duties and workload — the same PDF as the teacher page.</summary>
public sealed class TeacherTimetableSource(ITimetableService timetable, ITeacherService teachers, IWorkloadService workload, TimetableData data) : ITimetableSource
{
    public string Key => "teacher";
    public string Title => "Teacher Timetables";
    public string Description => "Each teacher's week: lessons, substitutions, office hours and duties, with the weekly workload.";
    public string Icon => "user";
    public string Folder => "Teacher Timetables";
    public int Order => 20;
    public TimetableFilters Filters => TimetableFilters.Department | TimetableFilters.Subject | TimetableFilters.Teacher | TimetableFilters.Day;

    public async Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope s, CancellationToken ct)
    {
        var list = await teachers.ListAllAsync(new TeacherListQuery { DepartmentId = s.DepartmentId, PageSize = 10_000 }, ct);
        var counts = await data.Active(s.Week).GroupBy(t => t.TeacherId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        HashSet<int>? teachesSubject = null;
        if (!string.IsNullOrWhiteSpace(s.Subject))
            teachesSubject = (await data.Active(s.Week).Where(t => t.Subject == s.Subject && (s.DepartmentId == null || t.DepartmentId == s.DepartmentId))
                .Select(t => t.TeacherId).Distinct().ToListAsync(ct)).ToHashSet();
        return list
            .Where(t => s.TeacherId is null || t.Id == s.TeacherId)
            .Where(t => teachesSubject is null || teachesSubject.Contains(t.Id) || string.Equals(t.Subject, s.Subject, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Subcategory, s.Subject, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.DepartmentName ?? "~").ThenBy(t => t.FullName)
            .Select(t => new TimetableItem(Key, t.Id.ToString(CultureInfo.InvariantCulture), t.FullName,
                $"{t.DepartmentName ?? "No department"}{(t.Subcategory is null ? "" : " · " + DepartmentCatalog.Detail(t.Subcategory, t.Subject))} · {Ui.Plural(counts.GetValueOrDefault(t.Id), "lesson", "lessons")} a week",
                t.DepartmentName ?? "No department", $"/timetable/teachers/{t.Id}?week={SchoolWeek.Iso(s.Week)}"))
            .ToList();
    }

    public async Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct)
    {
        if (!int.TryParse(id, out var teacherId)) return null;
        var t = await teachers.GetAsync(teacherId, ct);
        if (t is null) return null;
        var department = t.DepartmentName is null ? null : $"{t.DepartmentName}{(t.SubcategoryLabel.Length == 0 ? "" : " · " + t.SubcategoryLabel)}";
        return PdfReports.TeacherTimetable(await timetable.TeacherGridAsync(teacherId, week, ct), t.FullName, department, await workload.SummaryAsync(teacherId, week, ct), days);
    }
}

/// <summary>Subject timetables: one subject across every class that has it (read from the class timetables).</summary>
public sealed class SubjectTimetableSource(TimetableData data) : ITimetableSource
{
    public string Key => "subject";
    public string Title => "Subject Timetables";
    public string Description => "One subject across all classes: when and where it is taught, and by whom.";
    public string Icon => "list";
    public string Folder => "Subject Timetables";
    public int Order => 30;
    public TimetableFilters Filters => TimetableFilters.Department | TimetableFilters.Subject | TimetableFilters.Day;

    public async Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope s, CancellationToken ct)
    {
        var q = data.Active(s.Week);
        if (s.DepartmentId is { } d) q = q.Where(t => t.DepartmentId == d);
        if (!string.IsNullOrWhiteSpace(s.Subject)) q = q.Where(t => t.Subject == s.Subject);
        var rows = await q.GroupBy(t => new { t.DepartmentId, Department = t.Department.Name, t.Subject })
            .Select(g => new { g.Key.DepartmentId, g.Key.Department, g.Key.Subject, Lessons = g.Count(), Classes = g.Select(x => x.SchoolClassId).Distinct().Count() })
            .ToListAsync(ct);
        return rows.OrderBy(r => r.Department).ThenBy(r => r.Subject)
            .Select(r => new TimetableItem(Key, $"{r.DepartmentId}:{r.Subject}", r.Subject,
                $"{r.Department} · {Ui.Plural(r.Classes, "class", "classes")} · {Ui.Plural(r.Lessons, "lesson", "lessons")} a week", r.Department, null))
            .ToList();
    }

    public async Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct)
    {
        var parts = id.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var deptId)) return null;
        var subject = parts[1];
        var lessons = await data.LessonsAsync(week, q => q.Where(t => t.DepartmentId == deptId && t.Subject == subject), ct);
        var department = lessons.FirstOrDefault()?.DepartmentName;
        if (department is null) return null;
        return TimetableReports.Subject(SchoolWeek.StartOf(week), await data.SlotsAsync(ct), department, subject, lessons, days);
    }
}

/// <summary>Department timetables: every lesson of a department, teacher by teacher, day by day.</summary>
public sealed class DepartmentTimetableSource(SchoolDbContext db, TimetableData data) : ITimetableSource
{
    public string Key => "department";
    public string Title => "Department Timetables";
    public string Description => "Each academic department's week: its teachers down the side, the periods across, one page per day.";
    public string Icon => "layers";
    public string Folder => "Department Timetables";
    public int Order => 40;
    public TimetableFilters Filters => TimetableFilters.Department | TimetableFilters.Day;

    public async Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope s, CancellationToken ct)
    {
        var lessons = await data.Active(s.Week).GroupBy(t => t.DepartmentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var departments = await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, Teachers = d.StaffMembers.Count }).ToListAsync(ct);
        return departments
            .Where(d => (s.DepartmentId is null || d.Id == s.DepartmentId) && (lessons.ContainsKey(d.Id) || d.Teachers > 0))
            .Select(d => new TimetableItem(Key, d.Id.ToString(CultureInfo.InvariantCulture), d.Name,
                $"{Ui.Plural(d.Teachers, "teacher", "teachers")} · {Ui.Plural(lessons.GetValueOrDefault(d.Id), "lesson", "lessons")} a week", null, $"/staff/departments/{d.Id}"))
            .ToList();
    }

    public async Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct)
    {
        if (!int.TryParse(id, out var deptId)) return null;
        var name = await db.Departments.AsNoTracking().Where(d => d.Id == deptId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        if (name is null) return null;
        var lessons = await data.LessonsAsync(week, q => q.Where(t => t.DepartmentId == deptId), ct);
        var teacherIds = lessons.Select(l => l.TeacherId).Distinct().ToList();
        var teachers = (await db.Teachers.AsNoTracking().Where(t => t.DepartmentId == deptId || teacherIds.Contains(t.Id)).OrderBy(t => t.FullName)
            .Select(t => new { t.Id, t.FullName }).ToListAsync(ct)).Select(t => (t.Id, t.FullName)).ToList();
        return TimetableReports.Department(SchoolWeek.StartOf(week), await data.SlotsAsync(ct), name, teachers, lessons, days);
    }
}

/// <summary>The school-wide (master) timetable: every class, one page per day.</summary>
public sealed class SchoolTimetableSource(SchoolDbContext db, TimetableData data) : ITimetableSource
{
    public string Key => "school";
    public string Title => "School-wide Timetable";
    public string Description => "The master timetable: every class and period of the school on one page per day.";
    public string Icon => "school";
    public string Folder => "School Timetable";
    public int Order => 50;
    public TimetableFilters Filters => TimetableFilters.Day;

    public async Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope s, CancellationToken ct)
    {
        var lessons = await data.Active(s.Week).CountAsync(ct);
        var classes = await db.Classes.CountAsync(c => c.IsActive, ct);
        return [new TimetableItem(Key, "all", "Whole school", $"{Ui.Plural(classes, "active class", "active classes")} · {Ui.Plural(lessons, "lesson", "lessons")} a week", null, null)];
    }

    public async Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct)
    {
        var lessons = await data.LessonsAsync(week, null, ct);
        var withLessons = lessons.Select(l => l.ClassId).ToHashSet();
        var classes = (await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name, c.IsActive }).ToListAsync(ct))
            .Where(c => c.IsActive || withLessons.Contains(c.Id)).Select(c => (c.Id, c.Name)).ToList();
        return TimetableReports.School(SchoolWeek.StartOf(week), await data.SlotsAsync(ct), classes, lessons, days);
    }
}

/// <summary>The P.E. timetable (Duties → P.E. / PE department page): the same PDF as /pe.</summary>
public sealed class PeTimetableSource(IPeService pe, ITimetableService timetable, ITeacherService teachers) : ITimetableSource
{
    public string Key => "pe";
    public string Title => "P.E. Timetable";
    public string Description => "Physical Education sessions of every class with facility and co-teacher.";
    public string Icon => "activity";
    public string Folder => "PE Timetable";
    public int Order => 60;
    public TimetableFilters Filters => TimetableFilters.Department | TimetableFilters.Day;

    public async Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope s, CancellationToken ct)
    {
        var dept = await pe.DepartmentIdAsync(ct);
        if (dept is null || (s.DepartmentId is { } d && d != dept)) return [];
        var sessions = await pe.WeekAsync(SchoolWeek.StartOf(s.Week), ct);
        return [new TimetableItem(Key, "pe", "P.E. Timetable", $"{Ui.Plural(sessions.Count, "session", "sessions")} a week", null, $"/pe?date={SchoolWeek.Iso(s.Week)}")];
    }

    public async Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct)
    {
        var dept = await pe.DepartmentIdAsync(ct);
        if (dept is null) return null;
        week = SchoolWeek.StartOf(week);
        var slots = (await timetable.SlotsAsync(ct)).Where(s => s.IsActive).ToList();
        var department = (await teachers.DepartmentsAsync(ct)).FirstOrDefault(d => d.Id == dept)?.Name;
        var count = (await teachers.ListAllAsync(new TeacherListQuery { DepartmentId = dept, PageSize = 10_000 }, ct)).Count;
        return PdfReports.PeTimetable(week, slots, await pe.WeekAsync(week, ct), department, count, days);
    }
}

/// <summary>Duty rotas (Duties): morning, midday and dismissal duties of the week with their staff.</summary>
public sealed class DutyRotaSource(IDutyService duties) : ITimetableSource
{
    public string Key => "duty";
    public string Title => "Duty Rotas";
    public string Description => "Morning, midday and dismissal duties of the week: time, place, assigned staff and backup.";
    public string Icon => "shield-check";
    public string Folder => "Duty Rotas";
    public int Order => 70;
    public TimetableFilters Filters => TimetableFilters.Day;

    private Task<IReadOnlyList<DutyRow>> WeekAsync(DateOnly week, DutyType? type, CancellationToken ct)
    {
        week = SchoolWeek.StartOf(week);
        return duties.AuditListAllAsync(new DutyAuditQuery { From = SchoolWeek.Iso(week), To = SchoolWeek.Iso(week.AddDays(6)), Type = type?.ToString() }, ct);
    }

    public async Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope s, CancellationToken ct)
    {
        var rows = await WeekAsync(s.Week, null, ct);
        return DutyComponents.Types.Select(t => new TimetableItem(Key, DutyComponents.TypeSlug(t), DutyComponents.TypeName(t),
            $"{Ui.Plural(rows.Count(r => r.DutyType == t), "duty", "duties")} this week", null,
            $"/duties?type={DutyComponents.TypeSlug(t)}&date={SchoolWeek.Iso(s.Week)}")).ToList();
    }

    public async Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct)
    {
        if (!DutyComponents.Types.Any(t => DutyComponents.TypeSlug(t) == id)) return null;
        var type = DutyComponents.TypeFromSlug(id);
        return TimetableReports.DutyRota(SchoolWeek.StartOf(week), type, await WeekAsync(week, type, ct), days);
    }
}

/// <summary>The bell schedule (Timetable → Time Slots): the ten periods and their times.</summary>
public sealed class BellScheduleSource(TimetableData data) : ITimetableSource
{
    public string Key => "bells";
    public string Title => "Bell Schedule";
    public string Description => "The school day's periods and their start and end times (Time Slots).";
    public string Icon => "clock";
    public string Folder => "Bell Schedule";
    public int Order => 80;
    public TimetableFilters Filters => TimetableFilters.None;

    public async Task<IReadOnlyList<TimetableItem>> ItemsAsync(TimetableScope s, CancellationToken ct)
    {
        var slots = await data.SlotsAsync(ct);
        return [new TimetableItem(Key, "slots", "Bell Schedule", $"{slots.Count(x => x.IsActive)} active periods · {slots.Count(x => x.HasTimes)} with times", null, "/timetable/slots")];
    }

    public async Task<PdfReport?> ReportAsync(string id, DateOnly week, IReadOnlyList<DayOfWeek> days, CancellationToken ct)
    {
        var perSlot = await data.Active(week).GroupBy(t => t.TimeSlotId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return TimetableReports.BellSchedule(SchoolWeek.StartOf(week), await data.SlotsAsync(ct), perSlot);
    }
}

// ============================================================================================ center

/// <summary>A selection on the Timetables Center: category, academic year, weeks, day and the category's filters.</summary>
public sealed record TimetableRequest(
    string? Category, int? YearId, string? YearName, IReadOnlyList<DateOnly> Weeks, DayOfWeek? Day,
    int? DepartmentId, string? Subject, int? TeacherId, int? ClassId)
{
    public IReadOnlyList<DayOfWeek> Days => Day is { } d ? [d] : ScheduleRules.Days;
    public DateOnly FirstWeek => Weeks[0];

    public TimetableScope Scope(ITimetableSource source) => new(FirstWeek,
        source.Filters.HasFlag(TimetableFilters.Department) ? DepartmentId : null,
        source.Filters.HasFlag(TimetableFilters.Subject) ? Subject : null,
        source.Filters.HasFlag(TimetableFilters.Teacher) ? TeacherId : null,
        source.Filters.HasFlag(TimetableFilters.Class) ? ClassId : null);
}

public sealed record TimetableFile(byte[] Bytes, string ContentType, string FileName, int Documents);

public sealed record TimetableFilterOptions(
    IReadOnlyList<(int Id, string Name)> Departments, IReadOnlyList<string> Subjects,
    IReadOnlyList<(int Id, string Name, string? Department)> Teachers, IReadOnlyList<(int Id, string Name)> Classes);

public interface ITimetableCenter
{
    IReadOnlyList<ITimetableSource> Sources { get; }
    ITimetableSource? Source(string? key);
    Task<TimetableFilterOptions> OptionsAsync(TimetableRequest request, CancellationToken ct = default);

    /// <summary>The items of one source matching the request's filters.</summary>
    Task<IReadOnlyList<TimetableItem>> ItemsAsync(ITimetableSource source, TimetableRequest request, CancellationToken ct = default);

    /// <summary>One PDF (one item, one week) or a ZIP of separate PDFs in category / department folders.</summary>
    /// <param name="forceZip">Always a ZIP (Download All, department packs), even for a single timetable.</param>
    Task<TimetableFile?> DownloadAsync(IReadOnlyList<(ITimetableSource Source, TimetableItem Item)> items, TimetableRequest request, string bundleName, bool flatGroups = false, bool forceZip = false, CancellationToken ct = default);
}

public sealed class TimetableCenter(IEnumerable<ITimetableSource> sources, SchoolDbContext db, TimetableData data, IPdfExportService pdf) : ITimetableCenter
{
    /// <summary>Upper bound on PDFs in one download (e.g. 69 teachers × 20 weeks would be too many).</summary>
    public const int MaxDocuments = 1500;

    private readonly IReadOnlyList<ITimetableSource> _sources = sources.OrderBy(s => s.Order).ToList();

    public IReadOnlyList<ITimetableSource> Sources => _sources;

    public ITimetableSource? Source(string? key) => _sources.FirstOrDefault(s => s.Key == key);

    public async Task<TimetableFilterOptions> OptionsAsync(TimetableRequest r, CancellationToken ct = default)
    {
        var departments = (await db.Departments.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
            .Select(d => (d.Id, d.Name)).ToList();
        var active = data.Active(r.FirstWeek);
        if (r.DepartmentId is { } dept) active = active.Where(t => t.DepartmentId == dept);
        var subjects = await active.Select(t => t.Subject).Distinct().OrderBy(s => s).ToListAsync(ct);
        var teachers = db.Teachers.AsNoTracking();
        if (r.DepartmentId is { } d2) teachers = teachers.Where(t => t.DepartmentId == d2 || active.Any(a => a.TeacherId == t.Id));
        if (!string.IsNullOrWhiteSpace(r.Subject))
        {
            var subject = r.Subject;
            teachers = teachers.Where(t => active.Any(a => a.TeacherId == t.Id && a.Subject == subject) || t.Subject == subject || t.Subcategory == subject);
        }
        var teacherList = (await teachers.OrderBy(t => t.FullName).Select(t => new { t.Id, t.FullName, Department = t.Department != null ? t.Department.Name : null }).ToListAsync(ct))
            .Select(t => (t.Id, t.FullName, t.Department)).ToList();
        var classes = (await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Select(c => (c.Id, c.Name)).ToList();
        return new TimetableFilterOptions(departments, subjects, teacherList, classes);
    }

    public Task<IReadOnlyList<TimetableItem>> ItemsAsync(ITimetableSource source, TimetableRequest request, CancellationToken ct = default) =>
        source.ItemsAsync(request.Scope(source), ct);

    public async Task<TimetableFile?> DownloadAsync(IReadOnlyList<(ITimetableSource Source, TimetableItem Item)> items, TimetableRequest request, string bundleName, bool flatGroups = false, bool forceZip = false, CancellationToken ct = default)
    {
        if (items.Count == 0) return null;
        var multiWeek = request.Weeks.Count > 1;

        // A single timetable for a single week: the PDF itself.
        if (items.Count == 1 && !multiWeek && !forceZip)
        {
            var (source, item) = items[0];
            var report = await source.ReportAsync(item.Id, request.FirstWeek, request.Days, ct);
            if (report is null) return null;
            report = Decorate(report, request, multiWeek: false);
            return new TimetableFile(await pdf.RenderAsync(report, ct), "application/pdf", report.FileName, 1);
        }

        using var buffer = new MemoryStream();
        var documents = 0;
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (source, item) in items)
                foreach (var week in request.Weeks)
                {
                    ct.ThrowIfCancellationRequested();
                    var report = await source.ReportAsync(item.Id, week, request.Days, ct);
                    if (report is null) continue;
                    report = Decorate(report, request, multiWeek);
                    var folder = Folder(source.Folder) + (flatGroups || item.Group is null ? "" : "/" + Folder(item.Group));
                    var path = Unique($"{folder}/{report.FileName}", used);
                    var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                    var bytes = await pdf.RenderAsync(report, ct);
                    await using (var s = entry.Open()) await s.WriteAsync(bytes, ct);
                    documents++;
                }
        }
        if (documents == 0) return null;
        var name = PdfFileName.From([bundleName, request.Weeks.Count == 1 ? $"Week {SchoolWeek.Iso(request.FirstWeek)}" : $"{SchoolWeek.Iso(request.FirstWeek)} to {SchoolWeek.Iso(request.Weeks[^1])}"]);
        return new TimetableFile(buffer.ToArray(), "application/zip", Path.ChangeExtension(name, ".zip"), documents);
    }

    /// <summary>The chosen academic year in the header (instead of the one in Settings), the day filter, and the week in multi-week file names.</summary>
    private static PdfReport Decorate(PdfReport r, TimetableRequest request, bool multiWeek)
    {
        var meta = new List<PdfMeta>();
        if (request.YearName is not null) meta.Add(new("Academic Year", request.YearName));
        meta.AddRange(r.Meta);
        if (request.Day is { } d) meta.Add(new("Day", ScheduleRules.DayName(d)));
        var parts = r.FileNameParts.ToList();
        if (request.Day is { } day) parts.Add(ScheduleRules.DayName(day));
        if (multiWeek && meta.FirstOrDefault(m => m.Label == "Week")?.Value is { } label) parts.Add(label);
        return new PdfReport
        {
            Title = r.Title, Subtitle = r.Subtitle, Meta = meta, Sections = r.Sections, FileNameParts = parts,
            ShowAcademicYear = request.YearName is null && r.ShowAcademicYear,
        };
    }

    /// <summary>A readable, safe folder name (letters of any script, digits, spaces, dots, dashes).</summary>
    private static string Folder(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '.' or '&' ? ch : ' ');
        var clean = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return clean.Length == 0 ? "Other" : clean;
    }

    private static string Unique(string path, HashSet<string> used)
    {
        if (used.Add(path)) return path;
        var ext = Path.GetExtension(path);
        var stem = path[..^ext.Length];
        for (var i = 2; ; i++)
            if (used.Add($"{stem}_{i}{ext}")) return $"{stem}_{i}{ext}";
    }
}
