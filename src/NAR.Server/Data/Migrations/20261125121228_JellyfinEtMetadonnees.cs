using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NAR.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class JellyfinEtMetadonnees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RemoteKeyProtected",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteLibraryId",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteLibraryName",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteUrl",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpisodeNumber",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EpisodeTitle",
                table: "MediaAssets",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "MediaAssets",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "MediaAssets",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Overview",
                table: "MediaAssets",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Rating",
                table: "MediaAssets",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesName",
                table: "MediaAssets",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "MediaAssets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_SeriesName",
                table: "MediaAssets",
                column: "SeriesName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_SeriesName",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "RemoteKeyProtected",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "RemoteLibraryId",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "RemoteLibraryName",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "RemoteUrl",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "EpisodeNumber",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "EpisodeTitle",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "Overview",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "SeriesName",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "MediaAssets");
        }
    }
}
