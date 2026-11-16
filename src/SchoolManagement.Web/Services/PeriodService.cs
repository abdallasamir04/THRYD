using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>Weekday applicability of a period, stored as a bit mask (0 = every configured school day).</summary>
public static class PeriodDays
{
    public static int Bit(DayOfWeek d) => 1 << (int)d;

    /// <summary>All configured school days (<see cref="ScheduleRules.Days"/>).</summary>
    public static int AllMask => ScheduleRules.Days.Aggregate(0, (m, d) => m | Bit(d));

    public static bool IsAll(int mask) => mask == 0 || (mask & AllMask) == AllMask;

    public static bool Applies(int mask, DayOfWeek d) => ScheduleRules.IsSchoolDay(d) && (mask == 0 || (mask & Bit(d)) != 0);

    public static IReadOnlyList<DayOfWeek> Days(int mask) => ScheduleRules.Days.Where(d => Applies(mask, d)).ToList();

    /// <summary>The stored mask: 0 for "all school days" (also when every day is ticked), else the ticked school days.</summary>
    public static int Mask(bool allDays, IEnumerable<DayOfWeek>? days)
    {
        if (allDays) return 0;
        var m = (days ?? []).Where(ScheduleRules.IsSchoolDay).Aggregate(0, (a, d) => a | Bit(d));
        return m == AllMask ? 0 : m;
    }

    public static string Label(int mask) => IsAll(mask) ? "Every school day" : string.Join(", ", Days(mask).Select(ScheduleRules.DayShort));
}

/// <summary>
/// Every period of the school and the structure each class gets from them: the school-wide periods it has
/// not replaced, plus its own periods. Loaded once per request (a school has tens of periods, not thousands).
/// </summary>
public sealed class PeriodMap
{
    private readonly Dictionary<int, SlotView> _byId;
    private readonly Dictionary<int, HashSet<int>> _replaced = [];
    private readonly Dictionary<int, IReadOnlyList<SlotView>> _forClass = [];

    public PeriodMap(IReadOnlyList<SlotView> all, IReadOnlyList<(int Id, string Name, bool IsActive)> classes)
    {
        All = all;
        Classes = classes;
        _byId = all.ToDictionary(s => s.Id);
    }

    /// <summary>Every period, archived ones included (history).</summary>
    public IReadOnlyList<SlotView> All { get; }
    public IReadOnlyList<(int Id, string Name, bool IsActive)> Classes { get; }

    public static readonly Expression<Func<TimeSlot, SlotView>> Project = s => new SlotView(s.Id, s.Number, s.Name, s.StartTime, s.EndTime, s.IsActive,
        s.SchoolClassId, s.SchoolClass != null ? s.SchoolClass.Name : null, s.DayMask, s.ReplacesTimeSlotId, s.ArchivedAt != null);

    public static async Task<PeriodMap> LoadAsync(SchoolDbContext db, CancellationToken ct)
    {
        var slots = await db.TimeSlots.AsNoTracking().Select(Project).ToListAsync(ct);
        var classes = await db.Classes.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name, c.IsActive }).ToListAsync(ct);
        return new PeriodMap(slots, classes.Select(c => (c.Id, c.Name, c.IsActive)).ToList());
    }

    /// <summary>Display order: order number, then start time (equal numbers are allowed), school-wide first.</summary>
    public static IReadOnlyList<SlotView> Order(IEnumerable<SlotView> slots) =>
        slots.OrderBy(s => s.Number).ThenBy(s => s.Start ?? TimeOnly.MinValue).ThenBy(s => s.ClassId ?? 0).ThenBy(s => s.Id).ToList();

    public SlotView? Get(int id) => _byId.GetValueOrDefault(id);

    public string ClassName(int classId) => Classes.FirstOrDefault(c => c.Id == classId).Name ?? "this class";

    /// <summary>The school-wide periods (not archived), in display order.</summary>
    public IReadOnlyList<SlotView> SchoolWide => Order(All.Where(s => s.ClassId is null && !s.Archived));

    /// <summary>School-wide periods a class does not use: replaced by its own version, or removed for it (archived replacement).</summary>
    public HashSet<int> ReplacedFor(int classId)
    {
        if (_replaced.TryGetValue(classId, out var set)) return set;
        set = All.Where(s => s.ClassId == classId && s.ReplacesId is not null).Select(s => s.ReplacesId!.Value).ToHashSet();
        _replaced[classId] = set;
        return set;
    }

    /// <summary>A class's periods (enabled and disabled, not archived), in display order.</summary>
    public IReadOnlyList<SlotView> ForClass(int classId)
    {
        if (_forClass.TryGetValue(classId, out var list)) return list;
        var replaced = ReplacedFor(classId);
        list = Order(All.Where(s => !s.Archived && (s.ClassId == classId || (s.ClassId is null && !replaced.Contains(s.Id)))));
        _forClass[classId] = list;
        return list;
    }

    /// <summary>The enabled periods of a class on one day.</summary>
    public IReadOnlyList<SlotView> ForClassDay(int classId, DayOfWeek day) => ForClass(classId).Where(s => s.IsActive && s.AppliesOn(day)).ToList();

    /// <summary>True when the class has this period, enabled, on this day (a lesson may be placed there).</summary>
    public bool Schedules(int classId, int slotId, DayOfWeek day) =>
        Get(slotId) is { IsActive: true, Archived: false } s && s.AppliesOn(day) && ForClass(classId).Any(x => x.Id == slotId);

    public bool InStructure(int classId, int slotId) => ForClass(classId).Any(x => x.Id == slotId);

    /// <summary>Classes whose timetable includes this period.</summary>
    public IReadOnlyList<int> ClassesUsing(SlotView slot) =>
        slot.ClassId is { } c ? [c] : Classes.Where(x => !ReplacedFor(x.Id).Contains(slot.Id)).Select(x => x.Id).ToList();

    /// <summary>Lessons a class can have in a week (enabled periods × the days they run).</summary>
    public int Capacity(int classId) => ScheduleRules.Days.Sum(d => ForClassDay(classId, d).Count);

    /// <summary>The class's own versions / removals of school periods (for "use the school period again").</summary>
    public IReadOnlyList<SlotView> Replacements(int classId) => Order(All.Where(s => s.ClassId == classId && s.ReplacesId is not null));
}

public enum PeriodScope
{
    /// <summary>Only the selected class's timetable.</summary>
    ThisClass = 1,

    /// <summary>Every class's timetable (a school-wide period).</summary>
    AllClasses = 2,
}

public sealed record PeriodInput(string? Name, TimeOnly? Start, TimeOnly? End, int? Order, bool IsActive, bool AllDays, IReadOnlyList<DayOfWeek>? Days);

/// <summary>A current lesson affected by a period change.</summary>
public sealed record LessonRef(int Id, int ClassId, string ClassName, DayOfWeek Day, string Subject, int TeacherId, string TeacherName)
{
    public string Text => $"{ClassName} · {ScheduleRules.DayName(Day)}: {Subject} ({TeacherName})";
}

/// <summary>What a period change would do: shown before it is applied, and the reasons it cannot be.</summary>
public sealed class PeriodPlan
{
    public required string Summary { get; init; }
    public IReadOnlyList<string> Classes { get; init; } = [];
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>Current lessons that move to the new times / days, or end (delete).</summary>
    public IReadOnlyList<LessonRef> Lessons { get; init; } = [];
    public string? LessonsNote { get; init; }

    /// <summary>Echoed back by the confirmation; a different value (inputs or data changed) means "show the plan again".</summary>
    public required string Fingerprint { get; init; }
}

public sealed record PeriodOutcome(ServiceResult Result, PeriodPlan? Plan, string? Message = null)
{
    public bool NeedsConfirmation => Result.Errors.Any(e => e.Key == PeriodService.ConfirmKey);
}

public interface IPeriodService
{
    Task<PeriodMap> MapAsync(CancellationToken ct = default);

    /// <summary>Current lessons in a period (of one class, or all).</summary>
    Task<IReadOnlyList<LessonRef>> LessonsInAsync(int slotId, int? classId, CancellationToken ct = default);

    Task<PeriodOutcome> AddAsync(PeriodInput input, PeriodScope scope, int? classId, string? confirmation, CancellationToken ct = default);
    Task<PeriodOutcome> EditAsync(int id, PeriodInput input, PeriodScope scope, int? classId, string? confirmation, CancellationToken ct = default);
    Task<PeriodOutcome> DeleteAsync(int id, PeriodScope scope, int? classId, bool endLessons, string? confirmation, CancellationToken ct = default);

    /// <summary>A class uses the school-wide period again instead of its own version / removal.</summary>
    Task<ServiceResult> RestoreAsync(int replacementId, CancellationToken ct = default);
}

/// <summary>
/// Period management. Every change is validated for every affected class and day before anything is written
/// (overlaps, display order, duplicate names, lessons on removed days, teacher clashes at new times), then
/// applied in one transaction under the period-structure lock — all classes or none. Lessons are never
/// deleted: a lesson that must leave its period is ended from this week (history and attendance stay) and,
/// when it moves, re-created in the new period. Changes to every class need an explicit confirmation of the
/// shown plan.
/// </summary>
public sealed class PeriodService(SchoolDbContext db, ICurrentUser currentUser) : IPeriodService
{
    public const string ConfirmKey = "Confirmation";
    private const int ListLimit = 8;

    private sealed record Candidate(int Id, int Number, string Label, TimeOnly? Start, TimeOnly? End, bool IsActive, int DayMask);

    private sealed record Problem(string Message, int ClassId, DayOfWeek Day);

    public Task<PeriodMap> MapAsync(CancellationToken ct = default) => PeriodMap.LoadAsync(db, ct);

    public async Task<IReadOnlyList<LessonRef>> LessonsInAsync(int slotId, int? classId, CancellationToken ct = default) =>
        await Refs(Current(slotId, classId), ct);

    // ================================================================== add

    public async Task<PeriodOutcome> AddAsync(PeriodInput input, PeriodScope scope, int? classId, string? confirmation, CancellationToken ct = default)
    {
        var (check, name) = Check(input, requireTimes: true, hadTimes: false);
        if (scope == PeriodScope.ThisClass && classId is null) check.AddError("ClassId", "Choose the class.");
        if (!check.Succeeded) return new(check, null);

        return await InTransactionAsync(async map =>
        {
            if (scope == PeriodScope.ThisClass && !map.Classes.Any(c => c.Id == classId)) return new PeriodOutcome(ServiceResult.Fail("ClassId", "This class no longer exists."), null);
            var mask = PeriodDays.Mask(input.AllDays, input.Days);
            var days = PeriodDays.Days(mask);
            var classes = scope == PeriodScope.ThisClass ? [classId!.Value] : map.Classes.Select(c => c.Id).ToList();
            var order = input.Order ?? AutoOrder(map, classes, days, input.Start!.Value);
            var cand = new Candidate(0, order, name ?? $"Period {order}", input.Start, input.End, input.IsActive, mask);

            var problems = new List<Problem>();
            foreach (var c in classes) StructureProblems(map, c, cand, [], days, problems);

            var target = scope == PeriodScope.ThisClass ? map.ClassName(classId!.Value) : $"all {Ui.Plural(classes.Count, "class", "classes")}";
            var plan = new PeriodPlan
            {
                Summary = $"Add “{cand.Label}” ({ScheduleRules.Times(cand.Start, cand.End)}, {PeriodDays.Label(mask)}) to {target}.",
                Classes = classes.Select(map.ClassName).ToList(),
                Conflicts = Format(map, problems),
                Fingerprint = Fingerprint("add", scope, classId, cand, input),
            };
            if (plan.Conflicts.Count > 0) return Blocked(plan);
            if (scope == PeriodScope.AllClasses && confirmation != plan.Fingerprint) return Confirm(plan);

            var slot = new TimeSlot
            {
                Number = order, Name = name, StartTime = input.Start, EndTime = input.End, IsActive = input.IsActive,
                SchoolClassId = scope == PeriodScope.ThisClass ? classId : null, DayMask = mask, UpdatedAt = DateTime.UtcNow, UpdatedBy = currentUser.Name,
            };
            db.TimeSlots.Add(slot);
            await db.SaveChangesAsync(ct);
            return new PeriodOutcome(ServiceResult.Ok(slot.Id), plan, $"“{cand.Label}” added to {target} ({PeriodDays.Label(mask)}).");
        }, ct);
    }

    // ================================================================== edit

    public async Task<PeriodOutcome> EditAsync(int id, PeriodInput input, PeriodScope scope, int? classId, string? confirmation, CancellationToken ct = default)
    {
        return await InTransactionAsync(async map =>
        {
            var p = map.Get(id);
            if (p is null || p.Archived) return new PeriodOutcome(ServiceResult.Fail("", "This period no longer exists."), null);
            var (check, name) = Check(input, requireTimes: false, hadTimes: p.HasTimes);
            // A class-specific period only ever affects its class; a school-wide one affects one class (its own version) or all.
            var fork = p.ClassId is null && scope == PeriodScope.ThisClass;
            if (fork && classId is null) check.AddError("ClassId", "Choose the class.");
            else if (fork && !map.ClassesUsing(p).Contains(classId!.Value)) check.AddError("ClassId", $"{map.ClassName(classId.Value)} does not use {p.Label}.");
            if (!check.Succeeded) return new PeriodOutcome(check, null);

            var mask = PeriodDays.Mask(input.AllDays, input.Days);
            var days = PeriodDays.Days(mask);
            var order = input.Order ?? p.Number;
            var cand = new Candidate(fork ? 0 : p.Id, order, name ?? $"Period {order}", input.Start, input.End, input.IsActive, mask);
            var classes = fork ? [classId!.Value] : map.ClassesUsing(p);
            var lessons = await Refs(Current(p.Id, fork ? classId : null), ct);
            var timesChanged = p.Start != input.Start || p.End != input.End;
            var unchanged = !timesChanged && p.Name == name && p.Number == order && p.IsActive == input.IsActive && p.DayMask == mask;
            if (unchanged && !fork) return new PeriodOutcome(ServiceResult.Ok(p.Id), null, $"{p.Label}: nothing changed.");

            var problems = new List<Problem>();
            foreach (var c in classes) StructureProblems(map, c, cand, [p.Id], days, problems);
            foreach (var l in lessons.Where(l => !PeriodDays.Applies(mask, l.Day)))
                problems.Add(new($"{l.Subject} with {l.TeacherName} is taught in {p.Label} on {ScheduleRules.DayName(l.Day)} — move or remove it first, or keep that day", l.ClassId, l.Day));
            if (!input.IsActive && lessons.Count > 0)
                foreach (var l in lessons) problems.Add(new($"{l.Subject} with {l.TeacherName} is taught in {p.Label} — move or remove the lessons before disabling it", l.ClassId, l.Day));
            var conflicts = Format(map, problems).ToList();
            if (timesChanged && input.Start is not null && input.IsActive)
            {
                var moved = new TimeSlot { Id = fork ? 0 : p.Id, Number = order, Name = name, StartTime = input.Start, EndTime = input.End };
                foreach (var l in lessons.Where(l => PeriodDays.Applies(mask, l.Day)))
                    if (await ScheduleRules.TeacherConflictAsync(db, l.TeacherId, l.TeacherName, l.Day, moved, null, l.Id, null, ct) is { } clash)
                        conflicts.Add($"{l.ClassName} · {ScheduleRules.DayName(l.Day)} at the new times: {clash}");
            }

            var target = fork ? $"{map.ClassName(classId!.Value)} only" : p.ClassId is { } ownClass ? $"{map.ClassName(ownClass)} only" : $"all {Ui.Plural(classes.Count, "class", "classes")} using it";
            var plan = new PeriodPlan
            {
                Summary = $"Change {p.Label} ({p.Times}, {p.DaysLabel}) to “{cand.Label}” ({ScheduleRules.Times(cand.Start, cand.End)}, {PeriodDays.Label(mask)}{(input.IsActive ? "" : ", disabled")}) for {target}.",
                Classes = classes.Select(map.ClassName).ToList(),
                Conflicts = conflicts,
                Lessons = lessons,
                LessonsNote = lessons.Count == 0 ? null
                    : fork ? $"{Ui.Plural(lessons.Count, "lesson moves", "lessons move")} to the class's own version of the period from this week; earlier weeks and attendance stay as they were."
                    : timesChanged ? $"{Ui.Plural(lessons.Count, "lesson stays", "lessons stay")} in this period and move to the new times." : null,
                Fingerprint = Fingerprint(fork ? "fork" : "edit", scope, classId, cand, input, p.Id, lessons.Count),
            };
            if (plan.Conflicts.Count > 0) return Blocked(plan);
            var needsConfirm = (p.ClassId is null && !fork) || (timesChanged && lessons.Count > 0);
            if (needsConfirm && confirmation != plan.Fingerprint) return Confirm(plan);

            if (!fork)
            {
                var slot = await db.TimeSlots.FirstAsync(s => s.Id == p.Id, ct);
                (slot.Number, slot.Name, slot.StartTime, slot.EndTime, slot.IsActive, slot.DayMask, slot.UpdatedAt, slot.UpdatedBy) =
                    (order, name, input.Start, input.End, input.IsActive, mask, DateTime.UtcNow, currentUser.Name);
                await db.SaveChangesAsync(ct);
                return new PeriodOutcome(ServiceResult.Ok(p.Id), plan, $"{cand.Label} saved for {target}.");
            }

            // This class only: the class gets its own version of the school period; its lessons move to it.
            var own = new TimeSlot
            {
                Number = order, Name = name, StartTime = input.Start, EndTime = input.End, IsActive = input.IsActive, DayMask = mask,
                SchoolClassId = classId, ReplacesTimeSlotId = p.Id, UpdatedAt = DateTime.UtcNow, UpdatedBy = currentUser.Name,
            };
            db.TimeSlots.Add(own);
            await db.SaveChangesAsync(ct);
            await LockTeachersAsync(lessons.Select(l => l.TeacherId), ct);
            var rows = await db.TimetableAssignments.Where(t => t.TimeSlotId == p.Id && t.SchoolClassId == classId && t.EndedOn == null).ToListAsync(ct);
            foreach (var row in rows) await MoveRowAsync(row, row.Day, own.Id, $"{p.Label} changed for {map.ClassName(classId!.Value)}", ct);
            // This and later weeks' substitutions of the class follow the lessons into the new period.
            var current = SchoolWeek.Current;
            await db.WorkloadAssignments.Where(w => w.TimeSlotId == p.Id && w.SchoolClassId == classId && w.WeekStart >= current)
                .ExecuteUpdateAsync(u => u.SetProperty(w => w.TimeSlotId, own.Id), ct);
            return new PeriodOutcome(ServiceResult.Ok(own.Id), plan, $"{cand.Label} saved for {map.ClassName(classId!.Value)} only{(rows.Count == 0 ? "" : $" ({Ui.Plural(rows.Count, "lesson", "lessons")} moved)")}.");
        }, ct);
    }

    // ================================================================== delete

    public async Task<PeriodOutcome> DeleteAsync(int id, PeriodScope scope, int? classId, bool endLessons, string? confirmation, CancellationToken ct = default)
    {
        return await InTransactionAsync(async map =>
        {
            var p = map.Get(id);
            if (p is null || p.Archived) return new PeriodOutcome(ServiceResult.Fail("", "This period no longer exists."), null);
            var removeForClass = p.ClassId is null && scope == PeriodScope.ThisClass;
            if (removeForClass && (classId is null || !map.ClassesUsing(p).Contains(classId.Value)))
                return new PeriodOutcome(ServiceResult.Fail("ClassId", classId is null ? "Choose the class." : $"{map.ClassName(classId.Value)} does not use {p.Label}."), null);

            var forClass = removeForClass ? classId : null;
            var lessons = await Refs(Current(p.Id, forClass), ct);
            var current = SchoolWeek.Current;
            var weekRows = await db.WorkloadAssignments.AsNoTracking()
                .Where(w => w.TimeSlotId == p.Id && w.WeekStart >= current && (forClass == null || w.SchoolClassId == forClass))
                .OrderBy(w => w.WeekStart).Select(w => new { w.Category, w.WeekStart, w.Day, Teacher = w.Teacher.FullName, Class = w.SchoolClass != null ? w.SchoolClass.Name : null })
                .ToListAsync(ct);

            var conflicts = new List<string>();
            foreach (var w in weekRows.Take(ListLimit))
                conflicts.Add($"{w.Teacher} has {(w.Category == WorkloadCategory.Substitution ? "a substitution" : "office hours")}{(w.Class is null ? "" : " in " + w.Class)} in this period on {ScheduleRules.DayName(w.Day!.Value)}, week of {SchoolWeek.Label(w.WeekStart)} — remove it first.");
            if (weekRows.Count > ListLimit) conflicts.Add($"…and {weekRows.Count - ListLimit} more single-week periods (substitutions / office hours) in this period.");
            if (lessons.Count > 0 && !endLessons)
                conflicts.Add($"{Ui.Plural(lessons.Count, "lesson is", "lessons are")} scheduled in {p.Label}. Move them, or tick “End these lessons” to end them from this week (earlier weeks and attendance are kept).");

            var target = removeForClass ? $"{map.ClassName(classId!.Value)} only" : p.ClassId is { } own ? $"{map.ClassName(own)}" : $"all {Ui.Plural(map.ClassesUsing(p).Count, "class", "classes")}";
            var plan = new PeriodPlan
            {
                Summary = $"Delete {p.Label} ({p.Times}, {p.DaysLabel}) from {target}.",
                Classes = map.ClassesUsing(p).Where(c => forClass is null || c == forClass).Select(map.ClassName).ToList(),
                Conflicts = conflicts,
                Lessons = lessons,
                LessonsNote = lessons.Count == 0 ? null : $"{Ui.Plural(lessons.Count, "lesson ends", "lessons end")} from this week. Earlier weeks, attendance and workload history are kept.",
                Fingerprint = Fingerprint("delete", scope, classId, new Candidate(p.Id, p.Number, p.Label, p.Start, p.End, p.IsActive, p.DayMask), null, lessons.Count, endLessons ? 1 : 0),
            };
            if (plan.Conflicts.Count > 0) return Blocked(plan);
            if (p.ClassId is null && !removeForClass && confirmation != plan.Fingerprint) return Confirm(plan);

            if (lessons.Count > 0)
            {
                await LockTeachersAsync(lessons.Select(l => l.TeacherId), ct);
                var rows = await Current(p.Id, forClass).ToListAsync(ct);
                foreach (var row in rows) End(row, $"{p.Label} deleted{(removeForClass ? $" for {map.ClassName(classId!.Value)}" : "")}");
                await db.SaveChangesAsync(ct);
            }

            var slot = await db.TimeSlots.FirstAsync(s => s.Id == p.Id, ct);
            string how;
            if (removeForClass)
            {
                // An archived replacement: the class no longer has the school period (kept as a record, can be undone).
                db.TimeSlots.Add(new TimeSlot
                {
                    Number = slot.Number, Name = slot.Name, StartTime = slot.StartTime, EndTime = slot.EndTime, DayMask = slot.DayMask, IsActive = false,
                    SchoolClassId = classId, ReplacesTimeSlotId = slot.Id, ArchivedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, UpdatedBy = currentUser.Name,
                });
                how = "removed";
            }
            else if (await UsedAsync(slot.Id, ct) || slot.ReplacesTimeSlotId is not null)
            {
                // Used before: archived so earlier weeks, lessons and attendance keep their period and times.
                (slot.ArchivedAt, slot.IsActive, slot.UpdatedAt, slot.UpdatedBy) = (DateTime.UtcNow, false, DateTime.UtcNow, currentUser.Name);
                how = "deleted (kept in the history of earlier weeks)";
            }
            else
            {
                db.TimeSlots.Remove(slot);
                how = "deleted";
            }
            await db.SaveChangesAsync(ct);
            return new PeriodOutcome(ServiceResult.Ok(p.Id), plan,
                $"{p.Label} {how} for {target}.{(lessons.Count == 0 ? "" : $" {Ui.Plural(lessons.Count, "lesson", "lessons")} ended from this week.")}");
        }, ct);
    }

    // ================================================================== restore

    public async Task<ServiceResult> RestoreAsync(int replacementId, CancellationToken ct = default)
    {
        var outcome = await InTransactionAsync(async map =>
        {
            var o = map.Get(replacementId);
            if (o is not { ClassId: { } classId, ReplacesId: { } schoolId }) return new PeriodOutcome(ServiceResult.Fail("", "This class has no own version of a school period here."), null);
            var p = map.Get(schoolId);
            if (p is null || p.Archived) return new PeriodOutcome(ServiceResult.Fail("", "The school period no longer exists."), null);
            if (await Current(o.Id, null).AnyAsync(ct))
                return new PeriodOutcome(ServiceResult.Fail("", $"{map.ClassName(classId)}'s {o.Label} still has lessons. Move or remove them first."), null);

            // Check the school period against the class's other periods (without its own version).
            var problems = new List<Problem>();
            var cand = new Candidate(p.Id, p.Number, p.Label, p.Start, p.End, p.IsActive, p.DayMask);
            StructureProblems(map, classId, cand, [o.Id, p.Id], PeriodDays.Days(p.DayMask), problems);
            var conflicts = Format(map, problems);
            if (conflicts.Count > 0)
            {
                var fail = ServiceResult.Fail("", $"{p.Label} cannot return to {map.ClassName(classId)}:");
                foreach (var c in conflicts) fail.AddError("", c);
                return new PeriodOutcome(fail, null);
            }

            var slot = await db.TimeSlots.FirstAsync(s => s.Id == o.Id, ct);
            if (await UsedAsync(slot.Id, ct))
            {
                (slot.ReplacesTimeSlotId, slot.IsActive, slot.UpdatedAt, slot.UpdatedBy) = (null, false, DateTime.UtcNow, currentUser.Name);
                slot.ArchivedAt ??= DateTime.UtcNow;
            }
            else db.TimeSlots.Remove(slot);
            await db.SaveChangesAsync(ct);
            return new PeriodOutcome(ServiceResult.Ok(p.Id), null);
        }, ct);
        return outcome.Result;
    }

    // ================================================================== validation

    private static (ServiceResult Result, string? Name) Check(PeriodInput i, bool requireTimes, bool hadTimes)
    {
        var r = ServiceResult.Ok();
        var name = TextNormalizer.CleanText(i.Name);
        if (name.Length > TimeSlot.NameMaxLength) r.AddError("Name", $"Use {TimeSlot.NameMaxLength} characters or fewer.");
        if (requireTimes || hadTimes)
        {
            if (i.Start is null) r.AddError("StartTime", "Enter the start time.");
            if (i.End is null) r.AddError("EndTime", "Enter the end time.");
        }
        else if (i.Start is null != i.End is null) r.AddError(i.Start is null ? "StartTime" : "EndTime", "Enter both the start and the end time, or leave both empty.");
        if (i.Start is { } s && i.End is { } e && e <= s) r.AddError("EndTime", "The end time must be later than the start time.");
        if (i.Order is { } o && (o < 1 || o > TimeSlot.MaxOrder)) r.AddError("Order", $"Enter a display order from 1 to {TimeSlot.MaxOrder}.");
        if (!i.AllDays && !(i.Days ?? []).Any(ScheduleRules.IsSchoolDay)) r.AddError("Days", "Choose at least one school day, or apply the period to all school days.");
        return (r, name.Length == 0 ? null : name);
    }

    /// <summary>
    /// The candidate in one class's timetable on each day it runs: no overlap with the class's other enabled periods,
    /// no second period of the same name, and a display order that follows the start times.
    /// </summary>
    private static void StructureProblems(PeriodMap map, int classId, Candidate c, IReadOnlyCollection<int> exclude, IEnumerable<DayOfWeek> days, List<Problem> into)
    {
        if (!c.IsActive) return;
        foreach (var day in days)
            foreach (var o in map.ForClassDay(classId, day))
            {
                if (exclude.Contains(o.Id) || o.Id == c.Id) continue;
                if (c.Start is { } cs && c.End is { } ce && o.Start is { } os && o.End is { } oe)
                {
                    if (cs < oe && os < ce) { into.Add(new($"overlaps {o.Label} ({o.Times})", classId, day)); continue; }
                    if ((c.Number < o.Number && cs > os) || (c.Number > o.Number && cs < os))
                    {
                        into.Add(new($"display order {c.Number} would show it {(c.Number < o.Number ? "before" : "after")} {o.Label} ({o.Times}) although it starts {(cs > os ? "later" : "earlier")} — change the order", classId, day));
                        continue;
                    }
                }
                if (string.Equals(o.Label, c.Label, StringComparison.OrdinalIgnoreCase))
                    into.Add(new($"already has a period called “{o.Label}” — give this one another name", classId, day));
            }
    }

    /// <summary>A display order that keeps the time order: after the periods that start earlier (last + 1 when it is the latest).</summary>
    private static int AutoOrder(PeriodMap map, IReadOnlyList<int> classes, IReadOnlyList<DayOfWeek> days, TimeOnly start)
    {
        var relevant = classes.SelectMany(c => days.SelectMany(d => map.ForClass(c).Where(s => s.AppliesOn(d)))).DistinctBy(s => s.Id).ToList();
        if (relevant.Count == 0) return 1;
        var later = relevant.Where(s => s.Start is { } st && st > start).ToList();
        if (later.Count == 0) return Math.Min(TimeSlot.MaxOrder, relevant.Max(s => s.Number) + 1);
        var before = relevant.Where(s => s.Start is { } st && st < start).ToList();
        return before.Count > 0 ? before.Max(s => s.Number) : Math.Max(1, later.Min(s => s.Number) - 1);
    }

    /// <summary>"KG1A (Sun, Tue), KG1B (Sun) — overlaps Break (10:15–10:30)", one line per kind of problem.</summary>
    private static IReadOnlyList<string> Format(PeriodMap map, List<Problem> problems) =>
        problems.GroupBy(p => p.Message).Select(g =>
        {
            var byClass = g.GroupBy(p => p.ClassId).Select(c => $"{map.ClassName(c.Key)} ({string.Join(", ", c.Select(x => x.Day).Distinct().OrderBy(d => d).Select(ScheduleRules.DayShort))})").ToList();
            var list = string.Join(", ", byClass.Take(ListLimit)) + (byClass.Count > ListLimit ? $" and {byClass.Count - ListLimit} more classes" : "");
            return $"{list} — {g.Key}.";
        }).ToList();

    private static string Fingerprint(string op, PeriodScope scope, int? classId, Candidate c, PeriodInput? i, params int[] extra)
    {
        var text = string.Join("|", op, scope, classId, c.Id, c.Number, c.Label, c.Start, c.End, c.IsActive, c.DayMask, i?.Order, string.Join(",", extra));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static PeriodOutcome Blocked(PeriodPlan plan)
    {
        var r = ServiceResult.Fail("", "Nothing was changed. This change cannot be applied because:");
        foreach (var c in plan.Conflicts) r.AddError("", c);
        return new(r, plan);
    }

    private static PeriodOutcome Confirm(PeriodPlan plan) =>
        new(ServiceResult.Fail(ConfirmKey, "Review the changes below, then confirm to apply them."), plan);

    // ================================================================== data helpers

    private IQueryable<TimetableAssignment> Current(int slotId, int? classId) =>
        db.TimetableAssignments.Where(t => t.TimeSlotId == slotId && t.EndedOn == null && (classId == null || t.SchoolClassId == classId));

    private static async Task<IReadOnlyList<LessonRef>> Refs(IQueryable<TimetableAssignment> q, CancellationToken ct) =>
        await q.AsNoTracking().OrderBy(t => t.SchoolClass.SortOrder).ThenBy(t => t.SchoolClass.Name).ThenBy(t => t.Day)
            .Select(t => new LessonRef(t.Id, t.SchoolClassId, t.SchoolClass.Name, t.Day, t.Subject, t.TeacherId, t.Teacher.FullName)).ToListAsync(ct);

    /// <summary>Was the period ever used (lessons, single-week periods, a class's own version)? Then it is archived, never removed.</summary>
    private async Task<bool> UsedAsync(int slotId, CancellationToken ct) =>
        await db.TimetableAssignments.AnyAsync(t => t.TimeSlotId == slotId, ct)
        || await db.WorkloadAssignments.AnyAsync(w => w.TimeSlotId == slotId, ct)
        || await db.TimeSlots.AnyAsync(s => s.ReplacesTimeSlotId == slotId, ct);

    private async Task LockTeachersAsync(IEnumerable<int> teacherIds, CancellationToken ct)
    {
        foreach (var t in teacherIds.Distinct().Order()) await ScheduleRules.LockTeacherAsync(db, t, ct);
    }

    private void End(TimetableAssignment session, string reason) => TimetableRows.End(session, reason, currentUser.Name);

    private Task MoveRowAsync(TimetableAssignment row, DayOfWeek day, int slotId, string reason, CancellationToken ct) =>
        TimetableRows.MoveAsync(db, row, day, slotId, reason, currentUser.Name, ct);

    /// <summary>Runs the change in one transaction holding the period lock; anything not committed is rolled back.</summary>
    private async Task<PeriodOutcome> InTransactionAsync(Func<PeriodMap, Task<PeriodOutcome>> work, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await ScheduleRules.LockPeriodsAsync(db, exclusive: true, ct);
            var outcome = await work(await PeriodMap.LoadAsync(db, ct));
            if (outcome.Result.Succeeded) await tx.CommitAsync(ct);
            return outcome;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: WorkloadService.LimitErrorNumber })
        {
            db.ChangeTracker.Clear();
            return new(ServiceResult.Fail("", "Nothing was changed: a teacher of these lessons is above the weekly maximum. Resolve their workload first."), null);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return new(ServiceResult.Fail("", "Nothing was changed: the timetable was just changed by someone else. Refresh and try again."), null);
        }
        catch (SqlException ex) when (ex.Number == 51001)
        {
            db.ChangeTracker.Clear();
            return new(ServiceResult.Fail("", "Someone else is changing the timetable right now. Try again in a moment."), null);
        }
    }
}

/// <summary>Row operations shared by the timetable and period management: ending a lesson, and moving it (end + new row).</summary>
public static class TimetableRows
{
    /// <summary>Ends the session from the current week (kept as history; earlier weeks still count it).</summary>
    public static void End(TimetableAssignment session, string reason, string? by)
    {
        var current = SchoolWeek.Current;
        session.EndedOn = current < session.StartsOn ? session.StartsOn : current;
        session.EndReason = reason.Length > 200 ? reason[..200] : reason;
        session.UpdatedBy = by;
    }

    /// <summary>
    /// Moves a lesson to another day / period from this week: the old row ends (history, attendance and
    /// earlier weeks unchanged) and a new row starts, keeping subject, teacher, notes, P.E. details and —
    /// on the same weekday — upcoming cancellations. The week's lesson count stays the same.
    /// </summary>
    public static async Task<TimetableAssignment> MoveAsync(SchoolDbContext db, TimetableAssignment row, DayOfWeek day, int slotId, string reason, string? by, CancellationToken ct)
    {
        var starts = SchoolWeek.Current < row.StartsOn ? row.StartsOn : SchoolWeek.Current;
        End(row, reason, by);
        await db.SaveChangesAsync(ct);
        var copy = new TimetableAssignment
        {
            SchoolClassId = row.SchoolClassId, Day = day, TimeSlotId = slotId, DepartmentId = row.DepartmentId, Subject = row.Subject,
            TeacherId = row.TeacherId, Notes = row.Notes, StartsOn = starts, CreatedBy = by, UpdatedBy = by,
        };
        db.TimetableAssignments.Add(copy);
        await db.SaveChangesAsync(ct);

        var pe = await db.PeSessionDetails.AsNoTracking().Include(d => d.Equipment).FirstOrDefaultAsync(d => d.TimetableAssignmentId == row.Id, ct);
        if (pe is not null)
            db.PeSessionDetails.Add(new PeSessionDetail
            {
                TimetableAssignmentId = copy.Id, CoTeacherId = pe.CoTeacherId, Facility = pe.Facility, Activity = pe.Activity, Notes = pe.Notes, UpdatedBy = by,
                Equipment = pe.Equipment.Select(e => new PeEquipmentItem { Name = e.Name, IsChecked = e.IsChecked, SortOrder = e.SortOrder }).ToList(),
            });
        if (day == row.Day)
        {
            var today = SchoolWeek.Today;
            foreach (var c in await db.SessionCancellations.AsNoTracking().Where(c => c.TimetableAssignmentId == row.Id && c.SessionDate >= today).ToListAsync(ct))
                db.SessionCancellations.Add(new SessionCancellation { TimetableAssignmentId = copy.Id, SessionDate = c.SessionDate, Reason = c.Reason, CancelledBy = c.CancelledBy, CancelledAt = c.CancelledAt });
        }
        await db.SaveChangesAsync(ct);
        return copy;
    }
}
