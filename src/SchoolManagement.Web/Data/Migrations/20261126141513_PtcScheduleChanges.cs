using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PtcScheduleChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PtcScheduleChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcEventId = table.Column<int>(type: "int", nullable: false),
                    PtcClassScheduleId = table.Column<int>(type: "int", nullable: false),
                    PtcRoundId = table.Column<int>(type: "int", nullable: false),
                    PtcParentBookingId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    OldSlot = table.Column<int>(type: "int", nullable: true),
                    NewSlot = table.Column<int>(type: "int", nullable: true),
                    OldStart = table.Column<TimeOnly>(type: "time", nullable: false),
                    OldEnd = table.Column<TimeOnly>(type: "time", nullable: false),
                    NewStart = table.Column<TimeOnly>(type: "time", nullable: false),
                    NewEnd = table.Column<TimeOnly>(type: "time", nullable: false),
                    Teachers = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcScheduleChanges", x => x.Id);
                    table.CheckConstraint("CK_PtcScheduleChanges_Kind", "[Kind] BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_PtcScheduleChanges_PtcClassSchedules_PtcClassScheduleId",
                        column: x => x.PtcClassScheduleId,
                        principalTable: "PtcClassSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcScheduleChanges_PtcEvents_PtcEventId",
                        column: x => x.PtcEventId,
                        principalTable: "PtcEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcScheduleChanges_PtcParentBookings_PtcParentBookingId",
                        column: x => x.PtcParentBookingId,
                        principalTable: "PtcParentBookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcScheduleChanges_PtcRounds_PtcRoundId",
                        column: x => x.PtcRoundId,
                        principalTable: "PtcRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PtcScheduleChanges_PtcClassScheduleId",
                table: "PtcScheduleChanges",
                column: "PtcClassScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcScheduleChanges_PtcEventId_ChangedAt",
                table: "PtcScheduleChanges",
                columns: new[] { "PtcEventId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PtcScheduleChanges_PtcParentBookingId",
                table: "PtcScheduleChanges",
                column: "PtcParentBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcScheduleChanges_PtcRoundId",
                table: "PtcScheduleChanges",
                column: "PtcRoundId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PtcScheduleChanges");
        }
    }
}
