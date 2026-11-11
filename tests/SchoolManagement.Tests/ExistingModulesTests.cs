using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Import;

namespace SchoolManagement.Tests;

/// <summary>Students, Teachers, Classes and Excel import still work with the new tables in place.</summary>
[Collection("db")]
public class ExistingModulesTests(DbFixture fx)
{
    [Fact]
    public async Task The_25_default_classes_exist_exactly_once()
    {
        await using var db = fx.NewContext();
        var names = await db.Classes.Where(c => c.Id <= 25).OrderBy(c => c.SortOrder).Select(c => c.Name).ToListAsync();
        Assert.Equal(SchoolManagement.Web.Data.DefaultClasses.Names, names);
        Assert.Equal(await db.Classes.CountAsync(), await db.Classes.Select(c => c.NormalizedName).Distinct().CountAsync());

        await db.Database.MigrateAsync(); // applying migrations again adds nothing
        Assert.Equal(25, await db.Classes.CountAsync(c => c.Id <= 25));
    }

    [Fact]
    public async Task Students_are_added_and_duplicates_in_a_class_are_refused()
    {
        await using var db = fx.NewContext();
        var students = DbFixture.Students(db);
        var name = "Student " + DbFixture.Unique();
        var r = await students.CreateAsync(name, 5);
        Assert.True(r.Succeeded);
        Assert.False((await students.CreateAsync("  " + name.ToUpperInvariant() + " ", 5)).Succeeded);
        Assert.True((await students.CreateAsync(name, 6)).Succeeded); // same name, other class
        Assert.Equal("KG2B", (await students.GetAsync(r.EntityId!.Value))!.ClassName);
        Assert.True((await students.DeleteAsync(r.EntityId!.Value)).Succeeded);
    }

    [Fact]
    public async Task Teachers_keep_phone_text_and_duplicates_are_refused()
    {
        await using var db = fx.NewContext();
        var teachers = DbFixture.Teachers(db);
        var name = "Teacher " + DbFixture.Unique();
        var r = await teachers.CreateAsync(name, "Math Teacher", "010 1234 5678");
        Assert.True(r.Succeeded);
        Assert.Equal("010 1234 5678", (await teachers.GetAsync(r.EntityId!.Value))!.ContactNumber);
        Assert.False((await teachers.CreateAsync(name, null, "01012345678")).Succeeded);
        Assert.True((await teachers.DeleteAsync(r.EntityId!.Value)).Succeeded);
    }

    [Fact]
    public async Task Deleting_a_class_moves_its_students_first()
    {
        await using var db = fx.NewContext();
        var classes = DbFixture.Classes(db);
        var created = await classes.CreateAsync("Z" + DbFixture.Unique()[..6], null, true);
        Assert.True(created.Succeeded);
        var classId = created.EntityId!.Value;
        var student = await DbFixture.Students(db).CreateAsync("Student " + DbFixture.Unique(), classId);

        Assert.False((await classes.DeleteAsync(classId, null)).Succeeded); // students present, no target
        var deleted = await classes.DeleteAsync(classId, 25);               // move to G10B, then delete
        Assert.True(deleted.Succeeded, string.Join(" ", deleted.Errors.Select(e => e.Value)));
        Assert.Equal(25, (await DbFixture.Students(db).GetAsync(student.EntityId!.Value))!.ClassId);
    }

    [Fact]
    public async Task Student_excel_import_reports_invalid_and_duplicate_rows()
    {
        var a = "Pupil " + DbFixture.Unique();
        var b = "Pupil " + DbFixture.Unique();
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("List");
        ws.Cell(1, 1).Value = "اسم الطالب";
        ws.Cell(1, 2).Value = "Grade";
        ws.Cell(2, 1).Value = a; ws.Cell(2, 2).Value = "g3a";
        ws.Cell(3, 1).Value = b; ws.Cell(3, 2).Value = "Grade 3A";
        ws.Cell(4, 1).Value = a; ws.Cell(4, 2).Value = "G3A";      // duplicate of row 2
        ws.Cell(5, 1).Value = "Nobody"; ws.Cell(5, 2).Value = "G11Z"; // unknown class
        ws.Cell(6, 1).Value = ""; ws.Cell(6, 2).Value = "G1A";       // missing name
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        await using var db = fx.NewContext();
        var import = DbFixture.Import(db, new MemoryCache(new MemoryCacheOptions()));
        var (session, error) = await import.StartAsync(ImportKind.Students, "students.xlsx", ms.Length, ms);
        Assert.Null(error);
        Assert.Equal(0, session!.HeaderIndex);
        Assert.True(import.ApplyMapping(session, session.Mapping).Succeeded);

        var preview = await import.PreviewAsync(session);
        Assert.Equal((2, 1, 2), (preview.Ready, preview.Duplicates, preview.Invalid));
        Assert.Contains(preview.Rows, r => r.RowNumber == 5 && r.Errors.Any(e => e.Contains("G11Z")));

        var result = await import.CommitAsync(session);
        Assert.Equal((2, 1, 2), (result.Imported, result.SkippedDuplicates, result.Failed));
        Assert.Same(result, await import.CommitAsync(session)); // confirming twice imports once
        Assert.Equal(2, await db.Students.CountAsync(s => s.FullName == a || s.FullName == b));
    }
}
