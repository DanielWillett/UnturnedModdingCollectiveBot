using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnturnedModdingCollective.Migrations
{
    /// <inheritdoc />
    public partial class InitialMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "applicable_roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserAddedBy = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    RoleId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    GuildId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Emoji = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NetVotesRequired = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applicable_roles", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "persisting_roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    UtcRemoveAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ExpiryProcessed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GuildId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    RoleId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    UserAddedBy = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    UtcTimestamp = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_persisting_roles", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "review_requests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Steam64 = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    GlobalName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UtcTimeStarted = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RolesAppliedFor = table.Column<int>(type: "int", nullable: false),
                    RolesAccepted = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_requests", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "review_request_roles",
                columns: table => new
                {
                    RoleId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Request = table.Column<int>(type: "int", nullable: false),
                    ClosedUnderError = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PollMessageId = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    Accepted = table.Column<bool>(type: "tinyint(1)", nullable: true),
                    YesVotes = table.Column<int>(type: "int", nullable: false),
                    NoVotes = table.Column<int>(type: "int", nullable: false),
                    ThreadId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    ResubmitApprover = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    UtcTimeCancelled = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UtcTimeSubmitted = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UtcTimeVoteExpires = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UtcTimeClosed = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_request_roles", x => new { x.Request, x.RoleId });
                    table.ForeignKey(
                        name: "FK_review_request_roles_review_requests_Request",
                        column: x => x.Request,
                        principalTable: "review_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "review_request_votes",
                columns: table => new
                {
                    VoteIndex = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Request = table.Column<int>(type: "int", nullable: false),
                    Vote = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    UserId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    GlobalName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_request_votes", x => new { x.Request, x.RoleId, x.VoteIndex });
                    table.ForeignKey(
                        name: "FK_review_request_votes_review_request_roles_Request_RoleId",
                        columns: x => new { x.Request, x.RoleId },
                        principalTable: "review_request_roles",
                        principalColumns: new[] { "Request", "RoleId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_review_request_votes_review_requests_Request",
                        column: x => x.Request,
                        principalTable: "review_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "applicable_roles");

            migrationBuilder.DropTable(
                name: "persisting_roles");

            migrationBuilder.DropTable(
                name: "review_request_votes");

            migrationBuilder.DropTable(
                name: "review_request_roles");

            migrationBuilder.DropTable(
                name: "review_requests");
        }
    }
}
