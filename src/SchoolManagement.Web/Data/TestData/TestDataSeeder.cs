using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.Data.TestData;

/// <summary>The "Working as" name the services record while the seeder runs (the person who would do it).</summary>
public sealed class TestDataUser : ICurrentUser
{
    public string? Name { get; set; } = "School Administration";
    public int? StaffId { get; set; }
    public bool IsVerified => false;
}

/// <summary>
/// Adds a realistic, connected school to the current database next to the records already there:
/// staff, students, HODs, Management Team and tasks, timetables, P.E. details, workload, duties with
/// backups and evaluations, arrivals and belongings. Everything goes through the application services
/// (same validation, conflict checks, weekly limits and history as the UI). Existing records are reused
/// by reference only and never changed (the tracker refuses any change to them).
/// Work is split into stages; a finished stage is recorded and skipped on the next run, and a stage
/// that did not finish has its rows removed and is redone, so running the seeder again never duplicates.
/// </summary>
public sealed partial class TestDataSeeder(IServiceProvider root, TestDataTracker tracker, TestDataUser user, TextWriter log)
{
    private const int Seed = 20261111;
    private Random _rng = new(Seed);
    private IServiceScope? _scope;
    private int _ops;
    public List<string> Skipped { get; } = [];
    public List<string> Notes { get; } = [];

    private readonly DateOnly _today = SchoolWeek.Today;
    private readonly DateOnly _week0 = SchoolWeek.Current;

    private Dictionary<string, int> _dept = [];
    private List<(int Id, string Name, string Stage)> _classes = [];
    private Dictionary<int, (TimeOnly Start, TimeOnly End)> _slotTimes = [];   // slot number → times (planned when not set)
    private Dictionary<int, int> _slotIds = [];                               // slot number → id
    private List<Staff> _teachers = [];
    private List<Staff> _management = [];
    private List<(int Id, string Name, int ClassId)> _students = [];
    private List<Lesson> _lessons = [];          // generated timetable sessions
    private List<Lesson> _existingLessons = [];  // sessions that were already there (kept, never changed)
    private List<DutyMade> _duties = [];

    private sealed record Staff(int Id, string Name, string Dept, string Teaches, ManagementPosition? Position = null);
    private sealed record Lesson(int ClassId, int Day, int Slot, string Dept, string Subject, int TeacherId);
    private sealed record DutyMade(int Id, DateOnly Date, DutyComponent Component, string Location, TimeOnly Start, TimeOnly End,
        int Primary, AttendanceStatus PrimaryAtt, int? Backup, bool BackupPresent);

    // ------------------------------------------------------------------ plumbing

    private T S<T>() where T : notnull => _scope!.ServiceProvider.GetRequiredService<T>();
    private SchoolDbContext Db => S<SchoolDbContext>();

    private void NewScope()
    {
        _scope?.Dispose();
        _scope = root.CreateScope();
    }

    private void Tick()
    {
        if (++_ops % 150 == 0) NewScope();   // keeps the change tracker small
    }

    private async Task<int> Must(Func<Task<ServiceResult>> call, string what)
    {
        var r = await call();
        Tick();
        if (!r.Succeeded) throw new InvalidOperationException($"{what} failed: {string.Join(" ", r.Errors.Select(e => e.Value))}");
        return r.EntityId ?? 0;
    }

    private async Task<int?> Try(Func<Task<ServiceResult>> call, string what)
    {
        var r = await call();
        Tick();
        if (r.Succeeded) return r.EntityId ?? 0;
        Skipped.Add($"{what}: {string.Join(" ", r.Errors.Select(e => e.Value))}");
        return null;
    }

    private T Pick<T>(IReadOnlyList<T> items) => items[_rng.Next(items.Count)];
    private bool Chance(double p) => _rng.NextDouble() < p;
    private static bool IsSchoolDay(DateOnly d) => ScheduleRules.IsSchoolDay(d.DayOfWeek);
    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) if (IsSchoolDay(d)) yield return d;
    }

    private void Step(string text) => log.WriteLine($"  · {text}");

    // ------------------------------------------------------------------ stages

    private (string Name, Func<Task> Run)[] Stages =>
    [
        ("settings", SettingsAsync),
        ("people", PeopleAsync),
        ("hods", HodsAsync),
        ("timetable", TimetableAsync),
        ("pe", PeDetailsAsync),
        ("duties", DutiesAsync),
        ("feedback", FeedbackAsync),
        ("workload", WorkloadAsync),
        ("arrivals", ArrivalsAsync),
        ("tasks", TasksAsync),
        ("belongings", BelongingsAsync),
        ("timestamps", BackdateAsync),
        // Two-level attendance and incident reports (TestDataSeeder.Attendance.cs / .Incidents.cs).
        ("school-attendance", SchoolAttendanceScenariosAsync),
        ("session-attendance", SessionAttendanceAsync),
        ("alert-followups", AlertFollowUpsAsync),
        ("incidents", IncidentsAsync),
    ];

    public async Task RunAsync()
    {
        NewScope();
        var registry = await TestDataTracker.ReadAsync(Db);
        // Tables added after the first seed (attendance, incidents) get their own baseline before anything is added.
        await TestDataTracker.RecordBaselineAsync(Db, registry.Where(r => r.Kind == "B").Select(r => r.EntityType).ToHashSet());
        tracker.Remember(registry.Where(r => r.Kind == "A").Select(r => (r.EntityType, r.KeyJson)));
        var done = registry.Where(r => r.Kind == "P").Select(r => r.Phase).ToHashSet();

        var stages = Stages;
        for (var i = 0; i < stages.Length; i++)
        {
            var (name, run) = stages[i];
            if (done.Contains(name))
            {
                Step($"{name}: already in place");
                continue;
            }
            await UndoUnfinishedAsync(name);
            tracker.Phase = name;
            _rng = new Random(Seed + i * 7919);
            NewScope();
            await LoadAsync();
            await run();
            NewScope();
            await TestDataTracker.InsertAsync(Db.Database, [(name, "P", "stage", name, null)]);
        }
        _scope!.Dispose();
    }

    /// <summary>Rows of a stage that started earlier but did not finish are removed, so the stage is redone cleanly.</summary>
    private async Task UndoUnfinishedAsync(string stage)
    {
        NewScope();
        var registry = await TestDataTracker.ReadAsync(Db);
        var rows = registry.Where(r => r.Kind == "A" && r.Phase == stage).ToList();
        if (rows.Count == 0) return;
        await using var tx = await Db.Database.BeginTransactionAsync();
        var deleted = await TestDataTracker.DeleteGeneratedAsync(Db, rows, registry.Where(r => r.Kind == "B").ToList());
        var sql = $"DELETE FROM dbo.{TestDataTracker.Table} WHERE Kind = 'A' AND Phase = {{0}}";   // stage name is a parameter
        await Db.Database.ExecuteSqlRawAsync(sql, stage);
        await tx.CommitAsync();
        tracker.Forget(rows.Select(r => (r.EntityType, r.KeyJson)));
        Step($"{stage}: removed {deleted} rows of an unfinished earlier run; redoing it");
    }

    /// <summary>Reference data and everything generated by earlier stages (also from earlier runs).</summary>
    private async Task LoadAsync()
    {
        _dept = await Db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Name, d => d.Id);
        foreach (var name in Curriculum.SelectMany(c => c.Value).Select(s => s.Dept).Distinct())
            if (!_dept.ContainsKey(name)) throw new InvalidOperationException($"Department \"{name}\" is missing. The test data expects the standard departments.");
        _classes = (await Db.Classes.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).Select(c => new { c.Id, c.Name }).ToListAsync())
            .Select(c => (c.Id, c.Name, Stage: StageOf(c.Name))).Where(c => c.Stage != "").ToList();
        if (_classes.Count == 0) throw new InvalidOperationException("No active classes (PreK, KG1A … G10B) found.");

        var slots = await Db.TimeSlots.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Number).ToListAsync();
        _slotIds = slots.ToDictionary(s => s.Number, s => s.Id);
        // The school's own period times when set; otherwise a typical day (used only to keep duties out of lessons).
        var start = new TimeOnly(7, 45);
        var planned = new Dictionary<int, (TimeOnly, TimeOnly)>();
        for (var n = 1; n <= TimeSlot.Count; n++)
        {
            if (n is 4 or 7) start = start.AddMinutes(20);
            planned[n] = (start, start.AddMinutes(45));
            start = start.AddMinutes(45);
        }
        _slotTimes = slots.ToDictionary(s => s.Number, s => s.StartTime is { } a && s.EndTime is { } b ? (a, b) : planned[s.Number]);

        var ids = (await TestDataTracker.ReadAsync(Db)).Where(r => r.Kind == "A")
            .Select(r => (r.EntityType, Key: JsonSerializer.Deserialize<JsonElement[]>(r.KeyJson)![0]))
            .Where(r => r.Key.ValueKind == JsonValueKind.Number)
            .GroupBy(r => r.EntityType).ToDictionary(g => g.Key, g => g.Select(r => (int)r.Key.GetInt64()).ToList());
        List<int> Gen<T>() => ids.GetValueOrDefault(typeof(T).FullName!) ?? [];

        var teacherIds = Gen<Teacher>();
        _teachers = (await Db.Teachers.AsNoTracking().Where(t => teacherIds.Contains(t.Id)).OrderBy(t => t.Id)
                .Select(t => new { t.Id, t.FullName, Dept = t.Department!.Name, t.Subcategory, t.Subject }).ToListAsync())
            .Select(t => new Staff(t.Id, t.FullName, t.Dept, t.Subject ?? t.Subcategory ?? t.Dept)).ToList();
        var managerIds = Gen<ManagementMember>();
        _management = (await Db.ManagementMembers.AsNoTracking().Where(m => managerIds.Contains(m.Id)).OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.FullName, m.Position }).ToListAsync()).Select(m => new Staff(m.Id, m.FullName, "", "", m.Position)).ToList();
        var studentIds = Gen<Student>();
        _students = (await Db.Students.AsNoTracking().Where(s => studentIds.Contains(s.Id)).OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.FullName, s.SchoolClassId }).ToListAsync()).Select(s => (s.Id, s.FullName, s.SchoolClassId)).ToList();

        var sessionIds = Gen<TimetableAssignment>().ToHashSet();
        var slotNumber = slots.ToDictionary(s => s.Id, s => s.Number);
        var sessions = await Db.TimetableAssignments.AsNoTracking().Where(t => t.EndedOn == null)
            .Select(t => new { t.Id, t.SchoolClassId, t.Day, t.TimeSlotId, Dept = t.Department.Name, t.Subject, t.TeacherId }).ToListAsync();
        _lessons = sessions.Where(t => sessionIds.Contains(t.Id)).Select(t => new Lesson(t.SchoolClassId, (int)t.Day, slotNumber.GetValueOrDefault(t.TimeSlotId), t.Dept, t.Subject, t.TeacherId)).ToList();
        _existingLessons = sessions.Where(t => !sessionIds.Contains(t.Id)).Select(t => new Lesson(t.SchoolClassId, (int)t.Day, slotNumber.GetValueOrDefault(t.TimeSlotId), t.Dept, t.Subject, t.TeacherId)).ToList();

        var dutyIds = Gen<DutyAssignment>();
        _duties = (await Db.DutyAssignments.AsNoTracking().Where(d => dutyIds.Contains(d.Id)).OrderBy(d => d.Id).ToListAsync())
            .Select(d => new DutyMade(d.Id, d.DutyDate, d.Component, d.Location ?? "", d.StartTime, d.EndTime, d.PrimaryStaffId, d.PrimaryAttendance,
                d.BackupStaffId, d.BackupStatus == BackupRequestStatus.Accepted && d.BackupAttendance == AttendanceStatus.Present)).ToList();
    }

    private static string StageOf(string className) => GradeCatalog.KeyOf(className) switch
    {
        "PREK" or "KG1" or "KG2" => "KG",
        "G1" or "G2" or "G3" or "G4" or "G5" => "Primary",
        "G6" or "G7" or "G8" or "G9" or "G10" => "Upper",
        _ => "",
    };

    private DateOnly Q1Start => _week0.AddDays(-35);
    private string YearName()
    {
        var y = Q1Start.Month >= 8 ? Q1Start.Year : Q1Start.Year - 1;
        return $"{y}–{y + 1}";
    }

    private Staff Mgmt(ManagementPosition p) =>
        _management.FirstOrDefault(m => m.Position == p) ?? _management.FirstOrDefault(m => m.Position is not null) ?? new Staff(0, "School Administration", "", "");

    // ------------------------------------------------------------------ settings

    private async Task SettingsAsync()
    {
        // Settings are only added when the school has none yet (an existing setting is never changed).
        if (await Db.Settings.AnyAsync())
        {
            Notes.Add("Settings: the school already has settings, so none were added.");
            Step("Settings already configured — left as they are");
            return;
        }
        var settings = S<ISettingsService>();
        var profile = await settings.GetProfileAsync();
        await Must(() => settings.SaveProfileAsync(profile.SchoolName, YearName()), "Academic year label");
        await Must(() => S<ISettingsService>().SaveArrivalDeadlineAsync(new TimeOnly(7, 40)), "Arrival deadline");
        Step($"Academic year label {YearName()} and arrival deadline 07:40 (school name unchanged)");
    }

    // ------------------------------------------------------------------ people

    private static readonly string[] Boys =
    [
        "Omar", "Youssef", "Adam", "Ali", "Karim", "Ziad", "Hamza", "Mostafa", "Ahmed", "Mohamed", "Seif", "Yassin", "Malek", "Hassan",
        "Marwan", "Khaled", "Tarek", "Fares", "Ibrahim", "Mazen", "Hussein", "Amr", "Abdelrahman", "Eyad", "Rayan", "Selim", "Taha",
        "Yahia", "Daniel", "Mark", "Peter", "George", "Mina", "Andrew", "Kirollos", "Ramy", "Sherif", "Hady", "Nader", "Belal",
    ];
    private static readonly string[] Girls =
    [
        "Laila", "Malak", "Jana", "Farida", "Salma", "Hana", "Nour", "Mariam", "Habiba", "Yasmin", "Rania", "Lina", "Judy", "Sara", "Aya",
        "Dina", "Rokaya", "Retaj", "Talia", "Celine", "Carma", "Marina", "Mira", "Nada", "Reem", "Heba", "Shahd", "Tala", "Kenzy", "Layan",
        "Hala", "Yara", "Nadine", "Rahma", "Lojain", "Maya", "Haya", "Sandy", "Veronica", "Mona",
    ];
    private static readonly string[] Families =
    [
        "Hassan", "Adel", "Sami", "Fawzy", "Tarek", "Mansour", "El-Sayed", "Abdelaziz", "Ramadan", "Kamal", "Nabil", "Soliman", "Farouk",
        "Gamal", "Hamdy", "Shawky", "Zaki", "Ezzat", "Sabry", "Hegazy", "Lotfy", "Fathy", "Saber", "Barakat", "El-Masry", "Khalil",
        "Naguib", "Wahba", "Girgis", "Tawfik", "Abdallah", "Rashad", "Helmy", "Saad", "Fouad", "Anwar", "El-Gendy", "Morsy", "Shehata",
        "Salem", "Ashraf", "Raafat", "Magdy", "Ghoneim", "Osman", "Hafez", "Abbas", "Nassar", "Zaher", "Rizk",
    ];

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _phones = [];

    private string NewName(bool threePart)
    {
        while (true)
        {
            var first = Chance(0.5) ? Pick(Boys) : Pick(Girls);
            var name = threePart ? $"{first} {Pick(Boys)} {Pick(Families)}" : $"{first} {Pick(Families)}";
            if (_names.Add(name)) return name;
        }
    }

    private string NewPhone()
    {
        while (true)
        {
            var p = $"01{Pick(new[] { "0", "1", "2", "5" })}{_rng.Next(1000, 9999)} {_rng.Next(1000, 9999)}";
            if (_phones.Add(new string(p.Where(char.IsDigit).ToArray()))) return p;
        }
    }

    /// <summary>(Department, subcategory, subject, how many, job title). Teaches = subject ?? subcategory ?? department.</summary>
    private static readonly (string Dept, string? Sub, string? Subject, int Count, string Title)[] Roster =
    [
        ("English", null, null, 9, "English Teacher"),
        ("Arabic", null, null, 8, "Arabic Teacher"),
        ("Math", null, null, 8, "Math Teacher"),
        ("Science", null, null, 6, "Science Teacher"),
        ("ASS", null, null, 3, "Social Studies Teacher (ASS)"),
        ("ESS", null, null, 2, "Social Studies Teacher (ESS)"),
        ("Religion", "Islamic", "Religion", 2, "Islamic Religion Teacher"),
        ("Religion", "Islamic", "Quran", 2, "Quran Teacher"),
        ("Religion", "Christian", "Christian Religion", 1, "Christian Religion Teacher"),
        ("PE", null, null, 3, "P.E. Teacher"),
        ("Art", null, null, 2, "Art Teacher"),
        ("Music", null, null, 2, "Music Teacher"),
        ("Library / Reading", null, null, 2, "Librarian"),
        ("Drama", null, null, 1, "Drama Teacher"),
        ("ICT", "ICT", null, 2, "ICT Teacher"),
        ("ICT", "AI", null, 1, "AI & Robotics Teacher"),
        ("Second Language", "French", null, 2, "French Teacher"),
        ("Second Language", "Deutsch", null, 1, "German Teacher"),
        ("WSD", "Wellbeing", null, 1, "Wellbeing Counsellor"),
        ("WSD", "Character Building", null, 1, "Character Education Teacher"),
        ("WSD", "Support", null, 1, "Learning Support Specialist"),
        ("WSD", "Discipline", null, 1, "Student Affairs Officer"),
    ];

    private static readonly ManagementPosition[] Positions =
    [
        ManagementPosition.SchoolDirector, ManagementPosition.Sqam, ManagementPosition.KgStageCoordinator, ManagementPosition.LowerElementaryCoordinator,
        ManagementPosition.UpperElementaryCoordinator, ManagementPosition.MiddleSchoolCoordinator, ManagementPosition.HighSchoolCoordinator,
    ];

    private async Task PeopleAsync()
    {
        foreach (var n in await Db.Students.AsNoTracking().Select(s => s.FullName).ToListAsync()) _names.Add(n);
        foreach (var n in await Db.StaffMembers.AsNoTracking().Select(s => s.FullName).ToListAsync()) _names.Add(n);
        foreach (var c in await Db.StaffMembers.AsNoTracking().Where(s => s.NormalizedContact != "").Select(s => s.NormalizedContact).ToListAsync()) _phones.Add(c);

        foreach (var (dept, sub, subject, count, title) in Roster)
        {
            for (var i = 0; i < count; i++)
            {
                var name = NewName(false);
                var job = i == 0 && count >= 6 ? "Senior " + title : title;
                var id = await Must(() => S<ITeacherService>().CreateAsync(name, job, NewPhone(), _dept[dept], sub, subject), $"Teacher {name}");
                _teachers.Add(new Staff(id, name, dept, subject ?? sub ?? dept));
            }
        }

        // Management Team: positions not held yet (an existing holder is kept), plus a former High School
        // coordinator who handed the position over this year.
        var held = (await Db.PositionAssignments.AsNoTracking().Where(p => p.IsCurrent).Select(p => p.Position).ToListAsync()).ToHashSet();
        Staff? hs = null;
        foreach (var position in Positions)
        {
            var name = NewName(false);
            var free = !held.Contains(position);
            var input = new ManagementMemberInput
            {
                FullName = name, Position = free && position != ManagementPosition.HighSchoolCoordinator ? position : null,
                ContactNumber = NewPhone(), Email = Email(name), AppointedOn = new DateOnly(2019 + _rng.Next(0, 6), 8, 25),
            };
            var id = await Must(() => S<IManagementService>().CreateAsync(input), $"Management member {name}");
            var member = new Staff(id, name, "", "", free ? position : null);
            _management.Add(member);
            if (position == ManagementPosition.HighSchoolCoordinator && free) hs = member;
        }
        var former = NewName(false);
        var formerId = await Must(() => S<IManagementService>().CreateAsync(new ManagementMemberInput
        {
            FullName = former, ContactNumber = NewPhone(), Email = Email(former), AppointedOn = new DateOnly(2018, 8, 26),
        }), "Former coordinator");
        if (hs is not null)
        {
            await Must(() => S<IManagementService>().ReassignPositionAsync(ManagementPosition.HighSchoolCoordinator, formerId, new DateOnly(2021, 9, 5)), "High School coordinator (former)");
            await Must(() => S<IManagementService>().ReassignPositionAsync(ManagementPosition.HighSchoolCoordinator, hs.Id, Q1Start.AddDays(-35)), "High School coordinator (current)");
        }
        _management.Add(new Staff(formerId, former, "", "", null));

        // Students: 12 in Pre-K, 14 in KG, 15–19 from Grade 1; never above a class's capacity.
        var capacity = await Db.Classes.AsNoTracking().ToDictionaryAsync(c => c.Id, c => new { c.Capacity, Count = c.Students.Count });
        foreach (var (classId, className, stage) in _classes)
        {
            var count = className == "PreK" ? 12 : stage == "KG" ? 14 : 15 + _rng.Next(0, 5);
            if (className == "G9A") count = 20;
            if (className == "G6B") count = 23;
            count = Math.Min(count, capacity[classId].Capacity - capacity[classId].Count);
            for (var i = 0; i < count; i++)
            {
                var name = NewName(true);
                var id = await Must(() => S<IStudentService>().CreateAsync(name, classId), $"Student {name}");
                _students.Add((id, name, classId));
            }
        }

        // A few class transfers (history on the student profile).
        var transfers = 0;
        foreach (var (from, to) in new[] { ("G4A", "G4B"), ("G7B", "G7A"), ("KG1A", "KG1B") })
        {
            var fromClass = _classes.FirstOrDefault(c => c.Name == from);
            var toClass = _classes.FirstOrDefault(c => c.Name == to);
            if (fromClass.Id == 0 || toClass.Id == 0) continue;
            var s = _students.FirstOrDefault(x => x.ClassId == fromClass.Id);
            if (s.Id == 0 || await Try(() => S<IStudentService>().TransferClassAsync(s.Id, toClass.Id, "Registrar's Office"), $"Transfer {s.Name}") is null) continue;
            _students[_students.IndexOf(s)] = (s.Id, s.Name, toClass.Id);
            transfers++;
        }
        Step($"{_teachers.Count} teachers, {_management.Count} Management Team members, {_students.Count} students, {transfers} class transfers");
    }

    private static string Email(string name) => name.ToLowerInvariant().Replace(' ', '.').Replace("-", "") + "@nilevalley.example";

    // ------------------------------------------------------------------ heads of department

    private async Task HodsAsync()
    {
        var withHead = (await Db.DepartmentHeads.AsNoTracking().Where(h => h.IsCurrent).Select(h => h.DepartmentId).ToListAsync()).ToHashSet();
        var added = 0;
        foreach (var dept in new[] { "English", "Arabic", "Math", "Science", "Religion", "PE", "ICT", "Second Language", "WSD", "ASS" })
        {
            if (withHead.Contains(_dept[dept])) { Notes.Add($"HOD: {dept} already has a Head of Department (kept)."); continue; }
            var members = _teachers.Where(t => t.Dept == dept).ToList();
            if (members.Count == 0) continue;
            if (dept is "English" or "Math" && members.Count > 1)
            {
                var earlier = members[1];
                await Must(() => S<IDepartmentService>().AssignHeadAsync(_dept[dept], earlier.Id, new DateOnly(2023, 9, 3), "Appointed by the School Director."), $"Earlier HOD of {dept}");
                await Must(() => S<IDepartmentService>().AssignHeadAsync(_dept[dept], members[0].Id, Q1Start.AddDays(-28),
                    $"{earlier.Name} moved to a full teaching timetable; handover completed in August."), $"HOD of {dept}");
            }
            else
            {
                await Must(() => S<IDepartmentService>().AssignHeadAsync(_dept[dept], members[0].Id, new DateOnly(2022 + _rng.Next(0, 3), 9, 4), null), $"HOD of {dept}");
            }
            added++;
        }
        Step($"{added} Heads of Department (English and Math with an earlier HOD in history)");
    }

    // ------------------------------------------------------------------ timetable

    private sealed record Subj(string Dept, string Subject, int Periods, bool Core);

    private static readonly Dictionary<string, Subj[]> Curriculum = new()
    {
        ["KG"] =
        [
            new("English", "English", 6, true), new("Arabic", "Arabic", 5, true), new("Math", "Math", 4, true), new("Science", "Science", 2, false),
            new("Religion", "Religion", 1, false), new("Religion", "Quran", 1, false), new("PE", "PE", 2, false), new("Art", "Art", 1, false),
            new("Music", "Music", 1, false), new("Library / Reading", "Library / Reading", 1, false), new("Drama", "Drama", 1, false),
        ],
        ["Primary"] =
        [
            new("English", "English", 7, true), new("Arabic", "Arabic", 6, true), new("Math", "Math", 6, true), new("Science", "Science", 4, true),
            new("ASS", "ASS", 2, false), new("Religion", "Religion", 1, false), new("Religion", "Quran", 1, false), new("PE", "PE", 2, false),
            new("Art", "Art", 1, false), new("Music", "Music", 1, false), new("ICT", "ICT", 1, false), new("Second Language", "French", 1, false),
            new("Library / Reading", "Library / Reading", 1, false), new("WSD", "Wellbeing", 1, false),
        ],
        ["Upper"] =
        [
            new("English", "English", 6, true), new("Arabic", "Arabic", 6, true), new("Math", "Math", 6, true), new("Science", "Science", 5, true),
            new("ASS", "ASS", 2, false), new("ESS", "ESS", 2, false), new("Religion", "Religion", 1, false), new("Religion", "Quran", 1, false),
            new("PE", "PE", 2, false), new("Art", "Art", 1, false), new("ICT", "ICT", 1, false), new("ICT", "AI", 1, false),
            new("Second Language", "French", 2, false), new("Drama", "Drama", 1, false), new("WSD", "Character Building", 1, false),
            new("Music", "Music", 1, false), new("Library / Reading", "Library / Reading", 1, false),
        ],
    };

    private static int PeriodsPerDay(string stage) => stage switch { "KG" => 5, "Primary" => 7, _ => 8 };

    private async Task TimetableAsync()
    {
        // Plan in memory around the sessions already on the timetable (their periods stay taken and count
        // towards the class's subject hours), then save every new session through the timetable service.
        var plans = _classes.ToDictionary(c => c.Id, c => SubjectsFor(c).Select(s =>
        {
            var existing = _existingLessons.Count(l => l.ClassId == c.Id && l.Dept == s.Dept && l.Subject == s.Subject);
            return s with { Periods = Math.Max(0, s.Periods - existing) };
        }).Where(s => s.Periods > 0).ToList());
        var demand = new Dictionary<string, int>();
        foreach (var s in plans.Values.SelectMany(p => p)) demand[s.Subject] = demand.GetValueOrDefault(s.Subject) + s.Periods;
        var target = _teachers.ToDictionary(t => t.Id, t =>
        {
            var team = _teachers.Count(o => o.Teaches == t.Teaches);
            return Math.Min(22, (int)Math.Ceiling(demand.GetValueOrDefault(t.Teaches) / (double)Math.Max(1, team)) + 2);
        });
        var load = _teachers.ToDictionary(t => t.Id, _ => 0);
        var busy = new HashSet<(int Teacher, int Day, int Slot)>(_existingLessons.Select(l => (l.TeacherId, l.Day, l.Slot)));
        var planned = new List<Lesson>();
        var unplaced = 0;

        foreach (var cls in _classes)
        {
            var perDay = PeriodsPerDay(cls.Stage);
            var free = new HashSet<(int Day, int Slot)>();
            for (var d = 0; d < 5; d++) for (var s = 1; s <= perDay; s++) free.Add((d, s));
            free.ExceptWith(_existingLessons.Where(l => l.ClassId == cls.Id).Select(l => (l.Day, l.Slot)));
            var subjects = plans[cls.Id].OrderBy(s => _teachers.Count(t => t.Teaches == s.Subject)).ThenByDescending(s => s.Periods).ToList();

            foreach (var subj in subjects)
            {
                var team = _teachers.Where(t => t.Teaches == subj.Subject).OrderBy(t => load[t.Id]).ThenBy(_ => _rng.Next()).ToList();
                var placed = false;
                foreach (var t in team.Where(t => load[t.Id] + subj.Periods <= target[t.Id]))
                {
                    var cells = Place(free, busy, t.Id, subj, subj.Periods > 5 ? 2 : 1);
                    if (cells is null) continue;
                    Commit(cls.Id, subj, t.Id, cells, free, busy, load, planned);
                    placed = true;
                    break;
                }
                if (placed) continue;
                for (var i = 0; i < subj.Periods; i++)
                {
                    var one = subj with { Periods = 1 };
                    var ok = false;
                    foreach (var t in team)
                    {
                        var cells = Place(free, busy, t.Id, one, 3);
                        if (cells is null) continue;
                        Commit(cls.Id, one, t.Id, cells, free, busy, load, planned);
                        ok = true;
                        break;
                    }
                    if (!ok) unplaced++;
                }
            }
        }

        user.Name = Mgmt(ManagementPosition.SchoolDirector).Name;
        foreach (var l in planned)
        {
            await Must(() => S<ITimetableService>().AssignAsync(new SessionInput(l.ClassId, (DayOfWeek)l.Day, _slotIds[l.Slot], _dept[l.Dept], l.Subject, l.TeacherId, null)),
                $"Timetable {l.Subject} for class {l.ClassId}");
            _lessons.Add(l);
        }
        if (unplaced > 0) Notes.Add($"Timetable: {unplaced} period(s) left free (no qualified teacher free at that time).");
        Step($"{planned.Count} weekly timetable sessions across {_classes.Count} classes ({_existingLessons.Count} existing sessions kept and counted)");
    }

    private IEnumerable<Subj> SubjectsFor((int Id, string Name, string Stage) cls) =>
        Curriculum[cls.Stage].Select(s => s.Subject == "French" && cls.Stage == "Upper" && cls.Name.EndsWith('B') ? s with { Subject = "Deutsch" } : s);

    private List<(int Day, int Slot)>? Place(HashSet<(int Day, int Slot)> free, HashSet<(int, int, int)> busy, int teacher, Subj subj, int maxPerDay)
    {
        var chosen = new List<(int Day, int Slot)>();
        var start = _rng.Next(5);
        for (var round = 0; round < maxPerDay && chosen.Count < subj.Periods; round++)
        {
            for (var k = 0; k < 5 && chosen.Count < subj.Periods; k++)
            {
                var day = (start + k) % 5;
                if (chosen.Count(c => c.Day == day) > round) continue;
                var options = free.Where(c => c.Day == day && !chosen.Contains(c) && !busy.Contains((teacher, c.Day, c.Slot)));
                // Core subjects in the morning, activity subjects later in the day.
                var cell = subj.Core ? options.OrderBy(c => c.Slot).FirstOrDefault(c => c.Slot > 0) : options.OrderByDescending(c => c.Slot).FirstOrDefault(c => c.Slot > 0);
                if (cell.Slot > 0) chosen.Add(cell);
            }
        }
        return chosen.Count == subj.Periods ? chosen : null;
    }

    private static void Commit(int classId, Subj subj, int teacher, List<(int Day, int Slot)> cells, HashSet<(int Day, int Slot)> free,
        HashSet<(int, int, int)> busy, Dictionary<int, int> load, List<Lesson> planned)
    {
        foreach (var c in cells)
        {
            free.Remove(c);
            busy.Add((teacher, c.Day, c.Slot));
            planned.Add(new Lesson(classId, c.Day, c.Slot, subj.Dept, subj.Subject, teacher));
        }
        load[teacher] += cells.Count;
    }

    private bool Busy(int staffId, int day, TimeOnly start, TimeOnly end) =>
        _lessons.Any(l => l.TeacherId == staffId && l.Day == day && _slotTimes.ContainsKey(l.Slot) && start < _slotTimes[l.Slot].End && _slotTimes[l.Slot].Start < end);

    // ------------------------------------------------------------------ P.E.

    private static readonly string[] PeActivities =
    [
        "Football passing drills and small-sided games", "Freestyle technique and water confidence", "Relay races and sprint starts",
        "Basketball dribbling circuit", "Gymnastics: balance and rolls", "Parachute games and gross-motor play", "Volleyball serving practice",
        "Fitness stations (skipping, planks, shuttle runs)", "Backstroke kicks with floats", "Handball throwing and catching",
    ];

    private async Task PeDetailsAsync()
    {
        // Details only for generated sessions; the school's own P.E. sessions are left exactly as they are.
        var pe = _dept["PE"];
        var generated = (await TestDataTracker.ReadAsync(Db)).Where(r => r.Kind == "A" && r.EntityType == typeof(TimetableAssignment).FullName)
            .Select(r => (int)JsonSerializer.Deserialize<long[]>(r.KeyJson)![0]).ToList();   // numeric keys only for this type
        var sessions = await Db.TimetableAssignments.AsNoTracking().Where(t => t.DepartmentId == pe && t.EndedOn == null && generated.Contains(t.Id))
            .OrderBy(t => t.Id).Select(t => new { t.Id, t.TeacherId, t.Day, t.TimeSlotId, Class = t.SchoolClass.Name }).ToListAsync();
        var peTeachers = _teachers.Where(t => t.Dept == "PE").ToList();
        var coTeaching = new HashSet<(int Teacher, DayOfWeek Day, int Slot)>();
        var detailed = 0;
        foreach (var s in sessions)
        {
            if (Chance(0.2)) continue; // some sessions without details yet
            var kg = StageOf(s.Class) == "KG";
            var facility = kg ? PeFacility.KgGarden : Chance(0.35) ? PeFacility.SwimmingPool : PeFacility.MainGarden;
            var activity = facility == PeFacility.SwimmingPool ? Pick(new[] { PeActivities[1], PeActivities[8] })
                : kg ? PeActivities[5] : Pick(PeActivities.Where(a => !a.Contains("stroke") && !a.Contains("Freestyle") && !a.Contains("Parachute")).ToList());
            var slotNumber = _slotIds.First(x => x.Value == s.TimeSlotId).Key;
            var (st, en) = _slotTimes[slotNumber];
            var co = peTeachers.Where(t => t.Id != s.TeacherId && !Busy(t.Id, (int)s.Day, st, en) && !coTeaching.Contains((t.Id, s.Day, s.TimeSlotId))).ToList();
            var coTeacher = (kg || facility == PeFacility.SwimmingPool) && co.Count > 0 && Chance(0.7) ? co[_rng.Next(co.Count)].Id : (int?)null;
            if (coTeacher is { } c) coTeaching.Add((c, s.Day, s.TimeSlotId));
            var equipment = facility switch
            {
                PeFacility.SwimmingPool => new[] { "Kickboards", "Pool noodles", "Lifeguard tube", "Whistle" },
                PeFacility.KgGarden => new[] { "Soft balls", "Hula hoops", "Cones", "Parachute" },
                _ => new[] { "Footballs", "Cones", "Bibs", "Stopwatch", "First-aid kit" },
            };
            var input = new PeDetailsInput(coTeacher, facility, activity,
                Chance(0.3) ? Pick(new[] { "Two students excused with a medical note.", "Bring water bottles — hot afternoon.", "Swimming caps required.", "Assessment of passing skills this week." }) : null,
                equipment.Select(e => new PeEquipmentInput(e, Chance(0.75))).ToList());
            await Must(() => S<IPeService>().SaveDetailsAsync(s.Id, input), $"P.E. details for session {s.Id}");
            detailed++;
        }
        Step($"P.E. details on {detailed} of {sessions.Count} generated P.E. sessions");
    }

    // ------------------------------------------------------------------ duties

    private sealed record DutySpec(DutyComponent Component, string Description, string Location, TimeOnly Start, TimeOnly End, string[]? Classes = null);

    private static readonly DutySpec[] DailyDuties =
    [
        new(DutyComponent.StudentsArrival, "Main gate arrival check", "Main Gate", new(7, 0), new(7, 40)),
        new(DutyComponent.StudentsArrival, "Bus bay arrival check", "Bus Bay", new(7, 0), new(7, 40)),
        new(DutyComponent.InClassMorning, "Before-lesson supervision — KG classrooms", "KG Building", new(7, 20), new(7, 45), ["PreK", "KG1A", "KG1B", "KG2A", "KG2B"]),
        new(DutyComponent.InClassMorning, "Before-lesson supervision — Grades 1–3", "Lower Elementary Corridor", new(7, 20), new(7, 45), ["G1A", "G1B", "G2A", "G2B", "G3A", "G3B"]),
        new(DutyComponent.MorningAssembly, "Morning line-up and proadcast", "Main Courtyard", new(7, 25), new(7, 45)),
        new(DutyComponent.PreKKg1Break, "Pre-K / KG1 garden break", "KG Garden", new(10, 0), new(10, 20)),
        new(DutyComponent.Kg2Break, "KG2 snack and garden break", "KG Garden", new(10, 0), new(10, 20)),
        new(DutyComponent.LowerElementaryBreak, "Grades 1–3 playground break", "Lower Playground", new(10, 0), new(10, 20)),
        new(DutyComponent.UpperElementaryBreak, "Grades 4–6 lunch break", "Main Playground", new(12, 35), new(12, 55)),
        new(DutyComponent.MiddleHighBreak, "Grades 7–10 lunch break", "Main Courtyard & Canteen", new(12, 35), new(12, 55)),
        new(DutyComponent.DismissalPickup, "KG parent pickup", "KG Gate", new(11, 50), new(12, 20)),
        new(DutyComponent.DismissalBus, "Bus dismissal — buses 1–8", "Bus Bay", new(14, 25), new(14, 55)),
        new(DutyComponent.DismissalPickup, "Parent pickup — Gate 2", "Gate 2", new(14, 25), new(15, 0)),
    ];

    private static readonly string[] CoveredNotes =
    [
        "All students received and accounted for.", "Smooth session; no incidents.", "Three late arrivals logged at the gate.",
        "Line-up started on time; announcements read by Grade 9 students.", "Rain — students kept under the shaded area.",
        "Bus 3 arrived 10 minutes late; students escorted to class.", "Two minor scrapes treated by the nurse.",
    ];

    private async Task DutiesAsync()
    {
        // Duty staff: generated staff only (the school's own staff get no new duties).
        user.Name = Mgmt(ManagementPosition.Sqam).Name;
        var staff = _teachers.Concat(_management.Where(m => m.Position is not null)).ToList();
        var usage = staff.ToDictionary(s => s.Id, _ => 0);
        var from = _week0.AddDays(-21);
        var to = _week0.AddDays(11);
        var classIds = _classes.ToDictionary(c => c.Name, c => c.Id);
        var plannedLeave = 0;

        foreach (var date in Days(from, to))
        {
            var day = (int)date.DayOfWeek;
            var onDuty = new HashSet<int>();
            Staff? Choose(DutySpec spec, int? except = null) => staff
                .Where(s => s.Id != except && !onDuty.Contains(s.Id) && !Busy(s.Id, day, spec.Start, spec.End))
                .OrderBy(s => usage[s.Id]).ThenBy(_ => _rng.Next()).FirstOrDefault();

            foreach (var spec in DailyDuties)
            {
                var primary = Choose(spec);
                if (primary is null) { Skipped.Add($"No free staff for {spec.Description} on {date}"); continue; }
                onDuty.Add(primary.Id);
                usage[primary.Id]++;

                var past = date < _today;
                var isToday = date == _today;
                var roll = _rng.NextDouble();
                var absent = past ? roll is >= 0.80 and < 0.94 : !isToday && plannedLeave < 2 && Chance(0.03);
                if (!past && absent) plannedLeave++;
                Staff? backup = null;
                if (absent)
                {
                    backup = Choose(spec, primary.Id);
                    if (backup is null) absent = false;
                    else { onDuty.Add(backup.Id); usage[backup.Id]++; }
                }
                var attendance = absent ? AttendanceStatus.Absent : past || (isToday && spec.Start < new TimeOnly(13, 0)) ? AttendanceStatus.Present : AttendanceStatus.NotRecorded;
                var ids = spec.Classes?.Where(classIds.ContainsKey).Select(n => classIds[n]).ToList() ?? [];
                var input = new DutyInput(spec.Component, date, spec.Description, spec.Start, spec.End, spec.Location, primary.Id, attendance, backup?.Id,
                    absent ? Pick(new[] { "Sick leave (medical certificate received).", "Approved personal leave.", "Attending external training (EFA workshop)." }) : null, ids);
                var id = await Must(() => S<IDutyService>().CreateAsync(input), $"Duty {spec.Description} {date}");
                var backupPresent = false;

                if (past)
                {
                    DutyStatus status;
                    string? notes = null, issues = null;
                    var followUp = false;
                    if (!absent)
                    {
                        // Most duties closed as Covered; a few audits left open (Pending / In Progress).
                        status = roll is >= 0.77 and < 0.80 ? DutyStatus.Pending : roll is >= 0.94 and < 0.97 ? DutyStatus.InProgress : DutyStatus.Covered;
                        if (status == DutyStatus.Covered && Chance(0.35)) notes = Pick(CoveredNotes);
                        if (status == DutyStatus.Covered && Chance(0.06))
                        {
                            issues = Pick(new[] { "Two students left the line without permission.", "Gate left unattended for 5 minutes during the handover.", "Bus list did not match the students on board." });
                            followUp = true;
                        }
                    }
                    else if (roll < 0.87)
                    {
                        await Must(() => S<IDutyService>().RecordBackupResponseAsync(id, true, "Happy to cover."), "Backup accepted");
                        await Must(() => S<IDutyService>().SetBackupAttendanceAsync(id, AttendanceStatus.Present), "Backup present");
                        backupPresent = true;
                        status = DutyStatus.Covered;
                        notes = $"Covered by {backup!.Name}.";
                    }
                    else if (roll < 0.91)
                    {
                        await Must(() => S<IDutyService>().RecordBackupResponseAsync(id, false, "Teaching a cover lesson at that time."), "Backup declined");
                        status = DutyStatus.NotCovered;
                        issues = "Primary absent and the backup declined; area supervised only partly by the coordinator.";
                        followUp = true;
                    }
                    else
                    {
                        await Must(() => S<IDutyService>().RecordBackupResponseAsync(id, true, null), "Backup accepted");
                        await Must(() => S<IDutyService>().SetBackupAttendanceAsync(id, AttendanceStatus.Absent), "Backup absent");
                        status = DutyStatus.NotCovered;
                        issues = "Backup accepted but did not attend. No one on duty for the first 15 minutes.";
                        followUp = true;
                    }
                    if (status != DutyStatus.Pending || notes is not null)
                        await Must(() => S<IDutyService>().UpdateAuditAsync(id, status, notes, issues, followUp), "Duty audit");
                }
                else if (isToday && attendance == AttendanceStatus.Present)
                {
                    var status = spec.End < new TimeOnly(10, 30) ? DutyStatus.Covered : DutyStatus.InProgress;
                    await Must(() => S<IDutyService>().UpdateAuditAsync(id, status, status == DutyStatus.Covered ? Pick(CoveredNotes) : null, null, false), "Duty audit (today)");
                }
                _duties.Add(new DutyMade(id, date, spec.Component, spec.Location, spec.Start, spec.End, primary.Id, attendance, backup?.Id, backupPresent));
            }
        }
        Step($"{_duties.Count} duty sessions ({Days(from, to).Count()} school days × Morning, Midday and Dismissal), with backups, statuses and audit notes");
    }

    private async Task FeedbackAsync()
    {
        var dutyIds = _duties.Where(d => d.Date < _today).Select(d => d.Id).ToList();
        var assignments = await Db.DutyStaffAssignments.AsNoTracking()
            .Where(a => dutyIds.Contains(a.DutyAssignmentId) && a.IsCurrent && (a.Role == DutyRole.Primary || a.BackupStatus == BackupRequestStatus.Accepted))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.StaffMemberId, a.Attendance, a.DutyAssignment.DutyDate, a.DutyAssignment.DutyType }).ToListAsync();
        var sqam = Mgmt(ManagementPosition.Sqam);
        var director = Mgmt(ManagementPosition.SchoolDirector);
        var coordinators = _management.Where(m => m.Position is >= ManagementPosition.KgStageCoordinator).ToList();
        var added = 0;
        foreach (var a in assignments.Where(_ => Chance(0.35)))
        {
            var others = coordinators.Where(m => m.Id != a.StaffMemberId).ToList();
            var evaluator = a.StaffMemberId == sqam.Id ? director : a.DutyType == DutyType.Midday && others.Count > 0 && Chance(0.5) ? Pick(others) : sqam;
            if (evaluator.Id == a.StaffMemberId || evaluator.Id == 0) continue;
            user.Name = evaluator.Name;
            var present = a.Attendance == AttendanceStatus.Present;
            var rating = present ? Pick(new[] { 5, 5, 5, 4, 4, 4, 4, 3, 3, 2 }) : Pick(new[] { 1, 2 });
            var (text, strengths, issues) = rating switch
            {
                5 => ("Excellent supervision; arrived early and kept students calm.", "Proactive, visible, good rapport with students.", (string?)null),
                4 => ("Good duty overall; students well supervised.", "Reliable and punctual.", "Could circulate more around the far end of the area."),
                3 => ("Duty covered but supervision was mostly static.", "On time.", "Stayed near the entrance; blind spots near the stairs."),
                2 => ("Arrived late and missed the start of the duty.", null, "Late by 10 minutes; handover not done."),
                _ => ("Did not attend and no notice was given.", null, "Absent without informing the coordinator."),
            };
            var date = a.DutyDate.AddDays(_rng.Next(0, 3));
            if (date > _today) date = _today;
            await Must(() => S<IStaffDutyService>().AddFeedbackAsync(new FeedbackInput(a.Id, rating, text, strengths, issues, rating <= 2, evaluator.Id, date)), "Duty evaluation");
            added++;
        }
        Step($"{added} staff duty evaluations (1–5 stars)");
    }

    // ------------------------------------------------------------------ workload

    private async Task WorkloadAsync()
    {
        user.Name = Mgmt(ManagementPosition.Sqam).Name;
        var weeks = new[] { _week0.AddDays(-21), _week0.AddDays(-14), _week0.AddDays(-7), _week0, _week0.AddDays(7) };
        var fullTeachers = _teachers.OrderByDescending(t => _lessons.Count(l => l.TeacherId == t.Id)).Take(3).Select(t => t.Id).ToHashSet();
        var classNames = _classes.ToDictionary(c => c.Id, c => c.Name);
        var rows = 0;

        bool DutyClash(int teacher, DateOnly date, int slot) =>
            _duties.Any(d => d.Date == date && (d.Primary == teacher || d.Backup == teacher) && d.Start < _slotTimes[slot].End && _slotTimes[slot].Start < d.End);

        foreach (var week in weeks)
        {
            var timetableCounts = week >= _week0;
            foreach (var t in _teachers)
            {
                var lessons = _lessons.Where(l => l.TeacherId == t.Id).ToList();
                var total = timetableCounts ? lessons.Count : 0;

                if (!timetableCounts)
                {
                    // Earlier weeks (before the timetable started): class periods recorded per class.
                    foreach (var g in lessons.GroupBy(l => l.ClassId))
                    {
                        var periods = g.Count() - (Chance(0.06) ? 1 : 0); // a lesson lost to a trip or an exam
                        if (periods < 1) continue;
                        if (await Try(() => S<IWorkloadService>().CreateAsync(new WorkloadInput(t.Id, week, WorkloadCategory.Class, periods, g.Key, null, $"{g.First().Subject} — {classNames[g.Key]}")), "Class periods") is not null)
                        { total += periods; rows++; }
                    }
                }

                foreach (var d in _duties.Where(d => SchoolWeek.StartOf(d.Date) == week && d.Date <= _today
                    && ((d.Primary == t.Id && d.PrimaryAtt == AttendanceStatus.Present) || (d.Backup == t.Id && d.BackupPresent))))
                {
                    if (total >= 23 || !Chance(0.7)) continue;
                    if (await Try(() => S<IWorkloadService>().CreateAsync(new WorkloadInput(t.Id, week, WorkloadCategory.Duty, 1, null, d.Id, null)), "Duty period") is not null)
                    { total++; rows++; }
                }

                var office = t.Dept == "WSD" ? 4 : 2;
                if (week >= _week0)
                {
                    // Office hours in free periods that do not overlap the teacher's duties.
                    var freeCells = Enumerable.Range(0, 5).SelectMany(d => Enumerable.Range(1, 8).Select(s => (Day: d, Slot: s)))
                        .Where(c => _slotIds.ContainsKey(c.Slot) && !lessons.Any(l => l.Day == c.Day && l.Slot == c.Slot) && !DutyClash(t.Id, week.AddDays(c.Day), c.Slot))
                        .OrderBy(_ => _rng.Next()).ToList();
                    var placed = 0;
                    foreach (var c in freeCells)
                    {
                        if (placed == office || total >= 24) break;
                        var desc = Pick(new[] { "Office hour — parent meetings", "Lesson planning", "Marking and feedback", "Department meeting" });
                        if (await Try(() => S<IWorkloadService>().CreateAsync(new WorkloadInput(t.Id, week, WorkloadCategory.Office, 1, null, null, desc, (DayOfWeek)c.Day, _slotIds[c.Slot])), "Office hour") is not null)
                        { placed++; total++; rows++; }
                    }
                }
                else if (total + office <= 24)
                {
                    if (await Try(() => S<IWorkloadService>().CreateAsync(new WorkloadInput(t.Id, week, WorkloadCategory.Office, office, null, null, "Office hours and planning")), "Office periods") is not null)
                    { total += office; rows++; }
                }

                if (week <= _week0 && Chance(0.35) && total <= 22)
                {
                    var cls = _classes[_rng.Next(_classes.Count)];
                    var periods = total <= 21 && Chance(0.4) ? 2 : 1;
                    var absentee = _teachers[_rng.Next(_teachers.Count)];
                    if (await Try(() => S<IWorkloadService>().CreateAsync(new WorkloadInput(t.Id, week, WorkloadCategory.Substitution, periods, cls.Id, null, $"Cover for {absentee.Name} ({cls.Name})")), "Substitution") is not null)
                    { total += periods; rows++; }
                }

                if (week == _week0 && fullTeachers.Contains(t.Id) && total < 24)
                {
                    var rest = 24 - total;
                    if (await Try(() => S<IWorkloadService>().CreateAsync(new WorkloadInput(t.Id, week, WorkloadCategory.Office, rest, null, null, "Exam paper preparation")), "Fill to 24") is not null) rows++;
                }
            }
        }
        Step($"{rows} workload entries over 5 weeks (class, office hours, substitutions, duties; 3 teachers at 24/24 this week)");
    }

    // ------------------------------------------------------------------ arrivals

    private async Task ArrivalsAsync()
    {
        var method = _students.ToDictionary(s => s.Id, _ => _rng.NextDouble() switch { < 0.45 => ArrivalMethod.SchoolBus, < 0.8 => ArrivalMethod.PrivateCar, _ => ArrivalMethod.ParentDropOff });
        var oftenLate = _students.Where(_ => Chance(0.05)).Select(s => s.Id).ToHashSet();
        var stage = _classes.ToDictionary(c => c.Id, c => c.Stage);
        var days = Days(_today.AddDays(-16), _today).ToList();
        var lateBusDay = days.Count > 3 ? days[^4] : (DateOnly?)null;
        var saved = 0;
        var failed = 0;
        user.Name = Mgmt(ManagementPosition.Sqam).Name;

        foreach (var date in days)
        {
            int? Receiver(string location) => _duties.Where(d => d.Date == date && d.Location == location).Select(d => d.PrimaryAtt == AttendanceStatus.Absent ? d.Backup : d.Primary).FirstOrDefault();
            var kgReceiver = _duties.Where(d => d.Date == date && d.Component == DutyComponent.InClassMorning).Select(d => (int?)d.Primary).FirstOrDefault();
            var classes = date == _today ? _classes.Take(_classes.Count * 3 / 5).ToList() : _classes; // today: still being recorded

            foreach (var cls in classes)
            {
                var entries = new List<ArrivalEntry>();
                foreach (var s in _students.Where(x => x.ClassId == cls.Id))
                {
                    if (Chance(0.04))
                    {
                        entries.Add(new ArrivalEntry(s.Id, ArrivalStatus.Absent, null, null, null, null, false,
                            Chance(0.6) ? Pick(new[] { "Parent called — fever.", "Family travel.", "Doctor's appointment.", "No reason given yet." }) : null));
                        continue;
                    }
                    var m = method[s.Id];
                    var location = m == ArrivalMethod.SchoolBus ? "Bus Bay" : stage[cls.Id] == "KG" && m == ArrivalMethod.ParentDropOff ? "KG Gate" : "Main Gate";
                    var minutes = m == ArrivalMethod.SchoolBus ? _rng.Next(10, 26) : _rng.Next(5, 38);
                    if (m == ArrivalMethod.SchoolBus && date == lateBusDay && Chance(0.3)) minutes = _rng.Next(44, 52);
                    if (m != ArrivalMethod.SchoolBus && (oftenLate.Contains(s.Id) ? Chance(0.5) : Chance(0.05))) minutes = _rng.Next(41, 58);
                    var time = new TimeOnly(7, 0).AddMinutes(minutes);
                    var receiver = location == "KG Gate" ? kgReceiver : Receiver(location);
                    var note = minutes > 40 ? Pick(new[] { "Traffic on the ring road.", "Bus 3 delayed.", "Overslept — parent informed.", (string?)null }) : null;
                    entries.Add(new ArrivalEntry(s.Id, ArrivalStatus.Present, time, m, location, receiver, receiver is not null && Chance(0.96), note));
                }
                if (entries.Count == 0) continue;
                var results = await S<IArrivalService>().RecordAsync(date, entries);
                Tick();
                saved += results.Count(r => r.Saved);
                failed += results.Count(r => !r.Saved);
            }
        }

        var studentIds = _students.Select(s => s.Id).ToList();
        var records = await Db.StudentArrivals.AsNoTracking().Where(r => r.Status == ArrivalStatus.Absent && r.ArrivalDate < _today && studentIds.Contains(r.StudentId))
            .OrderBy(r => r.Id).Take(4).Select(r => new { r.Id, r.StudentId }).ToListAsync();
        user.Name = Mgmt(ManagementPosition.SchoolDirector).Name;
        foreach (var r in records)
            await Must(() => S<IArrivalService>().CorrectAsync(r.Id, new ArrivalEntry(r.StudentId, ArrivalStatus.Present, new TimeOnly(7, 52), ArrivalMethod.ParentDropOff, "Main Gate", null, false, "Arrived with a parent after the register was taken."),
                "Marked absent at the gate; the student arrived late with a parent and signed in at reception."), "Arrival correction");
        if (failed > 0) Skipped.Add($"{failed} arrival entries were not saved");
        Step($"{saved} student arrival records over {days.Count} school days (today partly recorded), {records.Count} corrections");
    }

    // ------------------------------------------------------------------ management tasks

    private static readonly Dictionary<ManagementPosition, string[]> TaskTitles = new()
    {
        [ManagementPosition.SchoolDirector] = ["Approve the staff professional development plan", "Review Q1 budget variance report", "Meet the parents' council about transport fees", "Sign off the evacuation drill report", "Finalise the teacher appraisal calendar", "Renew the canteen supplier contract"],
        [ManagementPosition.Sqam] = ["Draft the school self-evaluation form (SEF)", "Audit Q1 lesson observation records", "Update the duty audit checklist", "Compile parent satisfaction survey results", "Review belongings readiness trends with coordinators"],
        [ManagementPosition.KgStageCoordinator] = ["Plan the KG winter celebration", "Order shade sails for the KG garden", "Review KG2 phonics assessment results", "Schedule the KG parents' orientation"],
        [ManagementPosition.LowerElementaryCoordinator] = ["Run Grade 1–3 reading benchmark tests", "Organise the Grade 3 science fair", "Follow up on Grade 2 homework policy feedback", "Arrange the cover plan for a Grade 1 teacher on leave"],
        [ManagementPosition.UpperElementaryCoordinator] = ["Register Grades 4–6 for the math olympiad", "Update the Grade 5 field trip risk assessment", "Review the Grade 6 transition plan", "Collect Q1 progress report comments"],
        [ManagementPosition.MiddleSchoolCoordinator] = ["Schedule the Grade 7–8 project week", "Coordinate Grade 8 MAP testing", "Investigate repeated lateness in Grade 7", "Plan the anti-bullying awareness week"],
        [ManagementPosition.HighSchoolCoordinator] = ["Hold Grade 9–10 IGCSE subject selection meetings", "Submit Grade 10 exam entries", "Invite speakers for the university fair", "Review Grade 9 attendance concerns"],
    };

    private async Task TasksAsync()
    {
        user.Name = Mgmt(ManagementPosition.SchoolDirector).Name;
        var pattern = new[] { ManagementTaskStatus.InProgress, ManagementTaskStatus.ToDo, ManagementTaskStatus.Completed, ManagementTaskStatus.InProgress, ManagementTaskStatus.OnHold, ManagementTaskStatus.ToDo, ManagementTaskStatus.Completed };
        var priorities = new[] { TaskPriority.High, TaskPriority.Medium, TaskPriority.Urgent, TaskPriority.Low, TaskPriority.Medium, TaskPriority.High };
        var created = new List<(int Id, ManagementTaskStatus Status, bool Overdue)>();
        var i = 0;
        // Tasks go to the generated Management Team members (by the position they hold, or held).
        var owners = _management.Where(m => m.Position is not null).ToDictionary(m => m.Position!.Value);
        foreach (var (position, titles) in TaskTitles)
        {
            if (!owners.TryGetValue(position, out var member)) continue;
            foreach (var title in titles)
            {
                var status = pattern[i % pattern.Length];
                var overdue = status is ManagementTaskStatus.ToDo or ManagementTaskStatus.InProgress && i % 4 == 1;
                var input = new TaskInput
                {
                    MemberId = member.Id, Title = title, Priority = priorities[i % priorities.Length],
                    Description = $"Owner: {member.Name}. Agreed in the weekly management meeting.",
                    Notes = i % 3 == 0 ? "Share the outcome with the School Director." : null,
                    DueDate = i % 9 == 8 ? null : _today.AddDays(3 + _rng.Next(0, 35)),
                    DueTime = i % 5 == 0 && i % 9 != 8 ? new TimeOnly(13, 0) : null,
                };
                var id = await Must(() => S<IManagementTaskService>().CreateAsync(input), $"Task {title}");
                if (status != ManagementTaskStatus.ToDo)
                {
                    if (status == ManagementTaskStatus.Completed) await Must(() => S<IManagementTaskService>().SetStatusAsync(id, ManagementTaskStatus.InProgress), "Task status");
                    await Must(() => S<IManagementTaskService>().SetStatusAsync(id, status), "Task status");
                }
                created.Add((id, status, overdue));
                i++;
            }
        }
        var former = _management.FirstOrDefault(m => m.Position is null);
        if (former is not null)
        {
            var handover = await Must(() => S<IManagementTaskService>().CreateAsync(new TaskInput { MemberId = former.Id, Title = "Hand over High School files to the new coordinator", Priority = TaskPriority.High, DueDate = _today.AddDays(5) }), "Handover task");
            await Must(() => S<IManagementTaskService>().SetStatusAsync(handover, ManagementTaskStatus.Completed), "Handover completed");
            created.Add((handover, ManagementTaskStatus.Completed, false));
        }

        // Real schedules: completed tasks were due (and done) in the past; some open tasks are overdue.
        // Entering a past due date is refused by design, so it is set afterwards on these generated tasks only.
        var taskIds = created.Select(c => c.Id).ToList();
        var tasks = await Db.ManagementTasks.Where(t => taskIds.Contains(t.Id)).ToListAsync();
        foreach (var t in tasks)
        {
            var c = created.First(x => x.Id == t.Id);
            if (c.Status == ManagementTaskStatus.Completed)
            {
                t.DueDate = _today.AddDays(-_rng.Next(3, 30));
                t.DueTime = null;
                t.CompletedAt = t.DueDate.Value.AddDays(-_rng.Next(0, 3)).ToDateTime(new TimeOnly(14, 30)).ToUniversalTime();
            }
            else if (c.Overdue) t.DueDate = _today.AddDays(-_rng.Next(1, 12));
        }
        await Db.SaveChangesAsync();
        Step($"{created.Count} Management Team tasks (to do, in progress, on hold, completed, {created.Count(c => c.Overdue)} overdue)");
    }

    // ------------------------------------------------------------------ belongings

    private async Task BelongingsAsync()
    {
        // A new academic year with dated quarters. An existing year is never changed; if one already uses
        // this name, the new one gets a distinct name.
        user.Name = Mgmt(ManagementPosition.Sqam).Name;
        var name = YearName();
        if (await Db.AcademicYears.AnyAsync(y => y.Name == name)) name = $"Academic Year {name}";
        var yearId = await Must(() => S<IBelongingsService>().CreateYearAsync(name), "Academic year");
        var quarters = await Db.AcademicQuarters.AsNoTracking().Where(q => q.AcademicYearId == yearId).OrderBy(q => q.Number).Select(q => q.Id).ToListAsync();
        // Q1 holds today (week 6); Q3 starts on a Tuesday and Q4 ends on a Wednesday (partial weeks).
        var q1 = (Start: Q1Start, End: Q1Start.AddDays(53));
        var q2 = (Start: q1.End.AddDays(10), End: q1.End.AddDays(63));
        var q3 = (Start: q2.End.AddDays(12), End: q2.End.AddDays(63));
        var q4 = (Start: q3.End.AddDays(10), End: q3.End.AddDays(62));
        var dates = new[] { q1, q2, q3, q4 };
        for (var n = 0; n < 4; n++)
        {
            var (s, e) = dates[n];
            await Must(() => S<IBelongingsService>().UpdateQuarterAsync(quarters[n], $"Quarter {n + 1}", s, e), $"Quarter {n + 1} dates");
        }

        var tracked = new[] { "PreK", "KG2A", "G1A", "G3B", "G5A", "G7A", "G9B" };
        var classes = _classes.Where(c => tracked.Contains(c.Name)).ToList();
        var academic = new HashSet<string> { "Arabic", "English", "Math", "Science", "ASS", "ESS", "Religion", "Second Language" };
        var diligence = _students.ToDictionary(s => s.Id, _ => _rng.NextDouble() switch { < 0.1 => 0.55, < 0.35 => 0.82, _ => 0.95 });
        var schoolMeals = _students.Where(_ => Chance(0.15)).Select(s => s.Id).ToHashSet();
        var coldFrom = _today.AddDays(-10);
        var days = Days(q1.Start.AddDays(14), _today).ToList();
        var skippedDay = days.Count > 6 ? days[^6] : (DateOnly?)null;
        var records = 0;
        var materials = 0;
        var noteEdits = 0;

        ItemStatus Status(double good) => Chance(good) ? ItemStatus.Green : ItemStatus.Red;

        foreach (var date in days)
        {
            foreach (var cls in classes)
            {
                if (date == skippedDay && cls.Name == "G5A") continue;                 // a whole day not recorded
                if (date == _today && cls.Stage == "Upper") continue;                   // today: upper classes not checked yet
                var grade = GradeCatalog.KeyOf(cls.Name);
                var subjects = _lessons.Where(l => l.ClassId == cls.Id && l.Day == (int)date.DayOfWeek && academic.Contains(l.Dept))
                    .Select(l => (l.Dept, l.Subject)).Distinct().OrderBy(_ => _rng.Next()).Take(cls.Stage == "KG" ? 2 : 3).ToList();

                foreach (var s in _students.Where(x => x.ClassId == cls.Id))
                {
                    if (Chance(0.06)) continue;                                         // missing record (not Red)
                    var d = diligence[s.Id];
                    var jacket = date < coldFrom ? ItemStatus.NotApplicable : Status(d);
                    var pencil = cls.Name == "PreK" ? ItemStatus.NotApplicable : Status(d);
                    var lunch = schoolMeals.Contains(s.Id) ? ItemStatus.NotApplicable : Status(Math.Min(0.99, d + 0.05));
                    var flask = Status(d);
                    var bag = Status(0.99);
                    string? note = null;
                    if (pencil == ItemStatus.Red && Chance(0.5)) note = "Pencil case left at home; borrowed from the class box.";
                    else if (flask == ItemStatus.Red && Chance(0.4)) note = "No water flask — the school provided a cup.";
                    else if (jacket == ItemStatus.Red && Chance(0.4)) note = "No jacket on a cold morning; parents reminded.";
                    var input = new PersonalInput(s.Id, date, grade, jacket, bag, pencil, flask, lunch, note);
                    await Must(() => S<IBelongingsService>().SavePersonalAsync(input), "Belongings");
                    records++;
                    if (note is not null && noteEdits < 12 && Chance(0.3))
                    {
                        await Must(() => S<IBelongingsService>().SavePersonalAsync(input with { Notes = note + " Parent replied in the diary: will send it tomorrow." }), "Belongings note edit");
                        noteEdits++;
                    }

                    foreach (var (dept, subject) in subjects)
                    {
                        var book = cls.Stage == "KG" && Chance(0.5) ? ItemStatus.NotApplicable : Status(d);
                        var copy = Status(Math.Max(0.4, d - 0.05));
                        await Must(() => S<IBelongingsService>().SaveMaterialAsync(new MaterialInput(s.Id, date, grade, _dept[dept], subject, book, copy)), "Academic materials");
                        materials++;
                    }
                }
            }
        }
        Step($"Academic year {name} with 4 quarters; {records} daily belongings records, {materials} subject book checks, {noteEdits} edited notes");
    }

    // ------------------------------------------------------------------ realistic timestamps

    /// <summary>Generated rows were all written now; give them the time they would really have been entered (generated rows only).</summary>
    private async Task BackdateAsync()
    {
        string Join(Type type, string alias) =>
            $"JOIN dbo.{TestDataTracker.Table} r ON r.Kind = 'A' AND r.EntityType = '{type.FullName}' AND TRY_CAST(JSON_VALUE(r.KeyJson, '$[0]') AS bigint) = {alias}.Id";
        var yearStart = Q1Start.AddDays(-30).ToString("yyyy-MM-dd");
        var statements = new[]
        {
            $"UPDATE s SET CreatedAt = DATEADD(MINUTE, s.Id % 600, CAST('{yearStart}' AS datetime2)), UpdatedAt = DATEADD(MINUTE, s.Id % 600, CAST('{yearStart}' AS datetime2)) FROM Students s {Join(typeof(Student), "s")}",
            $"UPDATE s SET CreatedAt = DATEADD(MINUTE, s.Id % 600, CAST('{yearStart}' AS datetime2)), UpdatedAt = DATEADD(MINUTE, s.Id % 600, CAST('{yearStart}' AS datetime2)) FROM StaffMembers s {Join(typeof(Teacher), "s")}",
            $"UPDATE s SET CreatedAt = DATEADD(MINUTE, s.Id % 600, CAST('{yearStart}' AS datetime2)), UpdatedAt = DATEADD(MINUTE, s.Id % 600, CAST('{yearStart}' AS datetime2)) FROM StaffMembers s {Join(typeof(ManagementMember), "s")}",
            $"UPDATE d SET CreatedAt = DATEADD(HOUR, -60, CAST(d.DutyDate AS datetime2)), UpdatedAt = DATEADD(MINUTE, DATEDIFF(MINUTE, '00:00', d.EndTime) + 25, CAST(d.DutyDate AS datetime2)) FROM DutyAssignments d {Join(typeof(DutyAssignment), "d")} WHERE d.DutyDate <= CAST(GETDATE() AS date)",
            $"UPDATE a SET ChangedAt = DATEADD(MINUTE, DATEDIFF(MINUTE, '00:00', d.EndTime) + 20 + (a.Id % 15), CAST(d.DutyDate AS datetime2)) FROM DutyAudits a JOIN DutyAssignments d ON d.Id = a.DutyAssignmentId {Join(typeof(DutyAudit), "a")} WHERE d.DutyDate < CAST(GETDATE() AS date)",
            $"UPDATE x SET CreatedAt = DATEADD(MINUTE, DATEDIFF(MINUTE, '00:00', COALESCE(x.ArrivalTime, '07:45')) + 3, CAST(x.ArrivalDate AS datetime2)), UpdatedAt = DATEADD(MINUTE, DATEDIFF(MINUTE, '00:00', COALESCE(x.ArrivalTime, '07:45')) + 3, CAST(x.ArrivalDate AS datetime2)) FROM StudentArrivalRecords x {Join(typeof(StudentArrivalRecord), "x")} WHERE x.ArrivalDate < CAST(GETDATE() AS date)",
            $"UPDATE b SET CreatedAt = DATEADD(MINUTE, 480 + b.Id % 90, CAST(b.Date AS datetime2)), UpdatedAt = DATEADD(MINUTE, 480 + b.Id % 90, CAST(b.Date AS datetime2)) FROM BelongingsRecords b {Join(typeof(BelongingsRecord), "b")} WHERE b.Date < CAST(GETDATE() AS date)",
            $"UPDATE w SET CreatedAt = DATEADD(HOUR, -40, CAST(w.WeekStart AS datetime2)), UpdatedAt = DATEADD(HOUR, -40, CAST(w.WeekStart AS datetime2)) FROM TeacherWorkloadAssignments w {Join(typeof(TeacherWorkloadAssignment), "w")} WHERE w.WeekStart < CAST(GETDATE() AS date)",
            $"UPDATE f SET CreatedAt = DATEADD(HOUR, 15, CAST(f.EvaluationDate AS datetime2)) FROM StaffDutyFeedback f {Join(typeof(StaffDutyFeedback), "f")}",
            $"UPDATE t SET CreatedAt = DATEADD(DAY, -(7 + t.Id % 20), SYSUTCDATETIME()) FROM ManagementTasks t {Join(typeof(ManagementTask), "t")}",
        };
        foreach (var sql in statements) await Db.Database.ExecuteSqlRawAsync(sql);
        Step("Entry timestamps of generated rows set to the days they belong to");
    }
}
