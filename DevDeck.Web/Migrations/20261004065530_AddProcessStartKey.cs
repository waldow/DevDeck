using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevDeck.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessStartKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ProcessStartKey",
                table: "ServiceRuns",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessStartKey",
                table: "ServiceRuns");
        }
    }
}
