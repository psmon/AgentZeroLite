using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agent.Common.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenUsageRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TokenSourceCheckpoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceFile = table.Column<string>(type: "TEXT", nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", nullable: false),
                    ByteOffset = table.Column<long>(type: "INTEGER", nullable: false),
                    LineCount = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenSourceCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TokenUsageRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Vendor = table.Column<string>(type: "TEXT", nullable: false),
                    AccountKey = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Cwd = table.Column<string>(type: "TEXT", nullable: false),
                    GitBranch = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheCreateTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ReasoningTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    RawRequestId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceFile = table.Column<string>(type: "TEXT", nullable: false),
                    SourceLine = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenUsageRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TokenSourceCheckpoints_SourceFile",
                table: "TokenSourceCheckpoints",
                column: "SourceFile",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsageRecords_SourceFile_SourceLine",
                table: "TokenUsageRecords",
                columns: new[] { "SourceFile", "SourceLine" });

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsageRecords_Vendor_RawRequestId",
                table: "TokenUsageRecords",
                columns: new[] { "Vendor", "RawRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsageRecords_Vendor_RecordedAt",
                table: "TokenUsageRecords",
                columns: new[] { "Vendor", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TokenSourceCheckpoints");

            migrationBuilder.DropTable(
                name: "TokenUsageRecords");
        }
    }
}
