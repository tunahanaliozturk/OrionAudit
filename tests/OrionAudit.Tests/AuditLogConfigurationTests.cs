using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OrionAudit;

namespace OrionAudit.Tests;

public class AuditLogConfigurationTests
{
    private sealed class TestContext : DbContext
    {
        public DbSet<AuditLog> Logs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration("MyAuditLog"));
        }
    }

    [Fact]
    public void AuditLog_TableNameIsCustomizable()
    {
        var opts = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var ctx = new TestContext(opts);
        var entity = ctx.Model.FindEntityType(typeof(AuditLog))!;
        Assert.Equal("MyAuditLog", entity.GetTableName());
    }

    [Fact]
    public void AuditLog_HasExpectedColumns()
    {
        var opts = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var ctx = new TestContext(opts);
        var entity = ctx.Model.FindEntityType(typeof(AuditLog))!;

        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Id));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.EntityType));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.EntityId));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Action));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.OccurredOnUtc));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.UserId));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.UserDisplay));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.UserType));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.TenantId));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.CorrelationId));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Diff));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Snapshot));
        Assert.Contains(entity.GetProperties(), p => p.Name == nameof(AuditLog.Error));
    }
}
