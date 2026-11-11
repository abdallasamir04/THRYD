using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services.Excel;

namespace SchoolManagement.Web.Services.Import;

public enum ImportKind
{
    Students,
    Teachers,
}

public sealed record ImportField(string Key, string Label, bool Required, string Hint, IReadOnlyList<string> Synonyms)
{
    private IReadOnlyList<string>? _keys;
    public IReadOnlyList<string> SynonymKeys => _keys ??= Synonyms.Select(TextNormalizer.HeaderKey).Distinct().ToList();
}

/// <summary>The fields each import fills, and the header names (English and Arabic) recognised for each.</summary>
public static class ImportDefinitions
{
    public const string FullName = "FullName";
    public const string Class = "Class";
    public const string JobTitle = "JobTitle";
    public const string ContactNumber = "ContactNumber";

    public static readonly IReadOnlyList<ImportField> StudentFields =
    [
        new(FullName, "Student Name", true, "The student's full name",
            ["Student Name", "Student Full Name", "Full Name", "Name", "Student", "Pupil", "Pupil Name", "Learner",
             "الاسم", "اسم الطالب", "اسم الطالبة", "الطالب", "الاسم الكامل", "اسم الطالب رباعي"]),
        new(Class, "Class", true, "Must match a class name, e.g. G3A or KG1B",
            ["Class", "Class Name", "Grade", "Grade/Class", "Section", "Homeroom", "Form", "Grade Section",
             "الفصل", "الصف", "الشعبة", "الصف والشعبة", "اسم الفصل"]),
    ];

    public static readonly IReadOnlyList<ImportField> TeacherFields =
    [
        new(FullName, "Teacher Name", true, "The teacher's full name",
            ["Teacher Name", "Teacher Full Name", "Full Name", "Name", "Teacher", "Employee Name", "Staff Name", "Employee",
             "الاسم", "اسم المعلم", "اسم المعلمة", "المعلم", "المعلمة", "اسم الموظف", "الاسم الكامل"]),
        new(JobTitle, "Job Title", false, "Position, e.g. Math Teacher",
            ["Job Title", "Title", "Position", "Job", "Role", "Designation", "Job Position",
             "الوظيفة", "المسمى الوظيفي", "المسمى", "الوظيفه"]),
        new(ContactNumber, "Contact Number", false, "Phone number, kept exactly as written",
            ["Contact Number", "Contact", "Phone", "Phone Number", "Mobile", "Mobile Number", "Tel", "Telephone", "WhatsApp", "Cell",
             "رقم التواصل", "الهاتف", "رقم الهاتف", "الجوال", "رقم الجوال", "الموبايل", "رقم الموبايل", "التليفون", "تليفون", "واتساب"]),
    ];

    public static IReadOnlyList<ImportField> Fields(ImportKind kind) => kind == ImportKind.Students ? StudentFields : TeacherFields;

    public static string Title(ImportKind kind) => kind == ImportKind.Students ? "Students" : "Teachers";
}

/// <summary>Uploaded file and the user's choices, kept in server memory between the import steps.</summary>
public sealed class ImportSession
{
    public required Guid Token { get; init; }
    public required ImportKind Kind { get; init; }
    public required string FileName { get; init; }
    public required IReadOnlyList<SheetGrid> Sheets { get; init; }
    public int SheetIndex { get; set; }

    /// <summary>Index into the sheet's row list of the header row; -1 = the file has no header row.</summary>
    public int HeaderIndex { get; set; } = -1;

    /// <summary>Field key → zero-based column index (null = not imported).</summary>
    public Dictionary<string, int?> Mapping { get; set; } = [];

    public bool MappingConfirmed { get; set; }
    public ImportResult? Result { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Lets only one confirm run at a time for this upload.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public SheetGrid Sheet => Sheets[Math.Clamp(SheetIndex, 0, Sheets.Count - 1)];
    public IEnumerable<SheetRow> DataRows => Sheet.Rows.Skip(HeaderIndex + 1);
}

public enum RowStatus
{
    Ready,
    Duplicate,
    Invalid,
}

public sealed class PreviewRow
{
    public required int RowNumber { get; init; }
    public required Dictionary<string, string> Values { get; init; }
    public RowStatus Status { get; set; } = RowStatus.Ready;
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>Students: the matched class.</summary>
    public int? ClassId { get; set; }
    public string? ClassName { get; set; }

    public string Value(string key) => Values.GetValueOrDefault(key) ?? "";
}

public sealed class ImportPreview
{
    public required ImportKind Kind { get; init; }
    public required IReadOnlyList<PreviewRow> Rows { get; init; }
    public int Ready => Rows.Count(r => r.Status == RowStatus.Ready);
    public int Duplicates => Rows.Count(r => r.Status == RowStatus.Duplicate);
    public int Invalid => Rows.Count(r => r.Status == RowStatus.Invalid);
    public int WithWarnings => Rows.Count(r => r.Status == RowStatus.Ready && r.Warnings.Count > 0);
}

public sealed class ImportResult
{
    public required int Imported { get; init; }
    public required int SkippedDuplicates { get; init; }
    public required int Failed { get; init; }
    public required int TotalRows { get; init; }
    public required IReadOnlyList<PreviewRow> NotImported { get; init; }
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}
