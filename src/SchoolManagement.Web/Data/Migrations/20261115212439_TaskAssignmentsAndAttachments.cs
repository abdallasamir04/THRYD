using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskAssignmentsAndAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ManagementTaskHistory_StaffMembers_ManagementMemberId",
                table: "ManagementTaskHistory");

            migrationBuilder.DropForeignKey(
                name: "FK_ManagementTasks_StaffMembers_ManagementMemberId",
                table: "ManagementTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagementTasks_Status",
                table: "ManagementTasks");

            migrationBuilder.RenameColumn(
                name: "ManagementMemberId",
                table: "ManagementTasks",
                newName: "AssignedToId");

            migrationBuilder.RenameIndex(
                name: "IX_ManagementTasks_ManagementMemberId_Status",
                table: "ManagementTasks",
                newName: "IX_ManagementTasks_AssignedToId_Status");

            migrationBuilder.RenameColumn(
                name: "ManagementMemberId",
                table: "ManagementTaskHistory",
                newName: "StaffMemberId");

            migrationBuilder.RenameIndex(
                name: "IX_ManagementTaskHistory_ManagementMemberId_ChangedAt",
                table: "ManagementTaskHistory",
                newName: "IX_ManagementTaskHistory_StaffMemberId_ChangedAt");

            migrationBuilder.AddColumn<int>(
                name: "CreatedById",
                table: "ManagementTasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewerId",
                table: "ManagementTasks",
                type: "int",
                nullable: true);

            // Existing tasks recorded their creator by name only. Link each to that staff record where exactly one
            // staff member has the name; any other task keeps just the name (nothing is guessed).
            migrationBuilder.Sql("""
                UPDATE t SET CreatedById = s.Id
                FROM ManagementTasks t
                CROSS APPLY (SELECT MIN(m.Id) AS Id, COUNT(*) AS N FROM StaffMembers m WHERE m.FullName = t.CreatedBy) s
                WHERE t.CreatedById IS NULL AND t.CreatedBy IS NOT NULL AND s.N = 1;
                """);

            migrationBuilder.CreateTable(
                name: "TaskAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    StagingKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoredName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAttachments", x => x.Id);
                    table.CheckConstraint("CK_TaskAttachments_Purpose", "[Purpose] = 1");
                    table.ForeignKey(
                        name: "FK_TaskAttachments_ManagementTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ManagementTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManagementTasks_CreatedById",
                table: "ManagementTasks",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ManagementTasks_ReviewerId",
                table: "ManagementTasks",
                column: "ReviewerId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagementTasks_Status",
                table: "ManagementTasks",
                sql: "[Status] BETWEEN 1 AND 5");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_StagingKey",
                table: "TaskAttachments",
                column: "StagingKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_StoredName",
                table: "TaskAttachments",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_TaskId_UploadedAt",
                table: "TaskAttachments",
                columns: new[] { "TaskId", "UploadedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_ManagementTaskHistory_StaffMembers_StaffMemberId",
                table: "ManagementTaskHistory",
                column: "StaffMemberId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ManagementTasks_StaffMembers_AssignedToId",
                table: "ManagementTasks",
                column: "AssignedToId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ManagementTasks_StaffMembers_CreatedById",
                table: "ManagementTasks",
                column: "CreatedById",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ManagementTasks_StaffMembers_ReviewerId",
                table: "ManagementTasks",
                column: "ReviewerId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ManagementTaskHistory_StaffMembers_StaffMemberId",
                table: "ManagementTaskHistory");

            migrationBuilder.DropForeignKey(
                name: "FK_ManagementTasks_StaffMembers_AssignedToId",
                table: "ManagementTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_ManagementTasks_StaffMembers_CreatedById",
                table: "ManagementTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_ManagementTasks_StaffMembers_ReviewerId",
                table: "ManagementTasks");

            migrationBuilder.DropTable(
                name: "TaskAttachments");

            migrationBuilder.DropIndex(
                name: "IX_ManagementTasks_CreatedById",
                table: "ManagementTasks");

            migrationBuilder.DropIndex(
                name: "IX_ManagementTasks_ReviewerId",
                table: "ManagementTasks");

            // Rolling back: Cancelled (5) did not exist before; such tasks become On Hold so the old check constraint holds.
            migrationBuilder.Sql("UPDATE ManagementTasks SET Status = 4 WHERE Status = 5;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagementTasks_Status",
                table: "ManagementTasks");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ManagementTasks");

            migrationBuilder.DropColumn(
                name: "ReviewerId",
                table: "ManagementTasks");

            migrationBuilder.RenameColumn(
                name: "AssignedToId",
                table: "ManagementTasks",
                newName: "ManagementMemberId");

            migrationBuilder.RenameIndex(
                name: "IX_ManagementTasks_AssignedToId_Status",
                table: "ManagementTasks",
                newName: "IX_ManagementTasks_ManagementMemberId_Status");

            migrationBuilder.RenameColumn(
                name: "StaffMemberId",
                table: "ManagementTaskHistory",
                newName: "ManagementMemberId");

            migrationBuilder.RenameIndex(
                name: "IX_ManagementTaskHistory_StaffMemberId_ChangedAt",
                table: "ManagementTaskHistory",
                newName: "IX_ManagementTaskHistory_ManagementMemberId_ChangedAt");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagementTasks_Status",
                table: "ManagementTasks",
                sql: "[Status] BETWEEN 1 AND 4");

            migrationBuilder.AddForeignKey(
                name: "FK_ManagementTaskHistory_StaffMembers_ManagementMemberId",
                table: "ManagementTaskHistory",
                column: "ManagementMemberId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ManagementTasks_StaffMembers_ManagementMemberId",
                table: "ManagementTasks",
                column: "ManagementMemberId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
