using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Agent.Common.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppWindowStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Left = table.Column<double>(type: "REAL", nullable: false),
                    Top = table.Column<double>(type: "REAL", nullable: false),
                    Width = table.Column<double>(type: "REAL", nullable: false),
                    Height = table.Column<double>(type: "REAL", nullable: false),
                    IsMaximized = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastActiveGroupIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    LastActiveTabIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    OnboardingDismissedVersion = table.Column<string>(type: "TEXT", nullable: false),
                    IsBotDocked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppWindowStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CliDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ExePath = table.Column<string>(type: "TEXT", nullable: false),
                    Arguments = table.Column<string>(type: "TEXT", nullable: true),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CliDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CliGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DirectoryPath = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CliGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClipboardEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CopiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipboardEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CliTabs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    CliDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CliTabs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CliTabs_CliDefinitions_CliDefinitionId",
                        column: x => x.CliDefinitionId,
                        principalTable: "CliDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CliTabs_CliGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "CliGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppWindowStates",
                columns: new[] { "Id", "Height", "IsBotDocked", "IsMaximized", "LastActiveGroupIndex", "LastActiveTabIndex", "Left", "OnboardingDismissedVersion", "Top", "Width" },
                values: new object[] { 1, 720.0, true, false, 0, 0, 0.0, "", 0.0, 860.0 });

            migrationBuilder.InsertData(
                table: "CliDefinitions",
                columns: new[] { "Id", "Arguments", "ExePath", "IsBuiltIn", "Name", "SortOrder" },
                values: new object[,]
                {
                    { 1, null, "cmd.exe", true, "CMD", 0 },
                    { 2, null, "powershell.exe", true, "PW5", 1 },
                    { 3, null, "pwsh.exe", true, "PW7", 2 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CliTabs_CliDefinitionId",
                table: "CliTabs",
                column: "CliDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CliTabs_GroupId",
                table: "CliTabs",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppWindowStates");

            migrationBuilder.DropTable(
                name: "ClipboardEntries");

            migrationBuilder.DropTable(
                name: "CliTabs");

            migrationBuilder.DropTable(
                name: "CliDefinitions");

            migrationBuilder.DropTable(
                name: "CliGroups");
        }
    }
}
