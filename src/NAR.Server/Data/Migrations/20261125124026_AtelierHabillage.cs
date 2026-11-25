using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NAR.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AtelierHabillage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasLogo",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AtelierSounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Duration = table.Column<double>(type: "REAL", nullable: false),
                    Bpm = table.Column<double>(type: "REAL", nullable: false),
                    BeatCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AnalysisJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AtelierSounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AtelierCreations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    PaletteKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SoundId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    LogoOnBlack = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    SourceAssetId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExtractStarts = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Seed = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    OutputFile = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AtelierCreations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AtelierCreations_AtelierSounds_SoundId",
                        column: x => x.SoundId,
                        principalTable: "AtelierSounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AtelierCreations_SoundId",
                table: "AtelierCreations",
                column: "SoundId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AtelierCreations");

            migrationBuilder.DropTable(
                name: "AtelierSounds");

            migrationBuilder.DropColumn(
                name: "HasLogo",
                table: "Channels");
        }
    }
}
