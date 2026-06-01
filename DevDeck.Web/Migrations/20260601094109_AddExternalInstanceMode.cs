using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevDeck.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalInstanceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExternalPort",
                table: "DevServices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseExternalInstance",
                table: "DevServices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalPort",
                table: "DevServices");

            migrationBuilder.DropColumn(
                name: "UseExternalInstance",
                table: "DevServices");
        }
    }
}
