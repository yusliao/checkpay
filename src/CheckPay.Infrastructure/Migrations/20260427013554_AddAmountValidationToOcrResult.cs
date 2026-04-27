using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAmountValidationToOcrResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "amount_validated_at",
                table: "ocr_results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "amount_validation_error_message",
                table: "ocr_results",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "amount_validation_result",
                table: "ocr_results",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "amount_validation_status",
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
                name: "amount_validated_at",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "amount_validation_error_message",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "amount_validation_result",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "amount_validation_status",
                table: "ocr_results");
        }
    }
}
