using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Import;

namespace SchoolManagement.Web.ViewModels;

// ---------- Dashboard ----------

public sealed class DashboardViewModel
{
    public required DashboardData Data { get; init; }
    public required SchoolProfile Profile { get; init; }
    public OperationsIndicators? Operations { get; init; }
}

// ---------- Students ----------

public sealed class StudentIndexViewModel
{
    public required StudentListQuery Query { get; init; }
    public required PagedResult<StudentRow> Result { get; init; }
    public required IReadOnlyList<ClassOption> Classes { get; init; }
    public required int TotalStudents { get; init; }
}

public sealed class StudentFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Student full name")]
    [Required(ErrorMessage = "Enter the student's full name.")]
    [StringLength(Student.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    [Remote("CheckDuplicate", "Students", AdditionalFields = "SchoolClassId,Id", ErrorMessage = "A student with this name is already in this class.")]
    public string FullName { get; set; } = "";

    [Display(Name = "Class")]
    [Required(ErrorMessage = "Choose a class.")]
    [Range(1, int.MaxValue, ErrorMessage = "Choose a class.")]
    public int? SchoolClassId { get; set; }

    public IReadOnlyList<ClassOption> ClassOptions { get; set; } = [];
    public bool IsEdit => Id is > 0;

    /// <summary>Set when the form was opened from a class: the class is fixed and no dropdown is shown.</summary>
    public ClassRow? LockedClass { get; set; }
    public bool IsClassLocked => LockedClass is not null && !IsEdit;
}

// ---------- Teachers ----------

public sealed class DepartmentSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Subcategories { get; set; }
    public int MemberCount { get; set; }
}

public sealed class TeacherIndexViewModel
{
    public required TeacherListQuery Query { get; init; }
    public required PagedResult<TeacherRow> Result { get; init; }
    public required IReadOnlyList<JobTitleCount> JobTitles { get; init; }
    public IReadOnlyList<DepartmentSummary> Departments { get; init; } = [];
    public required int TotalTeachers { get; init; }
}

public sealed class TeacherFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Full name")]
    [Required(ErrorMessage = "Enter the staff member's full name.")]
    [StringLength(StaffMember.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    [Remote("CheckDuplicate", "Teachers", AdditionalFields = "ContactNumber,Id", ErrorMessage = "This staff member already exists.")]
    public string FullName { get; set; } = "";

    [Display(Name = "Department")]
    public int? DepartmentId { get; set; }

    /// <summary>Only for departments with subcategories (Religion, Second Language, ICT, WSD); checked by the server.</summary>
    [Display(Name = "Subcategory")]
    [StringLength(50)]
    public string? Subcategory { get; set; }

    /// <summary>Religion → Islamic → Religion / Quran; Christian → Christian Religion.</summary>
    [Display(Name = "Subject")]
    [StringLength(50)]
    public string? Subject { get; set; }

    [Display(Name = "Job title / Position")]
    [StringLength(StaffMember.JobTitleMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? JobTitle { get; set; }

    [Display(Name = "Contact number")]
    [StringLength(StaffMember.ContactMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    [RegularExpression(@"^\s*\+?[0-9٠-٩۰-۹][0-9٠-٩۰-۹ ()\-.]*[0-9٠-٩۰-۹]\s*$",
        ErrorMessage = "Use digits, spaces or dashes, with an optional + at the start.")]
    public string? ContactNumber { get; set; }

    public IReadOnlyList<Department> Departments { get; set; } = [];
    public IReadOnlyList<JobTitleCount> JobTitleSuggestions { get; set; } = [];
    public bool IsEdit => Id is > 0;
}

// ---------- Classes ----------

public sealed class ClassIndexViewModel
{
    public required IReadOnlyList<ClassRow> Classes { get; init; }
    public string? Search { get; init; }
    public string? Status { get; init; }
    public required int TotalClasses { get; init; }
    public required int ActiveClasses { get; init; }
    public required int TotalStudents { get; init; }
    public string View { get; init; } = "grid";
}

public sealed class ClassFormModel
{
    public int? Id { get; set; }

    [Display(Name = "Class name")]
    [StringLength(SchoolClass.NameMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string Name { get; set; } = "";

    [Display(Name = "Grade / Class Level")]
    public string? GradeLevel { get; set; }

    [Display(Name = "Section")]
    public string? Section { get; set; }

    [Display(Name = "Class capacity")]
    [Range(1, 1000, ErrorMessage = "Enter a capacity between 1 and 1,000.")]
    public int Capacity { get; set; } = 25;

    [Display(Name = "Display order")]
    [Range(0, 1_000_000, ErrorMessage = "Enter a number from 0 to 1,000,000.")]
    public int? SortOrder { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    public int StudentCount { get; set; }
    public bool IsEdit => Id is > 0;

    /// <summary>Existing sections per stage (KG / G), offered as suggestions; a new section can be typed.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Sections { get; set; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>Preview of the next class name per stage + existing section, e.g. KG3A.</summary>
    public IReadOnlyList<string> NextNames { get; set; } = [];
}

public sealed class ClassDeleteModel
{
    public int Id { get; set; }
    public int? ReassignToClassId { get; set; }

    public ClassRow? Class { get; set; }
    public IReadOnlyList<ClassOption> Targets { get; set; } = [];
}

// ---------- Import / Export ----------

public sealed class ImportExportViewModel
{
    public required int Students { get; init; }
    public required int Teachers { get; init; }
    public required int ActiveClasses { get; init; }
    public ImportKind Kind { get; init; } = ImportKind.Students;
    public string? Error { get; init; }
}

public sealed class ImportMapViewModel
{
    public required ImportSession Session { get; init; }
    public required IReadOnlyList<ImportField> Fields { get; init; }
    public required IReadOnlyList<string> ColumnLabels { get; init; }
    public required IReadOnlyList<Services.Excel.SheetRow> Sample { get; init; }
    public required int DataRowCount { get; init; }
}

public sealed class ImportPreviewViewModel
{
    public required ImportSession Session { get; init; }
    public required ImportPreview Preview { get; init; }
    public string Filter { get; init; } = "all";
}

public sealed class ImportResultViewModel
{
    public required ImportSession Session { get; init; }
    public required ImportResult Result { get; init; }
}

// ---------- Settings ----------

public sealed class SchoolProfileForm
{
    [Display(Name = "School name")]
    [Required(ErrorMessage = "Enter the school name.")]
    [StringLength(120, ErrorMessage = "Use {1} characters or fewer.")]
    public string SchoolName { get; set; } = "";

    [Display(Name = "Academic year")]
    [StringLength(20, ErrorMessage = "Use {1} characters or fewer.")]
    public string? AcademicYear { get; set; }
}

public sealed class SettingsViewModel
{
    public required SchoolProfileForm Profile { get; init; }
    public required SystemInfo System { get; init; }
    public TimeOnly? ArrivalDeadline { get; init; }
    public int MaxWeeklyPeriods { get; init; } = 24;
    public IReadOnlyList<StaffOption> Staff { get; init; } = [];
    public int? OperatorStaffId { get; init; }
    public bool OperatorVerified { get; init; }
}
