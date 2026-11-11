using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DutyAndWorkload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DutyAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Component = table.Column<int>(type: "int", nullable: false),
                    DutyDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    PrimaryStaffId = table.Column<int>(type: "int", nullable: false),
                    PrimaryAttendance = table.Column<int>(type: "int", nullable: false),
                    BackupStaffId = table.Column<int>(type: "int", nullable: true),
                    BackupStatus = table.Column<int>(type: "int", nullable: false),
                    BackupAttendance = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyAssignments", x => x.Id);
                    table.CheckConstraint("CK_DutyAssignments_BackupNotPrimary", "[BackupStaffId] IS NULL OR [BackupStaffId] <> [PrimaryStaffId]");
                    table.CheckConstraint("CK_DutyAssignments_Time", "[EndTime] > [StartTime]");
                    table.ForeignKey(
                        name: "FK_DutyAssignments_StaffMembers_BackupStaffId",
                        column: x => x.BackupStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DutyAssignments_StaffMembers_PrimaryStaffId",
                        column: x => x.PrimaryStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkloadAssignmentHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssignmentId = table.Column<int>(type: "int", nullable: false),
                    TeacherId = table.Column<int>(type: "int", nullable: false),
                    WeekStart = table.Column<DateOnly>(type: "date", nullable: false),
                    Change = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    PeriodsBefore = table.Column<int>(type: "int", nullable: true),
                    PeriodsAfter = table.Column<int>(type: "int", nullable: true),
                    WeekTotalAfter = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkloadAssignmentHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkloadAssignmentHistory_StaffMembers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DutyAssignmentClasses",
                columns: table => new
                {
                    DutyAssignmentId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyAssignmentClasses", x => new { x.DutyAssignmentId, x.SchoolClassId });
                    table.ForeignKey(
                        name: "FK_DutyAssignmentClasses_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DutyAssignmentClasses_DutyAssignments_DutyAssignmentId",
                        column: x => x.DutyAssignmentId,
                        principalTable: "DutyAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DutyBackupResponses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DutyAssignmentId = table.Column<int>(type: "int", nullable: false),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Response = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyBackupResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DutyBackupResponses_DutyAssignments_DutyAssignmentId",
                        column: x => x.DutyAssignmentId,
                        principalTable: "DutyAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DutyBackupResponses_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeacherWorkloadAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TeacherId = table.Column<int>(type: "int", nullable: false),
                    WeekStart = table.Column<DateOnly>(type: "date", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Periods = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: true),
                    DutyAssignmentId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherWorkloadAssignments", x => x.Id);
                    table.CheckConstraint("CK_TeacherWorkloadAssignments_Category", "[Category] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_TeacherWorkloadAssignments_ClassNeedsClass", "[Category] <> 1 OR [SchoolClassId] IS NOT NULL");
                    table.CheckConstraint("CK_TeacherWorkloadAssignments_DutyLink", "[DutyAssignmentId] IS NULL OR [Category] = 3");
                    table.CheckConstraint("CK_TeacherWorkloadAssignments_Periods", "[Periods] BETWEEN 1 AND 24");
                    table.ForeignKey(
                        name: "FK_TeacherWorkloadAssignments_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeacherWorkloadAssignments_DutyAssignments_DutyAssignmentId",
                        column: x => x.DutyAssignmentId,
                        principalTable: "DutyAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeacherWorkloadAssignments_StaffMembers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DutyAssignmentClasses_SchoolClassId",
                table: "DutyAssignmentClasses",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_DutyAssignments_BackupStaffId",
                table: "DutyAssignments",
                column: "BackupStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_DutyAssignments_DutyDate_Component",
                table: "DutyAssignments",
                columns: new[] { "DutyDate", "Component" });

            migrationBuilder.CreateIndex(
                name: "IX_DutyAssignments_PrimaryStaffId",
                table: "DutyAssignments",
                column: "PrimaryStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_DutyBackupResponses_DutyAssignmentId_RecordedAt",
                table: "DutyBackupResponses",
                columns: new[] { "DutyAssignmentId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DutyBackupResponses_StaffMemberId",
                table: "DutyBackupResponses",
                column: "StaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherWorkloadAssignments_DutyAssignmentId",
                table: "TeacherWorkloadAssignments",
                column: "DutyAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherWorkloadAssignments_SchoolClassId",
                table: "TeacherWorkloadAssignments",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherWorkloadAssignments_TeacherId_WeekStart",
                table: "TeacherWorkloadAssignments",
                columns: new[] { "TeacherId", "WeekStart" });

            migrationBuilder.CreateIndex(
                name: "IX_TeacherWorkloadAssignments_WeekStart_Category",
                table: "TeacherWorkloadAssignments",
                columns: new[] { "WeekStart", "Category" });

            migrationBuilder.CreateIndex(
                name: "UX_Workload_ClassPerWeek",
                table: "TeacherWorkloadAssignments",
                columns: new[] { "TeacherId", "WeekStart", "SchoolClassId" },
                unique: true,
                filter: "[Category] = 1 AND [SchoolClassId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Workload_DutyOnce",
                table: "TeacherWorkloadAssignments",
                columns: new[] { "TeacherId", "DutyAssignmentId" },
                unique: true,
                filter: "[DutyAssignmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkloadAssignmentHistory_TeacherId_ChangedAt",
                table: "WorkloadAssignmentHistory",
                columns: new[] { "TeacherId", "ChangedAt" });

            // Backstop for the 24-period weekly limit: rejects any insert or update that leaves a
            // teacher's week above 24, including changes made outside the application. UPDLOCK +
            // HOLDLOCK make the sum a locking read, so concurrent writers to the same week serialise.
            migrationBuilder.Sql($@"
CREATE TRIGGER [dbo].[{Data.SchoolDbContext.WorkloadLimitTrigger}]
ON [dbo].[TeacherWorkloadAssignments]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM (SELECT DISTINCT TeacherId, WeekStart FROM inserted) AS k
        CROSS APPLY (
            SELECT SUM(w.Periods) AS Total
            FROM [dbo].[TeacherWorkloadAssignments] AS w WITH (UPDLOCK, HOLDLOCK)
            WHERE w.TeacherId = k.TeacherId AND w.WeekStart = k.WeekStart
        ) AS s
        WHERE s.Total > {Entities.TeacherWorkloadAssignment.MaxWeeklyPeriods})
    BEGIN
        THROW 51000, 'A teacher cannot have more than {Entities.TeacherWorkloadAssignment.MaxWeeklyPeriods} periods in a week.', 1;
    END
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DROP TRIGGER IF EXISTS [dbo].[{Data.SchoolDbContext.WorkloadLimitTrigger}];");

            migrationBuilder.DropTable(
                name: "DutyAssignmentClasses");

            migrationBuilder.DropTable(
                name: "DutyBackupResponses");

            migrationBuilder.DropTable(
                name: "TeacherWorkloadAssignments");

            migrationBuilder.DropTable(
                name: "WorkloadAssignmentHistory");

            migrationBuilder.DropTable(
                name: "DutyAssignments");
        }
    }
}
