using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CheckPay.Infrastructure.Data.Configurations;

public class CheckRecordConfiguration : IEntityTypeConfiguration<CheckRecord>
{
    public void Configure(EntityTypeBuilder<CheckRecord> builder)
    {
        builder.ToTable("check_records");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.CheckNumber).HasColumnName("check_number").HasMaxLength(50).IsRequired();
        builder.Property(e => e.CheckAmount).HasColumnName("check_amount").HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(e => e.CheckDate).HasColumnName("check_date").IsRequired();
        builder.Property(e => e.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.ImageUrl).HasColumnName("image_url").HasMaxLength(500);
        builder.Property(e => e.OcrResultId).HasColumnName("ocr_result_id");
        builder.Property(e => e.Notes).HasColumnName("notes");
        builder.Property(e => e.BankName).HasColumnName("bank_name").HasMaxLength(200);
        builder.Property(e => e.RoutingNumber).HasColumnName("routing_number").HasMaxLength(20);
        builder.Property(e => e.AccountNumber).HasColumnName("account_number").HasMaxLength(50);
        builder.Property(e => e.AccountType).HasColumnName("account_type").HasMaxLength(80);
        builder.Property(e => e.AccountHolderName).HasColumnName("account_holder_name").HasMaxLength(300);
        builder.Property(e => e.AccountAddress).HasColumnName("account_address").HasMaxLength(500);
        builder.Property(e => e.PayToOrderOf).HasColumnName("pay_to_order_of").HasMaxLength(300);
        builder.Property(e => e.ForMemo).HasColumnName("for_memo").HasMaxLength(500);
        builder.Property(e => e.MicrLineRaw).HasColumnName("micr_line_raw").HasColumnType("text");
        builder.Property(e => e.CheckNumberMicr).HasColumnName("check_number_micr").HasMaxLength(50);
        builder.Property(e => e.CompanyName).HasColumnName("company_name").HasMaxLength(300);
        builder.Property(e => e.CustomerCompanyNewRelationshipWarning)
            .HasColumnName("customer_company_new_relationship_warning")
            .IsRequired();
        builder.Property(e => e.InvoiceNumbers).HasColumnName("invoice_numbers").HasColumnType("text");
        builder.Property(e => e.PaymentPeriodText).HasColumnName("payment_period_text").HasMaxLength(50);
        builder.Property(e => e.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(e => e.CustomerMasterMismatchWarning).HasColumnName("customer_master_mismatch_warning").IsRequired();
        builder.Property(e => e.AchDebitSucceeded).HasColumnName("ach_debit_succeeded").IsRequired();
        builder.Property(e => e.AchDebitSucceededAt).HasColumnName("ach_debit_succeeded_at");
        builder.Property(e => e.AchDebitSuccessRevokedAt).HasColumnName("ach_debit_success_revoked_at");
        builder.Property(e => e.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken()
            .Metadata.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => new { e.CheckNumber, e.RoutingNumber })
            .IsUnique()
            .HasDatabaseName("ix_check_records_check_number")
            .HasFilter("deleted_at IS NULL");
        builder.HasIndex(e => e.CustomerId).HasDatabaseName("ix_check_records_customer_id");
        builder.HasIndex(e => e.Status).HasDatabaseName("ix_check_records_status");
        builder.HasIndex(e => e.SubmittedAt).HasDatabaseName("ix_check_records_submitted_at");
        builder.HasIndex(e => e.AchDebitSucceeded).HasDatabaseName("ix_check_records_ach_debit_succeeded");

        builder.HasOne(e => e.Customer).WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.OcrResult).WithMany().HasForeignKey(e => e.OcrResultId).OnDelete(DeleteBehavior.SetNull);
    }
}
