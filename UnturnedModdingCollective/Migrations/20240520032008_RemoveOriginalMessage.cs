using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnturnedModdingCollective.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOriginalMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MessageChannelId",
                table: "review_requests");

            migrationBuilder.DropColumn(
                name: "MessageId",
                table: "review_requests");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "MessageChannelId",
                table: "review_requests",
                type: "bigint unsigned",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.AddColumn<ulong>(
                name: "MessageId",
                table: "review_requests",
                type: "bigint unsigned",
                nullable: false,
                defaultValue: 0ul);
        }
    }
}
