using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class KgOneCoTeacher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One active Co-Teacher per KG class and academic year (like the Homeroom Teacher). Existing rows are never changed:
            // if some class already has more than one active Co-Teacher, the index is not created and those classes are listed
            // in the migration output; the service still allows only one, and the class page asks which Co-Teacher stays.
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM ClassTeachingAssignments WHERE IsActive = 1 AND Role = 2
           GROUP BY SchoolClassId, AcademicYearId HAVING COUNT(*) > 1)
BEGIN
    DECLARE @list nvarchar(max) = (SELECT STRING_AGG(CONCAT(c.Name, ' (year ', a.AcademicYearId, '): ', a.n, ' active Co-Teachers'), '; ')
        FROM (SELECT SchoolClassId, AcademicYearId, COUNT(*) n FROM ClassTeachingAssignments WHERE IsActive = 1 AND Role = 2
              GROUP BY SchoolClassId, AcademicYearId HAVING COUNT(*) > 1) a JOIN Classes c ON c.Id = a.SchoolClassId);
    PRINT CONCAT('UX_ClassTeaching_CoTeacher not created. Choose one Co-Teacher for: ', @list);
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ClassTeaching_CoTeacher' AND object_id = OBJECT_ID('ClassTeachingAssignments'))
    EXEC('CREATE UNIQUE INDEX [UX_ClassTeaching_CoTeacher] ON [ClassTeachingAssignments] ([SchoolClassId], [AcademicYearId]) WHERE [IsActive] = 1 AND [Role] = 2');
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ClassTeaching_CoTeacher' AND object_id = OBJECT_ID('ClassTeachingAssignments'))
    DROP INDEX [UX_ClassTeaching_CoTeacher] ON [ClassTeachingAssignments];
");
        }
    }
}
