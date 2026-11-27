using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Ptc;

/// <summary>
/// A booking request: the Round, the student, the parent's number (checked against the student's record) and the subjects
/// to discuss, each given as a <see cref="PtcSubjectOption.Key"/> (resolved again on the server; never a teacher id).
/// </summary>
public sealed record PtcBookingRequest(int RoundId, int StudentId, string? ParentContact, IReadOnlyList<string> SubjectKeys, string? Notes = null);

/// <summary>
/// Direct booking: a valid request is Confirmed at once, with no approval step. One booking core serves both entry points:
/// the Super Admin (<see cref="BookAsync"/>) and the parent portal (<see cref="BookAsParentAsync"/>, whose authority is the
/// parent number recorded on the student). Only a Published event's future, open Rounds with a place left are bookable.
/// </summary>
public interface IPtcBookingService
{
    /// <summary>The subjects (and their actual teachers) a parent can choose for a Round (Super Admin).</summary>
    Task<IReadOnlyList<PtcSubjectOption>> SubjectOptionsAsync(int roundId, CancellationToken ct = default);

    /// <summary>Books on a parent's behalf (Super Admin only). See <see cref="BookAsParentAsync"/> for the rules.</summary>
    Task<ServiceResult> BookAsync(PtcBookingRequest request, CancellationToken ct = default);

    /// <summary>
    /// Books from the parent portal: one Confirmed booking (in the Round's first free slot) with one subject row and one
    /// teacher interview per selected subject, all in the slot's time (parallel interviews), written as one unit. Rejected
    /// when the event is not Published, the Round is closed, past or full, the student already has an active booking in the
    /// class schedule (any Round), the number is not the student's recorded parent number, the student is not in the Round's
    /// class, a subject is not offered to the class or chosen twice, or a teacher is busy then. Safe under concurrent requests.
    /// A failure for an existing booking has the field <see cref="AlreadyBookedField"/>.
    /// </summary>
    Task<ServiceResult> BookAsParentAsync(PtcBookingRequest request, CancellationToken ct = default);
}

public sealed class PtcBookingService(SchoolDbContext db, IPtcAccess access, ICurrentUser currentUser, ISchoolTimeService? schoolTime = null) : IPtcBookingService
{
    public const string AlreadyBookedField = "AlreadyBooked";
    public const string FullField = "RoundFull";
    public const string NoCommonSlotField = "NoCommonSlot";
    public const string ParentBookedBy = "Parent (booking link)";

    private readonly ISchoolTimeService _time = schoolTime ?? SchoolTime.Current;

    private sealed record RoundInfo(int Id, int ScheduleId, int ClassId, DateOnly Date, TimeOnly Start, TimeOnly End, int Capacity, int MeetingMinutes,
        PtcRoundStatus Status, PtcScheduleStatus ScheduleStatus, PtcEventStatus EventStatus);

    private Task<RoundInfo?> RoundAsync(int roundId, CancellationToken ct) =>
        db.PtcRounds.AsNoTracking().Where(r => r.Id == roundId)
            .Select(r => new RoundInfo(r.Id, r.PtcClassScheduleId, r.ClassSchedule.SchoolClassId, r.ClassSchedule.EventDate, r.StartTime, r.EndTime,
                r.Capacity, r.MeetingDurationMinutes, r.Status, r.ClassSchedule.Status, r.ClassSchedule.PtcEvent.Status))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PtcSubjectOption>> SubjectOptionsAsync(int roundId, CancellationToken ct = default)
    {
        access.Ensure();
        return await RoundAsync(roundId, ct) is { } round
            ? await PtcSchedulingRules.ClassSubjectsAsync(db, round.ClassId, round.Date, ct, round.ScheduleId)
            : [];
    }

    public Task<ServiceResult> BookAsync(PtcBookingRequest request, CancellationToken ct = default)
    {
        access.Ensure();
        return BookCoreAsync(request, currentUser.AccountName(), ct);
    }

    public Task<ServiceResult> BookAsParentAsync(PtcBookingRequest request, CancellationToken ct = default) =>
        BookCoreAsync(request, ParentBookedBy, ct);

    private async Task<ServiceResult> BookCoreAsync(PtcBookingRequest request, string? bookedBy, CancellationToken ct)
    {
        var notes = TextNormalizer.CleanText(request.Notes);
        if (notes.Length > PtcParentBooking.NotesMaxLength) return ServiceResult.Fail("Notes", $"Use {PtcParentBooking.NotesMaxLength} characters or fewer.");
        var keys = (request.SubjectKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        if (keys.Count == 0) return ServiceResult.Fail("Subjects", "Choose at least one subject.");
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) return ServiceResult.Fail("Subjects", "A subject was chosen twice.");
        var parentKey = PtcRules.ParentKey(request.ParentContact);
        if (parentKey is null) return ServiceResult.Fail("ParentContact", "Enter the parent's phone number.");

        // One lock for every PTC write that reads other PTC rows: capacity, one booking per student and class schedule, and
        // teacher overlaps across classes are checked and written as one unit, so concurrent requests cannot both pass. The
        // unique indexes on (Round, Slot), (Round, Student) and (Class schedule, Student) are the database backstop.
        await using var tx = await DbScope.BeginAsync(db, IsolationLevel.ReadCommitted, ct);
        await PtcSetupService.LockAsync(db, ct);

        var round = await RoundAsync(request.RoundId, ct);
        if (round is null) return ServiceResult.Fail("RoundId", "This Round no longer exists.");
        // Only a Published event is bookable: a Draft is still being configured and is never open to parents.
        if (round.EventStatus != PtcEventStatus.Published) return ServiceResult.Fail("RoundId", "This PTC event is not published, so it is not open for booking.");
        if (round.Status != PtcRoundStatus.Open || round.ScheduleStatus != PtcScheduleStatus.Scheduled)
            return ServiceResult.Fail("RoundId", "This Round is not open for booking.");
        if (!PtcAvailability.IsFuture(round.Date, round.Start, _time.Now)) return ServiceResult.Fail("RoundId", "This Round has already started or passed.");

        // Student, class and parent come from the student's own record.
        var student = await db.Students.AsNoTracking().Where(s => s.Id == request.StudentId)
            .Select(s => new { s.SchoolClassId, s.ParentContactNumber }).FirstOrDefaultAsync(ct);
        if (student is null) return ServiceResult.Fail("StudentId", "This student does not exist.");
        if (student.SchoolClassId != round.ClassId) return ServiceResult.Fail("StudentId", "The student is not in the class of this Round.");
        if (PtcRules.ParentKey(student.ParentContactNumber) is null)
            return ServiceResult.Fail("ParentContact", "The student has no parent contact number on record.");
        if (!PtcRules.IsStudentParent(student.ParentContactNumber, request.ParentContact))
            return ServiceResult.Fail("ParentContact", "This phone number is not the student's parent contact number.");

        // One active booking per student in the class schedule, whichever Round.
        if (await db.PtcParentBookings.AnyAsync(b => b.PtcClassScheduleId == round.ScheduleId && b.StudentId == request.StudentId && b.Status != PtcBookingStatus.Cancelled, ct))
            return ServiceResult.Fail(AlreadyBookedField, "The student already has a booking for this class's PTC.");

        var taken = await db.PtcParentBookings.AsNoTracking()
            .Where(b => b.PtcRoundId == round.Id && b.Status != PtcBookingStatus.Cancelled)
            .Select(b => b.SlotNumber).ToListAsync(ct);
        if (taken.Count >= round.Capacity) return ServiceResult.Fail(FullField, "This Round is full.");
        var usedSlots = taken.ToHashSet();

        // Every subject must be one offered to the class: its current timetable lessons (with that lesson's teacher), the same
        // rule for every class, KG included. Keys are resolved here; nothing the browser sends is trusted.
        var options = await PtcSchedulingRules.ClassSubjectsAsync(db, round.ClassId, round.Date, ct, round.ScheduleId);
        var selected = new List<PtcSubjectOption>();
        foreach (var key in keys)
        {
            var option = options.FirstOrDefault(o => o.Key == key) ?? await LessonOptionAsync(key, round.ClassId, options, ct);
            if (option is null) return ServiceResult.Fail("Subjects", "A chosen subject is not offered to this class.");
            selected.Add(option);
        }
        if (selected.GroupBy(o => (o.DepartmentId, o.Subject.Trim().ToUpperInvariant())).Any(g => g.Count() > 1))
            return ServiceResult.Fail("Subjects", "A subject was chosen twice.");

        // Scheduling engine: the earliest free slot of the chosen Round where every chosen teacher is free at the same time,
        // across the whole PTC picture (other classes' interviews, substitute cover, uncovered lessons, duties, workload
        // periods). The parent's interviews all take that one slot (parallel). The same parent's subjects with one teacher
        // are one meeting. No slot for everyone: refused, nothing is saved.
        var teachers = selected.Select(o => o.TeacherId).Distinct().ToList();
        var availability = await PtcTeacherAvailability.LoadAsync(db, teachers, [round.Date], ct);
        var (candidate, reason) = PtcSlotAllocator.EarliestCommonSlot(round.Start, round.End, round.Capacity, round.MeetingMinutes, usedSlots, teachers, round.Date, availability);
        if (candidate is null)
            return reason is null
                ? ServiceResult.Fail(FullField, "This Round has no room for another meeting.")
                : ServiceResult.Fail(NoCommonSlotField, "No time left in this Round suits all the chosen teachers together: " + reason.Message);
        var (slot, start, end) = (candidate.Slot, candidate.Start, candidate.End);

        var booking = new PtcParentBooking
        {
            PtcRoundId = round.Id,
            PtcClassScheduleId = round.ScheduleId,
            StudentId = request.StudentId,
            ParentContact = parentKey,
            BookingReference = PtcRules.NewReference(),
            SlotNumber = slot,
            Status = PtcBookingStatus.Confirmed,
            Notes = notes.Length == 0 ? null : notes,
            BookedBy = bookedBy,
        };
        foreach (var o in selected)
        {
            var subject = new PtcBookingSubject
            {
                DepartmentId = o.DepartmentId,
                SubjectName = o.Subject.Trim(),
                AssignedTeacherStaffMemberId = o.TeacherId,
                SourceTimetableAssignmentId = o.SourceLessonId,
                SourcePtcClassTeacherId = o.ClassTeacherId,
            };
            booking.Subjects.Add(subject);
            booking.Interviews.Add(new PtcTeacherInterview
            {
                BookingSubject = subject,
                TeacherStaffMemberId = o.TeacherId,
                Date = round.Date,
                ScheduledStart = start,
                ScheduledEnd = end,
            });
        }
        db.PtcParentBookings.Add(booking);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ex.InnerException is SqlException sql && sql.Message.Contains(SchoolDbContext.ScheduleStudentIndex, StringComparison.Ordinal)
                ? ServiceResult.Fail(AlreadyBookedField, "The student already has a booking for this class's PTC.")
                : ServiceResult.Fail("RoundId", "The Round changed while booking. Please try again.");
        }
        await tx.CommitAsync(ct);
        return ServiceResult.Ok(booking.Id);
    }

    /// <summary>A timetable lesson key ("L123") of the class that maps to one of its options (any of the option's lessons).</summary>
    private async Task<PtcSubjectOption?> LessonOptionAsync(string key, int classId, IReadOnlyList<PtcSubjectOption> options, CancellationToken ct)
    {
        if (key.Length < 2 || key[0] != 'L' || !int.TryParse(key.AsSpan(1), out var lessonId)) return null;
        var lesson = await db.TimetableAssignments.AsNoTracking().Where(t => t.Id == lessonId && t.SchoolClassId == classId)
            .Select(t => new { t.DepartmentId, t.Subject, t.TeacherId }).FirstOrDefaultAsync(ct);
        return lesson is null ? null
            : options.FirstOrDefault(o => o.SourceLessonId is not null && o.DepartmentId == lesson.DepartmentId && o.TeacherId == lesson.TeacherId
                && string.Equals(o.Subject.Trim(), lesson.Subject.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
