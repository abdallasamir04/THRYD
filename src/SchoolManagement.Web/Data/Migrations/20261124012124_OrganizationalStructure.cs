using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationalStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrgTiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Section = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgTiers", x => x.Id);
                    table.CheckConstraint("CK_OrgTiers_Section", "[Section] BETWEEN 1 AND 2");
                });

            migrationBuilder.CreateTable(
                name: "StaffDataQualityFlags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsResolved = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDataQualityFlags", x => x.Id);
                    table.CheckConstraint("CK_StaffDataQualityFlags_Kind", "[Kind] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_StaffDataQualityFlags_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrgUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OrgTierId = table.Column<int>(type: "int", nullable: false),
                    ParentUnitId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgUnits_OrgTiers_OrgTierId",
                        column: x => x.OrgTierId,
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
                name: "OrgUnitDepartments",
                columns: table => new
                {
                    OrgUnitId = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    DepartmentSubcategoryId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnitDepartments", x => new { x.OrgUnitId, x.DepartmentId });
                    table.ForeignKey(
                        name: "FK_OrgUnitDepartments_DepartmentSubcategories_DepartmentSubcategoryId",
                        column: x => x.DepartmentSubcategoryId,
                        principalTable: "DepartmentSubcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrgUnitDepartments_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrgUnitDepartments_OrgUnits_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffPositions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OrgUnitId = table.Column<int>(type: "int", nullable: false),
                    RoleType = table.Column<int>(type: "int", nullable: false),
                    GradeRange = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPositions", x => x.Id);
                    table.CheckConstraint("CK_StaffPositions_RoleType", "[RoleType] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_StaffPositions_OrgUnits_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffPositionAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    StaffPositionId = table.Column<int>(type: "int", nullable: false),
                    GradeRange = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Scope = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReportsToStaffMemberId = table.Column<int>(type: "int", nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPositionAssignments", x => x.Id);
                    table.CheckConstraint("CK_StaffPositionAssignments_CurrentHasNoEnd", "[IsCurrent] = 0 OR [EndDate] IS NULL");
                    table.CheckConstraint("CK_StaffPositionAssignments_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_StaffPositionAssignments_NotOwnManager", "[ReportsToStaffMemberId] IS NULL OR [ReportsToStaffMemberId] <> [StaffMemberId]");
                    table.CheckConstraint("CK_StaffPositionAssignments_PrimaryIsCurrent", "[IsPrimary] = 0 OR [IsCurrent] = 1");
                    table.ForeignKey(
                        name: "FK_StaffPositionAssignments_StaffMembers_ReportsToStaffMemberId",
                        column: x => x.ReportsToStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StaffPositionAssignments_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffPositionAssignments_StaffPositions_StaffPositionId",
                        column: x => x.StaffPositionId,
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
                name: "IX_OrgTiers_SourceKey",
                table: "OrgTiers",
                column: "SourceKey",
                unique: true,
                filter: "[SourceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnitDepartments_DepartmentId",
                table: "OrgUnitDepartments",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnitDepartments_DepartmentSubcategoryId",
                table: "OrgUnitDepartments",
                column: "DepartmentSubcategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_OrgTierId_NormalizedName",
                table: "OrgUnits",
                columns: new[] { "OrgTierId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_ParentUnitId",
                table: "OrgUnits",
                column: "ParentUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_SourceKey",
                table: "OrgUnits",
                column: "SourceKey",
                unique: true,
                filter: "[SourceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDataQualityFlags_SourceKey",
                table: "StaffDataQualityFlags",
                column: "SourceKey",
                unique: true,
                filter: "[SourceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDataQualityFlags_StaffMemberId_IsResolved",
                table: "StaffDataQualityFlags",
                columns: new[] { "StaffMemberId", "IsResolved" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositionAssignments_ReportsToStaffMemberId",
                table: "StaffPositionAssignments",
                column: "ReportsToStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositionAssignments_SourceKey",
                table: "StaffPositionAssignments",
                column: "SourceKey",
                unique: true,
                filter: "[SourceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositionAssignments_StaffPositionId_IsCurrent",
                table: "StaffPositionAssignments",
                columns: new[] { "StaffPositionId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "UX_StaffPositionAssignments_CurrentPerPosition",
                table: "StaffPositionAssignments",
                columns: new[] { "StaffMemberId", "StaffPositionId" },
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_StaffPositionAssignments_OnePrimary",
                table: "StaffPositionAssignments",
                column: "StaffMemberId",
                unique: true,
                filter: "[IsCurrent] = 1 AND [IsPrimary] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_OrgUnitId_NormalizedTitle_GradeRange",
                table: "StaffPositions",
                columns: new[] { "OrgUnitId", "NormalizedTitle", "GradeRange" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_RoleType",
                table: "StaffPositions",
                column: "RoleType");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPositions_SourceKey",
                table: "StaffPositions",
                column: "SourceKey",
                unique: true,
                filter: "[SourceKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrgUnitDepartments");

            migrationBuilder.DropTable(
                name: "StaffDataQualityFlags");

            migrationBuilder.DropTable(
                name: "StaffPositionAssignments");

            migrationBuilder.DropTable(
                name: "StaffPositions");

            migrationBuilder.DropTable(
                name: "OrgUnits");

            migrationBuilder.DropTable(
                name: "OrgTiers");
        }
    }
}
