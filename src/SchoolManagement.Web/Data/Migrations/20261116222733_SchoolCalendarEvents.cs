using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SchoolCalendarEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "CalendarEndDate",
                table: "AcademicQuarters",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CalendarStartDate",
                table: "AcademicQuarters",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SchoolCalendarEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AcademicYearId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TitleArabic = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DateLabel = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ListMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    Color = table.Column<int>(type: "int", nullable: false),
                    AccentText = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NeedsConfirmation = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsHidden = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchoolCalendarEvents", x => x.Id);
                    table.CheckConstraint("CK_SchoolCalendarEvents_Color", "[Color] BETWEEN 0 AND 9");
                    table.CheckConstraint("CK_SchoolCalendarEvents_Dates", "[EndDate] IS NULL OR ([StartDate] IS NOT NULL AND [EndDate] >= [StartDate])");
                    table.ForeignKey(
                        name: "FK_SchoolCalendarEvents_AcademicYears_AcademicYearId",
                        column: x => x.AcademicYearId,
                        principalTable: "AcademicYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchoolCalendarMarks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AcademicYearId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Color = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchoolCalendarMarks", x => x.Id);
                    table.CheckConstraint("CK_SchoolCalendarMarks_Color", "[Color] BETWEEN 1 AND 9");
                    table.ForeignKey(
                        name: "FK_SchoolCalendarMarks_AcademicYears_AcademicYearId",
                        column: x => x.AcademicYearId,
                        principalTable: "AcademicYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AcademicQuarters_CalendarDates",
                table: "AcademicQuarters",
                sql: "([CalendarStartDate] IS NULL AND [CalendarEndDate] IS NULL) OR ([CalendarStartDate] IS NOT NULL AND [CalendarEndDate] IS NOT NULL AND [CalendarEndDate] >= [CalendarStartDate])");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolCalendarEvents_AcademicYearId_ListMonth_SortOrder",
                table: "SchoolCalendarEvents",
                columns: new[] { "AcademicYearId", "ListMonth", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SchoolCalendarMarks_AcademicYearId_Date",
                table: "SchoolCalendarMarks",
                columns: new[] { "AcademicYearId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchoolCalendarEvents");

            migrationBuilder.DropTable(
                name: "SchoolCalendarMarks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AcademicQuarters_CalendarDates",
                table: "AcademicQuarters");

            migrationBuilder.DropColumn(
                name: "CalendarEndDate",
                table: "AcademicQuarters");

            migrationBuilder.DropColumn(
                name: "CalendarStartDate",
                table: "AcademicQuarters");
        }
    }
}
