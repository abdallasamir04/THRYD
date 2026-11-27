using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Accounts;
using SchoolManagement.Web.Services.Ptc;

namespace SchoolManagement.Tests;

/// <summary>
/// Sign-in and accounts: login e-mails and passwords, provisioning (one account per person, idempotent, nothing reset),
/// signing in with the generated password (and again: no forced change), voluntary change and Super Admin reset, disabled
/// accounts, the Super Admin's unrestricted access without "Working as", least-privilege staff access that a "Working as"
/// cookie cannot change, PTC identity and one meeting in progress per teacher, and the credentials workbook. A host with
/// sign-in required; the test database only.
/// </summary>
[Collection("db")]
public class AuthTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
    private static WebApplicationFactory<Program>? _signIn;

    /// <summary>The application with sign-in required (as in normal use), sharing the test database.</summary>
    private WebApplicationFactory<Program> SignInHost => _signIn ??= fx.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
        c.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Enabled"] = "true", ["PtcPortal:SubmitPermitLimit"] = "1000" })));

    private sealed class Admin : ICurrentUser
    {
        public string? Name => "Super Admin";
        public int? StaffId => null;
        public bool IsVerified => true;
        public bool IsSuperAdmin => true;
    }

    private sealed record Services(IServiceScope Scope, SchoolDbContext Db, UserManager<AppUser> Users, AccountProvisioningService Provisioning, IPeopleDirectoryService Directory) : IDisposable
    {
        public void Dispose() => Scope.Dispose();
    }

    /// <summary>The real services of the sign-in host, acting as the Super Admin.</summary>
    private Services Svc()
    {
        var scope = SignInHost.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SchoolDbContext>();
        var provisioning = new AccountProvisioningService(db, sp.GetRequiredService<UserManager<AppUser>>(), sp.GetRequiredService<RoleManager<IdentityRole>>(),
            sp.GetRequiredService<IAccountAudit>(), new Admin(), sp.GetRequiredService<IOptions<AccountOptions>>(), sp.GetRequiredService<IPeopleDirectoryService>());
        return new Services(scope, db, sp.GetRequiredService<UserManager<AppUser>>(), provisioning, sp.GetRequiredService<IPeopleDirectoryService>());
    }

    private static PeopleDirectory Only(PeopleDirectory d, params int[] staffIds) => new()
    {
        People = d.People.Where(p => p.StaffId is { } id && staffIds.Contains(id)).ToList(), Accounts = d.Accounts, Responsibilities = d.Responsibilities, Tasks = d.Tasks,
        Duties = d.Duties, Academic = d.Academic, Ptc = d.Ptc, Review = d.Review.Where(r => staffIds.Any(i => r.PersonId == $"S{i}")).ToList(), Linked = d.Linked,
        ConfiguredGrants = d.ConfiguredGrants,
    };

    /// <summary>A new teacher with a unique two-word name, and its fresh account (credential returned once).</summary>
    private async Task<(int StaffId, GeneratedCredential Credential)> AccountAsync()
    {
        var (staffId, _) = await fx.NewTeacherAsync();
        using var s = Svc();
        var (result, credential) = await s.Provisioning.CreateForPersonAsync(staffId, false);
        Assert.True(result.Succeeded, string.Join(" | ", result.Errors.Select(e => e.Value)));
        return (staffId, credential!);
    }

    private async Task<(HttpClient Client, HttpResponseMessage Response)> PostLoginAsync(string email, string password, HttpClient? client = null)
    {
        client ??= SignInHost.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await (await client.GetAsync("/account/login")).Content.ReadAsStringAsync();
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token.Match(page).Groups[1].Value, ["Email"] = email, ["Password"] = password, ["RememberMe"] = "false",
        }));
        return (client, response);
    }

    private async Task<HttpClient> SignedInAsync(string email, string password)
    {
        var (client, response) = await PostLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    // ------------------------------------------------------------------ e-mails and passwords

    [Fact]
    public void Login_emails_are_first_dot_last_and_passwords_are_individual_and_strong()
    {
        Assert.Equal("mohamed.ali", LoginEmails.LocalPart("Mr. Mohamed Ali"));
        Assert.Equal("sara.mostafa", LoginEmails.LocalPart("Ms.  Sara   Mostafa"));
        Assert.Equal("noha.hassan", LoginEmails.LocalPart("Dr. Noha Ahmed Hassan"));
        Assert.Equal("eloise.dupre", LoginEmails.LocalPart("Mme Éloïse Duprè"));
        Assert.Null(LoginEmails.LocalPart("محمد علي"));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mohamed.ali@royalkings.com" };
        Assert.Equal("mohamed.ali2@royalkings.com", LoginEmails.Unique("mohamed.ali", "royalkings.com", taken));
        Assert.Equal("mohamed.ali3@royalkings.com", LoginEmails.Unique("mohamed.ali", "royalkings.com", taken));

        var passwords = Enumerable.Range(0, 300).Select(_ => PasswordGenerator.New()).ToList();
        Assert.Equal(300, passwords.Distinct().Count());
        Assert.All(passwords, p =>
        {
            Assert.Equal(16, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, ch => !char.IsLetterOrDigit(ch));
        });
    }

    // ------------------------------------------------------------------ provisioning

    [Fact]
    public async Task Provisioning_gives_each_eligible_person_one_account_and_never_resets_or_duplicates()
    {
        var (active, _) = await fx.NewTeacherAsync();
        var (archived, _) = await fx.NewTeacherAsync();
        var (dupA, _) = await fx.NewTeacherAsync();
        var (dupB, _) = await fx.NewTeacherAsync();
        await using (var db = fx.NewContext())
        {
            await db.StaffMembers.Where(s => s.Id == archived).ExecuteUpdateAsync(u => u.SetProperty(s => s.ArchivedAt, DateTime.UtcNow));
            var phone = "010" + Random.Shared.Next(10_000_000, 99_999_999);
            await db.StaffMembers.Where(s => s.Id == dupA || s.Id == dupB).ExecuteUpdateAsync(u => u.SetProperty(s => s.ContactNumber, phone).SetProperty(s => s.NormalizedContact, phone));
        }
        using var s = Svc();
        var first = await s.Provisioning.ProvisionAllAsync(Only(await s.Directory.BuildAsync(), active, archived, dupA, dupB));
        var created = Assert.Single(first.Credentials);
        Assert.Equal(active, created.StaffId);
        Assert.EndsWith("@royalkings.com", created.Email);
        Assert.Equal("Skipped", first.Outcomes.Single(o => o.StaffId == archived).Status);
        Assert.All(first.Outcomes.Where(o => o.StaffId == dupA || o.StaffId == dupB), o => Assert.Contains("Duplicate identity", o.Reason));
        var user = await s.Users.FindByEmailAsync(created.Email);
        Assert.True(await s.Users.CheckPasswordAsync(user!, created.Password));
        Assert.Equal(new[] { AppRoles.Staff }, await s.Users.GetRolesAsync(user!));
        Assert.Equal(active, user!.StaffMemberId);

        // Again: nothing new, the existing account and its password unchanged.
        var second = await s.Provisioning.ProvisionAllAsync(Only(await s.Directory.BuildAsync(), active, archived, dupA, dupB));
        Assert.Empty(second.Credentials);
        Assert.Equal("Existing", second.Outcomes.Single(o => o.StaffId == active).Status);
        Assert.Equal(1, await s.Db.Users.CountAsync(u => u.StaffMemberId == active));
        Assert.True(await s.Users.CheckPasswordAsync((await s.Users.FindByEmailAsync(created.Email))!, created.Password));

        // The database refuses a second account for the same person; a duplicate candidate needs an explicit review.
        await Assert.ThrowsAnyAsync<Exception>(() => s.Users.CreateAsync(new AppUser { UserName = "x" + created.Email, Email = "x" + created.Email, DisplayName = "x", StaffMemberId = active }, PasswordGenerator.New()));
        s.Db.ChangeTracker.Clear();
        Assert.False((await s.Provisioning.CreateForPersonAsync(dupA, identityReviewed: false)).Result.Succeeded);
        Assert.True((await s.Provisioning.CreateForPersonAsync(dupA, identityReviewed: true)).Result.Succeeded);
        Assert.False((await s.Provisioning.CreateForPersonAsync(archived, identityReviewed: true)).Result.Succeeded);
    }

    // ------------------------------------------------------------------ sign-in

    [Fact]
    public async Task A_generated_password_signs_in_and_keeps_working_with_no_forced_change()
    {
        var (staffId, cred) = await AccountAsync();
        var client = await SignedInAsync(cred.Email, cred.Password);
        var home = await client.GetAsync("/");
        Assert.Equal("/me", home.Headers.Location?.OriginalString);                               // staff home: own dashboard (not a password page)
        var me = await (await client.GetAsync("/me")).Content.ReadAsStringAsync();
        Assert.Contains(cred.FullName, me);

        // Sign out and in again with the same password: still valid.
        var token = Token.Match(me).Groups[1].Value;
        await client.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(cred.Email, cred.Password)).Response.StatusCode);

        // Wrong password: the same generic answer, recorded.
        var wrong = await PostLoginAsync(cred.Email, cred.Password + "x");
        Assert.Equal(HttpStatusCode.OK, wrong.Response.StatusCode);
        Assert.Contains("The e-mail or password is not correct", await wrong.Response.Content.ReadAsStringAsync());
        await using var db = fx.NewContext();
        Assert.True(await db.AccountAuditEvents.AnyAsync(e => e.TargetStaffMemberId == staffId && e.Action == AccountAuditAction.LoginFailed));
        Assert.True(await db.AccountAuditEvents.AnyAsync(e => e.TargetStaffMemberId == staffId && e.Action == AccountAuditAction.LoginSucceeded));
        Assert.False(await db.AccountAuditEvents.AnyAsync(e => (e.Detail ?? "").Contains(cred.Password) || (e.NewValue ?? "").Contains(cred.Password)));
    }

    [Fact]
    public async Task Passwords_change_voluntarily_reset_by_the_super_admin_and_disabled_accounts_cannot_sign_in()
    {
        var (_, cred) = await AccountAsync();
        var client = await SignedInAsync(cred.Email, cred.Password);
        var form = await (await client.GetAsync("/account/password")).Content.ReadAsStringAsync();
        var changed = await client.PostAsync("/account/password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token.Match(form).Groups[1].Value, ["CurrentPassword"] = cred.Password, ["NewPassword"] = "My-Own-Pass-2027", ["ConfirmPassword"] = "My-Own-Pass-2027",
        }));
        Assert.Equal("/me", changed.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(cred.Email, cred.Password)).Response.StatusCode);          // old one no longer works
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(cred.Email, "My-Own-Pass-2027")).Response.StatusCode);

        // Each Super Admin action in its own scope, as each request has its own context in the app.
        string userId;
        using (var s0 = Svc()) userId = (await s0.Users.FindByEmailAsync(cred.Email))!.Id;
        string? newPassword;
        using (var s1 = Svc())
        {
            var (reset, pw) = await s1.Provisioning.ResetPasswordAsync(userId);
            Assert.True(reset.Succeeded);
            newPassword = pw;
        }
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(cred.Email, "My-Own-Pass-2027")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(cred.Email, newPassword!)).Response.StatusCode);

        using (var s2 = Svc()) Assert.True((await s2.Provisioning.SetDisabledAsync(userId, true)).Succeeded);
        var disabled = await PostLoginAsync(cred.Email, newPassword!);
        Assert.Contains("disabled", await disabled.Response.Content.ReadAsStringAsync());
        using (var s3 = Svc()) Assert.True((await s3.Provisioning.SetDisabledAsync(userId, false)).Succeeded);
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(cred.Email, newPassword!)).Response.StatusCode);
    }

    // ------------------------------------------------------------------ authorization

    [Fact]
    public async Task Without_sign_in_nothing_opens_except_the_sign_in_pages_portal_and_static_files()
    {
        var anonymous = SignInHost.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var page in new[] { "/", "/staff", "/settings", "/ptc", "/ptc/my", "/me", "/staff/accounts", "/students/hub" })
        {
            var r = await anonymous.GetAsync(page);
            Assert.True(r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.ToString().Contains("/account/login"), $"{page}: {r.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/account/login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/account/forgot")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/ptc-booking/unknown-code")).StatusCode);   // portal: no sign-in, no redirect
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/css/app.css")).StatusCode);
    }

    [Fact]
    public async Task With_sign_in_switched_off_everything_opens_as_before_and_the_login_page_signs_no_one_in()
    {
        var (_, cred) = await AccountAsync();                                                     // a real account exists
        var off = fx.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Enabled"] = "false", ["PtcPortal:SubmitPermitLimit"] = "1000" })));
        var client = off.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var page in new[] { "/", "/staff", "/settings", "/ptc", "/students/hub", "/classes", "/timetables", "/duties", "/tasks" })
        {
            var r = await client.GetAsync(page);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{page}: {r.StatusCode} {r.Headers.Location}");
        }
        var home = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("/staff/accounts", home);                                           // previous navigation
        Assert.DoesNotContain("/account/logout", home);

        var (_, login) = await PostLoginAsync(cred.Email, cred.Password, client);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("Sign-in is turned off", await login.Content.ReadAsStringAsync());
        Assert.DoesNotContain(login.Headers.TryGetValues("Set-Cookie", out var set) ? set : [], c => c.StartsWith("thryd.auth"));

        // "Working as" is the identity again: a teacher's dashboard, not the Super Admin's.
        var (teacherId, teacherName) = await fx.NewTeacherAsync();
        var asTeacher = fx.ClientAs(off, teacherId, follow: false);
        Assert.Contains(teacherName, await (await asTeacher.GetAsync("/me")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Staff_have_least_privilege_and_a_working_as_cookie_cannot_change_who_they_are()
    {
        var (staffId, cred) = await AccountAsync();
        var (otherId, otherName) = await fx.NewTeacherAsync();
        var client = await SignedInAsync(cred.Email, cred.Password);
        foreach (var page in new[] { "/settings", "/staff", "/staff/accounts", "/ptc", "/students/hub", "/classes", "/timetables", "/import-export", "/duties", "/management/tasks" })
            Assert.True((await client.GetAsync(page)).StatusCode == HttpStatusCode.Forbidden, page);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ptc/my")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);

        // A "Working as" cookie for someone else changes nothing: the account is the identity.
        var spoof = fx.ClientAs(SignInHost, otherId, follow: false);                              // carries "Working as" = the other teacher
        await PostLoginAsync(cred.Email, cred.Password, spoof);
        var me = await (await spoof.GetAsync("/me")).Content.ReadAsStringAsync();
        Assert.Contains(cred.FullName, me);
        Assert.DoesNotContain(otherName, me);
    }

    [Fact]
    public async Task The_super_admin_signs_in_and_opens_everything_without_working_as()
    {
        using var s = Svc();
        var created = await s.Provisioning.EnsureSuperAdminAsync();
        var admin = (await s.Users.FindByEmailAsync("superadmin@royalkings.com"))!;
        var password = created?.Password ?? (await s.Provisioning.ResetPasswordAsync(admin.Id)).Password!;
        Assert.Contains(AppRoles.SuperAdmin, await s.Users.GetRolesAsync(admin));
        Assert.Null(admin.StaffMemberId);                                                         // no restricted employee identity

        var client = await SignedInAsync("superadmin@royalkings.com", password);
        foreach (var page in new[] { "/", "/settings", "/staff", "/staff/accounts", "/ptc", "/students/hub", "/classes", "/timetables", "/duties", "/tasks", "/import-export" })
        {
            var r = await client.GetAsync(page);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{page}: {r.StatusCode}");
        }
        // An old address that moved (301) still is not a sign-in redirect.
        Assert.DoesNotContain("/account/login", (await client.GetAsync("/management/tasks")).Headers.Location?.ToString() ?? "");
        var (staffId, _) = await fx.NewTeacherAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/staff/accounts/{staffId}")).StatusCode);
    }

    // ------------------------------------------------------------------ PTC: one meeting in progress per teacher

    [Fact]
    public async Task A_teacher_runs_one_parent_meeting_at_a_time_and_one_bookings_subjects_never_block_each_other()
    {
        await using var db = fx.NewContext();
        var day = new DateOnly(2027, 3, 9);
        var settings = new SettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        var admin = new Admin();
        var setup = new PtcSetupService(db, new PtcAccessFor(true), settings, admin, new PtcAdminService(db, new PtcAccessFor(true), settings, new ClassTeamService(db, admin)));
        var year = new AcademicYear { Name = "PTC-auth " + DbFixture.Unique() };
        db.AcademicYears.Add(year);
        await db.SaveChangesAsync();
        var ev = (await setup.SaveEventAsync(null, new PtcEventInput("PTC " + DbFixture.Unique(), null, year.Id, day, day))).EntityId!.Value;
        var cls = await fx.NewClassAsync();
        var (teacher, _) = await fx.NewTeacherAsync();
        var lessons = new List<int>();
        foreach (var (dept, subject, wd) in new[] { (3, "Math", DayOfWeek.Sunday), (4, "Science", DayOfWeek.Monday) })
        {
            var slot = new TimeSlot { Number = Random.Shared.Next(100, 999), SchoolClassId = cls, UpdatedAt = DateTime.UtcNow };
            db.TimeSlots.Add(slot);
            await db.SaveChangesAsync();
            var l = new TimetableAssignment { SchoolClassId = cls, Day = wd, TimeSlotId = slot.Id, DepartmentId = dept, Subject = subject, TeacherId = teacher, StartsOn = SchoolWeek.StartOf(new DateOnly(2026, 9, 6)) };
            db.TimetableAssignments.Add(l);
            await db.SaveChangesAsync();
            lessons.Add(l.Id);
        }
        var schedule = (await setup.ScheduleClassAsync(ev, cls, day, new TimeOnly(8, 0), new TimeOnly(14, 0), null)).EntityId!.Value;
        var round = (await setup.AddRoundAsync(schedule, new TimeOnly(8, 0))).EntityId!.Value;
        await db.PtcEvents.Where(e => e.Id == ev).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, PtcEventStatus.Published));
        async Task Book(params int[] chosen)
        {
            var (student, _) = await fx.NewStudentAsync(cls);
            var phone = "010" + Random.Shared.Next(10_000_000, 99_999_999);
            await db.Students.Where(x => x.Id == student).ExecuteUpdateAsync(u => u.SetProperty(x => x.ParentContactNumber, phone));
            Assert.True((await new PtcBookingService(db, new PtcAccessFor(true), admin).BookAsParentAsync(new PtcBookingRequest(round, student, phone, chosen.Select(PtcSubjectOption.LessonKey).ToList()))).Succeeded);
        }
        await Book(lessons[0], lessons[1]);     // parent A: Math and Science, both with this teacher (one meeting, two subject records)
        await Book(lessons[0]);                 // parent B

        var clock = new SchoolTimeService(new FixedUtc(new DateTimeOffset(day.ToDateTime(new TimeOnly(6, 0)), TimeSpan.Zero)), SchoolTimeService.DefaultTimeZoneId);
        var keys = new EphemeralDataProtectionProvider();
        PtcOperationsService Ops(SchoolDbContext c) => new(c, new PtcAccessFor(false), new TeacherUser(teacher), keys, Options.Create(new PtcOperationsOptions()), clock);
        var dashboard = (await Ops(db).TeacherDashboardAsync(day))!;
        Assert.Equal(2, dashboard.Meetings.Count);
        var a = dashboard.Meetings.Single(m => m.Subjects.Count == 2);
        var b = dashboard.Meetings.Single(m => m.Subjects.Count == 1);

        var startA = await Ops(db).StartAsync(a.Key);
        Assert.True(startA.Succeeded, startA.Error);
        Assert.Equal(2, await db.PtcTeacherInterviews.CountAsync(i => i.TeacherStaffMemberId == teacher && i.Status == PtcInterviewStatus.InProgress));   // both subjects, one meeting
        Assert.StartsWith(PtcOperationsService.OneMeetingMessage, (await Ops(db).StartAsync(b.Key)).Error);
        Assert.True((await Ops(db).EndAsync(a.Key)).Succeeded);
        Assert.True((await Ops(db).StartAsync(b.Key)).Succeeded);
    }

    private sealed class PtcAccessFor(bool admin) : IPtcAccess { public bool CanManage => admin; }
    private sealed class TeacherUser(int id) : ICurrentUser
    {
        public string? Name => "Teacher";
        public int? StaffId => id;
        public bool IsVerified => true;
    }
    private sealed class FixedUtc(DateTimeOffset utc) : TimeProvider { public override DateTimeOffset GetUtcNow() => utc; }

    // ------------------------------------------------------------------ the credentials workbook

    [Fact]
    public async Task The_workbook_has_ten_sheets_real_counts_and_the_generated_credentials()
    {
        var (a, _) = await fx.NewTeacherAsync();
        var (b, _) = await fx.NewTeacherAsync();
        using var s = Svc();
        var report = await s.Provisioning.ProvisionAllAsync(Only(await s.Directory.BuildAsync(), a, b));
        Assert.Equal(2, report.Credentials.Count);
        var after = Only(await s.Directory.BuildAsync(), a, b);
        var (wb, expected) = StaffCredentialsWorkbook.Build(after, report, null, new AccountOptions(), "E-mail delivery is not configured.", DateTime.UtcNow);
        var dir = Path.Combine(Path.GetTempPath(), "thryd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, StaffCredentialsWorkbook.FileName);
        try
        {
            using (wb) wb.SaveAs(path);
            Assert.Empty(StaffCredentialsWorkbook.Verify(path, expected));
            using var check = new XLWorkbook(path);
            Assert.Equal(StaffCredentialsWorkbook.SheetNames, check.Worksheets.Select(w => w.Name));
            var creds = check.Worksheet(StaffCredentialsWorkbook.SheetNames[1]);
            Assert.StartsWith(StaffCredentialsWorkbook.Banner, creds.Cell(1, 1).GetString());
            foreach (var c in report.Credentials)
            {
                var row = creds.RowsUsed().Single(r => r.Cell(3).GetString() == c.Email);
                Assert.Equal(c.Password, row.Cell(4).GetString());                                   // the actual generated password
                Assert.True(await s.Users.CheckPasswordAsync((await s.Users.FindByEmailAsync(c.Email))!, row.Cell(4).GetString()));
            }
            Assert.Equal(2, check.Worksheet(StaffCredentialsWorkbook.SheetNames[0]).RowsUsed().Count() - 2);   // one row per person
        }
        finally { Directory.Delete(dir, true); }
    }
}
