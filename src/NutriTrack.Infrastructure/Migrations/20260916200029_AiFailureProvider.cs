using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NutriTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiFailureProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "AiFailures",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Provider",
                table: "AiFailures");
        }
    }
}
