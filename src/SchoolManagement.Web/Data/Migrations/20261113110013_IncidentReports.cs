using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class IncidentReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReferenceId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    IncidentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReportingDepartment = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PreparedByStaffId = table.Column<int>(type: "int", nullable: true),
                    SignOffSchool = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ReportDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReviewedByStaffId = table.Column<int>(type: "int", nullable: true),
                    Approval = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    ApprovalNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsClosed = table.Column<bool>(type: "bit", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.CheckConstraint("CK_Incidents_Approval", "[Approval] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_Incidents_Severity", "[Severity] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_Incidents_Status", "[Status] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_Incidents_StaffMembers_PreparedByStaffId",
                        column: x => x.PreparedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Incidents_StaffMembers_ReviewedByStaffId",
                        column: x => x.ReviewedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IncidentId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ActionItem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Finding = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentActions", x => x.Id);
                    table.CheckConstraint("CK_IncidentActions_Status", "[Status] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_IncidentActions_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IncidentId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    By = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentHistory_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentStudents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IncidentId = table.Column<int>(type: "int", nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    RoleOther = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Involvement = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentStudents", x => x.Id);
                    table.CheckConstraint("CK_IncidentStudents_Role", "[Role] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_IncidentStudents_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IncidentStudents_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IncidentStudents_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentTimelineEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IncidentId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TimePeriod = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Time = table.Column<TimeOnly>(type: "time", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    OthersPresent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Account = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentTimelineEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentTimelineEntries_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentTimelineStudents",
                columns: table => new
                {
                    IncidentTimelineEntryId = table.Column<int>(type: "int", nullable: false),
                    IncidentStudentId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentTimelineStudents", x => new { x.IncidentTimelineEntryId, x.IncidentStudentId });
                    table.ForeignKey(
                        name: "FK_IncidentTimelineStudents_IncidentStudents_IncidentStudentId",
                        column: x => x.IncidentStudentId,
                        principalTable: "IncidentStudents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IncidentTimelineStudents_IncidentTimelineEntries_IncidentTimelineEntryId",
                        column: x => x.IncidentTimelineEntryId,
                        principalTable: "IncidentTimelineEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentActions_IncidentId_SortOrder",
                table: "IncidentActions",
                columns: new[] { "IncidentId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentHistory_IncidentId_At",
                table: "IncidentHistory",
                columns: new[] { "IncidentId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_IncidentDate",
                table: "Incidents",
                column: "IncidentDate");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_PreparedByStaffId",
                table: "Incidents",
                column: "PreparedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ReviewedByStaffId",
                table: "Incidents",
                column: "ReviewedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Status_Severity",
                table: "Incidents",
                columns: new[] { "Status", "Severity" });

            migrationBuilder.CreateIndex(
                name: "UX_Incident_Reference",
                table: "Incidents",
                column: "ReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentStudents_SchoolClassId",
                table: "IncidentStudents",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentStudents_StudentId",
                table: "IncidentStudents",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "UX_IncidentStudent_Student",
                table: "IncidentStudents",
                columns: new[] { "IncidentId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentTimelineEntries_IncidentId_SortOrder",
                table: "IncidentTimelineEntries",
                columns: new[] { "IncidentId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentTimelineStudents_IncidentStudentId",
                table: "IncidentTimelineStudents",
                column: "IncidentStudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidentActions");

            migrationBuilder.DropTable(
                name: "IncidentHistory");

            migrationBuilder.DropTable(
                name: "IncidentTimelineStudents");

            migrationBuilder.DropTable(
                name: "IncidentStudents");

            migrationBuilder.DropTable(
                name: "IncidentTimelineEntries");

            migrationBuilder.DropTable(
                name: "Incidents");
        }
    }
}
