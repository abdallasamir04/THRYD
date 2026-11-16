using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActivitySessionCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivitySessionCoverage",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TimetableAssignmentId = table.Column<int>(type: "int", nullable: true),
                    ActivityScheduleId = table.Column<int>(type: "int", nullable: true),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivitySessionCoverage", x => x.Id);
                    table.CheckConstraint("CK_ActivitySessionCoverage_Link", "([Kind] = 1 AND [TimetableAssignmentId] IS NOT NULL AND [ActivityScheduleId] IS NULL) OR ([Kind] = 2 AND [ActivityScheduleId] IS NOT NULL AND [TimetableAssignmentId] IS NULL)");
                    table.CheckConstraint("CK_ActivitySessionCoverage_Status", "[Status] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_ActivitySessionCoverage_ActivitySchedule_ActivityScheduleId",
                        column: x => x.ActivityScheduleId,
                        principalTable: "ActivitySchedule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActivitySessionCoverage_TimetableAssignments_TimetableAssignmentId",
                        column: x => x.TimetableAssignmentId,
                        principalTable: "TimetableAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySessionCoverage_SessionDate",
                table: "ActivitySessionCoverage",
                column: "SessionDate");

            migrationBuilder.CreateIndex(
                name: "UX_ActivitySessionCoverage_Lesson",
                table: "ActivitySessionCoverage",
                columns: new[] { "TimetableAssignmentId", "SessionDate" },
                unique: true,
                filter: "[TimetableAssignmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ActivitySessionCoverage_Meeting",
                table: "ActivitySessionCoverage",
                columns: new[] { "ActivityScheduleId", "SessionDate" },
                unique: true,
                filter: "[ActivityScheduleId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivitySessionCoverage");
        }
    }
}
