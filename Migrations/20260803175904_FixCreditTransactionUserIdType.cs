using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryFunTimeApi.Migrations
{
    /// <inheritdoc />
    public partial class FixCreditTransactionUserIdType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CreditTransactions_Users_UserId1",
                table: "CreditTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CreditTransactions_UserId1",
                table: "CreditTransactions");

            migrationBuilder.DropColumn(
                name: "UserId1",
                table: "CreditTransactions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "CreditTransactions");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "CreditTransactions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.CreateIndex(
                name: "IX_CreditTransactions_UserId",
                table: "CreditTransactions",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CreditTransactions_Users_UserId",
                table: "CreditTransactions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CreditTransactions_Users_UserId",
                table: "CreditTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CreditTransactions_UserId",
                table: "CreditTransactions");

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "CreditTransactions",
                type: "int",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId1",
                table: "CreditTransactions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_CreditTransactions_UserId1",
                table: "CreditTransactions",
                column: "UserId1");

            migrationBuilder.AddForeignKey(
                name: "FK_CreditTransactions_Users_UserId1",
                table: "CreditTransactions",
                column: "UserId1",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
