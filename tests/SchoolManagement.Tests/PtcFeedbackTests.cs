using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Tests;

/// <summary>
/// PTC Phase 7: parent feedback (signed, time-limited links; once per booking; only after the class's PTC; Arabic and
/// English; teacher feedback only for the teachers in the booking), concern management (immutable answers, audited status,
/// assignment, resolution, reopening, concurrency) and reports (bookings apart from interviews, server-stamp durations, the
/// existing Missed rule, teacher attribution, every class, filters shared by cards, charts and the export). Test DB only.
/// </summary>
[Collection("db")]
public class PtcFeedbackTests(DbFixture fx)
{
    private static readonly DateOnly Day = new(2027, 3, 9); // a Tuesday
    private static readonly DateOnly LessonsFrom = SchoolWeek.StartOf(new DateOnly(2026, 9, 6));
    private static readonly IDataProtectionProvider Keys = new EphemeralDataProtectionProvider();
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private sealed class Admin : IPtcAccess { public bool CanManage => true; }
    private sealed class NoAccess : IPtcAccess { public bool CanManage => false; }
    private sealed class User : ICurrentUser
    {
        public string? Name => "Test runner";
        public int? StaffId => null;
        public bool IsVerified => false;
    }
    private sealed class FixedUtc(DateTimeOffset utc) : TimeProvider { public override DateTimeOffset GetUtcNow() => utc; }

    /// <summary>School time at a local date and time (Cairo).</summary>
    private static SchoolTimeService At(DateOnly date, int hour = 10)
    {
        var probe = new SchoolTimeService(TimeProvider.System);
        return new SchoolTimeService(new FixedUtc(new DateTimeOffset(probe.ToUtc(date, new TimeOnly(hour, 0)), TimeSpan.Zero)));
    }

    private static SettingsService Settings(SchoolDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));
    private static PtcAdminService AdminService(SchoolDbContext db) => new(db, new Admin(), Settings(db), new ClassTeamService(db, new User()));
    private static PtcSetupService Setup(SchoolDbContext db) => new(db, new Admin(), Settings(db), new User(), AdminService(db));

    private static PtcFeedbackService Feedback(SchoolDbContext db, ISchoolTimeService time, IPtcAccess? access = null) =>
        new(db, access ?? new Admin(), new User(),
            new PtcPortalService(db, new PtcBookingService(db, new Admin(), new User()), Keys, new MemoryCache(new MemoryCacheOptions()), Options.Create(new PtcPortalOptions()), time),
            Keys, Options.Create(new PtcFeedbackOptions()), time);

    private static PtcReportService Reports(SchoolDbContext db, ISchoolTimeService time) =>
        new(db, new Admin(), AdminService(db), Options.Create(new PtcFeedbackOptions()), time);

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

    // ------------------------------------------------------------------ fixtures (test database only)

    private sealed record Scenario(int Event, string PublicToken, int Class, string ClassName, int Schedule, int Round, int TeacherA, string TeacherAName,
        int TeacherB, int Booking, string BookingReference, int Student, string StudentName, string Phone, int Other);

    private async Task<int> LessonAsync(int classId, int teacherId, int departmentId, string subject, int hour)
    {
        await using var db = fx.NewContext();
        var slot = new TimeSlot { Number = Random.Shared.Next(100, 999), SchoolClassId = classId, StartTime = new TimeOnly(hour, 0), EndTime = new TimeOnly(hour, 45), UpdatedAt = DateTime.UtcNow };
        db.TimeSlots.Add(slot);
        await db.SaveChangesAsync();
        var lesson = new TimetableAssignment { SchoolClassId = classId, Day = DayOfWeek.Sunday, TimeSlotId = slot.Id, DepartmentId = departmentId, Subject = subject, TeacherId = teacherId, StartsOn = LessonsFrom };
        db.TimetableAssignments.Add(lesson);
        await db.SaveChangesAsync();
        return lesson.Id;
    }

    /// <summary>A Published event; one class taught Maths by A and Science by B; one family booked both (one booking, two interviews).</summary>
    private async Task<Scenario> ScenarioAsync()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "PTC7 " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        var ev = Ok(await Setup(db).SaveEventAsync(null, new PtcEventInput("PTC " + DbFixture.Unique(), null, year.Id, Day, Day)));
        var cls = await fx.NewClassAsync();
        var className = await db.Classes.Where(c => c.Id == cls).Select(c => c.Name).SingleAsync();
        var (a, aName) = await fx.NewTeacherAsync();
        var (b, _) = await fx.NewTeacherAsync();
        var (other, _) = await fx.NewTeacherAsync();
        var maths = await LessonAsync(cls, a, 3, "Maths", 11);
        var science = await LessonAsync(cls, b, 4, "Science", 12);
        var schedule = Ok(await Setup(db).ScheduleClassAsync(ev, cls, Day, new TimeOnly(8, 0), new TimeOnly(14, 0), null));
        var round = Ok(await Setup(db).AddRoundsAsync(schedule, new PtcRoundPlan(new TimeOnly(8, 0), 1, 70, 9, 7, 7)));
        await db.PtcEvents.Where(e => e.Id == ev).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Published).SetProperty(e => e.PublicToken, PtcRules.NewPublicToken()));
        var (student, studentName) = await fx.NewStudentAsync(cls);
        var phone = "010" + Random.Shared.Next(10_000_000, 99_999_999);
        await db.Students.Where(s => s.Id == student).ExecuteUpdateAsync(u => u.SetProperty(s => s.ParentContactNumber, phone));
        var booking = Ok(await new PtcBookingService(db, new Admin(), new User()).BookAsParentAsync(
            new PtcBookingRequest(round, student, phone, [PtcSubjectOption.LessonKey(maths), PtcSubjectOption.LessonKey(science)])));
        var info = await db.PtcParentBookings.Where(x => x.Id == booking).Select(x => new { x.BookingReference, x.ClassSchedule.PtcEvent.PublicToken }).SingleAsync();
        return new Scenario(ev, info.PublicToken!, cls, className, schedule, round, a, aName, b, booking, info.BookingReference, student, studentName, phone, other);
    }

    private static async Task CompleteAsync(SchoolDbContext db, int ev) =>
        await db.PtcEvents.Where(e => e.Id == ev).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Completed));

    private static async Task<string> TokenAsync(PtcFeedbackService svc, Scenario s, string? phone = null)
    {
        var ev = (await svc.EventAsync(s.PublicToken))!;
        var found = await svc.IdentifyAsync(ev, phone ?? s.Phone, s.StudentName.Split(' ')[0]);
        Assert.Equal(PortalIdentifyStatus.Found, found.Status);
        return Assert.Single(found.Children).FeedbackToken!;
    }

    private static PtcFeedbackInput Answers(string? comments = null, string? concern = null, string? category = null, params PtcTeacherFeedbackInput[] teachers) =>
        new("Good", "Excellent", "Average", "Yes", comments, category, concern, teachers);

    // ------------------------------------------------------------------ availability and links

    [Fact]
    public async Task Feedback_opens_only_after_the_class_PTC_and_closes_after_the_period()
    {
        var s = await ScenarioAsync();
        await using var db = fx.NewContext();

        // Before the PTC day (Published): the family is found, but feedback is not open yet.
        var before = Feedback(db, At(Day.AddDays(-1)));
        var child = Assert.Single((await before.IdentifyAsync((await before.EventAsync(s.PublicToken))!, s.Phone, s.StudentName.Split(' ')[0])).Children);
        Assert.Equal((PtcFeedbackAvailability.NotYet, (string?)null), (child.Availability, child.FeedbackToken));

        // The day after the class's PTC (still Published): open.
        var after = Feedback(db, At(Day.AddDays(1)));
        var token = await TokenAsync(after, s);
        Assert.Equal(PtcFeedbackAvailability.Open, (await after.FormAsync(token))!.Availability);

        // After the feedback period: closed, nothing accepted.
        var late = Feedback(db, At(Day.AddDays(new PtcFeedbackOptions().WindowDays + 1)));
        Assert.Equal(PtcFeedbackAvailability.Closed, (await late.FormAsync(token))!.Availability);
        var refused = await late.SubmitAsync(token, Answers());
        Assert.False(refused.Succeeded);
        Assert.Contains("ended", refused.Error);

        // A Draft or cancelled event never offers feedback.
        await db.PtcEvents.Where(e => e.Id == s.Event).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Draft));
        Assert.Null(await after.EventAsync(s.PublicToken));
        Assert.Null(await after.FormAsync(token));
    }

    [Fact]
    public async Task Invalid_tampered_expired_and_cancelled_links_are_refused_generically()
    {
        var s = await ScenarioAsync();
        await using var db = fx.NewContext();
        await CompleteAsync(db, s.Event);
        var svc = Feedback(db, At(Day.AddDays(1)));
        Assert.Null(await svc.FormAsync("not-a-code"));
        Assert.Null(await svc.FormAsync(null));
        var other = Keys.CreateProtector("SchoolManagement.Ptc.Portal.Booking").ToTimeLimitedDataProtector().Protect($"fb|{s.Booking}|{s.Student}", TimeSpan.FromDays(1));
        Assert.Null(await svc.FormAsync(other));                                                  // a code made for another purpose
        var expired = Keys.CreateProtector("SchoolManagement.Ptc.Feedback").ToTimeLimitedDataProtector().Protect($"fb|{s.Booking}|{s.Student}", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Null(await svc.FormAsync(expired));
        Assert.Equal(PtcFeedbackService.NotAvailableMessage, (await svc.SubmitAsync(expired, Answers())).Error);

        var token = await TokenAsync(svc, s);
        await db.PtcParentBookings.Where(b => b.Id == s.Booking).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, PtcBookingStatus.Cancelled).SetProperty(b => b.CancelledAt, DateTime.UtcNow));
        Assert.Null(await svc.FormAsync(token));                                                  // looks like any unknown link
        Assert.True((await svc.SubmitAsync(token, Answers())).Unavailable);
        Assert.False(await db.PtcFeedbacks.AnyAsync(f => f.PtcParentBookingId == s.Booking));
    }

    // ------------------------------------------------------------------ submission

    [Fact]
    public async Task One_submission_per_booking_in_Arabic_or_English_with_teacher_feedback_only_for_booked_teachers()
    {
        var s = await ScenarioAsync();
        await using var db = fx.NewContext();
        await CompleteAsync(db, s.Event);
        var svc = Feedback(db, At(Day.AddDays(2)));
        var token = await TokenAsync(svc, s);
        var form = (await svc.FormAsync(token))!;
        Assert.Equal(2, form.Teachers.Count);                                                     // the two teachers of the booking only
        Assert.DoesNotContain(form.Teachers, t => t.Name.Contains("other"));
        var codeA = form.Teachers.Single(t => t.Name == s.TeacherAName).Code;

        Assert.Equal(PtcFeedbackService.EmptyMessage, (await svc.SubmitAsync(token, new PtcFeedbackInput(null, null, null, null, " ", null, null, []))).Error);
        Assert.False((await svc.SubmitAsync(token, Answers(teachers: new PtcTeacherFeedbackInput("ABCDEF123456", "Good", null, "x")))).Succeeded);   // not a booked teacher
        Assert.False((await svc.SubmitAsync(token, new PtcFeedbackInput("Superb", null, null, null, null, null, null, []))).Succeeded);            // not a defined answer
        Assert.Contains("describe", (await svc.SubmitAsync(token, Answers(category: "Scheduling"))).Error);

        const string arabic = "شكراً جزيلاً للمعلمين، كان اللقاء مفيداً جداً.\nMaths was great.";
        var result = await svc.SubmitAsync(token, Answers(arabic, "الموعد كان متأخراً", "Scheduling", new PtcTeacherFeedbackInput(codeA, "Excellent", "Yes", "معلم رائع")));
        Assert.True(result.Succeeded);
        Assert.False(result.AlreadySubmitted);
        var saved = await db.PtcFeedbacks.AsNoTracking().Include(f => f.Teachers).SingleAsync(f => f.PtcParentBookingId == s.Booking);
        Assert.Equal(arabic, saved.Comments);
        Assert.Equal(("الموعد كان متأخراً", PtcConcernCategory.Scheduling, PtcConcernStatus.New), (saved.ConcernText, saved.ConcernCategory, saved.ConcernStatus));
        var tf = Assert.Single(saved.Teachers);
        Assert.Equal((s.TeacherA, PtcRating.Excellent, "معلم رائع"), (tf.TeacherStaffMemberId, tf.CommunicationRating, tf.Comment));
        Assert.Matches("^FB-[2-9A-Z]{8}$", saved.Reference);

        // Again (or twice at once): the first is kept, never overwritten.
        var again = await svc.SubmitAsync(token, new PtcFeedbackInput("Poor", "Poor", "Poor", "No", "changed", null, null, []));
        Assert.True(again.AlreadySubmitted);
        Assert.Equal(saved.Reference, again.Reference);
        var after = await db.PtcFeedbacks.AsNoTracking().SingleAsync(f => f.PtcParentBookingId == s.Booking);
        Assert.Equal((PtcRating.Good, arabic), (after.OverallRating!.Value, after.Comments));
        Assert.Equal(saved.Reference, (await svc.FormAsync(token))!.SubmittedReference);
    }

    [Fact]
    public async Task The_public_pages_show_no_private_data_and_never_internal_notes()
    {
        var s = await ScenarioAsync();
        await using (var db = fx.NewContext()) await CompleteAsync(db, s.Event);
        var client = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await (await client.GetAsync($"/ptc-feedback/{s.PublicToken}")).Content.ReadAsStringAsync();
        var children = await client.PostAsync($"/ptc-feedback/{s.PublicToken}/identify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token.Match(start).Groups[1].Value, ["phone"] = s.Phone, ["firstName"] = s.StudentName.Split(' ')[0],
        }));
        var list = await children.Content.ReadAsStringAsync();
        var link = Regex.Match(list, "href=\"(/ptc-feedback/f/[^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(link);
        var formPage = await (await client.GetAsync(WebUtility.HtmlDecode(link))).Content.ReadAsStringAsync();
        Assert.Contains(s.StudentName.Split(' ')[0], formPage);
        Assert.Contains(s.ClassName, formPage);
        foreach (var page in new[] { list, formPage })
        {
            Assert.DoesNotContain(s.Phone, page);
            Assert.DoesNotContain(s.StudentName, page);                                             // first name only
            Assert.DoesNotContain(s.BookingReference, page);
            Assert.DoesNotContain($"/{s.Booking}", page);
        }
        var sent = await client.PostAsync(WebUtility.HtmlDecode(link), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token.Match(formPage).Groups[1].Value, ["Overall"] = "Excellent", ["Comments"] = "Very well organised",
        }));
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);

        await using var db2 = fx.NewContext();
        var id = await db2.PtcFeedbacks.Where(f => f.PtcParentBookingId == s.Booking).Select(f => f.Id).SingleAsync();
        Ok(await Feedback(db2, At(Day.AddDays(1))).AddNoteAsync(id, "INTERNAL-NOTE-secret follow up"));
        var again = await (await client.GetAsync(WebUtility.HtmlDecode(link))).Content.ReadAsStringAsync();
        Assert.Contains("Feedback already received", again);
        Assert.DoesNotContain("INTERNAL-NOTE", again);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/ptc-feedback/f/forged-code")).StatusCode);
    }

    // ------------------------------------------------------------------ administration

    [Fact]
    public async Task Concerns_are_reviewed_assigned_resolved_and_reopened_with_history_and_the_answers_never_change()
    {
        var s = await ScenarioAsync();
        var (staff, staffName) = await fx.NewTeacherAsync();
        await using var db = fx.NewContext();
        await CompleteAsync(db, s.Event);
        var svc = Feedback(db, At(Day.AddDays(1)));
        Assert.True((await svc.SubmitAsync(await TokenAsync(svc, s), Answers("Fine", "The room was too noisy", "Organization"))).Succeeded);
        var id = await db.PtcFeedbacks.Where(f => f.PtcParentBookingId == s.Booking).Select(f => f.Id).SingleAsync();
        await Assert.ThrowsAsync<PtcAccessDeniedException>(() => Feedback(db, At(Day), new NoAccess()).ListAsync(new PtcFeedbackFilter()));

        var list = await svc.ListAsync(new PtcFeedbackFilter { EventId = s.Event });
        var row = Assert.Single(list.Rows);
        Assert.Equal((1, 1, 0, 1, 1), (list.Summary.Submissions, list.Summary.Concerns, list.Summary.Resolved, list.Summary.Unresolved, list.Summary.PendingReview));
        Assert.Single((await svc.ListAsync(new PtcFeedbackFilter { Q = row.Reference })).Rows);
        Assert.Single((await svc.ListAsync(new PtcFeedbackFilter { EventId = s.Event, TeacherId = s.TeacherB })).Rows);
        Assert.Empty((await svc.ListAsync(new PtcFeedbackFilter { EventId = s.Event, Rating = PtcRating.Poor })).Rows);
        Assert.Empty((await svc.ListAsync(new PtcFeedbackFilter { EventId = s.Event, TeacherId = s.Other })).Rows);

        string V() => db.PtcFeedbacks.AsNoTracking().Where(f => f.Id == id).Select(f => f.Version).Single() is var v ? Convert.ToBase64String(v) : "";
        var stale = V();
        Ok(await svc.MarkReviewedAsync(id, V()));
        Fails(await svc.SetConcernStatusAsync(id, PtcConcernStatus.UnderReview, null, stale), "changed by someone else");   // concurrent edit
        Ok(await svc.SetConcernStatusAsync(id, PtcConcernStatus.InProgress, "Checking with facilities", V()));
        Ok(await svc.AssignAsync(id, staff, V()));
        Ok(await svc.AddNoteAsync(id, "Called the parent"));
        Fails(await svc.ResolveAsync(id, " ", V()), "Record how");
        Ok(await svc.ResolveAsync(id, "Quieter room booked for next PTC", V()));
        Fails(await svc.ReopenAsync(id, "", V()), "reason");
        Ok(await svc.ReopenAsync(id, "Parent wrote again", V()));
        Ok(await svc.ResolveAsync(id, "Spoke to the parent", V()));

        var d = (await svc.DetailsAsync(id))!;
        Assert.Equal((PtcConcernStatus.Resolved, staffName, PtcFeedbackReview.Reviewed), (d.Row.ConcernStatus!.Value, d.Row.AssignedTo!, d.Row.Review));
        Assert.Equal(new[] { PtcFeedbackActionKind.Resolved, PtcFeedbackActionKind.Reopened, PtcFeedbackActionKind.Resolved, PtcFeedbackActionKind.NoteAdded,
            PtcFeedbackActionKind.Assigned, PtcFeedbackActionKind.ConcernStatusChanged, PtcFeedbackActionKind.Reviewed }, d.History.Select(h => h.Kind));
        Assert.Contains(d.History, h => h.Note == "Quieter room booked for next PTC");             // the earlier resolution stays in the history
        // The parent's answers are exactly as submitted.
        var f = await db.PtcFeedbacks.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(("Fine", "The room was too noisy", PtcRating.Good, PtcRating.Average), (f.Comments, f.ConcernText, f.OverallRating!.Value, f.OrganizationRating!.Value));
    }

    // ------------------------------------------------------------------ reports

    [Fact]
    public async Task Reports_count_bookings_apart_from_interviews_use_server_stamps_and_attribute_teacher_feedback_only_to_that_teacher()
    {
        var s = await ScenarioAsync();
        await using var db = fx.NewContext();
        // Teacher A's meeting happened (server stamps, 6 minutes); teacher B's was never started.
        var plannedA = await db.PtcTeacherInterviews.Where(i => i.PtcParentBookingId == s.Booking && i.TeacherStaffMemberId == s.TeacherA).Select(i => new { i.Date, i.ScheduledStart }).SingleAsync();
        var startUtc = At(Day).ToUtc(plannedA.Date, plannedA.ScheduledStart);
        await db.PtcTeacherInterviews.Where(i => i.PtcParentBookingId == s.Booking && i.TeacherStaffMemberId == s.TeacherA).ExecuteUpdateAsync(u => u
            .SetProperty(i => i.Status, PtcInterviewStatus.Completed).SetProperty(i => i.ActualStart, startUtc).SetProperty(i => i.ActualEnd, startUtc.AddMinutes(6)));

        // Before the meetings' planned end, B's interview is upcoming, never missed.
        var early = await Reports(db, At(Day, 7)).BuildAsync(new PtcReportFilter { EventId = s.Event });
        Assert.Equal((1, 0), (early.Kpis.Upcoming, early.Kpis.Missed));

        await CompleteAsync(db, s.Event);
        var fb = Feedback(db, At(Day.AddDays(1)));
        var form = (await fb.FormAsync(await TokenAsync(fb, s)))!;
        Assert.True((await fb.SubmitAsync(form.Token, Answers(teachers: new PtcTeacherFeedbackInput(form.Teachers.Single(t => t.Name == s.TeacherAName).Code, "Good", "Yes", null)))).Succeeded);

        var r = await Reports(db, At(Day.AddDays(1))).BuildAsync(new PtcReportFilter { EventId = s.Event });
        Assert.Equal((1, 2, 1, 1), (r.Kpis.Bookings, r.Kpis.Interviews, r.Kpis.Completed, r.Kpis.Missed));   // one family, two teacher interviews
        Assert.Equal(50.0, r.Kpis.CompletionRate);
        Assert.Equal(6.0, r.Kpis.AverageActualMinutes.Value!.Value, 3);
        Assert.Equal((1, 1, 100.0), (r.Kpis.Feedback, r.Kpis.Eligible, r.Kpis.ResponseRate!.Value));
        Assert.False(r.Kpis.Satisfaction.Sufficient);                                             // one answer: no average is shown
        var a = r.Teachers.Single(t => t.Id == s.TeacherA);
        var b = r.Teachers.Single(t => t.Id == s.TeacherB);
        Assert.Equal((1, 1, 0, 1), (a.Meetings, a.Completed, a.Missed, a.TeacherFeedback));
        Assert.Equal((1, 0, 1, 0, 0), (b.Meetings, b.Completed, b.Missed, b.TeacherFeedback, b.Communication.Count));   // general feedback not attributed to B
        Assert.Equal(1, b.ParentFeedback);
        var ev = Assert.Single(r.Events);
        Assert.Equal((1, 2, 1, 1), (ev.Bookings, ev.Interviews, ev.Completed, ev.Missed));
        Assert.Equal(await db.Classes.CountAsync(c => c.IsActive), r.Classes.Count);              // every active class, booked or not
        Assert.Equal((1, 1, 1), (r.Classes.Single(c => c.Id == s.Class).Bookings, r.Classes.Single(c => c.Id == s.Class).Completed, r.Classes.Single(c => c.Id == s.Class).Missed));
        Assert.Equal(new[] { 3, 4 }, r.Departments.Select(d => d.Id).Order());                  // the subjects' departments

        // One teacher selected: cards, charts and tables all narrow together.
        var onlyB = await Reports(db, At(Day.AddDays(1))).BuildAsync(new PtcReportFilter { EventId = s.Event, TeacherId = s.TeacherB });
        Assert.Equal((1, 1, 0, 1), (onlyB.Kpis.Bookings, onlyB.Kpis.Interviews, onlyB.Kpis.Completed, onlyB.Kpis.Missed));
        Assert.Equal(new double[] { 0, 1, 0, 0, 0 }, onlyB.Charts.Single(c => c.Title == "Interview outcomes").Rows.Select(x => x.Values[0]));
        Assert.Equal(new[] { s.TeacherB }, onlyB.Teachers.Select(t => t.Id));
        Assert.Empty(onlyB.TeacherFeedback);

        // The export holds the same filtered records, sheet by sheet, and no phone number.
        using var wb = PtcReportExport.Build(onlyB);
        Assert.Equal(PtcReportExport.SheetNames, wb.Worksheets.Select(w => w.Name));
        Assert.Equal(onlyB.Interviews.Count, wb.Worksheet("Teacher Interviews").RowsUsed().Count() - 1);
        Assert.Equal(onlyB.Bookings.Count, wb.Worksheet("Parent Bookings").RowsUsed().Count() - 1);
        Assert.DoesNotContain(wb.Worksheets.SelectMany(w => w.CellsUsed()), c => c.GetString().Contains(s.Phone));
    }

    [Fact]
    public async Task Empty_selections_give_zero_counts_no_averages_and_no_division_by_zero()
    {
        await using var db = fx.NewContext();
        var year = new AcademicYear { Name = "PTC7e " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        var ev = Ok(await Setup(db).SaveEventAsync(null, new PtcEventInput("PTC " + DbFixture.Unique(), null, year.Id, Day, Day)));
        var r = await Reports(db, At(Day)).BuildAsync(new PtcReportFilter { EventId = ev });
        Assert.True(r.Empty);
        Assert.Equal((1, 0, 0, 0), (r.Kpis.Events, r.Kpis.Bookings, r.Kpis.Interviews, r.Kpis.Feedback));
        Assert.Null(r.Kpis.CompletionRate);
        Assert.Null(r.Kpis.ResponseRate);
        Assert.Null(r.Kpis.CancellationRate);
        Assert.Equal("No data", r.Kpis.Satisfaction.Text());
        Assert.All(r.Charts, c => Assert.True(c.Empty));
        Assert.Empty(r.Teachers);
        Assert.Equal(await db.Classes.CountAsync(c => c.IsActive), r.Classes.Count);
        Assert.All(r.Classes, c => Assert.Equal(0, c.Bookings));
        using var wb = PtcReportExport.Build(r);
        Assert.Equal("No records match the selected filters.", wb.Worksheet("Parent Feedback").Cell(2, 1).GetString());
    }

    [Fact]
    public async Task The_admin_pages_open_for_the_super_admin()
    {
        var s = await ScenarioAsync();
        var client = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });   // Super Admin (sign-in off)
        foreach (var page in new[] { "/ptc/feedback", "/ptc/reports", "/ptc/reports/teachers", $"/ptc/reports/teachers/{s.TeacherA}", $"/ptc/events/{s.Event}/feedback",
                     $"/ptc/reports?EventId={s.Event}&State=Missed" })
        {
            var r = await client.GetAsync(page);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{page}: {r.StatusCode}");
        }
        var export = await client.GetAsync($"/ptc/reports/export?EventId={s.Event}");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
        using var wb = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        Assert.Equal(PtcReportExport.SheetNames, wb.Worksheets.Select(w => w.Name));
    }
}
