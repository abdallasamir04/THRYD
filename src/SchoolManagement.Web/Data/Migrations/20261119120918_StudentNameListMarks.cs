using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StudentNameListMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HighlightColor",
                table: "Students",
                type: "varchar(7)",
                unicode: false,
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameMarker",
                table: "Students",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Students_HighlightColor",
                table: "Students",
                sql: "[HighlightColor] IS NULL OR [HighlightColor] LIKE '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Students_HighlightColor",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "HighlightColor",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "NameMarker",
                table: "Students");
        }
    }
}
