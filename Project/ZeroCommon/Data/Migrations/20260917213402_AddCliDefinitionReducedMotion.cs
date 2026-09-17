using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agent.Common.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCliDefinitionReducedMotion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReducedMotion",
                table: "CliDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "CliDefinitions",
                keyColumn: "Id",
                keyValue: 1,
                column: "ReducedMotion",
                value: false);

            migrationBuilder.UpdateData(
                table: "CliDefinitions",
                keyColumn: "Id",
                keyValue: 2,
                column: "ReducedMotion",
                value: false);

            migrationBuilder.UpdateData(
                table: "CliDefinitions",
                keyColumn: "Id",
                keyValue: 3,
                column: "ReducedMotion",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReducedMotion",
                table: "CliDefinitions");
        }
    }
}
