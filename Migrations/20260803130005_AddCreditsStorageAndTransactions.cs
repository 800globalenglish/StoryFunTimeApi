using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryFunTimeApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditsStorageAndTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreditBalance",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "StorageValidUntil",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CreditTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    UserId1 = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreditsDelta = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StripeSessionId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditTransactions_Users_UserId1",
                        column: x => x.UserId1,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditTransactions_StripeSessionId",
                table: "CreditTransactions",
                column: "StripeSessionId",
                unique: true,
                filter: "[StripeSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CreditTransactions_UserId1",
                table: "CreditTransactions",
                column: "UserId1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditTransactions");

            migrationBuilder.DropColumn(
                name: "CreditBalance",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "StorageValidUntil",
                table: "Users");
        }
    }
}
