using CheckPay.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CheckPay.Infrastructure.Data.Configurations;

public class OcrTrainingSampleConfiguration : IEntityTypeConfiguration<OcrTrainingSample>
{
    public void Configure(EntityTypeBuilder<OcrTrainingSample> builder)
    {
        builder.ToTable("ocr_training_samples");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.ImageUrl).HasColumnName("image_url").HasMaxLength(500).IsRequired();
        builder.Property(e => e.DocumentType).HasColumnName("document_type").HasMaxLength(20).IsRequired();
        builder.Property(e => e.OcrRawResponse).HasColumnName("ocr_raw_response").HasColumnType("text").IsRequired();

        builder.Property(e => e.OcrCheckNumber).HasColumnName("ocr_check_number").HasMaxLength(100);
        builder.Property(e => e.OcrAmount).HasColumnName("ocr_amount").HasColumnType("decimal(18,2)");
        builder.Property(e => e.OcrDate).HasColumnName("ocr_date");
        builder.Property(e => e.OcrBankReference).HasColumnName("ocr_bank_reference").HasMaxLength(200);

        builder.Property(e => e.CorrectCheckNumber).HasColumnName("correct_check_number").HasMaxLength(100);
        builder.Property(e => e.CorrectAmount).HasColumnName("correct_amount").HasColumnType("decimal(18,2)");
        builder.Property(e => e.CorrectDate).HasColumnName("correct_date");
        builder.Property(e => e.CorrectBankReference).HasColumnName("correct_bank_reference").HasMaxLength(200);

        builder.Property(e => e.Notes).HasColumnName("notes").HasColumnType("text");
        builder.Property(e => e.OcrAchExtensionJson).HasColumnName("ocr_ach_extension_json").HasColumnType("text");
        builder.Property(e => e.CorrectAchExtensionJson).HasColumnName("correct_ach_extension_json").HasColumnType("text");
        builder.Property(e => e.OcrCheckTemplateId).HasColumnName("ocr_check_template_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne(e => e.OcrCheckTemplate)
            .WithMany()
            .HasForeignKey(e => e.OcrCheckTemplateId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.DocumentType).HasDatabaseName("ix_ocr_training_samples_document_type");
        builder.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_ocr_training_samples_created_at");
    }
}
