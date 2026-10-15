using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ChatId",
                table: "ChatSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyDigest",
                table: "ChatSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "ChatSettings");

            migrationBuilder.DropColumn(
                name: "WeeklyDigest",
                table: "ChatSettings");
        }
    }
}
