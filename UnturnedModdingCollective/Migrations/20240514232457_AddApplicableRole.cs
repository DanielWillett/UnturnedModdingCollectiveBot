using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnturnedModdingCollective.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicableRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ReviewRequests",
                table: "ReviewRequests");

            migrationBuilder.RenameTable(
                name: "ReviewRequests",
                newName: "review_requests");

            migrationBuilder.AddPrimaryKey(
                name: "PK_review_requests",
                table: "review_requests",
                column: "Id");

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
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applicable_roles", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "applicable_roles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_review_requests",
                table: "review_requests");

            migrationBuilder.RenameTable(
                name: "review_requests",
                newName: "ReviewRequests");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ReviewRequests",
                table: "ReviewRequests",
                column: "Id");
        }
    }
}
