using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Tests;

/// <summary>
/// PTC Phase 3: the parent booking portal (identification by the registered parent number, siblings, server-side class and
/// subjects, one booking per child per class schedule, parallel interviews, capacity and duplicate submissions), the
/// "bookings open" definition, KG teachers selected by the Admin, uncovered lesson clashes blocking publishing, the public
/// endpoints (no admin data, rate limiting) and the Admin booking visibility / WhatsApp link. Test database only.
/// </summary>
[Collection("db")]
public class PtcPortalTests(DbFixture fx)
{
    private static readonly DateOnly Day = new(2027, 3, 9); // a Tuesday
    private static readonly DateOnly LessonsFrom = SchoolWeek.StartOf(new DateOnly(2026, 9, 6));
    private static readonly TimeOnly T8 = new(8, 0), T14 = new(14, 0);
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();
    private static readonly Regex Antiforgery = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private sealed class Admin : IPtcAccess { public bool CanManage => true; }
    private sealed class User : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider { public override DateTimeOffset GetUtcNow() => utc; }

    private static ISchoolTimeService At(DateTimeOffset utc) => new SchoolTimeService(new FixedClock(utc), SchoolTimeService.DefaultTimeZoneId);

    private static SettingsService Settings(SchoolDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));
    private static PtcAdminService AdminService(SchoolDbContext db, ISchoolTimeService? time = null) => new(db, new Admin(), Settings(db), new ClassTeamService(db, new User()), time);
    private static PtcSetupService Setup(SchoolDbContext db) => new(db, new Admin(), Settings(db), new User(), AdminService(db));
    private static PtcBookingService Booking(SchoolDbContext db, ISchoolTimeService? time = null) => new(db, new Admin(), new User(), time);

    private static PtcPortalService Portal(SchoolDbContext db, IMemoryCache? cache = null, ISchoolTimeService? time = null, PtcPortalOptions? options = null) =>
        new(db, Booking(db, time), Protection, cache ?? new MemoryCache(new MemoryCacheOptions()), Options.Create(options ?? new PtcPortalOptions()), time);

    private static int Ok(ServiceResult r)
    {
        Assert.True(r.Succeeded, string.Join(" | ", r.Errors.Select(e => e.Value)));
        return r.EntityId ?? 0;
    }

    private static void Fails(ServiceResult r, string text)
    {
        Assert.False(r.Succeeded, "Expected a failure containing: " + text);
        Assert.Contains(r.Errors, e => e.Value.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ scenario helpers (test database only)

    private async Task<(int Event, int Year)> EventAsync()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "PTC3 " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        return (Ok(await Setup(db).SaveEventAsync(null, new PtcEventInput("PTC " + DbFixture.Unique(), null, year.Id, Day, Day.AddDays(4)))), year.Id);
    }

    private async Task<(int Id, string Name)> ClassAsync(string prefix = "P")
    {
        await using var db = fx.NewContext();
        for (var i = 0; i < 50; i++)
        {
            var name = prefix + new string(Enumerable.Range(0, prefix.StartsWith("KG") ? 3 : 4).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray());
            var r = await DbFixture.Classes(db).CreateAsync(name, null, true);
            if (r.Succeeded) return (r.EntityId!.Value, name);
        }
        throw new InvalidOperationException("No free class name.");
    }

    private async Task<int> LessonAsync(int classId, int teacherId, int departmentId, string subject, DayOfWeek day, TimeOnly? start = null, TimeOnly? end = null)
    {
        await using var db = fx.NewContext();
        var slot = new TimeSlot { Number = Random.Shared.Next(100, 999), SchoolClassId = classId, StartTime = start, EndTime = end, UpdatedAt = DateTime.UtcNow };
        db.TimeSlots.Add(slot);
        await db.SaveChangesAsync();
        var lesson = new TimetableAssignment { SchoolClassId = classId, Day = day, TimeSlotId = slot.Id, DepartmentId = departmentId, Subject = subject, TeacherId = teacherId, StartsOn = LessonsFrom };
        db.TimetableAssignments.Add(lesson);
        await db.SaveChangesAsync();
        return lesson.Id;
    }

    private static string Phone() => "010" + Random.Shared.Next(10_000_000, 99_999_999);

    /// <summary>A student named "{first} …" with the given parent number (null: none recorded).</summary>
    private async Task<int> StudentAsync(int classId, string? phone, string? first = null)
    {
        await using var db = fx.NewContext();
        var name = (first ?? "Kid" + DbFixture.Unique()[..5]) + " " + DbFixture.Unique();
        var r = await DbFixture.Students(db).CreateAsync(name, classId);
        Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Value)));
        await db.Students.Where(s => s.Id == r.EntityId).ExecuteUpdateAsync(u => u.SetProperty(s => s.ParentContactNumber, phone));
        return r.EntityId!.Value;
    }

    private async Task<string> FirstNameAsync(int studentId)
    {
        await using var db = fx.NewContext();
        return (await db.Students.Where(s => s.Id == studentId).Select(s => s.FullName).SingleAsync()).Split(' ')[0];
    }

    private sealed record Scenario(int Event, int Year, int Class, string ClassName, int Schedule, IReadOnlyList<int> Teachers, IReadOnlyList<int> Lessons);

    /// <summary>A Published event with one class: <paramref name="teachers"/> subject teachers (timetable) and Rounds from 08:00.</summary>
    private async Task<Scenario> PublishedAsync(int teachers = 2, int rounds = 2, int capacity = 9, bool publish = true)
    {
        var (ev, year) = await EventAsync();
        var (cls, name) = await ClassAsync();
        var ids = new List<int>();
        var lessons = new List<int>();
        string[] subjects = ["Math", "Science", "English"];
        int[] departments = [3, 4, 2];
        for (var i = 0; i < teachers; i++)
        {
            var (t, _) = await fx.NewTeacherAsync();
            ids.Add(t);
            lessons.Add(await LessonAsync(cls, t, departments[i], subjects[i], DayOfWeek.Sunday + i));
        }
        await StudentAsync(cls, Phone());   // the class has a booked-able student so readiness has no "no students" warning
        await using var db = fx.NewContext();
        var schedule = Ok(await Setup(db).ScheduleClassAsync(ev, cls, Day, T8, T14, null));
        Ok(await Setup(db).AddRoundsAsync(schedule, new PtcRoundPlan(T8, rounds, Math.Max(70, capacity * 7 + 7), capacity, 7, 7)));
        if (publish) Ok(await Setup(db).PublishAsync(ev, true));
        return new Scenario(ev, year, cls, name, schedule, ids, lessons);
    }

    private async Task<PortalEvent> PortalEventAsync(int eventId, ISchoolTimeService? time = null)
    {
        await using var db = fx.NewContext();
        var token = await db.PtcEvents.Where(e => e.Id == eventId).Select(e => e.PublicToken).SingleAsync();
        return (await Portal(db, time: time).EventAsync(token))!;
    }

    private async Task<string> ChildTokenAsync(PortalEvent ev, string phone, int studentId)
    {
        var first = await FirstNameAsync(studentId);
        await using var db = fx.NewContext();
        var r = await Portal(db).IdentifyAsync(ev, phone, first);
        Assert.Equal(PortalIdentifyStatus.Found, r.Status);
        return r.Children.Single(c => c.FirstName == first).Token;
    }

    private async Task<PortalBookingResult> PortalBookAsync(PortalEvent ev, string childToken, int round, int subjects = 1, ISchoolTimeService? time = null)
    {
        await using var db = fx.NewContext();
        var portal = Portal(db, time: time);
        var page = (await portal.BookingPageAsync(ev, childToken))!;
        return await portal.BookAsync(ev, childToken, round, page.Subjects.Take(subjects).Select(s => s.Code).ToList());
    }

    // ------------------------------------------------------------------ bookings open

    [Fact]
    public async Task Bookings_open_means_published_with_a_future_open_round_that_has_a_place()
    {
        var s = await PublishedAsync(teachers: 1, rounds: 1, capacity: 1, publish: false);
        await using var db = fx.NewContext();
        var before = At(new DateTimeOffset(2027, 3, 1, 8, 0, 0, TimeSpan.Zero));
        Assert.DoesNotContain(s.Event, await PtcAvailability.OpenEventsAsync(db, before.Now, default));         // Draft
        Ok(await Setup(db).PublishAsync(s.Event, true));
        Assert.Contains(s.Event, await PtcAvailability.OpenEventsAsync(db, before.Now, default));               // Published, future, free
        var after = At(new DateTimeOffset(2027, 3, 9, 12, 0, 0, TimeSpan.Zero));                                 // 14:00 in Cairo: the 08:00 Round has passed
        Assert.DoesNotContain(s.Event, await PtcAvailability.OpenEventsAsync(db, after.Now, default));
        Assert.False((await AdminService(db, after).EventAsync(s.Event))!.BookingsOpen);

        // The only place is taken: closed.
        var phone = Phone();
        var student = await StudentAsync(s.Class, phone);
        var ev = await PortalEventAsync(s.Event, before);
        Assert.True(ev.BookingsOpen);
        Assert.True((await PortalBookAsync(ev, await ChildTokenAsync(ev, phone, student), 1, time: before)).Succeeded);
        Assert.DoesNotContain(s.Event, await PtcAvailability.OpenEventsAsync(db, before.Now, default));
        Assert.False((await AdminService(db, before).DashboardAsync()).Events.Single(e => e.Id == s.Event).BookingsOpen);
    }

    // ------------------------------------------------------------------ identification

    [Fact]
    public async Task Parents_are_identified_by_the_registered_number_and_first_name_and_failures_reveal_nothing()
    {
        var s = await PublishedAsync(teachers: 1);
        var phone = Phone();
        var student = await StudentAsync(s.Class, "0" + phone[1..4] + " " + phone[4..]);   // stored with a space
        var noNumber = await StudentAsync(s.Class, null);
        var first = await FirstNameAsync(student);
        var ev = await PortalEventAsync(s.Event);
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = fx.NewContext();
        var portal = Portal(db, cache, options: new PtcPortalOptions { MaxFailedAttemptsPerNumber = 3 });

        // +20 international form and a lower-case name match the stored "010…" number; only first name and class are returned.
        var found = await portal.IdentifyAsync(ev, "+20 " + phone[1..], first.ToLowerInvariant());
        var child = Assert.Single(found.Children);
        Assert.Equal((PortalIdentifyStatus.Found, first, s.ClassName, false), (found.Status, child.FirstName, child.ClassName, child.AlreadyBooked));

        // Wrong name, unknown number, a student without a number: the same generic answer.
        var wrongName = await portal.IdentifyAsync(ev, phone, "Nobody");
        var unknown = await portal.IdentifyAsync(ev, Phone(), first);
        var missing = await portal.IdentifyAsync(ev, Phone(), await FirstNameAsync(noNumber));
        Assert.All(new[] { wrongName, unknown, missing }, r => Assert.Equal((PortalIdentifyStatus.NotFound, 0), (r.Status, r.Children.Count)));

        // Repeated failures pause the number, even with the right name.
        await portal.IdentifyAsync(ev, phone, "Nobody");
        await portal.IdentifyAsync(ev, phone, "Nobody");
        Assert.Equal(PortalIdentifyStatus.Throttled, (await portal.IdentifyAsync(ev, phone, first)).Status);

        // The stored number is never changed.
        Assert.Equal("0" + phone[1..4] + " " + phone[4..], await db.Students.Where(x => x.Id == student).Select(x => x.ParentContactNumber).SingleAsync());
        // A Draft or unknown link opens nothing.
        Assert.Null(await portal.EventAsync("not-a-real-code"));
    }

    [Fact]
    public async Task Siblings_sharing_a_number_are_chosen_and_booked_separately()
    {
        var s = await PublishedAsync(teachers: 1);
        var (other, otherName) = await ClassAsync();
        var (t, _) = await fx.NewTeacherAsync();
        await LessonAsync(other, t, 1, "Arabic", DayOfWeek.Sunday);
        await using (var db = fx.NewContext())
        {
            // Add the second class to the (published) event: return it to Draft, add, publish again.
            Ok(await Setup(db).ReturnToDraftAsync(s.Event));
            var schedule = Ok(await Setup(db).ScheduleClassAsync(s.Event, other, Day.AddDays(1), T8, T14, null));
            Ok(await Setup(db).AddRoundAsync(schedule, T8));
            await StudentAsync(other, Phone());
            Ok(await Setup(db).PublishAsync(s.Event, true));
        }
        var phone = Phone();
        var older = await StudentAsync(s.Class, phone);
        var younger = await StudentAsync(other, "+2" + phone);
        var ev = await PortalEventAsync(s.Event);
        await using var check = fx.NewContext();
        var found = await Portal(check).IdentifyAsync(ev, phone, await FirstNameAsync(younger));
        Assert.Equal(new[] { s.ClassName, otherName }.Order(), found.Children.Select(c => c.ClassName).Order());

        foreach (var child in found.Children) Assert.True((await PortalBookAsync(ev, child.Token, 1)).Succeeded);
        Assert.Equal(2, await check.PtcParentBookings.CountAsync(b => b.StudentId == older || b.StudentId == younger));
        var again = await Portal(check).IdentifyAsync(ev, phone, await FirstNameAsync(older));
        Assert.All(again.Children, c => Assert.True(c.AlreadyBooked));
    }

    // ------------------------------------------------------------------ booking rules

    [Fact]
    public async Task A_child_has_one_booking_per_class_schedule_whatever_the_round()
    {
        var s = await PublishedAsync(teachers: 1, rounds: 2);
        var phone = Phone();
        var student = await StudentAsync(s.Class, phone);
        var ev = await PortalEventAsync(s.Event);
        var token = await ChildTokenAsync(ev, phone, student);
        var first = await PortalBookAsync(ev, token, 1);
        Assert.True(first.Succeeded);

        // Round 2 through the portal: the existing booking is shown, no second one is made.
        var second = await PortalBookAsync(ev, token, 2);
        Assert.Equal((false, true), (second.Succeeded, second.AlreadyBooked));
        await using var db = fx.NewContext();
        Assert.Equal((await Portal(db).ConfirmationAsync(ev, first.ConfirmationToken))!.Reference, (await Portal(db).ConfirmationAsync(ev, second.ConfirmationToken))!.Reference);

        // The booking service refuses it too, and so does the database.
        var round2 = await db.PtcRounds.Where(r => r.PtcClassScheduleId == s.Schedule && r.RoundNumber == 2).Select(r => r.Id).SingleAsync();
        var direct = await Booking(db).BookAsParentAsync(new PtcBookingRequest(round2, student, phone, [PtcSubjectOption.LessonKey(s.Lessons[0])]));
        Assert.Equal(PtcBookingService.AlreadyBookedField, direct.Errors.Single().Key);
        db.PtcParentBookings.Add(new PtcParentBooking
        {
            PtcRoundId = round2, PtcClassScheduleId = s.Schedule, StudentId = student, ParentContact = "201000000000", BookingReference = PtcRules.NewReference(), SlotNumber = 5,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains(SchoolDbContext.ScheduleStudentIndex, ex.InnerException!.Message);
        Assert.Equal(1, await fx.NewContext().PtcParentBookings.CountAsync(b => b.StudentId == student));
    }

    [Fact]
    public async Task Duplicate_and_concurrent_submissions_never_exceed_one_booking_per_child_or_the_capacity()
    {
        var s = await PublishedAsync(teachers: 1, rounds: 1, capacity: 9);
        var ev = await PortalEventAsync(s.Event);

        // The same child submitted six times at once: one booking; every answer leads to it.
        var phone = Phone();
        var student = await StudentAsync(s.Class, phone);
        var token = await ChildTokenAsync(ev, phone, student);
        var same = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => PortalBookAsync(ev, token, 1))));
        Assert.All(same, r => Assert.NotNull(r.ConfirmationToken));
        Assert.Equal(1, same.Count(r => r.Succeeded));

        // Eleven more families for the eight places left.
        var families = new List<(int Id, string Phone)>();
        for (var i = 0; i < 11; i++) { var p = Phone(); families.Add((await StudentAsync(s.Class, p), p)); }
        var tokens = new List<string>();
        foreach (var f in families) tokens.Add(await ChildTokenAsync(ev, f.Phone, f.Id));
        var results = await Task.WhenAll(tokens.Select(t => Task.Run(() => PortalBookAsync(ev, t, 1))));
        Assert.Equal(8, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains("filled", r.Error));
        await using var db = fx.NewContext();
        Assert.Equal(Enumerable.Range(1, 9), await db.PtcParentBookings.Where(b => b.PtcClassScheduleId == s.Schedule).Select(b => b.SlotNumber).OrderBy(n => n).ToListAsync());
    }

    [Fact]
    public async Task One_booking_holds_every_chosen_subject_with_parallel_interviews_and_a_protected_confirmation()
    {
        var s = await PublishedAsync(teachers: 3);
        var phone = Phone();
        var student = await StudentAsync(s.Class, phone);
        var ev = await PortalEventAsync(s.Event);
        var result = await PortalBookAsync(ev, await ChildTokenAsync(ev, phone, student), 1, subjects: 3);
        Assert.True(result.Succeeded, result.Error);

        await using var db = fx.NewContext();
        var booking = await db.PtcParentBookings.Include(b => b.Interviews).Include(b => b.Subjects).SingleAsync(b => b.StudentId == student);
        Assert.Equal((3, 3, PtcRules.ParentKey(phone)), (booking.Subjects.Count, booking.Interviews.Count, booking.ParentContact));
        Assert.Equal(s.Teachers.Order(), booking.Interviews.Select(i => i.TeacherStaffMemberId).Order());
        Assert.Single(booking.Interviews.Select(i => (i.ScheduledStart, i.ScheduledEnd)).Distinct());   // parallel: one shared slot
        Assert.Equal(PtcBookingService.ParentBookedBy, booking.BookedBy);

        var confirmation = (await Portal(db).ConfirmationAsync(ev, result.ConfirmationToken))!;
        Assert.Equal((booking.BookingReference, 3, s.ClassName, PtcBookingStatus.Confirmed), (confirmation.Reference, confirmation.Subjects.Count, confirmation.ClassName, confirmation.Status));
        // A changed token, or a token of another event, shows nothing.
        Assert.Null(await Portal(db).ConfirmationAsync(ev, result.ConfirmationToken![..^4] + "AAAA"));
        Assert.Null(await Portal(db).ConfirmationAsync(ev, booking.Id.ToString()));
        var other = await PublishedAsync(teachers: 1);
        Assert.Null(await Portal(db).ConfirmationAsync(await PortalEventAsync(other.Event), result.ConfirmationToken));
    }

    [Fact]
    public async Task The_class_round_and_subjects_are_resolved_on_the_server()
    {
        var s = await PublishedAsync(teachers: 1, rounds: 2);
        var other = await PublishedAsync(teachers: 1);
        var phone = Phone();
        var student = await StudentAsync(s.Class, phone);
        var ev = await PortalEventAsync(s.Event);
        var token = await ChildTokenAsync(ev, phone, student);
        await using var db = fx.NewContext();
        var portal = Portal(db);

        Assert.Null(await portal.BookingPageAsync(ev, token[..^3] + "abc"));                     // tampered identification
        Assert.True((await portal.BookAsync(ev, "forged", 1, ["X"])).Restart);
        Assert.Null(await portal.BookingPageAsync(await PortalEventAsync(other.Event), token));  // another event's link

        var page = (await portal.BookingPageAsync(ev, token))!;
        Assert.Equal((s.ClassName, Day, 2), (page.ClassName, page.Date, page.Rounds.Count));
        var otherPage = (await Portal(db).BookingPageAsync(await PortalEventAsync(other.Event),
            await ChildTokenAsync(await PortalEventAsync(other.Event), phone, await StudentAsync(other.Class, phone))))!;
        Assert.Contains("no longer available", (await portal.BookAsync(ev, token, 1, [otherPage.Subjects[0].Code])).Error);   // another class's subject
        Assert.Contains("choose one of the sessions", (await portal.BookAsync(ev, token, 7, [page.Subjects[0].Code])).Error);

        // 08:30 in Cairo on the day: Round 1 (08:00) has started and is no longer offered or bookable; Round 2 is.
        var during = At(new DateTimeOffset(2027, 3, 9, 6, 30, 0, TimeSpan.Zero));
        var later = (await Portal(db, time: during).BookingPageAsync(ev, token))!;
        Assert.Equal(new[] { 2 }, later.Rounds.Select(r => r.Number));
        Assert.False((await Portal(db, time: during).BookAsync(ev, token, 1, [page.Subjects[0].Code])).Succeeded);
        Assert.True((await Portal(db, time: during).BookAsync(ev, token, 2, [page.Subjects[0].Code])).Succeeded);
    }

    [Fact]
    public async Task Classes_without_subject_teachers_or_parents_without_a_number_cannot_book()
    {
        // Published directly (test shortcut) so the portal can be checked for a class with no timetable at all.
        var (ev, _) = await EventAsync();
        var (cls, _) = await ClassAsync();
        var phone = Phone();
        var student = await StudentAsync(cls, phone);
        await using var db = fx.NewContext();
        var schedule = Ok(await Setup(db).ScheduleClassAsync(ev, cls, Day, T8, T14, null));
        Ok(await Setup(db).AddRoundAsync(schedule, T8));
        Fails(await Setup(db).PublishAsync(ev, true), "No timetable assignments found");
        await db.PtcEvents.Where(e => e.Id == ev).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Published));

        var portalEvent = await PortalEventAsync(ev);
        var page = (await Portal(db).BookingPageAsync(portalEvent, await ChildTokenAsync(portalEvent, phone, student)))!;
        Assert.False(page.CanBook);
        Assert.Contains("still preparing its teachers", page.Unavailable);
        Assert.Empty(page.Subjects);
        Assert.False(await db.TimetableAssignments.AnyAsync(t => t.SchoolClassId == cls));   // nothing invented
    }

    // ------------------------------------------------------------------ KG and publishing rules

    [Fact]
    public async Task Kg_classes_use_their_timetable_teachers_like_every_class()
    {
        var (ev, year) = await EventAsync();
        var (kg, kgName) = await ClassAsync("KG1");
        var (homeroom, _) = await fx.NewTeacherAsync();
        var (english, englishName) = await fx.NewTeacherAsync();
        var phone = Phone();
        var student = await StudentAsync(kg, phone);
        await using var db = fx.NewContext();
        db.ClassTeachingAssignments.Add(new ClassTeachingAssignment { SchoolClassId = kg, StaffMemberId = homeroom, AcademicYearId = year, Role = ClassTeachingRole.HomeroomTeacher, AssignedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var setup = Setup(db);
        var schedule = Ok(await setup.ScheduleClassAsync(ev, kg, Day, T8, T14, null));
        Ok(await setup.AddRoundAsync(schedule, T8));

        // No timetable lessons: the same blocking issue as any class; the Homeroom Teacher is never added automatically.
        var detail = (await AdminService(db).EventAsync(ev))!;
        Assert.Empty(detail.Classes.Single(c => c.ClassId == kg).Subjects);
        Assert.Contains(detail.Readiness.Blockers, b => b.Message.Contains($"No timetable assignments found for {kgName}"));
        Assert.DoesNotContain(detail.Readiness.Issues, i => i.Message.Contains("selected", StringComparison.OrdinalIgnoreCase));

        // A former manual selection (history) is reported, never offered.
        db.PtcClassTeachers.Add(new PtcClassTeacher { PtcClassScheduleId = schedule, StaffMemberId = homeroom, DepartmentId = 1, SubjectName = "Homeroom", AssignedBy = "Earlier phase" });
        await db.SaveChangesAsync();
        var legacy = await db.PtcClassTeachers.Where(t => t.PtcClassScheduleId == schedule).Select(t => t.Id).SingleAsync();

        // The KG class's timetable lesson gives its subject and actual teacher, exactly like any other class.
        var lesson = await LessonAsync(kg, english, 2, "English", Day.DayOfWeek);
        detail = (await AdminService(db).EventAsync(ev))!;
        var option = Assert.Single(detail.Classes.Single(c => c.ClassId == kg).Subjects);
        Assert.Equal((english, "English", (int?)lesson, (int?)null), (option.TeacherId, option.Subject, option.SourceLessonId, option.ClassTeacherId));
        Assert.Contains(detail.Readiness.Warnings, w => w.Message.Contains("earlier manual KG teacher selection") && w.Message.Contains("no longer used"));
        Ok(await setup.PublishAsync(ev, true));

        var round = await db.PtcRounds.Where(r => r.PtcClassScheduleId == schedule).Select(r => r.Id).SingleAsync();
        Fails(await new PtcBookingService(db, new Admin(), new User()).BookAsParentAsync(new PtcBookingRequest(round, student, phone, [PtcSubjectOption.TeacherKey(legacy)])), "not offered");
        var portalEvent = await PortalEventAsync(ev);
        var result = await PortalBookAsync(portalEvent, await ChildTokenAsync(portalEvent, phone, student), 1);
        Assert.True(result.Succeeded, result.Error);
        var subject = await db.PtcBookingSubjects.SingleAsync(x => x.Booking.StudentId == student);
        Assert.Equal(((int?)null, (int?)lesson, english), (subject.SourcePtcClassTeacherId, subject.SourceTimetableAssignmentId, subject.AssignedTeacherStaffMemberId));
        Assert.Contains(englishName, (await Portal(db).ConfirmationAsync(portalEvent, result.ConfirmationToken))!.Subjects.Single().Teacher);
        Assert.Equal(PtcClassTeacherStatus.Active, await db.PtcClassTeachers.Where(t => t.Id == legacy).Select(t => t.Status).SingleAsync());   // history untouched
    }

    [Fact]
    public async Task An_uncovered_lesson_clash_blocks_publishing_until_a_substitute_is_recorded()
    {
        var s = await PublishedAsync(teachers: 1, rounds: 1, publish: false);
        var (taught, _) = await ClassAsync();
        var (substitute, _) = await fx.NewTeacherAsync();
        // The PTC teacher also teaches another class on the PTC weekday, 08:30–09:15, during Round 1 (08:00–09:10).
        await LessonAsync(taught, s.Teachers[0], 3, "Math", Day.DayOfWeek, new TimeOnly(8, 30), new TimeOnly(9, 15));
        await using var db = fx.NewContext();
        var readiness = (await AdminService(db).EventAsync(s.Event))!.Readiness;
        Assert.Contains(readiness.Blockers, b => b.Message.Contains("no PTC substitute covers it"));
        Fails(await Setup(db).PublishAsync(s.Event, true), "no PTC substitute covers it");

        Ok(await Setup(db).AssignSubstituteAsync(s.Schedule, s.Teachers[0], substitute, new TimeOnly(8, 30), new TimeOnly(9, 15), "PTC"));
        Ok(await Setup(db).PublishAsync(s.Event, true));
    }

    // ------------------------------------------------------------------ public endpoints and admin visibility (HTTP)

    [Fact]
    public async Task The_public_portal_books_end_to_end_without_exposing_admin_data_and_is_rate_limited()
    {
        var s = await PublishedAsync(teachers: 2);
        var phone = Phone();
        var student = await StudentAsync(s.Class, phone);
        string token;
        await using (var db = fx.NewContext()) token = (await db.PtcEvents.Where(e => e.Id == s.Event).Select(e => e.PublicToken).SingleAsync())!;
        var client = fx.Factory.CreateClient();

        var start = await client.GetAsync($"/ptc-booking/{token}");
        var html = await start.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal("no-referrer", start.Headers.GetValues("Referrer-Policy").Single());
        Assert.DoesNotContain("nav__link", html);           // no staff sidebar or admin navigation
        Assert.DoesNotContain("/ptc/events", html);

        var identified = await client.PostAsync($"/ptc-booking/{token}/identify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Antiforgery.Match(html).Groups[1].Value, ["phone"] = "+20" + phone[1..], ["firstName"] = await FirstNameAsync(student),
        }));
        var book = await identified.Content.ReadAsStringAsync();
        Assert.Contains("Choose one session", book);
        Assert.DoesNotContain("name=\"studentId\"", book);   // the child is carried in an opaque token, never as an id
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", Antiforgery.Match(book).Groups[1].Value),
            new("c", WebUtility.HtmlDecode(Regex.Match(book, "name=\"c\" value=\"([^\"]+)\"").Groups[1].Value)),
            new("round", "1"),
        };
        form.AddRange(Regex.Matches(book, "name=\"subjects\" value=\"([^\"]+)\"").Select(m => new KeyValuePair<string, string>("subjects", m.Groups[1].Value)));
        var confirmed = await client.PostAsync($"/ptc-booking/{token}/book", new FormUrlEncodedContent(form));
        var page = await confirmed.Content.ReadAsStringAsync();
        Assert.True(page.Contains("Booking confirmed"), $"{confirmed.StatusCode} {confirmed.RequestMessage?.RequestUri}: "
            + Regex.Match(page, "pp-error.*?</p>", RegexOptions.Singleline).Value);
        await using (var db = fx.NewContext())
        {
            var reference = await db.PtcParentBookings.Where(b => b.StudentId == student).Select(b => b.BookingReference).SingleAsync();
            Assert.Contains("Booking confirmed", page);
            Assert.Contains(reference, page);
            Assert.Equal(2, await db.PtcTeacherInterviews.CountAsync(i => i.Booking.StudentId == student));
        }

        // Draft or unknown links: not found, with the same page. The code is exact: another letter case does not open it.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/ptc-booking/unknown-code")).StatusCode);
        var otherCase = new string(token.Select(ch => char.IsUpper(ch) ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch)).ToArray());
        if (otherCase != token) Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/ptc-booking/{otherCase}")).StatusCode);

        // Rate limited per client address (a host configured with a low limit).
        var limited = fx.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PtcPortal:SubmitPermitLimit"] = "2",
        }))).CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) codes.Add((await limited.PostAsync($"/ptc-booking/{token}/identify", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, codes.Take(2));
    }

    [Fact]
    public async Task Bookings_and_the_share_link_are_super_admin_only()
    {
        var s = await PublishedAsync(teachers: 1);
        var phone = Phone();
        var student = await StudentAsync(s.Class, phone);
        var ev = await PortalEventAsync(s.Event);
        Assert.True((await PortalBookAsync(ev, await ChildTokenAsync(ev, phone, student), 1)).Succeeded);
        string reference;
        await using (var db = fx.NewContext()) reference = await db.PtcParentBookings.Where(b => b.StudentId == student).Select(b => b.BookingReference).SingleAsync();

        var (staff, _) = await fx.NewTeacherAsync();
        var asStaff = fx.ClientAs(fx.Factory, staff, follow: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await asStaff.GetAsync($"/ptc/events/{s.Event}/bookings")).StatusCode);
        var asAdmin = fx.Factory.CreateClient();
        var bookings = await (await asAdmin.GetAsync($"/ptc/events/{s.Event}/bookings")).Content.ReadAsStringAsync();
        Assert.Contains(reference, bookings);
        var eventPage = await (await asAdmin.GetAsync($"/ptc/events/{s.Event}")).Content.ReadAsStringAsync();
        Assert.Contains("https://wa.me/?text=", eventPage);
        Assert.Contains("/ptc-booking/" + ev.Token, eventPage);

        // The WhatsApp link carries the message, encoded; the message holds the link and no student data.
        var url = PtcLinks.BookingUrl("https://school.example/", ev.Token);
        Assert.Equal("https://school.example/ptc-booking/" + ev.Token, url);
        var message = PtcLinks.InvitationMessage(ev.Title, url);
        Assert.Equal("https://wa.me/?text=" + Uri.EscapeDataString(message), PtcLinks.WhatsAppShare(message));
        Assert.StartsWith("Dear Parent, Parent-Teacher Conference booking is now open", message);
    }
}
