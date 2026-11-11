using System.Text;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Data.TestData;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Import;

// CSV files saved by Excel on Arabic Windows use code page 1256.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// "seed-test-data" / "test-data-status" / "remove-test-data": runs the command and exits (see TestDataCommand).
var testDataCommand = TestDataCommand.Parse(args);
var builder = WebApplication.CreateBuilder(testDataCommand is null ? args : []);

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
builder.Services.AddScoped<IClassService, ClassService>();
builder.Services.AddScoped<IStudentService, StudentService>();
builder.Services.AddScoped<ITeacherService, TeacherService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IDutyService, DutyService>();
builder.Services.AddScoped<IWorkloadService, WorkloadService>();
builder.Services.AddScoped<IStaffDutyService, StaffDutyService>();
builder.Services.AddScoped<IArrivalService, ArrivalService>();
builder.Services.AddScoped<IDepartmentService, DepartmentService>();
builder.Services.AddScoped<IManagementService, ManagementService>();
builder.Services.AddScoped<IManagementTaskService, ManagementTaskService>();
builder.Services.AddScoped<ITimetableService, TimetableService>();
builder.Services.AddScoped<IPeService, PeService>();
builder.Services.AddScoped<IBelongingsService, BelongingsService>();
// The arrival checklist saves rows as JSON; fetch() sends the antiforgery token in this header.
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
builder.Services.AddSingleton<IExcelExportService, ExcelExportService>();
// Branded PDF downloads of tables and timetables (one shared generator for every page).
builder.Services.AddScoped<SchoolManagement.Web.Services.Pdf.IPdfExportService, SchoolManagement.Web.Services.Pdf.PdfExportService>();
if (testDataCommand is not null) TestDataCommand.Register(builder.Services);

builder.Services.AddControllersWithViews(options =>
{
    // Clear messages for values that cannot be parsed (e.g. letters in a number field).
    options.ModelBindingMessageProvider.SetValueMustBeANumberAccessor(field => $"{field} must be a number.");
    options.ModelBindingMessageProvider.SetAttemptedValueIsInvalidAccessor((value, field) => $"\"{value}\" is not valid for {field}.");
});
builder.Services.AddRouting(options => options.LowercaseUrls = true);

var app = builder.Build();

// Applies pending migrations (the first one creates the tables and the 25 default classes).
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
    await db.Database.MigrateAsync();
}

if (testDataCommand is not null)
{
    Environment.ExitCode = await TestDataCommand.RunAsync(app, testDataCommand);
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
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program;
