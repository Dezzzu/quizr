using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quizr.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeFranchiseDefaultsOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DefaultVenue",
                table: "Franchises",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text"
            );

            migrationBuilder.AlterColumn<int>(
                name: "DefaultCapacity",
                table: "Franchises",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DefaultVenue",
                table: "Franchises",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true
            );

            migrationBuilder.AlterColumn<int>(
                name: "DefaultCapacity",
                table: "Franchises",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true
            );
        }
    }
}
