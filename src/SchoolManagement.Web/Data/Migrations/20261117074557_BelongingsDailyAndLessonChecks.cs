using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class BelongingsDailyAndLessonChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AcademicMaterials_RecordSubject",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AcademicMaterials_Book",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AcademicMaterials_CopyBook",
                table: "AcademicMaterialRecords");

            migrationBuilder.AddColumn<string>(
                name: "RecordedBy",
                table: "BelongingsRecords",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "CopyBook",
                table: "AcademicMaterialRecords",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "Book",
                table: "AcademicMaterialRecords",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "AcademicMaterialRecords",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "LessonEnd",
                table: "AcademicMaterialRecords",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "LessonStart",
                table: "AcademicMaterialRecords",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "AcademicMaterialRecords",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PeriodName",
                table: "AcademicMaterialRecords",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeriodNumber",
                table: "AcademicMaterialRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecordedBy",
                table: "AcademicMaterialRecords",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "AcademicMaterialRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeacherName",
                table: "AcademicMaterialRecords",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimetableAssignmentId",
                table: "AcademicMaterialRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "AcademicMaterialRecords",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            // Rows saved before this migration: their creation time is their last saved time (not 0001-01-01).
            migrationBuilder.Sql("UPDATE [AcademicMaterialRecords] SET [CreatedAt] = [UpdatedAt];");

            migrationBuilder.CreateTable(
                name: "BelongingsChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BelongingsRecordId = table.Column<int>(type: "int", nullable: false),
                    AcademicMaterialRecordId = table.Column<int>(type: "int", nullable: true),
                    Item = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Context = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Before = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    After = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BelongingsChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BelongingsChanges_BelongingsRecords_BelongingsRecordId",
                        column: x => x.BelongingsRecordId,
                        principalTable: "BelongingsRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicMaterialRecords_TimetableAssignmentId",
                table: "AcademicMaterialRecords",
                column: "TimetableAssignmentId");

            migrationBuilder.CreateIndex(
                name: "UX_AcademicMaterials_RecordLesson",
                table: "AcademicMaterialRecords",
                columns: new[] { "BelongingsRecordId", "TimetableAssignmentId" },
                unique: true,
                filter: "[TimetableAssignmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AcademicMaterials_RecordSubject",
                table: "AcademicMaterialRecords",
                columns: new[] { "BelongingsRecordId", "DepartmentId", "Subject" },
                unique: true,
                filter: "[TimetableAssignmentId] IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AcademicMaterials_Book",
                table: "AcademicMaterialRecords",
                sql: "[Book] IS NULL OR [Book] BETWEEN 1 AND 3");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AcademicMaterials_CopyBook",
                table: "AcademicMaterialRecords",
                sql: "[CopyBook] IS NULL OR [CopyBook] BETWEEN 1 AND 3");

            migrationBuilder.CreateIndex(
                name: "IX_BelongingsChanges_BelongingsRecordId_ChangedAt",
                table: "BelongingsChanges",
                columns: new[] { "BelongingsRecordId", "ChangedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_AcademicMaterialRecords_TimetableAssignments_TimetableAssignmentId",
                table: "AcademicMaterialRecords",
                column: "TimetableAssignmentId",
                principalTable: "TimetableAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AcademicMaterialRecords_TimetableAssignments_TimetableAssignmentId",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropTable(
                name: "BelongingsChanges");

            migrationBuilder.DropIndex(
                name: "IX_AcademicMaterialRecords_TimetableAssignmentId",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropIndex(
                name: "UX_AcademicMaterials_RecordLesson",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropIndex(
                name: "UX_AcademicMaterials_RecordSubject",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AcademicMaterials_Book",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AcademicMaterials_CopyBook",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "RecordedBy",
                table: "BelongingsRecords");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "LessonEnd",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "LessonStart",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "PeriodName",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "PeriodNumber",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "RecordedBy",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "TeacherName",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "TimetableAssignmentId",
                table: "AcademicMaterialRecords");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "AcademicMaterialRecords");

            migrationBuilder.AlterColumn<int>(
                name: "CopyBook",
                table: "AcademicMaterialRecords",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Book",
                table: "AcademicMaterialRecords",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_AcademicMaterials_RecordSubject",
                table: "AcademicMaterialRecords",
                columns: new[] { "BelongingsRecordId", "DepartmentId", "Subject" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AcademicMaterials_Book",
                table: "AcademicMaterialRecords",
                sql: "[Book] BETWEEN 1 AND 3");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AcademicMaterials_CopyBook",
                table: "AcademicMaterialRecords",
                sql: "[CopyBook] BETWEEN 1 AND 3");
        }
    }
}
