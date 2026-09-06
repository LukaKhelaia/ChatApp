using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ChatApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReplyToMessageId",
                table: "Messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplyToMessageId",
                table: "GroupMessages",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Reactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    Emoji = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupMessageDeletions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupMessageId = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMessageDeletions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reactions_Scope_MessageId",
                table: "Reactions",
                columns: new[] { "Scope", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Reactions_Scope_MessageId_UserName",
                table: "Reactions",
                columns: new[] { "Scope", "MessageId", "UserName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessageDeletions_GroupMessageId_UserName",
                table: "GroupMessageDeletions",
                columns: new[] { "GroupMessageId", "UserName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessageDeletions_UserName",
                table: "GroupMessageDeletions",
                column: "UserName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupMessageDeletions");
            migrationBuilder.DropTable(name: "Reactions");

            migrationBuilder.DropColumn(name: "ReplyToMessageId", table: "GroupMessages");
            migrationBuilder.DropColumn(name: "ReplyToMessageId", table: "Messages");
        }
    }
}
