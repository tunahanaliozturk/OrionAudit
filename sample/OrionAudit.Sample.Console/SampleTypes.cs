using Microsoft.EntityFrameworkCore;
using OrionAudit;

namespace OrionAudit.Sample;

[Auditable]
public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Status { get; set; } = "New";
    public decimal Total { get; set; }
}

public sealed class SampleDb : DbContext
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public SampleDb(DbContextOptions<SampleDb> options) : base(options) { }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().HasKey(o => o.Id);
        modelBuilder.ApplyOrionAuditConfigurations();
    }
}

public sealed class DemoUserResolver : IAuditUserResolver
{
    public AuditUser? Resolve(IServiceProvider serviceProvider) => new("demo-user", "Demo User");
}
