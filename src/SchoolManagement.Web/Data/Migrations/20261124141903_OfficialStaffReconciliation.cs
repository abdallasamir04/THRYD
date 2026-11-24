using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class OfficialStaffReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffPositions_RoleType",
                table: "StaffPositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrgTiers_Section",
                table: "OrgTiers");

            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                table: "TeachingRosterReviewItems",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "TeachingRosterReviewItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveReason",
                table: "StaffMembers",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "StaffMembers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffPositions_RoleType",
                table: "StaffPositions",
                sql: "[RoleType] BETWEEN 1 AND 12");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrgTiers_Section",
                table: "OrgTiers",
                sql: "[Section] BETWEEN 1 AND 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffPositions_RoleType",
                table: "StaffPositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrgTiers_Section",
                table: "OrgTiers");

            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "TeachingRosterReviewItems");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "TeachingRosterReviewItems");

            migrationBuilder.DropColumn(
                name: "ArchiveReason",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "StaffMembers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffPositions_RoleType",
                table: "StaffPositions",
                sql: "[RoleType] BETWEEN 1 AND 10");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrgTiers_Section",
                table: "OrgTiers",
                sql: "[Section] BETWEEN 1 AND 2");
        }
    }
}
