using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HR.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPeople : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.CreateSequence<int>(
                name: "PersonCodeSequence",
                schema: "dbo");

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CodeNumber = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Designation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Phone = table.Column<string>(type: "varchar(13)", unicode: false, maxLength: 13, nullable: false),
                    Cnic = table.Column<string>(type: "varchar(15)", unicode: false, maxLength: 15, nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Iban = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    JoiningDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LeavingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    HireSource = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                    table.CheckConstraint("CK_People_InactiveHasLeavingDate", "([IsActive] = 1 AND [LeavingDate] IS NULL) OR ([IsActive] = 0 AND [LeavingDate] IS NOT NULL)");
                    table.CheckConstraint("CK_People_LeavingAfterJoining", "[LeavingDate] IS NULL OR [LeavingDate] >= [JoiningDate]");
                });

            migrationBuilder.CreateIndex(
                name: "IX_People_CodeNumber",
                table: "People",
                column: "CodeNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_People_IsActive_FullName",
                table: "People",
                columns: new[] { "IsActive", "FullName" });

            migrationBuilder.CreateIndex(
                name: "IX_People_JoiningDate",
                table: "People",
                column: "JoiningDate");

            migrationBuilder.CreateIndex(
                name: "UX_People_ActiveOwner",
                table: "People",
                column: "HireSource",
                unique: true,
                filter: "[HireSource] = 'Owner' AND [IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_People_Cnic",
                table: "People",
                column: "Cnic",
                unique: true,
                filter: "[Cnic] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_People_Code",
                table: "People",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_People_Email",
                table: "People",
                column: "Email",
                unique: true,
                filter: "[Email] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropSequence(
                name: "PersonCodeSequence",
                schema: "dbo");
        }
    }
}
