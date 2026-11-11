namespace SchoolManagement.Web.Entities;

/// <summary>Where a P.E. session takes place. Numbers are stored: add new values, never reuse one.</summary>
public enum PeFacility
{
    KgGarden = 1,
    SwimmingPool = 2,
    MainGarden = 3,
}

/// <summary>
/// P.E. details of a timetable session of the P.E. department (the session itself — class, day,
/// slot, teacher — is the normal TimetableAssignment, so it shows in the class and teacher
/// timetables and counts in workload). One row per session, created when details are first saved.
/// </summary>
public class PeSessionDetail : IAuditable
{
    public const int ActivityMaxLength = 300;
    public const int NotesMaxLength = 2000;

    public int TimetableAssignmentId { get; set; }
    public TimetableAssignment Session { get; set; } = null!;

    /// <summary>Optional second academic teacher for the session.</summary>
    public int? CoTeacherId { get; set; }
    public Teacher? CoTeacher { get; set; }

    public PeFacility? Facility { get; set; }
    public string? Activity { get; set; }
    public string? Notes { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PeEquipmentItem> Equipment { get; set; } = new List<PeEquipmentItem>();
}

/// <summary>One line of a session's equipment checklist.</summary>
public class PeEquipmentItem
{
    public const int NameMaxLength = 100;

    public int Id { get; set; }
    public int TimetableAssignmentId { get; set; }
    public PeSessionDetail Detail { get; set; } = null!;
    public string Name { get; set; } = "";
    public bool IsChecked { get; set; }
    public int SortOrder { get; set; }
}
