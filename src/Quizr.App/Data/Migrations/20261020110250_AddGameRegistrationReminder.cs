using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quizr.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRegistrationReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RegistrationReminderSentAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TeamRegisteredAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "RegistrationReminderSentAt", table: "Games");

            migrationBuilder.DropColumn(name: "TeamRegisteredAt", table: "Games");
        }
    }
}
