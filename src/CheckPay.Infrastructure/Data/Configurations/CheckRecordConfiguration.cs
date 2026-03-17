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
        builder.Property(e => e.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken()
            .Metadata.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => e.CheckNumber).IsUnique().HasDatabaseName("ix_check_records_check_number");
        builder.HasIndex(e => e.CustomerId).HasDatabaseName("ix_check_records_customer_id");
        builder.HasIndex(e => e.Status).HasDatabaseName("ix_check_records_status");

        builder.HasOne(e => e.Customer).WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.OcrResult).WithMany().HasForeignKey(e => e.OcrResultId).OnDelete(DeleteBehavior.SetNull);
    }
}
