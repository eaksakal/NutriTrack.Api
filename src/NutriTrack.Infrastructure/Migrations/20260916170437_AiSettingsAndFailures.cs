using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NutriTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiSettingsAndFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ThinkingLevel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ThinkingLevel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiFailures_OccurredAt",
                table: "AiFailures",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiFailures");

            migrationBuilder.DropTable(
                name: "AiSettings");
        }
    }
}
