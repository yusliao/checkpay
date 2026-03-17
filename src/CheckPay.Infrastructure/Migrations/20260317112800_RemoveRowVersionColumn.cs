using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRowVersionColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "row_version",
                table: "debit_records");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "check_records");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "row_version",
                table: "debit_records",
                type: "xid",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<uint>(
                name: "row_version",
                table: "check_records",
                type: "xid",
                rowVersion: true,
                nullable: false);
        }
    }
}
