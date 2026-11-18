using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProbationDatesAndCustomPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustomPositionId",
                table: "StaffMembers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ProbationEndDate",
                table: "StaffMembers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ProbationStartDate",
                table: "StaffMembers",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ManagementCustomPositions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementCustomPositions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_StaffMembers_CustomPosition",
                table: "StaffMembers",
                column: "CustomPositionId",
                unique: true,
                filter: "[CustomPositionId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffMembers_OnePosition",
                table: "StaffMembers",
                sql: "[Position] IS NULL OR [CustomPositionId] IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffMembers_ProbationDates",
                table: "StaffMembers",
                sql: "[ProbationEndDate] IS NULL OR ([ProbationStartDate] IS NOT NULL AND [ProbationEndDate] >= [ProbationStartDate])");

            migrationBuilder.CreateIndex(
                name: "UX_ManagementCustomPositions_Title",
                table: "ManagementCustomPositions",
                column: "NormalizedTitle",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_StaffMembers_ManagementCustomPositions_CustomPositionId",
                table: "StaffMembers",
                column: "CustomPositionId",
                principalTable: "ManagementCustomPositions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StaffMembers_ManagementCustomPositions_CustomPositionId",
                table: "StaffMembers");

            migrationBuilder.DropTable(
                name: "ManagementCustomPositions");

            migrationBuilder.DropIndex(
                name: "UX_StaffMembers_CustomPosition",
                table: "StaffMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffMembers_OnePosition",
                table: "StaffMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffMembers_ProbationDates",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "CustomPositionId",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "ProbationEndDate",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "ProbationStartDate",
                table: "StaffMembers");
        }
    }
}
