using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agent.Common.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCliDefinitionRemoteShell : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRemote",
                table: "CliDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SshHost",
                table: "CliDefinitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshUser",
                table: "CliDefinitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshAuthMethod",
                table: "CliDefinitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshKeyPath",
                table: "CliDefinitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedPassword",
                table: "CliDefinitions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsRemote",
                table: "CliDefinitions");

            migrationBuilder.DropColumn(
                name: "SshHost",
                table: "CliDefinitions");

            migrationBuilder.DropColumn(
                name: "SshUser",
                table: "CliDefinitions");

            migrationBuilder.DropColumn(
                name: "SshAuthMethod",
                table: "CliDefinitions");

            migrationBuilder.DropColumn(
                name: "SshKeyPath",
                table: "CliDefinitions");

            migrationBuilder.DropColumn(
                name: "EncryptedPassword",
                table: "CliDefinitions");
        }
    }
}
