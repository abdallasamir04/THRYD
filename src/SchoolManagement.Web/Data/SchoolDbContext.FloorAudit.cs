using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

/// <summary>Floor Audit &amp; Student Movement: floors, areas, checklist, inspections, movements, access and audit trail.</summary>
public partial class SchoolDbContext
{
    public DbSet<SchoolFloor> Floors => Set<SchoolFloor>();
    public DbSet<FloorArea> FloorAreas => Set<FloorArea>();
    public DbSet<FloorAuditItem> FloorAuditItems => Set<FloorAuditItem>();
    public DbSet<FloorAreaInspection> FloorInspections => Set<FloorAreaInspection>();
    public DbSet<FloorInspectionResponse> FloorInspectionResponses => Set<FloorInspectionResponse>();
    public DbSet<FloorInspectionAttachment> FloorInspectionAttachments => Set<FloorInspectionAttachment>();
    public DbSet<StudentMovement> StudentMovements => Set<StudentMovement>();
    public DbSet<FloorAuditAccess> FloorAuditAccess => Set<FloorAuditAccess>();
    public DbSet<FloorAuditLogEntry> FloorAuditLog => Set<FloorAuditLogEntry>();

    /// <summary>The floors the module starts with (more can be added, renamed or deactivated in Manage).</summary>
    public static readonly (int Id, string Name)[] SeedFloors = [(1, "Ground Floor"), (2, "1st Floor"), (3, "2nd Floor"), (4, "3rd Floor")];

    /// <summary>The checklist the module starts with: suggestions that can be edited, deactivated or added to.</summary>
    public static readonly (int Id, string Name, string Description, AuditItemScope Scope, AuditResponseType Type, bool Required)[] SeedAuditItems =
    [
        (1, "Student Count", "Students in the classroom at the time of the inspection.", AuditItemScope.Classroom, AuditResponseType.Number, true),
        (2, "Teacher / Staff Presence", "A teacher or staff member is with the class.", AuditItemScope.Classroom, AuditResponseType.PresentAbsent, true),
        (3, "Classroom Cleanliness", null!, AuditItemScope.Classroom, AuditResponseType.GoodNeedsAttention, true),
        (4, "Student Safety", "No hazard to students (furniture, windows, cables, behaviour).", AuditItemScope.Classroom, AuditResponseType.PassFail, true),
        (5, "Classroom Condition", "Lights, air conditioning, doors, windows and furniture.", AuditItemScope.Classroom, AuditResponseType.GoodNeedsAttention, true),
        (6, "Classroom Organization", null!, AuditItemScope.Classroom, AuditResponseType.GoodNeedsAttention, true),
        (7, "Equipment and Learning Materials", "Board, screen and the materials needed for the lesson.", AuditItemScope.Classroom, AuditResponseType.GoodNeedsAttention, true),
        (8, "Any Issues or Incidents", "Anything written here is recorded as an issue.", AuditItemScope.Classroom, AuditResponseType.IssueText, false),
        (9, "Cleanliness", null!, AuditItemScope.Facility, AuditResponseType.GoodNeedsAttention, true),
        (10, "Safety", "No hazard (wet floor, broken fittings, blocked exits).", AuditItemScope.Facility, AuditResponseType.PassFail, true),
        (11, "Accessibility", "The facility is open and can be used.", AuditItemScope.Facility, AuditResponseType.YesNo, true),
        (12, "Facility Condition", null!, AuditItemScope.Facility, AuditResponseType.GoodNeedsAttention, true),
        (13, "Equipment Condition", null!, AuditItemScope.Facility, AuditResponseType.GoodNeedsAttention, true),
        (14, "Maintenance Issues", "Describe any maintenance needed; anything written here is recorded as an issue.", AuditItemScope.Facility, AuditResponseType.IssueText, false),
        (15, "Any Other Observations", null!, AuditItemScope.Facility, AuditResponseType.Text, false),
    ];

    private static void ConfigureFloorAudit(ModelBuilder b)
    {
        b.Entity<SchoolFloor>(e =>
        {
            e.ToTable("SchoolFloors");
            e.Property(x => x.Name).HasMaxLength(SchoolFloor.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(SchoolFloor.NameMaxLength).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasData(SeedFloors.Select((f, i) => new SchoolFloor
            {
                Id = f.Id, Name = f.Name, NormalizedName = TextNormalizer.NameKey(f.Name), SortOrder = i, IsActive = true,
                CreatedAt = SeedTime, UpdatedAt = SeedTime,
            }));
        });

        b.Entity<FloorArea>(e =>
        {
            e.ToTable("FloorAreas", t => t.HasCheckConstraint("CK_FloorAreas_Kind",
                "([Kind] = 1 AND [SchoolClassId] IS NOT NULL AND [Name] IS NULL) OR ([Kind] = 2 AND [SchoolClassId] IS NULL AND [Name] IS NOT NULL)"));
            e.Property(x => x.Name).HasMaxLength(FloorArea.NameMaxLength);
            e.Property(x => x.NormalizedName).HasMaxLength(FloorArea.NameMaxLength);
            e.Property(x => x.Description).HasMaxLength(FloorArea.DescriptionMaxLength);
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            e.HasOne(x => x.Floor).WithMany(f => f.Areas).HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
            // A class has one classroom (one floor at a time); a facility name is used once per floor.
            e.HasIndex(x => x.SchoolClassId).IsUnique().HasFilter("[SchoolClassId] IS NOT NULL");
            e.HasIndex(x => new { x.FloorId, x.NormalizedName }).IsUnique().HasFilter("[NormalizedName] IS NOT NULL");
        });

        b.Entity<FloorAuditItem>(e =>
        {
            e.ToTable("FloorAuditItems");
            e.Property(x => x.Name).HasMaxLength(FloorAuditItem.NameMaxLength).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(FloorAuditItem.NameMaxLength).IsRequired();
            e.Property(x => x.Description).HasMaxLength(FloorAuditItem.DescriptionMaxLength);
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.Property(x => x.UpdatedBy).HasMaxLength(150);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasData(SeedAuditItems.Select((s, i) => new FloorAuditItem
            {
                Id = s.Id, Name = s.Name, NormalizedName = TextNormalizer.NameKey(s.Name), Description = s.Description, AppliesTo = s.Scope,
                ResponseType = s.Type, IsRequired = s.Required, IsActive = true, IsDefault = true, SortOrder = (i + 1) * 10,
                CreatedAt = SeedTime, UpdatedAt = SeedTime,
            }));
        });

        b.Entity<FloorAreaInspection>(e =>
        {
            e.ToTable("FloorAreaInspections", t =>
            {
                t.HasCheckConstraint("CK_FloorAreaInspections_Round", "[Round] BETWEEN 1 AND 3");
                t.HasCheckConstraint("CK_FloorAreaInspections_Completed", "([Status] = 2 AND [CompletedAt] IS NOT NULL) OR ([Status] = 1 AND [CompletedAt] IS NULL)");
            });
            e.Property(x => x.AreaName).HasMaxLength(FloorAreaInspection.AreaNameMaxLength).IsRequired();
            e.Property(x => x.SessionLabel).HasMaxLength(FloorAreaInspection.SessionLabelMaxLength).IsRequired();
            e.Property(x => x.Observations).HasMaxLength(FloorAreaInspection.ObservationsMaxLength);
            e.Property(x => x.IssueSummary).HasMaxLength(FloorAreaInspection.IssueSummaryMaxLength);
            e.Property(x => x.InspectorName).HasMaxLength(FloorAreaInspection.PersonMaxLength);
            e.Property(x => x.CompletedBy).HasMaxLength(FloorAreaInspection.PersonMaxLength);
            e.Property(x => x.UpdatedBy).HasMaxLength(FloorAreaInspection.PersonMaxLength);
            // History is never deleted with what it is about.
            e.HasOne(x => x.Area).WithMany().HasForeignKey(x => x.FloorAreaId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Floor).WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TimeSlot).WithMany().HasForeignKey(x => x.TimeSlotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AcademicYear).WithMany().HasForeignKey(x => x.AcademicYearId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Inspector).WithMany().HasForeignKey(x => x.InspectorStaffId).OnDelete(DeleteBehavior.Restrict);
            // One inspection per area, date, session and round.
            e.HasIndex(x => new { x.FloorAreaId, x.Date, x.TimeSlotId, x.Round }).IsUnique();
            e.HasIndex(x => new { x.Date, x.TimeSlotId, x.Round });
            e.HasIndex(x => new { x.FloorId, x.Date });
        });

        b.Entity<FloorInspectionResponse>(e =>
        {
            e.ToTable("FloorInspectionResponses");
            e.Property(x => x.ItemName).HasMaxLength(FloorAuditItem.NameMaxLength).IsRequired();
            e.Property(x => x.ItemDescription).HasMaxLength(FloorAuditItem.DescriptionMaxLength);
            e.Property(x => x.Value).HasMaxLength(FloorInspectionResponse.ValueMaxLength);
            e.HasOne(x => x.Inspection).WithMany(i => i.Responses).HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.InspectionId, x.ItemId }).IsUnique().HasFilter("[ItemId] IS NOT NULL");
        });

        b.Entity<FloorInspectionAttachment>(e =>
        {
            e.ToTable("FloorInspectionAttachments");
            e.Property(x => x.StoredName).HasMaxLength(32).IsFixedLength().IsUnicode(false).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(FloorInspectionAttachment.FileNameMaxLength).IsRequired();
            e.Property(x => x.Extension).HasMaxLength(10).IsUnicode(false).IsRequired();
            e.Property(x => x.UploadedBy).HasMaxLength(150);
            e.HasOne(x => x.Inspection).WithMany(i => i.Attachments).HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.StoredName).IsUnique();
        });

        b.Entity<StudentMovement>(e =>
        {
            e.ToTable("StudentMovements", t => t.HasCheckConstraint("CK_StudentMovements_ReturnAfterExit", "[ReturnAt] IS NULL OR [ReturnAt] > [ExitAt]"));
            e.Property(x => x.ToName).HasMaxLength(StudentMovement.DestinationMaxLength).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(StudentMovement.NotesMaxLength);
            e.Property(x => x.RecordedBy).HasMaxLength(StudentMovement.PersonMaxLength);
            e.Property(x => x.ReturnedBy).HasMaxLength(StudentMovement.PersonMaxLength);
            e.Property(x => x.VoidedBy).HasMaxLength(StudentMovement.PersonMaxLength);
            e.Property(x => x.VoidReason).HasMaxLength(StudentMovement.NotesMaxLength);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FromClass).WithMany().HasForeignKey(x => x.FromClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FromArea).WithMany().HasForeignKey(x => x.FromAreaId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FromFloor).WithMany().HasForeignKey(x => x.FromFloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToArea).WithMany().HasForeignKey(x => x.ToAreaId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToFloor).WithMany().HasForeignKey(x => x.ToFloorId).OnDelete(DeleteBehavior.Restrict);
            // At most one open (not returned, not voided) movement per student.
            e.HasIndex(x => x.StudentId).IsUnique().HasFilter("[ReturnAt] IS NULL AND [VoidedAt] IS NULL").HasDatabaseName("UX_StudentMovements_OneOpenPerStudent");
            e.HasIndex(x => new { x.Date, x.StudentId });
            e.HasIndex(x => new { x.Date, x.FromFloorId });
            e.HasIndex(x => new { x.Date, x.ToFloorId });
        });

        b.Entity<FloorAuditAccess>(e =>
        {
            e.ToTable("FloorAuditAccess");
            e.Property(x => x.GrantedBy).HasMaxLength(150);
            e.Property(x => x.EndedBy).HasMaxLength(150);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Floor).WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StaffMemberId, x.EndedAt });
        });

        b.Entity<FloorAuditLogEntry>(e =>
        {
            e.ToTable("FloorAuditLog");
            e.Property(x => x.Entity).HasMaxLength(FloorAuditLogEntry.EntityMaxLength).IsRequired();
            e.Property(x => x.Action).HasMaxLength(FloorAuditLogEntry.ActionMaxLength).IsRequired();
            e.Property(x => x.Text).HasMaxLength(FloorAuditLogEntry.TextMaxLength).IsRequired();
            e.Property(x => x.By).HasMaxLength(150);
            e.HasIndex(x => new { x.Entity, x.EntityId });
            e.HasIndex(x => x.At);
        });
    }
}
