using SchoolManagement.Web.Common;

namespace SchoolManagement.Web.Services.Timetables.Import;

// ============================================================================================
// Timetables → Upload Timetable. The extracted timetable travels between the preview steps as
// this form (every value is posted back, so nothing is stored until the administrator saves):
// the class and year, the days, and one row per period with one cell per day.
// ============================================================================================

/// <summary>How the teacher named in a cell was matched to the staff records.</summary>
public static class TeacherMatch
{
    /// <summary>The full name (or every part of it) matches one teacher of the subject.</summary>
    public const string Matched = "matched";

    /// <summary>Only one part of the name matches (e.g. "Ms Hana"): assigned, but to be checked.</summary>
    public const string Check = "check";

    /// <summary>Several teachers match the name: left unassigned.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>A staff member matches the name but is not set up to teach this subject: left unassigned.</summary>
    public const string Ineligible = "ineligible";

    /// <summary>No staff member matches the name: left unassigned.</summary>
    public const string None = "none";

    /// <summary>The file names no teacher for the lesson.</summary>
    public const string Missing = "missing";
}

public sealed class TimetableImportForm
{
    public int ClassId { get; set; }
    public int? YearId { get; set; }
    public string FileName { get; set; } = "";
    public string? Sheet { get; set; }
    public List<DayOfWeek> Days { get; set; } = [];
    public List<ImportPeriodRow> Periods { get; set; } = [];

    /// <summary>What the extraction noticed (kept as warnings until the timetable is saved).</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>The administrator confirmed that the class's current timetable is replaced.</summary>
    public bool ReplaceExisting { get; set; }

    /// <summary>The button pressed: check, save, add-period, remove-period:N, break-period:N, add-day:N, remove-day:N.</summary>
    public string? Op { get; set; }
}

public sealed class ImportPeriodRow
{
    public string? Label { get; set; }

    /// <summary>24-hour "HH:mm" (the time input's value).</summary>
    public string? Start { get; set; }
    public string? End { get; set; }

    /// <summary>One cell per day of <see cref="TimetableImportForm.Days"/>, in the same order.</summary>
    public List<ImportCellRow> Cells { get; set; } = [];
}

public sealed class ImportCellRow
{
    public DayOfWeek Day { get; set; }
    public ImportCellKind Kind { get; set; } = ImportCellKind.Free;

    /// <summary>"departmentId|Subject" (one of the school's subjects), or empty.</summary>
    public string? Subject { get; set; }
    public int? TeacherId { get; set; }

    /// <summary>The cell as written in the file (shown, never changed).</summary>
    public string? Raw { get; set; }

    /// <summary>The teacher's name as written in the file.</summary>
    public string? TeacherText { get; set; }

    /// <summary>How <see cref="TeacherText"/> was matched (<see cref="TeacherMatch"/>), and for which subject.</summary>
    public string? Match { get; set; }
    public string? MatchedFor { get; set; }

    /// <summary>The teacher the match suggested (to tell an unchanged "check" suggestion from the administrator's own choice).</summary>
    public int? SuggestedId { get; set; }

    /// <summary>Break / activity name ("Break", "Assembly").</summary>
    public string? Label { get; set; }

    /// <summary>Other text from the cell (a room, a group), saved as the lesson's notes.</summary>
    public string? Notes { get; set; }

    public static string SubjectKey(int departmentId, string subject) => $"{departmentId}|{subject}";

    public (int DepartmentId, string Subject)? ParsedSubject =>
        Subject is { } s && s.IndexOf('|') is var i and > 0 && int.TryParse(s[..i], out var d) && s.Length > i + 1 ? (d, s[(i + 1)..]) : null;
}

/// <summary>The school's subjects, grouped by department, for the preview's subject lists.</summary>
public sealed record SubjectGroup(int DepartmentId, string Department, IReadOnlyList<string> Subjects);

/// <summary>The result of checking the preview (and of the dry run of the save): what blocks saving and what needs attention.</summary>
public sealed class ImportCheck
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public Dictionary<string, List<string>> CellErrors { get; } = [];
    public Dictionary<string, List<string>> CellWarnings { get; } = [];
    public Dictionary<int, List<string>> RowErrors { get; } = [];
    public Dictionary<int, List<string>> RowWarnings { get; } = [];

    public static string Key(int row, DayOfWeek day) => $"{row}:{(int)day}";

    public void CellError(int row, DayOfWeek day, string message) => Add(CellErrors, Key(row, day), message);
    public void CellWarning(int row, DayOfWeek day, string message) => Add(CellWarnings, Key(row, day), message);
    public void RowError(int row, string message) => Add(RowErrors, row, message);
    public void RowWarning(int row, string message) => Add(RowWarnings, row, message);

    public IReadOnlyList<string> ErrorsAt(int row, DayOfWeek day) => CellErrors.GetValueOrDefault(Key(row, day)) ?? [];
    public IReadOnlyList<string> WarningsAt(int row, DayOfWeek day) => CellWarnings.GetValueOrDefault(Key(row, day)) ?? [];

    public bool HasErrors => Errors.Count > 0 || CellErrors.Count > 0 || RowErrors.Count > 0;
    public int ErrorCount => Errors.Count + CellErrors.Values.Sum(v => v.Count) + RowErrors.Values.Sum(v => v.Count);
    public int WarningCount => Warnings.Count + CellWarnings.Values.Sum(v => v.Count) + RowWarnings.Values.Sum(v => v.Count);

    /// <summary>The save was tried in a transaction and rolled back (teacher clashes, workload and period rules checked).</summary>
    public bool DryRun { get; set; }

    /// <summary>Current lessons of the class (the timetable an upload would replace).</summary>
    public int ExistingLessons { get; set; }

    public int Days { get; set; }
    public int Periods { get; set; }
    public int SubjectAssignments { get; set; }
    public int TeacherAssignments { get; set; }
    public int Unassigned { get; set; }
    public int UnmatchedTeachers { get; set; }
    public int Breaks { get; set; }

    private static void Add<TKey>(Dictionary<TKey, List<string>> map, TKey key, string message) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(message)) list.Add(message);
    }
}

public sealed class TimetableImportPreview
{
    public required TimetableImportForm Form { get; init; }
    public required ImportCheck Check { get; init; }
    public required string ClassName { get; init; }
    public string? YearName { get; init; }
    public required IReadOnlyList<SubjectGroup> Subjects { get; init; }

    /// <summary>The teachers who may teach each subject ("departmentId|Subject" → id, name).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<(int Id, string Name)>> Teachers { get; init; }

    /// <summary>Every teacher by id (names of teachers suggested by the file even when not eligible).</summary>
    public required IReadOnlyDictionary<int, string> TeacherNames { get; init; }
}

public sealed record UploadClassOption(int Id, string Name, int Lessons);

public sealed class TimetableUploadPage
{
    public required IReadOnlyList<UploadClassOption> Classes { get; init; }
    public required IReadOnlyList<AcademicYearOption> Years { get; init; }
    public int? ClassId { get; set; }
    public int? YearId { get; set; }
    public string? Error { get; set; }
}

/// <summary>A lesson that was not saved because its teacher was left unassigned (kept on the import to assign later).</summary>
public sealed record PendingLesson(DayOfWeek Day, int SlotId, string Period, string Times, string Subject, string Department, string? TeacherText);

public sealed record TimetableImportDetails(IReadOnlyList<PendingLesson> Pending, IReadOnlyList<string> Warnings);

public sealed class TimetableImportSummary
{
    public required int Id { get; init; }
    public required int ClassId { get; init; }
    public required string ClassName { get; init; }
    public string? YearName { get; init; }
    public required string FileName { get; init; }
    public string? Sheet { get; init; }
    public required int Days { get; init; }
    public required int Periods { get; init; }
    public required int SubjectAssignments { get; init; }
    public required int TeacherAssignments { get; init; }
    public required int Unassigned { get; init; }
    public required int Breaks { get; init; }
    public required int Warnings { get; init; }
    public required int ReplacedLessons { get; init; }
    public required DateTime ImportedAt { get; init; }
    public string? ImportedBy { get; init; }
    public required TimetableImportDetails Details { get; init; }
}
