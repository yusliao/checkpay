using CheckPay.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CheckPay.Infrastructure.Data.Configurations;

public class OcrCheckTemplateConfiguration : IEntityTypeConfiguration<OcrCheckTemplate>
{
    public void Configure(EntityTypeBuilder<OcrCheckTemplate> builder)
    {
        builder.ToTable("ocr_check_templates");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.RoutingPrefix).HasColumnName("routing_prefix").HasMaxLength(32);
        builder.Property(e => e.BankNameKeywords).HasColumnName("bank_name_keywords").HasMaxLength(500);
        builder.Property(e => e.ParsingProfileJson).HasColumnName("parsing_profile_json").HasColumnType("text");
        builder.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(e => e.SortOrder).HasColumnName("sort_order").IsRequired();

        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => new { e.IsActive, e.SortOrder }).HasDatabaseName("ix_ocr_check_templates_active_sort");
    }
}
