using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PeSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PeSessionDetails",
                columns: table => new
                {
                    TimetableAssignmentId = table.Column<int>(type: "int", nullable: false),
                    CoTeacherId = table.Column<int>(type: "int", nullable: true),
                    Facility = table.Column<int>(type: "int", nullable: true),
                    Activity = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeSessionDetails", x => x.TimetableAssignmentId);
                    table.CheckConstraint("CK_PeSessionDetails_Facility", "[Facility] IS NULL OR [Facility] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_PeSessionDetails_StaffMembers_CoTeacherId",
                        column: x => x.CoTeacherId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PeSessionDetails_TimetableAssignments_TimetableAssignmentId",
                        column: x => x.TimetableAssignmentId,
                        principalTable: "TimetableAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeEquipmentItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TimetableAssignmentId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsChecked = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeEquipmentItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeEquipmentItems_PeSessionDetails_TimetableAssignmentId",
                        column: x => x.TimetableAssignmentId,
                        principalTable: "PeSessionDetails",
                        principalColumn: "TimetableAssignmentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PeEquipmentItems_TimetableAssignmentId_SortOrder",
                table: "PeEquipmentItems",
                columns: new[] { "TimetableAssignmentId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PeSessionDetails_CoTeacherId",
                table: "PeSessionDetails",
                column: "CoTeacherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PeEquipmentItems");

            migrationBuilder.DropTable(
                name: "PeSessionDetails");
        }
    }
}
