using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CustomerCodeUniqueExcludeSoftDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_customers_customer_code",
                table: "customers");

            migrationBuilder.CreateIndex(
                name: "ix_customers_customer_code",
                table: "customers",
                column: "customer_code",
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_customers_customer_code",
                table: "customers");

            migrationBuilder.CreateIndex(
                name: "ix_customers_customer_code",
                table: "customers",
                column: "customer_code",
                unique: true);
        }
    }
}
