using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CheckPay.Infrastructure.Data.Configurations;

public class DebitRecordConfiguration : IEntityTypeConfiguration<DebitRecord>
{
    public void Configure(EntityTypeBuilder<DebitRecord> builder)
    {
        builder.ToTable("debit_records");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(e => e.CheckNumber).HasColumnName("check_number").HasMaxLength(50).IsRequired();
        builder.Property(e => e.DebitAmount).HasColumnName("debit_amount").HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(e => e.DebitDate).HasColumnName("debit_date").IsRequired();
        builder.Property(e => e.BankReference).HasColumnName("bank_reference").HasMaxLength(100).IsRequired();
        builder.Property(e => e.DebitStatus).HasColumnName("debit_status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.ScanImageUrl).HasColumnName("scan_image_url").HasMaxLength(500);
        builder.Property(e => e.CheckRecordId).HasColumnName("check_record_id");
        builder.Property(e => e.Notes).HasColumnName("notes");
        builder.Property(e => e.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken()
            .Metadata.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => e.CustomerId).HasDatabaseName("ix_debit_records_customer_id");
        builder.HasIndex(e => e.CheckNumber).HasDatabaseName("ix_debit_records_check_number");
        builder.HasIndex(e => e.DebitStatus).HasDatabaseName("ix_debit_records_debit_status");

        builder.HasOne(e => e.Customer).WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.CheckRecord).WithOne(c => c.DebitRecord).HasForeignKey<DebitRecord>(e => e.CheckRecordId).OnDelete(DeleteBehavior.SetNull);
    }
}
