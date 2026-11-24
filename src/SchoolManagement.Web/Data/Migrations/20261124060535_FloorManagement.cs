using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FloorManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecondaryContactNumber",
                table: "StaffMembers",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GradeRange",
                table: "SchoolFloors",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FloorReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    RelatedStaffId = table.Column<int>(type: "int", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Recipient = table.Column<int>(type: "int", nullable: false),
                    RecipientStaffId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedByStaffId = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorReports", x => x.Id);
                    table.CheckConstraint("CK_FloorReports_Category", "[Category] BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_FloorReports_NotToRelated", "[RelatedStaffId] IS NULL OR [RecipientStaffId] IS NULL OR [RelatedStaffId] <> [RecipientStaffId]");
                    table.CheckConstraint("CK_FloorReports_Recipient", "[Recipient] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_FloorReports_Status", "[Status] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_FloorReports_SchoolFloors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "SchoolFloors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorReports_StaffMembers_CreatedByStaffId",
                        column: x => x.CreatedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FloorReports_StaffMembers_RecipientStaffId",
                        column: x => x.RecipientStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FloorReports_StaffMembers_RelatedStaffId",
                        column: x => x.RelatedStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "FloorStaffAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: true),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Coverage = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorStaffAssignments", x => x.Id);
                    table.CheckConstraint("CK_FloorStaff_CurrentHasNoEnd", "[IsCurrent] = 0 OR [EndDate] IS NULL");
                    table.CheckConstraint("CK_FloorStaff_Role", "[Role] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_FloorStaffAssignments_SchoolFloors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "SchoolFloors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorStaffAssignments_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FloorTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AssignedSocialWorkerId = table.Column<int>(type: "int", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedByStaffId = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorTickets", x => x.Id);
                    table.CheckConstraint("CK_FloorTickets_Category", "[Category] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_FloorTickets_Status", "[Status] BETWEEN 1 AND 6");
                    table.ForeignKey(
                        name: "FK_FloorTickets_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorTickets_SchoolFloors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "SchoolFloors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorTickets_StaffMembers_AssignedSocialWorkerId",
                        column: x => x.AssignedSocialWorkerId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FloorTickets_StaffMembers_CreatedByStaffId",
                        column: x => x.CreatedByStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FloorTickets_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FloorReportEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FloorReportId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    StatusAfter = table.Column<int>(type: "int", nullable: false),
                    RecipientAfter = table.Column<int>(type: "int", nullable: true),
                    RecipientStaffIdAfter = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ByStaffId = table.Column<int>(type: "int", nullable: true),
                    By = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorReportEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloorReportEvents_FloorReports_FloorReportId",
                        column: x => x.FloorReportId,
                        principalTable: "FloorReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FloorStaffClasses",
                columns: table => new
                {
                    FloorStaffAssignmentId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorStaffClasses", x => new { x.FloorStaffAssignmentId, x.SchoolClassId });
                    table.ForeignKey(
                        name: "FK_FloorStaffClasses_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorStaffClasses_FloorStaffAssignments_FloorStaffAssignmentId",
                        column: x => x.FloorStaffAssignmentId,
                        principalTable: "FloorStaffAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FloorTicketEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FloorTicketId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    StatusAfter = table.Column<int>(type: "int", nullable: false),
                    AssignedAfterId = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ByStaffId = table.Column<int>(type: "int", nullable: true),
                    By = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorTicketEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloorTicketEvents_FloorTickets_FloorTicketId",
                        column: x => x.FloorTicketId,
                        principalTable: "FloorTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "SchoolFloors",
                keyColumn: "Id",
                keyValue: 1,
                column: "GradeRange",
                value: null);

            migrationBuilder.UpdateData(
                table: "SchoolFloors",
                keyColumn: "Id",
                keyValue: 2,
                column: "GradeRange",
                value: null);

            migrationBuilder.UpdateData(
                table: "SchoolFloors",
                keyColumn: "Id",
                keyValue: 3,
                column: "GradeRange",
                value: null);

            migrationBuilder.UpdateData(
                table: "SchoolFloors",
                keyColumn: "Id",
                keyValue: 4,
                column: "GradeRange",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_FloorReportEvents_FloorReportId_At",
                table: "FloorReportEvents",
                columns: new[] { "FloorReportId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorReports_CreatedByStaffId",
                table: "FloorReports",
                column: "CreatedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorReports_FloorId_Status",
                table: "FloorReports",
                columns: new[] { "FloorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorReports_RecipientStaffId",
                table: "FloorReports",
                column: "RecipientStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorReports_RelatedStaffId",
                table: "FloorReports",
                column: "RelatedStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorStaffAssignments_SourceKey",
                table: "FloorStaffAssignments",
                column: "SourceKey",
                unique: true,
                filter: "[SourceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FloorStaffAssignments_StaffMemberId_IsCurrent",
                table: "FloorStaffAssignments",
                columns: new[] { "StaffMemberId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "UX_FloorStaff_CurrentRole",
                table: "FloorStaffAssignments",
                columns: new[] { "FloorId", "StaffMemberId", "Role" },
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_FloorStaff_OneLead",
                table: "FloorStaffAssignments",
                columns: new[] { "FloorId", "Role" },
                unique: true,
                filter: "[IsCurrent] = 1 AND [Role] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_FloorStaffClasses_SchoolClassId",
                table: "FloorStaffClasses",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorTicketEvents_FloorTicketId_At",
                table: "FloorTicketEvents",
                columns: new[] { "FloorTicketId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorTickets_AssignedSocialWorkerId",
                table: "FloorTickets",
                column: "AssignedSocialWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorTickets_CreatedByStaffId",
                table: "FloorTickets",
                column: "CreatedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorTickets_FloorId_Status",
                table: "FloorTickets",
                columns: new[] { "FloorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorTickets_Number",
                table: "FloorTickets",
                column: "Number");

            migrationBuilder.CreateIndex(
                name: "IX_FloorTickets_SchoolClassId",
                table: "FloorTickets",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorTickets_StudentId",
                table: "FloorTickets",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FloorReportEvents");

            migrationBuilder.DropTable(
                name: "FloorStaffClasses");

            migrationBuilder.DropTable(
                name: "FloorTicketEvents");

            migrationBuilder.DropTable(
                name: "FloorReports");

            migrationBuilder.DropTable(
                name: "FloorStaffAssignments");

            migrationBuilder.DropTable(
                name: "FloorTickets");

            migrationBuilder.DropColumn(
                name: "SecondaryContactNumber",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "GradeRange",
                table: "SchoolFloors");
        }
    }
}
