using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NAR.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HabillageAntenne : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Signaletique",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OverlayClock",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OverlayCorner",
                table: "Channels",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "haut-droite");

            migrationBuilder.AddColumn<bool>(
                name: "OverlayLogo",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "OverlayLogoOpacity",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 85);

            migrationBuilder.AddColumn<double>(
                name: "OverlayLogoSize",
                table: "Channels",
                type: "REAL",
                nullable: false,
                defaultValue: 9.0);

            migrationBuilder.AddColumn<bool>(
                name: "OverlayRating",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "OverlayRatingAlways",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OverlayUpNext",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "OverlayUpNextBefore",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 120);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Signaletique",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "OverlayClock",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OverlayCorner",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OverlayLogo",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OverlayLogoOpacity",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OverlayLogoSize",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OverlayRating",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OverlayRatingAlways",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OverlayUpNext",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OverlayUpNextBefore",
                table: "Channels");
        }
    }
}
