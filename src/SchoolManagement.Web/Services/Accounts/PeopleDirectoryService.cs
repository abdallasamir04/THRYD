using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.OrgStructure;

namespace SchoolManagement.Web.Services.Accounts;

// ============================================================ read models

/// <summary>
/// One non-student person. A verified person is a staff record (Teachers and Management Team share the StaffMembers table);
/// a name that only appears in a source document with no staff record (e.g. an unresolved Teaching Staff roster row) is
/// listed as an unverified identity: never given an account, always sent to Admin review. Students are never included.
/// </summary>
public sealed record DirectoryPerson(string PersonId, int? StaffId, string FullName, string SourceEntity, string SourceRecordId, string Category,
    string? JobTitle, string? Position, string? Department, string? Subdepartment, string? Section, string? AssignedClass, string? AssignedFloor,
    string? Phone, string? ExistingEmail, bool Archived, string? ArchiveReason, IReadOnlyList<string> Roles, string? IdentityConflict, bool Verified)
{
    public bool Eligible => Verified && !Archived && IdentityConflict is null;
}

public sealed record DirectoryAccount(string UserId, int? StaffId, string Email, string DisplayName, bool Disabled, IReadOnlyList<string> Roles,
    DateTime CreatedAt, string? CreatedBy, DateTime? LastLoginAt);

public sealed record ResponsibilityRow(int StaffId, string Type, string Name, string? Description, string Module, string Entity, string SourceId,
    string? Class = null, string? Section = null, string? Floor = null, string? Location = null, DateOnly? Date = null, TimeOnly? Start = null,
    TimeOnly? End = null, string? AssignedBy = null, string Status = "Current", string? Priority = null, string? Notes = null);

public sealed record TaskRow(int StaffId, int TaskId, string Title, string? Description, TaskPriority Priority, string Status, string? CreatedBy,
    string? AssignedBy, DateTime AssignedAt, DateOnly? DueDate, DateTime? CompletedAt, string? Department, string? Notes, bool AsReviewer);

public sealed record PersonDutyRow(int StaffId, int DutyId, string DutyType, string DutyName, string? Description, string? Location, DateOnly Date,
    TimeOnly Start, TimeOnly End, string? AssignedBy, string Status, string Responsibility, string? Notes);

public sealed record AcademicRow(int StaffId, string? Department, string? Subject, string? Class, string? Section, string AssignmentType, string? Day,
    string? Time, int? WeeklyPeriods, string? ActiveWeeks, string? SubstituteStatus, string? Notes);

public sealed record PtcRow(int StaffId, string Event, string? Class, string? Department, string? Subject, int? Round, DateOnly? Date,
    TimeOnly? PlannedStart, TimeOnly? PlannedEnd, DateTime? ActualStart, DateTime? ActualEnd, string Status, string Responsibility, string? Notes);

public sealed record ReviewRow(string PersonId, string Person, string Category, string Detail, string ActionNeeded);

/// <summary>Records of a table that refer to a person (found from the data model, so no module is missed).</summary>
public sealed record LinkedRecords(int StaffId, string Table, string Column, int Count);

public sealed class PeopleDirectory
{
    public required IReadOnlyList<DirectoryPerson> People { get; init; }
    public required IReadOnlyList<DirectoryAccount> Accounts { get; init; }
    public required IReadOnlyList<ResponsibilityRow> Responsibilities { get; init; }
    public required IReadOnlyList<TaskRow> Tasks { get; init; }
    public required IReadOnlyList<PersonDutyRow> Duties { get; init; }
    public required IReadOnlyList<AcademicRow> Academic { get; init; }
    public required IReadOnlyList<PtcRow> Ptc { get; init; }
    public required IReadOnlyList<ReviewRow> Review { get; init; }
    public required IReadOnlyList<LinkedRecords> Linked { get; init; }

    /// <summary>Staff granted a specific module through configuration (DutyAudit / OrgStructure / StudentRecords AuthorizedStaffIds).</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> ConfiguredGrants { get; init; }

    public DirectoryAccount? AccountOf(int? staffId) => staffId is { } id ? Accounts.FirstOrDefault(a => a.StaffId == id) : null;
    public DirectoryPerson? Person(int staffId) => People.FirstOrDefault(p => p.StaffId == staffId);
}

public interface IPeopleDirectoryService
{
    /// <summary>Everyone (or one staff member) with every verified responsibility, account and review item, from the current records.</summary>
    Task<PeopleDirectory> BuildAsync(int? onlyStaffId = null, CancellationToken ct = default);
}

/// <summary>
/// The complete non-student people audit. Every person comes from an existing record (staff records, and unresolved source
/// rows that name a person without one); responsibilities come only from existing relationships (positions, floor teams,
/// department heads, KG class teams, tasks, duties, timetable lessons, workload, PTC) — nothing is guessed from a name or a
/// department. Duplicate identity candidates (same phone, same name) are flagged, never merged.
/// </summary>
public sealed class PeopleDirectoryService(SchoolDbContext db, IOptions<DutyAuditOptions> dutyAudit, IOptions<OrgStructureOptions> orgStructure,
    IOptions<StudentRecordsOptions> studentRecords, ISchoolTimeService? schoolTime = null) : IPeopleDirectoryService
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    public async Task<PeopleDirectory> BuildAsync(int? onlyStaffId = null, CancellationToken ct = default)
    {
        bool Mine(int id) => onlyStaffId is null || onlyStaffId == id;

        // ---------------- people
        var staff = await db.StaffMembers.AsNoTracking()
            .Where(s => onlyStaffId == null || s.Id == onlyStaffId)
            .OrderBy(s => s.FullName)
            .Select(s => new
            {
                s.Id, s.FullName, s.NormalizedName, s.JobTitle, Department = s.Department != null ? s.Department.Name : null, s.Subcategory, s.Subject,
                s.ContactNumber, s.NormalizedContact, s.ArchivedAt, s.ArchiveReason, IsTeacher = s is Teacher,
                Email = s is ManagementMember ? ((ManagementMember)s).Email : null,
                Position = s is ManagementMember ? (ManagementPosition?)((ManagementMember)s).Position : null,
                CustomPosition = s is ManagementMember && ((ManagementMember)s).CustomPosition != null ? ((ManagementMember)s).CustomPosition!.Title : null,
            })
            .ToListAsync(ct);
        var ids = staff.Select(s => s.Id).ToList();
        // Duplicate candidates are checked against everyone (also for one person's view).
        var everyone = await db.StaffMembers.AsNoTracking().Select(s => new { s.Id, s.FullName, s.NormalizedName, s.NormalizedContact }).ToListAsync(ct);

        var positions = await db.StaffPositionAssignments.AsNoTracking().Where(a => a.IsCurrent && ids.Contains(a.StaffMemberId))
            .Select(a => new { a.Id, a.StaffMemberId, a.Position.Title, Unit = a.Position.Unit.Name, a.GradeRange, a.Scope, ReportsTo = a.ReportsTo != null ? a.ReportsTo.FullName : null,
                a.StartDate, a.AssignedBy, a.IsPrimary, a.Notes })
            .ToListAsync(ct);
        var floors = await db.FloorStaffAssignments.AsNoTracking().Where(a => a.IsCurrent && ids.Contains(a.StaffMemberId))
            .Select(a => new { a.Id, a.StaffMemberId, Floor = a.Floor.Name, a.Role, a.Coverage, a.StartDate, a.AssignedBy, a.Note, Classes = a.Classes.Select(c => c.SchoolClass.Name).ToList() })
            .ToListAsync(ct);
        var heads = await db.DepartmentHeads.AsNoTracking().Where(h => h.IsCurrent && ids.Contains(h.TeacherId))
            .Select(h => new { h.Id, h.TeacherId, Department = h.Department.Name, h.StartDate, h.Note }).ToListAsync(ct);
        var kgTeams = await db.ClassTeachingAssignments.AsNoTracking().Where(a => a.IsActive && ids.Contains(a.StaffMemberId))
            .Select(a => new { a.Id, a.StaffMemberId, Class = a.SchoolClass.Name, Year = a.AcademicYear.Name, a.Role, a.AssignedAt, a.AssignedBy }).ToListAsync(ct);
        var floorAudit = await db.FloorAuditAccess.AsNoTracking().Where(a => ids.Contains(a.StaffMemberId))
            .Select(a => new { a.Id, a.StaffMemberId, a.Role, Floor = a.Floor != null ? a.Floor.Name : null, a.GrantedAt }).ToListAsync(ct);
        var activities = await db.ActivityStaff.AsNoTracking().Where(a => ids.Contains(a.StaffMemberId))
            .Select(a => new { a.SchoolActivityId, a.StaffMemberId, a.Activity.Name, a.Activity.IsActive }).ToListAsync(ct);

        // ---------------- tasks (assignee or reviewer)
        var today = _time.Today;
        var taskRows = await db.ManagementTasks.AsNoTracking().Where(t => ids.Contains(t.AssignedToId) || (t.ReviewerId != null && ids.Contains(t.ReviewerId.Value)))
            .Select(t => new
            {
                t.Id, t.AssignedToId, t.ReviewerId, t.Title, t.Description, t.Priority, t.Status, t.CreatedBy, Creator = t.Creator != null ? t.Creator.FullName : null,
                t.CreatedAt, t.DueDate, t.CompletedAt, t.Notes, Department = t.AssignedTo.Department != null ? t.AssignedTo.Department.Name : null,
            })
            .ToListAsync(ct);
        string TaskStatus(ManagementTaskStatus s, DateOnly? due) =>
            s is ManagementTaskStatus.ToDo or ManagementTaskStatus.InProgress && due is { } d && d < today ? "Overdue"
            : s switch { ManagementTaskStatus.ToDo => "Pending", ManagementTaskStatus.InProgress => "In Progress", ManagementTaskStatus.Completed => "Completed",
                ManagementTaskStatus.OnHold => "On Hold", _ => "Cancelled" };
        var tasks = new List<TaskRow>();
        foreach (var t in taskRows)
        {
            if (Mine(t.AssignedToId) && ids.Contains(t.AssignedToId))
                tasks.Add(new TaskRow(t.AssignedToId, t.Id, t.Title, t.Description, t.Priority, TaskStatus(t.Status, t.DueDate), t.CreatedBy, t.Creator ?? t.CreatedBy,
                    t.CreatedAt, t.DueDate, t.CompletedAt, t.Department, t.Notes, false));
            if (t.ReviewerId is { } r && ids.Contains(r) && r != t.AssignedToId)
                tasks.Add(new TaskRow(r, t.Id, t.Title, t.Description, t.Priority, TaskStatus(t.Status, t.DueDate), t.CreatedBy, t.Creator ?? t.CreatedBy,
                    t.CreatedAt, t.DueDate, t.CompletedAt, t.Department, t.Notes, true));
        }

        // ---------------- duties (primary, or backup)
        var dutyRows = await db.DutyAssignments.AsNoTracking()
            .Where(d => ids.Contains(d.PrimaryStaffId) || (d.BackupStaffId != null && ids.Contains(d.BackupStaffId.Value)))
            .Select(d => new { d.Id, d.PrimaryStaffId, d.BackupStaffId, d.BackupStatus, d.DutyType, d.Component, d.Description, d.Location, d.DutyDate, d.StartTime, d.EndTime, d.Status, d.Notes })
            .ToListAsync(ct);
        var duties = new List<PersonDutyRow>();
        foreach (var d in dutyRows)
        {
            if (ids.Contains(d.PrimaryStaffId))
                duties.Add(new PersonDutyRow(d.PrimaryStaffId, d.Id, d.DutyType.ToString(), DutyComponents.Name(d.Component), d.Description, d.Location, d.DutyDate, d.StartTime, d.EndTime,
                    null, d.Status.ToString(), "Primary", d.Notes));
            if (d.BackupStaffId is { } b && ids.Contains(b))
                duties.Add(new PersonDutyRow(b, d.Id, d.DutyType.ToString(), DutyComponents.Name(d.Component), d.Description, d.Location, d.DutyDate, d.StartTime, d.EndTime,
                    null, d.Status.ToString(), $"Backup ({d.BackupStatus})", d.Notes));
        }

        // ---------------- academic: current timetable lessons, workload, KG class teams, PTC substitutes
        var lessons = await db.TimetableAssignments.AsNoTracking().Where(t => t.EndedOn == null && ids.Contains(t.TeacherId))
            .Select(t => new { t.Id, t.TeacherId, Department = t.Department.Name, t.Subject, Class = t.SchoolClass.Name, t.Day, Slot = t.TimeSlot.Name, t.TimeSlot.Number,
                t.TimeSlot.StartTime, t.TimeSlot.EndTime, t.StartsOn, t.Notes })
            .ToListAsync(ct);
        var workload = await db.WorkloadAssignments.AsNoTracking().Where(w => ids.Contains(w.TeacherId))
            .Select(w => new { w.Id, w.TeacherId, w.WeekStart, w.Category, w.Periods, Class = w.SchoolClass != null ? w.SchoolClass.Name : null, w.Description, w.Day }).ToListAsync(ct);
        var academic = new List<AcademicRow>();
        academic.AddRange(lessons.Select(l => new AcademicRow(l.TeacherId, l.Department, l.Subject, l.Class, null, "Timetable lesson", ScheduleRules.DayName(l.Day),
            l.StartTime is { } s && l.EndTime is { } e ? ScheduleRules.Times(s, e) : (l.Slot ?? $"Period {l.Number}"), 1, $"From week of {l.StartsOn:yyyy-MM-dd}", null, l.Notes)));
        academic.AddRange(workload.Select(w => new AcademicRow(w.TeacherId, null, null, w.Class, null, $"Workload ({w.Category})", w.Day is { } dd ? ScheduleRules.DayName(dd) : null,
            null, w.Periods, $"Week of {w.WeekStart:yyyy-MM-dd}", w.Category == WorkloadCategory.Substitution ? "Substitution" : null, w.Description)));
        academic.AddRange(kgTeams.Select(k => new AcademicRow(k.StaffMemberId, null, null, k.Class, null, $"KG class team: {(k.Role == ClassTeachingRole.HomeroomTeacher ? "Homeroom Teacher" : "Co-Teacher")}",
            null, null, null, k.Year, null, null)));

        // ---------------- PTC
        var interviews = await db.PtcTeacherInterviews.AsNoTracking().Where(i => ids.Contains(i.TeacherStaffMemberId))
            .Select(i => new { i.TeacherStaffMemberId, Event = i.Booking.ClassSchedule.PtcEvent.Title, Class = i.Booking.ClassSchedule.SchoolClass.Name,
                Department = i.BookingSubject.Department.Name, i.BookingSubject.SubjectName, i.Booking.Round.RoundNumber, i.Date, i.ScheduledStart, i.ScheduledEnd,
                i.ActualStart, i.ActualEnd, i.Status, BookingStatus = i.Booking.Status, i.Booking.BookingReference })
            .ToListAsync(ct);
        var kgPtc = await db.PtcClassTeachers.AsNoTracking().Where(t => ids.Contains(t.StaffMemberId) && t.Status == PtcClassTeacherStatus.Active)
            .Select(t => new { t.StaffMemberId, Event = t.ClassSchedule.PtcEvent.Title, Class = t.ClassSchedule.SchoolClass.Name, Department = t.Department.Name, t.SubjectName, t.ClassSchedule.EventDate })
            .ToListAsync(ct);
        var subs = await db.PtcSubstituteAssignments.AsNoTracking()
            .Where(a => a.Status == PtcSubstituteStatus.Active && (ids.Contains(a.SubstituteTeacherStaffMemberId) || ids.Contains(a.OriginalTeacherStaffMemberId)))
            .Select(a => new { a.SubstituteTeacherStaffMemberId, a.OriginalTeacherStaffMemberId, Original = a.OriginalTeacher.FullName, Substitute = a.SubstituteTeacher.FullName,
                Event = a.ClassSchedule.PtcEvent.Title, Class = a.ClassSchedule.SchoolClass.Name, a.ClassSchedule.EventDate, a.StartTime, a.EndTime, a.Reason })
            .ToListAsync(ct);
        var ptc = new List<PtcRow>();
        ptc.AddRange(interviews.Select(i => new PtcRow(i.TeacherStaffMemberId, i.Event, i.Class, i.Department, i.SubjectName, i.RoundNumber, i.Date, i.ScheduledStart, i.ScheduledEnd,
            i.ActualStart, i.ActualEnd, i.BookingStatus == PtcBookingStatus.Cancelled ? "Cancelled" : i.Status.ToString(), "Teacher interview", $"Booking {i.BookingReference}")));
        ptc.AddRange(kgPtc.Select(k => new PtcRow(k.StaffMemberId, k.Event, k.Class, k.Department, k.SubjectName, null, k.EventDate, null, null, null, null, "Selected",
            "Former manual KG PTC selection (history only: PTC now uses timetable teachers)", null)));
        foreach (var a in subs)
        {
            if (ids.Contains(a.SubstituteTeacherStaffMemberId))
                ptc.Add(new PtcRow(a.SubstituteTeacherStaffMemberId, a.Event, a.Class, null, null, null, a.EventDate, a.StartTime, a.EndTime, null, null, "Active",
                    $"PTC substitute for {a.Original}", a.Reason));
            if (ids.Contains(a.OriginalTeacherStaffMemberId))
                ptc.Add(new PtcRow(a.OriginalTeacherStaffMemberId, a.Event, a.Class, null, null, null, a.EventDate, a.StartTime, a.EndTime, null, null, "Active",
                    $"Covered at PTC by {a.Substitute}", a.Reason));
            if (ids.Contains(a.SubstituteTeacherStaffMemberId))
                academic.Add(new AcademicRow(a.SubstituteTeacherStaffMemberId, null, null, a.Class, null, "PTC substitute cover", a.EventDate.ToString("ddd d MMM yyyy", C),
                    ScheduleRules.Times(a.StartTime, a.EndTime), null, null, $"Covering {a.Original}", a.Reason));
        }

        // ---------------- responsibilities (one row per verified responsibility)
        var resp = new List<ResponsibilityRow>();
        foreach (var s in staff.Where(s => s.Position is not null || s.CustomPosition is not null))
            resp.Add(new ResponsibilityRow(s.Id, "Management position", s.Position is { } mp ? ManagementPositions.Name(mp) : s.CustomPosition!, null, "Management Team", "StaffMembers", $"StaffMember #{s.Id}"));
        resp.AddRange(positions.Select(p => new ResponsibilityRow(p.StaffMemberId, "Organizational position", p.Title, p.Unit + (p.ReportsTo is null ? "" : $" · reports to {p.ReportsTo}"),
            "Organizational Structure", "StaffPositionAssignments", $"#{p.Id}", Section: p.GradeRange ?? p.Scope, Date: p.StartDate, AssignedBy: p.AssignedBy,
            Status: p.IsPrimary ? "Current (primary)" : "Current", Notes: p.Notes)));
        resp.AddRange(floors.Select(f => new ResponsibilityRow(f.StaffMemberId, "Floor team", FloorRoleName(f.Role), f.Coverage, "Floor Management", "FloorStaffAssignments", $"#{f.Id}",
            Class: f.Classes.Count == 0 ? null : string.Join(", ", f.Classes), Floor: f.Floor, Date: f.StartDate, AssignedBy: f.AssignedBy, Notes: f.Note)));
        resp.AddRange(heads.Select(h => new ResponsibilityRow(h.TeacherId, "Head of department", h.Department, null, "Departments", "DepartmentHeadAssignments", $"#{h.Id}", Date: h.StartDate, Notes: h.Note)));
        resp.AddRange(kgTeams.Select(k => new ResponsibilityRow(k.StaffMemberId, "KG class team", k.Role == ClassTeachingRole.HomeroomTeacher ? "Homeroom Teacher" : "Co-Teacher", k.Year,
            "Classes", "ClassTeachingAssignments", $"#{k.Id}", Class: k.Class, AssignedBy: k.AssignedBy)));
        resp.AddRange(floorAudit.Select(f => new ResponsibilityRow(f.StaffMemberId, "Floor audit access", f.Role.ToString(), null, "Floor Audit", "FloorAuditAccess", $"#{f.Id}", Floor: f.Floor)));
        resp.AddRange(activities.Select(a => new ResponsibilityRow(a.StaffMemberId, "Activity staff", a.Name, null, "Activities", "ActivityStaff", $"Activity #{a.SchoolActivityId}",
            Status: a.IsActive ? "Current" : "Activity inactive")));
        resp.AddRange(tasks.Select(t => new ResponsibilityRow(t.StaffId, t.AsReviewer ? "Task (reviewer)" : "Task", t.Title, t.Description, "Tasks", "ManagementTasks", $"Task #{t.TaskId}",
            Date: t.DueDate, AssignedBy: t.AssignedBy, Status: t.Status, Priority: t.Priority.ToString(), Notes: t.Notes)));
        resp.AddRange(duties.Select(d => new ResponsibilityRow(d.StaffId, "Duty", d.DutyName, d.Description, "Duties", "DutyAssignments", $"Duty #{d.DutyId}", Location: d.Location,
            Date: d.Date, Start: d.Start, End: d.End, Status: d.Status, Notes: d.Responsibility)));
        resp.AddRange(lessons.Select(l => new ResponsibilityRow(l.TeacherId, "Teaching", $"{l.Subject} · {l.Class}", l.Department, "Timetable", "TimetableAssignments", $"Lesson #{l.Id}",
            Class: l.Class, Start: l.StartTime, End: l.EndTime, Notes: ScheduleRules.DayName(l.Day))));
        resp.AddRange(ptc.Select(p => new ResponsibilityRow(p.StaffId, "PTC", p.Responsibility, p.Subject, "PTC", "PTC", p.Notes ?? p.Event, Class: p.Class, Date: p.Date,
            Start: p.PlannedStart, End: p.PlannedEnd, Status: p.Status)));

        // ---------------- linked records of every table that refers to a person (from the data model)
        var linked = await LinkedRecordsAsync(onlyStaffId, ct);

        // ---------------- people rows
        var people = new List<DirectoryPerson>();
        foreach (var s in staff)
        {
            var conflicts = new List<string>();
            if (s.NormalizedContact.Length > 0)
                conflicts.AddRange(everyone.Where(o => o.Id != s.Id && o.NormalizedContact == s.NormalizedContact).Select(o => $"shares phone {s.ContactNumber} with #{o.Id} {o.FullName}"));
            conflicts.AddRange(everyone.Where(o => o.Id != s.Id && o.NormalizedName == s.NormalizedName).Select(o => $"same name as #{o.Id} {o.FullName}"));
            var myFloors = floors.Where(f => f.StaffMemberId == s.Id).ToList();
            var roles = new List<string> { s.IsTeacher ? "Teacher" : "Management Team" };
            if (s.Position is { } p) roles.Add(ManagementPositions.Name(p));
            else if (s.CustomPosition is { } cp) roles.Add(cp);
            roles.AddRange(myFloors.Select(f => FloorRoleName(f.Role)));
            if (heads.Any(h => h.TeacherId == s.Id)) roles.Add("Head of Department");
            roles.AddRange(kgTeams.Where(k => k.StaffMemberId == s.Id).Select(k => k.Role == ClassTeachingRole.HomeroomTeacher ? "KG Homeroom Teacher" : "KG Co-Teacher"));
            var myPositions = positions.Where(x => x.StaffMemberId == s.Id).OrderByDescending(x => x.IsPrimary).ToList();
            people.Add(new DirectoryPerson($"S{s.Id}", s.Id, s.FullName, s.IsTeacher ? "StaffMembers (Teacher)" : "StaffMembers (Management)", s.Id.ToString(C),
                s.IsTeacher ? "Teacher" : "Management Team", s.JobTitle,
                s.Position is { } pp ? ManagementPositions.Name(pp) : s.CustomPosition ?? myPositions.FirstOrDefault()?.Title,
                s.Department, s.Subcategory ?? s.Subject, myPositions.Select(x => x.GradeRange ?? x.Scope).FirstOrDefault(x => x is not null),
                Join(myFloors.SelectMany(f => f.Classes).Concat(kgTeams.Where(k => k.StaffMemberId == s.Id).Select(k => k.Class)).Distinct()),
                Join(myFloors.Select(f => f.Floor).Distinct()), s.ContactNumber, s.Email, s.ArchivedAt is not null, s.ArchiveReason,
                roles.Distinct().ToList(), conflicts.Count == 0 ? null : string.Join("; ", conflicts), true));
        }

        // Names in a source document with no staff record (never merged with anyone by name).
        var unresolved = onlyStaffId is not null ? [] : await db.TeachingRosterEntries.AsNoTracking().Where(r => r.StaffMemberId == null && !r.IsBlank)
            .OrderBy(r => r.RowNumber).Select(r => new { r.Id, r.RowNumber, r.StaffName, r.Subject, r.GradeLevels, r.MatchNote, r.SourceName }).ToListAsync(ct);
        foreach (var r in unresolved)
            people.Add(new DirectoryPerson($"R{r.RowNumber}", null, r.StaffName ?? "(no name in the source row)", $"TeachingRosterEntries ({r.SourceName})", r.Id.ToString(C),
                "Unverified identity (roster name only)", null, null, null, r.Subject, r.GradeLevels, null, null, null, null, false, null, ["Unverified"],
                r.MatchNote ?? "Identity unresolved", false));

        // ---------------- accounts
        var accountRows = await db.Users.AsNoTracking().Where(u => onlyStaffId == null || u.StaffMemberId == onlyStaffId)
            .Select(u => new { u.Id, u.StaffMemberId, u.Email, u.DisplayName, u.IsDisabled, u.CreatedAt, u.CreatedBy, u.LastLoginAt }).ToListAsync(ct);
        var roleRows = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync(ct);
        var accounts = accountRows.Select(u => new DirectoryAccount(u.Id, u.StaffMemberId, u.Email ?? "", u.DisplayName, u.IsDisabled,
            roleRows.Where(r => r.UserId == u.Id).Select(r => r.Name!).ToList(), u.CreatedAt, u.CreatedBy, u.LastLoginAt)).ToList();

        // ---------------- configured grants
        var grants = new Dictionary<int, List<string>>();
        void Grant(IEnumerable<int>? staffIds, string module) { foreach (var id in staffIds ?? []) (grants.TryGetValue(id, out var l) ? l : grants[id] = []).Add(module); }
        Grant(dutyAudit.Value.AuthorizedStaffIds, "Duties & Duty Audit");
        Grant(orgStructure.Value.AuthorizedStaffIds, "Management & Organizational Structure");
        Grant(studentRecords.Value.AuthorizedStaffIds, "Students & Classes (Student Records)");

        // ---------------- review
        var review = new List<ReviewRow>();
        var flags = await db.StaffDataQualityFlags.AsNoTracking().Where(f => !f.IsResolved && ids.Contains(f.StaffMemberId))
            .Select(f => new { f.StaffMemberId, f.Kind, f.Message }).ToListAsync(ct);
        foreach (var p in people)
        {
            string Who() => p.FullName;
            if (!p.Verified)
            {
                review.Add(new ReviewRow(p.PersonId, Who(), "Unverified identity", $"{p.SourceEntity} row {p.PersonId[1..]}: {p.IdentityConflict}",
                    "Confirm who this is and link the row to a staff record (or create one). No account is created for an unverified identity."));
                continue;
            }
            var acc = accounts.FirstOrDefault(a => a.StaffId == p.StaffId);
            if (p.IdentityConflict is not null)
                review.Add(new ReviewRow(p.PersonId, Who(), "Duplicate identity candidate", p.IdentityConflict,
                    "Check whether these records are the same person. Not merged; no account was created automatically. Create it from Staff → Accounts once confirmed."));
            if (p.ExistingEmail is null)
                review.Add(new ReviewRow(p.PersonId, Who(), "Missing e-mail", "No contact e-mail on record; the login e-mail is a generated identifier, e-mail delivery is not configured.",
                    "Record a contact e-mail if one is needed for password-reset e-mails."));
            if (string.IsNullOrWhiteSpace(p.JobTitle)) review.Add(new ReviewRow(p.PersonId, Who(), "Missing job title", "No job title on the staff record.", "Add the job title."));
            if (p.Category == "Teacher" && p.Department is null) review.Add(new ReviewRow(p.PersonId, Who(), "Missing department", "A teacher with no academic department.", "Assign the department."));
            if (!resp.Any(r => r.StaffId == p.StaffId))
                review.Add(new ReviewRow(p.PersonId, Who(), "No recorded responsibilities", "No position, floor team, class team, task, duty, lesson or PTC assignment is recorded.",
                    "Confirm the person's responsibilities and record them in the relevant module."));
            if (p.Archived && acc is { Disabled: false })
                review.Add(new ReviewRow(p.PersonId, Who(), "Archived person with an active account", "The staff record is archived but the account is enabled.", "Disable the account."));
            if (acc is null && p.Eligible)
                review.Add(new ReviewRow(p.PersonId, Who(), "No account", "An eligible active person without a sign-in account.", "Run account provisioning or create the account."));
            foreach (var f in flags.Where(f => f.StaffMemberId == p.StaffId))
                review.Add(new ReviewRow(p.PersonId, Who(), $"Data quality: {f.Kind}", f.Message, "Resolve the flag in Positions & Assignments → Review."));
        }
        foreach (var t in tasks.Where(t => t.Status == "Overdue"))
            review.Add(new ReviewRow($"S{t.StaffId}", people.FirstOrDefault(p => p.StaffId == t.StaffId)?.FullName ?? "", "Overdue task", $"Task #{t.TaskId} {t.Title} was due {t.DueDate:yyyy-MM-dd}.", "Follow up the task."));
        foreach (var d in dutyRows.Where(d => d.BackupStaffId is not null && d.BackupStatus == BackupRequestStatus.Pending))
            review.Add(new ReviewRow($"S{d.PrimaryStaffId}", people.FirstOrDefault(p => p.StaffId == d.PrimaryStaffId)?.FullName ?? "", "Duty backup not confirmed",
                $"Duty #{d.Id} on {d.DutyDate:yyyy-MM-dd}: the backup has not accepted.", "Confirm the backup or assign another."));

        return new PeopleDirectory
        {
            People = people, Accounts = accounts, Responsibilities = resp, Tasks = tasks, Duties = duties, Academic = academic, Ptc = ptc, Review = review, Linked = linked,
            ConfiguredGrants = grants.ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Value),
        };
    }

    private static string? Join(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    public static string FloorRoleName(FloorRole r) => r switch
    {
        FloorRole.StageCoordinator => "Stage Coordinator",
        FloorRole.FloorSupervisor => "Floor Supervisor",
        FloorRole.Matron => "Matron",
        FloorRole.FloatingMatron => "Floating Matron",
        _ => "Floor Social Worker",
    };

    /// <summary>
    /// Every table / column of the data model that refers to a staff record (found from the EF model, not a fixed list), with
    /// its record count per person — so a module that stores a person is never missed by the audit.
    /// </summary>
    private async Task<List<LinkedRecords>> LinkedRecordsAsync(int? onlyStaffId, CancellationToken ct)
    {
        var staffType = db.Model.FindEntityType(typeof(StaffMember))!;
        var targets = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType == staffType || fk.PrincipalEntityType.GetAllBaseTypes().Contains(staffType))
            .Where(fk => fk.DeclaringEntityType.ClrType != typeof(AppUser) && fk.Properties.Count == 1)
            .Select(fk => (Table: fk.DeclaringEntityType.GetTableName()!, Column: fk.Properties[0].GetColumnName()))
            .Distinct().ToList();
        var result = new List<LinkedRecords>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            foreach (var (table, column) in targets)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                // Identifiers come from the data model (not input); the person filter is a parameter.
                cmd.CommandText = $"SELECT [{column}], COUNT(*) FROM [{table}] WHERE [{column}] IS NOT NULL" + (onlyStaffId is null ? "" : $" AND [{column}] = @id") + $" GROUP BY [{column}]";
                if (onlyStaffId is { } id)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@id";
                    p.Value = id;
                    cmd.Parameters.Add(p);
                }
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) result.Add(new LinkedRecords(reader.GetInt32(0), table, column, reader.GetInt32(1)));
            }
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
        return result;
    }
}
