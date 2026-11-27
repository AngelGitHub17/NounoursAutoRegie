using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NAR.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Enchainement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ALaSuite",
                table: "GrilleCases",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnchainementActif",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnchainementJingleProgramme",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "EnchainementPubSeconds",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<int>(
                name: "JinglePubDebutId",
                table: "Channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JinglePubFinId",
                table: "Channels",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ALaSuite",
                table: "GrilleCases");

            migrationBuilder.DropColumn(
                name: "EnchainementActif",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "EnchainementJingleProgramme",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "EnchainementPubSeconds",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "JinglePubDebutId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "JinglePubFinId",
                table: "Channels");
        }
    }
}
