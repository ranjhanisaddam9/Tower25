using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HR.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPayroll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_People_IsActive_FullName",
                table: "People");

            migrationBuilder.DropIndex(
                name: "UX_People_ActiveOwner",
                table: "People");

            migrationBuilder.DropCheckConstraint(
                name: "CK_People_InactiveHasLeavingDate",
                table: "People");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "People");

            migrationBuilder.CreateTable(
                name: "PayrollRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ExchangeRateEntryId = table.Column<int>(type: "int", nullable: true),
                    RateOverridden = table.Column<bool>(type: "bit", nullable: false),
                    RateNote = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GeneratedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinalizedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRuns", x => x.Id);
                    table.CheckConstraint("CK_PayrollRuns_FinalizedHasRate", "[Status] <> 'Finalized' OR ([ExchangeRate] IS NOT NULL AND [FinalizedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_PayrollRuns_Period", "[PeriodEnd] >= [PeriodStart] AND DAY([PeriodStart]) IN (1, 16)");
                    table.CheckConstraint("CK_PayrollRuns_Rate", "[ExchangeRate] IS NULL OR [ExchangeRate] > 0");
                });

            migrationBuilder.CreateTable(
                name: "PayrollLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    PersonCode = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    PersonName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Designation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PersonType = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    HireSource = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Iban = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    RateRecordId = table.Column<int>(type: "int", nullable: true),
                    BilledMonthlyUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CommissionPerPeriodUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PayMonthlyAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PayCurrency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: true),
                    ExtraDays = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ExtraDaysNote = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    WorkingDays = table.Column<int>(type: "int", nullable: false),
                    EmployedWorkingDays = table.Column<int>(type: "int", nullable: false),
                    UnpaidDays = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    PayableDays = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    SalaryPartUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CommissionUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BilledUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PayUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PayPkr = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AdjustmentsPkr = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AdjustmentsUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    NetPayPkr = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    NetPayUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    InvoiceUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    OwnerEarningUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    OwnerEarningPkr = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Issue = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    IsOrphaned = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollLines", x => x.Id);
                    table.CheckConstraint("CK_PayrollLines_ExtraDays", "[ExtraDays] = 0 OR ([ExtraDays] BETWEEN 0.5 AND 10 AND [ExtraDays] * 2 = FLOOR([ExtraDays] * 2))");
                    table.ForeignKey(
                        name: "FK_PayrollLines_PayrollRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PayrollLines_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollRunEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRunEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollRunEvents_PayrollRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PayrollAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AmountPkr = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AmountUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollAdjustments", x => x.Id);
                    table.CheckConstraint("CK_PayrollAdjustments_Amount", "([Currency] = 'USD' AND [Amount] BETWEEN 0.01 AND 100000) OR ([Currency] = 'PKR' AND [Amount] BETWEEN 1 AND 50000000 AND [Amount] = ROUND([Amount], 0))");
                    table.ForeignKey(
                        name: "FK_PayrollAdjustments_PayrollLines_LineId",
                        column: x => x.LineId,
                        principalTable: "PayrollLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PayrollLineAbsences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Portion = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    PaidDays = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    UnpaidDays = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollLineAbsences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollLineAbsences_PayrollLines_LineId",
                        column: x => x.LineId,
                        principalTable: "PayrollLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_People_FullName",
                table: "People",
                column: "FullName");

            migrationBuilder.CreateIndex(
                name: "IX_People_HireSource",
                table: "People",
                column: "HireSource");

            migrationBuilder.CreateIndex(
                name: "IX_People_LeavingDate",
                table: "People",
                column: "LeavingDate");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustments_LineId",
                table: "PayrollAdjustments",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLineAbsences_LineId_Date",
                table: "PayrollLineAbsences",
                columns: new[] { "LineId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLines_PersonId",
                table: "PayrollLines",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "UX_PayrollLines_RunId_PersonId",
                table: "PayrollLines",
                columns: new[] { "RunId", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRunEvents_RunId_At",
                table: "PayrollRunEvents",
                columns: new[] { "RunId", "At" });

            migrationBuilder.CreateIndex(
                name: "UX_PayrollRuns_PeriodStart",
                table: "PayrollRuns",
                column: "PeriodStart",
                unique: true);

            // SPEC §2: at most one active Owner. "Active" now depends on today (Asia/Karachi, UTC+05:00, no DST), so it
            // can't be a filtered unique index any more; the service checks it and this trigger is the safety net.
            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_People_SingleActiveOwner] ON [dbo].[People] AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted WHERE [HireSource] = 'Owner') RETURN;
    DECLARE @today date = CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+05:00') AS date);
    IF (SELECT COUNT(*) FROM [dbo].[People]
        WHERE [HireSource] = 'Owner' AND ([LeavingDate] IS NULL OR [LeavingDate] >= @today)) > 1
        THROW 51002, 'At most one active person can have the Owner hire source.', 1;
END");

            // A finalized payroll can never be deleted (the service refuses too).
            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_PayrollRuns_NoDeleteFinalized] ON [dbo].[PayrollRuns] AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted WHERE [Status] = 'Finalized')
        THROW 51010, 'A finalized payroll can never be deleted.', 1;
END");

            // A finalized payroll's lines, absences and adjustments are frozen: no insert, update or delete.
            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_PayrollLines_Frozen] ON [dbo].[PayrollLines] AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM (SELECT [RunId] FROM inserted UNION ALL SELECT [RunId] FROM deleted) AS x
               JOIN [dbo].[PayrollRuns] AS r ON r.[Id] = x.[RunId] WHERE r.[Status] = 'Finalized')
        THROW 51011, 'A finalized payroll can''t be changed.', 1;
END");

            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_PayrollAdjustments_Frozen] ON [dbo].[PayrollAdjustments] AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM (SELECT [LineId] FROM inserted UNION ALL SELECT [LineId] FROM deleted) AS x
               JOIN [dbo].[PayrollLines] AS l ON l.[Id] = x.[LineId]
               JOIN [dbo].[PayrollRuns] AS r ON r.[Id] = l.[RunId] WHERE r.[Status] = 'Finalized')
        THROW 51011, 'A finalized payroll can''t be changed.', 1;
END");

            migrationBuilder.Sql(@"
CREATE TRIGGER [dbo].[TR_PayrollLineAbsences_Frozen] ON [dbo].[PayrollLineAbsences] AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM (SELECT [LineId] FROM inserted UNION ALL SELECT [LineId] FROM deleted) AS x
               JOIN [dbo].[PayrollLines] AS l ON l.[Id] = x.[LineId]
               JOIN [dbo].[PayrollRuns] AS r ON r.[Id] = l.[RunId] WHERE r.[Status] = 'Finalized')
        THROW 51011, 'A finalized payroll can''t be changed.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_PayrollLineAbsences_Frozen]");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_PayrollAdjustments_Frozen]");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_PayrollLines_Frozen]");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_PayrollRuns_NoDeleteFinalized]");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_People_SingleActiveOwner]");

            migrationBuilder.DropTable(
                name: "PayrollAdjustments");

            migrationBuilder.DropTable(
                name: "PayrollLineAbsences");

            migrationBuilder.DropTable(
                name: "PayrollRunEvents");

            migrationBuilder.DropTable(
                name: "PayrollLines");

            migrationBuilder.DropTable(
                name: "PayrollRuns");

            migrationBuilder.DropIndex(
                name: "IX_People_FullName",
                table: "People");

            migrationBuilder.DropIndex(
                name: "IX_People_HireSource",
                table: "People");

            migrationBuilder.DropIndex(
                name: "IX_People_LeavingDate",
                table: "People");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "People",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // The old flag meant "no leaving date".
            migrationBuilder.Sql("UPDATE [dbo].[People] SET [IsActive] = CASE WHEN [LeavingDate] IS NULL THEN 1 ELSE 0 END");

            migrationBuilder.CreateIndex(
                name: "IX_People_IsActive_FullName",
                table: "People",
                columns: new[] { "IsActive", "FullName" });

            migrationBuilder.CreateIndex(
                name: "UX_People_ActiveOwner",
                table: "People",
                column: "HireSource",
                unique: true,
                filter: "[HireSource] = 'Owner' AND [IsActive] = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_People_InactiveHasLeavingDate",
                table: "People",
                sql: "([IsActive] = 1 AND [LeavingDate] IS NULL) OR ([IsActive] = 0 AND [LeavingDate] IS NOT NULL)");
        }
    }
}
