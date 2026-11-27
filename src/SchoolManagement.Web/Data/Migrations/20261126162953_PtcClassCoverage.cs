using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PtcClassCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "PtcSubstituteAssignments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimetableAssignmentId",
                table: "PtcSubstituteAssignments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PtcSubstituteChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcEventId = table.Column<int>(type: "int", nullable: false),
                    PtcClassScheduleId = table.Column<int>(type: "int", nullable: false),
                    PtcSubstituteAssignmentId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OriginalTeacherStaffMemberId = table.Column<int>(type: "int", nullable: false),
                    OldSubstituteStaffMemberId = table.Column<int>(type: "int", nullable: true),
                    NewSubstituteStaffMemberId = table.Column<int>(type: "int", nullable: true),
                    Lesson = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcSubstituteChanges", x => x.Id);
                    table.CheckConstraint("CK_PtcSubstituteChanges_Kind", "[Kind] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_PtcSubstituteChanges_PtcClassSchedules_PtcClassScheduleId",
                        column: x => x.PtcClassScheduleId,
                        principalTable: "PtcClassSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcSubstituteChanges_PtcEvents_PtcEventId",
                        column: x => x.PtcEventId,
                        principalTable: "PtcEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcSubstituteChanges_PtcSubstituteAssignments_PtcSubstituteAssignmentId",
                        column: x => x.PtcSubstituteAssignmentId,
                        principalTable: "PtcSubstituteAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcSubstituteChanges_StaffMembers_NewSubstituteStaffMemberId",
                        column: x => x.NewSubstituteStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcSubstituteChanges_StaffMembers_OldSubstituteStaffMemberId",
                        column: x => x.OldSubstituteStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcSubstituteChanges_StaffMembers_OriginalTeacherStaffMemberId",
                        column: x => x.OriginalTeacherStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteAssignments_TimetableAssignmentId",
                table: "PtcSubstituteAssignments",
                column: "TimetableAssignmentId");

            migrationBuilder.CreateIndex(
                name: "UX_PtcSubstitutes_ScheduleLesson",
                table: "PtcSubstituteAssignments",
                columns: new[] { "PtcClassScheduleId", "TimetableAssignmentId" },
                unique: true,
                filter: "[Status] = 1 AND [TimetableAssignmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteChanges_NewSubstituteStaffMemberId",
                table: "PtcSubstituteChanges",
                column: "NewSubstituteStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteChanges_OldSubstituteStaffMemberId",
                table: "PtcSubstituteChanges",
                column: "OldSubstituteStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteChanges_OriginalTeacherStaffMemberId",
                table: "PtcSubstituteChanges",
                column: "OriginalTeacherStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteChanges_PtcClassScheduleId",
                table: "PtcSubstituteChanges",
                column: "PtcClassScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteChanges_PtcEventId_ChangedAt",
                table: "PtcSubstituteChanges",
                columns: new[] { "PtcEventId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteChanges_PtcSubstituteAssignmentId",
                table: "PtcSubstituteChanges",
                column: "PtcSubstituteAssignmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_PtcSubstituteAssignments_TimetableAssignments_TimetableAssignmentId",
                table: "PtcSubstituteAssignments",
                column: "TimetableAssignmentId",
                principalTable: "TimetableAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PtcSubstituteAssignments_TimetableAssignments_TimetableAssignmentId",
                table: "PtcSubstituteAssignments");

            migrationBuilder.DropTable(
                name: "PtcSubstituteChanges");

            migrationBuilder.DropIndex(
                name: "IX_PtcSubstituteAssignments_TimetableAssignmentId",
                table: "PtcSubstituteAssignments");

            migrationBuilder.DropIndex(
                name: "UX_PtcSubstitutes_ScheduleLesson",
                table: "PtcSubstituteAssignments");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "PtcSubstituteAssignments");

            migrationBuilder.DropColumn(
                name: "TimetableAssignmentId",
                table: "PtcSubstituteAssignments");
        }
    }
}
