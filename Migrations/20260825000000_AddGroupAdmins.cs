using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatApp.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupAdmins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows become ordinary members: the creator's authority
            // comes from Groups.CreatorUserName, not from this column, so
            // nobody loses anything by defaulting it to false.
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "UserGroups",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsAdmin", table: "UserGroups");
        }
    }
}
