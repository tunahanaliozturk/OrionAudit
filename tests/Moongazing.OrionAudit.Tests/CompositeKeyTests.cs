using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Tests;

public class CompositeKeyTests
{
    [Auditable]
    public sealed class Translation
    {
        public string TenantId { get; set; } = "";
        public Guid DocumentId { get; set; } = Guid.NewGuid();
        public string Locale { get; set; } = "";
        public string Body { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Translation> Translations => Set<Translation>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Translation>().HasKey(t => new { t.TenantId, t.DocumentId, t.Locale });
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<TestContext> NewAsync()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Translation>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        return await Task.FromResult(services.BuildServiceProvider().GetRequiredService<TestContext>());
    }

    [Fact]
    public async Task Insert_OnCompositeKeyEntity_WritesAuditRowWithJoinedKey()
    {
        await using var ctx = await NewAsync();
        var t = new Translation { TenantId = "acme", DocumentId = Guid.NewGuid(), Locale = "en", Body = "hello" };
        ctx.Translations.Add(t);
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal($"acme|{t.DocumentId}|en", entry.EntityId);
    }

    [Fact]
    public async Task AuditKey_From_ProducesSameShapeAsInterceptor()
    {
        await using var ctx = await NewAsync();
        var docId = Guid.NewGuid();
        ctx.Translations.Add(new Translation { TenantId = "acme", DocumentId = docId, Locale = "tr", Body = "merhaba" });
        await ctx.SaveChangesAsync();

        var rendered = AuditKey.From("acme", docId, "tr");
        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal(rendered, entry.EntityId);
    }

    [Fact]
    public async Task Reconstruct_AcceptsCompositeKey_AndReplaysHistory()
    {
        await using var ctx = await NewAsync();
        var docId = Guid.NewGuid();
        ctx.Translations.Add(new Translation { TenantId = "acme", DocumentId = docId, Locale = "en", Body = "v1" });
        await ctx.SaveChangesAsync();

        var t = await ctx.Translations.FirstAsync();
        t.Body = "v2";
        await ctx.SaveChangesAsync();

        var key = AuditKey.From(t.TenantId, t.DocumentId, t.Locale);
        var reconstructor = new AuditReconstructor(ctx);
        var rebuilt = await reconstructor.ReconstructAsync<Translation>(key, DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(rebuilt);
        Assert.Equal("v2", rebuilt!.Body);
    }

    [Fact]
    public void AuditKey_From_EscapesLiteralPipe()
    {
        var rendered = AuditKey.From("ten|ant", "doc");
        Assert.Equal("ten%7Cant|doc", rendered);
    }

    [Fact]
    public void AuditKey_From_SingleComponent_StaysVerbatim()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString(), AuditKey.From(id));
    }
}
