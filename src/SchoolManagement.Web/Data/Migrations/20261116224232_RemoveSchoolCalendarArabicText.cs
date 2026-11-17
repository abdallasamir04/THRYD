using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSchoolCalendarArabicText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TitleArabic",
                table: "SchoolCalendarEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TitleArabic",
                table: "SchoolCalendarEvents",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);
        }
    }
}
