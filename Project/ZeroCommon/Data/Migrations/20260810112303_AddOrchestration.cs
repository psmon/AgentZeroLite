using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agent.Common.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrchestrationDispatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkerName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DispatchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrchestrationDispatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrchestrationRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrchestrationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrchestrationTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskKey = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    DependsOnJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResultMessage = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrchestrationTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrchestrationTasks_OrchestrationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "OrchestrationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrchestrationDispatches_RunId_TaskId",
                table: "OrchestrationDispatches",
                columns: new[] { "RunId", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrchestrationTasks_RunId_TaskKey",
                table: "OrchestrationTasks",
                columns: new[] { "RunId", "TaskKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrchestrationDispatches");

            migrationBuilder.DropTable(
                name: "OrchestrationTasks");

            migrationBuilder.DropTable(
                name: "OrchestrationRuns");
        }
    }
}
