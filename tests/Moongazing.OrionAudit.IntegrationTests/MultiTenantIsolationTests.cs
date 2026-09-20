using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Testing;

namespace Moongazing.OrionAudit.IntegrationTests;

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

    /// <summary>
    /// One entity id whose history is split across two tenants: tenant A owns the Insert, tenant B
    /// owns the Update. The reconstructor used to query the audit table unfiltered, so it replayed
    /// BOTH tenants' rows into one object - a cross-tenant disclosure, and an object that never
    /// existed in either tenant. Each tenant must now see only its own rows.
    /// </summary>
    [Fact]
    public async Task ReconstructAsync_ReplaysOnlyTheCallingTenantsRows()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var resolver = new InMemoryAuditTenantResolver();
        await using var sp = BuildProvider(connection, resolver);
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        }

        var noteId = Guid.NewGuid();

        // Tenant A inserts the note.
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Notes.Add(new Note { Id = noteId, Text = "A-secret" });
            await ctx.SaveChangesAsync();
        }

        // Tenant B updates the same row, producing an Update audit row stamped tenant-B.
        resolver.TenantId = "tenant-B";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var note = await ctx.Notes.SingleAsync(n => n.Id == noteId);
            note.Text = "B-secret";
            await ctx.SaveChangesAsync();
        }

        // Fixture check: the split history really is two rows, one per tenant.
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var all = await ctx.AuditFor<Note>(crossTenant: true).ToListAsync();
            Assert.Equal(2, all.Count);
            Assert.Equal(
                new[] { "tenant-A", "tenant-B" },
                all.Select(a => a.TenantId).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        }

        // Tenant A reconstructs: its own Insert only. Before the fix this returned "B-secret".
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
            var note = await reconstructor.ReconstructAsync<Note>(noteId.ToString(), DateTime.UtcNow.AddMinutes(1));
            Assert.NotNull(note);
            Assert.Equal("A-secret", note.Text);

            var many = await reconstructor.ReconstructManyAsync<Note>([noteId.ToString()], DateTime.UtcNow.AddMinutes(1));
            Assert.Equal("A-secret", many[noteId.ToString()]?.Text);
        }

        // Tenant B sees only its Update - a history that does not start with an Insert, which is
        // a corrupt-history error rather than a silent replay of tenant A's state.
        resolver.TenantId = "tenant-B";
        await using (var scope = sp.CreateAsyncScope())
        {
            var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
            await Assert.ThrowsAsync<OrionAuditException>(
                () => reconstructor.ReconstructAsync<Note>(noteId.ToString(), DateTime.UtcNow.AddMinutes(1)));
        }

        // A third tenant that never touched the note sees nothing at all.
        resolver.TenantId = "tenant-C";
        await using (var scope = sp.CreateAsyncScope())
        {
            var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
            Assert.Null(await reconstructor.ReconstructAsync<Note>(noteId.ToString(), DateTime.UtcNow.AddMinutes(1)));
        }

        await connection.DisposeAsync();
    }

    /// <summary>
    /// Pins the reconstructor to the same unresolved-tenant semantics as the query extensions: a
    /// registered resolver that cannot name a tenant DENIES (scopes to the no-tenant stream)
    /// instead of failing open onto every tenant's rows.
    /// </summary>
    [Fact]
    public async Task ReconstructAsync_WithUnresolvedTenant_DeniesInsteadOfFailingOpen()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var resolver = new InMemoryAuditTenantResolver();
        await using var sp = BuildProvider(connection, resolver);
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        }

        var noteId = Guid.NewGuid();
        resolver.TenantId = "tenant-A";
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Notes.Add(new Note { Id = noteId, Text = "A-secret" });
            await ctx.SaveChangesAsync();
        }

        // Resolver registered, but it cannot name a tenant for this call.
        resolver.TenantId = null;
        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            Assert.Empty(await ctx.AuditFor<Note>().ToListAsync());

            var reconstructor = scope.ServiceProvider.GetRequiredService<IAuditReconstructor>();
            Assert.Null(await reconstructor.ReconstructAsync<Note>(noteId.ToString(), DateTime.UtcNow.AddMinutes(1)));

            var many = await reconstructor.ReconstructManyAsync<Note>([noteId.ToString()], DateTime.UtcNow.AddMinutes(1));
            Assert.Null(many[noteId.ToString()]);
        }

        await connection.DisposeAsync();
    }

    private static ServiceProvider BuildProvider(SqliteConnection connection, IAuditTenantResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Note>());
        services.AddSingleton(connection);
        services.AddSingleton(resolver);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        return services.BuildServiceProvider();
    }
}
