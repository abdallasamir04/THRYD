using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

/// <summary>
/// PTC (Parent-Teacher Conference). Every reference to existing data (academic years, classes, students, staff, departments,
/// timetable lessons) is Restrict: nothing PTC does can delete or cascade into them. PTC rows are never deleted either.
/// "Active" in the filtered indexes means not cancelled / removed.
/// </summary>
public partial class SchoolDbContext
{
    public DbSet<PtcEvent> PtcEvents => Set<PtcEvent>();
    public DbSet<PtcClassSchedule> PtcClassSchedules => Set<PtcClassSchedule>();
    public DbSet<PtcRound> PtcRounds => Set<PtcRound>();
    public DbSet<PtcParentBooking> PtcParentBookings => Set<PtcParentBooking>();
    public DbSet<PtcBookingSubject> PtcBookingSubjects => Set<PtcBookingSubject>();
    public DbSet<PtcTeacherInterview> PtcTeacherInterviews => Set<PtcTeacherInterview>();
    public DbSet<PtcSubstituteAssignment> PtcSubstituteAssignments => Set<PtcSubstituteAssignment>();
    public DbSet<PtcClassTeacher> PtcClassTeachers => Set<PtcClassTeacher>();
    public DbSet<PtcScheduleChange> PtcScheduleChanges => Set<PtcScheduleChange>();
    public DbSet<PtcInterviewEvent> PtcInterviewEvents => Set<PtcInterviewEvent>();
    public DbSet<PtcSubstituteChange> PtcSubstituteChanges => Set<PtcSubstituteChange>();

    /// <summary>The unique index behind "one active booking per student per class schedule".</summary>
    public const string ScheduleStudentIndex = "UX_PtcParentBookings_ScheduleStudent";

    private static void ConfigurePtc(ModelBuilder b)
    {
        b.Entity<PtcEvent>(e =>
        {
            e.ToTable("PtcEvents", t =>
            {
                t.HasCheckConstraint("CK_PtcEvents_Status", "[Status] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_PtcEvents_Dates", "[EndDate] >= [StartDate]");
            });
            e.Property(x => x.Title).HasMaxLength(PtcEvent.TitleMaxLength).IsRequired();
            e.Property(x => x.Description).HasMaxLength(PtcEvent.DescriptionMaxLength);
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            e.Property(x => x.PublicToken).HasMaxLength(PtcEvent.PublicTokenMaxLength).IsUnicode(false);
            e.HasOne(x => x.AcademicYear).WithMany().HasForeignKey(x => x.AcademicYearId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.AcademicYearId, x.Status });
            e.HasIndex(x => x.PublicToken).IsUnique().HasFilter("[PublicToken] IS NOT NULL").HasDatabaseName("UX_PtcEvents_PublicToken");
        });

        b.Entity<PtcClassSchedule>(e =>
        {
            e.ToTable("PtcClassSchedules", t =>
            {
                t.HasCheckConstraint("CK_PtcClassSchedules_Status", "[Status] BETWEEN 1 AND 3");
                t.HasCheckConstraint("CK_PtcClassSchedules_EndAfterStart", "[EndTime] > [StartTime]");
            });
            e.Property(x => x.Notes).HasMaxLength(PtcClassSchedule.NotesMaxLength);
            e.HasOne(x => x.PtcEvent).WithMany(v => v.ClassSchedules).HasForeignKey(x => x.PtcEventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
            // One active schedule per class in an event (a cancelled one stays as history).
            e.HasIndex(x => new { x.PtcEventId, x.SchoolClassId }).IsUnique()
                .HasFilter("[Status] <> 3").HasDatabaseName("UX_PtcClassSchedules_EventClass");
            e.HasIndex(x => new { x.SchoolClassId, x.EventDate });
            e.HasIndex(x => x.EventDate);
        });

        b.Entity<PtcRound>(e =>
        {
            e.ToTable("PtcRounds", t =>
            {
                t.HasCheckConstraint("CK_PtcRounds_Status", "[Status] BETWEEN 1 AND 3");
                t.HasCheckConstraint("CK_PtcRounds_EndAfterStart", "[EndTime] > [StartTime]");
                t.HasCheckConstraint("CK_PtcRounds_Capacity", "[Capacity] >= 1");
                t.HasCheckConstraint("CK_PtcRounds_Durations", "[MeetingDurationMinutes] >= 1 AND [TransitionDurationMinutes] >= 0");
                t.HasCheckConstraint("CK_PtcRounds_Number", "[RoundNumber] >= 1");
            });
            e.HasOne(x => x.ClassSchedule).WithMany(s => s.Rounds).HasForeignKey(x => x.PtcClassScheduleId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PtcClassScheduleId, x.RoundNumber }).IsUnique().HasDatabaseName("UX_PtcRounds_ScheduleNumber");
        });

        b.Entity<PtcParentBooking>(e =>
        {
            e.ToTable("PtcParentBookings", t =>
            {
                t.HasCheckConstraint("CK_PtcParentBookings_Status", "[Status] BETWEEN 1 AND 2");
                t.HasCheckConstraint("CK_PtcParentBookings_Slot", "[SlotNumber] >= 1");
                t.HasCheckConstraint("CK_PtcParentBookings_Cancelled",
                    "([Status] = 2 AND [CancelledAt] IS NOT NULL) OR ([Status] <> 2 AND [CancelledAt] IS NULL)");
            });
            e.Property(x => x.ParentContact).HasMaxLength(PtcParentBooking.ParentContactMaxLength).IsUnicode(false).IsRequired();
            e.Property(x => x.BookingReference).HasMaxLength(PtcParentBooking.ReferenceLength).IsUnicode(false).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(PtcParentBooking.NotesMaxLength);
            e.Property(x => x.BookedBy).HasMaxLength(150);
            e.Property(x => x.CancelledBy).HasMaxLength(150);
            e.HasOne(x => x.Round).WithMany(r => r.Bookings).HasForeignKey(x => x.PtcRoundId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ClassSchedule).WithMany().HasForeignKey(x => x.PtcClassScheduleId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.BookingReference).IsUnique();
            // One active booking per student in a class schedule, whichever Round.
            e.HasIndex(x => new { x.PtcClassScheduleId, x.StudentId }).IsUnique()
                .HasFilter("[Status] <> 2").HasDatabaseName(ScheduleStudentIndex);
            // One active booking per student in a Round.
            e.HasIndex(x => new { x.PtcRoundId, x.StudentId }).IsUnique()
                .HasFilter("[Status] <> 2").HasDatabaseName("UX_PtcParentBookings_RoundStudent");
            // Capacity backstop: each slot of a Round is held by at most one active booking (slots run 1 … Capacity).
            e.HasIndex(x => new { x.PtcRoundId, x.SlotNumber }).IsUnique()
                .HasFilter("[Status] <> 2").HasDatabaseName("UX_PtcParentBookings_RoundSlot");
            e.HasIndex(x => x.StudentId);
            e.HasIndex(x => x.ParentContact);
        });

        b.Entity<PtcBookingSubject>(e =>
        {
            e.ToTable("PtcBookingSubjects", t =>
            {
                t.HasCheckConstraint("CK_PtcBookingSubjects_Status", "[Status] BETWEEN 1 AND 2");
                // Exactly one source: a timetable lesson (every class, KG included), or on older rows a former manual KG selection.
                t.HasCheckConstraint("CK_PtcBookingSubjects_Source",
                    "([SourceTimetableAssignmentId] IS NOT NULL AND [SourcePtcClassTeacherId] IS NULL) OR ([SourceTimetableAssignmentId] IS NULL AND [SourcePtcClassTeacherId] IS NOT NULL)");
            });
            e.Property(x => x.SubjectName).HasMaxLength(TimetableAssignment.SubjectMaxLength).IsRequired();
            e.HasOne(x => x.Booking).WithMany(bk => bk.Subjects).HasForeignKey(x => x.PtcParentBookingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AssignedTeacher).WithMany().HasForeignKey(x => x.AssignedTeacherStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SourceTimetableAssignment).WithMany().HasForeignKey(x => x.SourceTimetableAssignmentId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SourceClassTeacher).WithMany().HasForeignKey(x => x.SourcePtcClassTeacherId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SourcePtcClassTeacherId);
            // A subject is selected once per booking.
            e.HasIndex(x => new { x.PtcParentBookingId, x.DepartmentId, x.SubjectName }).IsUnique()
                .HasFilter("[Status] = 1").HasDatabaseName("UX_PtcBookingSubjects_BookingSubject");
            e.HasIndex(x => x.AssignedTeacherStaffMemberId);
            e.HasIndex(x => x.SourceTimetableAssignmentId);
        });

        b.Entity<PtcTeacherInterview>(e =>
        {
            e.ToTable("PtcTeacherInterviews", t =>
            {
                t.HasCheckConstraint("CK_PtcTeacherInterviews_Status", "[Status] BETWEEN 1 AND 5");
                t.HasCheckConstraint("CK_PtcTeacherInterviews_Scheduled", "[ScheduledEnd] > [ScheduledStart]");
                t.HasCheckConstraint("CK_PtcTeacherInterviews_Actual", "[ActualStart] IS NULL OR [ActualEnd] IS NULL OR [ActualEnd] >= [ActualStart]");
            });
            e.HasOne(x => x.Booking).WithMany(bk => bk.Interviews).HasForeignKey(x => x.PtcParentBookingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.BookingSubject).WithMany().HasForeignKey(x => x.PtcBookingSubjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            // One live interview per selected subject.
            e.HasIndex(x => x.PtcBookingSubjectId).IsUnique()
                .HasFilter("[Status] <> 4").HasDatabaseName("UX_PtcTeacherInterviews_Subject");
            // A teacher's interviews on a day (overlap checks).
            e.HasIndex(x => new { x.TeacherStaffMemberId, x.Date, x.ScheduledStart });
            e.HasIndex(x => x.PtcParentBookingId);
        });

        b.Entity<PtcClassTeacher>(e =>
        {
            e.ToTable("PtcClassTeachers", t =>
            {
                t.HasCheckConstraint("CK_PtcClassTeachers_Status", "[Status] BETWEEN 1 AND 2");
                t.HasCheckConstraint("CK_PtcClassTeachers_Removed",
                    "([Status] = 2 AND [RemovedAt] IS NOT NULL) OR ([Status] <> 2 AND [RemovedAt] IS NULL)");
            });
            e.Property(x => x.SubjectName).HasMaxLength(TimetableAssignment.SubjectMaxLength).IsRequired();
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.Property(x => x.RemovedBy).HasMaxLength(150);
            e.HasOne(x => x.ClassSchedule).WithMany(s => s.Teachers).HasForeignKey(x => x.PtcClassScheduleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            // A subject is offered once per KG class schedule.
            e.HasIndex(x => new { x.PtcClassScheduleId, x.DepartmentId, x.SubjectName }).IsUnique()
                .HasFilter("[Status] = 1").HasDatabaseName("UX_PtcClassTeachers_ScheduleSubject");
            e.HasIndex(x => x.StaffMemberId);
        });

        b.Entity<PtcScheduleChange>(e =>
        {
            e.ToTable("PtcScheduleChanges", t => t.HasCheckConstraint("CK_PtcScheduleChanges_Kind", "[Kind] BETWEEN 1 AND 2"));
            e.Property(x => x.Teachers).HasMaxLength(PtcScheduleChange.TextMaxLength);
            e.Property(x => x.Reason).HasMaxLength(PtcScheduleChange.TextMaxLength).IsRequired();
            e.Property(x => x.ChangedBy).HasMaxLength(150);
            e.HasOne(x => x.PtcEvent).WithMany().HasForeignKey(x => x.PtcEventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ClassSchedule).WithMany().HasForeignKey(x => x.PtcClassScheduleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Round).WithMany().HasForeignKey(x => x.PtcRoundId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.PtcParentBookingId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PtcEventId, x.ChangedAt });
            e.HasIndex(x => x.PtcParentBookingId);
        });

        b.Entity<PtcInterviewEvent>(e =>
        {
            e.ToTable("PtcInterviewEvents", t =>
            {
                t.HasCheckConstraint("CK_PtcInterviewEvents_Kind", "[Kind] BETWEEN 1 AND 3");
                t.HasCheckConstraint("CK_PtcInterviewEvents_Times", "[NewActualStart] IS NULL OR [NewActualEnd] IS NULL OR [NewActualEnd] >= [NewActualStart]");
                t.HasCheckConstraint("CK_PtcInterviewEvents_Reason", "[Kind] <> 3 OR [Reason] IS NOT NULL");
            });
            e.Property(x => x.Reason).HasMaxLength(PtcInterviewEvent.ReasonMaxLength);
            e.Property(x => x.By).HasMaxLength(150);
            e.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.PtcParentBookingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PtcParentBookingId, x.TeacherStaffMemberId, x.At });
            e.HasIndex(x => x.At);
        });

        b.Entity<PtcSubstituteAssignment>(e =>
        {
            e.ToTable("PtcSubstituteAssignments", t =>
            {
                t.HasCheckConstraint("CK_PtcSubstitutes_Status", "[Status] BETWEEN 1 AND 2");
                t.HasCheckConstraint("CK_PtcSubstitutes_EndAfterStart", "[EndTime] > [StartTime]");
                t.HasCheckConstraint("CK_PtcSubstitutes_NotSelf", "[OriginalTeacherStaffMemberId] <> [SubstituteTeacherStaffMemberId]");
                t.HasCheckConstraint("CK_PtcSubstitutes_Cancelled",
                    "([Status] = 2 AND [CancelledAt] IS NOT NULL) OR ([Status] <> 2 AND [CancelledAt] IS NULL)");
            });
            e.Property(x => x.Reason).HasMaxLength(PtcSubstituteAssignment.ReasonMaxLength);
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.Property(x => x.CancelledBy).HasMaxLength(150);
            e.HasOne(x => x.ClassSchedule).WithMany(s => s.SubstituteAssignments).HasForeignKey(x => x.PtcClassScheduleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OriginalTeacher).WithMany().HasForeignKey(x => x.OriginalTeacherStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SubstituteTeacher).WithMany().HasForeignKey(x => x.SubstituteTeacherStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PtcClassScheduleId, x.Status });
            e.HasIndex(x => x.OriginalTeacherStaffMemberId);
            e.HasIndex(x => x.SubstituteTeacherStaffMemberId);
            e.Property(x => x.Notes).HasMaxLength(PtcSubstituteAssignment.NotesMaxLength);
            // The lesson is history, not owned: if a lesson row is ever deleted the cover keeps its record (lesson link cleared).
            e.HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.TimetableAssignmentId).OnDelete(DeleteBehavior.SetNull);
            // One active cover per lesson in a class schedule (the service also refuses overlapping covers for a teacher).
            e.HasIndex(x => new { x.PtcClassScheduleId, x.TimetableAssignmentId }).IsUnique()
                .HasFilter("[Status] = 1 AND [TimetableAssignmentId] IS NOT NULL").HasDatabaseName("UX_PtcSubstitutes_ScheduleLesson");
        });

        b.Entity<PtcSubstituteChange>(e =>
        {
            e.ToTable("PtcSubstituteChanges", t => t.HasCheckConstraint("CK_PtcSubstituteChanges_Kind", "[Kind] BETWEEN 1 AND 4"));
            e.Property(x => x.Lesson).HasMaxLength(PtcSubstituteChange.LessonMaxLength);
            e.Property(x => x.Reason).HasMaxLength(PtcSubstituteChange.TextMaxLength);
            e.Property(x => x.Notes).HasMaxLength(PtcSubstituteChange.TextMaxLength);
            e.Property(x => x.ChangedBy).HasMaxLength(150);
            e.HasOne(x => x.PtcEvent).WithMany().HasForeignKey(x => x.PtcEventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ClassSchedule).WithMany().HasForeignKey(x => x.PtcClassScheduleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.PtcSubstituteAssignmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OriginalTeacher).WithMany().HasForeignKey(x => x.OriginalTeacherStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OldSubstitute).WithMany().HasForeignKey(x => x.OldSubstituteStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.NewSubstitute).WithMany().HasForeignKey(x => x.NewSubstituteStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PtcEventId, x.ChangedAt });
            e.HasIndex(x => x.PtcSubstituteAssignmentId);
        });
    }
}
