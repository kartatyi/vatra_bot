using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatSettings",
                columns: table => new
                {
                    ChatHash = table.Column<string>(type: "TEXT", nullable: false),
                    VideoCaption = table.Column<string>(type: "TEXT", nullable: false),
                    PostCaption = table.Column<string>(type: "TEXT", nullable: false),
                    OfferFullText = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSettings", x => x.ChatHash);
                });

            migrationBuilder.CreateTable(
                name: "RevealableTexts",
                columns: table => new
                {
                    ChatHash = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevealableTexts", x => new { x.ChatHash, x.MessageId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RevealableTexts_CreatedAt",
                table: "RevealableTexts",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatSettings");

            migrationBuilder.DropTable(
                name: "RevealableTexts");
        }
    }
}
