using System.Text.Json;
using CheckPay.Domain.Entities;
using CheckPay.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CheckPay.Infrastructure.Data.Configurations;

public class OcrResultConfiguration : IEntityTypeConfiguration<OcrResult>
{
    public void Configure(EntityTypeBuilder<OcrResult> builder)
    {
        builder.ToTable("ocr_results");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.ImageUrl).HasColumnName("image_url").HasMaxLength(500).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(e => e.RawResult).HasColumnName("raw_result").HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v));

        builder.Property(e => e.ConfidenceScores).HasColumnName("confidence_scores").HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v));

        builder.Property(e => e.ErrorMessage).HasColumnName("error_message");
        builder.Property(e => e.RetryCount).HasColumnName("retry_count").HasDefaultValue(0).IsRequired();
        builder.Property(e => e.AmountValidationStatus).HasColumnName("amount_validation_status").HasConversion<string>()
            .HasMaxLength(20).HasDefaultValue(AmountValidationStatus.Pending).IsRequired();
        builder.Property(e => e.AmountValidationResult).HasColumnName("amount_validation_result").HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v));
        builder.Property(e => e.AmountValidationErrorMessage).HasColumnName("amount_validation_error_message");
        builder.Property(e => e.AmountValidatedAt).HasColumnName("amount_validated_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // 历史 Azure 并行列（保留 jsonb，新任务不再写入）
        builder.Property(e => e.AzureStatus).HasColumnName("azure_status").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(OcrStatus.Pending).IsRequired();

        builder.Property(e => e.AzureRawResult).HasColumnName("azure_raw_result").HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v));

        builder.Property(e => e.AzureConfidenceScores).HasColumnName("azure_confidence_scores").HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v));

        builder.Property(e => e.AzureErrorMessage).HasColumnName("azure_error_message");

        builder.HasIndex(e => e.Status).HasDatabaseName("ix_ocr_results_status");
    }
}
