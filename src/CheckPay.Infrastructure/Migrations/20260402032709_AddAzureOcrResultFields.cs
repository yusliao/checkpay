using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAzureOcrResultFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "azure_confidence_scores",
                table: "ocr_results",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "azure_error_message",
                table: "ocr_results",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "azure_raw_result",
                table: "ocr_results",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "azure_status",
                table: "ocr_results",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "azure_confidence_scores",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "azure_error_message",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "azure_raw_result",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "azure_status",
                table: "ocr_results");
        }
    }
}
