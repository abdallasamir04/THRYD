using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClassCapacityDefault20 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Capacity",
                table: "Classes",
                type: "int",
                nullable: false,
                defaultValue: 20,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 25);

            // Classes still at the former default (25) that were never edited since they were added take the new standard
            // of 20. A capacity someone set (any other value, or any class edited since it was created) is left as it is.
            // No student, enrolment or other class data changes.
            migrationBuilder.Sql("""
                UPDATE [Classes] SET [Capacity] = 20
                WHERE [Capacity] = 25 AND [UpdatedAt] = [CreatedAt];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Capacity",
                table: "Classes",
                type: "int",
                nullable: false,
                defaultValue: 25,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 20);

            // Capacities are not changed back: after this migration a 20 may also be a capacity someone chose.
        }
    }
}
