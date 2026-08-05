using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryFunTimeApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryTypeToBook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StoryType",
                table: "Books",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoryType",
                table: "Books");
        }
    }
}
