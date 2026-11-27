using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PtcFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PtcEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AcademicYearId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcEvents", x => x.Id);
                    table.CheckConstraint("CK_PtcEvents_Status", "[Status] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_PtcEvents_AcademicYears_AcademicYearId",
                        column: x => x.AcademicYearId,
                        principalTable: "AcademicYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtcClassSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcEventId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    EventDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcClassSchedules", x => x.Id);
                    table.CheckConstraint("CK_PtcClassSchedules_EndAfterStart", "[EndTime] > [StartTime]");
                    table.CheckConstraint("CK_PtcClassSchedules_Status", "[Status] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_PtcClassSchedules_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcClassSchedules_PtcEvents_PtcEventId",
                        column: x => x.PtcEventId,
                        principalTable: "PtcEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtcRounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcClassScheduleId = table.Column<int>(type: "int", nullable: false),
                    RoundNumber = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: false),
                    MeetingDurationMinutes = table.Column<int>(type: "int", nullable: false),
                    TransitionDurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcRounds", x => x.Id);
                    table.CheckConstraint("CK_PtcRounds_Capacity", "[Capacity] >= 1");
                    table.CheckConstraint("CK_PtcRounds_Durations", "[MeetingDurationMinutes] >= 1 AND [TransitionDurationMinutes] >= 0");
                    table.CheckConstraint("CK_PtcRounds_EndAfterStart", "[EndTime] > [StartTime]");
                    table.CheckConstraint("CK_PtcRounds_Number", "[RoundNumber] >= 1");
                    table.CheckConstraint("CK_PtcRounds_Status", "[Status] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_PtcRounds_PtcClassSchedules_PtcClassScheduleId",
                        column: x => x.PtcClassScheduleId,
                        principalTable: "PtcClassSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtcSubstituteAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcClassScheduleId = table.Column<int>(type: "int", nullable: false),
                    OriginalTeacherStaffMemberId = table.Column<int>(type: "int", nullable: false),
                    SubstituteTeacherStaffMemberId = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcSubstituteAssignments", x => x.Id);
                    table.CheckConstraint("CK_PtcSubstitutes_Cancelled", "([Status] = 2 AND [CancelledAt] IS NOT NULL) OR ([Status] <> 2 AND [CancelledAt] IS NULL)");
                    table.CheckConstraint("CK_PtcSubstitutes_EndAfterStart", "[EndTime] > [StartTime]");
                    table.CheckConstraint("CK_PtcSubstitutes_NotSelf", "[OriginalTeacherStaffMemberId] <> [SubstituteTeacherStaffMemberId]");
                    table.CheckConstraint("CK_PtcSubstitutes_Status", "[Status] BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_PtcSubstituteAssignments_PtcClassSchedules_PtcClassScheduleId",
                        column: x => x.PtcClassScheduleId,
                        principalTable: "PtcClassSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcSubstituteAssignments_StaffMembers_OriginalTeacherStaffMemberId",
                        column: x => x.OriginalTeacherStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcSubstituteAssignments_StaffMembers_SubstituteTeacherStaffMemberId",
                        column: x => x.SubstituteTeacherStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtcParentBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcRoundId = table.Column<int>(type: "int", nullable: false),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    ParentContact = table.Column<string>(type: "varchar(15)", unicode: false, maxLength: 15, nullable: false),
                    BookingReference = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    SlotNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BookedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BookedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcParentBookings", x => x.Id);
                    table.CheckConstraint("CK_PtcParentBookings_Cancelled", "([Status] = 2 AND [CancelledAt] IS NOT NULL) OR ([Status] <> 2 AND [CancelledAt] IS NULL)");
                    table.CheckConstraint("CK_PtcParentBookings_Slot", "[SlotNumber] >= 1");
                    table.CheckConstraint("CK_PtcParentBookings_Status", "[Status] BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_PtcParentBookings_PtcRounds_PtcRoundId",
                        column: x => x.PtcRoundId,
                        principalTable: "PtcRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcParentBookings_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtcBookingSubjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcParentBookingId = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    SubjectName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AssignedTeacherStaffMemberId = table.Column<int>(type: "int", nullable: false),
                    SourceTimetableAssignmentId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcBookingSubjects", x => x.Id);
                    table.CheckConstraint("CK_PtcBookingSubjects_Status", "[Status] BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "FK_PtcBookingSubjects_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcBookingSubjects_PtcParentBookings_PtcParentBookingId",
                        column: x => x.PtcParentBookingId,
                        principalTable: "PtcParentBookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcBookingSubjects_StaffMembers_AssignedTeacherStaffMemberId",
                        column: x => x.AssignedTeacherStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcBookingSubjects_TimetableAssignments_SourceTimetableAssignmentId",
                        column: x => x.SourceTimetableAssignmentId,
                        principalTable: "TimetableAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PtcTeacherInterviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PtcParentBookingId = table.Column<int>(type: "int", nullable: false),
                    PtcBookingSubjectId = table.Column<int>(type: "int", nullable: false),
                    TeacherStaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ScheduledStart = table.Column<TimeOnly>(type: "time", nullable: false),
                    ScheduledEnd = table.Column<TimeOnly>(type: "time", nullable: false),
                    ActualStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PtcTeacherInterviews", x => x.Id);
                    table.CheckConstraint("CK_PtcTeacherInterviews_Actual", "[ActualStart] IS NULL OR [ActualEnd] IS NULL OR [ActualEnd] >= [ActualStart]");
                    table.CheckConstraint("CK_PtcTeacherInterviews_Scheduled", "[ScheduledEnd] > [ScheduledStart]");
                    table.CheckConstraint("CK_PtcTeacherInterviews_Status", "[Status] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_PtcTeacherInterviews_PtcBookingSubjects_PtcBookingSubjectId",
                        column: x => x.PtcBookingSubjectId,
                        principalTable: "PtcBookingSubjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcTeacherInterviews_PtcParentBookings_PtcParentBookingId",
                        column: x => x.PtcParentBookingId,
                        principalTable: "PtcParentBookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PtcTeacherInterviews_StaffMembers_TeacherStaffMemberId",
                        column: x => x.TeacherStaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PtcBookingSubjects_AssignedTeacherStaffMemberId",
                table: "PtcBookingSubjects",
                column: "AssignedTeacherStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcBookingSubjects_DepartmentId",
                table: "PtcBookingSubjects",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcBookingSubjects_SourceTimetableAssignmentId",
                table: "PtcBookingSubjects",
                column: "SourceTimetableAssignmentId");

            migrationBuilder.CreateIndex(
                name: "UX_PtcBookingSubjects_BookingSubject",
                table: "PtcBookingSubjects",
                columns: new[] { "PtcParentBookingId", "DepartmentId", "SubjectName" },
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PtcClassSchedules_EventDate",
                table: "PtcClassSchedules",
                column: "EventDate");

            migrationBuilder.CreateIndex(
                name: "IX_PtcClassSchedules_SchoolClassId_EventDate",
                table: "PtcClassSchedules",
                columns: new[] { "SchoolClassId", "EventDate" });

            migrationBuilder.CreateIndex(
                name: "UX_PtcClassSchedules_EventClass",
                table: "PtcClassSchedules",
                columns: new[] { "PtcEventId", "SchoolClassId" },
                unique: true,
                filter: "[Status] <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_PtcEvents_AcademicYearId_Status",
                table: "PtcEvents",
                columns: new[] { "AcademicYearId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PtcParentBookings_BookingReference",
                table: "PtcParentBookings",
                column: "BookingReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PtcParentBookings_ParentContact",
                table: "PtcParentBookings",
                column: "ParentContact");

            migrationBuilder.CreateIndex(
                name: "IX_PtcParentBookings_StudentId",
                table: "PtcParentBookings",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "UX_PtcParentBookings_RoundSlot",
                table: "PtcParentBookings",
                columns: new[] { "PtcRoundId", "SlotNumber" },
                unique: true,
                filter: "[Status] <> 2");

            migrationBuilder.CreateIndex(
                name: "UX_PtcParentBookings_RoundStudent",
                table: "PtcParentBookings",
                columns: new[] { "PtcRoundId", "StudentId" },
                unique: true,
                filter: "[Status] <> 2");

            migrationBuilder.CreateIndex(
                name: "UX_PtcRounds_ScheduleNumber",
                table: "PtcRounds",
                columns: new[] { "PtcClassScheduleId", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteAssignments_OriginalTeacherStaffMemberId",
                table: "PtcSubstituteAssignments",
                column: "OriginalTeacherStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteAssignments_PtcClassScheduleId_Status",
                table: "PtcSubstituteAssignments",
                columns: new[] { "PtcClassScheduleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PtcSubstituteAssignments_SubstituteTeacherStaffMemberId",
                table: "PtcSubstituteAssignments",
                column: "SubstituteTeacherStaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcTeacherInterviews_PtcParentBookingId",
                table: "PtcTeacherInterviews",
                column: "PtcParentBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_PtcTeacherInterviews_TeacherStaffMemberId_Date_ScheduledStart",
                table: "PtcTeacherInterviews",
                columns: new[] { "TeacherStaffMemberId", "Date", "ScheduledStart" });

            migrationBuilder.CreateIndex(
                name: "UX_PtcTeacherInterviews_Subject",
                table: "PtcTeacherInterviews",
                column: "PtcBookingSubjectId",
                unique: true,
                filter: "[Status] <> 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PtcSubstituteAssignments");

            migrationBuilder.DropTable(
                name: "PtcTeacherInterviews");

            migrationBuilder.DropTable(
                name: "PtcBookingSubjects");

            migrationBuilder.DropTable(
                name: "PtcParentBookings");

            migrationBuilder.DropTable(
                name: "PtcRounds");

            migrationBuilder.DropTable(
                name: "PtcClassSchedules");

            migrationBuilder.DropTable(
                name: "PtcEvents");
        }
    }
}
