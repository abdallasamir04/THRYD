using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.Accounts;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Sign in, sign out, voluntary password change, and password reset (by e-mail only when e-mail delivery is configured;
/// otherwise the Super Admin resets it). No forced password change: a generated password stays valid until the person or
/// the Super Admin changes it. Failures say nothing about which part was wrong; accounts lock after repeated failures.
/// </summary>
[Route("account")]
[StaffAccess(StaffAccessRule.Self)]
public class AccountController(SignInManager<AppUser> signIn, UserManager<AppUser> users, IAccountAudit audit, IAccountEmailSender email,
    Microsoft.Extensions.Options.IOptionsMonitor<AccountOptions> settings) : AppController
{
    /// <summary>Sign-in switched off (Authentication:Enabled = false): the page stays, says so, and signs no one in.</summary>
    private bool SignInOff => !settings.CurrentValue.RequireAuthentication;

    private const string Generic = "The e-mail or password is not correct.";

    private IActionResult Home(string? returnUrl) => LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");

    [HttpGet("login"), AllowAnonymous]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true) return Home(returnUrl);
        ViewData["SignInOff"] = SignInOff;
        return View(new LoginModel { ReturnUrl = returnUrl });
    }

    [HttpPost("login"), AllowAnonymous, ValidateAntiForgeryToken, EnableRateLimiting(PtcPortalController.SubmitPolicy)]
    public async Task<IActionResult> Login(LoginModel model, CancellationToken ct)
    {
        model.Password ??= "";
        if (SignInOff)
        {
            ViewData["SignInOff"] = true;
            return View(model.Cleared());
        }
        var user = string.IsNullOrWhiteSpace(model.Email) ? null : await users.FindByEmailAsync(model.Email.Trim());
        if (user is null)
        {
            await audit.AddAsync(AccountAuditAction.LoginFailed, null, detail: "Unknown e-mail", save: true, ct: ct, targetName: Trim(model.Email));
            ModelState.AddModelError("", Generic);
            return View(model.Cleared());
        }
        // Password first (with lockout), so a disabled account is only reported to someone who knows its password.
        var check = await signIn.CheckPasswordSignInAsync(user, model.Password, lockoutOnFailure: true);
        if (check.IsLockedOut)
        {
            await audit.AddAsync(AccountAuditAction.LoginFailed, user, detail: "Locked out", save: true, ct: ct);
            ModelState.AddModelError("", "Too many failed attempts: this account is locked for 15 minutes. Try again later or ask the Super Admin.");
            return View(model.Cleared());
        }
        if (!check.Succeeded)
        {
            await audit.AddAsync(AccountAuditAction.LoginFailed, user, detail: "Wrong password", save: true, ct: ct);
            ModelState.AddModelError("", Generic);
            return View(model.Cleared());
        }
        if (user.IsDisabled)
        {
            await audit.AddAsync(AccountAuditAction.LoginFailed, user, detail: "Account disabled", save: true, ct: ct);
            ModelState.AddModelError("", "This account is disabled. Ask the Super Admin.");
            return View(model.Cleared());
        }
        await signIn.SignInAsync(user, model.RememberMe);
        user.LastLoginAt = DateTime.UtcNow;
        await users.UpdateAsync(user);
        await audit.AddAsync(AccountAuditAction.LoginSucceeded, user, save: true, ct: ct);
        return Home(model.ReturnUrl);
    }

    private static string? Trim(string? s) => s is null ? null : s.Length > 100 ? s[..100] : s;

    [HttpPost("logout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        await signIn.SignOutAsync();
        if (user is not null) await audit.AddAsync(AccountAuditAction.Logout, user, save: true, ct: ct);
        return Redirect("/account/login");
    }

    [HttpGet("password")]
    public IActionResult ChangePassword() => View(new ChangePasswordModel());

    [HttpPost("password"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordModel model, CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Redirect("/account/login");
        if (model.NewPassword != model.ConfirmPassword) ModelState.AddModelError(nameof(model.ConfirmPassword), "The new passwords do not match.");
        else
        {
            var result = await users.ChangePasswordAsync(user, model.CurrentPassword ?? "", model.NewPassword ?? "");
            if (result.Succeeded)
            {
                await signIn.RefreshSignInAsync(user);
                await audit.AddAsync(AccountAuditAction.PasswordChanged, user, detail: "Changed by the account holder", save: true, ct: ct);
                Toast("Your password was changed.");
                return Redirect("/me");
            }
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
        }
        return View(new ChangePasswordModel());
    }

    [HttpGet("forgot"), AllowAnonymous]
    public IActionResult ForgotPassword() => View(new ForgotPasswordModel { EmailConfigured = email.IsConfigured });

    [HttpPost("forgot"), AllowAnonymous, ValidateAntiForgeryToken, EnableRateLimiting(PtcPortalController.SubmitPolicy)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordModel model, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(model.Email) ? null : await users.FindByEmailAsync(model.Email.Trim());
        if (user is not null)
            await audit.AddAsync(AccountAuditAction.PasswordResetRequested, user, detail: email.IsConfigured ? "Reset e-mail sent" : "No e-mail delivery: Super Admin to reset", save: true, ct: ct);
        if (user is not null && !user.IsDisabled && email.IsConfigured)
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = Url.Action(nameof(ResetPassword), "Account", new { email = user.Email, token }, Request.Scheme)!;
            await email.SendAsync(user.Email!, "THRYD password reset",
                $"<p>To choose a new THRYD password, open <a href=\"{HtmlEncoder.Default.Encode(link)}\">this link</a> within one hour.</p><p>If you did not ask for it, ignore this e-mail.</p>", ct);
        }
        // The same answer whether or not the address has an account.
        return View(new ForgotPasswordModel { EmailConfigured = email.IsConfigured, Sent = true });
    }

    [HttpGet("reset"), AllowAnonymous]
    public IActionResult ResetPassword(string? email, string? token) => View(new ResetPasswordModel { Email = email, Token = token });

    [HttpPost("reset"), AllowAnonymous, ValidateAntiForgeryToken, EnableRateLimiting(PtcPortalController.SubmitPolicy)]
    public async Task<IActionResult> ResetPassword(ResetPasswordModel model, CancellationToken ct)
    {
        if (model.NewPassword != model.ConfirmPassword) { ModelState.AddModelError("", "The new passwords do not match."); return View(model.Cleared()); }
        var user = string.IsNullOrWhiteSpace(model.Email) ? null : await users.FindByEmailAsync(model.Email.Trim());
        var result = user is null ? null : await users.ResetPasswordAsync(user, model.Token ?? "", model.NewPassword ?? "");
        if (result is { Succeeded: true })
        {
            await audit.AddAsync(AccountAuditAction.PasswordReset, user, detail: "Reset with an e-mailed link", save: true, ct: ct);
            Toast("Your password was changed. Sign in with the new password.");
            return Redirect("/account/login");
        }
        if (result is not null) foreach (var e in result.Errors.Where(e => e.Code != "InvalidToken")) ModelState.AddModelError("", e.Description);
        ModelState.AddModelError("", "This reset link is not valid or has expired. Ask for a new one, or ask the Super Admin.");
        return View(model.Cleared());
    }

    [HttpGet("denied"), AllowAnonymous]
    public IActionResult Denied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }
}
