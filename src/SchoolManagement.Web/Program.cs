using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Data.TestData;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Import;

// CSV files saved by Excel on Arabic Windows use code page 1256.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// "seed-test-data" / "test-data-status" / "remove-test-data": runs the command and exits (see TestDataCommand).
var testDataCommand = TestDataCommand.Parse(args);
// "seed-org-structure": records the official organizational structure (idempotent), prints the report and exits.
var orgSeedCommand = args.Length > 0 && args[0] == SchoolManagement.Web.Services.OrgStructure.OrgStructureCommand.Name;
// "import-teaching-staff-2027": records the Teaching Staff 2027 roster (idempotent), prints the report and exits.
var rosterCommand = args.Length > 0 && args[0] == SchoolManagement.Web.Services.TeachingRoster.TeachingRosterCommand.Name;
// "seed-floor-management": records the floors' teams for 2026/2027 (idempotent), prints the report and exits.
var floorCommand = args.Length > 0 && args[0] == "seed-floor-management";
// "reconcile-official-staff": reconciles the staff records with the two official sources (idempotent), prints the report and exits.
var officialStaffCommand = args.Length > 0 && args[0] == SchoolManagement.Web.Services.OrgStructure.OfficialStaffCommand.Name;
// "provision-accounts" / "reset-super-admin-password": sign-in accounts and the confidential credentials workbook (see AccountProvisioningCommand).
var accountsCommand = args.Length > 0 && (args[0] == SchoolManagement.Web.Services.Accounts.AccountProvisioningCommand.Name
    || args[0] == SchoolManagement.Web.Services.Accounts.AccountProvisioningCommand.RecoveryName);
var builder = WebApplication.CreateBuilder(testDataCommand is null && !orgSeedCommand && !rosterCommand && !floorCommand && !officialStaffCommand && !accountsCommand ? args : []);

var connectionString = builder.Configuration.GetConnectionString("SchoolDb")
    ?? throw new InvalidOperationException("Connection string 'SchoolDb' is not configured.");

// No retrying execution strategy: class deletion, imports and workload changes use explicit
// transactions (with locks), which a retrying strategy does not allow.
builder.Services.AddDbContext<SchoolDbContext>((sp, options) =>
{
    options.UseSqlServer(connectionString);
    if (testDataCommand is not null) options.AddInterceptors(sp.GetRequiredService<TestDataTracker>());
});

builder.Services.AddMemoryCache();
// One school time reference (Egyptian local time, Africa/Cairo, DST from the OS time zone database) for every module.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISchoolTimeService>(sp => SchoolTimeService.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TimeProvider>(),
    message => sp.GetRequiredService<ILogger<SchoolTimeService>>().LogError("{Message}", message)));
builder.Services.AddScoped<IClassService, ClassService>();
builder.Services.AddScoped<IStudentService, StudentService>();
// Who may add / edit / transfer / delete students ("StudentRecords" section; unrestricted until configured).
builder.Services.Configure<StudentRecordsOptions>(builder.Configuration.GetSection(StudentRecordsOptions.Section));
builder.Services.AddScoped<IStudentRecordsAccess, StudentRecordsAccess>();
builder.Services.AddScoped<ITeacherService, TeacherService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

// ---------------- Sign-in (ASP.NET Core Identity): one account per person; least-privilege staff access; unrestricted Super Admin.
builder.Services.AddOptions<SchoolManagement.Web.Services.Accounts.AccountOptions>()
    .Bind(builder.Configuration.GetSection(SchoolManagement.Web.Services.Accounts.AccountOptions.Section))
    // The single on/off switch: Authentication:Enabled (temporarily false; set true to require sign-in again).
    .Configure<IConfiguration>((o, c) => o.RequireAuthentication = c.GetValue(SchoolManagement.Web.Services.Accounts.AccountOptions.EnabledKey, false));
builder.Services.AddIdentity<SchoolManagement.Web.Entities.AppUser, Microsoft.AspNetCore.Identity.IdentityRole>(o =>
    {
        o.Password.RequiredLength = 12;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireNonAlphanumeric = true;
        o.Password.RequiredUniqueChars = 6;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;   // no confirmation step and no forced password change
        o.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<SchoolDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<SchoolManagement.Web.Services.Accounts.ThrydClaimsFactory>();
// Password-reset tokens expire after an hour; a disabled account's open session ends within a minute.
builder.Services.Configure<Microsoft.AspNetCore.Identity.DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));
builder.Services.Configure<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/account/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/account/denied";
    o.Cookie.Name = "thryd.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(10);
    o.SlidingExpiration = true;
});
// Every endpoint needs a signed-in account unless it is [AllowAnonymous] (sign-in pages, parent portal, error page, static files).
builder.Services.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
    .AddRequirements(new SchoolManagement.Web.Services.Accounts.SignInRequiredRequirement()).Build());
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, SchoolManagement.Web.Services.Accounts.SignInRequiredHandler>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Accounts.IAccountAudit, SchoolManagement.Web.Services.Accounts.AccountAudit>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Accounts.IPeopleDirectoryService, SchoolManagement.Web.Services.Accounts.PeopleDirectoryService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Accounts.IAccountProvisioningService, SchoolManagement.Web.Services.Accounts.AccountProvisioningService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Accounts.IStaffAccessEvaluator, SchoolManagement.Web.Services.Accounts.StaffAccessEvaluator>();
builder.Services.AddSingleton<SchoolManagement.Web.Services.Accounts.IAccountEmailSender, SchoolManagement.Web.Services.Accounts.SmtpAccountEmailSender>();
// Duty Audit is open only to the staff / positions / roles configured in "DutyAudit" (none by default).
builder.Services.Configure<DutyAuditOptions>(builder.Configuration.GetSection(DutyAuditOptions.Section));
builder.Services.AddScoped<IDutyAuditAccess, DutyAuditAccess>();
// Organizational structure (tiers, units, positions, assignments): viewing is open; changes need OrgStructure access.
builder.Services.Configure<SchoolManagement.Web.Services.OrgStructure.OrgStructureOptions>(
    builder.Configuration.GetSection(SchoolManagement.Web.Services.OrgStructure.OrgStructureOptions.Section));
builder.Services.AddScoped<SchoolManagement.Web.Services.OrgStructure.IOrgStructureAccess, SchoolManagement.Web.Services.OrgStructure.OrgStructureAccess>();
builder.Services.AddScoped<SchoolManagement.Web.Services.OrgStructure.IOrgStructureService, SchoolManagement.Web.Services.OrgStructure.OrgStructureService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.OrgStructure.IOrgStructureSeeder, SchoolManagement.Web.Services.OrgStructure.OrgStructureSeeder>();
builder.Services.AddScoped<SchoolManagement.Web.Services.TeachingRoster.ITeachingRosterImporter, SchoolManagement.Web.Services.TeachingRoster.TeachingRosterImporter>();
builder.Services.AddScoped<SchoolManagement.Web.Services.OrgStructure.IOfficialStaffReconciler, SchoolManagement.Web.Services.OrgStructure.OfficialStaffReconciler>();
builder.Services.AddScoped<SchoolManagement.Web.Services.TeachingRoster.IStaffRosterQuery, SchoolManagement.Web.Services.TeachingRoster.StaffRosterQuery>();
// Floor Management: floors' teams, reports / escalations, Social Worker tickets.
builder.Services.AddScoped<SchoolManagement.Web.Services.FloorManagement.IFloorManagementService, SchoolManagement.Web.Services.FloorManagement.FloorManagementService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.FloorManagement.IFloorManagementSeeder, SchoolManagement.Web.Services.FloorManagement.FloorManagementSeeder>();
builder.Services.AddScoped<IDutyService, DutyService>();
builder.Services.AddScoped<IDutyBoardService, DutyBoardService>();
builder.Services.AddScoped<IWorkloadService, WorkloadService>();
builder.Services.AddScoped<IStaffDutyService, StaffDutyService>();
builder.Services.AddScoped<IArrivalService, ArrivalService>();
builder.Services.AddScoped<IDepartmentService, DepartmentService>();
builder.Services.AddScoped<IManagementService, ManagementService>();
builder.Services.AddSingleton<ITaskFileStore, DiskTaskFileStore>();
builder.Services.AddScoped<IManagementTaskService, ManagementTaskService>();
builder.Services.AddScoped<ITimetableService, TimetableService>();
builder.Services.AddScoped<IPeriodService, PeriodService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.Import.ITimetableImportService, SchoolManagement.Web.Services.Timetables.Import.TimetableImportService>();
builder.Services.AddScoped<IActivityService, ActivityService>();
builder.Services.AddScoped<IClassTeamService, ClassTeamService>();
builder.Services.AddScoped<IStaffDirectoryService, StaffDirectoryService>();
builder.Services.AddScoped<IStaffDirectoryExportService, StaffDirectoryExportService>();
builder.Services.AddScoped<IStaffAttendanceService, StaffAttendanceService>();
builder.Services.AddScoped<IStaffEmploymentService, StaffEmploymentService>();
builder.Services.AddScoped<IClassRosterService, ClassRosterService>();
builder.Services.AddScoped<IAttendanceReportService, AttendanceReportService>();
builder.Services.AddScoped<IPeService, PeService>();
builder.Services.AddScoped<IBelongingsService, BelongingsService>();
builder.Services.AddScoped<IBelongingsDailyService, BelongingsDailyService>();
builder.Services.AddScoped<IBelongingsHistoryService, BelongingsHistoryService>();
builder.Services.AddScoped<ISchoolCalendarService, SchoolCalendarService>();
builder.Services.AddScoped<IAttendanceAlertService, AttendanceAlertService>();
builder.Services.AddScoped<ISessionAttendanceService, SessionAttendanceService>();
builder.Services.AddScoped<IAttendanceCalendarService, AttendanceCalendarService>();
// Timetables Center: every timetable type is an ITimetableSource; register a new source to add a type.
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.TimetableData>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableSource, SchoolManagement.Web.Services.Timetables.ClassTimetableSource>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableSource, SchoolManagement.Web.Services.Timetables.TeacherTimetableSource>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableSource, SchoolManagement.Web.Services.Timetables.SubjectTimetableSource>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableSource, SchoolManagement.Web.Services.Timetables.DepartmentTimetableSource>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableSource, SchoolManagement.Web.Services.Timetables.SchoolTimetableSource>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableSource, SchoolManagement.Web.Services.Timetables.PeTimetableSource>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableSource, SchoolManagement.Web.Services.Timetables.DutyRotaSource>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableSource, SchoolManagement.Web.Services.Timetables.BellScheduleSource>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Timetables.ITimetableCenter, SchoolManagement.Web.Services.Timetables.TimetableCenter>();
builder.Services.AddScoped<IIncidentService, IncidentService>();
// Floor Audit & Student Movement: floors and areas, session-based audit rounds, checklist, student access.
builder.Services.AddScoped<IFloorAuditAccessService, FloorAuditAccessService>();
builder.Services.AddScoped<IStudentMovementService, StudentMovementService>();
builder.Services.AddScoped<IFloorAuditService, FloorAuditService>();
builder.Services.AddScoped<IFloorConfigService, FloorConfigService>();
// PTC (Parent-Teacher Conference): Super Admin only; admin pages under /ptc, no parent-facing or public endpoints yet.
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcAccess, SchoolManagement.Web.Services.Ptc.PtcAccess>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcSetupService, SchoolManagement.Web.Services.Ptc.PtcSetupService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcBookingService, SchoolManagement.Web.Services.Ptc.PtcBookingService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcAdminService, SchoolManagement.Web.Services.Ptc.PtcAdminService>();
// PTC scheduling engine (event-wide analysis, conflict review, audited Admin schedule changes).
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcScheduleService, SchoolManagement.Web.Services.Ptc.PtcScheduleService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcCoverageService, SchoolManagement.Web.Services.Ptc.PtcCoverageService>();
builder.Services.Configure<SchoolManagement.Web.Services.Ptc.PtcFeedbackOptions>(builder.Configuration.GetSection(SchoolManagement.Web.Services.Ptc.PtcFeedbackOptions.Section));
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcFeedbackService, SchoolManagement.Web.Services.Ptc.PtcFeedbackService>();
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcReportService, SchoolManagement.Web.Services.Ptc.PtcReportService>();
// PTC live operations: teacher dashboard and interview timer (/ptc/my), Super Admin live view.
builder.Services.Configure<SchoolManagement.Web.Services.Ptc.PtcOperationsOptions>(builder.Configuration.GetSection(SchoolManagement.Web.Services.Ptc.PtcOperationsOptions.Section));
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcOperationsService, SchoolManagement.Web.Services.Ptc.PtcOperationsService>();
// Public parent booking portal (/ptc-booking/{link code}): rate limited per client address; no staff account involved.
builder.Services.Configure<SchoolManagement.Web.Services.Ptc.PtcPortalOptions>(builder.Configuration.GetSection(SchoolManagement.Web.Services.Ptc.PtcPortalOptions.Section));
builder.Services.AddScoped<SchoolManagement.Web.Services.Ptc.IPtcPortalService, SchoolManagement.Web.Services.Ptc.PtcPortalService>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("Too many requests. Please wait a few minutes and try again.", ct);
    };
    // Fixed windows per client address; limits from the "PtcPortal" section (read when a client's window is created).
    static System.Threading.RateLimiting.RateLimitPartition<string> Window(HttpContext http, string policy, Func<SchoolManagement.Web.Services.Ptc.PtcPortalOptions, int> limit)
    {
        var portal = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<SchoolManagement.Web.Services.Ptc.PtcPortalOptions>>().Value;
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(policy + ":" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, limit(portal)), Window = TimeSpan.FromMinutes(Math.Max(1, portal.WindowMinutes)), QueueLimit = 0,
            });
    }
    o.AddPolicy(SchoolManagement.Web.Controllers.PtcPortalController.RequestPolicy, http => Window(http, "request", p => p.RequestPermitLimit));
    o.AddPolicy(SchoolManagement.Web.Controllers.PtcPortalController.SubmitPolicy, http => Window(http, "submit", p => p.SubmitPermitLimit));
});
if (testDataCommand is null) builder.Services.AddHostedService<AttendanceAlertSweep>();
// After the 09:30 cutoff students with no arrival are Absent; ArrivalFinalizer stores those records (off in tests).
var automaticAbsences = builder.Configuration.GetValue("Attendance:AutoFinalize", true);
builder.Services.AddSingleton(new AttendanceOptions(automaticAbsences));
if (testDataCommand is null && automaticAbsences) builder.Services.AddHostedService<ArrivalFinalizer>();
// Staff attendance closes after 09:30 (StaffAttendance:Cutoff, school time); staff with no record are then Absent and
// StaffAttendanceFinalizer stores those records every minute (off with StaffAttendance:AutoFinalize=false, and in tests).
var staffAutomaticAbsences = builder.Configuration.GetValue("StaffAttendance:AutoFinalize", true);
var staffCutoff = TimeOnly.TryParseExact(builder.Configuration["StaffAttendance:Cutoff"], "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
    System.Globalization.DateTimeStyles.None, out var configuredCutoff) ? configuredCutoff : StaffAttendanceOptions.DefaultCutoff;
builder.Services.AddSingleton(new StaffAttendanceOptions(staffAutomaticAbsences, staffCutoff));
if (testDataCommand is null && staffAutomaticAbsences) builder.Services.AddHostedService<StaffAttendanceFinalizer>();
// The arrival checklist saves rows as JSON; fetch() sends the antiforgery token in this header.
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
builder.Services.AddSingleton<IExcelExportService, ExcelExportService>();
// Branded PDF downloads of tables and timetables (one shared generator for every page).
builder.Services.AddScoped<SchoolManagement.Web.Services.Pdf.IPdfExportService, SchoolManagement.Web.Services.Pdf.PdfExportService>();
if (testDataCommand is not null) TestDataCommand.Register(builder.Services);

builder.Services.AddControllersWithViews(options =>
{
    // Least privilege: once sign-in is required, a staff account opens only what it is allowed to (Super Admin: everything).
    options.Filters.Add<SchoolManagement.Web.Services.Accounts.StaffAccessFilter>();
    // Clear messages for values that cannot be parsed (e.g. letters in a number field).
    options.ModelBindingMessageProvider.SetValueMustBeANumberAccessor(field => $"{field} must be a number.");
    options.ModelBindingMessageProvider.SetAttemptedValueIsInvalidAccessor((value, field) => $"\"{value}\" is not valid for {field}.");
});
builder.Services.AddRouting(options =>
{
    options.LowercaseUrls = true;
    // /duties/{type:dutytype}/…: any Main Duty in the catalog (the original three and duties added with Add Duty).
    options.ConstraintMap[DutyTypeRouteConstraint.Name] = typeof(DutyTypeRouteConstraint);
});

var app = builder.Build();

// Static helpers (SchoolWeek.Today, Ui.DateTime, views) use the same school clock as the injected services.
SchoolTime.Current = app.Services.GetRequiredService<ISchoolTimeService>();

// Applies pending migrations (the first one creates the tables and the 25 default classes).
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
    await db.Database.MigrateAsync();
}

// Sub-Duty definitions (names, slugs, order) come from the database; the built-in list is the fallback until then.
{
    using var scope = app.Services.CreateScope();
    try { await DutyCatalog.LoadAsync(scope.ServiceProvider.GetRequiredService<SchoolDbContext>()); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Duty sections could not be loaded; using the built-in list."); }
}

if (testDataCommand is not null)
{
    Environment.ExitCode = await TestDataCommand.RunAsync(app, testDataCommand);
    return;
}

if (floorCommand)
{
    using var scope = app.Services.CreateScope();
    var floorReport = await scope.ServiceProvider.GetRequiredService<SchoolManagement.Web.Services.FloorManagement.IFloorManagementSeeder>().SeedAsync();
    Console.WriteLine($"Floor Management setup: floors created {floorReport.FloorsCreated}, grade ranges set {floorReport.FloorsUpdated}, staff reused {floorReport.StaffReused}, "
        + $"staff created {floorReport.StaffCreated}, assignments created {floorReport.AssignmentsCreated} (already {floorReport.AlreadyAssigned}), flagged {floorReport.Flagged}");
    foreach (var line in floorReport.Lines) Console.WriteLine("  " + line);
    return;
}

if (rosterCommand)
{
    Environment.ExitCode = await SchoolManagement.Web.Services.TeachingRoster.TeachingRosterCommand.RunAsync(app);
    return;
}

if (officialStaffCommand)
{
    Environment.ExitCode = await SchoolManagement.Web.Services.OrgStructure.OfficialStaffCommand.RunAsync(app);
    return;
}

if (accountsCommand)
{
    Environment.ExitCode = args[0] == SchoolManagement.Web.Services.Accounts.AccountProvisioningCommand.Name
        ? await SchoolManagement.Web.Services.Accounts.AccountProvisioningCommand.RunAsync(app, args)
        : await SchoolManagement.Web.Services.Accounts.AccountProvisioningCommand.RecoverAsync(app, args);
    return;
}

if (orgSeedCommand)
{
    Environment.ExitCode = await SchoolManagement.Web.Services.OrgStructure.OrgStructureCommand.RunAsync(app);
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
// Sign-in switched off (Authentication:Enabled = false): every request is anonymous, so the identity is "Working as" as
// before sign-in existed, whatever sign-in cookie the browser still holds. Nothing of the Identity setup is removed.
app.Use((context, next) =>
{
    if (!context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SchoolManagement.Web.Services.Accounts.AccountOptions>>().CurrentValue.RequireAuthentication)
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
    return next(context);
});
app.UseAuthorization();

// Static files (CSS, scripts, the login page's assets) never need a sign-in.
app.MapStaticAssets().AllowAnonymous();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program;
