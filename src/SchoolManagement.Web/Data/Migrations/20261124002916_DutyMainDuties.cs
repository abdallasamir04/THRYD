using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DutyMainDuties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DutyMainDuties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyMainDuties", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "DutyMainDuties",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "Icon", "Name", "Slug", "SortOrder", "Summary" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, "shield-check", "Morning Duty", "morning", 1, "Students arrival check, morning revision in the classrooms and the morning assembly line-up." },
                    { 2, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, "sun", "Midday Duty", "midday", 2, "Break supervision by stage: Pre-K / KG1, KG2, Lower and Upper Elementary, Middle / High School." },
                    { 3, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, "door", "Dismissal Duty", "dismissal", 3, "End-of-day duties: students leaving by bus and pickup at the gates." }
                });

            migrationBuilder.CreateIndex(
                name: "IX_DutyMainDuties_Name",
                table: "DutyMainDuties",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DutyMainDuties_Slug",
                table: "DutyMainDuties",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DutyMainDuties");
        }
    }
}
