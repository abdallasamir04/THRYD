using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

/// <summary>Floor Management: floor teams, reports with escalation history, Social Worker tickets. Nothing cascades to people.</summary>
public partial class SchoolDbContext
{
    public DbSet<FloorStaffAssignment> FloorStaffAssignments => Set<FloorStaffAssignment>();
    public DbSet<FloorStaffClass> FloorStaffClasses => Set<FloorStaffClass>();
    public DbSet<FloorReport> FloorReports => Set<FloorReport>();
    public DbSet<FloorReportEvent> FloorReportEvents => Set<FloorReportEvent>();
    public DbSet<FloorTicket> FloorTickets => Set<FloorTicket>();
    public DbSet<FloorTicketEvent> FloorTicketEvents => Set<FloorTicketEvent>();

    private static void ConfigureFloorManagement(ModelBuilder b)
    {
        b.Entity<SchoolFloor>().Property(x => x.GradeRange).HasMaxLength(FloorStaffAssignment.CoverageMaxLength);

        b.Entity<FloorStaffAssignment>(e =>
        {
            e.ToTable("FloorStaffAssignments", t =>
            {
                t.HasCheckConstraint("CK_FloorStaff_Role", "[Role] BETWEEN 1 AND 5");
                t.HasCheckConstraint("CK_FloorStaff_CurrentHasNoEnd", "[IsCurrent] = 0 OR [EndDate] IS NULL");
            });
            e.Property(x => x.SourceKey).HasMaxLength(120).IsUnicode(false);
            e.Property(x => x.Coverage).HasMaxLength(FloorStaffAssignment.CoverageMaxLength);
            e.Property(x => x.EndReason).HasMaxLength(FloorStaffAssignment.NoteMaxLength);
            e.Property(x => x.Note).HasMaxLength(FloorStaffAssignment.NoteMaxLength);
            e.Property(x => x.AssignedBy).HasMaxLength(150);
            e.HasOne(x => x.Floor).WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SourceKey).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
            // A person holds a role on a floor once at a time; one Stage Coordinator and one Floor Supervisor per floor at a time.
            e.HasIndex(x => new { x.FloorId, x.StaffMemberId, x.Role }).IsUnique().HasFilter("[IsCurrent] = 1").HasDatabaseName("UX_FloorStaff_CurrentRole");
            e.HasIndex(x => new { x.FloorId, x.Role }).IsUnique().HasFilter("[IsCurrent] = 1 AND [Role] IN (1, 2)").HasDatabaseName("UX_FloorStaff_OneLead");
            e.HasIndex(x => new { x.StaffMemberId, x.IsCurrent });
        });

        b.Entity<FloorStaffClass>(e =>
        {
            e.ToTable("FloorStaffClasses");
            e.HasKey(x => new { x.FloorStaffAssignmentId, x.SchoolClassId });
            e.HasOne(x => x.Assignment).WithMany(a => a.Classes).HasForeignKey(x => x.FloorStaffAssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FloorReport>(e =>
        {
            e.ToTable("FloorReports", t =>
            {
                t.HasCheckConstraint("CK_FloorReports_Category", "[Category] BETWEEN 1 AND 5");
                t.HasCheckConstraint("CK_FloorReports_Recipient", "[Recipient] BETWEEN 1 AND 3");
                t.HasCheckConstraint("CK_FloorReports_Status", "[Status] BETWEEN 1 AND 5");
                // A report is never addressed to the person it concerns.
                t.HasCheckConstraint("CK_FloorReports_NotToRelated", "[RelatedStaffId] IS NULL OR [RecipientStaffId] IS NULL OR [RelatedStaffId] <> [RecipientStaffId]");
            });
            e.Property(x => x.Title).HasMaxLength(FloorReport.TitleMaxLength).IsRequired();
            e.Property(x => x.Description).HasMaxLength(FloorReport.TextMaxLength).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.HasOne(x => x.Floor).WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RelatedStaff).WithMany().HasForeignKey(x => x.RelatedStaffId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.RecipientStaff).WithMany().HasForeignKey(x => x.RecipientStaffId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.CreatedByStaff).WithMany().HasForeignKey(x => x.CreatedByStaffId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.FloorId, x.Status });
            e.HasIndex(x => x.RecipientStaffId);
        });

        b.Entity<FloorReportEvent>(e =>
        {
            e.ToTable("FloorReportEvents");
            e.Property(x => x.Note).HasMaxLength(FloorReportEvent.NoteMaxLength);
            e.Property(x => x.By).HasMaxLength(150);
            e.HasOne(x => x.Report).WithMany(r => r.Events).HasForeignKey(x => x.FloorReportId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.FloorReportId, x.At });
        });

        b.Entity<FloorTicket>(e =>
        {
            e.ToTable("FloorTickets", t =>
            {
                t.HasCheckConstraint("CK_FloorTickets_Category", "[Category] BETWEEN 1 AND 4");
                t.HasCheckConstraint("CK_FloorTickets_Status", "[Status] BETWEEN 1 AND 6");
            });
            e.Property(x => x.Number).HasMaxLength(20).IsUnicode(false).IsRequired();
            e.Property(x => x.Description).HasMaxLength(FloorTicket.TextMaxLength).IsRequired();
            e.Property(x => x.ResolutionNotes).HasMaxLength(FloorTicket.TextMaxLength);
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Floor).WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SchoolClass).WithMany().HasForeignKey(x => x.SchoolClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AssignedSocialWorker).WithMany().HasForeignKey(x => x.AssignedSocialWorkerId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.CreatedByStaff).WithMany().HasForeignKey(x => x.CreatedByStaffId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.Number);
            e.HasIndex(x => new { x.FloorId, x.Status });
            e.HasIndex(x => x.AssignedSocialWorkerId);
        });

        b.Entity<FloorTicketEvent>(e =>
        {
            e.ToTable("FloorTicketEvents");
            e.Property(x => x.Note).HasMaxLength(FloorReportEvent.NoteMaxLength);
            e.Property(x => x.By).HasMaxLength(150);
            e.HasOne(x => x.Ticket).WithMany(t => t.Events).HasForeignKey(x => x.FloorTicketId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.FloorTicketId, x.At });
        });
    }
}
