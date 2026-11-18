using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>A staff member's contract, probation, employment type, working days and working hours.</summary>
public sealed class StaffEmploymentInput
{
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    /// <summary>Probation preset: 30, 60 or 90 days; null = no probation period.</summary>
    public int? ProbationDays { get; set; }
    /// <summary>First day of probation; empty = the contract start date.</summary>
    public DateOnly? ProbationStartDate { get; set; }
    public EmploymentType? EmploymentType { get; set; }
    public IReadOnlyCollection<DayOfWeek> WorkingDays { get; set; } = [];
    public WorkingHoursType? WorkingHoursType { get; set; }
    public TimeOnly? WorkStart { get; set; }
    public TimeOnly? WorkEnd { get; set; }
}

public interface IStaffEmploymentService
{
    /// <summary>Checks the input without touching the database (the same rules the form shows).</summary>
    ServiceResult Validate(StaffEmploymentInput input);

    /// <summary>Saves the employment settings of an existing staff member (part-time weekdays replace the earlier ones).</summary>
    Task<ServiceResult> SaveAsync(int staffId, StaffEmploymentInput input, CancellationToken ct = default);
}

/// <summary>
/// Employment configuration of any staff member (teachers and Management Team alike). Nothing here changes timetables or
/// workload. Flexible hours must fall within the school working window (08:00–15:00).
/// </summary>
public sealed class StaffEmploymentService(SchoolDbContext db) : IStaffEmploymentService
{
    public static readonly TimeOnly WindowStart = new(8, 0);
    public static readonly TimeOnly WindowEnd = new(15, 0);
    /// <summary>The probation periods offered, in days.</summary>
    public static readonly IReadOnlyList<int> ProbationPresets = [30, 60, 90];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>Weekdays in the school's order (the week starts on Sunday).</summary>
    public static readonly IReadOnlyList<DayOfWeek> Week =
        [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    public ServiceResult Validate(StaffEmploymentInput x)
    {
        var errors = new List<(string Field, string Message)>();
        if (x.ContractStartDate is null) errors.Add(("ContractStartDate", "Enter the contract start date."));
        if (x.ContractStartDate is { } s && x.ContractEndDate is { } e && e < s)
            errors.Add(("ContractEndDate", "The contract end date cannot be earlier than the start date."));

        if (x.ProbationDays is { } days)
        {
            if (!ProbationPresets.Contains(days)) errors.Add(("ProbationDays", "Choose a probation period of 30, 60 or 90 days."));
            else if (ProbationStart(x) is { } ps)
            {
                var pe = ProbationEnd(ps, days);
                if (x.ContractStartDate is { } cs && ps < cs)
                    errors.Add(("ProbationStartDate", "The probation period cannot start before the contract start date."));
                else if (pe < ps)
                    errors.Add(("ProbationStartDate", "The probation end date cannot be earlier than its start date."));
                else if (x.ContractEndDate is { } ce && pe > ce)
                    errors.Add(("ProbationDays", $"This probation period would end on {pe.ToString("d MMM yyyy", C)}, after the contract end date ({ce.ToString("d MMM yyyy", C)}). Choose a shorter period or a later start."));
            }
        }

        if (x.EmploymentType is null || !Enum.IsDefined(x.EmploymentType.Value)) errors.Add(("EmploymentType", "Choose Full Time or Part Time."));
        else if (x.EmploymentType == Entities.EmploymentType.PartTime && x.WorkingDays.Count == 0)
            errors.Add(("WorkingDays", "Choose at least one working day for a part-time staff member."));
        if (x.WorkingDays.Any(d => !Enum.IsDefined(d))) errors.Add(("WorkingDays", "Choose valid weekdays."));

        if (x.WorkingHoursType is null || !Enum.IsDefined(x.WorkingHoursType.Value)) errors.Add(("WorkingHoursType", "Choose Fixed Hours or Flexible Hours."));
        else
        {
            if (x.WorkStart is null) errors.Add(("WorkStart", "Enter the start time."));
            if (x.WorkEnd is null) errors.Add(("WorkEnd", "Enter the end time."));
            if (x.WorkStart is { } ws && x.WorkEnd is { } we)
            {
                if (we <= ws) errors.Add(("WorkEnd", "The end time must be later than the start time."));
                if (x.WorkingHoursType == Entities.WorkingHoursType.Flexible && (ws < WindowStart || we > WindowEnd || ws > WindowEnd || we < WindowStart))
                    errors.Add(("WorkStart", $"Flexible hours must be within the school working window, {Arrivals.Time(WindowStart)} – {Arrivals.Time(WindowEnd)}."));
            }
        }
        var result = ServiceResult.Ok();
        foreach (var (field, message) in errors) result.AddError(field, message);
        return result;
    }

    /// <summary>The first day of probation: the chosen date, or the contract start date.</summary>
    public static DateOnly? ProbationStart(StaffEmploymentInput x) => x.ProbationDays is null ? null : x.ProbationStartDate ?? x.ContractStartDate;

    /// <summary>The last day of a probation of <paramref name="days"/> days from <paramref name="start"/> (both days counted): 30 days from 1 Sep end on 30 Sep.</summary>
    public static DateOnly ProbationEnd(DateOnly start, int days) => start.AddDays(days - 1);

    public async Task<ServiceResult> SaveAsync(int staffId, StaffEmploymentInput x, CancellationToken ct = default)
    {
        var check = Validate(x);
        if (!check.Succeeded) return check;
        var staff = await db.StaffMembers.Include(s => s.WorkingDays).FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (staff is null) return ServiceResult.Fail("", "This staff member no longer exists.");

        staff.ContractStartDate = x.ContractStartDate;
        staff.ContractEndDate = x.ContractEndDate;
        // The chosen duration and the actual dates are both kept (no probation: all four empty).
        var probationStart = ProbationStart(x);
        staff.ProbationLength = probationStart is null ? null : x.ProbationDays;
        staff.ProbationUnit = probationStart is null ? null : Entities.ProbationUnit.Days;
        staff.ProbationStartDate = probationStart;
        staff.ProbationEndDate = probationStart is { } ps && x.ProbationDays is { } pd ? ProbationEnd(ps, pd) : null;
        staff.EmploymentType = x.EmploymentType;
        staff.WorkingHoursType = x.WorkingHoursType;
        staff.WorkStart = x.WorkStart;
        staff.WorkEnd = x.WorkEnd;
        // Full-time staff keep no weekday list (the school week applies); part-time staff keep exactly the chosen days.
        var days = x.EmploymentType == Entities.EmploymentType.PartTime ? x.WorkingDays.Distinct().ToHashSet() : [];
        foreach (var d in staff.WorkingDays.Where(d => !days.Contains(d.Day)).ToList()) staff.WorkingDays.Remove(d);
        foreach (var d in days.Where(d => staff.WorkingDays.All(w => w.Day != d))) staff.WorkingDays.Add(new StaffWorkingDay { Day = d });
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(staff.Id);
    }

    public static string Describe(TimeOnly? start, TimeOnly? end) =>
        start is { } s && end is { } e ? $"{Arrivals.Time(s)} – {Arrivals.Time(e)}" : "—";

    public static string DayName(DayOfWeek d) => C.DateTimeFormat.GetDayName(d);
}
