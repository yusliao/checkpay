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
        builder.Property(e => e.ExpectedBankName).HasColumnName("expected_bank_name").HasMaxLength(200);
        builder.Property(e => e.ExpectedAccountHolderName).HasColumnName("expected_account_holder_name").HasMaxLength(300);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => e.CustomerCode).IsUnique().HasDatabaseName("ix_customers_customer_code");
    }
}
