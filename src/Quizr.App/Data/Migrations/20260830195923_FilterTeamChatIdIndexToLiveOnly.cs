using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quizr.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class FilterTeamChatIdIndexToLiveOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Teams_ChatId", table: "Teams");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_ChatId",
                table: "Teams",
                column: "ChatId",
                unique: true,
                filter: "\"DeactivatedAt\" IS NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Teams_ChatId", table: "Teams");

            migrationBuilder.CreateIndex(name: "IX_Teams_ChatId", table: "Teams", column: "ChatId", unique: true);
        }
    }
}
