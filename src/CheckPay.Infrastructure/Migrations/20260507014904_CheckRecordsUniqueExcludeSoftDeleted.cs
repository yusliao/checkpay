using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CheckPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CheckRecordsUniqueExcludeSoftDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_check_records_check_number;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ix_check_records_check_number
                ON check_records (upper(btrim(check_number)), coalesce(btrim(routing_number), ''))
                WHERE deleted_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_check_records_check_number;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ix_check_records_check_number
                ON check_records (upper(btrim(check_number)), coalesce(btrim(routing_number), ''));
                """);
        }
    }
}
