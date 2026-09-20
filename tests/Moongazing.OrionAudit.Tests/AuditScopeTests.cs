using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Tests;

public class AuditScopeTests
{
    [Auditable]
    public sealed class Job
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Status { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Job> Jobs => Set<Job>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Job>().HasKey(j => j.Id);
            modelBuilder.ApplyOrionAuditConfigurations();
        }
    }

    private static async Task<TestContext> NewAsync()
    {
        var services = new ServiceCollection();
        services.AddOrionAudit<TestContext>(o => o.Audit<Job>());
        services.AddDbContext<TestContext>((sp, o) =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()).UseOrionAudit(sp));
        return await Task.FromResult(services.BuildServiceProvider().GetRequiredService<TestContext>());
    }

    [Fact]
    public async Task Push_SetsCorrelationOnAuditRow()
    {
        await using var ctx = await NewAsync();
        const string jobId = "nightly-2026-05-20";
        using (AuditScope.Push(jobId))
        {
            ctx.Jobs.Add(new Job { Status = "running" });
            await ctx.SaveChangesAsync();
        }
        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal(jobId, entry.CorrelationId);
    }

    [Fact]
    public void NestedScopes_RestoreOuterValueOnDispose()
    {
        using (AuditScope.Push("outer"))
        {
            Assert.Equal("outer", AuditScope.Current);
            using (AuditScope.Push("inner"))
            {
                Assert.Equal("inner", AuditScope.Current);
            }
            Assert.Equal("outer", AuditScope.Current);
        }
        Assert.Null(AuditScope.Current);
    }

    [Fact]
    public async Task Push_FlowsAcrossAwaits()
    {
        using (AuditScope.Push("flow-test"))
        {
            await Task.Yield();
            await Task.Run(() => Assert.Equal("flow-test", AuditScope.Current));
        }
    }

    [Fact]
    public async Task NoScope_FallsBackToActivityOrNull()
    {
        await using var ctx = await NewAsync();
        ctx.Jobs.Add(new Job { Status = "free" });
        await ctx.SaveChangesAsync();
        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        // No AuditScope.Push, no Activity in flight → CorrelationId is null.
        Assert.Null(entry.CorrelationId);
    }

    // OrionAudit's ActivitySource only produces spans while something is listening. Any test that
    // wants to prove capture does not stamp its OWN span id has to turn that on deliberately —
    // otherwise StartActivity returns null and the bug is invisible. (It also used to make
    // NoScope_FallsBackToActivityOrNull above fail intermittently, whenever another test class
    // happened to have a listener registered in parallel.)
    private static ActivityListener ListenToOrionAudit()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OrionAuditTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public async Task NoScope_StampsTheCallersTrace_NotOrionAuditsOwnSpan()
    {
        using var listener = ListenToOrionAudit();
        await using var ctx = await NewAsync();

        using var caller = new Activity("caller-request").Start();
        Assert.NotNull(caller.Id);

        ctx.Jobs.Add(new Job { Status = "in-request" });
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        // Previously this was the id of the OrionAudit.Capture span the interceptor had already
        // started, so audit rows could not be joined back to the request that produced them.
        Assert.Equal(caller.Id, entry.CorrelationId);
    }

    [Fact]
    public async Task NoScope_AndNoCallerTrace_LeavesCorrelationNull_EvenWhileOrionAuditSpansAreSampled()
    {
        using var listener = ListenToOrionAudit();
        await using var ctx = await NewAsync();
        Assert.Null(Activity.Current);

        ctx.Jobs.Add(new Job { Status = "no-trace" });
        await ctx.SaveChangesAsync();

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Null(entry.CorrelationId);
    }

    [Fact]
    public async Task ExplicitScope_WinsOverTheCallersTrace()
    {
        using var listener = ListenToOrionAudit();
        await using var ctx = await NewAsync();

        using var caller = new Activity("caller-request").Start();
        using (AuditScope.Push("batch-42"))
        {
            ctx.Jobs.Add(new Job { Status = "scoped" });
            await ctx.SaveChangesAsync();
        }

        var entry = Assert.Single(await ctx.AuditLogs.ToListAsync());
        Assert.Equal("batch-42", entry.CorrelationId);
    }
}
