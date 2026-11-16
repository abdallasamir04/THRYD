using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <summary>
    /// Late cutoff (default 09:30): an arrival after it is Absent and keeps its real arrival time, so the "absent has no
    /// time" constraint becomes "absent is never late"; each record stores the cutoff it was evaluated with (CutoffUsed).
    /// Additive only: existing records are not changed (CutoffUsed stays NULL; they keep the outcome they were recorded with).
    /// </summary>
    public partial class ArrivalLateCutoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StudentArrivals_AbsentHasNoTime",
                table: "StudentArrivalRecords");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "CutoffUsed",
                table: "StudentArrivalRecords",
                type: "time",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StudentArrivals_AbsentNotLate",
                table: "StudentArrivalRecords",
                sql: "[Status] <> 2 OR [LateStatus] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [StudentArrivalRecords] WHERE [Status] = 2 AND [ArrivalTime] IS NOT NULL)
                    THROW 50000, 'ArrivalLateCutoff cannot be rolled back: absences after the late cutoff keep their arrival time.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_StudentArrivals_AbsentNotLate",
                table: "StudentArrivalRecords");

            migrationBuilder.DropColumn(
                name: "CutoffUsed",
                table: "StudentArrivalRecords");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StudentArrivals_AbsentHasNoTime",
                table: "StudentArrivalRecords",
                sql: "[Status] <> 2 OR ([ArrivalTime] IS NULL AND [LateStatus] IS NULL)");
        }
    }
}
