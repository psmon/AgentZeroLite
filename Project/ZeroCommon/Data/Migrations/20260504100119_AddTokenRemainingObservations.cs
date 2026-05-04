using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agent.Common.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenRemainingObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TokenRemainingObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Vendor = table.Column<string>(type: "TEXT", nullable: false),
                    AccountKey = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    FiveHourPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    FiveHourResetsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SevenDayPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    SevenDayResetsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenRemainingObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TokenRemainingObservations_AccountKey_Model_ObservedAtUtc",
                table: "TokenRemainingObservations",
                columns: new[] { "AccountKey", "Model", "ObservedAtUtc" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TokenRemainingObservations");
        }
    }
}
