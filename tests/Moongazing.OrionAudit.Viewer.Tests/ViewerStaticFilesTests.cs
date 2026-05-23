using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moongazing.OrionAudit.Viewer;

namespace Moongazing.OrionAudit.Viewer.Tests;

public class ViewerStaticFilesTests
{
    public sealed class StaticDb : DbContext
    {
        public StaticDb(DbContextOptions<StaticDb> options) : base(options) { }
    }

    [Fact]
    public async Task Root_ServesEmbeddedHtml()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthentication();
                    s.AddAuthorization();
                    s.AddDbContext<StaticDb>(o => o.UseSqlite("DataSource=:memory:"));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                        e.MapOrionAuditViewer<StaticDb>("/audit", o => o.AllowAnonymous()));
                }))
            .StartAsync();

        var response = await host.GetTestServer().CreateClient().GetAsync("/audit");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("OrionAudit", body, StringComparison.Ordinal);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
