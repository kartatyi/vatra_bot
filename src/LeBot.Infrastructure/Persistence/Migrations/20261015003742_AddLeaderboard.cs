using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaderboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SenderId",
                table: "RepostEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StatsOptOuts",
                columns: table => new
                {
                    ChatHash = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatsOptOuts", x => new { x.ChatHash, x.UserId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepostEvents_ChatHash_SenderId",
                table: "RepostEvents",
                columns: new[] { "ChatHash", "SenderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatsOptOuts");

            migrationBuilder.DropIndex(
                name: "IX_RepostEvents_ChatHash_SenderId",
                table: "RepostEvents");

            migrationBuilder.DropColumn(
                name: "SenderId",
                table: "RepostEvents");
        }
    }
}
