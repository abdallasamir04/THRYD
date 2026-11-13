using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.Data.TestData;

/// <summary>
/// Two-level attendance test data, all through the application services (so alerts are raised by the same
/// rules as in the app): school attendance with scripted absence scenarios, lesson attendance for the
/// timetable's lessons, and follow-up of the alerts. Existing attendance records are never changed:
/// only school days without a record are filled.
/// </summary>
public sealed partial class TestDataSeeder
{
    /// <summary>First school week of the generated school attendance (the academic calendar's quarter 1).</summary>
    private static readonly DateOnly AttendanceFrom = new(2026, 10, 4);

    private static readonly string[] ExcusedNotes =
    [
        "Excused — medical certificate received.", "Excused — family emergency, parent called the office.", "Excused — national swimming trials (letter on file).",
        "Excused — religious holiday, approved by the principal.", "Excused — hospital appointment, note received.",
    ];

    private static readonly string[] UnexcusedNotes = ["No reason given yet.", "Parent called — fever.", "Parent not reachable.", "Family travel.", "Unwell — parent called."];

    // ------------------------------------------------------------------ school attendance

    private async Task SchoolAttendanceScenariosAsync()
    {
        if (_students.Count == 0) { Notes.Add("School attendance: no generated students to record."); return; }
        user.Name = Mgmt(ManagementPosition.Sqam).Name;
        user.StaffId = Mgmt(ManagementPosition.Sqam).Id is > 0 and var sid ? sid : null;

        // A real public holiday inside the period (Armed Forces Day, 6 October): not a school day.
        var holiday = new DateOnly(2026, 10, 6);
        if (!await Db.SchoolHolidays.AnyAsync(h => h.StartDate <= holiday && holiday <= h.EndDate))
            await Must(() => S<IAttendanceCalendarService>().AddHolidayAsync("Armed Forces Day", holiday, holiday), "School holiday");

        // School days without any record yet: the weeks before the existing records, and Thursday 12 November.
        var recorded = (await Db.StudentArrivals.AsNoTracking().Select(a => a.ArrivalDate).Distinct().ToListAsync()).ToHashSet();
        var holidays = await Db.SchoolHolidays.AsNoTracking().Select(h => new { h.StartDate, h.EndDate }).ToListAsync();
        bool Free(DateOnly d) => d < _today && IsSchoolDay(d) && !recorded.Contains(d) && !holidays.Any(h => h.StartDate <= d && d <= h.EndDate);
        var firstExisting = recorded.Count == 0 ? _today : recorded.Min();
        var days = Days(AttendanceFrom, firstExisting.AddDays(-1)).Where(Free).ToList();
        var thursday = Days(SchoolWeek.Current, _today.AddDays(-1)).Where(Free).ToList(); // e.g. 12 Nov, if nobody recorded it
        if (days.Count < 10) { Notes.Add("School attendance: fewer than two free school weeks before the existing records; scenarios shortened."); }

        // ---- scripted scenarios (student → absent days); everyone else gets realistic background attendance
        var pool = _students.OrderBy(_ => _rng.Next()).ToList();
        var plan = new Dictionary<int, (HashSet<DateOnly> Absent, HashSet<DateOnly> Skip, string Scenario)>();
        var next = 0;
        (int Id, string Name, int ClassId) Take() => pool[next++ % pool.Count];
        void Script(string scenario, int count, Func<List<DateOnly>> absent, Func<List<DateOnly>>? skip = null)
        {
            for (var i = 0; i < count; i++)
            {
                var s = Take();
                plan[s.Id] = (absent().ToHashSet(), (skip?.Invoke() ?? []).ToHashSet(), scenario);
            }
        }
        List<DateOnly> Run(int length, int? fromIndex = null)
        {
            if (days.Count == 0) return [];
            length = Math.Min(length, days.Count);
            var start = fromIndex ?? _rng.Next(0, days.Count - length + 1);
            return days.Skip(Math.Min(start, days.Count - length)).Take(length).ToList();
        }
        int IndexOf(DateOnly d) => days.IndexOf(d);
        var wedThuSun = days.Where(d => d.DayOfWeek == DayOfWeek.Wednesday).Select(IndexOf).FirstOrDefault(i => i >= 0 && i + 2 < days.Count);
        var aroundHoliday = days.FindIndex(d => d > holiday) - 1;   // Monday before the holiday, then Wednesday, Thursday

        Script("1 day absent", 12, () => Run(1));
        Script("2 consecutive days", 9, () => Run(2));
        Script("3 consecutive days", 7, () => Run(3));
        Script("3 consecutive days across a weekend", 3, () => Run(3, wedThuSun));
        Script("3 consecutive days across the holiday", 3, () => aroundHoliday >= 0 ? Run(3, aroundHoliday) : Run(3));
        Script("4 consecutive days", 6, () => Run(4));
        Script("5 consecutive days", 5, () => Run(5));
        Script("Repeated absences across weeks", 6, () =>
        {
            var a = Run(2, 0);
            var b = Run(1, Math.Min(days.Count - 1, 6));
            var c = Run(3, Math.Max(0, days.Count - 4));
            return [.. a, .. b, .. c];
        });
        // Absent, then a school day nobody recorded, then absent twice: never three recorded absences in a row → no alert.
        for (var i = 0; i < 3; i++)
        {
            var r = Run(4);
            if (r.Count == 4) plan[Take().Id] = ([r[0], r[2], r[3]], [r[1]], "Absent, a day not recorded, absent again (no alert)");
        }
        Script("Occasional absences", 10, () => days.Where(_ => Chance(0.12)).ToList());

        // ---- record day by day, class by class (the way the gate checklist is saved)
        var method = _students.ToDictionary(s => s.Id, _ => _rng.NextDouble() switch { < 0.45 => ArrivalMethod.SchoolBus, < 0.8 => ArrivalMethod.PrivateCar, _ => ArrivalMethod.ParentDropOff });
        var oftenLate = _students.Where(_ => Chance(0.05)).Select(s => s.Id).ToHashSet();
        var stage = _classes.ToDictionary(c => c.Id, c => c.Stage);
        var lastAbsent = new Dictionary<int, DateOnly>();
        var saved = 0;
        var failed = 0;
        var unrecorded = 0;
        // Thursday 12 November: most classes recorded, four classes not yet (an "unrecorded" day for them).
        var notYet = _classes.OrderBy(_ => _rng.Next()).Take(4).Select(c => c.Id).ToHashSet();

        foreach (var date in days.Concat(thursday))
        {
            foreach (var cls in _classes)
            {
                if (thursday.Contains(date) && notYet.Contains(cls.Id)) { unrecorded += _students.Count(s => s.ClassId == cls.Id); continue; }
                var entries = new List<ArrivalEntry>();
                foreach (var s in _students.Where(x => x.ClassId == cls.Id))
                {
                    var scripted = plan.TryGetValue(s.Id, out var p);
                    if (scripted && p.Skip.Contains(date)) { unrecorded++; continue; }
                    if (!scripted && Chance(0.006)) { unrecorded++; continue; }   // occasionally missed at the gate
                    var absent = scripted ? p.Absent.Contains(date)
                        // background: a rare single absence, never two days in a row (no unplanned streaks)
                        : Chance(0.018) && !(lastAbsent.TryGetValue(s.Id, out var prev) && (date.DayNumber - prev.DayNumber) <= 4);
                    if (absent)
                    {
                        lastAbsent[s.Id] = date;
                        entries.Add(new ArrivalEntry(s.Id, ArrivalStatus.Absent, null, null, null, null, false,
                            Chance(0.45) ? Pick(ExcusedNotes) : Chance(0.6) ? Pick(UnexcusedNotes) : null));
                        continue;
                    }
                    // First school day back after a scripted absence.
                    var previous = days.LastOrDefault(d => d < date);
                    var returning = scripted && p.Absent.Contains(previous);
                    var m = method[s.Id];
                    var location = m == ArrivalMethod.SchoolBus ? "Bus Bay" : stage[cls.Id] == "KG" && m == ArrivalMethod.ParentDropOff ? "KG Gate" : "Main Gate";
                    var minutes = m == ArrivalMethod.SchoolBus ? _rng.Next(10, 26) : _rng.Next(5, 38);
                    if (m != ArrivalMethod.SchoolBus && (oftenLate.Contains(s.Id) ? Chance(0.5) : Chance(0.05))) minutes = _rng.Next(41, 58);
                    if (returning && Chance(0.5)) minutes = _rng.Next(42, 70); // first day back: often arrives with a parent, late
                    var note = returning ? Pick(new[] { "Returned after absence — note handed to the class teacher.", "Back at school after illness.", "Returned; parent met the coordinator." })
                        : minutes > 40 ? Pick(new[] { "Traffic on the ring road.", "Bus 3 delayed.", "Overslept — parent informed.", (string?)null }) : null;
                    entries.Add(new ArrivalEntry(s.Id, ArrivalStatus.Present, new TimeOnly(7, 0).AddMinutes(minutes), returning && minutes > 41 ? ArrivalMethod.ParentDropOff : m,
                        location, null, false, note));
                }
                if (entries.Count == 0) continue;
                var results = await S<IArrivalService>().RecordAsync(date, entries);
                Tick();
                saved += results.Count(r => r.Saved);
                failed += results.Count(r => !r.Saved);
            }
        }

        // Alerts for every streak in the period, including records saved before alerts existed.
        await S<IAttendanceAlertService>().RecheckAsync(AttendanceFrom.AddDays(-7), _today);
        NewScope();

        // Entry times of the generated rows: the morning they belong to.
        await Db.Database.ExecuteSqlRawAsync($"""
            UPDATE x SET CreatedAt = DATEADD(MINUTE, DATEDIFF(MINUTE, '00:00', COALESCE(x.ArrivalTime, '07:45')) + 3, CAST(x.ArrivalDate AS datetime2)),
                         UpdatedAt = DATEADD(MINUTE, DATEDIFF(MINUTE, '00:00', COALESCE(x.ArrivalTime, '07:45')) + 3, CAST(x.ArrivalDate AS datetime2))
            FROM StudentArrivalRecords x
            JOIN dbo.{TestDataTracker.Table} r ON r.Kind = 'A' AND r.Phase = 'school-attendance' AND r.EntityType = '{typeof(StudentArrivalRecord).FullName}'
                 AND TRY_CAST(JSON_VALUE(r.KeyJson, '$[0]') AS bigint) = x.Id
            """);
        if (failed > 0) Skipped.Add($"{failed} school attendance entries were not saved");
        var scenarios = plan.Values.GroupBy(p => p.Scenario).Select(g => $"{g.Key} ×{g.Count()}");
        Step($"{saved} school attendance records on {days.Count + thursday.Count} school days ({unrecorded} student-days left unrecorded); scenarios: {string.Join(", ", scenarios)}");
    }

    // ------------------------------------------------------------------ session (lesson) attendance

    private async Task SessionAttendanceAsync()
    {
        // Lessons are read from the timetable for each school day of the week(s) the sessions are active and already past.
        var firstWeek = await Db.TimetableAssignments.AsNoTracking().MinAsync(t => (DateOnly?)t.StartsOn);
        if (firstWeek is null) { Notes.Add("Session attendance: the timetable has no sessions."); return; }
        var days = Days(firstWeek.Value, _today.AddDays(-1)).ToList();
        user.Name = "Class teachers";

        // Students who skip particular lessons although they are at school (the lesson alerts' main story).
        var skippers = _students.OrderBy(_ => _rng.Next()).Take(14).ToDictionary(s => s.Id, _ => new HashSet<string>());
        var subjects = await Db.TimetableAssignments.AsNoTracking().Select(t => t.Subject).Distinct().ToListAsync();
        foreach (var set in skippers.Values) set.Add(Pick(subjects));

        var lessons = 0;
        var savedStudents = 0;
        var pendingLessons = 0;
        var cancelled = 0;
        var absentWhilePresent = 0;
        var cancelDay = days.FirstOrDefault(d => d.DayOfWeek == DayOfWeek.Tuesday);
        var cancelClass = _classes.Count > 3 ? _classes[3].Id : _classes.FirstOrDefault().Id;

        foreach (var date in days)
        {
            var day = await S<ISessionAttendanceService>().DayAsync(new SessionFilter(date));
            foreach (var row in day.Lessons)
            {
                var lesson = row.Lesson;
                // A class trip: two lessons of one class are cancelled (no attendance expected, no alerts).
                if (date == cancelDay && lesson.ClassId == cancelClass && lesson.SlotNumber is 4 or 5)
                {
                    await Must(() => S<ISessionAttendanceService>().CancelAsync(lesson.SessionId, date, "Class trip to the Science Museum."), "Cancel lesson");
                    cancelled++;
                    continue;
                }
                // Some lessons not recorded yet (pending): mostly the last periods of the most recent day.
                if ((date == days[^1] && lesson.SlotNumber >= 6 && Chance(0.6)) || Chance(0.04)) { pendingLessons++; continue; }

                var register = await S<ISessionAttendanceService>().RegisterAsync(lesson.SessionId, date);
                if (register is null) continue;
                var entries = new List<SessionEntry>();
                foreach (var r in register.Rows)
                {
                    if (Chance(0.015)) continue; // a name missed on the register: stays "Not recorded"
                    SessionAttendanceStatus status;
                    string? note = null;
                    if (r.School == ArrivalStatus.Absent)
                    {
                        if (Chance(0.1)) continue;   // the teacher left absent-from-school students unmarked
                        status = SessionAttendanceStatus.Absent;
                        note = "Absent from school today.";
                    }
                    else if (r.School is null)
                    {
                        // School attendance not recorded for the day: the lesson is still taken; no alerts are possible.
                        status = Chance(0.92) ? SessionAttendanceStatus.Present : Chance(0.5) ? SessionAttendanceStatus.Late : SessionAttendanceStatus.Absent;
                    }
                    else if (skippers.TryGetValue(r.StudentId, out var skipped) && skipped.Contains(lesson.Subject) && Chance(0.85))
                    {
                        status = SessionAttendanceStatus.Absent;
                        note = Pick(new[] { "Not in class — seen in the corridor.", "Did not come back from break.", (string?)null });
                        absentWhilePresent++;
                    }
                    else if (r.SchoolLate == LateStatus.Late && lesson.SlotNumber == 1)
                    {
                        status = SessionAttendanceStatus.Late;
                        note = "Arrived late to school.";
                    }
                    else
                    {
                        var x = _rng.NextDouble();
                        status = x switch
                        {
                            < 0.012 => SessionAttendanceStatus.Absent,
                            < 0.042 => SessionAttendanceStatus.Late,
                            < 0.060 => SessionAttendanceStatus.Excused,
                            _ => SessionAttendanceStatus.Present,
                        };
                        if (status == SessionAttendanceStatus.Absent) { absentWhilePresent++; note = Pick(new[] { "Left the room without permission.", "Not in class.", (string?)null }); }
                        if (status == SessionAttendanceStatus.Excused) note = Pick(new[] { "At the nurse's office.", "Music exam.", "Counsellor appointment.", "Representing the school at the debate." });
                        if (status == SessionAttendanceStatus.Late) note = Pick(new[] { "Came late from break.", "Came from the previous lesson's lab.", (string?)null });
                    }
                    entries.Add(new SessionEntry(r.StudentId, status, note));
                }
                if (entries.Count == 0) { pendingLessons++; continue; }
                user.Name = lesson.SubstituteName ?? lesson.TeacherName;
                await Must(() => S<ISessionAttendanceService>().SaveAsync(lesson.SessionId, date, entries, null), $"Lesson attendance {lesson.Subject} {lesson.ClassName} {date}");
                lessons++;
                savedStudents += entries.Count;
            }
        }

        // Corrections: some "absent" students had a reason (clears their alert; the change is kept in the history).
        NewScope();
        var toCorrect = await Db.SessionAttendance.AsNoTracking()
            .Where(r => r.Status == SessionAttendanceStatus.Absent && r.Notes != "Absent from school today.")
            .OrderBy(r => r.Id).Take(400).Select(r => new { r.TimetableAssignmentId, r.SessionDate, r.StudentId, Teacher = r.Session.Teacher.FullName }).ToListAsync();
        var corrected = 0;
        foreach (var r in toCorrect.Where(_ => Chance(0.08)).Take(18))
        {
            user.Name = r.Teacher;
            await Must(() => S<ISessionAttendanceService>().SaveAsync(r.TimetableAssignmentId, r.SessionDate,
                [new SessionEntry(r.StudentId, SessionAttendanceStatus.Late, "Arrived from the clinic with a note.")], "Student was at the clinic; arrived with a nurse's note."), "Lesson correction");
            corrected++;
        }

        // Entry times: a few minutes after the lesson starts (planned period times when the school has not set them).
        NewScope();
        foreach (var (number, (start, _)) in _slotTimes)
        {
            var minutes = start.Hour * 60 + start.Minute + 6;
            await Db.Database.ExecuteSqlRawAsync($"""
                UPDATE x SET CreatedAt = DATEADD(MINUTE, {minutes}, CAST(x.SessionDate AS datetime2)), UpdatedAt = DATEADD(MINUTE, {minutes} + (CASE WHEN EXISTS (SELECT 1 FROM SessionAttendanceCorrections c WHERE c.SessionAttendanceRecordId = x.Id) THEN 95 ELSE 0 END), CAST(x.SessionDate AS datetime2))
                FROM SessionAttendanceRecords x JOIN TimetableAssignments t ON t.Id = x.TimetableAssignmentId JOIN TimeSlots s ON s.Id = t.TimeSlotId
                JOIN dbo.{TestDataTracker.Table} r ON r.Kind = 'A' AND r.Phase = 'session-attendance' AND r.EntityType = '{typeof(SessionAttendanceRecord).FullName}'
                     AND TRY_CAST(JSON_VALUE(r.KeyJson, '$[0]') AS bigint) = x.Id
                WHERE s.Number = {number}
                """);
        }
        await Db.Database.ExecuteSqlRawAsync($"""
            UPDATE c SET CorrectedAt = DATEADD(MINUTE, 95, x.CreatedAt) FROM SessionAttendanceCorrections c JOIN SessionAttendanceRecords x ON x.Id = c.SessionAttendanceRecordId
            JOIN dbo.{TestDataTracker.Table} r ON r.Kind = 'A' AND r.Phase = 'session-attendance' AND r.EntityType = '{typeof(SessionAttendanceCorrection).FullName}'
                 AND TRY_CAST(JSON_VALUE(r.KeyJson, '$[0]') AS bigint) = c.Id
            """);
        Step($"{savedStudents} lesson attendance records in {lessons} lessons over {days.Count} school days; {pendingLessons} lessons pending, {cancelled} cancelled, " +
             $"{absentWhilePresent} absences while at school, {corrected} corrections");
    }

    // ------------------------------------------------------------------ alert follow-up

    private async Task AlertFollowUpsAsync()
    {
        var alerts = await Db.AttendanceAlerts.AsNoTracking().Where(a => a.Status == AttendanceAlertStatus.Open)
            .OrderBy(a => a.LastDate).ThenBy(a => a.Id).Select(a => new { a.Id, a.Type, a.LastDate }).ToListAsync();
        var coordinator = Mgmt(ManagementPosition.Sqam);
        var director = Mgmt(ManagementPosition.SchoolDirector);
        int followed = 0, resolved = 0;
        foreach (var a in alerts)
        {
            var streak = a.Type == AttendanceAlertType.ConsecutiveAbsence;
            var recent = a.LastDate >= _today.AddDays(-3);
            var x = _rng.NextDouble();
            // Older alerts are mostly handled; the most recent ones are still open.
            var resolve = !recent && x < (streak ? 0.45 : 0.35);
            var follow = !resolve && x < (streak ? 0.8 : 0.55) && !(recent && Chance(0.5));
            if (!resolve && !follow) continue;
            user.Name = coordinator.Name;
            await Must(() => S<IAttendanceAlertService>().FollowUpAsync(a.Id, streak
                ? Pick(new[] { "Called the mother: the student has the flu; a medical note will be sent.", "Parent did not answer; SMS sent and a message left with the bus supervisor.",
                    "Father informed us of a family emergency; homework sent through the class group.", "Called the guardian — the student is back on Sunday." })
                : Pick(new[] { "Spoke with the student: was in the toilets and lost track of time.", "Class teacher confirmed the student was not in the lesson; parent informed.",
                    "Student says they were with the counsellor — checking with the counsellor.", "Met the student with the coordinator; warned about leaving lessons." })), "Alert follow-up");
            followed++;
            if (!resolve) continue;
            user.Name = director.Name;
            await Must(() => S<IAttendanceAlertService>().ResolveAsync(a.Id, streak
                ? Pick(new[] { "Medical note received; student back at school.", "Parent meeting held; attendance plan agreed.", "Absence explained and excused by the principal." })
                : Pick(new[] { "Student apologised; parent informed. Monitoring for two weeks.", "Explained — the student was at the clinic (note received).", "Verbal warning given and logged." })), "Alert resolution");
            resolved++;
        }

        // Event times: the morning after the last absence, follow-ups the following days (never in the future).
        NewScope();
        await Db.Database.ExecuteSqlRawAsync($"""
            WITH e AS (
                SELECT ev.Id, ev.AttendanceAlertId, a.LastDate,
                       ROW_NUMBER() OVER (PARTITION BY ev.AttendanceAlertId ORDER BY ev.Id) - 1 AS n
                FROM AttendanceAlertEvents ev JOIN AttendanceAlerts a ON a.Id = ev.AttendanceAlertId
                JOIN dbo.{TestDataTracker.Table} r ON r.Kind = 'A' AND r.EntityType = '{typeof(AttendanceAlertEvent).FullName}'
                     AND TRY_CAST(JSON_VALUE(r.KeyJson, '$[0]') AS bigint) = ev.Id)
            UPDATE ev SET At = (SELECT MIN(v) FROM (VALUES (DATEADD(MINUTE, 600 + 30 * (e.Id % 7), DATEADD(DAY, e.n, CAST(e.LastDate AS datetime2)))), (DATEADD(MINUTE, -5, SYSUTCDATETIME()))) AS t(v))
            FROM AttendanceAlertEvents ev JOIN e ON e.Id = ev.Id
            """);
        await Db.Database.ExecuteSqlRawAsync($"""
            UPDATE a SET CreatedAt = (SELECT MIN(At) FROM AttendanceAlertEvents ev WHERE ev.AttendanceAlertId = a.Id),
                         UpdatedAt = (SELECT MAX(At) FROM AttendanceAlertEvents ev WHERE ev.AttendanceAlertId = a.Id),
                         ResolvedAt = CASE WHEN a.ResolvedAt IS NULL THEN NULL ELSE (SELECT MAX(At) FROM AttendanceAlertEvents ev WHERE ev.AttendanceAlertId = a.Id) END
            FROM AttendanceAlerts a
            JOIN dbo.{TestDataTracker.Table} r ON r.Kind = 'A' AND r.EntityType = '{typeof(AttendanceAlert).FullName}' AND TRY_CAST(JSON_VALUE(r.KeyJson, '$[0]') AS bigint) = a.Id
            """);
        Step($"{followed} alerts followed up, {resolved} resolved (the most recent ones left open)");
    }
}
