using CheckPay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CheckPay.Infrastructure.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260429083000_NormalizeCheckNumberUniqueIndex")]
public class NormalizeCheckNumberUniqueIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS ix_check_records_check_number;
            """);

        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX ix_check_records_check_number
            ON check_records (upper(btrim(check_number)), coalesce(btrim(routing_number), ''));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS ix_check_records_check_number;
            """);

        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX ix_check_records_check_number
            ON check_records (check_number, routing_number);
            """);
    }
}
