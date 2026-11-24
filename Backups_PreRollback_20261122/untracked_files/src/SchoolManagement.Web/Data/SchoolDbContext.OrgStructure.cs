using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

public partial class SchoolDbContext
{
    public DbSet<OrgTier> OrgTiers => Set<OrgTier>();
    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
    public DbSet<StaffPosition> StaffPositions => Set<StaffPosition>();
    public DbSet<StaffPositionAssignment> StaffPositionAssignments => Set<StaffPositionAssignment>();

    private static void ConfigureOrgStructure(ModelBuilder b)
    {
        b.Entity<OrgTier>(e =>
        {
            e.ToTable("OrgTiers", t => t.HasCheckConstraint("CK_OrgTiers_TierLevel", "[TierLevel] BETWEEN 1 AND 10"));
            e.Property(x => x.Name).HasMaxLength(OrgTier.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(OrgTier.NameMaxLength).IsRequired();
            e.Property(x => x.Description).HasMaxLength(OrgTier.DescriptionMaxLength);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasIndex(x => x.TierLevel);
            e.HasIndex(x => x.SortOrder);
        });

        b.Entity<OrgUnit>(e =>
        {
            e.ToTable("OrgUnits");
            e.Property(x => x.Name).HasMaxLength(OrgUnit.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(OrgUnit.NameMaxLength).IsRequired();
            e.Property(x => x.Code).HasMaxLength(OrgUnit.CodeMaxLength);
            e.Property(x => x.Description).HasMaxLength(OrgUnit.DescriptionMaxLength);
            e.Property(x => x.IsActive).HasDefaultValue(true);

            e.HasOne(x => x.Tier).WithMany(t => t.Units).HasForeignKey(x => x.TierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ParentUnit).WithMany(u => u.Children).HasForeignKey(x => x.ParentUnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.TierId, x.NormalizedName }).IsUnique().HasFilter(null);
            e.HasIndex(x => x.ParentUnitId);
            e.HasIndex(x => x.SortOrder);
        });

        b.Entity<StaffPosition>(e =>
        {
            e.ToTable("StaffPositions");
            e.Property(x => x.Title).HasMaxLength(StaffPosition.TitleMaxLength).IsRequired();
            e.Property(x => x.NormalizedTitle).HasMaxLength(StaffPosition.TitleMaxLength).IsRequired();
            e.Property(x => x.DefaultGradeRange).HasMaxLength(StaffPosition.GradeRangeMaxLength);
            e.Property(x => x.Description).HasMaxLength(StaffPosition.DescriptionMaxLength);
            e.Property(x => x.IsActive).HasDefaultValue(true);

            e.HasOne(x => x.Tier).WithMany(t => t.Positions).HasForeignKey(x => x.TierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OrgUnit).WithMany(u => u.Positions).HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ReportsToPosition).WithMany(p => p.Subordinates).HasForeignKey(x => x.ReportsToPositionId).OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.NormalizedTitle);
            e.HasIndex(x => x.OrgUnitId);
            e.HasIndex(x => x.SortOrder);
        });

        b.Entity<StaffPositionAssignment>(e =>
        {
            e.ToTable("StaffPositionAssignments");
            e.Property(x => x.GradeRange).HasMaxLength(StaffPositionAssignment.GradeRangeMaxLength);
            e.Property(x => x.Notes).HasMaxLength(StaffPositionAssignment.NotesMaxLength);
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.Property(x => x.IsPrimary).HasDefaultValue(true);
            e.Property(x => x.IsCurrent).HasDefaultValue(true);

            e.HasOne(x => x.StaffMember).WithMany(s => s.PositionAssignments).HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Position).WithMany(p => p.Assignments).HasForeignKey(x => x.PositionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OrgUnit).WithMany(u => u.PositionAssignments).HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ReportsToStaffMember).WithMany().HasForeignKey(x => x.ReportsToStaffMemberId).OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.StaffMemberId, x.PositionId, x.IsCurrent });
            e.HasIndex(x => new { x.OrgUnitId, x.IsCurrent });
            e.HasIndex(x => x.IsCurrent);
        });
    }
}
