using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimetableImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TimetableImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    AcademicYearId = table.Column<int>(type: "int", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    SheetName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Days = table.Column<int>(type: "int", nullable: false),
                    Periods = table.Column<int>(type: "int", nullable: false),
                    SubjectAssignments = table.Column<int>(type: "int", nullable: false),
                    TeacherAssignments = table.Column<int>(type: "int", nullable: false),
                    UnassignedTeachers = table.Column<int>(type: "int", nullable: false),
                    Breaks = table.Column<int>(type: "int", nullable: false),
                    Warnings = table.Column<int>(type: "int", nullable: false),
                    ReplacedLessons = table.Column<int>(type: "int", nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImportedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimetableImports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimetableImports_AcademicYears_AcademicYearId",
                        column: x => x.AcademicYearId,
                        principalTable: "AcademicYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimetableImports_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimetableImports_AcademicYearId",
                table: "TimetableImports",
                column: "AcademicYearId");

            migrationBuilder.CreateIndex(
                name: "IX_TimetableImports_SchoolClassId_ImportedAt",
                table: "TimetableImports",
                columns: new[] { "SchoolClassId", "ImportedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimetableImports");
        }
    }
}
