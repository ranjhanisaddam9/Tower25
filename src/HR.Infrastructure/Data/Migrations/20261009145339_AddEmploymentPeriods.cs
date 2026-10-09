using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HR.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmploymentPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmploymentPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmploymentPeriods", x => x.Id);
                    table.CheckConstraint("CK_EmploymentPeriods_EndAfterStart", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.ForeignKey(
                        name: "FK_EmploymentPeriods_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmploymentPeriods_PersonId_StartDate",
                table: "EmploymentPeriods",
                columns: new[] { "PersonId", "StartDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_EmploymentPeriods_OnePerPersonOpen",
                table: "EmploymentPeriods",
                column: "PersonId",
                unique: true,
                filter: "[EndDate] IS NULL");

            // A person's periods must never overlap (open periods run to the end of time).
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_EmploymentPeriods_NoOverlap]
                ON [dbo].[EmploymentPeriods]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM inserted i
                        JOIN [dbo].[EmploymentPeriods] p
                          ON p.[PersonId] = i.[PersonId] AND p.[Id] <> i.[Id]
                        WHERE p.[StartDate] <= ISNULL(i.[EndDate], '9999-12-31')
                          AND i.[StartDate] <= ISNULL(p.[EndDate], '9999-12-31'))
                    BEGIN
                        THROW 51001, 'Employment periods for a person must not overlap.', 1;
                    END
                END
                """);

            // Backfill: exactly one period per existing person, copied from the cached joining/leaving dates.
            migrationBuilder.Sql(
                """
                INSERT INTO [dbo].[EmploymentPeriods] ([PersonId], [StartDate], [EndDate], [CreatedAt], [CreatedByUserId], [UpdatedAt], [UpdatedByUserId])
                SELECT [Id], [JoiningDate], [LeavingDate], [CreatedAt], [CreatedByUserId], [UpdatedAt], [UpdatedByUserId]
                FROM [dbo].[People];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmploymentPeriods");
        }
    }
}
