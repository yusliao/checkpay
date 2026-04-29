using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class __PendingModelProbe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_check_records_check_number",
                table: "check_records");

            migrationBuilder.CreateIndex(
                name: "ix_check_records_check_number",
                table: "check_records",
                columns: new[] { "check_number", "routing_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_check_records_check_number",
                table: "check_records");

            migrationBuilder.CreateIndex(
                name: "ix_check_records_check_number",
                table: "check_records",
                column: "check_number",
                unique: true);
        }
    }
}
