using CheckPay.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CheckPay.Infrastructure.Data.Configurations;

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.CustomerCode).HasColumnName("customer_code").HasMaxLength(50).IsRequired();
        builder.Property(e => e.CustomerName).HasColumnName("customer_name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
        // 必须 ValueGeneratedNever：否则 IsAuthorized=false 可能与 CLR/模型默认值对齐而被 INSERT 省略，
        // 旧库 is_authorized 列若仍为 DB DEFAULT true，新行会错误显示为已授权。
        builder.Property(e => e.IsAuthorized)
            .HasColumnName("is_authorized")
            .IsRequired()
            .ValueGeneratedNever()
            .HasDefaultValue(false);
        builder.Property(e => e.ExpectedBankName).HasColumnName("expected_bank_name").HasMaxLength(200);
        builder.Property(e => e.ExpectedAccountHolderName).HasColumnName("expected_account_holder_name").HasMaxLength(300);
        builder.Property(e => e.ExpectedCompanyName).HasColumnName("expected_company_name").HasMaxLength(300);
        builder.Property(e => e.ExpectedAccountAddress).HasColumnName("expected_account_address").HasMaxLength(500);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        // 仅对未软删除行保证账号唯一；已删除行不参与唯一性，避免删客户后无法再以同账号建档
        builder.HasIndex(e => e.CustomerCode)
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ix_customers_customer_code");
    }
}
