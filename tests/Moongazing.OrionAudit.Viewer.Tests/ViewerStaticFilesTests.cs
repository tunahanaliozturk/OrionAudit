using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Viewer;

namespace Moongazing.OrionAudit.Viewer.Tests;

public class ViewerStaticFilesTests
{
    public sealed class StaticDb : DbContext
    {
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public StaticDb(DbContextOptions<StaticDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations();
    }

    [Fact]
    public async Task Root_ServesEmbeddedHtml()
    {
        // The page renders the audit rows server-side, so it needs the audit model and the
        // audit configuration - the same services the JSON API already required.
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
                    s.AddSingleton<IAuditConfiguration>(
                        new AuditConfiguration(new Dictionary<Type, AuditableTypeConfig>()));
                    s.AddDbContext<StaticDb>((sp, o) =>
                        o.UseSqlite(sp.GetRequiredService<SqliteConnection>()));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<StaticDb>("/audit", o => o.AllowAnonymous()));
                }))
            .StartAsync();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<StaticDb>().Database.EnsureCreatedAsync();
        }

        var response = await host.GetTestServer().CreateClient().GetAsync("/audit");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("OrionAudit", body, StringComparison.Ordinal);
        Assert.Contains("No audit entries yet.", body, StringComparison.Ordinal);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
