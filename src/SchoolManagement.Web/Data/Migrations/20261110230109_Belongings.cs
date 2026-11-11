using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchoolManagement.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Belongings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AcademicYears",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicYears", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AcademicQuarters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AcademicYearId = table.Column<int>(type: "int", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicQuarters", x => x.Id);
                    table.CheckConstraint("CK_AcademicQuarters_Dates", "([StartDate] IS NULL AND [EndDate] IS NULL) OR ([StartDate] IS NOT NULL AND [EndDate] IS NOT NULL AND [EndDate] >= [StartDate])");
                    table.CheckConstraint("CK_AcademicQuarters_Number", "[Number] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_AcademicQuarters_AcademicYears_AcademicYearId",
                        column: x => x.AcademicYearId,
                        principalTable: "AcademicYears",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BelongingsRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StudentId = table.Column<int>(type: "int", nullable: false),
                    SchoolClassId = table.Column<int>(type: "int", nullable: false),
                    AcademicQuarterId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Jacket = table.Column<int>(type: "int", nullable: true),
                    SchoolBag = table.Column<int>(type: "int", nullable: true),
                    PencilCase = table.Column<int>(type: "int", nullable: true),
                    WaterFlask = table.Column<int>(type: "int", nullable: true),
                    LunchBox = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BelongingsRecords", x => x.Id);
                    table.CheckConstraint("CK_BelongingsRecords_Jacket", "[Jacket] IS NULL OR [Jacket] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_BelongingsRecords_LunchBox", "[LunchBox] IS NULL OR [LunchBox] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_BelongingsRecords_PencilCase", "[PencilCase] IS NULL OR [PencilCase] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_BelongingsRecords_SchoolBag", "[SchoolBag] IS NULL OR [SchoolBag] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_BelongingsRecords_WaterFlask", "[WaterFlask] IS NULL OR [WaterFlask] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_BelongingsRecords_AcademicQuarters_AcademicQuarterId",
                        column: x => x.AcademicQuarterId,
                        principalTable: "AcademicQuarters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BelongingsRecords_Classes_SchoolClassId",
                        column: x => x.SchoolClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BelongingsRecords_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AcademicMaterialRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BelongingsRecordId = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Book = table.Column<int>(type: "int", nullable: false),
                    CopyBook = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicMaterialRecords", x => x.Id);
                    table.CheckConstraint("CK_AcademicMaterials_Book", "[Book] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_AcademicMaterials_CopyBook", "[CopyBook] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_AcademicMaterialRecords_BelongingsRecords_BelongingsRecordId",
                        column: x => x.BelongingsRecordId,
                        principalTable: "BelongingsRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AcademicMaterialRecords_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BelongingsNoteHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BelongingsRecordId = table.Column<int>(type: "int", nullable: false),
                    PreviousNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BelongingsNoteHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BelongingsNoteHistory_BelongingsRecords_BelongingsRecordId",
                        column: x => x.BelongingsRecordId,
                        principalTable: "BelongingsRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicMaterialRecords_DepartmentId",
                table: "AcademicMaterialRecords",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "UX_AcademicMaterials_RecordSubject",
                table: "AcademicMaterialRecords",
                columns: new[] { "BelongingsRecordId", "DepartmentId", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicQuarters_AcademicYearId_Number",
                table: "AcademicQuarters",
                columns: new[] { "AcademicYearId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicYears_Name",
                table: "AcademicYears",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BelongingsNoteHistory_BelongingsRecordId_ChangedAt",
                table: "BelongingsNoteHistory",
                columns: new[] { "BelongingsRecordId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BelongingsRecords_AcademicQuarterId_Date",
                table: "BelongingsRecords",
                columns: new[] { "AcademicQuarterId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_BelongingsRecords_SchoolClassId",
                table: "BelongingsRecords",
                column: "SchoolClassId");

            migrationBuilder.CreateIndex(
                name: "UX_Belongings_StudentDay",
                table: "BelongingsRecords",
                columns: new[] { "StudentId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcademicMaterialRecords");

            migrationBuilder.DropTable(
                name: "BelongingsNoteHistory");

            migrationBuilder.DropTable(
                name: "BelongingsRecords");

            migrationBuilder.DropTable(
                name: "AcademicQuarters");

            migrationBuilder.DropTable(
                name: "AcademicYears");
        }
    }
}
