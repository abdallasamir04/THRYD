using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services.Accounts;

/// <summary>Expected row counts of each sheet, checked again after the file is saved.</summary>
public sealed record WorkbookExpectation(string Sheet, int Rows);

/// <summary>
/// The confidential staff login and responsibilities workbook (Super Admin only): ten sheets built from the current records
/// and the credentials generated in the same provisioning run. Every sheet: a confidential banner (row 1), styled frozen
/// headers with filters (row 2), no merged cells in the data. Missing values are written as "Not recorded".
/// </summary>
public static partial class StaffCredentialsWorkbook
{
    public const string FileName = "THRYD_Complete_Staff_Login_and_Responsibilities.xlsx";
    public const string Banner = "CONFIDENTIAL — SUPER ADMIN ONLY";
    public const string Missing = "Not recorded";

    public static readonly string[] SheetNames =
    [
        "1 All People", "2 CONFIDENTIAL Credentials", "3 Responsibilities", "4 Tasks", "5 Duties & Operations",
        "6 Academic & Timetable", "7 PTC Responsibilities", "8 Roles & Permissions", "9 Admin Review", "10 Summary",
    ];

    [GeneratedRegex(@"[؀-ۿ]")]
    private static partial Regex Arabic();

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>Builds the workbook and the counts it must contain.</summary>
    public static (XLWorkbook Workbook, List<WorkbookExpectation> Expected) Build(PeopleDirectory dir, ProvisionReport report, GeneratedCredential? superAdmin,
        AccountOptions options, string emailDelivery, DateTime generatedUtc)
    {
        var wb = new XLWorkbook();
        var expected = new List<WorkbookExpectation>();
        var local = SchoolTime.Current.ToLocal(generatedUtc);
        var stamp = $"THRYD · Royal Kings International School · {Banner} · generated {local:yyyy-MM-dd HH:mm} (school time)";
        var accounts = dir.Accounts.ToDictionary(a => a.UserId);
        string? LoginOf(int? staffId) => dir.AccountOf(staffId)?.Email;
        var created = report.Credentials.Where(c => c.StaffId is not null).ToDictionary(c => c.StaffId!.Value);
        var outcomes = report.Outcomes.Where(o => o.StaffId is not null).GroupBy(o => o.StaffId!.Value).ToDictionary(g => g.Key, g => g.First());
        var people = dir.People;
        string PersonName(int staffId) => people.FirstOrDefault(p => p.StaffId == staffId)?.FullName ?? $"#{staffId}";
        string? JobOf(int staffId) => people.FirstOrDefault(p => p.StaffId == staffId)?.JobTitle;
        string? DeptOf(int staffId) => people.FirstOrDefault(p => p.StaffId == staffId)?.Department;

        string AccountStatus(DirectoryPerson p)
        {
            if (p.StaffId is not { } id) return "No account (unverified identity)";
            var acc = dir.AccountOf(id);
            if (acc is null) return outcomes.TryGetValue(id, out var o) ? $"Not created: {o.Reason}" : "No account";
            if (acc.Disabled) return "Disabled";
            return created.ContainsKey(id) ? "Active (created in this run)" : "Active (existing)";
        }
        string RoleOf(int? staffId) => dir.AccountOf(staffId) is { } a ? string.Join(", ", a.Roles) : "None (no account)";
        string ProposedEmail(DirectoryPerson p) =>
            dir.AccountOf(p.StaffId)?.Email ?? (LoginEmails.LocalPart(p.FullName) is { } lp ? $"Proposed (not created): {lp}@{options.EmailDomain}" : "Needs Admin Review (no Latin name)");

        // ---------------- 1 All People
        var reviewByPerson = dir.Review.GroupBy(r => r.PersonId).ToDictionary(g => g.Key, g => g.ToList());
        var rows1 = people.Select(p =>
        {
            var id = p.StaffId ?? -1;
            var resp = dir.Responsibilities.Where(r => r.StaffId == id).ToList();
            var linked = dir.Linked.Where(l => l.StaffId == id).Select(l => $"{l.Table}.{l.Column}: {l.Count}").ToList();
            var ptc = dir.Ptc.Where(x => x.StaffId == id).ToList();
            var other = resp.Count(r => r.Type is not ("Task" or "Task (reviewer)" or "Duty" or "PTC"));
            return new object?[]
            {
                p.PersonId, p.FullName, Arabic().IsMatch(p.FullName) ? p.FullName : Missing, Arabic().IsMatch(p.FullName) ? Missing : p.FullName,
                p.SourceEntity, p.SourceRecordId, p.Category, p.JobTitle, p.Position, p.Department, p.Subdepartment, p.Section, p.AssignedClass, p.AssignedFloor,
                p.Phone, p.ExistingEmail, ProposedEmail(p), RoleOf(p.StaffId), p.Archived ? "Archived" : p.Verified ? "Active" : "Unverified", AccountStatus(p),
                dir.Tasks.Count(t => t.StaffId == id), dir.Duties.Count(d => d.StaffId == id), other,
                ptc.Count == 0 ? "None" : string.Join("; ", ptc.Select(x => x.Responsibility).Distinct()),
                string.Join(" · ", new[] { "Roles: " + string.Join(", ", p.Roles), linked.Count == 0 ? null : "Linked records: " + string.Join("; ", linked) }.Where(x => x is not null)),
                p.IdentityConflict ?? "None", reviewByPerson.TryGetValue(p.PersonId, out var rv) ? $"Yes ({rv.Count}): " + string.Join("; ", rv.Select(r => r.Category).Distinct()) : "No",
            };
        }).ToList();
        AddSheet(wb, SheetNames[0], stamp, ["Person ID", "Full Name", "Arabic Name", "English Name", "Source Entity", "Source Record ID", "Person Category", "Job Title",
            "Position", "Department", "Subdepartment", "Assigned Section", "Assigned Class", "Assigned Floor", "Phone Number", "Existing Email", "Generated Login Email",
            "Assigned Role", "Active / Archived Status", "Account Status", "Number of Assigned Tasks", "Number of Assigned Duties", "Number of Other Responsibilities",
            "PTC Responsibilities", "Notes", "Identity Conflict", "Admin Review Required"], rows1);
        expected.Add(new(SheetNames[0], rows1.Count));

        // ---------------- 2 Credentials (actual generated passwords; existing accounts are listed without one)
        var rows2 = new List<object?[]>();
        if (superAdmin is not null)
            rows2.Add(["SUPERADMIN", superAdmin.FullName, superAdmin.Email, superAdmin.Password, "Active (created in this run)", AppRoles.SuperAdmin, Utc(superAdmin.CreatedAt),
                "Generated · valid until changed or reset", "Ready to sign in", "Dedicated Super Admin account (no staff record). No forced change at first sign-in."]);
        rows2.AddRange(report.Credentials.Select(c => new object?[]
        {
            c.PersonId, c.FullName, c.Email, c.Password, "Active (created in this run)", c.Role, Utc(c.CreatedAt), "Generated · valid until changed or reset",
            "Ready to sign in", "Individual random password. No forced change at first sign-in; the person may change it in Account settings.",
        }));
        foreach (var a in dir.Accounts.Where(a => !report.Credentials.Any(c => c.Email == a.Email) && (superAdmin is null || a.Email != superAdmin.Email)))
            rows2.Add([a.StaffId is { } sid ? $"S{sid}" : "SUPERADMIN", a.DisplayName, a.Email, "Not exported (existing account: its password is not recoverable)",
                a.Disabled ? "Disabled" : "Active (existing)", string.Join(", ", a.Roles), Utc(a.CreatedAt), "Existing · unchanged", "Ready to sign in",
                "The Super Admin can reset it from Staff → Accounts."]);
        AddSheet(wb, SheetNames[1], Banner + " — " + stamp, ["Person ID", "Full Name", "Login Email", "Generated Password", "Account Status", "Assigned Role",
            "Account Created Date", "Password Status", "Setup Status", "Notes"], rows2, confidential: true);
        expected.Add(new(SheetNames[1], rows2.Count));

        // ---------------- 3 Responsibilities (every person; one row per verified responsibility)
        var rows3 = new List<object?[]>();
        foreach (var p in people)
        {
            var mine = p.StaffId is { } id ? dir.Responsibilities.Where(r => r.StaffId == id).ToList() : [];
            if (mine.Count == 0)
                rows3.Add([p.FullName, p.PersonId, p.JobTitle, p.Department, "None recorded", "Needs Admin Review", p.Verified ? "No responsibility is recorded for this person." : "Unverified identity.",
                    null, null, p.SourceRecordId, null, null, null, null, null, null, null, null, "Needs Admin Review", null, null]);
            foreach (var r in mine)
                rows3.Add([p.FullName, p.PersonId, p.JobTitle, p.Department, r.Type, r.Name, r.Description, r.Module, r.Entity, r.SourceId, r.Class, r.Section, r.Floor, r.Location,
                    Date(r.Date), Time(r.Start), Time(r.End), r.AssignedBy, r.Status, r.Priority, r.Notes]);
        }
        AddSheet(wb, SheetNames[2], stamp, ["Person Name", "Person ID", "Job Title", "Department", "Responsibility Type", "Responsibility Name", "Description",
            "Related Module", "Related Entity", "Source Record ID", "Assigned Class", "Assigned Section", "Assigned Floor", "Location", "Date", "Start Time", "End Time",
            "Assigned By", "Assignment Status", "Priority", "Notes"], rows3);
        expected.Add(new(SheetNames[2], rows3.Count));

        // ---------------- 4 Tasks
        var rows4 = dir.Tasks.Select(t => new object?[]
        {
            PersonName(t.StaffId) + (t.AsReviewer ? " (reviewer)" : ""), LoginOf(t.StaffId), t.TaskId, t.Title, t.Description, t.Priority.ToString(), t.Status, t.CreatedBy, t.AssignedBy,
            Utc(t.AssignedAt), Date(t.DueDate), t.CompletedAt is { } done ? Utc(done) : null, "Tasks", t.Department, t.Notes,
        }).ToList();
        AddSheet(wb, SheetNames[3], stamp, ["Person Name", "Login Email", "Task ID", "Task Title", "Description", "Priority", "Status", "Created By", "Assigned By",
            "Assigned Date", "Due Date", "Completion Date", "Related Module", "Department", "Notes"], rows4,
            emptyNote: "No tasks are recorded in THRYD (the Tasks module has no records).");
        expected.Add(new(SheetNames[3], rows4.Count));

        // ---------------- 5 Duties & operational assignments (duties + floor-team assignments)
        var rows5 = dir.Duties.Select(d => new object?[]
        {
            PersonName(d.StaffId), LoginOf(d.StaffId), $"Duty #{d.DutyId}", d.DutyType, d.DutyName, d.Description, d.Location, null, Date(d.Date), Time(d.Start), Time(d.End),
            d.AssignedBy, d.Status, d.Responsibility, d.Notes,
        }).Concat(dir.Responsibilities.Where(r => r.Type is "Floor team" or "Floor audit access").Select(r => new object?[]
        {
            PersonName(r.StaffId), LoginOf(r.StaffId), $"{r.Entity} {r.SourceId}", "Floor Management", r.Name, r.Description, null, r.Floor, Date(r.Date), null, null,
            r.AssignedBy, r.Status, r.Class is null ? r.Name : $"{r.Name} · classes {r.Class}", r.Notes,
        })).ToList();
        AddSheet(wb, SheetNames[4], stamp, ["Person Name", "Login Email", "Duty ID", "Duty Type", "Duty Name", "Description", "Location", "Floor", "Date", "Start Time",
            "End Time", "Assigned By", "Status", "Responsibilities", "Notes"], rows5);
        expected.Add(new(SheetNames[4], rows5.Count));

        // ---------------- 6 Academic & timetable
        var rows6 = dir.Academic.Select(a => new object?[]
        {
            PersonName(a.StaffId), LoginOf(a.StaffId), a.StaffId, a.Department ?? DeptOf(a.StaffId), a.Subject, a.Class, a.Section, a.AssignmentType, a.Day, a.Time,
            a.WeeklyPeriods, a.ActiveWeeks, a.SubstituteStatus ?? "None", a.Notes,
        }).ToList();
        AddSheet(wb, SheetNames[5], stamp, ["Person Name", "Login Email", "Staff ID", "Department", "Subject", "Class", "Section", "Assignment Type", "Day", "Time",
            "Weekly Periods", "Active Weeks", "Substitute Status", "Notes"], rows6,
            emptyNote: "The timetable has no lessons; only KG class-team assignments are recorded.");
        expected.Add(new(SheetNames[5], rows6.Count));

        // ---------------- 7 PTC (no student or parent details beyond the booking reference)
        var rows7 = dir.Ptc.Select(p => new object?[]
        {
            PersonName(p.StaffId), LoginOf(p.StaffId), p.Event, p.Class, p.Department, p.Subject, p.Round, Time(p.PlannedStart), Time(p.PlannedEnd),
            p.ActualStart is { } s ? Utc(s) : null, p.ActualEnd is { } e ? Utc(e) : null, p.Status, p.Responsibility, Date(p.Date) is { } d ? $"{d:yyyy-MM-dd} · {p.Notes}" : p.Notes,
        }).ToList();
        rows7.Add([CurrentUserNames.SuperAdmin, options.SuperAdminEmail, "All PTC events", null, null, null, null, null, null, null, null, "Active",
            "PTC administration (events, scheduling, bookings, live operations, corrections)", "Super Admin role"]);
        AddSheet(wb, SheetNames[6], stamp, ["Person Name", "Login Email", "PTC Event", "Class", "Department", "Subject", "Round", "Planned Start", "Planned End",
            "Actual Start", "Actual End", "Interview Status", "Responsibility Type", "Notes"], rows7);
        expected.Add(new(SheetNames[6], rows7.Count));

        // ---------------- 8 Roles & permissions
        List<object?[]> rows8 =
        [
            [CurrentUserNames.SuperAdmin, options.SuperAdminEmail, AppRoles.SuperAdmin, "All modules", "Unrestricted", "View, create, edit, assign, delete, configure", "Whole school",
                "Role: SuperAdmin", "Unrestricted access to every module, account, setting, report and PTC record; no 'Working as' needed."],
        ];
        foreach (var p in people.Where(p => p.Verified))
        {
            var acc = dir.AccountOf(p.StaffId);
            var role = acc is null ? "None (no account)" : string.Join(", ", acc.Roles);
            var email = acc?.Email;
            if (acc is null)
            {
                rows8.Add([p.FullName, email, role, "None", "No access", "Cannot sign in", "—", "No account", AccountStatus(p)]);
                continue;
            }
            if (acc.Roles.Contains(AppRoles.SuperAdmin))
            {
                rows8.Add([p.FullName, email, role, "All modules", "Unrestricted", "All", "Whole school", "Role: SuperAdmin", "Super Admin role given to a staff account."]);
                continue;
            }
            rows8.Add([p.FullName, email, role, "My dashboard", "Own data", "View own profile, tasks, duties, schedule, responsibilities", "Own records", "Role: Staff", null]);
            rows8.Add([p.FullName, email, role, "My PTC interviews", "Own interviews", "View; start and end own interviews (one meeting at a time)", "Own interviews", "Role: Staff", null]);
            rows8.Add([p.FullName, email, role, "Account", "Own account", "Change own password; sign out", "Own account", "Role: Staff", "No forced password change."]);
            foreach (var f in dir.Responsibilities.Where(r => r.StaffId == p.StaffId && r.Type is "Floor team" or "Floor audit access"))
                rows8.Add([p.FullName, email, role, "Floor Management & Floor Audit", f.Name, "As the module allows this role", f.Floor ?? "Assigned floors", $"{f.Entity} {f.SourceId}", null]);
            if (dir.ConfiguredGrants.TryGetValue(p.StaffId!.Value, out var grants))
                foreach (var g in grants)
                    rows8.Add([p.FullName, email, role, g, "Configured grant", "As the module allows", "School", "Configuration (AuthorizedStaffIds)", null]);
        }
        AddSheet(wb, SheetNames[7], stamp, ["Person Name", "Login Email", "Assigned Role", "Module", "Permission", "Allowed Actions", "Access Scope", "Permission Source", "Notes"], rows8);
        expected.Add(new(SheetNames[7], rows8.Count));

        // ---------------- 9 Admin review
        var rows9 = dir.Review.Select(r => new object?[] { r.PersonId, r.Person, r.Category, r.Detail, r.ActionNeeded }).ToList();
        rows9.AddRange(report.Outcomes.Where(o => o.Status == "Failed").Select(o => new object?[]
            { o.PersonId, o.FullName, "Account provisioning conflict", o.Reason, "Resolve the reason, then create the account from Staff → Accounts." }));
        rows9.Add(["—", "All accounts", "Unverified email domain", $"Login e-mails use {options.EmailDomain} as given by the Super Admin; they are login identifiers. {emailDelivery}",
            "Configure e-mail delivery (Email:Smtp settings) if password-reset e-mails are wanted."]);
        AddSheet(wb, SheetNames[8], stamp, ["Person ID", "Person", "Review Category", "Detail", "What the Super Admin must review"], rows9);
        expected.Add(new(SheetNames[8], rows9.Count));

        // ---------------- 10 Summary (from the records above)
        var verified = people.Where(p => p.Verified).ToList();
        List<object?[]> stats =
        [
            ["Total unique non-student people (staff records)", verified.Count],
            ["Unverified identities (source rows without a staff record)", people.Count(p => !p.Verified)],
            ["Active people", verified.Count(p => !p.Archived)],
            ["Archived people", verified.Count(p => p.Archived)],
            ["Existing login accounts (before this run)", dir.Accounts.Count - report.Credentials.Count - (superAdmin is null ? 0 : 1)],
            ["Accounts created in this run (staff)", report.Credentials.Count],
            ["Super Admin account created in this run", superAdmin is null ? 0 : 1],
            ["Total login accounts now", dir.Accounts.Count],
            ["Accounts pending setup (eligible people without an account)", verified.Count(p => p.Eligible && dir.AccountOf(p.StaffId) is null)],
            ["People not provisioned (skipped or failed)", report.Outcomes.Count(o => o.Status is "Skipped" or "Failed")],
            ["Disabled accounts", dir.Accounts.Count(a => a.Disabled)],
            ["Assigned tasks", dir.Tasks.Count(t => !t.AsReviewer)],
            ["Assigned duties", dir.Duties.Count],
            ["Operational assignments (floor teams, floor audit access)", dir.Responsibilities.Count(r => r.Type is "Floor team" or "Floor audit access")],
            ["Academic assignments", dir.Academic.Count],
            ["PTC responsibilities", dir.Ptc.Count],
            ["Unresolved identity conflicts (people)", verified.Count(p => p.IdentityConflict is not null)],
            ["Records requiring Admin review", rows9.Count],
        ];
        foreach (var g in verified.GroupBy(p => p.Department ?? "No department").OrderBy(g => g.Key)) stats.Add(["Department: " + g.Key, g.Count()]);
        foreach (var g in people.GroupBy(p => p.Category).OrderBy(g => g.Key)) stats.Add(["Person category: " + g.Key, g.Count()]);
        foreach (var g in verified.GroupBy(p => RoleOf(p.StaffId)).OrderBy(g => g.Key)) stats.Add(["Role: " + g.Key, g.Count()]);
        foreach (var g in verified.GroupBy(p => AccountStatus(p).Split(':')[0]).OrderBy(g => g.Key)) stats.Add(["Account status: " + g.Key, g.Count()]);
        foreach (var g in people.GroupBy(p => p.Archived ? "Archived" : p.Verified ? "Active" : "Unverified").OrderBy(g => g.Key)) stats.Add(["Active / archived: " + g.Key, g.Count()]);
        stats.Add(["Email delivery", emailDelivery]);
        AddSheet(wb, SheetNames[9], stamp, ["Statistic", "Value"], stats);
        expected.Add(new(SheetNames[9], stats.Count));
        return (wb, expected);
    }

    private static DateOnly? Date(DateOnly? d) => d;
    private static TimeSpan? Time(TimeOnly? t) => t?.ToTimeSpan();
    private static DateTime Utc(DateTime utc) => SchoolTime.Current.ToLocal(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static void AddSheet(XLWorkbook wb, string name, string banner, string[] headers, List<object?[]> rows, bool confidential = false, string? emptyNote = null)
    {
        var ws = wb.Worksheets.Add(name);
        ws.Cell(1, 1).Value = banner;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#B91C1C");
        if (confidential) ws.SetTabColor(XLColor.FromHtml("#B91C1C"));
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(2, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(confidential ? "#B91C1C" : "#3B2A8C");
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < headers.Length; c++)
            {
                var v = c < rows[r].Length ? rows[r][c] : null;
                var cell = ws.Cell(r + 3, c + 1);
                switch (v)
                {
                    case null: cell.Value = Missing; cell.Style.Font.FontColor = XLColor.Gray; break;
                    case string s when s.Length == 0: cell.Value = Missing; cell.Style.Font.FontColor = XLColor.Gray; break;
                    case string s: cell.Value = s; break;
                    case int i: cell.Value = i; break;
                    case DateOnly d: cell.Value = d.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "yyyy-mm-dd"; break;
                    case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
                    case TimeSpan ts: cell.Value = ts; cell.Style.NumberFormat.Format = "hh:mm"; break;
                    default: cell.Value = Convert.ToString(v, C); break;
                }
            }
        var last = Math.Max(2, rows.Count + 2);
        ws.Range(2, 1, last, headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(2);
        ws.Columns(1, headers.Length).AdjustToContents(2, Math.Max(2, Math.Min(last, 200)));
        foreach (var col in ws.Columns(1, headers.Length)) col.Width = Math.Clamp(col.Width, 10, 60);
        ws.Row(2).Height = 30;
        if (rows.Count == 0 && emptyNote is not null) ws.Cell(4, 1).Value = emptyNote;
        else if (rows.Count == 0) ws.Cell(4, 1).Value = "No records.";
    }

    /// <summary>Re-opens the saved file and checks it: a real workbook, the ten sheets in order, and each sheet's data rows.</summary>
    public static List<string> Verify(string path, IReadOnlyList<WorkbookExpectation> expected)
    {
        var problems = new List<string>();
        using var wb = new XLWorkbook(path);
        var names = wb.Worksheets.Select(w => w.Name).ToList();
        if (!names.SequenceEqual(SheetNames)) problems.Add("Sheets: " + string.Join(", ", names));
        foreach (var e in expected)
        {
            if (!wb.TryGetWorksheet(e.Sheet, out var ws)) { problems.Add($"Missing sheet {e.Sheet}"); continue; }
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            var dataRows = e.Rows == 0 ? 0 : lastRow - 2;
            if (dataRows != e.Rows) problems.Add($"{e.Sheet}: {dataRows} data rows, expected {e.Rows}");
            if (ws.Cell(2, 1).GetString().Length == 0) problems.Add($"{e.Sheet}: no header row");
        }
        return problems;
    }
}
