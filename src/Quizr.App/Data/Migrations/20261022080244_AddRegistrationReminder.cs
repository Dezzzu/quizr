using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quizr.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RegistrationReminder",
                table: "Memberships",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RegistrationReminderSentAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RegistrationThreshold",
                table: "Games",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TeamRegisteredAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "DefaultRegistrationThreshold",
                table: "Franchises",
                type: "integer",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "RegistrationReminder", table: "Memberships");

            migrationBuilder.DropColumn(name: "RegistrationReminderSentAt", table: "Games");

            migrationBuilder.DropColumn(name: "RegistrationThreshold", table: "Games");

            migrationBuilder.DropColumn(name: "TeamRegisteredAt", table: "Games");

            migrationBuilder.DropColumn(name: "DefaultRegistrationThreshold", table: "Franchises");
        }
    }
}
