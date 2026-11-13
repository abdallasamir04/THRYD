using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Web.Data.TestData;

/// <summary>
/// Fictional official incident reports, created through IIncidentService like a coordinator would: header,
/// students involved (existing generated students), timeline, findings &amp; actions, sign-off and closure.
/// Statuses follow the age of the incident (recent ones are still reported / under investigation).
/// Names in the texts are the students of the report. {A} = student with the reported conduct, {B} = affected
/// student, {W} = intervening student or witness, {T} = the staff member.
/// </summary>
public sealed partial class TestDataSeeder
{
    private sealed record IncidentKind(
        string Category, IncidentSeverity[] Severities, string[] Departments, bool HasVictim,
        string[] Conduct, string[] Affected, string[] Intervening, string[] Witness,
        (string Period, int H, int M, string Location, string Account)[][] Timelines,
        (string Item, string Finding, string? Details)[] Actions);

    private static readonly (string Item, string Finding, string? Details) Inquiry =
        ("Individual Accounts Inquiry", "All students involved were interviewed separately and given the opportunity to share their accounts.", "Accounts recorded and signed by the students; consistent on the main facts.");

    private static readonly IncidentKind[] IncidentKinds =
    [
        new("Physical Aggression & Verbal Misconduct", [IncidentSeverity.Level2, IncidentSeverity.Level3], ["Discipline Department", "WSD"], true,
            ["Pushed {B} against the wall during the line-up and used insulting language.", "Hit {B} on the shoulder after an argument and shouted abuse.", "Shoved {B} and threw {B}'s bag on the floor."],
            ["Was pushed and verbally insulted; no injury. Reported feeling upset and unsafe.", "Was hit on the arm; seen by the school nurse — minor bruise, no treatment needed."],
            ["Stepped between the students and asked them to stop, then called the duty teacher.", "Separated the two students and walked {B} to the duty teacher."],
            ["Saw the push from the line and confirmed the insults.", "Heard the argument and saw the bag being thrown."],
            [
                [("Morning Line", 7, 50, "Assembly & Classroom", "An argument started between {A} and {B} in the morning line about a football game."),
                 ("Break Time", 10, 15, "School Yard / Corridor", "The argument resumed in the corridor; {A} pushed {B} and used offensive language. {W} intervened and called {T}."),
                 ("Escalation", 10, 30, "Discipline Office", "Both students were brought to the discipline office, separated and calmed down. Parents were informed by phone.")],
                [("Lunch Break", 12, 5, "Canteen", "{A} and {B} disagreed over a seat; {A} raised their voice and insulted {B}."),
                 ("Lunch Break", 12, 12, "Canteen", "{A} shoved {B}; {W} moved between them and the canteen supervisor ({T}) arrived."),
                 ("After Lunch", 12, 40, "Coordinator's Office", "Statements taken from both students and the witness. {A} accepted that the push was wrong.")],
            ],
            [Inquiry, ("Formal Verbal Warning", "A formal verbal warning was issued and logged on the student's record.", "Parent informed in writing; next incident leads to a formal sanction."),
             ("Behavioral & Conflict Guidance", "Students received guidance on positive communication, respectful peer interaction and peaceful conflict resolution.", "Two sessions with the school counsellor."),
             ("Restorative Apologies Exchanged", "Students exchanged apologies and acknowledged the importance of respectful conduct.", "Restorative conversation facilitated by the coordinator."),
             ("Parent Conference", "Parents of the students involved met the stage coordinator.", "Agreed a behaviour plan with weekly check-ins.")]),

        new("Bullying & Intimidation", [IncidentSeverity.Level3, IncidentSeverity.Level4], ["WSD", "Discipline Department"], true,
            ["Repeatedly mocked {B}'s appearance over two weeks and encouraged others to exclude {B}.", "Took {B}'s lunch and threatened {B} not to tell anyone."],
            ["Was mocked and excluded repeatedly; told the class teacher after a difficult week.", "Felt afraid to come to school; parent contacted the school."],
            ["Told the class teacher what was happening and supported {B}.", "Refused to join in and reported it to the counsellor."],
            ["Confirmed the comments in the group chat and in class.", "Saw {B} being followed at break on several days."],
            [
                [("Over two weeks", 0, 0, "Classroom & Corridor", "{B} was repeatedly mocked by {A}; other students laughed and avoided {B}."),
                 ("Break Time", 10, 20, "School Yard", "{A} blocked {B}'s way and made threatening remarks. {W} reported it to {T}."),
                 ("Follow-up", 13, 0, "Counsellor's Office", "{B} met the counsellor and described the pattern of behaviour. Safeguarding lead informed.")],
                [("Morning", 8, 10, "Bus Bay", "{B} arrived upset; the bus supervisor noticed {A} taking {B}'s snack."),
                 ("Second Period", 9, 0, "Classroom", "{B} asked to see the counsellor and reported threats from {A}."),
                 ("Afternoon", 13, 30, "Discipline Office", "{A} was interviewed by {T} and admitted the behaviour.")],
            ],
            [Inquiry, ("Safeguarding Referral", "The safeguarding lead reviewed the case and opened a support plan for the affected student.", "Weekly check-ins with the counsellor for four weeks."),
             ("Behaviour Contract", "A behaviour contract was signed by the student and parents.", "Reviewed every two weeks by the stage coordinator."),
             ("Parent Conference", "Parents of both students were invited to separate meetings.", "Parents supported the plan."),
             ("Monitoring Plan", "Supervising staff at break and on the bus were asked to monitor the students.", "Monitoring for six weeks; no further reports so far.")]),

        new("Cyberbullying / Misuse of Technology", [IncidentSeverity.Level2, IncidentSeverity.Level3], ["ICT", "Discipline Department", "WSD"], true,
            ["Posted an edited photo of {B} in the class group chat with offensive comments.", "Created a fake account using {B}'s name and sent messages to classmates."],
            ["Was the target of the posts; parent sent screenshots to the school.", "Felt humiliated in front of classmates; asked for the posts to be removed."],
            ["Told {A} to delete the post and informed the ICT teacher.", "Showed the messages to the class teacher."],
            ["Saw the posts in the group chat.", "Received the messages from the fake account."],
            [
                [("Evening (outside school)", 20, 30, "Online — class group chat", "{A} posted an edited photo of {B} with offensive comments; several students reacted."),
                 ("Next Morning", 7, 55, "Classroom", "{B}'s parent emailed screenshots to the school; {W} confirmed the posts to {T}."),
                 ("Second Period", 9, 10, "ICT Lab / Discipline Office", "{A} was interviewed and deleted the post in front of the ICT teacher.")],
                [("During ICT lesson", 11, 0, "ICT Lab", "{A} used a school device to message classmates from an account in {B}'s name."),
                 ("Same Day", 11, 30, "ICT Lab", "The ICT teacher ({T}) found the account in the browser history; screenshots saved."),
                 ("Afternoon", 13, 15, "Coordinator's Office", "Parents contacted; the account was reported and removed.")],
            ],
            [Inquiry, ("Digital Citizenship Session", "The student completed a digital citizenship and online safety session with the ICT teacher.", "Session on consent, privacy and respectful communication online."),
             ("Device Restriction", "School device access was restricted for one week.", null),
             ("Restorative Apologies Exchanged", "A written apology was given to the affected student.", "Apology read in a restorative meeting with the counsellor."),
             ("Parent Conference", "Parents were informed and shown the evidence.", "Parents agreed to supervise phone use at home.")]),

        new("Property Damage", [IncidentSeverity.Level1, IncidentSeverity.Level2], ["Discipline Department", "Science", "Art"], false,
            ["Broke a microscope slide box after being asked to stop playing with the equipment.", "Wrote on a desk and damaged a classroom chair while rocking on it."],
            [], ["Warned {A} to stop and told the teacher."], ["Saw the damage happen."],
            [
                [("Third Period", 9, 50, "Science Lab", "{A} played with the slide box after being asked twice to stop; the box fell and broke."),
                 ("Third Period", 10, 0, "Science Lab", "The teacher ({T}) secured the area and recorded the damage."),
                 ("After School", 14, 20, "Coordinator's Office", "Parent informed about the damage and the school's replacement policy.")],
                [("Fifth Period", 11, 30, "Classroom", "{A} wrote on the desk with a marker and broke a chair leg while rocking on it."),
                 ("End of Day", 13, 50, "Classroom", "The class teacher ({T}) photographed the damage and informed the coordinator.")],
            ],
            [Inquiry, ("Restitution", "The student cleaned the desk and contributed to the replacement cost.", "Parent paid the replacement cost of the equipment."),
             ("Formal Verbal Warning", "A formal verbal warning was issued and logged.", null),
             ("Lab Safety Briefing", "The student repeated the laboratory safety briefing.", "Signed the lab rules again.")]),

        new("Theft / Missing Property", [IncidentSeverity.Level2, IncidentSeverity.Level3], ["Discipline Department", "WSD"], true,
            ["Took {B}'s calculator from the bag during PE and kept it in the locker.", "Took money from {B}'s pencil case during break."],
            ["Reported the item missing after PE.", "Reported money missing from the pencil case."],
            [], ["Saw the item in {A}'s locker.", "Saw {A} near the bags during break."],
            [
                [("P.E. Lesson", 10, 40, "Changing Room", "{B}'s calculator went missing while the class was at P.E."),
                 ("After P.E.", 11, 30, "Classroom", "{B} reported the missing item to {T}; {W} said they saw it in {A}'s locker."),
                 ("Same Day", 12, 15, "Discipline Office", "With the student present, the locker was checked and the calculator was found and returned.")],
            ],
            [Inquiry, ("Item Returned", "The item was returned to its owner the same day.", null),
             ("Formal Written Warning", "A formal written warning was issued and logged.", "Parents signed the warning letter."),
             ("Restorative Apologies Exchanged", "The student apologised to the affected student.", "Restorative meeting with the counsellor.")]),

        new("Disruptive Classroom Behaviour", [IncidentSeverity.Level1], ["Math", "English", "Arabic", "Science"], false,
            ["Repeatedly talked over the teacher, threw paper and refused to move seats.", "Played music on a phone during the lesson and refused to hand it in."],
            [], [], ["Sat next to {A} and confirmed the behaviour."],
            [
                [("Second Period", 8, 45, "Classroom", "{A} talked over the teacher several times and threw paper at classmates."),
                 ("Second Period", 9, 0, "Classroom", "{T} asked {A} to move seats; {A} refused and the lesson was interrupted."),
                 ("Break Time", 10, 15, "Classroom", "{T} spoke with {A} privately and recorded the incident.")],
                [("Sixth Period", 12, 20, "Classroom", "{A} played music on a phone during the lesson."),
                 ("Sixth Period", 12, 25, "Classroom", "{A} refused to hand the phone to {T}; the coordinator was called and collected the phone.")],
            ],
            [Inquiry, ("Formal Verbal Warning", "A formal verbal warning was issued and logged.", null),
             ("Behavior Report Card", "The student carries a daily behaviour report card for two weeks.", "Signed by each teacher and by the parent."),
             ("Phone Confiscated", "The phone was kept by the coordinator and returned to the parent.", "In line with the device policy.")]),

        new("Academic Dishonesty", [IncidentSeverity.Level2], ["Math", "English", "Science", "Arabic"], false,
            ["Used notes hidden in a pencil case during the unit test.", "Copied a classmate's homework and submitted it as their own."],
            [], [], ["Sat next to {A} during the test."],
            [
                [("Unit Test", 9, 30, "Classroom", "{T} found notes hidden in {A}'s pencil case during the unit test."),
                 ("After the Test", 10, 20, "Department Office", "{A} admitted using the notes; the paper was kept by the head of department.")],
            ],
            [Inquiry, ("Assessment Reassigned", "The test was cancelled for this student and a new assessment was set.", "Resit supervised by the head of department."),
             ("Academic Integrity Guidance", "The student met the head of department about academic integrity.", null),
             ("Parent Notified", "Parents were informed in writing.", null)]),

        new("Unsafe Behaviour", [IncidentSeverity.Level1, IncidentSeverity.Level2], ["PE", "Discipline Department"], false,
            ["Climbed on the fence and the goalpost during break despite warnings.", "Stood up and moved around while the school bus was moving."],
            [], ["Asked {A} to come down and told the duty teacher."], ["Saw {A} on the fence."],
            [
                [("Break Time", 10, 20, "Football Pitch", "{A} climbed the goalpost and then the fence; {W} asked {A} to come down."),
                 ("Break Time", 10, 25, "Football Pitch", "The duty teacher ({T}) brought {A} down safely and recorded the incident.")],
                [("Morning Bus", 7, 20, "School Bus", "{A} stood up and moved between seats while the bus was moving."),
                 ("Arrival", 7, 35, "Bus Bay", "The bus supervisor reported the behaviour to {T}.")],
            ],
            [Inquiry, ("Safety Briefing", "The student received a safety briefing from the P.E. department.", null),
             ("Formal Verbal Warning", "A formal verbal warning was issued and logged.", null),
             ("Seat Assigned on the Bus", "A fixed seat near the supervisor was assigned for two weeks.", null)]),

        new("Leaving Class Without Permission", [IncidentSeverity.Level1, IncidentSeverity.Level2], ["Discipline Department"], false,
            ["Left the classroom without permission and was found in the corridor 20 minutes later.", "Did not return to class after break and stayed in the toilets."],
            [], [], ["Saw {A} in the corridor."],
            [
                [("Fourth Period", 10, 50, "Classroom", "{A} left the classroom without permission while the teacher was helping another student."),
                 ("Fourth Period", 11, 10, "Corridor", "{T} found {A} in the corridor and walked {A} back to class.")],
            ],
            [Inquiry, ("Formal Verbal Warning", "A formal verbal warning was issued and logged.", null),
             ("Lesson Attendance Monitoring", "The student's lesson attendance is checked by the coordinator each day for two weeks.", null)]),

        new("Inappropriate Language", [IncidentSeverity.Level1], ["Arabic", "English", "WSD"], true,
            ["Used offensive language towards {B} during group work.", "Made an inappropriate comment to {B} in front of the class."],
            ["Was upset by the comment and told the teacher."], [], ["Heard the comment."],
            [
                [("Group Work", 11, 15, "Classroom", "During group work {A} used offensive language towards {B}."),
                 ("Same Lesson", 11, 25, "Classroom", "{T} stopped the activity, spoke to both students and recorded the incident.")],
            ],
            [Inquiry, ("Restorative Apologies Exchanged", "The student apologised to the affected student.", null),
             ("Formal Verbal Warning", "A formal verbal warning was issued and logged.", null)]),

        new("Serious Safeguarding Concern", [IncidentSeverity.Level4], ["WSD"], true,
            ["Threatened {B} with harm in a message and at school.", "Brought a sharp object to school and showed it to classmates."],
            ["Received the threat and was very frightened; parent called the school."], ["Reported the object to the duty teacher immediately."], ["Saw the object in the bag."],
            [
                [("Morning", 7, 45, "Main Gate", "{B}'s parent called the school about a threatening message from {A}."),
                 ("First Period", 8, 5, "Principal's Office", "The safeguarding lead ({T}) met {B}; {A} was kept apart from {B}."),
                 ("Mid-morning", 9, 30, "Principal's Office", "{A}'s parents were called in; the school's safeguarding procedure was followed."),
                 ("Afternoon", 13, 0, "Principal's Office", "A risk assessment was completed and a safety plan agreed.")],
            ],
            [Inquiry, ("Safeguarding Referral", "The case was referred to the safeguarding lead and recorded under the safeguarding policy.", "External advice requested where required."),
             ("Risk Assessment", "A risk assessment was completed for both students.", "Safety plan shared with the staff concerned."),
             ("Suspension (Internal)", "The student completed two days of internal exclusion with supervised work.", "Return meeting held with the parents."),
             ("Monitoring Plan", "Both students are monitored by the pastoral team.", "Reviewed weekly.")]),
    ];

    private async Task IncidentsAsync()
    {
        if (_students.Count == 0 || _classes.Count == 0) { Notes.Add("Incidents: no generated students to use."); return; }
        var holidays = await Db.SchoolHolidays.AsNoTracking().Select(h => new { h.StartDate, h.EndDate }).ToListAsync();
        var schoolStart = new DateOnly(2026, 9, 13);
        var dates = Days(schoolStart, _today.AddDays(-1)).Where(d => !holidays.Any(h => h.StartDate <= d && d <= h.EndDate)).ToList();
        var director = Mgmt(ManagementPosition.SchoolDirector);
        var sqam = Mgmt(ManagementPosition.Sqam);
        var school = (await S<ISettingsService>().GetProfileAsync()).SchoolName;
        var byClass = _students.GroupBy(s => s.ClassId).ToDictionary(g => g.Key, g => g.ToList());
        var upper = _classes.Where(c => c.Stage is "Upper" or "Primary").Select(c => c.Id).Where(byClass.ContainsKey).ToList();
        var allClasses = _classes.Select(c => c.Id).Where(byClass.ContainsKey).ToList();
        // A few students appear in several reports (their incident history has more than one entry).
        var repeat = upper.OrderBy(_ => _rng.Next()).Take(8).Select(c => Pick(byClass[c])).ToList();
        var staffForTimeline = _teachers.Count > 0 ? _teachers : [new Staff(0, "the duty teacher", "", "")];

        Staff Coordinator(int classId)
        {
            var stage = _classes.First(c => c.Id == classId).Stage;
            var key = GradeCatalog.KeyOf(_classes.First(c => c.Id == classId).Name);
            var position = stage == "KG" ? ManagementPosition.KgStageCoordinator
                : key is "G1" or "G2" or "G3" ? ManagementPosition.LowerElementaryCoordinator
                : key is "G4" or "G5" ? ManagementPosition.UpperElementaryCoordinator
                : key is "G6" or "G7" or "G8" ? ManagementPosition.MiddleSchoolCoordinator : ManagementPosition.HighSchoolCoordinator;
            return Mgmt(position);
        }

        const int count = 52;
        int created = 0, closed = 0, students = 0, events = 0, actions = 0;
        for (var i = 0; i < count; i++)
        {
            // Spread over the year, a little denser in recent weeks.
            var date = dates[Math.Min(dates.Count - 1, (int)(Math.Sqrt(_rng.NextDouble()) * dates.Count))];
            var kind = i < IncidentKinds.Length ? IncidentKinds[i] : Pick(IncidentKinds);
            var severity = Pick(kind.Severities);
            var department = Pick(kind.Departments);

            // Students: the reported student (sometimes a "repeat" student) and classmates.
            var lead = kind.Severities[0] != IncidentSeverity.Level1 && Chance(0.35) ? Pick(repeat) : Pick(byClass[Pick(Chance(0.85) ? upper : allClasses)]);
            var classmates = byClass[lead.ClassId].Where(s => s.Id != lead.Id).OrderBy(_ => _rng.Next()).ToList();
            var others = Math.Min(classmates.Count, kind.HasVictim ? _rng.Next(1, 4) : _rng.Next(0, 3));
            var involved = new List<((int Id, string Name, int ClassId) S, IncidentRole Role, string Text)>();
            string Fill(string text, string a, string b, string w, string t) => text.Replace("{A}", a).Replace("{B}", b).Replace("{W}", w).Replace("{T}", t);
            var victim = kind.HasVictim && others > 0 ? classmates[0] : default;
            var third = others > (kind.HasVictim ? 1 : 0) ? classmates[kind.HasVictim ? 1 : 0] : default;
            var teacher = Pick(staffForTimeline).Name;
            var a = First(lead.Name);
            var b = victim.Id != 0 ? First(victim.Name) : "a classmate";
            var w = third.Id != 0 ? First(third.Name) : "a classmate";

            involved.Add((lead, IncidentRole.ReportedConduct, Fill(Pick(kind.Conduct), a, b, w, teacher)));
            if (victim.Id != 0) involved.Add((victim, IncidentRole.AffectedStudent, Fill(Pick(kind.Affected), a, b, w, teacher)));
            if (third.Id != 0)
            {
                var intervening = kind.Intervening.Length > 0 && Chance(0.6);
                involved.Add((third, intervening ? IncidentRole.InterveningStudent : IncidentRole.Witness,
                    Fill(Pick(intervening ? kind.Intervening : kind.Witness), a, b, w, teacher)));
            }
            if (others == 3 && classmates.Count > 2)
                involved.Add((classmates[2], IncidentRole.Other, $"Filmed part of the incident on a phone and later deleted it when asked by {teacher}."));

            // Status from the age of the incident.
            var age = _today.DayNumber - date.DayNumber;
            IncidentStatus final;
            bool close;
            if (age > 21) { final = Chance(0.8) ? IncidentStatus.Resolved : IncidentStatus.UnderMonitoring; close = Chance(0.75); }
            else if (age > 7) { final = Pick(new[] { IncidentStatus.UnderInvestigation, IncidentStatus.UnderMonitoring, IncidentStatus.Resolved }); close = final != IncidentStatus.UnderInvestigation && Chance(0.3); }
            else { final = Chance(0.5) ? IncidentStatus.Reported : IncidentStatus.UnderInvestigation; close = false; }

            var preparer = Coordinator(lead.ClassId);
            user.Name = preparer.Name;
            user.StaffId = preparer.Id > 0 ? preparer.Id : null;
            var id = await Must(() => S<IIncidentService>().CreateAsync(new IncidentHeaderInput(kind.Category, date, department, severity, IncidentStatus.Reported)), $"Incident {kind.Category}");
            created++;

            foreach (var (s, role, text) in involved)
                await Must(() => S<IIncidentService>().AddStudentAsync(id, new IncidentStudentInput(s.Id, role, role == IncidentRole.Other ? "Filmed on a phone" : null, text)), "Incident student");
            students += involved.Count;

            var rows = (await S<IIncidentService>().GetAsync(id))!.Students;
            var timeline = Pick(kind.Timelines);
            foreach (var (period, h, m, location, account) in timeline)
            {
                var present = rows.Where(r => r.Role != IncidentRole.Other || Chance(0.5)).Select(r => r.Id).ToList();
                await Must(() => S<IIncidentService>().AddTimelineAsync(id, new TimelineInput(period, h == 0 ? null : new TimeOnly(h, m), location, present,
                    Chance(0.3) ? Pick(new[] { "Duty teacher", "Other students in the class", "Bus supervisor", "Canteen staff" }) : null,
                    Fill(account, a, b, w, teacher))), "Incident timeline");
                events++;
            }

            if (final == IncidentStatus.Reported) continue;
            await Must(() => S<IIncidentService>().UpdateHeaderAsync(id, new IncidentHeaderInput(kind.Category, date, department, severity, IncidentStatus.UnderInvestigation)), "Incident status");

            var chosen = new List<(string Item, string Finding, string? Details)> { kind.Actions[0] };
            chosen.AddRange(kind.Actions.Skip(1).OrderBy(_ => _rng.Next()).Take(_rng.Next(1, Math.Min(4, kind.Actions.Length - 1) + 1)));
            var finished = final is IncidentStatus.Resolved || (final == IncidentStatus.UnderMonitoring && close);
            for (var k = 0; k < chosen.Count; k++)
            {
                var (item, finding, details) = chosen[k];
                var initial = k == 0 ? IncidentActionStatus.Completed : Chance(0.5) ? IncidentActionStatus.Pending : IncidentActionStatus.InProgress;
                await Must(() => S<IIncidentService>().AddActionAsync(id, new IncidentActionInput(item, finding, details, initial)), "Incident action");
                actions++;
            }
            if (finished || final == IncidentStatus.UnderMonitoring)
            {
                var list = (await S<IIncidentService>().GetAsync(id))!.Actions;
                foreach (var act in list.Where(x => !IncidentLabels.IsFinal(x.Status)))
                {
                    // Under monitoring and still open: the monitoring action stays in progress.
                    if (!finished && act.ActionItem.Contains("Monitoring", StringComparison.Ordinal)) continue;
                    var to = act.ActionItem.Contains("Warning", StringComparison.Ordinal) ? IncidentActionStatus.IssuedAndLogged
                        : act.ActionItem.Contains("Apolog", StringComparison.Ordinal) ? IncidentActionStatus.Resolved : IncidentActionStatus.Completed;
                    await Must(() => S<IIncidentService>().SetActionStatusAsync(id, act.Id, to), "Incident action status");
                }
            }
            if (final == IncidentStatus.UnderInvestigation) continue;
            await Must(() => S<IIncidentService>().UpdateHeaderAsync(id, new IncidentHeaderInput(kind.Category, date, department, severity, final)), "Incident status");

            // Sign-off: approved when the record is closed; otherwise often still waiting for approval.
            var reportDate = date.AddDays(_rng.Next(1, 6));
            if (reportDate >= _today) reportDate = _today.AddDays(-1);
            if (reportDate < date) reportDate = date;
            var reviewer = severity >= IncidentSeverity.Level3 ? director : Chance(0.5) ? director : sqam;
            var approval = close ? (Chance(0.75) ? IncidentApproval.Approved : IncidentApproval.ApprovedWithConditions) : Chance(0.5) ? IncidentApproval.Pending : IncidentApproval.Approved;
            await Must(() => S<IIncidentService>().SaveSignOffAsync(id, new SignOffInput(preparer.Id > 0 ? preparer.Id : null, school, reportDate,
                reviewer.Id > 0 ? reviewer.Id : null, approval,
                approval == IncidentApproval.ApprovedWithConditions ? "Approved on condition that the monitoring plan is reviewed in two weeks." : approval == IncidentApproval.Approved ? "Endorsed by the administration." : null)), "Incident sign-off");
            if (!close) continue;
            user.Name = reviewer.Name;
            await Must(() => S<IIncidentService>().CloseAsync(id), "Incident close");
            closed++;

            // Two closed reports were reopened after new information and closed again (shows in the audit history).
            if (closed is 3 or 9)
            {
                await Must(() => S<IIncidentService>().ReopenAsync(id, "Parent meeting brought new information that had to be added to the report."), "Incident reopen");
                await Must(() => S<IIncidentService>().AddActionAsync(id, new IncidentActionInput("Parent Conference", "A follow-up meeting was held with the parents after new information was shared.", "Parents agreed with the measures taken.", IncidentActionStatus.Completed)), "Incident action");
                await Must(() => S<IIncidentService>().CloseAsync(id), "Incident close");
            }
        }

        // Times: reports written on the day of the incident; their history spread until the report date / closure (never in the future).
        NewScope();
        string Gen(Type t, string alias) =>
            $"JOIN dbo.{TestDataTracker.Table} r ON r.Kind = 'A' AND r.Phase = 'incidents' AND r.EntityType = '{t.FullName}' AND TRY_CAST(JSON_VALUE(r.KeyJson, '$[0]') AS bigint) = {alias}.Id";
        await Db.Database.ExecuteSqlRawAsync($"""
            UPDATE i SET CreatedAt = DATEADD(MINUTE, 660 + i.Id % 200, CAST(i.IncidentDate AS datetime2))
            FROM Incidents i {Gen(typeof(Incident), "i")}
            """);
        await Db.Database.ExecuteSqlRawAsync($"""
            WITH h AS (
                SELECT x.Id, i.CreatedAt AS Start,
                       (SELECT MIN(v) FROM (VALUES (COALESCE(DATEADD(MINUTE, 780, CAST(i.ReportDate AS datetime2)), DATEADD(DAY, 1 + i.Id % 4, i.CreatedAt))), (DATEADD(MINUTE, -10, SYSUTCDATETIME()))) AS t(v)) AS Finish,
                       ROW_NUMBER() OVER (PARTITION BY x.IncidentId ORDER BY x.Id) - 1 AS n, COUNT(*) OVER (PARTITION BY x.IncidentId) AS c
                FROM IncidentHistory x JOIN Incidents i ON i.Id = x.IncidentId {Gen(typeof(IncidentHistory), "x")})
            UPDATE x SET At = CASE WHEN h.c <= 1 OR h.Finish <= h.Start THEN h.Start ELSE DATEADD(SECOND, CAST(DATEDIFF(SECOND, h.Start, h.Finish) AS bigint) * h.n / (h.c - 1), h.Start) END
            FROM IncidentHistory x JOIN h ON h.Id = x.Id
            """);
        await Db.Database.ExecuteSqlRawAsync($"""
            UPDATE i SET UpdatedAt = (SELECT MAX(At) FROM IncidentHistory x WHERE x.IncidentId = i.Id),
                         ClosedAt = CASE WHEN i.IsClosed = 1 THEN (SELECT MAX(At) FROM IncidentHistory x WHERE x.IncidentId = i.Id AND x.Action = 'Record closed') ELSE NULL END
            FROM Incidents i {Gen(typeof(Incident), "i")}
            """);
        await Db.Database.ExecuteSqlRawAsync($"""
            UPDATE a SET UpdatedAt = COALESCE((SELECT MAX(x.At) FROM IncidentHistory x WHERE x.IncidentId = a.IncidentId AND x.Details LIKE a.ActionItem + '%'), i.CreatedAt)
            FROM IncidentActions a JOIN Incidents i ON i.Id = a.IncidentId {Gen(typeof(IncidentAction), "a")}
            """);
        Step($"{created} incident reports ({closed} closed): {students} students involved, {events} timeline events, {actions} findings & actions");
    }

    private static string First(string fullName) => fullName.Split(' ', 2)[0];
}
