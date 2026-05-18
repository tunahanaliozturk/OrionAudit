using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionAudit;
using OrionAudit.Testing;

namespace OrionAudit.IntegrationTests;

public class MultiTenantIsolationTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Text { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    [Fact]
    public async Task AuditFor_FiltersToCurrentTenant_AutomaticallyAcrossWrites()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var resolver = new InMemoryAuditTenantResolver();

        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Note>());
        services.AddSingleton(connection);
        services.AddSingleton<IAuditTenantResolver>(resolver);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));

        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
        }

        // Tenant A writes one note
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Notes.Add(new Note { Text = "Alpha" });
            await ctx.SaveChangesAsync();
        }

        // Tenant B writes one note
        resolver.TenantId = "tenant-B";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Notes.Add(new Note { Text = "Beta" });
            await ctx.SaveChangesAsync();
        }

        // Tenant A reads — should see only their audit row
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var logs = await ctx.AuditFor<Note>().ToListAsync();
            Assert.Single(logs);
            Assert.Equal("tenant-A", logs[0].TenantId);
        }

        // Cross-tenant query bypasses the filter
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var logs = await ctx.AuditFor<Note>(crossTenant: true).ToListAsync();
            Assert.Equal(2, logs.Count);
        }

        await connection.DisposeAsync();
    }
}
