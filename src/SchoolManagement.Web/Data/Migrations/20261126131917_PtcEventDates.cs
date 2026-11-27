using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PtcEventDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "PtcEvents",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "PtcEvents",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddCheckConstraint(
                name: "CK_PtcEvents_Dates",
                table: "PtcEvents",
                sql: "[EndDate] >= [StartDate]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PtcEvents_Dates",
                table: "PtcEvents");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "PtcEvents");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "PtcEvents");
        }
    }
}
