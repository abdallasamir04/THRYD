using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffTimetable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Day",
                table: "TeacherWorkloadAssignments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeSlotId",
                table: "TeacherWorkloadAssignments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubcategoryLabel",
                table: "Departments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectLabel",
                table: "Departments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DepartmentSubcategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepartmentSubcategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepartmentSubcategories_DepartmentSubcategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "DepartmentSubcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DepartmentSubcategories_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManagementPositionAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ManagementMemberId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementPositionAssignments", x => x.Id);
                    table.CheckConstraint("CK_PositionAssignments_Position", "[Position] BETWEEN 1 AND 7");
                    table.ForeignKey(
                        name: "FK_ManagementPositionAssignments_StaffMembers_ManagementMemberId",
                        column: x => x.ManagementMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TimeSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeSlots", x => x.Id);
                    table.CheckConstraint("CK_TimeSlots_Number", "[Number] BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_TimeSlots_Times", "([StartTime] IS NULL AND [EndTime] IS NULL) OR ([StartTime] IS NOT NULL AND [EndTime] IS NOT NULL AND [EndTime] > [StartTime])");
                });

            migrationBuilder.CreateTable(
                name: "TimetableAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    TimeSlotId = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TeacherId = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimetableAssignments", x => x.Id);
                    table.CheckConstraint("CK_Timetable_EndAfterStart", "[EndedOn] IS NULL OR [EndedOn] >= [StartsOn]");
                    table.CheckConstraint("CK_Timetable_SchoolDay", "[Day] BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_TimetableAssignments_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimetableAssignments_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimetableAssignments_StaffMembers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimetableAssignments_TimeSlots_TimeSlotId",
                        column: x => x.TimeSlotId,
                        principalTable: "TimeSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "DepartmentSubcategories",
                columns: new[] { "Id", "CreatedAt", "DepartmentId", "IsActive", "Name", "NormalizedName", "ParentId", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 7, true, "Islamic", "ISLAMIC", null, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 7, true, "Christian", "CHRISTIAN", null, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 6, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 13, true, "French", "FRENCH", null, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 7, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 13, true, "Deutsch", "DEUTSCH", null, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 8, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 10, true, "ICT", "ICT", null, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 9, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 10, true, "AI", "AI", null, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 10, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 15, true, "Wellbeing", "WELLBEING", null, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 11, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 15, true, "Support", "SUPPORT", null, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 12, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 15, true, "Discipline", "DISCIPLINE", null, 3, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 13, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 15, true, "Character Building", "CHARACTER BUILDING", null, 4, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            // Form labels for the departments that have subcategories (the new columns start empty elsewhere).
            migrationBuilder.Sql("""
                UPDATE [Departments] SET [SubcategoryLabel] = N'Religion type', [SubjectLabel] = N'Subject' WHERE [NormalizedName] = N'RELIGION';
                UPDATE [Departments] SET [SubcategoryLabel] = N'Specialisation' WHERE [NormalizedName] = N'ICT';
                UPDATE [Departments] SET [SubcategoryLabel] = N'Language' WHERE [NormalizedName] = N'SECOND LANGUAGE';
                UPDATE [Departments] SET [SubcategoryLabel] = N'Subdepartment' WHERE [NormalizedName] = N'WSD';
                """);

            // "Reading Club" becomes "Library / Reading" (same record, so its staff and HOD history stay).
            // If both names already exist, Reading Club's staff and HOD history move into Library / Reading
            // and the old name is removed, so the two never exist side by side.
            migrationBuilder.Sql("""
                DECLARE @rc int = (SELECT [Id] FROM [Departments] WHERE [NormalizedName] = N'READING CLUB');
                DECLARE @lib int = (SELECT [Id] FROM [Departments] WHERE [NormalizedName] = N'LIBRARY / READING');
                IF @rc IS NOT NULL AND @lib IS NULL
                    UPDATE [Departments] SET [Name] = N'Library / Reading', [NormalizedName] = N'LIBRARY / READING' WHERE [Id] = @rc;
                ELSE IF @rc IS NOT NULL AND @lib IS NOT NULL
                BEGIN
                    UPDATE [DepartmentHeadAssignments]
                        SET [IsCurrent] = 0, [EndedAt] = SYSUTCDATETIME(), [EndReason] = N'Reading Club merged into Library / Reading'
                        WHERE [DepartmentId] = @rc AND [IsCurrent] = 1;
                    UPDATE [DepartmentHeadAssignments] SET [DepartmentId] = @lib WHERE [DepartmentId] = @rc;
                    UPDATE [StaffMembers] SET [DepartmentId] = @lib WHERE [DepartmentId] = @rc;
                    DELETE FROM [Departments] WHERE [Id] = @rc;
                END
                """);

            migrationBuilder.InsertData(
                table: "TimeSlots",
                columns: new[] { "Id", "EndTime", "IsActive", "Name", "Number", "StartTime", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, null, true, null, 1, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, null, true, null, 2, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 3, null, true, null, 3, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 4, null, true, null, 4, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 5, null, true, null, 5, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 6, null, true, null, 6, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 7, null, true, null, 7, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 8, null, true, null, 8, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 9, null, true, null, 9, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 10, null, true, null, 10, null, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "DepartmentSubcategories",
                columns: new[] { "Id", "CreatedAt", "DepartmentId", "IsActive", "Name", "NormalizedName", "ParentId", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { 3, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 7, true, "Religion", "RELIGION", 1, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 4, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 7, true, "Quran", "QURAN", 1, 2, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 5, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc), 7, true, "Christian Religion", "CHRISTIAN RELIGION", 2, 1, new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_TeacherWorkloadAssignments_TimeSlotId",
                table: "TeacherWorkloadAssignments",
                column: "TimeSlotId");

            migrationBuilder.CreateIndex(
                name: "UX_Workload_TeacherSlotPerWeek",
                table: "TeacherWorkloadAssignments",
                columns: new[] { "TeacherId", "WeekStart", "Day", "TimeSlotId" },
                unique: true,
                filter: "[TimeSlotId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TeacherWorkloadAssignments_Slot",
                table: "TeacherWorkloadAssignments",
                sql: "([Day] IS NULL AND [TimeSlotId] IS NULL) OR ([Day] BETWEEN 0 AND 4 AND [TimeSlotId] IS NOT NULL AND [Periods] = 1)");

            migrationBuilder.CreateIndex(
                name: "IX_DepartmentSubcategories_ParentId",
                table: "DepartmentSubcategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "UX_DepartmentSubcategories_Name",
                table: "DepartmentSubcategories",
                columns: new[] { "DepartmentId", "ParentId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManagementPositionAssignments_Position_AssignedAt",
                table: "ManagementPositionAssignments",
                columns: new[] { "Position", "AssignedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_PositionAssignments_CurrentPerMember",
                table: "ManagementPositionAssignments",
                column: "ManagementMemberId",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_PositionAssignments_CurrentPerPosition",
                table: "ManagementPositionAssignments",
                column: "Position",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_Number",
                table: "TimeSlots",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimetableAssignments_DepartmentId",
                table: "TimetableAssignments",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TimetableAssignments_TeacherId_StartsOn_EndedOn",
                table: "TimetableAssignments",
                columns: new[] { "TeacherId", "StartsOn", "EndedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_TimetableAssignments_TimeSlotId",
                table: "TimetableAssignments",
                column: "TimeSlotId");

            migrationBuilder.CreateIndex(
                name: "UX_Timetable_ClassSlot",
                table: "TimetableAssignments",
                columns: new[] { "SchoolClassId", "Day", "TimeSlotId" },
                unique: true,
                filter: "[EndedOn] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Timetable_TeacherSlot",
                table: "TimetableAssignments",
                columns: new[] { "TeacherId", "Day", "TimeSlotId" },
                unique: true,
                filter: "[EndedOn] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_TeacherWorkloadAssignments_TimeSlots_TimeSlotId",
                table: "TeacherWorkloadAssignments",
                column: "TimeSlotId",
                principalTable: "TimeSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Position history starts with the positions members already hold.
            migrationBuilder.Sql("""
                INSERT INTO [ManagementPositionAssignments] ([ManagementMemberId], [Position], [IsCurrent], [StartDate], [AssignedAt])
                SELECT [Id], [Position], 1, COALESCE([AppointedOn], CAST([CreatedAt] AS date)), SYSUTCDATETIME()
                FROM [StaffMembers] WHERE [StaffType] = N'Management' AND [Position] IS NOT NULL;
                """);

            // The 24-period backstop now counts timetable sessions too: total for a week = workload rows of
            // that week + timetable sessions active in it (StartsOn <= week < EndedOn).
            migrationBuilder.Sql(WorkloadTrigger(withTimetable: true));
            migrationBuilder.Sql($@"
CREATE TRIGGER [dbo].[{SchoolDbContext.TimetableLimitTrigger}]
ON [dbo].[TimetableAssignments]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM (SELECT DISTINCT TeacherId, StartsOn FROM inserted WHERE EndedOn IS NULL) AS k
        CROSS APPLY (
            SELECT k.StartsOn AS WeekStart
            UNION SELECT w.WeekStart FROM [dbo].[TeacherWorkloadAssignments] AS w WHERE w.TeacherId = k.TeacherId AND w.WeekStart > k.StartsOn
            UNION SELECT t.StartsOn FROM [dbo].[TimetableAssignments] AS t WHERE t.TeacherId = k.TeacherId AND t.StartsOn > k.StartsOn
        ) AS wk
        CROSS APPLY ({WeekTotal("k.TeacherId", "wk.WeekStart")}) AS s
        WHERE s.Total > {Entities.TeacherWorkloadAssignment.MaxWeeklyPeriods})
    BEGIN
        THROW 51000, 'A teacher cannot have more than {Entities.TeacherWorkloadAssignment.MaxWeeklyPeriods} periods in a week.', 1;
    END
END");
        }

        private static string WeekTotal(string teacher, string week) => $@"
            SELECT ISNULL((SELECT SUM(w.Periods) FROM [dbo].[TeacherWorkloadAssignments] AS w WITH (UPDLOCK, HOLDLOCK)
                           WHERE w.TeacherId = {teacher} AND w.WeekStart = {week}), 0)
                 + (SELECT COUNT(*) FROM [dbo].[TimetableAssignments] AS t WITH (UPDLOCK, HOLDLOCK)
                    WHERE t.TeacherId = {teacher} AND t.StartsOn <= {week} AND (t.EndedOn IS NULL OR t.EndedOn > {week})) AS Total";

        /// <summary>The workload trigger; <paramref name="withTimetable"/> = false restores the original version.</summary>
        private static string WorkloadTrigger(bool withTimetable) => $@"
CREATE OR ALTER TRIGGER [dbo].[{SchoolDbContext.WorkloadLimitTrigger}]
ON [dbo].[TeacherWorkloadAssignments]
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM (SELECT DISTINCT TeacherId, WeekStart FROM inserted) AS k
        CROSS APPLY ({(withTimetable ? WeekTotal("k.TeacherId", "k.WeekStart") : @"
            SELECT SUM(w.Periods) AS Total
            FROM [dbo].[TeacherWorkloadAssignments] AS w WITH (UPDLOCK, HOLDLOCK)
            WHERE w.TeacherId = k.TeacherId AND w.WeekStart = k.WeekStart")}
        ) AS s
        WHERE s.Total > {Entities.TeacherWorkloadAssignment.MaxWeeklyPeriods})
    BEGIN
        THROW 51000, 'A teacher cannot have more than {Entities.TeacherWorkloadAssignment.MaxWeeklyPeriods} periods in a week.', 1;
    END
END";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(WorkloadTrigger(withTimetable: false));
            migrationBuilder.Sql($"DROP TRIGGER IF EXISTS [dbo].[{SchoolDbContext.TimetableLimitTrigger}];");

            migrationBuilder.DropForeignKey(
                name: "FK_TeacherWorkloadAssignments_TimeSlots_TimeSlotId",
                table: "TeacherWorkloadAssignments");

            migrationBuilder.DropTable(
                name: "DepartmentSubcategories");

            migrationBuilder.DropTable(
                name: "ManagementPositionAssignments");

            migrationBuilder.DropTable(
                name: "TimetableAssignments");

            migrationBuilder.DropTable(
                name: "TimeSlots");

            migrationBuilder.DropIndex(
                name: "IX_TeacherWorkloadAssignments_TimeSlotId",
                table: "TeacherWorkloadAssignments");

            migrationBuilder.DropIndex(
                name: "UX_Workload_TeacherSlotPerWeek",
                table: "TeacherWorkloadAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TeacherWorkloadAssignments_Slot",
                table: "TeacherWorkloadAssignments");

            migrationBuilder.DropColumn(
                name: "Day",
                table: "TeacherWorkloadAssignments");

            migrationBuilder.DropColumn(
                name: "TimeSlotId",
                table: "TeacherWorkloadAssignments");

            migrationBuilder.DropColumn(
                name: "SubcategoryLabel",
                table: "Departments");

            migrationBuilder.DropColumn(
                name: "SubjectLabel",
                table: "Departments");

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 12,
                columns: new[] { "Name", "NormalizedName" },
                values: new object[] { "Reading Club", "READING CLUB" });
        }
    }
}
