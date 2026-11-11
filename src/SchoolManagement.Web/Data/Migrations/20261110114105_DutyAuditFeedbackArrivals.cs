using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DutyAuditFeedbackArrivals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuditNotes",
                table: "DutyAssignments",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "DutyAssignments",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DutyType",
                table: "DutyAssignments",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "FollowUpRequired",
                table: "DutyAssignments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IssuesEncountered",
                table: "DutyAssignments",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "DutyAssignments",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "DutyAssignments",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DutyAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DutyAssignmentId = table.Column<int>(type: "int", nullable: false),
                    DutyType = table.Column<int>(type: "int", nullable: false),
                    Component = table.Column<int>(type: "int", nullable: false),
                    DutyDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    StatusAfter = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DutyStaffAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DutyAssignmentId = table.Column<int>(type: "int", nullable: false),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Attendance = table.Column<int>(type: "int", nullable: false),
                    BackupStatus = table.Column<int>(type: "int", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyStaffAssignments", x => x.Id);
                    table.CheckConstraint("CK_DutyStaffAssignments_Role", "[Role] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_DutyStaffAssignments_DutyAssignments_DutyAssignmentId",
                        column: x => x.DutyAssignmentId,
                        principalTable: "DutyAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DutyStaffAssignments_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudentArrivalRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    ArrivalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ArrivalTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    Method = table.Column<int>(type: "int", nullable: true),
                    ArrivalLocation = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LateStatus = table.Column<int>(type: "int", nullable: true),
                    DeadlineUsed = table.Column<TimeOnly>(type: "time", nullable: true),
                    ReceivedByStaffId = table.Column<int>(type: "int", nullable: true),
                    ReceivingConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentArrivalRecords", x => x.Id);
                    table.CheckConstraint("CK_StudentArrivals_AbsentHasNoTime", "[Status] <> 2 OR ([ArrivalTime] IS NULL AND [LateStatus] IS NULL)");
                    table.CheckConstraint("CK_StudentArrivals_PresentHasTime", "[Status] <> 1 OR [ArrivalTime] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_StudentArrivalRecords_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentArrivalRecords_StaffMembers_ReceivedByStaffId",
                        column: x => x.ReceivedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentArrivalRecords_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffDutyFeedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DutyStaffAssignmentId = table.Column<int>(type: "int", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    Feedback = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Strengths = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Issues = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FollowUpRequired = table.Column<bool>(type: "bit", nullable: false),
                    EvaluatorStaffId = table.Column<int>(type: "int", nullable: false),
                    EvaluationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDutyFeedback", x => x.Id);
                    table.CheckConstraint("CK_StaffDutyFeedback_Rating", "[Rating] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_StaffDutyFeedback_DutyStaffAssignments_DutyStaffAssignmentId",
                        column: x => x.DutyStaffAssignmentId,
                        principalTable: "DutyStaffAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffDutyFeedback_StaffMembers_EvaluatorStaffId",
                        column: x => x.EvaluatorStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudentArrivalCorrections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StudentArrivalRecordId = table.Column<int>(type: "int", nullable: false),
                    Before = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    After = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CorrectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrectedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentArrivalCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentArrivalCorrections_StudentArrivalRecords_StudentArrivalRecordId",
                        column: x => x.StudentArrivalRecordId,
                        principalTable: "StudentArrivalRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DutyAssignments_DutyDate_Status",
                table: "DutyAssignments",
                columns: new[] { "DutyDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DutyAssignments_DutyType",
                table: "DutyAssignments",
                column: "DutyType");

            migrationBuilder.CreateIndex(
                name: "IX_DutyAudits_DutyAssignmentId_ChangedAt",
                table: "DutyAudits",
                columns: new[] { "DutyAssignmentId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DutyAudits_DutyDate",
                table: "DutyAudits",
                column: "DutyDate");

            migrationBuilder.CreateIndex(
                name: "IX_DutyStaffAssignments_StaffMemberId_AssignedAt",
                table: "DutyStaffAssignments",
                columns: new[] { "StaffMemberId", "AssignedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_DutyStaff_CurrentRole",
                table: "DutyStaffAssignments",
                columns: new[] { "DutyAssignmentId", "Role" },
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDutyFeedback_DutyStaffAssignmentId_CreatedAt",
                table: "StaffDutyFeedback",
                columns: new[] { "DutyStaffAssignmentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffDutyFeedback_EvaluatorStaffId",
                table: "StaffDutyFeedback",
                column: "EvaluatorStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentArrivalCorrections_StudentArrivalRecordId",
                table: "StudentArrivalCorrections",
                column: "StudentArrivalRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentArrivalRecords_ArrivalDate_SchoolClassId",
                table: "StudentArrivalRecords",
                columns: new[] { "ArrivalDate", "SchoolClassId" });

            migrationBuilder.CreateIndex(
                name: "IX_StudentArrivalRecords_ReceivedByStaffId",
                table: "StudentArrivalRecords",
                column: "ReceivedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentArrivalRecords_SchoolClassId",
                table: "StudentArrivalRecords",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "UX_StudentArrival_StudentDay",
                table: "StudentArrivalRecords",
                columns: new[] { "StudentId", "ArrivalDate" },
                unique: true);

            // Backfill: give every existing duty its current staff rows (primary, and backup if any),
            // so duty history and evaluations work for duties recorded before this migration.
            migrationBuilder.Sql(@"
INSERT INTO [DutyStaffAssignments] ([DutyAssignmentId], [StaffMemberId], [Role], [Attendance], [BackupStatus], [IsCurrent], [AssignedAt])
SELECT d.[Id], d.[PrimaryStaffId], 1, d.[PrimaryAttendance], 0, 1, d.[CreatedAt]
FROM [DutyAssignments] AS d;

INSERT INTO [DutyStaffAssignments] ([DutyAssignmentId], [StaffMemberId], [Role], [Attendance], [BackupStatus], [IsCurrent], [AssignedAt])
SELECT d.[Id], d.[BackupStaffId], 2, d.[BackupAttendance], d.[BackupStatus], 1, d.[UpdatedAt]
FROM [DutyAssignments] AS d
WHERE d.[BackupStaffId] IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DutyAudits");

            migrationBuilder.DropTable(
                name: "StaffDutyFeedback");

            migrationBuilder.DropTable(
                name: "StudentArrivalCorrections");

            migrationBuilder.DropTable(
                name: "DutyStaffAssignments");

            migrationBuilder.DropTable(
                name: "StudentArrivalRecords");

            migrationBuilder.DropIndex(
                name: "IX_DutyAssignments_DutyDate_Status",
                table: "DutyAssignments");

            migrationBuilder.DropIndex(
                name: "IX_DutyAssignments_DutyType",
                table: "DutyAssignments");

            migrationBuilder.DropColumn(
                name: "AuditNotes",
                table: "DutyAssignments");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "DutyAssignments");

            migrationBuilder.DropColumn(
                name: "DutyType",
                table: "DutyAssignments");

            migrationBuilder.DropColumn(
                name: "FollowUpRequired",
                table: "DutyAssignments");

            migrationBuilder.DropColumn(
                name: "IssuesEncountered",
                table: "DutyAssignments");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "DutyAssignments");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "DutyAssignments");
        }
    }
}
