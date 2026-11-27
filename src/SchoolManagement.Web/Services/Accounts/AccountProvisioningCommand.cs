using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Accounts;

/// <summary>
/// "provision-accounts": creates the dedicated Super Admin account and an account for every eligible active non-student
/// person without one, and writes the confidential workbook to the current Windows user's Desktop
/// (<see cref="StaffCredentialsWorkbook.FileName"/>). All in one database transaction that is committed only after the
/// workbook is saved, verified and limited to the current Windows user — so a generated password is never lost: if
/// anything fails, no account is created. Passwords are never printed. Existing accounts and passwords are never changed;
/// running it again creates nothing new. "--replace" allows overwriting an existing workbook (which holds earlier passwords).
///
/// "reset-super-admin-password": recovery for a lost Super Admin password: a new password is written to an owner-only file
/// on the Desktop (delete it after use).
/// </summary>
public static class AccountProvisioningCommand
{
    public const string Name = "provision-accounts";
    public const string RecoveryName = "reset-super-admin-password";

    public static string EmailDelivery(IConfiguration config) =>
        string.IsNullOrWhiteSpace(config["Email:Smtp:Host"]) ? "E-mail delivery is not configured (no SMTP settings): no e-mail is sent." : "E-mail delivery is configured (SMTP).";

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        var replace = args.Contains("--replace");
        var outputDir = OptionValue(args, "--output-dir") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
        {
            Console.Error.WriteLine($"The Desktop directory is not accessible: '{outputDir}'. Nothing was changed.");
            return 2;
        }
        var path = Path.Combine(outputDir, StaffCredentialsWorkbook.FileName);
        if (File.Exists(path) && !replace)
        {
            Console.Error.WriteLine($"{path} already exists and holds passwords from an earlier run. Move it away (or pass --replace). Nothing was changed.");
            return 3;
        }

        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SchoolDbContext>();
        var provisioning = sp.GetRequiredService<IAccountProvisioningService>();
        var directory = sp.GetRequiredService<IPeopleDirectoryService>();
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var options = sp.GetRequiredService<IOptions<AccountOptions>>().Value;
        var temp = Path.Combine(outputDir, $"~THRYD_provisioning_{Guid.NewGuid():N}.xlsx");

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var superAdmin = await provisioning.EnsureSuperAdminAsync();
            var report = await provisioning.ProvisionAllAsync(await directory.BuildAsync());
            var after = await directory.BuildAsync();
            var (workbook, expected) = StaffCredentialsWorkbook.Build(after, report, superAdmin, options, EmailDelivery(app.Configuration), DateTime.UtcNow);
            using (workbook) workbook.SaveAs(temp);
            var acl = RestrictToCurrentUser(temp);

            var problems = StaffCredentialsWorkbook.Verify(temp, expected);
            // Every generated credential must sign in (checked against the stored hash before anything is committed).
            foreach (var c in report.Credentials.Concat(superAdmin is null ? [] : [superAdmin]))
            {
                var user = await users.FindByEmailAsync(c.Email);
                if (user is null || !await users.CheckPasswordAsync(user, c.Password)) problems.Add($"Credential check failed for {c.PersonId}.");
            }
            if (problems.Count > 0) throw new InvalidOperationException("Workbook verification failed: " + string.Join(" | ", problems));

            File.Move(temp, path, overwrite: replace);
            await tx.CommitAsync();

            Console.WriteLine($"Workbook: {path}");
            Console.WriteLine($"Workbook check: OK ({StaffCredentialsWorkbook.SheetNames.Length} sheets; " + string.Join(", ", expected.Select(e => $"{e.Sheet} {e.Rows}")) + ")");
            Console.WriteLine($"File access: {acl}");
            Console.WriteLine($"People: {after.People.Count(p => p.Verified)} staff records, {after.People.Count(p => !p.Verified)} unverified identities");
            Console.WriteLine($"Accounts: Super Admin {(superAdmin is null ? "kept" : "created")}; staff created {report.Credentials.Count}; existing kept {report.Outcomes.Count(o => o.Status == "Existing")}; "
                + $"skipped {report.Outcomes.Count(o => o.Status == "Skipped")}; failed {report.Outcomes.Count(o => o.Status == "Failed")}");
            foreach (var o in report.Outcomes.Where(o => o.Status is "Skipped" or "Failed")) Console.WriteLine($"  {o.Status}: {o.PersonId} {o.FullName} — {o.Reason}");
            Console.WriteLine("Passwords are only in the workbook (never printed).");
            return 0;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            if (File.Exists(temp)) File.Delete(temp);
            Console.Error.WriteLine("Provisioning failed; no account was created and no workbook was kept: " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> RecoverAsync(WebApplication app, string[] args)
    {
        var outputDir = OptionValue(args, "--output-dir") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(outputDir)) { Console.Error.WriteLine($"The Desktop directory is not accessible: '{outputDir}'."); return 2; }
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var audit = scope.ServiceProvider.GetRequiredService<IAccountAudit>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AccountOptions>>().Value;
        var user = await users.FindByEmailAsync(options.SuperAdminEmail);
        if (user is null || !await users.IsInRoleAsync(user, AppRoles.SuperAdmin)) { Console.Error.WriteLine("The Super Admin account does not exist: run provision-accounts."); return 1; }
        var password = PasswordGenerator.New();
        var path = Path.Combine(outputDir, $"THRYD_SuperAdmin_Recovery_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await File.WriteAllTextAsync(path, $"{StaffCredentialsWorkbook.Banner}\r\nTHRYD Super Admin recovery\r\nLogin: {user.Email}\r\nPassword: {password}\r\nDelete this file after signing in.\r\n");
        RestrictToCurrentUser(path);
        var result = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
        if (!result.Succeeded) { File.Delete(path); Console.Error.WriteLine("The password could not be reset."); return 1; }
        user.IsDisabled = false;
        await users.UpdateAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await audit.AddAsync(AccountAuditAction.PasswordReset, user, null, null, "Super Admin recovery (command line)", save: true);
        Console.WriteLine($"The new Super Admin password is in {path} (only your Windows user can open it). Delete the file after use.");
        return 0;
    }

    private static string? OptionValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Removes inherited permissions and gives only the current Windows user access (icacls). Returns what was done.</summary>
    public static string RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return "Not Windows: file permissions unchanged.";
        var who = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var psi = new ProcessStartInfo("icacls", $"\"{path}\" /inheritance:r /grant:r \"{who}:(F)\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
        return p.ExitCode == 0 ? $"Restricted to {who} only (inherited permissions removed)." : $"Could not restrict permissions (icacls exit {p.ExitCode}).";
    }
}
