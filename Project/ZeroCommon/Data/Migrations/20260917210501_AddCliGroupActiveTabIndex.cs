using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agent.Common.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCliGroupActiveTabIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveTabIndex",
                table: "CliGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveTabIndex",
                table: "CliGroups");
        }
    }
}
