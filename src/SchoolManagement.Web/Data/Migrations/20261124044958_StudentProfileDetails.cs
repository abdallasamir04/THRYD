using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StudentProfileDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AdmissionApplicationDate",
                table: "Students",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BirthDate",
                table: "Students",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DismissalMethod",
                table: "Students",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentContactNumber",
                table: "Students",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Students_ApplicationAfterBirth",
                table: "Students",
                sql: "[BirthDate] IS NULL OR [AdmissionApplicationDate] IS NULL OR [AdmissionApplicationDate] >= [BirthDate]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Students_DismissalMethod",
                table: "Students",
                sql: "[DismissalMethod] IS NULL OR [DismissalMethod] BETWEEN 1 AND 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Students_ApplicationAfterBirth",
                table: "Students");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Students_DismissalMethod",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "AdmissionApplicationDate",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "DismissalMethod",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "ParentContactNumber",
                table: "Students");
        }
    }
}
