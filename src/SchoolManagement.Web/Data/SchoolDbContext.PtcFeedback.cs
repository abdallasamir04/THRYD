using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

/// <summary>PTC Phase 7: parent feedback, teacher-specific feedback and the administrative history. Nothing cascades.</summary>
public partial class SchoolDbContext
{
    public DbSet<PtcFeedback> PtcFeedbacks => Set<PtcFeedback>();
    public DbSet<PtcTeacherFeedback> PtcTeacherFeedbacks => Set<PtcTeacherFeedback>();
    public DbSet<PtcFeedbackHistory> PtcFeedbackHistories => Set<PtcFeedbackHistory>();

    /// <summary>The unique index behind "one feedback per booking".</summary>
    public const string FeedbackBookingIndex = "UX_PtcFeedback_Booking";

    private static void ConfigurePtcFeedback(ModelBuilder b)
    {
        b.Entity<PtcFeedback>(e =>
        {
            e.ToTable("PtcFeedback", t =>
            {
                t.HasCheckConstraint("CK_PtcFeedback_Overall", "[OverallRating] IS NULL OR [OverallRating] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_PtcFeedback_Communication", "[CommunicationRating] IS NULL OR [CommunicationRating] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_PtcFeedback_Organization", "[OrganizationRating] IS NULL OR [OrganizationRating] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_PtcFeedback_Helpful", "[MeetingHelpful] IS NULL OR [MeetingHelpful] BETWEEN 1 AND 3");
                t.HasCheckConstraint("CK_PtcFeedback_Category", "[ConcernCategory] IS NULL OR [ConcernCategory] BETWEEN 1 AND 6");
                t.HasCheckConstraint("CK_PtcFeedback_Review", "[ReviewStatus] BETWEEN 1 AND 2");
                t.HasCheckConstraint("CK_PtcFeedback_ConcernStatus", "[ConcernStatus] IS NULL OR [ConcernStatus] BETWEEN 1 AND 5");
                // A concern status exists exactly when the parent wrote a concern.
                t.HasCheckConstraint("CK_PtcFeedback_Concern", "([ConcernText] IS NULL AND [ConcernStatus] IS NULL) OR ([ConcernText] IS NOT NULL AND [ConcernStatus] IS NOT NULL)");
            });
            e.Property(x => x.Reference).HasMaxLength(PtcFeedback.ReferenceLength).IsUnicode(false);
            e.Property(x => x.Comments).HasMaxLength(PtcFeedback.CommentsMaxLength);
            e.Property(x => x.ConcernText).HasMaxLength(PtcFeedback.ConcernMaxLength);
            e.Property(x => x.ResolutionNotes).HasMaxLength(PtcFeedback.ResolutionMaxLength);
            e.Property(x => x.ReviewedBy).HasMaxLength(150);
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.Property(x => x.ResolvedBy).HasMaxLength(150);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => x.PtcParentBookingId).IsUnique().HasDatabaseName(FeedbackBookingIndex);
            e.HasIndex(x => new { x.PtcEventId, x.SubmittedAt });
            e.HasIndex(x => x.ConcernStatus);
            e.HasOne(x => x.PtcEvent).WithMany().HasForeignKey(x => x.PtcEventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ClassSchedule).WithMany().HasForeignKey(x => x.PtcClassScheduleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.PtcParentBookingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AssignedStaffMember).WithMany().HasForeignKey(x => x.AssignedStaffMemberId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PtcTeacherFeedback>(e =>
        {
            e.ToTable("PtcTeacherFeedback", t =>
            {
                t.HasCheckConstraint("CK_PtcTeacherFeedback_Communication", "[CommunicationRating] IS NULL OR [CommunicationRating] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_PtcTeacherFeedback_Helpful", "[MeetingHelpful] IS NULL OR [MeetingHelpful] BETWEEN 1 AND 3");
            });
            e.Property(x => x.Comment).HasMaxLength(PtcTeacherFeedback.CommentMaxLength);
            e.HasOne(x => x.Feedback).WithMany(f => f.Teachers).HasForeignKey(x => x.PtcFeedbackId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherStaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PtcFeedbackId, x.TeacherStaffMemberId }).IsUnique();
            e.HasIndex(x => x.TeacherStaffMemberId);
        });

        b.Entity<PtcFeedbackHistory>(e =>
        {
            e.ToTable("PtcFeedbackHistory", t => t.HasCheckConstraint("CK_PtcFeedbackHistory_Kind", "[Kind] BETWEEN 1 AND 7"));
            e.Property(x => x.OldValue).HasMaxLength(PtcFeedbackHistory.ValueMaxLength);
            e.Property(x => x.NewValue).HasMaxLength(PtcFeedbackHistory.ValueMaxLength);
            e.Property(x => x.Note).HasMaxLength(PtcFeedbackHistory.NoteMaxLength);
            e.Property(x => x.By).HasMaxLength(150);
            e.HasOne(x => x.Feedback).WithMany(f => f.History).HasForeignKey(x => x.PtcFeedbackId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PtcFeedbackId, x.At });
        });
    }
}
