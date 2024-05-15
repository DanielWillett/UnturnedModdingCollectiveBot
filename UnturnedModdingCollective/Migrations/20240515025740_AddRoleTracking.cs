using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnturnedModdingCollective.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_request_roles",
                columns: table => new
                {
                    RoleId = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    Request = table.Column<int>(type: "int", nullable: false)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_request_roles");
        }
    }
}
