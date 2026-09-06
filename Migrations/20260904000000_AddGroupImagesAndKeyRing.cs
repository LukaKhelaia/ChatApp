using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ChatApp.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupImagesAndKeyRing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Group pictures used to be files under wwwroot. A container's disk
            // is rebuilt on every deploy, so they did not survive one. Same
            // treatment the message attachments already get: bytea.
            migrationBuilder.CreateTable(
                name: "GroupImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupImages", x => x.Id);
                });

            // The ASP.NET Data Protection key ring. Framework-owned schema -
            // these three columns are what IDataProtectionKeyContext expects,
            // so do not rename them.
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            // Retire the URLs of the pictures that used to live on disk. Those
            // files were never in the container image (.gitignore kept them out
            // of the repo), so the rows pointing at them would render as broken
            // images forever. Null is what the UI already treats as "no
            // picture" and falls back to the default group icon.
            migrationBuilder.Sql(
                "UPDATE \"Groups\" SET \"ImageUrl\" = NULL WHERE \"ImageUrl\" LIKE '/group-images/%.%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupImages");
            migrationBuilder.DropTable(name: "DataProtectionKeys");
        }
    }
}
