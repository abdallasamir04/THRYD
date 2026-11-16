using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class KgClassTeachingTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassTeachingAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    AcademicYearId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassTeachingAssignments", x => x.Id);
                    table.CheckConstraint("CK_ClassTeachingAssignments_Ended", "([IsActive] = 1 AND [EndedAt] IS NULL) OR ([IsActive] = 0 AND [EndedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_ClassTeachingAssignments_Role", "[Role] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_ClassTeachingAssignments_AcademicYears_AcademicYearId",
                        column: x => x.AcademicYearId,
                        principalTable: "AcademicYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassTeachingAssignments_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassTeachingAssignments_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClassTeachingHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    AcademicYearId = table.Column<int>(type: "int", nullable: false),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    PreviousRole = table.Column<int>(type: "int", nullable: true),
                    NewRole = table.Column<int>(type: "int", nullable: true),
                    Details = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    By = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassTeachingHistory", x => x.Id);
                    table.CheckConstraint("CK_ClassTeachingHistory_NewRole", "[NewRole] IS NULL OR [NewRole] IN (1, 2)");
                    table.CheckConstraint("CK_ClassTeachingHistory_PreviousRole", "[PreviousRole] IS NULL OR [PreviousRole] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_ClassTeachingHistory_AcademicYears_AcademicYearId",
                        column: x => x.AcademicYearId,
                        principalTable: "AcademicYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassTeachingHistory_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassTeachingHistory_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassTeachingAssignments_AcademicYearId",
                table: "ClassTeachingAssignments",
                column: "AcademicYearId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassTeachingAssignments_StaffMemberId_AcademicYearId",
                table: "ClassTeachingAssignments",
                columns: new[] { "StaffMemberId", "AcademicYearId" });

            migrationBuilder.CreateIndex(
                name: "UX_ClassTeaching_Homeroom",
                table: "ClassTeachingAssignments",
                columns: new[] { "SchoolClassId", "AcademicYearId" },
                unique: true,
                filter: "[IsActive] = 1 AND [Role] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_ClassTeaching_StaffOnce",
                table: "ClassTeachingAssignments",
                columns: new[] { "SchoolClassId", "AcademicYearId", "StaffMemberId" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ClassTeachingHistory_AcademicYearId",
                table: "ClassTeachingHistory",
                column: "AcademicYearId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassTeachingHistory_SchoolClassId_At",
                table: "ClassTeachingHistory",
                columns: new[] { "SchoolClassId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassTeachingHistory_StaffMemberId",
                table: "ClassTeachingHistory",
                column: "StaffMemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassTeachingAssignments");

            migrationBuilder.DropTable(
                name: "ClassTeachingHistory");
        }
    }
}
