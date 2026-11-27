using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Accounts;

/// <summary>Sign-in settings ("Accounts" configuration section).</summary>
public sealed class AccountOptions
{
    public const string Section = "Accounts";

    /// <summary>The one switch for sign-in: <c>Authentication:Enabled</c> in configuration (not part of the "Accounts" section).</summary>
    public const string EnabledKey = "Authentication:Enabled";

    /// <summary>
    /// Set from <see cref="EnabledKey"/> (default off). Off: THRYD works as before sign-in existed: no login requirement or
    /// redirect, no least-privilege filter, and any sign-in cookie is ignored so the identity is "Working as" again.
    /// On: sign-in required, role-based access, the account is the identity. The Identity implementation, accounts and
    /// audit records stay in place either way.
    /// </summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>The domain of the generated login e-mails (the school's; given by the Super Admin). Login identifiers only.</summary>
    public string EmailDomain { get; set; } = "royalkings.com";

    /// <summary>The dedicated Super Admin account (no staff record).</summary>
    public string SuperAdminEmail { get; set; } = "superadmin@royalkings.com";
}

/// <summary>Claims added to a signed-in account (read by <see cref="HttpCurrentUser"/>): the linked staff record and display name.</summary>
public static class AccountClaims
{
    public const string StaffId = "thryd:staff_id";
    public const string DisplayName = "thryd:display_name";
}

/// <summary>Puts the account's linked staff record (server-side, never from the browser) and display name in the sign-in cookie.</summary>
public sealed class ThrydClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole> roles, IOptions<Microsoft.AspNetCore.Identity.IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AccountClaims.DisplayName, user.DisplayName));
        if (user.StaffMemberId is { } staffId) identity.AddClaim(new Claim(AccountClaims.StaffId, staffId.ToString(CultureInfo.InvariantCulture)));
        return identity;
    }
}

/// <summary>
/// Login e-mails: firstname.lastname@domain from the person's name (honorifics such as Mr. / Ms. / Dr. removed, accents
/// dropped, letters only), with a number appended when the address is already taken (john.smith2…). Names that give no
/// Latin letters yield no address (the person needs an Admin decision).
/// </summary>
public static class LoginEmails
{
    private static readonly HashSet<string> Honorifics = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "miss", "mx", "dr", "prof", "eng", "mme", "mlle", "madam", "madame", "sir", "coach", "sheikh", "capt",
    };

    /// <summary>The local part ("first.last") from a full name, or null when the name has no usable Latin letters.</summary>
    public static string? LocalPart(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        var decomposed = fullName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetter(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Honorifics.Contains(w)).ToList();
        return words.Count switch
        {
            0 => null,
            1 => words[0],
            _ => $"{words[0]}.{words[^1]}",
        };
    }

    /// <summary>A free address: local@domain, else local2@domain, local3@domain… (case-insensitive against <paramref name="taken"/>).</summary>
    public static string Unique(string local, string domain, ISet<string> taken)
    {
        var candidate = $"{local}@{domain}";
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{local}{n}@{domain}";
        taken.Add(candidate);
        return candidate;
    }
}

/// <summary>Individual, cryptographically random passwords (never derived from a name, number, date or id).</summary>
public static class PasswordGenerator
{
    // No look-alike characters (0/O, 1/l/I), so a password copied from the workbook is read correctly.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%*-_=+?";
    public const int Length = 16;

    public static string New()
    {
        var all = Upper + Lower + Digits + Symbols;
        var chars = new List<char>
        {
            Upper[RandomNumberGenerator.GetInt32(Upper.Length)], Lower[RandomNumberGenerator.GetInt32(Lower.Length)],
            Digits[RandomNumberGenerator.GetInt32(Digits.Length)], Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)],
        };
        while (chars.Count < Length) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }
}

/// <summary>Writes account audit events (never passwords, hashes or reset tokens).</summary>
public interface IAccountAudit
{
    /// <summary>Adds an event to the context; the caller's SaveChanges writes it (with <paramref name="save"/>, saved now).</summary>
    Task AddAsync(AccountAuditAction action, AppUser? target, string? oldValue = null, string? newValue = null, string? detail = null, bool save = false,
        CancellationToken ct = default, string? targetName = null);
}

public sealed class AccountAudit(SchoolDbContext db, IHttpContextAccessor http) : IAccountAudit
{
    private static string? Cut(string? s) => s is null ? null : s.Length <= AccountAuditEvent.TextMaxLength ? s : s[..AccountAuditEvent.TextMaxLength];

    public async Task AddAsync(AccountAuditAction action, AppUser? target, string? oldValue = null, string? newValue = null, string? detail = null, bool save = false,
        CancellationToken ct = default, string? targetName = null)
    {
        var principal = http.HttpContext?.User;
        var signedIn = principal?.Identity?.IsAuthenticated == true;
        db.AccountAuditEvents.Add(new AccountAuditEvent
        {
            At = DateTime.UtcNow,
            Action = action,
            ActorUserId = signedIn ? principal!.FindFirstValue(ClaimTypes.NameIdentifier) : null,
            ActorName = Cut(signedIn ? principal!.FindFirstValue(AccountClaims.DisplayName) ?? principal.Identity!.Name : http.HttpContext is null ? "System command" : null),
            TargetUserId = target?.Id,
            TargetStaffMemberId = target?.StaffMemberId,
            TargetName = Cut(target?.DisplayName ?? targetName),
            OldValue = Cut(oldValue),
            NewValue = Cut(newValue),
            Detail = Cut(detail),
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });
        if (save) await db.SaveChangesAsync(ct);
    }
}

/// <summary>The fallback policy of every endpoint: a signed-in account, unless sign-in is switched off (read per request).</summary>
public sealed class SignInRequiredRequirement : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

public sealed class SignInRequiredHandler(IOptionsMonitor<AccountOptions> options) : Microsoft.AspNetCore.Authorization.AuthorizationHandler<SignInRequiredRequirement>
{
    protected override Task HandleRequirementAsync(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, SignInRequiredRequirement requirement)
    {
        if (!options.CurrentValue.RequireAuthentication || context.User.Identity?.IsAuthenticated == true) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
