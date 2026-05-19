using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Tests;

public class SoftDeleteTests
{
    [Auditable]
    [SoftDelete(nameof(IsDeleted))]
    public sealed class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
        public bool IsDeleted { get; set; }
    }

    [Auditable]
    public sealed class FluentNote
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Body { get; set; } = "";
        public bool Archived { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<FluentNote> FluentNotes => Set<FluentNote>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().HasKey(n => n.Id);
            modelBuilder.Entity<FluentNote>().HasKey(n => n.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static TestContext NewWithAttribute()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Note>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        return services.BuildServiceProvider().GetRequiredService<TestContext>();
    }

    private static TestContext NewWithFluent()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o
            .Audit<FluentNote>(b => b.SoftDelete(x => x.Archived)));
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        return services.BuildServiceProvider().GetRequiredService<TestContext>();
    }

    [Fact]
    public async Task UpdateThatFlipsIsDeletedTrue_EmitsSoftDeletedAction()
    {
        await using var ctx = NewWithAttribute();
        var note = new Note { Body = "hi" };
        ctx.Notes.Add(note);
        await ctx.SaveChangesAsync();

        note.IsDeleted = true;
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(AuditAction.Inserted, rows[0].Action);
        Assert.Equal(AuditAction.SoftDeleted, rows[1].Action);
        Assert.NotNull(rows[1].Snapshot);
    }

    [Fact]
    public async Task UpdateThatDoesNotFlipIsDeleted_StaysUpdatedAction()
    {
        await using var ctx = NewWithAttribute();
        var note = new Note { Body = "hi" };
        ctx.Notes.Add(note);
        await ctx.SaveChangesAsync();

        note.Body = "hello again";
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(AuditAction.Updated, rows[1].Action);
        Assert.Null(rows[1].Snapshot);
    }

    [Fact]
    public async Task UnflipBackToFalse_DoesNotTriggerSoftDeleted()
    {
        await using var ctx = NewWithAttribute();
        var note = new Note { Body = "hi", IsDeleted = true };
        ctx.Notes.Add(note);
        await ctx.SaveChangesAsync();

        note.IsDeleted = false;
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(AuditAction.Updated, rows[1].Action);
    }

    [Fact]
    public async Task Reconstruct_AfterSoftDelete_ReturnsNull()
    {
        await using var ctx = NewWithAttribute();
        var note = new Note { Body = "hi" };
        ctx.Notes.Add(note);
        await ctx.SaveChangesAsync();
        note.IsDeleted = true;
        await ctx.SaveChangesAsync();

        var reconstructor = new AuditReconstructor(ctx);
        var rebuilt = await reconstructor.ReconstructAsync<Note>(note.Id.ToString(), DateTime.UtcNow.AddMinutes(1));
        Assert.Null(rebuilt);
    }

    [Fact]
    public async Task FluentSoftDelete_BehavesLikeAttribute()
    {
        await using var ctx = NewWithFluent();
        var n = new FluentNote { Body = "x" };
        ctx.FluentNotes.Add(n);
        await ctx.SaveChangesAsync();

        n.Archived = true;
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditLogs.OrderBy(a => a.OccurredOnUtc).ToListAsync();
        Assert.Equal(AuditAction.SoftDeleted, rows[1].Action);
    }

    [Fact]
    public void SoftDeleteAttribute_OnNonBoolProperty_FailsAtBuild()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<OrionAuditConfigurationException>(() =>
            services.AddOrionAudit<TestContext>(o => o.Audit<BadlyTagged>()));
        Assert.Contains("not a public boolean", ex.Message);
    }

    [Auditable]
    [SoftDelete(nameof(StateName))]
    public sealed class BadlyTagged
    {
        public int Id { get; set; }
        public string StateName { get; set; } = "";
    }
}
