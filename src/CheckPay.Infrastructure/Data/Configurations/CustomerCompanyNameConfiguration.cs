using CheckPay.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CheckPay.Infrastructure.Data.Configurations;

public class CustomerCompanyNameConfiguration : IEntityTypeConfiguration<CustomerCompanyName>
{
    public void Configure(EntityTypeBuilder<CustomerCompanyName> builder)
    {
        builder.ToTable("customer_company_names");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.CustomerId).HasColumnName("customer_id").IsRequired();
        builder.Property(e => e.CompanyName).HasColumnName("company_name").HasMaxLength(300).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => new { e.CustomerId, e.CompanyName })
            .IsUnique()
            .HasDatabaseName("ix_customer_company_names_customer_company");

        builder.HasIndex(e => e.CustomerId).HasDatabaseName("ix_customer_company_names_customer_id");

        builder.HasOne(e => e.Customer)
            .WithMany(c => c.CompanyNames)
            .HasForeignKey(e => e.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
