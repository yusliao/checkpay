using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrTrainingSample : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ocr_training_samples",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    image_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    document_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ocr_raw_response = table.Column<string>(type: "text", nullable: false),
                    ocr_check_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ocr_amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ocr_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ocr_bank_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    correct_check_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    correct_amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    correct_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    correct_bank_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ocr_training_samples", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ocr_training_samples_created_at",
                table: "ocr_training_samples",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ocr_training_samples_document_type",
                table: "ocr_training_samples",
                column: "document_type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ocr_training_samples");
        }
    }
}
