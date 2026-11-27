using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data;

/// <summary>Sign-in accounts: one account per staff record (unique, nothing cascades to people) and the account audit trail.</summary>
public partial class SchoolDbContext
{
    private static void ConfigureAccounts(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(AppUser.DisplayNameMaxLength).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(150);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            // One account per person (the Super Admin account has no person).
            e.HasIndex(x => x.StaffMemberId).IsUnique().HasFilter("[StaffMemberId] IS NOT NULL").HasDatabaseName("UX_AspNetUsers_StaffMember");
            // One account per e-mail address (Identity's NormalizedEmail index is not unique by default).
            e.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL").HasDatabaseName("UX_AspNetUsers_NormalizedEmail");
        });

        b.Entity<AccountAuditEvent>(e =>
        {
            e.ToTable("AccountAuditEvents", t => t.HasCheckConstraint("CK_AccountAuditEvents_Action", "[Action] BETWEEN 1 AND 11"));
            foreach (var p in new[] { nameof(AccountAuditEvent.ActorName), nameof(AccountAuditEvent.TargetName), nameof(AccountAuditEvent.OldValue),
                         nameof(AccountAuditEvent.NewValue), nameof(AccountAuditEvent.Detail) })
                e.Property<string?>(p).HasMaxLength(AccountAuditEvent.TextMaxLength);
            e.Property(x => x.ActorUserId).HasMaxLength(450);
            e.Property(x => x.TargetUserId).HasMaxLength(450);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.TargetUserId);
            e.HasIndex(x => x.TargetStaffMemberId);
        });
    }
}
