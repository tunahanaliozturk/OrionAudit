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

public class ViewerApiTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    public sealed class ApiDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public ApiDb(DbContextOptions<ApiDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private sealed record LogPage(IReadOnlyList<EntryDto> entries);
    private sealed record EntryDto(string action, IReadOnlyList<ChangeDto> changes);
    private sealed record ChangeDto(string propertyPath, string changeKind);
    private sealed record MetaDto(IReadOnlyList<string> auditedTypes, int queueDepth);

    private static async Task<(IHost host, SqliteConnection conn)> BuildAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthentication();
                    s.AddAuthorization();
                    s.AddSingleton(conn);
                    s.AddOrionAudit<ApiDb>(o => o.Audit<Note>());
                    s.AddDbContext<ApiDb>((sp, o) =>
                        o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<ApiDb>("/audit", o => o.AllowAnonymous()));
                }))
            .Build();
        await host.StartAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApiDb>().Database.EnsureCreatedAsync();
        }
        return (host, conn);
    }

    [Fact]
    public async Task LogEndpoint_ReturnsRenderedEntries()
    {
        var (host, conn) = await BuildAsync();
        using var _h = host;
        await using var _c = conn;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApiDb>();
            ctx.Notes.Add(new Note { Body = "first" });
            await ctx.SaveChangesAsync();
        }

        var page = await host.GetTestServer().CreateClient()
            .GetFromJsonAsync<LogPage>("/audit/api/log?page=1&size=20");
        Assert.NotNull(page);
        Assert.Single(page!.entries);
        Assert.Equal("Inserted", page.entries[0].action);
    }

    [Fact]
    public async Task MetaEndpoint_ReturnsAuditedTypeNames()
    {
        var (host, conn) = await BuildAsync();
        using var _h = host;
        await using var _c = conn;

        var meta = await host.GetTestServer().CreateClient()
            .GetFromJsonAsync<MetaDto>("/audit/api/meta");
        Assert.NotNull(meta);
        Assert.Contains(meta!.auditedTypes, t => t.Contains("Note", StringComparison.Ordinal));
    }
}
