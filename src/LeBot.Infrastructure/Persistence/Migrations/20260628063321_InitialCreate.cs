using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepostEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Extractor = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorVariant = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorReason = table.Column<string>(type: "TEXT", nullable: true),
                    MediaCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    ElapsedMs = table.Column<long>(type: "INTEGER", nullable: false),
                    BotVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ChatHash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepostEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepostEvents_Host",
                table: "RepostEvents",
                column: "Host");

            migrationBuilder.CreateIndex(
                name: "IX_RepostEvents_OccurredAt",
                table: "RepostEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_RepostEvents_Outcome",
                table: "RepostEvents",
                column: "Outcome");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepostEvents");
        }
    }
}
