using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Sample;

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
