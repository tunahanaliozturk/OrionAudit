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

/// <summary>
/// Regression guard for the stored-XSS defect: an audited value is attacker-controlled, and the
/// viewer page used to build its markup from those values without encoding, so whoever reviewed
/// the audit log - an admin, by definition - executed the attacker's script.
/// </summary>
public class ViewerXssTests
{
    private const string Payload = "<img src=x onerror=alert(1)>";
    private const string Encoded = "&lt;img src=x onerror=alert(1)&gt;";

    [Auditable]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
    }

    public sealed class XssDb : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public XssDb(DbContextOptions<XssDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

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
                    s.AddOrionAudit<XssDb>(o => o.Audit<Note>());
                    s.AddDbContext<XssDb>((sp, o) =>
                        o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseOrionAudit(sp));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<XssDb>("/audit", o => o.AllowAnonymous()));
                }))
            .Build();
        await host.StartAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<XssDb>().Database.EnsureCreatedAsync();
        }
        return (host, conn);
    }

    [Fact]
    public async Task Page_EncodesScriptPayload_StoredInAnAuditedValue()
    {
        var (host, conn) = await BuildAsync();
        using var _h = host;
        await using var _c = conn;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<XssDb>();
            ctx.Notes.Add(new Note { Body = Payload });
            await ctx.SaveChangesAsync();
        }

        var body = await host.GetTestServer().CreateClient().GetStringAsync("/audit");

        Assert.Contains(Encoded, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Payload, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_EncodesScriptPayload_StoredInTheActorDisplayName()
    {
        var (host, conn) = await BuildAsync();
        using var _h = host;
        await using var _c = conn;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<XssDb>();
            ctx.AuditLogs.Add(new AuditLog
            {
                EntityType = "T",
                EntityId = "1",
                UserDisplay = Payload,
                Diff = "[]",
                OccurredOnUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var body = await host.GetTestServer().CreateClient().GetStringAsync("/audit");

        Assert.Contains(Encoded, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Payload, body, StringComparison.Ordinal);
    }
}
