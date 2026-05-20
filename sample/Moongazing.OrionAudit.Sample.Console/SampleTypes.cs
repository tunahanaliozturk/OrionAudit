using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Sample;

/// <summary>
/// The v0.3 source-gen registration module. The generator emits
/// <c>RegisterAuditedTypes(AuditConfigurationBuilder)</c> and an <c>AuditedTypeNames</c> list
/// on this partial class — the trim-safe / AOT-clean way to register audited types.
/// </summary>
[OrionAuditModule]
public partial class SampleAuditModule { }

/// <summary>
/// Hand-written System.Text.Json source-generated context covering every audited entity, so
/// the snapshot builder and reconstructor never fall back to reflection — required for the
/// Native AOT publish to be warning-free.
/// </summary>
[JsonSerializable(typeof(Customer))]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(Article))]
public partial class SampleJsonContext : JsonSerializerContext { }

[Auditable]
public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    // Email and ApiKey are configured via fluent rules in Program.cs (Hash / Redact).
    public string Email { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

[Auditable]
public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CustomerName { get; set; } = "";
    public string Status { get; set; } = "New";
    public decimal Total { get; set; }

    [NotAuditable]
    public string InternalNote { get; set; } = "";
}

public sealed class ShopDb : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public ShopDb(DbContextOptions<ShopDb> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().HasKey(c => c.Id);
        modelBuilder.Entity<Order>().HasKey(o => o.Id);
        modelBuilder.ApplyOrionAuditConfigurations();
    }
}

[Auditable]
[SoftDelete(nameof(IsDeleted))]
public sealed class Article
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public bool IsDeleted { get; set; }
}

public sealed class SoftDeleteDb : DbContext
{
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public SoftDeleteDb(DbContextOptions<SoftDeleteDb> options) : base(options) { }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Article>().HasKey(a => a.Id);
        modelBuilder.ApplyOrionAuditConfigurations();
    }
}
