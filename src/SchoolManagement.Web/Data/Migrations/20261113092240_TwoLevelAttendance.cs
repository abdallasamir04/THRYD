using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TwoLevelAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttendanceAlerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    FirstDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LastDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DayCount = table.Column<int>(type: "int", nullable: false),
                    AbsenceDates = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TimetableAssignmentId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceAlerts", x => x.Id);
                    table.CheckConstraint("CK_AttendanceAlerts_Dates", "[LastDate] >= [FirstDate]");
                    table.CheckConstraint("CK_AttendanceAlerts_SessionLink", "[Type] <> 2 OR [TimetableAssignmentId] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_AttendanceAlerts_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceAlerts_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AttendanceAlerts_TimetableAssignments_TimetableAssignmentId",
                        column: x => x.TimetableAssignmentId,
                        principalTable: "TimetableAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SchoolHolidays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchoolHolidays", x => x.Id);
                    table.CheckConstraint("CK_SchoolHolidays_Range", "[EndDate] >= [StartDate]");
                });

            migrationBuilder.CreateTable(
                name: "SessionAttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    TimetableAssignmentId = table.Column<int>(type: "int", nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionAttendanceRecords", x => x.Id);
                    table.CheckConstraint("CK_SessionAttendance_Status", "[Status] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_SessionAttendanceRecords_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SessionAttendanceRecords_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SessionAttendanceRecords_TimetableAssignments_TimetableAssignmentId",
                        column: x => x.TimetableAssignmentId,
                        principalTable: "TimetableAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SessionCancellations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TimetableAssignmentId = table.Column<int>(type: "int", nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CancelledBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionCancellations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionCancellations_TimetableAssignments_TimetableAssignmentId",
                        column: x => x.TimetableAssignmentId,
                        principalTable: "TimetableAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceAlertEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AttendanceAlertId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    By = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceAlertEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceAlertEvents_AttendanceAlerts_AttendanceAlertId",
                        column: x => x.AttendanceAlertId,
                        principalTable: "AttendanceAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionAttendanceCorrections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionAttendanceRecordId = table.Column<int>(type: "int", nullable: false),
                    Before = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    After = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrectedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionAttendanceCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionAttendanceCorrections_SessionAttendanceRecords_SessionAttendanceRecordId",
                        column: x => x.SessionAttendanceRecordId,
                        principalTable: "SessionAttendanceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceAlertEvents_AttendanceAlertId_At",
                table: "AttendanceAlertEvents",
                columns: new[] { "AttendanceAlertId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceAlerts_SchoolClassId",
                table: "AttendanceAlerts",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceAlerts_Status_Type_LastDate",
                table: "AttendanceAlerts",
                columns: new[] { "Status", "Type", "LastDate" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceAlerts_StudentId_Type",
                table: "AttendanceAlerts",
                columns: new[] { "StudentId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceAlerts_TimetableAssignmentId",
                table: "AttendanceAlerts",
                column: "TimetableAssignmentId");

            migrationBuilder.CreateIndex(
                name: "UX_AttendanceAlert_Key",
                table: "AttendanceAlerts",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SchoolHolidays_StartDate_EndDate",
                table: "SchoolHolidays",
                columns: new[] { "StartDate", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionAttendanceCorrections_SessionAttendanceRecordId_CorrectedAt",
                table: "SessionAttendanceCorrections",
                columns: new[] { "SessionAttendanceRecordId", "CorrectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionAttendanceRecords_SchoolClassId",
                table: "SessionAttendanceRecords",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionAttendanceRecords_SessionDate_SchoolClassId",
                table: "SessionAttendanceRecords",
                columns: new[] { "SessionDate", "SchoolClassId" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionAttendanceRecords_TimetableAssignmentId_SessionDate",
                table: "SessionAttendanceRecords",
                columns: new[] { "TimetableAssignmentId", "SessionDate" });

            migrationBuilder.CreateIndex(
                name: "UX_SessionAttendance_StudentLessonDay",
                table: "SessionAttendanceRecords",
                columns: new[] { "StudentId", "TimetableAssignmentId", "SessionDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionCancellations_SessionDate",
                table: "SessionCancellations",
                column: "SessionDate");

            migrationBuilder.CreateIndex(
                name: "UX_SessionCancellation_LessonDay",
                table: "SessionCancellations",
                columns: new[] { "TimetableAssignmentId", "SessionDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceAlertEvents");

            migrationBuilder.DropTable(
                name: "SchoolHolidays");

            migrationBuilder.DropTable(
                name: "SessionAttendanceCorrections");

            migrationBuilder.DropTable(
                name: "SessionCancellations");

            migrationBuilder.DropTable(
                name: "AttendanceAlerts");

            migrationBuilder.DropTable(
                name: "SessionAttendanceRecords");
        }
    }
}
