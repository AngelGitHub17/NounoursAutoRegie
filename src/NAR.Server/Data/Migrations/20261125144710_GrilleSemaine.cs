using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NAR.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GrilleSemaine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupKey",
                table: "PlaylistEntries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Remplissage",
                table: "PlaylistEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "GrilleActive",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "GrilleHorizonJours",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "RemplissageKinds",
                table: "Channels",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "BandeAnnonce,Clip");

            migrationBuilder.CreateTable(
                name: "GrilleCases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Jours = table.Column<int>(type: "INTEGER", nullable: false),
                    Heure = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    ProgrammeTypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrilleCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrilleCases_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GrilleCases_MediaAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "MediaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GrilleCases_ProgrammeTypes_ProgrammeTypeId",
                        column: x => x.ProgrammeTypeId,
                        principalTable: "ProgrammeTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_GroupKey",
                table: "PlaylistEntries",
                column: "GroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_GrilleCases_AssetId",
                table: "GrilleCases",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_GrilleCases_ChannelId",
                table: "GrilleCases",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_GrilleCases_ProgrammeTypeId",
                table: "GrilleCases",
                column: "ProgrammeTypeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrilleCases");

            migrationBuilder.DropIndex(
                name: "IX_PlaylistEntries_GroupKey",
                table: "PlaylistEntries");

            migrationBuilder.DropColumn(
                name: "GroupKey",
                table: "PlaylistEntries");

            migrationBuilder.DropColumn(
                name: "Remplissage",
                table: "PlaylistEntries");

            migrationBuilder.DropColumn(
                name: "GrilleActive",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "GrilleHorizonJours",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemplissageKinds",
                table: "Channels");
        }
    }
}
