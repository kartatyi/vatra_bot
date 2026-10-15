using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackLinkSightings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeenLinks");

            migrationBuilder.CreateTable(
                name: "LinkSightings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChatHash = table.Column<string>(type: "TEXT", nullable: false),
                    LinkKey = table.Column<string>(type: "TEXT", nullable: false),
                    SenderId = table.Column<long>(type: "INTEGER", nullable: true),
                    SeenAt = table.Column<string>(type: "TEXT", nullable: false),
                    IsRepeat = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkSightings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkSightings_ChatHash_LinkKey",
                table: "LinkSightings",
                columns: new[] { "ChatHash", "LinkKey" });

            migrationBuilder.CreateIndex(
                name: "IX_LinkSightings_ChatHash_SenderId",
                table: "LinkSightings",
                columns: new[] { "ChatHash", "SenderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkSightings");

            migrationBuilder.CreateTable(
                name: "SeenLinks",
                columns: table => new
                {
                    ChatHash = table.Column<string>(type: "TEXT", nullable: false),
                    LinkKey = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeenLinks", x => new { x.ChatHash, x.LinkKey });
                });
        }
    }
}
