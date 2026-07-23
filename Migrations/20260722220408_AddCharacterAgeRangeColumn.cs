using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryFunTimeApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterAgeRangeColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgeRange",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgeRange",
                table: "Characters");
        }
    }
}
