using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase1_OrganizationalStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "StaffMembers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "StaffMembers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrgTiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TierLevel = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgTiers", x => x.Id);
                    table.CheckConstraint("CK_OrgTiers_TierLevel", "[TierLevel] BETWEEN 1 AND 10");
                });

            migrationBuilder.CreateTable(
                name: "OrgUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    TierId = table.Column<int>(type: "int", nullable: true),
                    ParentUnitId = table.Column<int>(type: "int", nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    UnitType = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgUnits_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrgUnits_OrgTiers_TierId",
                        column: x => x.TierId,
                        principalTable: "OrgTiers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrgUnits_OrgUnits_ParentUnitId",
                        column: x => x.ParentUnitId,
                        principalTable: "OrgUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffPositions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TierId = table.Column<int>(type: "int", nullable: true),
                    OrgUnitId = table.Column<int>(type: "int", nullable: true),
                    ReportsToPositionId = table.Column<int>(type: "int", nullable: true),
                    RoleType = table.Column<int>(type: "int", nullable: false),
                    DefaultGradeRange = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffPositions_OrgTiers_TierId",
                        column: x => x.TierId,
                        principalTable: "OrgTiers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffPositions_OrgUnits_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StaffPositions_StaffPositions_ReportsToPositionId",
                        column: x => x.ReportsToPositionId,
                        principalTable: "StaffPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffPositionAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    PositionId = table.Column<int>(type: "int", nullable: false),
                    OrgUnitId = table.Column<int>(type: "int", nullable: true),
                    GradeRange = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RoleType = table.Column<int>(type: "int", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReportsToStaffMemberId = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPositionAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffPositionAssignments_OrgUnits_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StaffPositionAssignments_StaffMembers_ReportsToStaffMemberId",
                        column: x => x.ReportsToStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffPositionAssignments_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StaffPositionAssignments_StaffPositions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "StaffPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrgTiers_NormalizedName",
                table: "OrgTiers",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrgTiers_SortOrder",
                table: "OrgTiers",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_OrgTiers_TierLevel",
                table: "OrgTiers",
                column: "TierLevel");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_DepartmentId",
                table: "OrgUnits",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_ParentUnitId",
                table: "OrgUnits",
                column: "ParentUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_SortOrder",
                table: "OrgUnits",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_TierId_NormalizedName",
                table: "OrgUnits",
                columns: new[] { "TierId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositionAssignments_IsCurrent",
                table: "StaffPositionAssignments",
                column: "IsCurrent");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositionAssignments_OrgUnitId_IsCurrent",
                table: "StaffPositionAssignments",
                columns: new[] { "OrgUnitId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositionAssignments_PositionId",
                table: "StaffPositionAssignments",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositionAssignments_ReportsToStaffMemberId",
                table: "StaffPositionAssignments",
                column: "ReportsToStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositionAssignments_StaffMemberId_PositionId_IsCurrent",
                table: "StaffPositionAssignments",
                columns: new[] { "StaffMemberId", "PositionId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_NormalizedTitle",
                table: "StaffPositions",
                column: "NormalizedTitle");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_OrgUnitId",
                table: "StaffPositions",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_ReportsToPositionId",
                table: "StaffPositions",
                column: "ReportsToPositionId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_SortOrder",
                table: "StaffPositions",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_TierId",
                table: "StaffPositions",
                column: "TierId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffPositionAssignments");

            migrationBuilder.DropTable(
                name: "StaffPositions");

            migrationBuilder.DropTable(
                name: "OrgUnits");

            migrationBuilder.DropTable(
                name: "OrgTiers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "StaffMembers");
        }
    }
}
