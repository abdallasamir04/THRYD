using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffStructureAndManagementTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AppointedOn",
                table: "StaffMembers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "StaffMembers",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "StaffMembers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "StaffMembers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Departments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DepartmentHeadAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    TeacherId = table.Column<int>(type: "int", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AssignedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepartmentHeadAssignments", x => x.Id);
                    table.CheckConstraint("CK_DepartmentHeads_CurrentHasNoEnd", "[IsCurrent] = 0 OR [EndedAt] IS NULL");
                    table.ForeignKey(
                        name: "FK_DepartmentHeadAssignments_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DepartmentHeadAssignments_StaffMembers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManagementTaskHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    ManagementMemberId = table.Column<int>(type: "int", nullable: false),
                    TaskTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    StatusAfter = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementTaskHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManagementTaskHistory_StaffMembers_ManagementMemberId",
                        column: x => x.ManagementMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManagementTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ManagementMemberId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    Priority = table.Column<int>(type: "int", nullable: false, defaultValue: 2),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DueTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementTasks", x => x.Id);
                    table.CheckConstraint("CK_ManagementTasks_Priority", "[Priority] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_ManagementTasks_Status", "[Status] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_ManagementTasks_TimeNeedsDate", "[DueTime] IS NULL OR [DueDate] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_ManagementTasks_StaffMembers_ManagementMemberId",
                        column: x => x.ManagementMemberId,
                        principalTable: "StaffMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // The new Description column starts empty for the existing 11 departments; only ICT gains subcategories.
            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 10,
                column: "Subcategories",
                value: "ICT,AI");

            // The four new departments are added only where no department with the same name (or id) exists,
            // so a database that already has one of them never gets a duplicate.
            migrationBuilder.Sql("""
                SET IDENTITY_INSERT [Departments] ON;
                IF NOT EXISTS (SELECT 1 FROM [Departments] WHERE [Id] = 12 OR [NormalizedName] = N'READING CLUB')
                    INSERT INTO [Departments] ([Id], [Description], [Name], [NormalizedName], [SortOrder], [Subcategories])
                    VALUES (12, NULL, N'Reading Club', N'READING CLUB', 12, NULL);
                IF NOT EXISTS (SELECT 1 FROM [Departments] WHERE [Id] = 13 OR [NormalizedName] = N'SECOND LANGUAGE')
                    INSERT INTO [Departments] ([Id], [Description], [Name], [NormalizedName], [SortOrder], [Subcategories])
                    VALUES (13, NULL, N'Second Language', N'SECOND LANGUAGE', 13, N'French,Deutsch');
                IF NOT EXISTS (SELECT 1 FROM [Departments] WHERE [Id] = 14 OR [NormalizedName] = N'DRAMA')
                    INSERT INTO [Departments] ([Id], [Description], [Name], [NormalizedName], [SortOrder], [Subcategories])
                    VALUES (14, NULL, N'Drama', N'DRAMA', 14, NULL);
                IF NOT EXISTS (SELECT 1 FROM [Departments] WHERE [Id] = 15 OR [NormalizedName] = N'WSD')
                    INSERT INTO [Departments] ([Id], [Description], [Name], [NormalizedName], [SortOrder], [Subcategories])
                    VALUES (15, N'Wellbeing, Support & Development', N'WSD', N'WSD', 15, N'Wellbeing,Support,Discipline,Character Building');
                SET IDENTITY_INSERT [Departments] OFF;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_StaffMembers_ManagementPosition",
                table: "StaffMembers",
                column: "Position",
                unique: true,
                filter: "[Position] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffMembers_ManagementHasNoDepartment",
                table: "StaffMembers",
                sql: "[StaffType] <> 'Management' OR ([DepartmentId] IS NULL AND [Subcategory] IS NULL AND [Subject] IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_DepartmentHeadAssignments_DepartmentId_AssignedAt",
                table: "DepartmentHeadAssignments",
                columns: new[] { "DepartmentId", "AssignedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_DepartmentHead_CurrentPerDepartment",
                table: "DepartmentHeadAssignments",
                column: "DepartmentId",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_DepartmentHead_CurrentPerTeacher",
                table: "DepartmentHeadAssignments",
                column: "TeacherId",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ManagementTaskHistory_ManagementMemberId_ChangedAt",
                table: "ManagementTaskHistory",
                columns: new[] { "ManagementMemberId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagementTaskHistory_TaskId",
                table: "ManagementTaskHistory",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ManagementTasks_ManagementMemberId_Status",
                table: "ManagementTasks",
                columns: new[] { "ManagementMemberId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagementTasks_Status_DueDate",
                table: "ManagementTasks",
                columns: new[] { "Status", "DueDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepartmentHeadAssignments");

            migrationBuilder.DropTable(
                name: "ManagementTaskHistory");

            migrationBuilder.DropTable(
                name: "ManagementTasks");

            migrationBuilder.DropIndex(
                name: "UX_StaffMembers_ManagementPosition",
                table: "StaffMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StaffMembers_ManagementHasNoDepartment",
                table: "StaffMembers");

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 15);

            migrationBuilder.DropColumn(
                name: "AppointedOn",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Departments");

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 10,
                column: "Subcategories",
                value: null);
        }
    }
}
