using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// "Who did this" under the registrations where the provider captured by <c>UseOrionAudit(sp)</c> is
/// NOT the request scope. With <c>AddDbContextPool</c> / <c>AddDbContextFactory</c> EF Core builds
/// <c>DbContextOptions</c> once, from the root provider, so a scoped resolver pulled out of it is the
/// first request's instance forever and every row was stamped with the first request's actor.
/// </summary>
public class PooledContextAttributionTests : IAsyncLifetime
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

    /// <summary>Per-request principal, registered scoped — one instance per request scope.</summary>
    private sealed class CurrentPrincipal
    {
        public string UserId { get; set; } = "";
        public string TenantId { get; set; } = "";
    }

    // Scoped resolvers that capture the principal of the scope they were constructed in. This is the
    // shape that misattributes: resolved once from the root provider, they keep answering with the
    // first request's principal no matter who is saving.
    private sealed class ScopedPrincipalUserResolver : IAuditUserResolver
    {
        private readonly CurrentPrincipal principal;
        public ScopedPrincipalUserResolver(CurrentPrincipal principal) => this.principal = principal;
        public AuditUser? Resolve(IServiceProvider serviceProvider) => new AuditUser(principal.UserId);
    }

    private sealed class ScopedPrincipalTenantResolver : IAuditTenantResolver
    {
        private readonly CurrentPrincipal principal;
        public ScopedPrincipalTenantResolver(CurrentPrincipal principal) => this.principal = principal;
        public string? Resolve(IServiceProvider serviceProvider) => principal.TenantId;
    }

    private SqliteConnection connection = null!;

    public async ValueTask InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    // AuditLog.Id is a Guid, so rows come back unordered: assert the actor->tenant pairing per row
    // instead of a sequence. That is the promise under test - every row carries ITS OWN actor.
    private static void AssertOneRowPerActor(List<AuditLog> rows)
    {
        Assert.Equal(3, rows.Count);
        var byUser = rows.ToDictionary(r => r.UserId!, r => r.TenantId, StringComparer.Ordinal);
        Assert.Equal("t-1", byUser["alice"]);
        Assert.Equal("t-2", byUser["bob"]);
        Assert.Equal("t-3", byUser["carol"]);
    }

    private ServiceCollection BaseServices(bool withResolvers)
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Note>());
        services.AddSingleton(connection);
        if (withResolvers)
        {
            services.AddScoped<CurrentPrincipal>();
            services.AddScoped<IAuditUserResolver, ScopedPrincipalUserResolver>();
            services.AddScoped<IAuditTenantResolver, ScopedPrincipalTenantResolver>();
        }
        return services;
    }

    // ValidateScopes is the setting that turns this class of bug from silent into loud: on (the
    // ASP.NET Core Development default) a scoped service resolved from the root throws; off (the
    // Production default) it quietly hands back the root-cached instance. These tests default to
    // ON so a fix that only works because scope validation is off cannot pass, and one test below
    // deliberately runs with it OFF because that is the configuration the wrong trail came from.
    private static ServiceProvider Build(IServiceCollection services, bool validateScopes = true) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });

    private static async Task CreateSchemaAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        using (AuditScope.PushServices(scope.ServiceProvider))
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
        }
    }

    private static async Task<List<AuditLog>> ReadAuditRowsAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        using (AuditScope.PushServices(scope.ServiceProvider))
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            return await ctx.AuditLogs.ToListAsync();
        }
    }

    [Fact]
    public async Task PooledContext_WithAmbientRequestScope_AttributesEachRequestToItsOwnActor()
    {
        var services = BaseServices(withResolvers: true);
        services.AddDbContextPool<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var provider = Build(services);
        await CreateSchemaAsync(provider);

        foreach (var (user, tenant) in new[] { ("alice", "t-1"), ("bob", "t-2"), ("carol", "t-3") })
        {
            await using var scope = provider.CreateAsyncScope();
            // The one line a pooled consumer adds: middleware pushing the request scope.
            using (AuditScope.PushServices(scope.ServiceProvider))
            {
                var principal = scope.ServiceProvider.GetRequiredService<CurrentPrincipal>();
                principal.UserId = user;
                principal.TenantId = tenant;

                var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
                ctx.Notes.Add(new Note { Text = $"note by {user}" });
                await ctx.SaveChangesAsync();
            }
        }

        // Before the fix all three read "alice" / "t-1" — the first request's scoped resolver,
        // cached on the root provider and reused for the life of the process.
        AssertOneRowPerActor(await ReadAuditRowsAsync(provider));
    }

    [Fact]
    public async Task PooledContext_WithScopeValidationOff_StillAttributesEachRequestToItsOwnActor()
    {
        // The Production configuration, and the one the defect was reported from: with scope
        // validation off nothing objects to a scoped resolver coming out of the root provider, so
        // the old code wrote alice / t-1 for all three requests and the trail looked healthy.
        var services = BaseServices(withResolvers: true);
        services.AddDbContextPool<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var provider = Build(services, validateScopes: false);
        await CreateSchemaAsync(provider);

        foreach (var (user, tenant) in new[] { ("alice", "t-1"), ("bob", "t-2"), ("carol", "t-3") })
        {
            await using var scope = provider.CreateAsyncScope();
            using (AuditScope.PushServices(scope.ServiceProvider))
            {
                var principal = scope.ServiceProvider.GetRequiredService<CurrentPrincipal>();
                principal.UserId = user;
                principal.TenantId = tenant;

                var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
                ctx.Notes.Add(new Note { Text = $"note by {user}" });
                await ctx.SaveChangesAsync();
            }
        }

        AssertOneRowPerActor(await ReadAuditRowsAsync(provider));
    }

    // Both ways round: with validation on our message replaces the container's opaque "cannot
    // resolve scoped service from root provider"; with it off — Production — the refusal is the
    // only thing between the consumer and a trail that names the wrong person.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PooledContext_WithoutAmbientRequestScope_RefusesInsteadOfMisattributing(bool validateScopes)
    {
        var services = BaseServices(withResolvers: true);
        services.AddDbContextPool<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var provider = Build(services, validateScopes);
        await CreateSchemaAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var principal = scope.ServiceProvider.GetRequiredService<CurrentPrincipal>();
        principal.UserId = "alice";
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        ctx.Notes.Add(new Note { Text = "unattributable" });

        var ex = await Assert.ThrowsAsync<OrionAuditConfigurationException>(() => ctx.SaveChangesAsync());
        // The message has to name the way out, not just the problem.
        Assert.Contains("AddDbContextPool", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddDbContext<TContext>((sp, o)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AuditScope.PushServices", ex.Message, StringComparison.Ordinal);

        // Refused before anything was written: no half-audited save, no wrong row.
        Assert.Empty(await ReadAuditRowsAsync(provider));
    }

    [Fact]
    public async Task PooledContext_WithNoResolversRegistered_StillCapturesUnattributedRows()
    {
        var services = BaseServices(withResolvers: false);
        services.AddDbContextPool<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var provider = Build(services);
        await CreateSchemaAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Notes.Add(new Note { Text = "system write" });
            await ctx.SaveChangesAsync();
        }

        // Nothing claims to know the actor, so there is nothing to get wrong: the guard stays out
        // of the way and the row is captured with null attribution.
        var row = Assert.Single(await ReadAuditRowsAsync(provider));
        Assert.Null(row.UserId);
        Assert.Null(row.TenantId);
    }

    [Fact]
    public async Task DbContextFactory_WithAmbientRequestScope_AttributesEachRequestToItsOwnActor()
    {
        var services = BaseServices(withResolvers: true);
        // Same root-provider capture as pooling: optionsLifetime defaults to Singleton.
        services.AddDbContextFactory<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var provider = Build(services);

        await using (var scope = provider.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TestContext>>();
            await using var ctx = await factory.CreateDbContextAsync();
            await ctx.Database.EnsureCreatedAsync();
        }

        foreach (var (user, tenant) in new[] { ("alice", "t-1"), ("bob", "t-2"), ("carol", "t-3") })
        {
            await using var scope = provider.CreateAsyncScope();
            using (AuditScope.PushServices(scope.ServiceProvider))
            {
                var principal = scope.ServiceProvider.GetRequiredService<CurrentPrincipal>();
                principal.UserId = user;
                principal.TenantId = tenant;

                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TestContext>>();
                await using var ctx = await factory.CreateDbContextAsync();
                ctx.Notes.Add(new Note { Text = $"note by {user}" });
                await ctx.SaveChangesAsync();
            }
        }

        await using var read = provider.CreateAsyncScope();
        var readFactory = read.ServiceProvider.GetRequiredService<IDbContextFactory<TestContext>>();
        await using var readCtx = await readFactory.CreateDbContextAsync();
        AssertOneRowPerActor(await readCtx.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task PlainAddDbContext_StillAttributesEachScopeToItsOwnActor()
    {
        // The common wiring, which was always correct and must stay correct: no ambient push, the
        // (sp, o) lambda runs per scope so the captured provider IS the request scope. Guards the
        // fix against "pooling works now, AddDbContext broke".
        var services = BaseServices(withResolvers: true);
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
        await using var provider = Build(services);

        await using (var scope = provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
        }

        foreach (var (user, tenant) in new[] { ("alice", "t-1"), ("bob", "t-2"), ("carol", "t-3") })
        {
            await using var scope = provider.CreateAsyncScope();
            var principal = scope.ServiceProvider.GetRequiredService<CurrentPrincipal>();
            principal.UserId = user;
            principal.TenantId = tenant;

            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Notes.Add(new Note { Text = $"note by {user}" });
            await ctx.SaveChangesAsync();
        }

        await using var read = provider.CreateAsyncScope();
        var readPrincipal = read.ServiceProvider.GetRequiredService<CurrentPrincipal>();
        readPrincipal.TenantId = "t-2";
        var readCtx = read.ServiceProvider.GetRequiredService<TestContext>();
        AssertOneRowPerActor(await readCtx.AuditLogs.ToListAsync());

        // The read-side tenant filter reads the same per-scope resolver.
        Assert.Equal("t-2", Assert.Single(await readCtx.AuditLog().ToListAsync()).TenantId);
    }
}
