using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Tests;

[JsonSerializable(typeof(JsonContextWiringTests.Widget))]
public partial class JsonCtxTestsContext : JsonSerializerContext { }

public class JsonContextWiringTests
{
    [Auditable]
    public sealed class Widget
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Widget> Widgets => Set<Widget>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().HasKey(w => w.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<(ServiceProvider sp, TestContext ctx)> BuildAsync(bool wireContext)
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o =>
        {
            o.Audit<Widget>();
            if (wireContext)
            {
                o.UseJsonContext(JsonCtxTestsContext.Default);
            }
        });
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        var sp = services.BuildServiceProvider();
        return (sp, await Task.FromResult(sp.GetRequiredService<TestContext>()));
    }

    [Fact]
    public async Task SaveChanges_WithUseJsonContext_StillProducesCorrectDiff()
    {
        var (sp, ctx) = await BuildAsync(wireContext: true);
        await using var _ = sp;
        await using var _ctx = ctx;
        ctx.Widgets.Add(new Widget { Name = "hello", Count = 3 });
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Contains("\"hello\"", entry.Diff);
        Assert.Contains("\"/Count\"", entry.Diff);
        Assert.Contains("\"value\":3", entry.Diff);
    }

    [Fact]
    public async Task Reconstruct_WithUseJsonContext_RoundtripsThroughGeneratedContext()
    {
        var (sp, ctx) = await BuildAsync(wireContext: true);
        await using var _ = sp;
        await using var _ctx = ctx;
        var w = new Widget { Name = "first", Count = 1 };
        ctx.Widgets.Add(w);
        await ctx.SaveChangesAsync();
        w.Name = "second"; w.Count = 2;
        await ctx.SaveChangesAsync();

        var reconstructor = sp.GetRequiredService<IAuditReconstructor>();
        var rebuilt = await reconstructor.ReconstructAsync<Widget>(w.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(rebuilt);
        Assert.Equal("second", rebuilt!.Name);
        Assert.Equal(2, rebuilt.Count);
    }

    [Fact]
    public async Task SaveChanges_WithoutUseJsonContext_StillWorksViaReflection()
    {
        var (sp, ctx) = await BuildAsync(wireContext: false);
        await using var _ = sp;
        await using var _ctx = ctx;
        ctx.Widgets.Add(new Widget { Name = "no-context", Count = 1 });
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Contains("\"no-context\"", entry.Diff);
    }
}
