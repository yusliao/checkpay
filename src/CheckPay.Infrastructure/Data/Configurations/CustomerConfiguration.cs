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
        builder.Property(e => e.ExpectedRoutingNumber)
            .HasColumnName("expected_routing_number")
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(e => e.CustomerName).HasColumnName("customer_name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.MobilePhone).HasColumnName("mobile_phone").HasMaxLength(30).IsRequired();
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
        builder.Property(e => e.ExpectedAccountType).HasColumnName("expected_account_type").HasMaxLength(80);
        builder.Property(e => e.ExpectedPayToOrderOf).HasColumnName("expected_pay_to_order_of").HasMaxLength(300);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        // 未软删除行：(客户账号, ABA 路由号) 唯一；路由为空字符串时仍为一条合法组合（旧数据兼容）
        builder.HasIndex(e => new { e.CustomerCode, e.ExpectedRoutingNumber })
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ix_customers_customer_code_expected_routing_number");
    }
}
