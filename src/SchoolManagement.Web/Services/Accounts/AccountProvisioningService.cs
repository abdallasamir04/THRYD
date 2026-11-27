using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Accounts;

/// <summary>
/// A newly generated credential. The plaintext password exists only in memory, only until it is written to the authorized
/// credential workbook or shown once to the Super Admin; the database holds only the Identity hash.
/// </summary>
public sealed record GeneratedCredential(string PersonId, int? StaffId, string FullName, string Email, string Password, string Role, DateTime CreatedAt);

/// <summary>What provisioning did for one person: Created, Existing (kept as is), Skipped or Failed (with the reason).</summary>
public sealed record ProvisionOutcome(string PersonId, int? StaffId, string FullName, string? Email, string Status, string? Reason);

public sealed class ProvisionReport
{
    public List<GeneratedCredential> Credentials { get; } = [];
    public List<ProvisionOutcome> Outcomes { get; } = [];
}

public interface IAccountProvisioningService
{
    Task EnsureRolesAsync(CancellationToken ct = default);

    /// <summary>The dedicated Super Admin account: created once (its credential returned) or left exactly as it is.</summary>
    Task<GeneratedCredential?> EnsureSuperAdminAsync(CancellationToken ct = default);

    /// <summary>
    /// An account for every eligible person of <paramref name="directory"/> (verified, active, no identity conflict) who has
    /// none: unique login e-mail, unique random password, Staff role. Existing accounts and their passwords are never
    /// touched; archived people, unverified identities and duplicate candidates are skipped with the reason. Idempotent.
    /// </summary>
    Task<ProvisionReport> ProvisionAllAsync(PeopleDirectory directory, CancellationToken ct = default);

    /// <summary>Creates one person's account (Super Admin action); a duplicate identity candidate needs <paramref name="identityReviewed"/>.</summary>
    Task<(ServiceResult Result, GeneratedCredential? Credential)> CreateForPersonAsync(int staffId, bool identityReviewed, CancellationToken ct = default);

    /// <summary>Sets a new random password (Super Admin action); the old one stops working and open sessions end.</summary>
    Task<(ServiceResult Result, string? Password)> ResetPasswordAsync(string userId, CancellationToken ct = default);

    Task<ServiceResult> SetDisabledAsync(string userId, bool disabled, CancellationToken ct = default);
    Task<ServiceResult> SetRoleAsync(string userId, string role, CancellationToken ct = default);
}

public sealed class AccountProvisioningService(SchoolDbContext db, UserManager<AppUser> users, RoleManager<IdentityRole> roles, IAccountAudit audit,
    ICurrentUser currentUser, IOptions<AccountOptions> options, IPeopleDirectoryService directory) : IAccountProvisioningService
{
    private string Domain => options.Value.EmailDomain.Trim().TrimStart('@').ToLowerInvariant();

    public async Task EnsureRolesAsync(CancellationToken ct = default)
    {
        foreach (var role in AppRoles.All)
            if (!await roles.RoleExistsAsync(role))
            {
                var r = await roles.CreateAsync(new IdentityRole(role));
                if (!r.Succeeded) throw new InvalidOperationException($"Role {role} could not be created: {string.Join("; ", r.Errors.Select(e => e.Description))}");
            }
    }

    private async Task<HashSet<string>> TakenEmailsAsync(CancellationToken ct) =>
        (await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => u.Email!).ToListAsync(ct)).Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<GeneratedCredential?> EnsureSuperAdminAsync(CancellationToken ct = default)
    {
        await EnsureRolesAsync(ct);
        if ((await users.GetUsersInRoleAsync(AppRoles.SuperAdmin)).Count > 0) return null;   // kept as it is
        var email = options.Value.SuperAdminEmail.Trim().ToLowerInvariant();
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null) throw new InvalidOperationException($"{email} exists but is not a Super Admin: review it before provisioning.");
        var password = PasswordGenerator.New();
        var user = new AppUser { UserName = email, Email = email, DisplayName = CurrentUserNames.SuperAdmin, CreatedAt = DateTime.UtcNow, CreatedBy = Actor(), LockoutEnabled = true };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded) throw new InvalidOperationException("The Super Admin account could not be created: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, AppRoles.SuperAdmin);
        await audit.AddAsync(AccountAuditAction.AccountCreated, user, null, AppRoles.SuperAdmin, "Dedicated Super Admin account", save: true, ct: ct);
        return new GeneratedCredential("SUPERADMIN", null, user.DisplayName, email, password, AppRoles.SuperAdmin, user.CreatedAt);
    }

    private string Actor() => currentUser.AccountName() ?? "Account provisioning";

    /// <summary>Creates the account (hash only stored) and returns its one-time credential; null with the reason when it cannot.</summary>
    private async Task<(GeneratedCredential? Credential, string? Error)> CreateAsync(DirectoryPerson person, ISet<string> taken, CancellationToken ct)
    {
        var local = LoginEmails.LocalPart(person.FullName);
        if (local is null) return (null, "The name has no Latin letters to build a login e-mail from.");
        var email = LoginEmails.Unique(local, Domain, taken);
        var password = PasswordGenerator.New();
        var user = new AppUser
        {
            UserName = email, Email = email, DisplayName = person.FullName.Length > AppUser.DisplayNameMaxLength ? person.FullName[..AppUser.DisplayNameMaxLength] : person.FullName,
            StaffMemberId = person.StaffId, CreatedAt = DateTime.UtcNow, CreatedBy = Actor(), LockoutEnabled = true,
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded) return (null, string.Join("; ", created.Errors.Select(e => e.Description)));
        var role = await users.AddToRoleAsync(user, AppRoles.Staff);
        if (!role.Succeeded) return (null, string.Join("; ", role.Errors.Select(e => e.Description)));
        await audit.AddAsync(AccountAuditAction.AccountCreated, user, null, $"{email} · {AppRoles.Staff}", $"Linked to staff #{person.StaffId}", save: true, ct: ct);
        return (new GeneratedCredential(person.PersonId, person.StaffId, person.FullName, email, password, AppRoles.Staff, user.CreatedAt), null);
    }

    public async Task<ProvisionReport> ProvisionAllAsync(PeopleDirectory directory, CancellationToken ct = default)
    {
        await EnsureRolesAsync(ct);
        var report = new ProvisionReport();
        var taken = await TakenEmailsAsync(ct);
        var linked = (await db.Users.AsNoTracking().Where(u => u.StaffMemberId != null).Select(u => new { u.StaffMemberId, u.Email }).ToListAsync(ct))
            .ToDictionary(u => u.StaffMemberId!.Value, u => u.Email);
        foreach (var p in directory.People)
        {
            if (!p.Verified) { report.Outcomes.Add(new(p.PersonId, null, p.FullName, null, "Skipped", "Unverified identity (no staff record): needs Super Admin review.")); continue; }
            if (linked.TryGetValue(p.StaffId!.Value, out var existing)) { report.Outcomes.Add(new(p.PersonId, p.StaffId, p.FullName, existing, "Existing", "Existing account kept; its password is unchanged and not recoverable.")); continue; }
            if (p.Archived) { report.Outcomes.Add(new(p.PersonId, p.StaffId, p.FullName, null, "Skipped", "Archived: no account is created automatically.")); continue; }
            if (p.IdentityConflict is not null) { report.Outcomes.Add(new(p.PersonId, p.StaffId, p.FullName, null, "Skipped", "Duplicate identity candidate: create the account after review.")); continue; }
            try
            {
                var (credential, error) = await CreateAsync(p, taken, ct);
                if (credential is null) report.Outcomes.Add(new(p.PersonId, p.StaffId, p.FullName, null, "Failed", error));
                else
                {
                    report.Credentials.Add(credential);
                    report.Outcomes.Add(new(p.PersonId, p.StaffId, p.FullName, credential.Email, "Created", null));
                }
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                db.ChangeTracker.Clear();
                report.Outcomes.Add(new(p.PersonId, p.StaffId, p.FullName, null, "Failed", "The account could not be saved: " + ex.GetBaseException().Message));
            }
        }
        return report;
    }

    public async Task<(ServiceResult Result, GeneratedCredential? Credential)> CreateForPersonAsync(int staffId, bool identityReviewed, CancellationToken ct = default)
    {
        if (!currentUser.IsSuperAdmin) return (ServiceResult.Fail("", "Only the Super Admin can create accounts."), null);
        await EnsureRolesAsync(ct);
        var dir = await directory.BuildAsync(staffId, ct);
        var person = dir.Person(staffId);
        if (person is null) return (ServiceResult.Fail("", "This person does not exist."), null);
        if (dir.AccountOf(staffId) is not null) return (ServiceResult.Fail("", "This person already has an account."), null);
        if (person.Archived) return (ServiceResult.Fail("", "This person is archived: reactivate the staff record first."), null);
        if (person.IdentityConflict is not null && !identityReviewed)
            return (ServiceResult.Fail("IdentityReviewed", "This person is a duplicate identity candidate: confirm you reviewed it."), null);
        var (credential, error) = await CreateAsync(person, await TakenEmailsAsync(ct), ct);
        return credential is null ? (ServiceResult.Fail("", error ?? "The account could not be created."), null) : (ServiceResult.Ok(), credential);
    }

    public async Task<(ServiceResult Result, string? Password)> ResetPasswordAsync(string userId, CancellationToken ct = default)
    {
        if (!currentUser.IsSuperAdmin) return (ServiceResult.Fail("", "Only the Super Admin can reset passwords."), null);
        var user = await users.FindByIdAsync(userId);
        if (user is null) return (ServiceResult.Fail("", "This account does not exist."), null);
        var password = PasswordGenerator.New();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, password);   // also renews the security stamp: open sessions end
        if (!result.Succeeded) return (ServiceResult.Fail("", string.Join("; ", result.Errors.Select(e => e.Description))), null);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await audit.AddAsync(AccountAuditAction.PasswordReset, user, null, null, "Reset by the Super Admin (new random password)", save: true, ct: ct);
        return (ServiceResult.Ok(), password);
    }

    private async Task<bool> IsLastSuperAdminAsync(AppUser user) =>
        await users.IsInRoleAsync(user, AppRoles.SuperAdmin) && (await users.GetUsersInRoleAsync(AppRoles.SuperAdmin)).Count(u => !u.IsDisabled) <= 1;

    public async Task<ServiceResult> SetDisabledAsync(string userId, bool disabled, CancellationToken ct = default)
    {
        if (!currentUser.IsSuperAdmin) return ServiceResult.Fail("", "Only the Super Admin can disable or reactivate accounts.");
        var user = await users.FindByIdAsync(userId);
        if (user is null) return ServiceResult.Fail("", "This account does not exist.");
        if (user.IsDisabled == disabled) return ServiceResult.Ok();
        if (disabled && await IsLastSuperAdminAsync(user)) return ServiceResult.Fail("", "The last active Super Admin account cannot be disabled.");
        if (!disabled && user.StaffMemberId is { } sid && await db.StaffMembers.AnyAsync(s => s.Id == sid && s.ArchivedAt != null, ct))
            return ServiceResult.Fail("", "The person is archived: reactivate the staff record before the account.");
        user.IsDisabled = disabled;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);   // an open session ends at the next check (within a minute)
        await audit.AddAsync(disabled ? AccountAuditAction.AccountDisabled : AccountAuditAction.AccountReactivated, user,
            disabled ? "Active" : "Disabled", disabled ? "Disabled" : "Active", save: true, ct: ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SetRoleAsync(string userId, string role, CancellationToken ct = default)
    {
        if (!currentUser.IsSuperAdmin) return ServiceResult.Fail("", "Only the Super Admin can change roles.");
        if (!AppRoles.All.Contains(role)) return ServiceResult.Fail("Role", "Choose an existing role.");
        var user = await users.FindByIdAsync(userId);
        if (user is null) return ServiceResult.Fail("", "This account does not exist.");
        var current = await users.GetRolesAsync(user);
        if (current.Count == 1 && current[0] == role) return ServiceResult.Ok();
        if (role != AppRoles.SuperAdmin && await IsLastSuperAdminAsync(user)) return ServiceResult.Fail("", "The last active Super Admin keeps the Super Admin role.");
        await users.RemoveFromRolesAsync(user, current);
        await users.AddToRoleAsync(user, role);
        await users.UpdateSecurityStampAsync(user);
        await audit.AddAsync(AccountAuditAction.RoleChanged, user, string.Join(", ", current), role, save: true, ct: ct);
        return ServiceResult.Ok();
    }
}
