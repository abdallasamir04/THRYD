using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PtcParentPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing booking takes its Round's class schedule: added empty, filled from the Round, then required.
            migrationBuilder.AddColumn<int>(
                name: "PtcClassScheduleId",
                table: "PtcParentBookings",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE b SET b.[PtcClassScheduleId] = r.[PtcClassScheduleId] FROM [PtcParentBookings] b JOIN [PtcRounds] r ON r.[Id] = b.[PtcRoundId];");

            migrationBuilder.AlterColumn<int>(
                name: "PtcClassScheduleId",
                table: "PtcParentBookings",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicToken",
                table: "PtcEvents",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SourceTimetableAssignmentId",
                table: "PtcBookingSubjects",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "SourcePtcClassTeacherId",
                table: "PtcBookingSubjects",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PtcClassTeachers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcClassScheduleId = table.Column<int>(type: "int", nullable: false),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    SubjectName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    RemovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemovedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcClassTeachers", x => x.Id);
                    table.CheckConstraint("CK_PtcClassTeachers_Removed", "([Status] = 2 AND [RemovedAt] IS NOT NULL) OR ([Status] <> 2 AND [RemovedAt] IS NULL)");
                    table.CheckConstraint("CK_PtcClassTeachers_Status", "[Status] BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_PtcClassTeachers_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcClassTeachers_PtcClassSchedules_PtcClassScheduleId",
                        column: x => x.PtcClassScheduleId,
                        principalTable: "PtcClassSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcClassTeachers_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_PtcParentBookings_ScheduleStudent",
                table: "PtcParentBookings",
                columns: new[] { "PtcClassScheduleId", "StudentId" },
                unique: true,
                filter: "[Status] <> 2");

            migrationBuilder.CreateIndex(
                name: "UX_PtcEvents_PublicToken",
                table: "PtcEvents",
                column: "PublicToken",
                unique: true,
                filter: "[PublicToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PtcBookingSubjects_SourcePtcClassTeacherId",
                table: "PtcBookingSubjects",
                column: "SourcePtcClassTeacherId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PtcBookingSubjects_Source",
                table: "PtcBookingSubjects",
                sql: "([SourceTimetableAssignmentId] IS NOT NULL AND [SourcePtcClassTeacherId] IS NULL) OR ([SourceTimetableAssignmentId] IS NULL AND [SourcePtcClassTeacherId] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_PtcClassTeachers_DepartmentId",
                table: "PtcClassTeachers",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcClassTeachers_StaffMemberId",
                table: "PtcClassTeachers",
                column: "StaffMemberId");

            migrationBuilder.CreateIndex(
                name: "UX_PtcClassTeachers_ScheduleSubject",
                table: "PtcClassTeachers",
                columns: new[] { "PtcClassScheduleId", "DepartmentId", "SubjectName" },
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_PtcBookingSubjects_PtcClassTeachers_SourcePtcClassTeacherId",
                table: "PtcBookingSubjects",
                column: "SourcePtcClassTeacherId",
                principalTable: "PtcClassTeachers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PtcParentBookings_PtcClassSchedules_PtcClassScheduleId",
                table: "PtcParentBookings",
                column: "PtcClassScheduleId",
                principalTable: "PtcClassSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PtcBookingSubjects_PtcClassTeachers_SourcePtcClassTeacherId",
                table: "PtcBookingSubjects");

            migrationBuilder.DropForeignKey(
                name: "FK_PtcParentBookings_PtcClassSchedules_PtcClassScheduleId",
                table: "PtcParentBookings");

            migrationBuilder.DropTable(
                name: "PtcClassTeachers");

            migrationBuilder.DropIndex(
                name: "UX_PtcParentBookings_ScheduleStudent",
                table: "PtcParentBookings");

            migrationBuilder.DropIndex(
                name: "UX_PtcEvents_PublicToken",
                table: "PtcEvents");

            migrationBuilder.DropIndex(
                name: "IX_PtcBookingSubjects_SourcePtcClassTeacherId",
                table: "PtcBookingSubjects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PtcBookingSubjects_Source",
                table: "PtcBookingSubjects");

            migrationBuilder.DropColumn(
                name: "PtcClassScheduleId",
                table: "PtcParentBookings");

            migrationBuilder.DropColumn(
                name: "PublicToken",
                table: "PtcEvents");

            migrationBuilder.DropColumn(
                name: "SourcePtcClassTeacherId",
                table: "PtcBookingSubjects");

            migrationBuilder.AlterColumn<int>(
                name: "SourceTimetableAssignmentId",
                table: "PtcBookingSubjects",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
