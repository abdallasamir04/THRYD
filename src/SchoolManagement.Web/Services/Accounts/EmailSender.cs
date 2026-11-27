using System.Net;
using System.Net.Mail;

namespace SchoolManagement.Web.Services.Accounts;

/// <summary>
/// Sends account e-mails (password-reset links) when SMTP is configured ("Email:Smtp" section: Host, Port, User, Password,
/// From, EnableSsl). Not configured (the current state): <see cref="IsConfigured"/> is false and nothing is sent — the
/// login e-mails are login identifiers only, and the Super Admin resets passwords instead.
/// </summary>
public interface IAccountEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

public sealed class SmtpAccountEmailSender(IConfiguration config) : IAccountEmailSender
{
    private IConfigurationSection Smtp => config.GetSection("Email:Smtp");
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Smtp["Host"]) && !string.IsNullOrWhiteSpace(Smtp["From"]);

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("E-mail delivery is not configured.");
        using var client = new SmtpClient(Smtp["Host"], int.TryParse(Smtp["Port"], out var port) ? port : 587)
        {
            EnableSsl = !string.Equals(Smtp["EnableSsl"], "false", StringComparison.OrdinalIgnoreCase),
            Credentials = string.IsNullOrWhiteSpace(Smtp["User"]) ? null : new NetworkCredential(Smtp["User"], Smtp["Password"]),
        };
        using var message = new MailMessage(Smtp["From"]!, to, subject, htmlBody) { IsBodyHtml = true };
        await client.SendMailAsync(message, ct);
    }
}
