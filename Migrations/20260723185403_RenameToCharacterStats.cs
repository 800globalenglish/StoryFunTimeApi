using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryFunTimeApi.Migrations
{
    /// <inheritdoc />
    public partial class RenameToCharacterStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TotalAvatarsDeleted",
                table: "UserStats",
                newName: "TotalCharactersDeleted");

            migrationBuilder.AddColumn<int>(
                name: "TotalCharactersCreated",
                table: "UserStats",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotalCharactersCreated",
                table: "UserStats");

            migrationBuilder.RenameColumn(
                name: "TotalCharactersDeleted",
                table: "UserStats",
                newName: "TotalAvatarsDeleted");
        }
    }
}
