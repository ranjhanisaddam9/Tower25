using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HR.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    BusinessName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BusinessAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BusinessEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    BusinessPhone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BankAccountTitle = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    BankAccountNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    BankSwift = table.Column<string>(type: "varchar(11)", unicode: false, maxLength: 11, nullable: true),
                    ClientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClientContactPerson = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ClientEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    InvoicePrefix = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    PaymentTermsDays = table.Column<int>(type: "int", nullable: false),
                    InvoiceFooter = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PayslipIssuerName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                    table.CheckConstraint("CK_AppSettings_Singleton", "[Id] = 1");
                    table.CheckConstraint("CK_AppSettings_Terms", "[PaymentTermsDays] BETWEEN 0 AND 120");
                });

            // The single settings row. The payslip issuer moves here from the Payslip:IssuerName setting, whose committed
            // value was "HR Payroll"; the config key is removed in the same milestone (M8).
            migrationBuilder.Sql("INSERT INTO [dbo].[AppSettings] ([Id], [InvoicePrefix], [PaymentTermsDays], [PayslipIssuerName]) VALUES (1, N'INV', 7, N'HR Payroll');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");
        }
    }
}
