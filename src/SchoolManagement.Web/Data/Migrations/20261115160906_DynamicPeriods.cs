using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <summary>
    /// Dynamic periods. Additive only: TimeSlots gains a class scope (SchoolClassId, null = all classes), a class's
    /// own version of a school period (ReplacesTimeSlotId), weekdays (DayMask, 0 = every school day), archiving
    /// (ArchivedAt) and UpdatedBy. The fixed 1–10 numbering (unique Number, CK 1–10) becomes a display order.
    /// Existing periods keep their ids, names, times, order and status and become school-wide, every-day periods,
    /// exactly as they behaved before. No timetable, workload or attendance row is touched.
    /// </summary>
    public partial class DynamicPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TimeSlots_Number",
                table: "TimeSlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TimeSlots_Number",
                table: "TimeSlots");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "TimeSlots",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DayMask",
                table: "TimeSlots",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReplacesTimeSlotId",
                table: "TimeSlots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchoolClassId",
                table: "TimeSlots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "TimeSlots",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);











            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_ReplacesTimeSlotId",
                table: "TimeSlots",
                column: "ReplacesTimeSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_SchoolClassId_Number",
                table: "TimeSlots",
                columns: new[] { "SchoolClassId", "Number" });

            migrationBuilder.CreateIndex(
                name: "UX_TimeSlots_ClassReplacement",
                table: "TimeSlots",
                columns: new[] { "SchoolClassId", "ReplacesTimeSlotId" },
                unique: true,
                filter: "[ReplacesTimeSlotId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TimeSlots_DayMask",
                table: "TimeSlots",
                sql: "[DayMask] BETWEEN 0 AND 127");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TimeSlots_Number",
                table: "TimeSlots",
                sql: "[Number] BETWEEN 1 AND 999");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TimeSlots_ReplacesNeedsClass",
                table: "TimeSlots",
                sql: "[ReplacesTimeSlotId] IS NULL OR [SchoolClassId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_TimeSlots_Classes_SchoolClassId",
                table: "TimeSlots",
                column: "SchoolClassId",
                principalTable: "Classes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TimeSlots_TimeSlots_ReplacesTimeSlotId",
                table: "TimeSlots",
                column: "ReplacesTimeSlotId",
                principalTable: "TimeSlots",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [TimeSlots] WHERE [Id] > 10 OR [SchoolClassId] IS NOT NULL OR [ReplacesTimeSlotId] IS NOT NULL OR [DayMask] <> 0 OR [ArchivedAt] IS NOT NULL)
                    THROW 50000, 'DynamicPeriods cannot be rolled back: periods were added, scoped to classes, limited to days or archived. Remove them first.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_TimeSlots_Classes_SchoolClassId",
                table: "TimeSlots");

            migrationBuilder.DropForeignKey(
                name: "FK_TimeSlots_TimeSlots_ReplacesTimeSlotId",
                table: "TimeSlots");

            migrationBuilder.DropIndex(
                name: "IX_TimeSlots_ReplacesTimeSlotId",
                table: "TimeSlots");

            migrationBuilder.DropIndex(
                name: "IX_TimeSlots_SchoolClassId_Number",
                table: "TimeSlots");

            migrationBuilder.DropIndex(
                name: "UX_TimeSlots_ClassReplacement",
                table: "TimeSlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TimeSlots_DayMask",
                table: "TimeSlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TimeSlots_Number",
                table: "TimeSlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TimeSlots_ReplacesNeedsClass",
                table: "TimeSlots");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "TimeSlots");

            migrationBuilder.DropColumn(
                name: "DayMask",
                table: "TimeSlots");

            migrationBuilder.DropColumn(
                name: "ReplacesTimeSlotId",
                table: "TimeSlots");

            migrationBuilder.DropColumn(
                name: "SchoolClassId",
                table: "TimeSlots");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "TimeSlots");

            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_Number",
                table: "TimeSlots",
                column: "Number",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TimeSlots_Number",
                table: "TimeSlots",
                sql: "[Number] BETWEEN 1 AND 10");
        }
    }
}
