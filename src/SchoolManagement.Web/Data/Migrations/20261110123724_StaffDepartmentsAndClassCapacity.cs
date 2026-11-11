using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffDepartmentsAndClassCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "StaffMembers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subcategory",
                table: "StaffMembers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Capacity",
                table: "Classes",
                type: "int",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subcategories = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StudentTransferHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    FromClassId = table.Column<int>(type: "int", nullable: false),
                    ToClassId = table.Column<int>(type: "int", nullable: false),
                    TransferredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TransferredBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentTransferHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentTransferHistories_Classes_FromClassId",
                        column: x => x.FromClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentTransferHistories_Classes_ToClassId",
                        column: x => x.ToClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentTransferHistories_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 1,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 2,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 3,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 4,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 5,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 6,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 7,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 8,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 9,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 10,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 11,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 12,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 13,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 14,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 15,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 16,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 17,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 18,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 19,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 20,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 21,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 22,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 23,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 24,
                column: "Capacity",
                value: 25);

            migrationBuilder.UpdateData(
                table: "Classes",
                keyColumn: "Id",
                keyValue: 25,
                column: "Capacity",
                value: 25);

            migrationBuilder.InsertData(
                table: "Departments",
                columns: new[] { "Id", "Name", "NormalizedName", "SortOrder", "Subcategories" },
                values: new object[,]
                {
                    { 1, "Arabic", "ARABIC", 1, null },
                    { 2, "English", "ENGLISH", 2, null },
                    { 3, "Math", "MATH", 3, null },
                    { 4, "Science", "SCIENCE", 4, null },
                    { 5, "ASS", "ASS", 5, null },
                    { 6, "ESS", "ESS", 6, null },
                    { 7, "Religion", "RELIGION", 7, "Islamic,Christian" },
                    { 8, "PE", "PE", 8, null },
                    { 9, "Art", "ART", 9, null },
                    { 10, "ICT", "ICT", 10, null },
                    { 11, "Music", "MUSIC", 11, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_DepartmentId",
                table: "StaffMembers",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_NormalizedName",
                table: "Departments",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentTransferHistories_FromClassId",
                table: "StudentTransferHistories",
                column: "FromClassId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentTransferHistories_StudentId_TransferredAt",
                table: "StudentTransferHistories",
                columns: new[] { "StudentId", "TransferredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StudentTransferHistories_ToClassId",
                table: "StudentTransferHistories",
                column: "ToClassId");

            migrationBuilder.AddForeignKey(
                name: "FK_StaffMembers_Departments_DepartmentId",
                table: "StaffMembers",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StaffMembers_Departments_DepartmentId",
                table: "StaffMembers");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "StudentTransferHistories");

            migrationBuilder.DropIndex(
                name: "IX_StaffMembers_DepartmentId",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "Subcategory",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "Capacity",
                table: "Classes");
        }
    }
}
