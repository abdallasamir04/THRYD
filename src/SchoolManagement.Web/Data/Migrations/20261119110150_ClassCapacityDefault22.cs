using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClassCapacityDefault22 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The standard class size becomes 22: the column default for new classes.
            migrationBuilder.AlterColumn<int>(
                name: "Capacity",
                table: "Classes",
                type: "int",
                nullable: false,
                defaultValue: 22,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 20);

            // Classes still at the former standard of exactly 20 take the new standard of 22. Any other capacity (25, 30 …)
            // was set for that class and is left as it is. The seeded classes are not forced to 22 (no UpdateData): a seeded
            // class with its own capacity keeps it. Only the Capacity column changes (UpdatedAt is left as it is); no student,
            // enrolment or other data changes.
            migrationBuilder.Sql("""
                UPDATE [Classes] SET [Capacity] = 22
                WHERE [Capacity] = 20;
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
                defaultValue: 20,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 22);

            // Capacities are not changed back: after this migration a 22 may also be a capacity someone chose.
        }
    }
}
