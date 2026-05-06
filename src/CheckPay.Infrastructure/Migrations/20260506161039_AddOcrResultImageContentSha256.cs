using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrResultImageContentSha256 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "image_content_sha256",
                table: "ocr_results",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ocr_results_image_content_sha256",
                table: "ocr_results",
                column: "image_content_sha256");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ocr_results_image_content_sha256",
                table: "ocr_results");

            migrationBuilder.DropColumn(
                name: "image_content_sha256",
                table: "ocr_results");
        }
    }
}
