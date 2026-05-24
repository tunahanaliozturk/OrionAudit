using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Viewer;

namespace Moongazing.OrionAudit.Viewer.Tests;

public class ViewerTenantFilterTests
{
    public sealed class TenantDb : DbContext
    {
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TenantDb(DbContextOptions<TenantDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations();
    }

    private sealed class FixedTenant : IAuditTenantResolver
    {
        public string? Resolve(IServiceProvider serviceProvider) => "tenant-A";
    }

    private sealed record EntryDto(string entityType);
    private sealed record LogPage(IReadOnlyList<EntryDto> entries);

    [Fact]
    public async Task LogEndpoint_OnlyReturnsCurrentTenantRows()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var _c = conn;

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthentication();
                    s.AddAuthorization();
                    s.AddSingleton(conn);
                    s.AddScoped<IAuditTenantResolver, FixedTenant>();
                    // Empty config — this test is about tenant filtering, not custom columns.
                    s.AddSingleton<Moongazing.OrionAudit.Configuration.IAuditConfiguration>(
                        new Moongazing.OrionAudit.Configuration.AuditConfiguration(
                            new Dictionary<Type, Moongazing.OrionAudit.Configuration.AuditableTypeConfig>()));
                    s.AddDbContext<TenantDb>((sp, o) =>
                        o.UseSqlite(sp.GetRequiredService<SqliteConnection>()));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<TenantDb>("/audit", o => o.AllowAnonymous()));
                }))
            .StartAsync();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TenantDb>();
            await ctx.Database.EnsureCreatedAsync();
            ctx.AuditLogs.Add(new AuditLog { EntityType = "T", EntityId = "1", TenantId = "tenant-A", OccurredOnUtc = DateTime.UtcNow });
            ctx.AuditLogs.Add(new AuditLog { EntityType = "T", EntityId = "2", TenantId = "tenant-B", OccurredOnUtc = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var page = await host.GetTestServer().CreateClient()
            .GetFromJsonAsync<LogPage>("/audit/api/log?page=1&size=20");
        Assert.NotNull(page);
        Assert.Single(page!.entries);   // tenant-B row filtered out
    }
}
