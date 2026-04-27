using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrCheckTemplatesAndTrainingTemplateLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ocr_check_template_id",
                table: "ocr_training_samples",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ocr_check_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    routing_prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    bank_name_keywords = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    parsing_profile_json = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ocr_check_templates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ocr_training_samples_ocr_check_template_id",
                table: "ocr_training_samples",
                column: "ocr_check_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_ocr_check_templates_active_sort",
                table: "ocr_check_templates",
                columns: new[] { "is_active", "sort_order" });

            migrationBuilder.AddForeignKey(
                name: "FK_ocr_training_samples_ocr_check_templates_ocr_check_template~",
                table: "ocr_training_samples",
                column: "ocr_check_template_id",
                principalTable: "ocr_check_templates",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ocr_training_samples_ocr_check_templates_ocr_check_template~",
                table: "ocr_training_samples");

            migrationBuilder.DropTable(
                name: "ocr_check_templates");

            migrationBuilder.DropIndex(
                name: "IX_ocr_training_samples_ocr_check_template_id",
                table: "ocr_training_samples");

            migrationBuilder.DropColumn(
                name: "ocr_check_template_id",
                table: "ocr_training_samples");
        }
    }
}
