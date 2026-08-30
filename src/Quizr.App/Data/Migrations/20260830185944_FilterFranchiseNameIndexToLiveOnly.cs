using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quizr.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class FilterFranchiseNameIndexToLiveOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Franchises_TeamId_Name", table: "Franchises");

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_TeamId_Name",
                table: "Franchises",
                columns: new[] { "TeamId", "Name" },
                unique: true,
                filter: "\"ArchivedAt\" IS NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Franchises_TeamId_Name", table: "Franchises");

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_TeamId_Name",
                table: "Franchises",
                columns: new[] { "TeamId", "Name" },
                unique: true
            );
        }
    }
}
