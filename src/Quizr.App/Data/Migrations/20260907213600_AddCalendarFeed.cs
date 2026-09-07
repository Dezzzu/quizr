using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quizr.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "CalendarToken", table: "Players", type: "text", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CalendarTokenIssuedAt",
                table: "Players",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "CalendarVersion",
                table: "Players",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevisedAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(
                    new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                    new TimeSpan(0, 0, 0, 0, 0)
                )
            );

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Games",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            // EF's default for a non-nullable column is 0001-01-01, which would hand every
            // game that already exists a DTSTAMP from the year one. RevisedAt means "when this
            // event was last revised", and for a game nobody has revised that is when it was
            // created.
            migrationBuilder.Sql("UPDATE \"Games\" SET \"RevisedAt\" = \"CreatedAt\"");

            migrationBuilder.CreateIndex(
                name: "IX_Players_CalendarToken",
                table: "Players",
                column: "CalendarToken",
                unique: true,
                filter: "\"CalendarToken\" IS NOT NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Players_CalendarToken", table: "Players");

            migrationBuilder.DropColumn(name: "CalendarToken", table: "Players");

            migrationBuilder.DropColumn(name: "CalendarTokenIssuedAt", table: "Players");

            migrationBuilder.DropColumn(name: "CalendarVersion", table: "Players");

            migrationBuilder.DropColumn(name: "RevisedAt", table: "Games");

            migrationBuilder.DropColumn(name: "Revision", table: "Games");
        }
    }
}
