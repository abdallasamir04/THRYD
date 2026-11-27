using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PtcFeedbackReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PtcFeedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Reference = table.Column<string>(type: "varchar(11)", unicode: false, maxLength: 11, nullable: false),
                    PtcEventId = table.Column<int>(type: "int", nullable: false),
                    PtcClassScheduleId = table.Column<int>(type: "int", nullable: false),
                    PtcParentBookingId = table.Column<int>(type: "int", nullable: false),
                    OverallRating = table.Column<int>(type: "int", nullable: true),
                    CommunicationRating = table.Column<int>(type: "int", nullable: true),
                    OrganizationRating = table.Column<int>(type: "int", nullable: true),
                    MeetingHelpful = table.Column<int>(type: "int", nullable: true),
                    Comments = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ConcernCategory = table.Column<int>(type: "int", nullable: true),
                    ConcernText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ConcernStatus = table.Column<int>(type: "int", nullable: true),
                    AssignedStaffMemberId = table.Column<int>(type: "int", nullable: true),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcFeedback", x => x.Id);
                    table.CheckConstraint("CK_PtcFeedback_Category", "[ConcernCategory] IS NULL OR [ConcernCategory] BETWEEN 1 AND 6");
                    table.CheckConstraint("CK_PtcFeedback_Communication", "[CommunicationRating] IS NULL OR [CommunicationRating] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_PtcFeedback_Concern", "([ConcernText] IS NULL AND [ConcernStatus] IS NULL) OR ([ConcernText] IS NOT NULL AND [ConcernStatus] IS NOT NULL)");
                    table.CheckConstraint("CK_PtcFeedback_ConcernStatus", "[ConcernStatus] IS NULL OR [ConcernStatus] BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_PtcFeedback_Helpful", "[MeetingHelpful] IS NULL OR [MeetingHelpful] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_PtcFeedback_Organization", "[OrganizationRating] IS NULL OR [OrganizationRating] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_PtcFeedback_Overall", "[OverallRating] IS NULL OR [OverallRating] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_PtcFeedback_Review", "[ReviewStatus] BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_PtcFeedback_PtcClassSchedules_PtcClassScheduleId",
                        column: x => x.PtcClassScheduleId,
                        principalTable: "PtcClassSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcFeedback_PtcEvents_PtcEventId",
                        column: x => x.PtcEventId,
                        principalTable: "PtcEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcFeedback_PtcParentBookings_PtcParentBookingId",
                        column: x => x.PtcParentBookingId,
                        principalTable: "PtcParentBookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcFeedback_StaffMembers_AssignedStaffMemberId",
                        column: x => x.AssignedStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtcFeedbackHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcFeedbackId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    By = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcFeedbackHistory", x => x.Id);
                    table.CheckConstraint("CK_PtcFeedbackHistory_Kind", "[Kind] BETWEEN 1 AND 7");
                    table.ForeignKey(
                        name: "FK_PtcFeedbackHistory_PtcFeedback_PtcFeedbackId",
                        column: x => x.PtcFeedbackId,
                        principalTable: "PtcFeedback",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtcTeacherFeedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcFeedbackId = table.Column<int>(type: "int", nullable: false),
                    TeacherStaffMemberId = table.Column<int>(type: "int", nullable: false),
                    CommunicationRating = table.Column<int>(type: "int", nullable: true),
                    MeetingHelpful = table.Column<int>(type: "int", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcTeacherFeedback", x => x.Id);
                    table.CheckConstraint("CK_PtcTeacherFeedback_Communication", "[CommunicationRating] IS NULL OR [CommunicationRating] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_PtcTeacherFeedback_Helpful", "[MeetingHelpful] IS NULL OR [MeetingHelpful] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_PtcTeacherFeedback_PtcFeedback_PtcFeedbackId",
                        column: x => x.PtcFeedbackId,
                        principalTable: "PtcFeedback",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcTeacherFeedback_StaffMembers_TeacherStaffMemberId",
                        column: x => x.TeacherStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PtcFeedback_AssignedStaffMemberId",
                table: "PtcFeedback",
                column: "AssignedStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcFeedback_ConcernStatus",
                table: "PtcFeedback",
                column: "ConcernStatus");

            migrationBuilder.CreateIndex(
                name: "IX_PtcFeedback_PtcClassScheduleId",
                table: "PtcFeedback",
                column: "PtcClassScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcFeedback_PtcEventId_SubmittedAt",
                table: "PtcFeedback",
                columns: new[] { "PtcEventId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PtcFeedback_Reference",
                table: "PtcFeedback",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PtcFeedback_Booking",
                table: "PtcFeedback",
                column: "PtcParentBookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PtcFeedbackHistory_PtcFeedbackId_At",
                table: "PtcFeedbackHistory",
                columns: new[] { "PtcFeedbackId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PtcTeacherFeedback_PtcFeedbackId_TeacherStaffMemberId",
                table: "PtcTeacherFeedback",
                columns: new[] { "PtcFeedbackId", "TeacherStaffMemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PtcTeacherFeedback_TeacherStaffMemberId",
                table: "PtcTeacherFeedback",
                column: "TeacherStaffMemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PtcFeedbackHistory");

            migrationBuilder.DropTable(
                name: "PtcTeacherFeedback");

            migrationBuilder.DropTable(
                name: "PtcFeedback");
        }
    }
}
