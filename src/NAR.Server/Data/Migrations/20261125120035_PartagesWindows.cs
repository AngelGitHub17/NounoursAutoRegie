using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NAR.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartagesWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastScanError",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbDomain",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbHost",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbPasswordProtected",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbShare",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbSubPath",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmbUser",
                table: "MediaFolders",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "MediaFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastScanError",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "SmbDomain",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "SmbHost",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "SmbPasswordProtected",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "SmbShare",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "SmbSubPath",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "SmbUser",
                table: "MediaFolders");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "MediaFolders");
        }
    }
}
