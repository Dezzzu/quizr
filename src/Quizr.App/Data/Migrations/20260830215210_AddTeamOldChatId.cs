using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quizr.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamOldChatId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(name: "OldChatId", table: "Teams", type: "bigint", nullable: true);

            migrationBuilder.CreateIndex(name: "IX_Teams_OldChatId", table: "Teams", column: "OldChatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Teams_OldChatId", table: "Teams");

            migrationBuilder.DropColumn(name: "OldChatId", table: "Teams");
        }
    }
}
