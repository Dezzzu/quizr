using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Quizr.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    Locale = table.Column<string>(type: "text", nullable: true),
                    DmEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    TimeZoneId = table.Column<string>(type: "text", nullable: false),
                    Locale = table.Column<string>(type: "text", nullable: false),
                    EveningBeforeAt = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    MorningOfAt = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    BeforeStartLead = table.Column<TimeSpan>(type: "interval", nullable: false),
                    BoardMessageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "DialogStates",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    TeamId = table.Column<long>(type: "bigint", nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Step = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DialogStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DialogStates_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_DialogStates_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Franchises",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    TeamId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DefaultVenue = table.Column<string>(type: "text", nullable: false),
                    DefaultCapacity = table.Column<int>(type: "integer", nullable: false),
                    DefaultPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    Schedule = table.Column<string>(type: "jsonb", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Franchises", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Franchises_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    TeamId = table.Column<long>(type: "bigint", nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    IsCaptain = table.Column<bool>(type: "boolean", nullable: false),
                    EveningBefore = table.Column<int>(type: "integer", nullable: false),
                    MorningOf = table.Column<int>(type: "integer", nullable: false),
                    BeforeStart = table.Column<int>(type: "integer", nullable: false),
                    RemindWhenReserve = table.Column<bool>(type: "boolean", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => new { x.TeamId, x.PlayerId });
                    table.ForeignKey(
                        name: "FK_Memberships_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_Memberships_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    TeamId = table.Column<long>(type: "bigint", nullable: false),
                    FranchiseId = table.Column<long>(type: "bigint", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Venue = table.Column<string>(type: "text", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    AnnouncementMessageId = table.Column<long>(type: "bigint", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeclinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastNudgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByPlayerId = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Games_Franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "Franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_Games_Players_CreatedByPlayerId",
                        column: x => x.CreatedByPlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_Games_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    TeamId = table.Column<long>(type: "bigint", nullable: false),
                    GameId = table.Column<long>(type: "bigint", nullable: true),
                    ActorPlayerId = table.Column<long>(type: "bigint", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEntries_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_AuditEntries_Players_ActorPlayerId",
                        column: x => x.ActorPlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_AuditEntries_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Participations",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    GameId = table.Column<long>(type: "bigint", nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Played = table.Column<bool>(type: "boolean", nullable: false),
                    Attended = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Participations_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_Participations_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Signups",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    GameId = table.Column<long>(type: "bigint", nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: true),
                    GuestName = table.Column<string>(type: "text", nullable: true),
                    InvitedByPlayerId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledByPlayerId = table.Column<long>(type: "bigint", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Signups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Signups_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_Signups_Players_CancelledByPlayerId",
                        column: x => x.CancelledByPlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_Signups_Players_InvitedByPlayerId",
                        column: x => x.InvitedByPlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_Signups_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    SignupId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Signups_SignupId",
                        column: x => x.SignupId,
                        principalTable: "Signups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ActorPlayerId",
                table: "AuditEntries",
                column: "ActorPlayerId"
            );

            migrationBuilder.CreateIndex(name: "IX_AuditEntries_GameId", table: "AuditEntries", column: "GameId");

            migrationBuilder.CreateIndex(name: "IX_AuditEntries_TeamId", table: "AuditEntries", column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_DialogStates_ChatId_PlayerId",
                table: "DialogStates",
                columns: new[] { "ChatId", "PlayerId" },
                unique: true
            );

            migrationBuilder.CreateIndex(name: "IX_DialogStates_PlayerId", table: "DialogStates", column: "PlayerId");

            migrationBuilder.CreateIndex(name: "IX_DialogStates_TeamId", table: "DialogStates", column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_TeamId_Name",
                table: "Franchises",
                columns: new[] { "TeamId", "Name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Games_CreatedByPlayerId",
                table: "Games",
                column: "CreatedByPlayerId"
            );

            migrationBuilder.CreateIndex(name: "IX_Games_FranchiseId", table: "Games", column: "FranchiseId");

            migrationBuilder.CreateIndex(name: "IX_Games_TeamId", table: "Games", column: "TeamId");

            migrationBuilder.CreateIndex(name: "IX_Memberships_PlayerId", table: "Memberships", column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_SignupId_Kind",
                table: "Notifications",
                columns: new[] { "SignupId", "Kind" },
                unique: true
            );

            migrationBuilder.CreateIndex(name: "IX_Participations_GameId", table: "Participations", column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_Participations_PlayerId",
                table: "Participations",
                column: "PlayerId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Players_TelegramUserId",
                table: "Players",
                column: "TelegramUserId",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Signups_CancelledByPlayerId",
                table: "Signups",
                column: "CancelledByPlayerId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Signups_GameId_CreatedAt",
                table: "Signups",
                columns: new[] { "GameId", "CreatedAt" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Signups_InvitedByPlayerId",
                table: "Signups",
                column: "InvitedByPlayerId"
            );

            migrationBuilder.CreateIndex(name: "IX_Signups_PlayerId", table: "Signups", column: "PlayerId");

            migrationBuilder.CreateIndex(name: "IX_Teams_ChatId", table: "Teams", column: "ChatId", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AuditEntries");

            migrationBuilder.DropTable(name: "DialogStates");

            migrationBuilder.DropTable(name: "Memberships");

            migrationBuilder.DropTable(name: "Notifications");

            migrationBuilder.DropTable(name: "Participations");

            migrationBuilder.DropTable(name: "Signups");

            migrationBuilder.DropTable(name: "Games");

            migrationBuilder.DropTable(name: "Franchises");

            migrationBuilder.DropTable(name: "Players");

            migrationBuilder.DropTable(name: "Teams");
        }
    }
}
