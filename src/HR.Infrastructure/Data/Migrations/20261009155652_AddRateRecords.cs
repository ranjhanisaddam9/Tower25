using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HR.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRateRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RateRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    BilledMonthlyUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CommissionPerPeriodUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PayMonthlyAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PayCurrency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    ChangeType = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NeedsBillingReview = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateRecords", x => x.Id);
                    table.CheckConstraint("CK_RateRecords_Amounts", "[BilledMonthlyUsd] > 0 AND [PayMonthlyAmount] > 0 AND [CommissionPerPeriodUsd] >= 0");
                    table.CheckConstraint("CK_RateRecords_EffectiveFromPeriodStart", "DAY([EffectiveFrom]) IN (1, 16)");
                    table.CheckConstraint("CK_RateRecords_PkrWholeRupees", "[PayCurrency] <> 'PKR' OR [PayMonthlyAmount] = ROUND([PayMonthlyAmount], 0)");
                    table.ForeignKey(
                        name: "FK_RateRecords_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RateRecords_NeedsBillingReview",
                table: "RateRecords",
                column: "NeedsBillingReview",
                filter: "[NeedsBillingReview] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_RateRecords_PersonId_EffectiveFrom",
                table: "RateRecords",
                columns: new[] { "PersonId", "EffectiveFrom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RateRecords");
        }
    }
}
