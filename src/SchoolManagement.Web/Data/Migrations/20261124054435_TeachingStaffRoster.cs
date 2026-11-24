using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TeachingStaffRoster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TeachingRosterEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AcademicYear = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    IsBlank = table.Column<bool>(type: "bit", nullable: false),
                    StaffName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    GradeLevels = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NumberOfClasses = table.Column<int>(type: "int", nullable: true),
                    ص = table.Column<int>(type: "int", nullable: true),
                    م = table.Column<int>(type: "int", nullable: true),
                    بريك = table.Column<int>(type: "int", nullable: true),
                    Role = table.Column<int>(type: "int", nullable: true),
                    StaffMemberId = table.Column<int>(type: "int", nullable: true),
                    MatchNote = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeachingRosterEntries", x => x.Id);
                    table.CheckConstraint("CK_TeachingRoster_Blank", "[IsBlank] = 0 OR ([StaffName] IS NULL AND [StaffMemberId] IS NULL)");
                    table.CheckConstraint("CK_TeachingRoster_Role", "[Role] IS NULL OR [Role] BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_TeachingRosterEntries_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeachingRosterDepartments",
                columns: table => new
                {
                    TeachingRosterEntryId = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    DepartmentSubcategoryId = table.Column<int>(type: "int", nullable: true),
                    SourcePart = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeachingRosterDepartments", x => new { x.TeachingRosterEntryId, x.DepartmentId });
                    table.ForeignKey(
                        name: "FK_TeachingRosterDepartments_DepartmentSubcategories_DepartmentSubcategoryId",
                        column: x => x.DepartmentSubcategoryId,
                        principalTable: "DepartmentSubcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeachingRosterDepartments_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeachingRosterDepartments_TeachingRosterEntries_TeachingRosterEntryId",
                        column: x => x.TeachingRosterEntryId,
                        principalTable: "TeachingRosterEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeachingRosterReviewItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    TeachingRosterEntryId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SourceValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ObservedValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsResolved = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeachingRosterReviewItems", x => x.Id);
                    table.CheckConstraint("CK_TeachingRosterReview_Kind", "[Kind] BETWEEN 1 AND 7");
                    table.ForeignKey(
                        name: "FK_TeachingRosterReviewItems_TeachingRosterEntries_TeachingRosterEntryId",
                        column: x => x.TeachingRosterEntryId,
                        principalTable: "TeachingRosterEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TeachingRosterDepartments_DepartmentId",
                table: "TeachingRosterDepartments",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TeachingRosterDepartments_DepartmentSubcategoryId",
                table: "TeachingRosterDepartments",
                column: "DepartmentSubcategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_TeachingRosterEntries_SourceKey",
                table: "TeachingRosterEntries",
                column: "SourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeachingRosterEntries_SourceName_RowNumber",
                table: "TeachingRosterEntries",
                columns: new[] { "SourceName", "RowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeachingRosterEntries_StaffMemberId",
                table: "TeachingRosterEntries",
                column: "StaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_TeachingRosterReviewItems_SourceKey",
                table: "TeachingRosterReviewItems",
                column: "SourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeachingRosterReviewItems_TeachingRosterEntryId_IsResolved",
                table: "TeachingRosterReviewItems",
                columns: new[] { "TeachingRosterEntryId", "IsResolved" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeachingRosterDepartments");

            migrationBuilder.DropTable(
                name: "TeachingRosterReviewItems");

            migrationBuilder.DropTable(
                name: "TeachingRosterEntries");
        }
    }
}
