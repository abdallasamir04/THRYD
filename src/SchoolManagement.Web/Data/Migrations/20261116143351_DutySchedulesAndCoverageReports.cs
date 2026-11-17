using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DutySchedulesAndCoverageReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DutyCoverageReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DutyDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Component = table.Column<int>(type: "int", nullable: false),
                    DutyAssignmentId = table.Column<int>(type: "int", nullable: true),
                    Area = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SupersedesId = table.Column<int>(type: "int", nullable: true),
                    Withdrawn = table.Column<bool>(type: "bit", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyCoverageReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DutyMainSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DutyType = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    DayMask = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyMainSchedules", x => x.Id);
                    table.CheckConstraint("CK_DutyMainSchedules_Times", "[EndTime] > [StartTime]");
                });

            migrationBuilder.CreateTable(
                name: "DutySections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    DutyType = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    HasClasses = table.Column<bool>(type: "bit", nullable: false),
                    DetailsPending = table.Column<bool>(type: "bit", nullable: false),
                    Grades = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    RetiredOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutySections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DutySectionSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DutySectionId = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    DayMask = table.Column<int>(type: "int", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutySectionSchedules", x => x.Id);
                    table.CheckConstraint("CK_DutySectionSchedules_Times", "[EndTime] > [StartTime]");
                    table.ForeignKey(
                        name: "FK_DutySectionSchedules_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DutySectionSchedules_DutySections_DutySectionId",
                        column: x => x.DutySectionId,
                        principalTable: "DutySections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "DutySections",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "DetailsPending", "DutyType", "Grades", "HasClasses", "Icon", "Name", "RetiredOn", "Slug", "SortOrder", "Summary" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 1, null, false, "users", "Students Arrival Check", null, "students-arrival", 1, "Staff on duty while students arrive at school." },
                    { 2, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 1, null, true, "door", "In-Class Morning", null, "in-class-morning", 2, "Staff supervising classes before the first lesson." },
                    { 3, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, true, 1, null, false, "activity", "Morning Assembly (Line) / Proadcast", null, "morning-assembly", 3, "Assembly line-up and proadcast duty." },
                    { 4, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 2, "Grades 7–10", false, "users", "Middle / High School Break", null, "middle-high-break", 5, "Break supervision for grades 7–10." },
                    { 5, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 2, "Grades 4–6", false, "users", "Upper Elementary", null, "upper-elementary", 4, "Break supervision for grades 4–6." },
                    { 6, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 2, "Grades 1–3", false, "users", "Lower Elementary", null, "lower-elementary", 3, "Break supervision for grades 1–3." },
                    { 7, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 2, "Grade KG2", false, "school", "KG2", null, "kg2", 2, "Break supervision for KG2." },
                    { 8, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 2, "Grades Pre-K and KG1", false, "school", "Pre-K / KG1", null, "prek-kg1", 1, "Break supervision for Pre-K and KG1." },
                    { 9, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 3, null, false, "door", "Bus", null, "bus", 1, "Staff seeing students onto the school buses. Gate and bus details go in location and notes." },
                    { 10, new DateTime(2026, 11, 16, 0, 0, 0, 0, DateTimeKind.Utc), null, false, 3, null, false, "user", "Pickup", null, "pickup", 2, "Staff handing students over to parents at pickup. Gate details go in location and notes." }
                });

            migrationBuilder.CreateIndex(
                name: "IX_DutyCoverageReports_DutyAssignmentId",
                table: "DutyCoverageReports",
                column: "DutyAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DutyCoverageReports_DutyDate_Component",
                table: "DutyCoverageReports",
                columns: new[] { "DutyDate", "Component" });

            migrationBuilder.CreateIndex(
                name: "IX_DutyCoverageReports_SupersedesId",
                table: "DutyCoverageReports",
                column: "SupersedesId");

            migrationBuilder.CreateIndex(
                name: "IX_DutyMainSchedules_DutyType_EffectiveFrom",
                table: "DutyMainSchedules",
                columns: new[] { "DutyType", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_DutySections_Slug",
                table: "DutySections",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DutySectionSchedules_DepartmentId",
                table: "DutySectionSchedules",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DutySectionSchedules_DutySectionId_EffectiveFrom",
                table: "DutySectionSchedules",
                columns: new[] { "DutySectionId", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DutyCoverageReports");

            migrationBuilder.DropTable(
                name: "DutyMainSchedules");

            migrationBuilder.DropTable(
                name: "DutySectionSchedules");

            migrationBuilder.DropTable(
                name: "DutySections");
        }
    }
}
