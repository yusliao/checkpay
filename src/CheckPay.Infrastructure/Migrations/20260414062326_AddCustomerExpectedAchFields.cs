using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerExpectedAchFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "expected_account_address",
                table: "customers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "expected_account_number",
                table: "customers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "expected_company_name",
                table: "customers",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expected_account_address",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "expected_account_number",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "expected_company_name",
                table: "customers");
        }
    }
}
