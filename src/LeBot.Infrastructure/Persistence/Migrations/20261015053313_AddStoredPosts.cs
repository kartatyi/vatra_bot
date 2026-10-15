using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoredPosts",
                columns: table => new
                {
                    LinkKey = table.Column<string>(type: "TEXT", nullable: false),
                    Document = table.Column<string>(type: "TEXT", nullable: false),
                    StoredAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredPosts", x => x.LinkKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredPosts_StoredAt",
                table: "StoredPosts",
                column: "StoredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoredPosts");
        }
    }
}
