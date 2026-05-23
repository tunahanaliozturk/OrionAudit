using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Viewer;

namespace Moongazing.OrionAudit.Viewer.Tests;

public class ViewerAuthorizationTests
{
    public sealed class ViewerDb : DbContext
    {
        public ViewerDb(DbContextOptions<ViewerDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations();
    }

    private static IHost BuildHost(Action<OrionAuditViewerOptions>? configure)
        => new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    // "Test" scheme always returns NoResult so requests are unauthenticated;
                    // the authorization middleware can then challenge cleanly via this scheme
                    // and produce a 401 instead of throwing "no default challenge scheme".
                    s.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, NoUserAuthHandler>("Test", _ => { });
                    s.AddAuthorization();
                    // The /api/meta handler needs both services. Use empties so the auth test
                    // exercises only the authorization pipeline, not real audit data.
                    s.AddSingleton<IAuditConfiguration>(
                        new AuditConfiguration(new Dictionary<Type, AuditableTypeConfig>()));
                    s.AddSingleton<IAuditDispatcher, NoOpAuditDispatcher>();
                    s.AddDbContext<ViewerDb>(o => o.UseSqlite("DataSource=:memory:"));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapOrionAuditViewer<ViewerDb>("/audit", configure));
                }))
            .Build();

    [Fact]
    public async Task ApiEndpoint_WithoutAuthenticatedUser_Returns401()
    {
        using var host = BuildHost(configure: null);   // default: authorization required
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/audit/api/meta");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiEndpoint_WithAllowAnonymous_DoesNotReturn401()
    {
        using var host = BuildHost(o => o.AllowAnonymous());
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/audit/api/meta");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
