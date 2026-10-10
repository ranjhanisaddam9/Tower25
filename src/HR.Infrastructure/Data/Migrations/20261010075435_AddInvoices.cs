using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HR.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoiceCounters",
                columns: table => new
                {
                    Year = table.Column<int>(type: "int", nullable: false),
                    LastNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceCounters", x => x.Year);
                    table.CheckConstraint("CK_InvoiceCounters_Last", "[LastNumber] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BusinessName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BusinessAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BusinessEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    BusinessPhone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BankAccountTitle = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    BankAccountNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    BankSwift = table.Column<string>(type: "varchar(11)", unicode: false, maxLength: 11, nullable: true),
                    ClientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClientAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClientContactPerson = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ClientEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Footer = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TotalUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AmountReceivedUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PaymentNote = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    VoidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VoidedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReplacesInvoiceId = table.Column<int>(type: "int", nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IssuedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.CheckConstraint("CK_Invoices_Due", "[DueDate] >= [IssueDate]");
                    table.CheckConstraint("CK_Invoices_Paid", "[Status] <> 'Paid' OR ([PaidDate] IS NOT NULL AND [AmountReceivedUsd] > 0)");
                    table.CheckConstraint("CK_Invoices_Void", "[Status] <> 'Void' OR ([VoidedAt] IS NOT NULL AND [VoidReason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Invoices_Invoices_ReplacesInvoiceId",
                        column: x => x.ReplacesInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoices_PayrollRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvoiceId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Designation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DaysText = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    SalaryUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExtrasUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_InvoiceId_Position",
                table: "InvoiceLines",
                columns: new[] { "InvoiceId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ReplacesInvoiceId",
                table: "Invoices",
                column: "ReplacesInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Status_DueDate",
                table: "Invoices",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "UX_Invoices_Number",
                table: "Invoices",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Invoices_RunId_NotVoid",
                table: "Invoices",
                column: "RunId",
                unique: true,
                filter: "[Status] <> 'Void'");

            // Paid and void invoices are immutable: their lines can't be inserted, changed or deleted (lines are only
            // ever written once, when the invoice is issued).
            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_InvoiceLines_Frozen] ON [dbo].[InvoiceLines] AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM (SELECT [InvoiceId] FROM inserted UNION ALL SELECT [InvoiceId] FROM deleted) AS x
               JOIN [dbo].[Invoices] AS i ON i.[Id] = x.[InvoiceId] WHERE i.[Status] IN ('Paid', 'Void'))
        THROW 51020, 'A paid or void invoice can''t be changed.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_InvoiceLines_Frozen]");

            migrationBuilder.DropTable(
                name: "InvoiceCounters");

            migrationBuilder.DropTable(
                name: "InvoiceLines");

            migrationBuilder.DropTable(
                name: "Invoices");
        }
    }
}
