using System.Net.Http.Json;
using System.Text.Json;
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

public class ViewerCustomColumnsTests
{
    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    public sealed class CustomColsDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public CustomColsDb(DbContextOptions<CustomColsDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations(this);
        }
    }

    private sealed record EntryDto(string action, Dictionary<string, JsonElement> customColumns);
    private sealed record LogPage(IReadOnlyList<EntryDto> entries);
    private sealed record MetaDto(IReadOnlyList<string> auditedTypes, int queueDepth, IReadOnlyList<string> customColumnNames);

    [Fact]
    public async Task LogEndpoint_Includes_CustomColumns()
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
                    s.AddOrionAudit<CustomColsDb>(o => o
                        .Audit<Note>()
                        .AddColumn<int>("Length", ctx => ((Note)ctx.Entity).Body.Length));
                    s.AddDbContext<CustomColsDb>((sp, o) =>
                        o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<CustomColsDb>("/audit", o => o.AllowAnonymous()));
                }))
            .StartAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<CustomColsDb>();
            await ctx.Database.EnsureCreatedAsync();
            ctx.Notes.Add(new Note { Body = "hello" });
            await ctx.SaveChangesAsync();
        }

        var client = host.GetTestServer().CreateClient();
        var page = await client.GetFromJsonAsync<LogPage>("/audit/api/log?page=1&size=20");
        Assert.NotNull(page);
        var entry = Assert.Single(page!.entries);
        Assert.True(entry.customColumns.ContainsKey("Length"));
        Assert.Equal(5, entry.customColumns["Length"].GetInt32());

        var meta = await client.GetFromJsonAsync<MetaDto>("/audit/api/meta");
        Assert.NotNull(meta);
        Assert.Contains("Length", meta!.customColumnNames);
    }
}
