using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropCustomerExpectedAccountNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expected_account_number",
                table: "customers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "expected_account_number",
                table: "customers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
