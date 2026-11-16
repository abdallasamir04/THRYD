using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>The icons and colours an activity can use (the app's icon set and THRYD tones).</summary>
public static class ActivityStyles
{
    public static readonly string[] Icons =
        ["sparkles", "activity", "flag", "users", "presentation", "school", "calendar", "graduation-cap", "layers", "monitor", "sun", "briefcase", "crown", "clipboard-check", "chart", "list"];

    public static readonly string[] Colors = ["violet", "blue", "teal", "green", "amber", "rose", "indigo", "cyan"];

    /// <summary>Suggestions for the category field (any text is allowed).</summary>
    public static readonly string[] Categories = ["Sports", "Arts", "Music", "Clubs", "Events", "Academic", "Community"];

    public const string DefaultIcon = "sparkles";
    public const string DefaultColor = "violet";

    /// <summary>P.E. is the existing P.E. module (built-in card): no second activity may take its name.</summary>
    public static bool IsPeName(string name)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return letters is "PE" or "PHYSICALEDUCATION";
    }
}

public sealed record ActivityInput(
    string? Name, string? Description, string? Category, string? Icon, string? Color, int? DepartmentId, string? Location, bool IsActive,
    IReadOnlyList<int>? StaffIds = null, IReadOnlyList<int>? ClassIds = null);

public sealed record ActivityScheduleInput(DayOfWeek? Day, TimeOnly? Start, TimeOnly? End, string? Location, string? Notes);

public sealed record ActivityCardView(int Id, string Name, string Description, string? Category, string Icon, string Color, bool IsActive, int Staff, int Classes, int Meetings);

public sealed record ActivityScheduleView(int Id, DayOfWeek Day, TimeOnly Start, TimeOnly End, string? Location, string? Notes)
{
    public string Times => ScheduleRules.Times(Start, End);
}

public sealed record ActivityHistoryView(DateTime At, string Action, string? Details, string? By);

/// <summary>
/// One activity session scheduled on a date with its stored coverage (Pending when no record exists).
/// <paramref name="RefId"/> is the TimetableAssignment id (P.E. lesson) or the ActivitySchedule id (activity meeting).
/// </summary>
public sealed record ActivitySessionView(
    ActivitySessionKind Kind, int RefId, int? ActivityId, string Title, string Detail, TimeOnly? Start, TimeOnly? End, string? Location,
    bool Started, ActivitySessionStatus Status, string? UpdatedBy, DateTime? UpdatedAt)
{
    public string Times => ScheduleRules.Times(Start, End);
}

/// <summary>What is scheduled on one date: P.E. lessons that take place and meetings of active activities, each with its coverage.</summary>
public sealed record ActivityCoverageDay(DateOnly Date, IReadOnlyList<ActivitySessionView> Sessions)
{
    public int PeLessons => Sessions.Count(s => s.Kind == ActivitySessionKind.PeLesson);
    public int Meetings => Sessions.Count(s => s.Kind == ActivitySessionKind.ActivityMeeting);

    // Session counts (the Activities page's own list).
    public int Total => Sessions.Count;
    public int Covered => Sessions.Count(s => s.Status == ActivitySessionStatus.Covered);
    public int Pending => Sessions.Count(s => s.Status == ActivitySessionStatus.Pending);
    public int NotCovered => Sessions.Count(s => s.Status == ActivitySessionStatus.NotCovered);

    /// <summary>
    /// Dashboard sections: each activity scheduled that date counts once — P.E. (all its lessons together) and each
    /// active created activity with a meeting that day — however many sessions or staff it has. An activity is fully
    /// covered only when every one of its sessions that date is Covered.
    /// </summary>
    public SectionCoverage Sections => SectionCoverage.Of(Sessions
        .GroupBy(s => s.Kind == ActivitySessionKind.PeLesson ? (int?)null : s.ActivityId)
        .Select(g => SectionStates.Of(g.Count(), g.Count(s => s.Status == ActivitySessionStatus.Covered), g.Count(s => s.Status == ActivitySessionStatus.NotCovered))));
}

public static class ActivitySessionStatuses
{
    public static readonly ActivitySessionStatus[] All = [ActivitySessionStatus.Pending, ActivitySessionStatus.Covered, ActivitySessionStatus.NotCovered];

    public static string Label(ActivitySessionStatus s) => s switch
    {
        ActivitySessionStatus.Covered => "Covered",
        ActivitySessionStatus.NotCovered => "Not Covered",
        _ => "Pending",
    };

    public static string Badge(ActivitySessionStatus s) => s switch
    {
        ActivitySessionStatus.Covered => "badge--success",
        ActivitySessionStatus.NotCovered => "badge--error",
        _ => "badge--neutral",
    };
}

public sealed class ActivityDetails
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? Category { get; init; }
    public required string Icon { get; init; }
    public required string Color { get; init; }
    public int? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public string? Location { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public IReadOnlyList<(int Id, string Name, string? JobTitle)> Staff { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> Classes { get; init; } = [];
    public IReadOnlyList<ActivityScheduleView> Schedule { get; init; } = [];
    public IReadOnlyList<ActivityHistoryView> History { get; init; } = [];

    /// <summary>Deleting is safe only for an activity nothing is attached to (no staff, classes or meetings).</summary>
    public bool CanDelete => Staff.Count == 0 && Classes.Count == 0 && Schedule.Count == 0;
}

public interface IActivityService
{
    /// <summary>The activities for the Activities page (active ones, or all), loaded from the database.</summary>
    Task<IReadOnlyList<ActivityCardView>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<int> InactiveCountAsync(CancellationToken ct = default);
    Task<ActivityDetails?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>Categories already used, plus the suggestions.</summary>
    Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default);

    Task<ServiceResult> CreateAsync(ActivityInput input, CancellationToken ct = default);
    Task<ServiceResult> UpdateAsync(int id, ActivityInput input, CancellationToken ct = default);
    Task<ServiceResult> SetActiveAsync(int id, bool active, CancellationToken ct = default);

    /// <summary>Removes an activity only when nothing is attached to it; otherwise suggests deactivating it.</summary>
    Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Activity sessions scheduled on a date: P.E. lessons as the timetable holds them for that day (no holidays, only
    /// periods that run that day, cancelled lessons excluded) plus the weekly meetings of ACTIVE activities on that
    /// weekday, each with its coverage record for that date (Pending when there is none). Inactive activities,
    /// cancelled lessons and sessions of other dates are not included.
    /// </summary>
    Task<ActivityCoverageDay> SessionsOnAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>Records the coverage of one session scheduled on <paramref name="date"/> (only once it has started).</summary>
    Task<ServiceResult> SetSessionStatusAsync(ActivitySessionKind kind, int refId, DateOnly date, ActivitySessionStatus status, CancellationToken ct = default);

    Task<ServiceResult> AddScheduleAsync(int id, ActivityScheduleInput input, CancellationToken ct = default);
    Task<ServiceResult> RemoveScheduleAsync(int id, int scheduleId, CancellationToken ct = default);
}

/// <summary>
/// Duties → Activities: administrator-created activities with their staff, classes, weekly schedule and history.
/// Every rule is checked here (the forms only help). P.E. is not managed here — it is the existing P.E. module.
/// </summary>
public sealed class ActivityService(SchoolDbContext db, ICurrentUser currentUser, ISchoolTimeService? schoolTime = null) : IActivityService
{
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    public async Task<IReadOnlyList<ActivityCardView>> ListAsync(bool includeInactive, CancellationToken ct = default) =>
        await db.Activities.AsNoTracking().Where(a => includeInactive || a.IsActive)
            .OrderByDescending(a => a.IsActive).ThenBy(a => a.Name)
            .Select(a => new ActivityCardView(a.Id, a.Name, a.Description, a.Category, a.Icon, a.Color, a.IsActive, a.Staff.Count, a.Classes.Count, a.Schedule.Count))
            .ToListAsync(ct);

    public Task<int> InactiveCountAsync(CancellationToken ct = default) => db.Activities.CountAsync(a => !a.IsActive, ct);

    public async Task<ActivityDetails?> GetAsync(int id, CancellationToken ct = default)
    {
        var a = await db.Activities.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.Name, x.Description, x.Category, x.Icon, x.Color, x.DepartmentId, Department = x.Department != null ? x.Department.Name : null,
            x.Location, x.IsActive, x.CreatedAt, x.CreatedBy, x.UpdatedAt, x.UpdatedBy,
        }).FirstOrDefaultAsync(ct);
        if (a is null) return null;
        var staff = await db.ActivityStaff.AsNoTracking().Where(s => s.SchoolActivityId == id).OrderBy(s => s.StaffMember.FullName)
            .Select(s => new { s.StaffMemberId, s.StaffMember.FullName, s.StaffMember.JobTitle }).ToListAsync(ct);
        var classes = await db.ActivityClasses.AsNoTracking().Where(c => c.SchoolActivityId == id).OrderBy(c => c.SchoolClass.SortOrder).ThenBy(c => c.SchoolClass.Name)
            .Select(c => new { c.SchoolClassId, c.SchoolClass.Name }).ToListAsync(ct);
        var schedule = await db.ActivitySchedule.AsNoTracking().Where(s => s.SchoolActivityId == id).OrderBy(s => s.Day).ThenBy(s => s.StartTime)
            .Select(s => new ActivityScheduleView(s.Id, s.Day, s.StartTime, s.EndTime, s.Location, s.Notes)).ToListAsync(ct);
        var history = await db.ActivityHistory.AsNoTracking().Where(h => h.SchoolActivityId == id).OrderByDescending(h => h.At).ThenByDescending(h => h.Id).Take(100)
            .Select(h => new ActivityHistoryView(h.At, h.Action, h.Details, h.By)).ToListAsync(ct);
        return new ActivityDetails
        {
            Id = a.Id, Name = a.Name, Description = a.Description, Category = a.Category, Icon = a.Icon, Color = a.Color,
            DepartmentId = a.DepartmentId, DepartmentName = a.Department, Location = a.Location, IsActive = a.IsActive,
            CreatedAt = a.CreatedAt, CreatedBy = a.CreatedBy, UpdatedAt = a.UpdatedAt, UpdatedBy = a.UpdatedBy,
            Staff = staff.Select(s => (s.StaffMemberId, s.FullName, s.JobTitle)).ToList(),
            Classes = classes.Select(c => (c.SchoolClassId, c.Name)).ToList(),
            Schedule = schedule, History = history,
        };
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default)
    {
        var used = await db.Activities.AsNoTracking().Where(a => a.Category != null).Select(a => a.Category!).Distinct().ToListAsync(ct);
        return used.Concat(ActivityStyles.Categories).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
    }

    // ------------------------------------------------------------------ create / edit

    public async Task<ServiceResult> CreateAsync(ActivityInput input, CancellationToken ct = default)
    {
        var (result, clean) = await ValidateAsync(null, input, ct);
        if (!result.Succeeded) return result;
        var activity = new SchoolActivity { CreatedBy = currentUser.Name };
        Apply(activity, clean, input);
        foreach (var s in clean.Staff) activity.Staff.Add(new SchoolActivityStaff { StaffMemberId = s });
        foreach (var c in clean.Classes) activity.Classes.Add(new SchoolActivityClass { SchoolClassId = c });
        db.Activities.Add(activity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("Name", $"An activity called “{clean.Name}” already exists.");
        }
        await LogAsync(activity.Id, "Created", $"{clean.Name}{(clean.Staff.Count + clean.Classes.Count == 0 ? "" : $" · {Ui.Plural(clean.Staff.Count, "staff member", "staff members")}, {Ui.Plural(clean.Classes.Count, "class", "classes")}")}", ct);
        return ServiceResult.Ok(activity.Id);
    }

    public async Task<ServiceResult> UpdateAsync(int id, ActivityInput input, CancellationToken ct = default)
    {
        var activity = await db.Activities.Include(a => a.Staff).Include(a => a.Classes).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (activity is null) return ServiceResult.Fail("", "This activity no longer exists.");
        var (result, clean) = await ValidateAsync(id, input, ct);
        if (!result.Succeeded) return result;

        var changes = new List<string>();
        void Changed(string label, string? before, string? after) { if (!string.Equals(before, after, StringComparison.Ordinal)) changes.Add($"{label}: {before ?? "—"} → {after ?? "—"}"); }
        Changed("Name", activity.Name, clean.Name);
        Changed("Category", activity.Category, clean.Category);
        Changed("Location", activity.Location, clean.Location);
        Changed("Icon", activity.Icon, clean.Icon);
        Changed("Colour", activity.Color, clean.Color);
        if (activity.Description != clean.Description) changes.Add("Description updated");
        if (activity.DepartmentId != input.DepartmentId) changes.Add("Department changed");
        if (activity.IsActive != input.IsActive) changes.Add(input.IsActive ? "Activated" : "Deactivated");

        var staffBefore = activity.Staff.Select(s => s.StaffMemberId).ToHashSet();
        var classesBefore = activity.Classes.Select(c => c.SchoolClassId).ToHashSet();
        if (!staffBefore.SetEquals(clean.Staff)) changes.Add($"Staff: {staffBefore.Count} → {clean.Staff.Count}");
        if (!classesBefore.SetEquals(clean.Classes)) changes.Add($"Classes: {classesBefore.Count} → {clean.Classes.Count}");
        if (changes.Count == 0) return ServiceResult.Fail("", "Nothing was changed.");

        Apply(activity, clean, input);
        foreach (var s in activity.Staff.Where(s => !clean.Staff.Contains(s.StaffMemberId)).ToList()) activity.Staff.Remove(s);
        foreach (var s in clean.Staff.Where(s => !staffBefore.Contains(s))) activity.Staff.Add(new SchoolActivityStaff { StaffMemberId = s });
        foreach (var c in activity.Classes.Where(c => !clean.Classes.Contains(c.SchoolClassId)).ToList()) activity.Classes.Remove(c);
        foreach (var c in clean.Classes.Where(c => !classesBefore.Contains(c))) activity.Classes.Add(new SchoolActivityClass { SchoolClassId = c });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("Name", $"An activity called “{clean.Name}” already exists.");
        }
        await LogAsync(id, "Edited", string.Join(" · ", changes), ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> SetActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        var activity = await db.Activities.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (activity is null) return ServiceResult.Fail("", "This activity no longer exists.");
        if (activity.IsActive == active) return ServiceResult.Ok(id);
        (activity.IsActive, activity.UpdatedBy) = (active, currentUser.Name);
        await db.SaveChangesAsync(ct);
        await LogAsync(id, active ? "Activated" : "Deactivated", active ? null : "Kept with its staff, classes, schedule and history.", ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        var details = await GetAsync(id, ct);
        if (details is null) return ServiceResult.Fail("", "This activity no longer exists.");
        if (!details.CanDelete)
        {
            var attached = new List<string>();
            if (details.Staff.Count > 0) attached.Add(Ui.Plural(details.Staff.Count, "staff member", "staff members"));
            if (details.Classes.Count > 0) attached.Add(Ui.Plural(details.Classes.Count, "class", "classes"));
            if (details.Schedule.Count > 0) attached.Add(Ui.Plural(details.Schedule.Count, "scheduled meeting", "scheduled meetings"));
            return ServiceResult.Fail("", $"{details.Name} cannot be deleted: it has {string.Join(", ", attached)}. Deactivate it instead — everything is kept and it can be activated again.");
        }
        // Nothing is attached: the activity and its own history are removed together.
        await db.ActivityHistory.Where(h => h.SchoolActivityId == id).ExecuteDeleteAsync(ct);
        await db.Activities.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ActivityCoverageDay> SessionsOnAsync(DateOnly date, CancellationToken ct = default)
    {
        var days = await SchoolDays.LoadAsync(db, ct);
        if (!days.IsSchoolDay(date)) return new(date, []);

        // P.E. lessons: the P.E. department's timetable sessions that take place that date (Lessons is shared with
        // session attendance, so both agree on what was scheduled or cancelled).
        var pe = await db.Departments.AsNoTracking().Where(d => d.NormalizedName == "PE").Select(d => d.Name).FirstOrDefaultAsync(ct);
        var lessons = pe is null ? [] : (await Lessons.OnAsync(db, date, days, null, ct)).Where(l => l.DepartmentName == pe && !l.Cancelled).ToList();

        // Meetings of active activities on that weekday.
        var meetings = await db.ActivitySchedule.AsNoTracking().Where(s => s.Day == date.DayOfWeek && s.Activity.IsActive)
            .Select(s => new { s.Id, s.SchoolActivityId, s.Activity.Name, s.StartTime, s.EndTime, Location = s.Location ?? s.Activity.Location })
            .ToListAsync(ct);

        var lessonIds = lessons.Select(l => l.SessionId).ToList();
        var meetingIds = meetings.Select(m => m.Id).ToList();
        var marks = await db.ActivitySessionCoverage.AsNoTracking()
            .Where(c => c.SessionDate == date
                && ((c.TimetableAssignmentId != null && lessonIds.Contains(c.TimetableAssignmentId.Value))
                    || (c.ActivityScheduleId != null && meetingIds.Contains(c.ActivityScheduleId.Value))))
            .Select(c => new { c.Kind, Ref = c.TimetableAssignmentId ?? c.ActivityScheduleId!.Value, c.Status, c.UpdatedBy, c.UpdatedAt })
            .ToListAsync(ct);
        var byKey = marks.ToDictionary(m => (m.Kind, m.Ref));

        var today = _time.Today;
        var now = _time.TimeOfDay;
        bool StartedAt(TimeOnly? start) => date < today || (date == today && (start is null || start <= now));

        var sessions = lessons.Select(l =>
            {
                byKey.TryGetValue((ActivitySessionKind.PeLesson, l.SessionId), out var m);
                return new ActivitySessionView(ActivitySessionKind.PeLesson, l.SessionId, null, $"P.E. · {l.ClassName}", $"{l.Period} · {l.Teacher}",
                    l.Start, l.End, null, StartedAt(l.Start), m?.Status ?? ActivitySessionStatus.Pending, m?.UpdatedBy, m?.UpdatedAt);
            })
            .Concat(meetings.Select(s =>
            {
                byKey.TryGetValue((ActivitySessionKind.ActivityMeeting, s.Id), out var m);
                return new ActivitySessionView(ActivitySessionKind.ActivityMeeting, s.Id, s.SchoolActivityId, s.Name, "Activity meeting",
                    s.StartTime, s.EndTime, s.Location, StartedAt(s.StartTime), m?.Status ?? ActivitySessionStatus.Pending, m?.UpdatedBy, m?.UpdatedAt);
            }))
            .OrderBy(s => s.Start ?? TimeOnly.MinValue).ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new(date, sessions);
    }

    public async Task<ServiceResult> SetSessionStatusAsync(ActivitySessionKind kind, int refId, DateOnly date, ActivitySessionStatus status, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind)) return ServiceResult.Fail("", "Unknown session.");
        if (!Enum.IsDefined(status)) return ServiceResult.Fail("Status", "Choose Pending, Covered or Not Covered.");
        // Only a session actually scheduled on that date (not cancelled, activity active, school day) can be recorded.
        var session = (await SessionsOnAsync(date, ct)).Sessions.FirstOrDefault(s => s.Kind == kind && s.RefId == refId);
        if (session is null)
            return ServiceResult.Fail("", $"This session is not scheduled on {date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} (cancelled, inactive, removed or not a school day).");
        if (status != ActivitySessionStatus.Pending && !session.Started)
            return ServiceResult.Fail("Status", "This session has not started yet: coverage can be recorded once it starts.");
        if (session.Status == status) return ServiceResult.Ok(refId);

        var row = await db.ActivitySessionCoverage.FirstOrDefaultAsync(c => c.SessionDate == date
            && (kind == ActivitySessionKind.PeLesson ? c.TimetableAssignmentId == refId : c.ActivityScheduleId == refId), ct);
        if (row is null)
        {
            row = new ActivitySessionCoverage
            {
                Kind = kind, SessionDate = date,
                TimetableAssignmentId = kind == ActivitySessionKind.PeLesson ? refId : null,
                ActivityScheduleId = kind == ActivitySessionKind.ActivityMeeting ? refId : null,
            };
            db.ActivitySessionCoverage.Add(row);
        }
        (row.Status, row.UpdatedBy) = (status, currentUser.Name);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ServiceResult.Fail("", "Someone else recorded this session at the same time. Reload and try again.");
        }
        if (session.ActivityId is { } activityId)
            await LogAsync(activityId, "Coverage recorded",
                $"{ScheduleRules.DayName(date.DayOfWeek)} {date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} {session.Times}: {ActivitySessionStatuses.Label(session.Status)} →{ActivitySessionStatuses.Label(status)}", ct);
        return ServiceResult.Ok(refId);
    }

    // ------------------------------------------------------------------ schedule

    public async Task<ServiceResult> AddScheduleAsync(int id, ActivityScheduleInput input, CancellationToken ct = default)
    {
        var activity = await db.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (activity is null) return ServiceResult.Fail("", "This activity no longer exists.");
        var r = ServiceResult.Ok();
        if (input.Day is not { } day || !ScheduleRules.IsSchoolDay(day)) r.AddError("Day", $"Choose a school day ({string.Join(", ", ScheduleRules.Days.Select(ScheduleRules.DayShort))}).");
        if (input.Start is null) r.AddError("Start", "Enter the start time.");
        if (input.End is null) r.AddError("End", "Enter the end time.");
        if (input.Start is { } s && input.End is { } e && e <= s) r.AddError("End", "The end time must be later than the start time.");
        var location = TextNormalizer.CleanText(input.Location);
        var notes = TextNormalizer.CleanText(input.Notes);
        if (location.Length > SchoolActivity.LocationMaxLength) r.AddError("Location", $"Use {SchoolActivity.LocationMaxLength} characters or fewer.");
        if (notes.Length > SchoolActivitySchedule.NotesMaxLength) r.AddError("Notes", $"Use {SchoolActivitySchedule.NotesMaxLength} characters or fewer.");
        if (!r.Succeeded) return r;

        var clash = await db.ActivitySchedule.AsNoTracking()
            .Where(x => x.SchoolActivityId == id && x.Day == input.Day && x.StartTime < input.End && input.Start < x.EndTime)
            .Select(x => new { x.StartTime, x.EndTime }).FirstOrDefaultAsync(ct);
        if (clash is not null)
            return ServiceResult.Fail("Start", $"{activity.Name} already meets on {ScheduleRules.DayName(input.Day!.Value)} {ScheduleRules.Times(clash.StartTime, clash.EndTime)}, which overlaps these times.");

        var row = new SchoolActivitySchedule
        {
            SchoolActivityId = id, Day = input.Day!.Value, StartTime = input.Start!.Value, EndTime = input.End!.Value,
            Location = location.Length == 0 ? null : location, Notes = notes.Length == 0 ? null : notes,
        };
        db.ActivitySchedule.Add(row);
        await db.SaveChangesAsync(ct);
        await LogAsync(id, "Schedule added", $"{ScheduleRules.DayName(row.Day)} {ScheduleRules.Times(row.StartTime, row.EndTime)}{(row.Location is null ? "" : " · " + row.Location)}", ct);
        return ServiceResult.Ok(row.Id);
    }

    public async Task<ServiceResult> RemoveScheduleAsync(int id, int scheduleId, CancellationToken ct = default)
    {
        var row = await db.ActivitySchedule.FirstOrDefaultAsync(x => x.Id == scheduleId && x.SchoolActivityId == id, ct);
        if (row is null) return ServiceResult.Fail("", "This meeting is no longer on the schedule.");
        db.ActivitySchedule.Remove(row);
        await db.SaveChangesAsync(ct);
        await LogAsync(id, "Schedule removed", $"{ScheduleRules.DayName(row.Day)} {ScheduleRules.Times(row.StartTime, row.EndTime)}", ct);
        return ServiceResult.Ok(id);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Clean(string Name, string Key, string Description, string? Category, string Icon, string Color, string? Location, HashSet<int> Staff, HashSet<int> Classes);

    private async Task<(ServiceResult, Clean)> ValidateAsync(int? id, ActivityInput i, CancellationToken ct)
    {
        var r = ServiceResult.Ok();
        var name = TextNormalizer.CleanText(i.Name);
        var description = TextNormalizer.CleanText(i.Description);
        var category = TextNormalizer.CleanText(i.Category);
        var location = TextNormalizer.CleanText(i.Location);
        var icon = string.IsNullOrWhiteSpace(i.Icon) ? ActivityStyles.DefaultIcon : i.Icon.Trim();
        var color = string.IsNullOrWhiteSpace(i.Color) ? ActivityStyles.DefaultColor : i.Color.Trim();
        var key = TextNormalizer.NameKey(name);

        if (name.Length == 0) r.AddError("Name", "Enter the activity name.");
        else if (name.Length > SchoolActivity.NameMaxLength) r.AddError("Name", $"Use {SchoolActivity.NameMaxLength} characters or fewer.");
        else if (!name.Any(char.IsLetter)) r.AddError("Name", "The name must contain letters.");
        else if (ActivityStyles.IsPeName(name)) r.AddError("Name", "P.E. is already an activity (the P.E. module). Choose another name.");
        else if (await db.Activities.AnyAsync(a => a.NormalizedName == key && a.Id != (id ?? 0), ct)) r.AddError("Name", $"An activity called “{name}” already exists.");
        if (description.Length == 0) r.AddError("Description", "Enter a short description.");
        else if (description.Length > SchoolActivity.DescriptionMaxLength) r.AddError("Description", $"Use {SchoolActivity.DescriptionMaxLength} characters or fewer.");
        if (category.Length > SchoolActivity.CategoryMaxLength) r.AddError("Category", $"Use {SchoolActivity.CategoryMaxLength} characters or fewer.");
        if (location.Length > SchoolActivity.LocationMaxLength) r.AddError("Location", $"Use {SchoolActivity.LocationMaxLength} characters or fewer.");
        if (!ActivityStyles.Icons.Contains(icon)) r.AddError("Icon", "Choose one of the icons shown.");
        if (!ActivityStyles.Colors.Contains(color)) r.AddError("Color", "Choose one of the colours shown.");
        if (i.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d, ct)) r.AddError("DepartmentId", "Choose a department from the list.");

        var staff = (i.StaffIds ?? []).ToHashSet();
        if (staff.Count > 0 && await db.StaffMembers.CountAsync(s => staff.Contains(s.Id), ct) != staff.Count) r.AddError("StaffIds", "Choose staff from the list.");
        var classes = (i.ClassIds ?? []).ToHashSet();
        if (classes.Count > 0 && await db.Classes.CountAsync(c => classes.Contains(c.Id), ct) != classes.Count) r.AddError("ClassIds", "Choose classes from the list.");

        return (r, new Clean(name, key, description, category.Length == 0 ? null : category, icon, color, location.Length == 0 ? null : location, staff, classes));
    }

    private void Apply(SchoolActivity a, Clean c, ActivityInput i)
    {
        (a.Name, a.NormalizedName, a.Description, a.Category, a.Icon, a.Color, a.Location, a.DepartmentId, a.IsActive, a.UpdatedBy) =
            (c.Name, c.Key, c.Description, c.Category, c.Icon, c.Color, c.Location, i.DepartmentId, i.IsActive, currentUser.Name);
    }

    private async Task LogAsync(int id, string action, string? details, CancellationToken ct)
    {
        db.ActivityHistory.Add(new SchoolActivityHistory
        {
            SchoolActivityId = id, Action = action, At = _time.UtcNow, By = currentUser.Name,
            Details = details is null ? null : details.Length > SchoolActivityHistory.TextMaxLength ? details[..SchoolActivityHistory.TextMaxLength] : details,
        });
        await db.SaveChangesAsync(ct);
    }
}
