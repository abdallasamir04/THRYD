using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FloorAuditAndStudentMovement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FloorAuditItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AppliesTo = table.Column<int>(type: "int", nullable: false),
                    ResponseType = table.Column<int>(type: "int", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorAuditItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FloorAuditLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Entity = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<int>(type: "int", nullable: true),
                    FloorId = table.Column<int>(type: "int", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ByStaffId = table.Column<int>(type: "int", nullable: true),
                    By = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorAuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchoolFloors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchoolFloors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FloorAreas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FacilityType = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorAreas", x => x.Id);
                    table.CheckConstraint("CK_FloorAreas_Kind", "([Kind] = 1 AND [SchoolClassId] IS NOT NULL AND [Name] IS NULL) OR ([Kind] = 2 AND [SchoolClassId] IS NULL AND [Name] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_FloorAreas_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorAreas_SchoolFloors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "SchoolFloors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FloorAuditAccess",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StaffMemberId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    FloorId = table.Column<int>(type: "int", nullable: true),
                    CanRecordMovements = table.Column<bool>(type: "bit", nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GrantedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorAuditAccess", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloorAuditAccess_SchoolFloors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "SchoolFloors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorAuditAccess_StaffMembers_StaffMemberId",
                        column: x => x.StaffMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FloorAreaInspections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FloorAreaId = table.Column<int>(type: "int", nullable: false),
                    FloorId = table.Column<int>(type: "int", nullable: false),
                    AreaName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    AreaKind = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: true),
                    AcademicYearId = table.Column<int>(type: "int", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TimeSlotId = table.Column<int>(type: "int", nullable: false),
                    SessionLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SessionStart = table.Column<TimeOnly>(type: "time", nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    ScheduledTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InspectorStaffId = table.Column<int>(type: "int", nullable: true),
                    InspectorName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CompletedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Observations = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IssueCount = table.Column<int>(type: "int", nullable: false),
                    IssueSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorAreaInspections", x => x.Id);
                    table.CheckConstraint("CK_FloorAreaInspections_Completed", "([Status] = 2 AND [CompletedAt] IS NOT NULL) OR ([Status] = 1 AND [CompletedAt] IS NULL)");
                    table.CheckConstraint("CK_FloorAreaInspections_Round", "[Round] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_FloorAreaInspections_AcademicYears_AcademicYearId",
                        column: x => x.AcademicYearId,
                        principalTable: "AcademicYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorAreaInspections_FloorAreas_FloorAreaId",
                        column: x => x.FloorAreaId,
                        principalTable: "FloorAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorAreaInspections_SchoolFloors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "SchoolFloors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorAreaInspections_StaffMembers_InspectorStaffId",
                        column: x => x.InspectorStaffId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FloorAreaInspections_TimeSlots_TimeSlotId",
                        column: x => x.TimeSlotId,
                        principalTable: "TimeSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudentMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    FromClassId = table.Column<int>(type: "int", nullable: false),
                    FromAreaId = table.Column<int>(type: "int", nullable: true),
                    FromFloorId = table.Column<int>(type: "int", nullable: true),
                    ToAreaId = table.Column<int>(type: "int", nullable: true),
                    ToFloorId = table.Column<int>(type: "int", nullable: true),
                    ToName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    IsCustomDestination = table.Column<bool>(type: "bit", nullable: false),
                    ExitAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReturnAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecordedByStaffId = table.Column<int>(type: "int", nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ReturnedByStaffId = table.Column<int>(type: "int", nullable: true),
                    ReturnedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    VoidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentMovements", x => x.Id);
                    table.CheckConstraint("CK_StudentMovements_ReturnAfterExit", "[ReturnAt] IS NULL OR [ReturnAt] > [ExitAt]");
                    table.ForeignKey(
                        name: "FK_StudentMovements_Classes_FromClassId",
                        column: x => x.FromClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentMovements_FloorAreas_FromAreaId",
                        column: x => x.FromAreaId,
                        principalTable: "FloorAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentMovements_FloorAreas_ToAreaId",
                        column: x => x.ToAreaId,
                        principalTable: "FloorAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentMovements_SchoolFloors_FromFloorId",
                        column: x => x.FromFloorId,
                        principalTable: "SchoolFloors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentMovements_SchoolFloors_ToFloorId",
                        column: x => x.ToFloorId,
                        principalTable: "SchoolFloors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentMovements_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FloorInspectionAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InspectionId = table.Column<int>(type: "int", nullable: false),
                    StoredName = table.Column<string>(type: "char(32)", unicode: false, fixedLength: true, maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Extension = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorInspectionAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloorInspectionAttachments_FloorAreaInspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "FloorAreaInspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FloorInspectionResponses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InspectionId = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<int>(type: "int", nullable: true),
                    ItemName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ItemDescription = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ResponseType = table.Column<int>(type: "int", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsIssue = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorInspectionResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloorInspectionResponses_FloorAreaInspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "FloorAreaInspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FloorInspectionResponses_FloorAuditItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "FloorAuditItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "FloorAuditItems",
                columns: new[] { "Id", "AppliesTo", "CreatedAt", "CreatedBy", "Description", "IsActive", "IsDefault", "IsRequired", "Name", "NormalizedName", "ResponseType", "SortOrder", "UpdatedAt", "UpdatedBy" },
                values: new object[,]
                {
                    { 1, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "Students in the classroom at the time of the inspection.", true, true, true, "Student Count", "STUDENT COUNT", 5, 10, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "A teacher or staff member is with the class.", true, true, true, "Teacher / Staff Presence", "TEACHER / STAFF PRESENCE", 4, 20, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 3, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, null, true, true, true, "Classroom Cleanliness", "CLASSROOM CLEANLINESS", 3, 30, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 4, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "No hazard to students (furniture, windows, cables, behaviour).", true, true, true, "Student Safety", "STUDENT SAFETY", 1, 40, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 5, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "Lights, air conditioning, doors, windows and furniture.", true, true, true, "Classroom Condition", "CLASSROOM CONDITION", 3, 50, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 6, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, null, true, true, true, "Classroom Organization", "CLASSROOM ORGANIZATION", 3, 60, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 7, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "Board, screen and the materials needed for the lesson.", true, true, true, "Equipment and Learning Materials", "EQUIPMENT AND LEARNING MATERIALS", 3, 70, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 8, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "Anything written here is recorded as an issue.", true, true, false, "Any Issues or Incidents", "ANY ISSUES OR INCIDENTS", 7, 80, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 9, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, null, true, true, true, "Cleanliness", "CLEANLINESS", 3, 90, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 10, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "No hazard (wet floor, broken fittings, blocked exits).", true, true, true, "Safety", "SAFETY", 1, 100, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 11, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "The facility is open and can be used.", true, true, true, "Accessibility", "ACCESSIBILITY", 2, 110, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 12, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, null, true, true, true, "Facility Condition", "FACILITY CONDITION", 3, 120, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 13, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, null, true, true, true, "Equipment Condition", "EQUIPMENT CONDITION", 3, 130, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 14, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, "Describe any maintenance needed; anything written here is recorded as an issue.", true, true, false, "Maintenance Issues", "MAINTENANCE ISSUES", 7, 140, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 15, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, null, true, true, false, "Any Other Observations", "ANY OTHER OBSERVATIONS", 6, 150, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });

            migrationBuilder.InsertData(
                table: "SchoolFloors",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "IsActive", "Name", "NormalizedName", "SortOrder", "UpdatedAt", "UpdatedBy" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Ground Floor", "GROUND FLOOR", 0, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "1st Floor", "1ST FLOOR", 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 3, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "2nd Floor", "2ND FLOOR", 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null },
                    { 4, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "3rd Floor", "3RD FLOOR", 3, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaInspections_AcademicYearId",
                table: "FloorAreaInspections",
                column: "AcademicYearId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaInspections_Date_TimeSlotId_Round",
                table: "FloorAreaInspections",
                columns: new[] { "Date", "TimeSlotId", "Round" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaInspections_FloorAreaId_Date_TimeSlotId_Round",
                table: "FloorAreaInspections",
                columns: new[] { "FloorAreaId", "Date", "TimeSlotId", "Round" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaInspections_FloorId_Date",
                table: "FloorAreaInspections",
                columns: new[] { "FloorId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaInspections_InspectorStaffId",
                table: "FloorAreaInspections",
                column: "InspectorStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreaInspections_TimeSlotId",
                table: "FloorAreaInspections",
                column: "TimeSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreas_FloorId_NormalizedName",
                table: "FloorAreas",
                columns: new[] { "FloorId", "NormalizedName" },
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FloorAreas_SchoolClassId",
                table: "FloorAreas",
                column: "SchoolClassId",
                unique: true,
                filter: "[SchoolClassId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FloorAuditAccess_FloorId",
                table: "FloorAuditAccess",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorAuditAccess_StaffMemberId_EndedAt",
                table: "FloorAuditAccess",
                columns: new[] { "StaffMemberId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorAuditItems_NormalizedName",
                table: "FloorAuditItems",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FloorAuditLog_At",
                table: "FloorAuditLog",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_FloorAuditLog_Entity_EntityId",
                table: "FloorAuditLog",
                columns: new[] { "Entity", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_FloorInspectionAttachments_InspectionId",
                table: "FloorInspectionAttachments",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_FloorInspectionAttachments_StoredName",
                table: "FloorInspectionAttachments",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FloorInspectionResponses_InspectionId_ItemId",
                table: "FloorInspectionResponses",
                columns: new[] { "InspectionId", "ItemId" },
                unique: true,
                filter: "[ItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FloorInspectionResponses_ItemId",
                table: "FloorInspectionResponses",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolFloors_NormalizedName",
                table: "SchoolFloors",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentMovements_Date_FromFloorId",
                table: "StudentMovements",
                columns: new[] { "Date", "FromFloorId" });

            migrationBuilder.CreateIndex(
                name: "IX_StudentMovements_Date_StudentId",
                table: "StudentMovements",
                columns: new[] { "Date", "StudentId" });

            migrationBuilder.CreateIndex(
                name: "IX_StudentMovements_Date_ToFloorId",
                table: "StudentMovements",
                columns: new[] { "Date", "ToFloorId" });

            migrationBuilder.CreateIndex(
                name: "IX_StudentMovements_FromAreaId",
                table: "StudentMovements",
                column: "FromAreaId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentMovements_FromClassId",
                table: "StudentMovements",
                column: "FromClassId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentMovements_FromFloorId",
                table: "StudentMovements",
                column: "FromFloorId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentMovements_ToAreaId",
                table: "StudentMovements",
                column: "ToAreaId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentMovements_ToFloorId",
                table: "StudentMovements",
                column: "ToFloorId");

            migrationBuilder.CreateIndex(
                name: "UX_StudentMovements_OneOpenPerStudent",
                table: "StudentMovements",
                column: "StudentId",
                unique: true,
                filter: "[ReturnAt] IS NULL AND [VoidedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FloorAuditAccess");

            migrationBuilder.DropTable(
                name: "FloorAuditLog");

            migrationBuilder.DropTable(
                name: "FloorInspectionAttachments");

            migrationBuilder.DropTable(
                name: "FloorInspectionResponses");

            migrationBuilder.DropTable(
                name: "StudentMovements");

            migrationBuilder.DropTable(
                name: "FloorAreaInspections");

            migrationBuilder.DropTable(
                name: "FloorAuditItems");

            migrationBuilder.DropTable(
                name: "FloorAreas");

            migrationBuilder.DropTable(
                name: "SchoolFloors");
        }
    }
}
