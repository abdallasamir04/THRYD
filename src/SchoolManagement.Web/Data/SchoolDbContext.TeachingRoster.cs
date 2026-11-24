using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

/// <summary>Teaching Staff roster source rows, their department links and review items. Nothing cascades to staff members.</summary>
public partial class SchoolDbContext
{
    public DbSet<TeachingRosterEntry> TeachingRosterEntries => Set<TeachingRosterEntry>();
    public DbSet<TeachingRosterDepartment> TeachingRosterDepartments => Set<TeachingRosterDepartment>();
    public DbSet<TeachingRosterReviewItem> TeachingRosterReviewItems => Set<TeachingRosterReviewItem>();

    private static void ConfigureTeachingRoster(ModelBuilder b)
    {
        b.Entity<TeachingRosterEntry>(e =>
        {
            e.ToTable("TeachingRosterEntries", t =>
            {
                t.HasCheckConstraint("CK_TeachingRoster_Role", "[Role] IS NULL OR [Role] BETWEEN 1 AND 2");
                t.HasCheckConstraint("CK_TeachingRoster_Blank", "[IsBlank] = 0 OR ([StaffName] IS NULL AND [StaffMemberId] IS NULL)");
            });
            e.Property(x => x.SourceKey).HasMaxLength(TeachingRosterEntry.KeyMaxLength).IsUnicode(false).IsRequired();
            e.Property(x => x.SourceName).HasMaxLength(TeachingRosterEntry.TextMaxLength).IsRequired();
            e.Property(x => x.AcademicYear).HasMaxLength(20).IsRequired();
            e.Property(x => x.StaffName).HasMaxLength(TeachingRosterEntry.TextMaxLength);
            e.Property(x => x.Subject).HasMaxLength(TeachingRosterEntry.TextMaxLength);
            e.Property(x => x.GradeLevels).HasMaxLength(TeachingRosterEntry.TextMaxLength);
            e.Property(x => x.MatchNote).HasMaxLength(TeachingRosterEntry.NoteMaxLength);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SourceKey).IsUnique();
            e.HasIndex(x => new { x.SourceName, x.RowNumber }).IsUnique();
            e.HasIndex(x => x.StaffMemberId);
        });

        b.Entity<TeachingRosterDepartment>(e =>
        {
            e.ToTable("TeachingRosterDepartments");
            e.HasKey(x => new { x.TeachingRosterEntryId, x.DepartmentId });
            e.Property(x => x.SourcePart).HasMaxLength(TeachingRosterEntry.TextMaxLength).IsRequired();
            e.HasOne(x => x.Entry).WithMany(r => r.Departments).HasForeignKey(x => x.TeachingRosterEntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Subcategory).WithMany().HasForeignKey(x => x.DepartmentSubcategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.DepartmentId);
        });

        b.Entity<TeachingRosterReviewItem>(e =>
        {
            e.ToTable("TeachingRosterReviewItems", t => t.HasCheckConstraint("CK_TeachingRosterReview_Kind", "[Kind] BETWEEN 1 AND 7"));
            e.Property(x => x.SourceKey).HasMaxLength(TeachingRosterEntry.KeyMaxLength * 2).IsUnicode(false).IsRequired();
            e.Property(x => x.Field).HasMaxLength(50);
            e.Property(x => x.SourceValue).HasMaxLength(TeachingRosterReviewItem.ValueMaxLength);
            e.Property(x => x.ObservedValue).HasMaxLength(TeachingRosterReviewItem.ValueMaxLength);
            e.Property(x => x.Note).HasMaxLength(TeachingRosterReviewItem.NoteMaxLength).IsRequired();
            e.Property(x => x.Resolution).HasMaxLength(TeachingRosterReviewItem.ResolutionMaxLength);
            e.HasOne(x => x.Entry).WithMany(r => r.ReviewItems).HasForeignKey(x => x.TeachingRosterEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SourceKey).IsUnique();
            e.HasIndex(x => new { x.TeachingRosterEntryId, x.IsResolved });
        });
    }
}
