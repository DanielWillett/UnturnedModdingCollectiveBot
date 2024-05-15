using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnturnedModdingCollective.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageChannelIdAndCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "MessageChannelId",
                table: "review_requests",
                type: "bigint unsigned",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.AddColumn<DateTime>(
                name: "UtcTimeCancelled",
                table: "review_requests",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UtcTimeVoteExpires",
                table: "review_requests",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MessageChannelId",
                table: "review_requests");

            migrationBuilder.DropColumn(
                name: "UtcTimeCancelled",
                table: "review_requests");

            migrationBuilder.DropColumn(
                name: "UtcTimeVoteExpires",
                table: "review_requests");
        }
    }
}
