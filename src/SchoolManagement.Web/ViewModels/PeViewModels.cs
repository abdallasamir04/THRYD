using System.ComponentModel.DataAnnotations;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.ViewModels;

public sealed class PePageViewModel
{
    public required DateOnly Date { get; init; }
    public required DateOnly Week { get; init; }
    public required IReadOnlyList<SlotView> Slots { get; init; }
    public required IReadOnlyList<PeChip> Sessions { get; init; }
    public int? DepartmentId { get; init; }
    public int PeTeachers { get; init; }
    public bool Editable => Week >= Common.SchoolWeek.Current;

    /// <summary>
    /// The P.E. timetable for the week of <paramref name="date"/>. Used by the P.E. page (/pe, opened from
    /// Duties) and by the PE department page, so both show the same data.
    /// </summary>
    public static async Task<PePageViewModel> LoadAsync(DateOnly date, IPeService pe, ITimetableService timetable, ITeacherService teachers, CancellationToken ct)
    {
        var week = Common.SchoolWeek.StartOf(date);
        var dept = await pe.DepartmentIdAsync(ct);
        return new PePageViewModel
        {
            Date = date, Week = week, DepartmentId = dept,
            Slots = (await timetable.SlotsAsync(ct)).Where(s => s.IsActive).ToList(),
            Sessions = await pe.WeekAsync(week, ct),
            PeTeachers = dept is null ? 0 : (await teachers.ListAllAsync(new TeacherListQuery { DepartmentId = dept }, ct)).Count,
        };
    }
}

/// <summary>New P.E. session from an empty cell: day and period come from the cell.</summary>
public sealed class PeNewModel
{
    public DayOfWeek Day { get; set; }
    public int SlotId { get; set; }
    public string? Date { get; set; }

    [Display(Name = "Class")]
    [Required(ErrorMessage = "Choose the class.")]
    public int? ClassId { get; set; }

    [Display(Name = "Teacher")]
    [Required(ErrorMessage = "Choose the P.E. teacher.")]
    public int? TeacherId { get; set; }

    public SlotView? Slot { get; set; }
    public IReadOnlyList<(int Id, string Name, string Grade)> Classes { get; set; } = [];
    public IReadOnlyList<TeacherCandidate> Teachers { get; set; } = [];
}

public sealed class PeEquipmentField
{
    public string? Name { get; set; }
    public bool Checked { get; set; }
}

/// <summary>Session details form: co-teacher, facility, activity, equipment checklist, notes.</summary>
public sealed class PeDetailsModel
{
    public int SessionId { get; set; }
    public string? Date { get; set; }

    [Display(Name = "Co-teacher")]
    public int? CoTeacherId { get; set; }

    [Display(Name = "Facility")]
    public PeFacility? Facility { get; set; }

    [Display(Name = "Curriculum focus / activity")]
    [StringLength(PeSessionDetail.ActivityMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Activity { get; set; }

    [Display(Name = "Notes / quality feedback")]
    [StringLength(PeSessionDetail.NotesMaxLength, ErrorMessage = "Use {1} characters or fewer.")]
    public string? Notes { get; set; }

    /// <summary>Bound with Equipment.Index keys, so rows can be added and removed in any order.</summary>
    public Dictionary<string, PeEquipmentField> Equipment { get; set; } = [];

    public PeSessionView? View { get; set; }
    public IReadOnlyList<(int Id, string Name, string? Note)> CoTeachers { get; set; } = [];

    public PeDetailsInput ToInput() => new(CoTeacherId, Facility, Activity, Notes,
        Equipment.Values.Select(e => new PeEquipmentInput(e.Name, e.Checked)).ToList());
}
