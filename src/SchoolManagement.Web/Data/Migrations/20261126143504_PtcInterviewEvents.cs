using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PtcInterviewEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PtcInterviewEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcParentBookingId = table.Column<int>(type: "int", nullable: false),
                    TeacherStaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OldStatus = table.Column<int>(type: "int", nullable: true),
                    NewStatus = table.Column<int>(type: "int", nullable: false),
                    OldActualStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OldActualEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NewActualStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NewActualEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    By = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ByStaffId = table.Column<int>(type: "int", nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcInterviewEvents", x => x.Id);
                    table.CheckConstraint("CK_PtcInterviewEvents_Kind", "[Kind] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_PtcInterviewEvents_Reason", "[Kind] <> 3 OR [Reason] IS NOT NULL");
                    table.CheckConstraint("CK_PtcInterviewEvents_Times", "[NewActualStart] IS NULL OR [NewActualEnd] IS NULL OR [NewActualEnd] >= [NewActualStart]");
                    table.ForeignKey(
                        name: "FK_PtcInterviewEvents_PtcParentBookings_PtcParentBookingId",
                        column: x => x.PtcParentBookingId,
                        principalTable: "PtcParentBookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcInterviewEvents_StaffMembers_TeacherStaffMemberId",
                        column: x => x.TeacherStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PtcInterviewEvents_At",
                table: "PtcInterviewEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_PtcInterviewEvents_PtcParentBookingId_TeacherStaffMemberId_At",
                table: "PtcInterviewEvents",
                columns: new[] { "PtcParentBookingId", "TeacherStaffMemberId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PtcInterviewEvents_TeacherStaffMemberId",
                table: "PtcInterviewEvents",
                column: "TeacherStaffMemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PtcInterviewEvents");
        }
    }
}
