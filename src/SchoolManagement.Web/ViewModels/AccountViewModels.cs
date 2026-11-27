using SchoolManagement.Web.Services.Accounts;

namespace SchoolManagement.Web.ViewModels;

public sealed class LoginModel
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }

    /// <summary>The same form without the password (never sent back to the browser).</summary>
    public LoginModel Cleared() => new() { Email = Email, RememberMe = RememberMe, ReturnUrl = ReturnUrl };
}

public sealed class ChangePasswordModel
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
    public string? ConfirmPassword { get; set; }
}

public sealed class ForgotPasswordModel
{
    public string? Email { get; set; }
    public bool EmailConfigured { get; set; }
    public bool Sent { get; set; }
}

public sealed class ResetPasswordModel
{
    public string? Email { get; set; }
    public string? Token { get; set; }
    public string? NewPassword { get; set; }
    public string? ConfirmPassword { get; set; }
    public ResetPasswordModel Cleared() => new() { Email = Email, Token = Token };
}

/// <summary>A person's own dashboard (or the Super Admin's view of one person).</summary>
public sealed class PersonDashboardModel
{
    public required PeopleDirectory Directory { get; init; }
    public required DirectoryPerson Person { get; init; }
    public DirectoryAccount? Account { get; init; }
    public IReadOnlyList<string> Modules { get; init; } = [];
    public bool HasPtc => Directory.Ptc.Count > 0;
}

public sealed class StaffAccountsListModel
{
    public required PeopleDirectory Directory { get; init; }
    public string? Status { get; init; }
    public string? Department { get; init; }
    public string? Role { get; init; }
    public string? Category { get; init; }
    public string? Setup { get; init; }
    public string? Query { get; init; }

    public IEnumerable<DirectoryPerson> Rows => Directory.People.Where(p =>
    {
        var acc = Directory.AccountOf(p.StaffId);
        if (Query is { Length: > 0 } q && !p.FullName.Contains(q, StringComparison.OrdinalIgnoreCase) && !(acc?.Email.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        if (Department is { Length: > 0 } d && p.Department != d) return false;
        if (Category is { Length: > 0 } c && p.Category != c) return false;
        if (Role is { Length: > 0 } r && !(acc?.Roles.Contains(r) ?? r == "None")) return false;
        if (Status == "active" && (acc is null || acc.Disabled)) return false;
        if (Status == "disabled" && acc is not { Disabled: true }) return false;
        if (Setup == "account" && acc is null) return false;
        if (Setup == "none" && acc is not null) return false;
        if (Setup == "review" && !Directory.Review.Any(x => x.PersonId == p.PersonId)) return false;
        return true;
    });
}

public sealed class StaffAccountDetailsModel
{
    public required PersonDashboardModel Person { get; init; }
    public required IReadOnlyList<SchoolManagement.Web.Entities.AccountAuditEvent> History { get; init; }
}

/// <summary>A credential shown once to the Super Admin (never stored, logged or shown again).</summary>
public sealed record OneTimeCredentialModel(string FullName, string Email, string Password, string What, int? StaffId);
