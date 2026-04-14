using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerIsAuthorized : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_authorized",
                table: "customers",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_authorized",
                table: "customers");
        }
    }
}
