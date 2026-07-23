using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryFunTimeApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviousCartoonImageUrlColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousCartoonImageUrl",
                table: "Pages",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousCartoonImageUrl",
                table: "Pages");
        }
    }
}
