using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NAR.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Recursive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultKind = table.Column<int>(type: "INTEGER", nullable: false),
                    LastScanAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastScanCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FolderId = table.Column<int>(type: "INTEGER", nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    FileModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    FrameRate = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    VideoCodec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AudioCodec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    HasAudio = table.Column<bool>(type: "INTEGER", nullable: false),
                    LoudnessLufs = table.Column<double>(type: "REAL", nullable: true),
                    GainDb = table.Column<double>(type: "REAL", nullable: false),
                    HasThumbnail = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AnalyzedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaAssets_MediaFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "MediaFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    FrameRate = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    VideoEncoder = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EncoderPreset = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    VideoBitrateKbps = table.Column<int>(type: "INTEGER", nullable: false),
                    AudioBitrateKbps = table.Column<int>(type: "INTEGER", nullable: false),
                    SegmentSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    EnginePort = table.Column<int>(type: "INTEGER", nullable: false),
                    PreloadSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Loop = table.Column<bool>(type: "INTEGER", nullable: false),
                    FillerAssetId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Channels_MediaAssets_FillerAssetId",
                        column: x => x.FillerAssetId,
                        principalTable: "MediaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    TitleOverride = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    StartMode = table.Column<int>(type: "INTEGER", nullable: false),
                    FixedStart = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    InPoint = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    OutPoint = table.Column<TimeSpan>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistEntries_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaylistEntries_MediaAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "MediaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_FillerAssetId",
                table: "Channels",
                column: "FillerAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_Number",
                table: "Channels",
                column: "Number");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_FolderId",
                table: "MediaAssets",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_Kind",
                table: "MediaAssets",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_Path",
                table: "MediaAssets",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_Status",
                table: "MediaAssets",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFolders_Path",
                table: "MediaFolders",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_AssetId",
                table: "PlaylistEntries",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_ChannelId_Position",
                table: "PlaylistEntries",
                columns: new[] { "ChannelId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistEntries_Key",
                table: "PlaylistEntries",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaylistEntries");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "MediaAssets");

            migrationBuilder.DropTable(
                name: "MediaFolders");
        }
    }
}
