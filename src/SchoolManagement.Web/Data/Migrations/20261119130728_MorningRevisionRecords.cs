using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MorningRevisionRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MorningRevisionRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DutyAssignmentId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    TeacherId = table.Column<int>(type: "int", nullable: false),
                    Lesson = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntryTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    ExitTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MorningRevisionRecords", x => x.Id);
                    table.CheckConstraint("CK_MorningRevision_ExitAfterEntry", "[EntryTime] IS NULL OR [ExitTime] IS NULL OR [ExitTime] > [EntryTime]");
                    table.ForeignKey(
                        name: "FK_MorningRevisionRecords_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MorningRevisionRecords_DutyAssignments_DutyAssignmentId",
                        column: x => x.DutyAssignmentId,
                        principalTable: "DutyAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MorningRevisionRecords_StaffMembers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // In-Class Morning becomes Morning Revision (same section, code and slug, so its records, history and links are
            // unchanged). Only a section still carrying the original name is renamed: a name set on the Sub-Duty page is kept.
            migrationBuilder.Sql("""
                UPDATE [DutySections]
                SET [Name] = N'Morning Revision',
                    [Summary] = CASE WHEN [Summary] = N'Staff supervising classes before the first lesson.'
                        THEN N'Classroom revision before the first lesson: which teacher revised which lesson with which class, and when they entered and left.'
                        ELSE [Summary] END
                WHERE [Id] = 2 AND [Name] = N'In-Class Morning';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MorningRevisionRecords_DutyAssignmentId",
                table: "MorningRevisionRecords",
                column: "DutyAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_MorningRevisionRecords_SchoolClassId",
                table: "MorningRevisionRecords",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_MorningRevisionRecords_TeacherId_DutyAssignmentId",
                table: "MorningRevisionRecords",
                columns: new[] { "TeacherId", "DutyAssignmentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MorningRevisionRecords");

            migrationBuilder.Sql("""
                UPDATE [DutySections]
                SET [Name] = N'In-Class Morning',
                    [Summary] = CASE WHEN [Summary] = N'Classroom revision before the first lesson: which teacher revised which lesson with which class, and when they entered and left.'
                        THEN N'Staff supervising classes before the first lesson.' ELSE [Summary] END
                WHERE [Id] = 2 AND [Name] = N'Morning Revision';
                """);
        }
    }
}
