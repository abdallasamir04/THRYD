using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Classes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaffMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FullName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ContactNumber = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    NormalizedContact = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StaffType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Students",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FullName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Students", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Students_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Classes",
                columns: new[] { "Id", "CreatedAt", "IsActive", "Name", "NormalizedName", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "PreK", "PREK", 10, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "KG1A", "KG1A", 20, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 3, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "KG1B", "KG1B", 30, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 4, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "KG2A", "KG2A", 40, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 5, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "KG2B", "KG2B", 50, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 6, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G1A", "G1A", 60, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 7, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G1B", "G1B", 70, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 8, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G2A", "G2A", 80, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 9, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G2B", "G2B", 90, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 10, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G3A", "G3A", 100, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 11, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G3B", "G3B", 110, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 12, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G4A", "G4A", 120, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 13, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G4B", "G4B", 130, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 14, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G5A", "G5A", 140, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 15, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G5B", "G5B", 150, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 16, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G6A", "G6A", 160, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 17, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G6B", "G6B", 170, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 18, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G7A", "G7A", 180, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 19, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G7B", "G7B", 190, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 20, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G8A", "G8A", 200, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 21, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G8B", "G8B", 210, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 22, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G9A", "G9A", 220, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 23, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G9B", "G9B", 230, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 24, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G10A", "G10A", 240, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 25, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), true, "G10B", "G10B", 250, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Classes_NormalizedName",
                table: "Classes",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Classes_SortOrder",
                table: "Classes",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_CreatedAt",
                table: "StaffMembers",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_JobTitle",
                table: "StaffMembers",
                column: "JobTitle");

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_StaffType_NormalizedName_NormalizedContact",
                table: "StaffMembers",
                columns: new[] { "StaffType", "NormalizedName", "NormalizedContact" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Students_CreatedAt",
                table: "Students",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Students_NormalizedName",
                table: "Students",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_Students_SchoolClassId_NormalizedName",
                table: "Students",
                columns: new[] { "SchoolClassId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "StaffMembers");

            migrationBuilder.DropTable(
                name: "Students");

            migrationBuilder.DropTable(
                name: "Classes");
        }
    }
}
