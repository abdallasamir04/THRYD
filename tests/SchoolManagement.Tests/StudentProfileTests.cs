using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services;

namespace SchoolManagement.Tests;

/// <summary>
/// Student profiles: birth date, admission application date, dismissal method (Bus / Car) and the parent's contact number
/// with a WhatsApp chat link — stored, validated, shown on the profile / details / list, editable by authorized users only.
/// Existing students without this information stay valid and show "Not recorded" (nothing is invented).
/// </summary>
[Collection("db")]
public class StudentProfileTests(DbFixture fx)
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static string PageMain(string html) => WebUtility.HtmlDecode(html[html.IndexOf("<main", StringComparison.Ordinal)..]);

    // ================================================================== WhatsApp links

    [Theory]
    [InlineData("01001234567", "201001234567")]            // Egyptian mobile, local form
    [InlineData("0100 123 4567", "201001234567")]
    [InlineData("0122-888-4774", "201228884774")]
    [InlineData("(0155) 192 9597", "201551929597")]
    [InlineData("+20 100 123 4567", "201001234567")]       // international with +
    [InlineData("0020 100 123 4567", "201001234567")]      // international with 00
    [InlineData("201001234567", "201001234567")]           // Egyptian mobile with the country code, no +
    [InlineData("+971 50 123 4567", "971501234567")]       // another country, written internationally
    [InlineData("+44 7700 900123", "447700900123")]
    [InlineData("٠١٠٠١٢٣٤٥٦٧", "201001234567")]           // Arabic-Indic digits
    public void A_number_with_a_certain_country_gets_a_wa_me_link(string phone, string expected)
    {
        Assert.Equal(expected, WhatsApp.Number(phone));
        Assert.Equal("https://wa.me/" + expected, WhatsApp.Link(phone));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0223456789")]          // Cairo landline: not a mobile, no WhatsApp guess
    [InlineData("01301234567")]         // 013 is not an Egyptian mobile prefix
    [InlineData("050 123 4567")]        // local number of an unknown country
    [InlineData("123456")]
    [InlineData("+12")]                 // too short
    [InlineData("+1234567890123456")]   // too long
    [InlineData("0100abc4567")]         // not a phone number
    [InlineData("010+0123456")]         // + in the middle
    public void A_missing_invalid_or_ambiguous_number_gets_no_link(string? phone)
    {
        Assert.Null(WhatsApp.Number(phone));
        Assert.Null(WhatsApp.Link(phone));
    }

    // ================================================================== service

    [Fact]
    public async Task Details_are_saved_validated_and_kept_when_not_given()
    {
        var classId = await fx.NewClassAsync();
        await using var db = fx.NewContext();
        var students = DbFixture.Students(db);
        var details = new StudentDetailsInput
        {
            BirthDate = new DateOnly(2016, 3, 12), AdmissionApplicationDate = new DateOnly(2021, 5, 2), DismissalMethod = DismissalMethod.Bus,
            ParentContactNumber = " 0100  123 4567 ",
        };
        var created = await students.CreateAsync("Student " + DbFixture.Unique(), classId, default, details);
        Assert.True(created.Succeeded);
        var row = (await students.GetAsync(created.EntityId!.Value))!;
        Assert.Equal((new DateOnly(2016, 3, 12), new DateOnly(2021, 5, 2), DismissalMethod.Bus, "0100 123 4567"),
            (row.BirthDate!.Value, row.AdmissionApplicationDate!.Value, row.DismissalMethod!.Value, row.ParentContactNumber!));
        Assert.Equal("https://wa.me/201001234567", row.ParentWhatsApp);

        // Renaming without details (older callers, imports) keeps every detail.
        Assert.True((await students.UpdateAsync(row.Id, row.FullName + " X", classId)).Succeeded);
        var kept = (await students.GetAsync(row.Id))!;
        Assert.Equal((row.BirthDate, row.AdmissionApplicationDate, row.DismissalMethod, row.ParentContactNumber),
            (kept.BirthDate, kept.AdmissionApplicationDate, kept.DismissalMethod, kept.ParentContactNumber));

        // Updating with details changes them (Car, an international number).
        Assert.True((await students.UpdateAsync(row.Id, kept.FullName, classId, default, new StudentDetailsInput
        {
            BirthDate = row.BirthDate, AdmissionApplicationDate = row.AdmissionApplicationDate, DismissalMethod = DismissalMethod.Car, ParentContactNumber = "+971 50 123 4567",
        })).Succeeded);
        var changed = (await students.GetAsync(row.Id))!;
        Assert.Equal(DismissalMethod.Car, changed.DismissalMethod);
        Assert.Equal("https://wa.me/971501234567", changed.ParentWhatsApp);

        // Server-side rules.
        async Task<string?> Error(StudentDetailsInput d) =>
            (await students.CreateAsync("Student " + DbFixture.Unique(), classId, default, d)).Errors.Select(e => e.Key).FirstOrDefault();
        Assert.Equal("BirthDate", await Error(new() { BirthDate = SchoolWeek.Today.AddDays(1) }));
        Assert.Equal("BirthDate", await Error(new() { BirthDate = new DateOnly(1900, 1, 1) }));
        Assert.Equal("AdmissionApplicationDate", await Error(new() { BirthDate = new DateOnly(2018, 1, 1), AdmissionApplicationDate = new DateOnly(2017, 1, 1) }));
        Assert.Equal("AdmissionApplicationDate", await Error(new() { AdmissionApplicationDate = SchoolWeek.Today.AddYears(2) }));
        Assert.Equal("DismissalMethod", await Error(new() { DismissalMethod = (DismissalMethod)7 }));
        Assert.Equal("ParentContactNumber", await Error(new() { ParentContactNumber = "call me" }));
        Assert.Equal("ParentContactNumber", await Error(new() { ParentContactNumber = "123" }));
        // Legitimate international formats are accepted (even when no WhatsApp link can be made for them).
        foreach (var ok in new[] { "+44 (0) 20 7946 0958", "+1-202-555-0143", "0223456789" })
            Assert.Null(await Error(new() { ParentContactNumber = ok }));
        // Everything empty is valid: nothing is required beyond name and class.
        Assert.Null(await Error(new()));
    }

    // ================================================================== pages

    [Fact]
    public async Task Every_existing_student_opens_a_profile_and_missing_details_show_as_not_recorded()
    {
        var classId = await fx.NewClassAsync();
        var (plain, _) = await fx.NewStudentAsync(classId);
        await using var db = fx.NewContext();
        var client = fx.Factory.CreateClient();
        foreach (var id in await db.Students.Select(s => s.Id).ToListAsync())
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/students/profile/{id}")).StatusCode);

        var html = PageMain(await client.GetStringAsync($"/students/profile/{plain}"));
        foreach (var label in new[] { "Student information", "Student name", "Grade / Class", "Birth date", "Admission application date", "Dismissal method", "Parent contact number" })
            Assert.Contains(label, html);
        Assert.True(Regex.Matches(html, "Not recorded").Count >= 4);
        Assert.DoesNotContain("wa.me", html);
        // Nothing was invented for the existing student.
        var stored = await db.Students.AsNoTracking().SingleAsync(s => s.Id == plain);
        Assert.Equal((null, null, null, null), ((DateOnly?)stored.BirthDate, stored.AdmissionApplicationDate, stored.DismissalMethod, stored.ParentContactNumber));
    }

    [Fact]
    public async Task Profile_details_and_list_show_the_information_and_a_working_WhatsApp_link()
    {
        var classId = await fx.NewClassAsync();
        int withLink, noLink;
        string className, name = "Student " + DbFixture.Unique();
        await using (var db = fx.NewContext())
        {
            var s = DbFixture.Students(db);
            withLink = (await s.CreateAsync(name, classId, default, new StudentDetailsInput
            {
                BirthDate = new DateOnly(2015, 9, 1), AdmissionApplicationDate = new DateOnly(2020, 2, 14), DismissalMethod = DismissalMethod.Car,
                ParentContactNumber = "0122 888 4774",
            })).EntityId!.Value;
            noLink = (await s.CreateAsync("Student " + DbFixture.Unique(), classId, default, new StudentDetailsInput { DismissalMethod = DismissalMethod.Bus, ParentContactNumber = "02 2345 6789" })).EntityId!.Value;
            className = await db.Classes.Where(c => c.Id == classId).Select(c => c.Name).SingleAsync();
        }
        var client = fx.Factory.CreateClient();

        var raw = await client.GetStringAsync($"/students/profile/{withLink}");
        var html = PageMain(raw);
        Assert.Contains(name, html);
        Assert.Contains(className, html);
        Assert.Contains("1 Sep 2015", html);
        Assert.Contains("14 Feb 2020", html);
        Assert.Contains("Car", html);
        Assert.Contains("0122 888 4774", html);
        // The chat link: the right number, a new tab, nothing pre-filled or sent.
        var links = Regex.Matches(raw, "<a [^>]*href=\"(https://wa\\.me/[^\"]*)\"[^>]*>").Select(m => m.Value).ToList();
        Assert.NotEmpty(links);
        Assert.All(links, l => Assert.Contains("href=\"https://wa.me/201228884774\"", l));
        Assert.All(links, l => Assert.Contains("target=\"_blank\"", l));
        Assert.All(links, l => Assert.Contains("rel=\"noopener noreferrer\"", l));
        Assert.DoesNotContain("wa.me/201228884774?text", raw);

        // A number whose country is not certain: shown, but no (broken) link.
        var plain = await client.GetStringAsync($"/students/profile/{noLink}");
        Assert.Contains("02 2345 6789", plain);
        Assert.DoesNotContain("wa.me", plain);
        Assert.Contains("Bus", PageMain(plain));

        // Details modal and the student list.
        var details = PageMain(await client.GetStringAsync($"/students/details/{withLink}"));
        Assert.Contains("1 Sep 2015", details);
        Assert.Contains("https://wa.me/201228884774", details);
        var list = await client.GetStringAsync($"/students?classId={classId}");
        Assert.Contains("Dismissal · Parent", WebUtility.HtmlDecode(list));
        Assert.Contains($"href=\"/students/profile/{withLink}\" title=\"Open profile\"", list);
        Assert.Contains("href=\"https://wa.me/201228884774\"", list);
        Assert.Single(Regex.Matches(list, "wa\\.me/"));   // the second student's number has no link
        // Search and paging keep working.
        Assert.Contains(name, WebUtility.HtmlDecode(await client.GetStringAsync($"/students?search={Uri.EscapeDataString(name)}&pageSize=10&page=1")));
    }

    [Fact]
    public async Task Add_and_edit_forms_save_the_details_and_edit_keeps_unchanged_values()
    {
        var classId = await fx.NewClassAsync();
        var client = fx.Factory.CreateClient();
        var form = await client.GetStringAsync("/students/create");
        foreach (var field in new[] { "name=\"BirthDate\"", "name=\"AdmissionApplicationDate\"", "name=\"DismissalMethod\"", "name=\"ParentContactNumber\"" })
            Assert.Contains(field, form);
        var name = "Student " + DbFixture.Unique();
        var post = await client.PostAsync("/students/create", new FormUrlEncodedContent([
            new("__RequestVerificationToken", Token.Match(form).Groups[1].Value), new("FullName", name), new("SchoolClassId", classId.ToString(CultureInfo.InvariantCulture)),
            new("BirthDate", "2017-04-20"), new("AdmissionApplicationDate", "2022-01-10"), new("DismissalMethod", "Bus"), new("ParentContactNumber", "+20 100 555 0101"),
        ]));
        Assert.True(post.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, post.StatusCode.ToString());
        await using var db = fx.NewContext();
        var s = await db.Students.AsNoTracking().SingleAsync(x => x.FullName == name);
        Assert.Equal((new DateOnly(2017, 4, 20), new DateOnly(2022, 1, 10), DismissalMethod.Bus, "+20 100 555 0101"),
            (s.BirthDate!.Value, s.AdmissionApplicationDate!.Value, s.DismissalMethod!.Value, s.ParentContactNumber!));

        // The edit form is filled with every stored value.
        var edit = await client.GetStringAsync($"/students/edit/{s.Id}");
        Assert.Contains("value=\"2017-04-20\"", edit);
        Assert.Contains("value=\"2022-01-10\"", edit);
        Assert.Matches("<option value=\"Bus\" selected=\"selected\">", edit);
        Assert.Contains("value=\"&#x2B;20 100 555 0101\"", edit);

        // Posting the form back with only the dismissal method changed keeps the rest.
        var changed = await client.PostAsync($"/students/edit/{s.Id}", new FormUrlEncodedContent([
            new("__RequestVerificationToken", Token.Match(edit).Groups[1].Value), new("Id", s.Id.ToString(CultureInfo.InvariantCulture)), new("FullName", name),
            new("SchoolClassId", classId.ToString(CultureInfo.InvariantCulture)), new("BirthDate", "2017-04-20"), new("AdmissionApplicationDate", "2022-01-10"),
            new("DismissalMethod", "Car"), new("ParentContactNumber", "+20 100 555 0101"),
        ]));
        Assert.True(changed.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, changed.StatusCode.ToString());
        var after = await db.Students.AsNoTracking().SingleAsync(x => x.Id == s.Id);
        Assert.Equal((s.BirthDate, s.AdmissionApplicationDate, DismissalMethod.Car, s.ParentContactNumber, s.SchoolClassId),
            (after.BirthDate, after.AdmissionApplicationDate, after.DismissalMethod!.Value, after.ParentContactNumber, after.SchoolClassId));

        // An invalid value is refused with the field's message, and nothing is saved.
        var bad = await client.PostAsync($"/students/edit/{s.Id}", new FormUrlEncodedContent([
            new("__RequestVerificationToken", Token.Match(edit).Groups[1].Value), new("Id", s.Id.ToString(CultureInfo.InvariantCulture)), new("FullName", name),
            new("SchoolClassId", classId.ToString(CultureInfo.InvariantCulture)), new("BirthDate", SchoolWeek.Iso(SchoolWeek.Today.AddDays(3))), new("ParentContactNumber", "abc"),
        ]));
        var badHtml = WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync());
        Assert.Contains("Enter a birth date between", badHtml);
        Assert.Equal(after.BirthDate, (await db.Students.AsNoTracking().SingleAsync(x => x.Id == s.Id)).BirthDate);
    }

    [Fact]
    public async Task Editing_students_follows_the_configured_access()
    {
        var classId = await fx.NewClassAsync();
        var (student, _) = await fx.NewStudentAsync(classId);
        var (allowed, _) = await fx.NewTeacherAsync("Registrar");
        var (other, _) = await fx.NewTeacherAsync("Teacher");

        // Not configured: the existing rule — anyone using the application may edit.
        Assert.Equal(HttpStatusCode.OK, (await fx.ClientAs(fx.Factory, other, follow: false).GetAsync($"/students/edit/{student}")).StatusCode);

        // Configured: only the listed staff (and the Super Admin) may add, edit, transfer or delete; everyone may still view.
        await using var restricted = fx.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StudentRecords:AuthorizedStaffIds:0"] = allowed.ToString(CultureInfo.InvariantCulture),
        })));
        var denied = fx.ClientAs(restricted, other, follow: false);
        foreach (var url in new[] { $"/students/edit/{student}", "/students/create", $"/students/transfer/{student}", $"/classes/{classId}/students/create" })
            Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(url)).StatusCode);
        var token = Token.Match(await restricted.CreateClient().GetStringAsync($"/students/edit/{student}")).Groups[1].Value;
        var post = await denied.PostAsync($"/students/edit/{student}", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("FullName", "Hacked"),
            new("SchoolClassId", classId.ToString(CultureInfo.InvariantCulture)), new("DismissalMethod", "Bus")]));
        Assert.True(post.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, post.StatusCode.ToString());
        await using (var db = fx.NewContext())
            Assert.Null((await db.Students.AsNoTracking().SingleAsync(s => s.Id == student)).DismissalMethod);
        var profile = await denied.GetAsync($"/students/profile/{student}");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.DoesNotContain($"/students/edit/{student}", await profile.Content.ReadAsStringAsync());
        Assert.DoesNotContain("/students/create", await denied.GetStringAsync($"/students?classId={classId}"));

        Assert.Equal(HttpStatusCode.OK, (await fx.ClientAs(restricted, allowed, follow: false).GetAsync($"/students/edit/{student}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await restricted.CreateClient().GetAsync($"/students/edit/{student}")).StatusCode); // Super Admin
    }
}
