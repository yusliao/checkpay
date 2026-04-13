using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAchCheckFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correct_ach_extension_json",
                table: "ocr_training_samples",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ocr_ach_extension_json",
                table: "ocr_training_samples",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "expected_account_holder_name",
                table: "customers",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "expected_bank_name",
                table: "customers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "account_address",
                table: "check_records",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "account_holder_name",
                table: "check_records",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "account_number",
                table: "check_records",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "account_type",
                table: "check_records",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ach_debit_succeeded",
                table: "check_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ach_debit_succeeded_at",
                table: "check_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bank_name",
                table: "check_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "check_number_micr",
                table: "check_records",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "customer_master_mismatch_warning",
                table: "check_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "for_memo",
                table: "check_records",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoice_numbers",
                table: "check_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "micr_line_raw",
                table: "check_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pay_to_order_of",
                table: "check_records",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_period_text",
                table: "check_records",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_number",
                table: "check_records",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "submitted_at",
                table: "check_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_check_records_ach_debit_succeeded",
                table: "check_records",
                column: "ach_debit_succeeded");

            migrationBuilder.CreateIndex(
                name: "ix_check_records_submitted_at",
                table: "check_records",
                column: "submitted_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_check_records_ach_debit_succeeded",
                table: "check_records");

            migrationBuilder.DropIndex(
                name: "ix_check_records_submitted_at",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "correct_ach_extension_json",
                table: "ocr_training_samples");

            migrationBuilder.DropColumn(
                name: "ocr_ach_extension_json",
                table: "ocr_training_samples");

            migrationBuilder.DropColumn(
                name: "expected_account_holder_name",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "expected_bank_name",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "account_address",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "account_holder_name",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "account_number",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "account_type",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "ach_debit_succeeded",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "ach_debit_succeeded_at",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "bank_name",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "check_number_micr",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "customer_master_mismatch_warning",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "for_memo",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "invoice_numbers",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "micr_line_raw",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "pay_to_order_of",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "payment_period_text",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "routing_number",
                table: "check_records");

            migrationBuilder.DropColumn(
                name: "submitted_at",
                table: "check_records");
        }
    }
}
