using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffAttendanceAndEmployment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ContractEndDate",
                table: "StaffMembers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ContractStartDate",
                table: "StaffMembers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmploymentType",
                table: "StaffMembers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProbationLength",
                table: "StaffMembers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProbationUnit",
                table: "StaffMembers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "WorkEnd",
                table: "StaffMembers",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "WorkStart",
                table: "StaffMembers",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkingHoursType",
                table: "StaffMembers",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffAttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ArrivalTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffAttendanceRecords", x => x.Id);
                    table.CheckConstraint("CK_StaffAttendance_ArrivalTime", "([Status] = 3 AND [ArrivalTime] IS NULL) OR ([Status] IN (1, 2) AND [ArrivalTime] IS NOT NULL)");
                    table.CheckConstraint("CK_StaffAttendance_Status", "[Status] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_StaffAttendanceRecords_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffWorkingDays",
                columns: table => new
                {
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffWorkingDays", x => new { x.StaffMemberId, x.Day });
                    table.CheckConstraint("CK_StaffWorkingDays_Day", "[Day] BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "FK_StaffWorkingDays_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffAttendanceCorrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StaffAttendanceRecordId = table.Column<int>(type: "int", nullable: false),
                    Before = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    After = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CorrectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrectedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffAttendanceCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffAttendanceCorrections_StaffAttendanceRecords_StaffAttendanceRecordId",
                        column: x => x.StaffAttendanceRecordId,
                        principalTable: "StaffAttendanceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffMembers_ContractDates",
                table: "StaffMembers",
                sql: "[ContractEndDate] IS NULL OR [ContractStartDate] IS NULL OR [ContractEndDate] >= [ContractStartDate]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffMembers_Probation",
                table: "StaffMembers",
                sql: "[ProbationLength] IS NULL OR ([ProbationLength] BETWEEN 0 AND 104 AND [ProbationUnit] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffMembers_WorkHours",
                table: "StaffMembers",
                sql: "[WorkStart] IS NULL OR [WorkEnd] IS NULL OR [WorkEnd] > [WorkStart]");

            migrationBuilder.CreateIndex(
                name: "IX_StaffAttendanceCorrections_StaffAttendanceRecordId_CorrectedAt",
                table: "StaffAttendanceCorrections",
                columns: new[] { "StaffAttendanceRecordId", "CorrectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffAttendanceRecords_Date",
                table: "StaffAttendanceRecords",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "UX_StaffAttendance_StaffDay",
                table: "StaffAttendanceRecords",
                columns: new[] { "StaffMemberId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffAttendanceCorrections");

            migrationBuilder.DropTable(
                name: "StaffWorkingDays");

            migrationBuilder.DropTable(
                name: "StaffAttendanceRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffMembers_ContractDates",
                table: "StaffMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffMembers_Probation",
                table: "StaffMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffMembers_WorkHours",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "ContractEndDate",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "ContractStartDate",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "EmploymentType",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "ProbationLength",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "ProbationUnit",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "WorkEnd",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "WorkStart",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "WorkingHoursType",
                table: "StaffMembers");
        }
    }
}
