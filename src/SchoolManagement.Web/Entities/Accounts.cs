using Microsoft.AspNetCore.Identity;

namespace SchoolManagement.Web.Entities;

// ============================================================ Sign-in accounts (ASP.NET Core Identity)
//
// One account per person and one person per account: an account is linked to exactly one existing staff record
// (StaffMemberId, unique) — or to none for the dedicated Super Admin account, which has no restricted employee identity.
// Students never get accounts. Passwords are stored only as Identity hashes.

/// <summary>The application roles. Staff permissions are least privilege (see Services/Accounts/StaffAccess.cs).</summary>
public static class AppRoles
{
    /// <summary>Unrestricted access to the whole application (the same name ICurrentUser checks).</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>A staff member: their own dashboard, tasks, duties, schedule and PTC, plus modules they hold a specific grant for.</summary>
    public const string Staff = "Staff";

    public static readonly string[] All = [SuperAdmin, Staff];
}

/// <summary>A THRYD sign-in account.</summary>
public class AppUser : IdentityUser
{
    public const int DisplayNameMaxLength = 150;

    /// <summary>The one staff record this account belongs to (null only for the dedicated Super Admin account). Never set by the user.</summary>
    public int? StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }

    public string DisplayName { get; set; } = "";

    /// <summary>Disabled by the Super Admin: cannot sign in (an open session ends within a minute).</summary>
    public bool IsDisabled { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>What an account audit event records. Numbers are stored.</summary>
public enum AccountAuditAction
{
    AccountCreated = 1,
    AccountDisabled = 2,
    AccountReactivated = 3,
    PasswordReset = 4,
    PasswordChanged = 5,
    RoleChanged = 6,
    LoginSucceeded = 7,
    LoginFailed = 8,
    Logout = 9,
    PasswordResetRequested = 10,
    PersonLinkChanged = 11,
}

/// <summary>
/// Audit trail of sensitive account actions: who, which account / person, what, when, and old / new values. Never holds
/// passwords, password hashes or reset tokens.
/// </summary>
public class AccountAuditEvent
{
    public const int TextMaxLength = 300;

    public int Id { get; set; }
    public DateTime At { get; set; }
    public AccountAuditAction Action { get; set; }

    /// <summary>Who did it (account id and name); null actor = the person themselves before sign-in, or a system command.</summary>
    public string? ActorUserId { get; set; }
    public string? ActorName { get; set; }

    public string? TargetUserId { get; set; }
    public int? TargetStaffMemberId { get; set; }
    public string? TargetName { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
}
