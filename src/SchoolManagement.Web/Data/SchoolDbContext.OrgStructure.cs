using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

/// <summary>
/// Organizational structure (Phase 1): tiers, units, positions, assignments and data-quality flags. Every relationship to
/// a staff member is Restrict / NoAction, so neither a person nor their assignment history is ever deleted by a cascade.
/// </summary>
public partial class SchoolDbContext
{
    public DbSet<OrgTier> OrgTiers => Set<OrgTier>();
    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
    public DbSet<OrgUnitDepartment> OrgUnitDepartments => Set<OrgUnitDepartment>();
    public DbSet<StaffPosition> StaffPositions => Set<StaffPosition>();
    public DbSet<StaffPositionAssignment> StaffPositionAssignments => Set<StaffPositionAssignment>();
    public DbSet<StaffDataQualityFlag> StaffDataQualityFlags => Set<StaffDataQualityFlag>();

    private static void ConfigureOrgStructure(ModelBuilder b)
    {
        b.Entity<OrgTier>(e =>
        {
            e.ToTable("OrgTiers", t => t.HasCheckConstraint("CK_OrgTiers_Section", "[Section] BETWEEN 1 AND 2"));
            e.Property(x => x.SourceKey).HasMaxLength(OrgTier.KeyMaxLength).IsUnicode(false);
            e.Property(x => x.Name).HasMaxLength(OrgTier.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(OrgTier.NameMaxLength).IsRequired();
            e.Property(x => x.Description).HasMaxLength(OrgTier.DescriptionMaxLength);
            e.HasIndex(x => x.SourceKey).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasIndex(x => x.SortOrder);
        });

        b.Entity<OrgUnit>(e =>
        {
            e.ToTable("OrgUnits");
            e.Property(x => x.SourceKey).HasMaxLength(OrgTier.KeyMaxLength).IsUnicode(false);
            e.Property(x => x.Name).HasMaxLength(OrgUnit.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(OrgUnit.NameMaxLength).IsRequired();
            e.Property(x => x.Description).HasMaxLength(OrgUnit.DescriptionMaxLength);
            e.HasOne(x => x.Tier).WithMany(t => t.Units).HasForeignKey(x => x.OrgTierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ParentUnit).WithMany(u => u.Children).HasForeignKey(x => x.ParentUnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SourceKey).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
            // A unit name is unique within its tier.
            e.HasIndex(x => new { x.OrgTierId, x.NormalizedName }).IsUnique();
        });

        b.Entity<OrgUnitDepartment>(e =>
        {
            e.ToTable("OrgUnitDepartments");
            e.HasKey(x => new { x.OrgUnitId, x.DepartmentId });
            e.HasOne(x => x.Unit).WithMany(u => u.Departments).HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.Cascade);
            // Departments are never deleted by the structure; a department delete is blocked while a unit refers to it.
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Subcategory).WithMany().HasForeignKey(x => x.DepartmentSubcategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.DepartmentId);
        });

        b.Entity<StaffPosition>(e =>
        {
            e.ToTable("StaffPositions", t => t.HasCheckConstraint("CK_StaffPositions_RoleType", "[RoleType] BETWEEN 1 AND 10"));
            e.Property(x => x.SourceKey).HasMaxLength(OrgTier.KeyMaxLength).IsUnicode(false);
            e.Property(x => x.Title).HasMaxLength(StaffPosition.TitleMaxLength).IsRequired();
            e.Property(x => x.NormalizedTitle).HasMaxLength(StaffPosition.TitleMaxLength).IsRequired();
            e.Property(x => x.Description).HasMaxLength(StaffPosition.DescriptionMaxLength);
            e.Property(x => x.GradeRange).HasMaxLength(StaffPosition.GradeRangeMaxLength);
            e.HasOne(x => x.Unit).WithMany(u => u.Positions).HasForeignKey(x => x.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SourceKey).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
            e.HasIndex(x => new { x.OrgUnitId, x.NormalizedTitle, x.GradeRange }).IsUnique().HasFilter(null);
            e.HasIndex(x => x.RoleType);
        });

        b.Entity<StaffPositionAssignment>(e =>
        {
            e.ToTable("StaffPositionAssignments", t =>
            {
                t.HasCheckConstraint("CK_StaffPositionAssignments_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                // A current assignment has no end; an ended one is never primary.
                t.HasCheckConstraint("CK_StaffPositionAssignments_CurrentHasNoEnd", "[IsCurrent] = 0 OR [EndDate] IS NULL");
                t.HasCheckConstraint("CK_StaffPositionAssignments_PrimaryIsCurrent", "[IsPrimary] = 0 OR [IsCurrent] = 1");
                t.HasCheckConstraint("CK_StaffPositionAssignments_NotOwnManager", "[ReportsToStaffMemberId] IS NULL OR [ReportsToStaffMemberId] <> [StaffMemberId]");
            });
            e.Property(x => x.SourceKey).HasMaxLength(OrgTier.KeyMaxLength).IsUnicode(false);
            e.Property(x => x.GradeRange).HasMaxLength(StaffPosition.GradeRangeMaxLength);
            e.Property(x => x.Scope).HasMaxLength(StaffPositionAssignment.ScopeMaxLength);
            e.Property(x => x.Notes).HasMaxLength(StaffPositionAssignment.NotesMaxLength);
            e.Property(x => x.EndReason).HasMaxLength(StaffPositionAssignment.ReasonMaxLength);
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Position).WithMany(p => p.Assignments).HasForeignKey(x => x.StaffPositionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReportsTo).WithMany().HasForeignKey(x => x.ReportsToStaffMemberId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.SourceKey).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
            // Never the same position twice at once for one person, and at most one current primary position per person.
            e.HasIndex(x => new { x.StaffMemberId, x.StaffPositionId }).IsUnique().HasFilter("[IsCurrent] = 1")
                .HasDatabaseName("UX_StaffPositionAssignments_CurrentPerPosition");
            e.HasIndex(x => x.StaffMemberId).IsUnique().HasFilter("[IsCurrent] = 1 AND [IsPrimary] = 1")
                .HasDatabaseName("UX_StaffPositionAssignments_OnePrimary");
            e.HasIndex(x => new { x.StaffPositionId, x.IsCurrent });
            e.HasIndex(x => x.ReportsToStaffMemberId);
        });

        b.Entity<StaffDataQualityFlag>(e =>
        {
            e.ToTable("StaffDataQualityFlags", t => t.HasCheckConstraint("CK_StaffDataQualityFlags_Kind", "[Kind] BETWEEN 1 AND 3"));
            e.Property(x => x.SourceKey).HasMaxLength(OrgTier.KeyMaxLength).IsUnicode(false);
            e.Property(x => x.Message).HasMaxLength(StaffDataQualityFlag.MessageMaxLength).IsRequired();
            e.Property(x => x.ResolvedBy).HasMaxLength(150);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SourceKey).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
            e.HasIndex(x => new { x.StaffMemberId, x.IsResolved });
        });
    }
}
