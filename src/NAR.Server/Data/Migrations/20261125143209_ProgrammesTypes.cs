using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NAR.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProgrammesTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProgrammeTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgrammeTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeriesProgress",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    LastAssetId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesProgress", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProgrammeTypeBlocs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProgrammeTypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    AtelierKind = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgrammeTypeBlocs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProgrammeTypeBlocs_MediaAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "MediaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProgrammeTypeBlocs_ProgrammeTypes_ProgrammeTypeId",
                        column: x => x.ProgrammeTypeId,
                        principalTable: "ProgrammeTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProgrammeTypeBlocs_AssetId",
                table: "ProgrammeTypeBlocs",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_ProgrammeTypeBlocs_ProgrammeTypeId",
                table: "ProgrammeTypeBlocs",
                column: "ProgrammeTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesProgress_ChannelId_SeriesName",
                table: "SeriesProgress",
                columns: new[] { "ChannelId", "SeriesName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProgrammeTypeBlocs");

            migrationBuilder.DropTable(
                name: "SeriesProgress");

            migrationBuilder.DropTable(
                name: "ProgrammeTypes");
        }
    }
}
