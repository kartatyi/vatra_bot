using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShrugOnFailure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShrugOnFailure",
                table: "ChatSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShrugOnFailure",
                table: "ChatSettings");
        }
    }
}
