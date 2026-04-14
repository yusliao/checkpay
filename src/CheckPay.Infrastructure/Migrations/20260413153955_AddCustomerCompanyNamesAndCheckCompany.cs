using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerCompanyNamesAndCheckCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "company_name",
                table: "check_records",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "customer_company_new_relationship_warning",
                table: "check_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "customer_company_names",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_company_names", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_company_names_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_company_names_customer_company",
                table: "customer_company_names",
                columns: new[] { "customer_id", "company_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_company_names_customer_id",
                table: "customer_company_names",
                column: "customer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_company_names");

            migrationBuilder.DropColumn(
                name: "company_name",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "customer_company_new_relationship_warning",
                table: "check_records");
        }
    }
}
