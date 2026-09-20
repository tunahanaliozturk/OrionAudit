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
    private const string ViewerPolicy = "AuditViewers";

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
                    // The "Test" scheme authenticates a caller from the X-Test-Roles header and
                    // leaves a header-less request unauthenticated, so the authorization
                    // middleware can challenge cleanly via this scheme and produce a 401
                    // instead of throwing "no default challenge scheme".
                    s.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthHandler>("Test", _ => { });
                    s.AddAuthorization(o => o.AddPolicy(
                        ViewerPolicy, p => p.RequireRole("Auditor")));
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

    private static async Task<HttpResponseMessage> GetMetaAsync(IHost host, string? roles)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/audit/api/meta");
        if (roles is not null)
        {
            request.Headers.Add(RoleHeaderAuthHandler.RolesHeader, roles);
        }

        return await host.GetTestServer().CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task Registration_WithoutExplicitAccessDecision_ThrowsNamingTheMissingCall()
    {
        using var host = BuildHost(configure: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("MapOrionAuditViewer requires an explicit access decision", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RequireAuthorization(\"AuditViewers\")", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RequireAuthenticatedUser()", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AllowAnonymous()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_WithConfigureThatStatesNoDecision_Throws()
    {
        using var host = BuildHost(o => { /* touched the options but decided nothing */ });

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task NamedPolicy_WithoutAuthenticatedUser_Returns401()
    {
        using var host = BuildHost(o => o.RequireAuthorization(ViewerPolicy));
        await host.StartAsync();

        var response = await GetMetaAsync(host, roles: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NamedPolicy_WithAuthenticatedUserOutsidePolicy_Returns403()
    {
        using var host = BuildHost(o => o.RequireAuthorization(ViewerPolicy));
        await host.StartAsync();

        // Authenticated, but not an Auditor — exactly the caller the bare RequireAuthorization()
        // default used to let read the whole audit trail.
        var response = await GetMetaAsync(host, roles: "Support");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NamedPolicy_WithAuthenticatedUserInsidePolicy_Serves()
    {
        using var host = BuildHost(o => o.RequireAuthorization(ViewerPolicy));
        await host.StartAsync();

        var response = await GetMetaAsync(host, roles: "Auditor");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InlinePolicy_EnforcesTheConfiguredRequirements()
    {
        using var host = BuildHost(o => o.RequireAuthorization(p => p.RequireRole("Auditor")));
        await host.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMetaAsync(host, roles: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetMetaAsync(host, roles: "Support")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMetaAsync(host, roles: "Auditor")).StatusCode);
    }

    [Fact]
    public async Task AllowAnonymous_StillRegistersAndServes()
    {
        using var host = BuildHost(o => o.AllowAnonymous());
        await host.StartAsync();

        var response = await GetMetaAsync(host, roles: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
