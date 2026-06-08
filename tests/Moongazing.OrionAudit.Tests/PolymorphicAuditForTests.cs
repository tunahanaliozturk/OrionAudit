namespace Moongazing.OrionAudit.Tests;

using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Xunit;

public sealed class PolymorphicAuditForTests
{
    public abstract class Document
    {
        public Guid Id { get; set; }
    }

    public sealed class Invoice : Document
    {
        public decimal Amount { get; set; }
    }

    public sealed class Memo : Document
    {
        public string Body { get; set; } = "";
    }

    public sealed class Receipt
    {
        public Guid Id { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
        }
    }

    private static TestContext NewContext() =>
        new(new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AuditLog MakeRow(Type runtime, Type? baseType, string entityId)
        => new()
        {
            EntityType = runtime.AssemblyQualifiedName!,
            EntityBaseType = baseType?.FullName,
            EntityId = entityId,
            Action = AuditAction.Inserted,
            OccurredOnUtc = DateTime.UtcNow,
        };

    [Fact]
    public async Task AuditFor_TBase_returns_rows_from_every_subclass()
    {
        await using var ctx = NewContext();
        ctx.AuditLogs.Add(MakeRow(typeof(Invoice), typeof(Document), "inv-1"));
        ctx.AuditLogs.Add(MakeRow(typeof(Memo), typeof(Document), "memo-1"));
        ctx.AuditLogs.Add(MakeRow(typeof(Receipt), baseType: null, "rcpt-1"));
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditFor<Document>().ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.EntityId == "inv-1");
        Assert.Contains(rows, r => r.EntityId == "memo-1");
        Assert.DoesNotContain(rows, r => r.EntityId == "rcpt-1");
    }

    [Fact]
    public async Task AuditFor_TConcrete_narrows_to_runtime_type_only()
    {
        await using var ctx = NewContext();
        ctx.AuditLogs.Add(MakeRow(typeof(Invoice), typeof(Document), "inv-1"));
        ctx.AuditLogs.Add(MakeRow(typeof(Memo), typeof(Document), "memo-1"));
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditFor<Invoice>().ToListAsync();

        Assert.Single(rows);
        Assert.Equal("inv-1", rows[0].EntityId);
    }

    [Fact]
    public async Task AuditFor_concrete_still_matches_rows_without_base_type()
    {
        // Pre-v0.7.1 rows leave EntityBaseType null. The exact-type predicate must still match.
        await using var ctx = NewContext();
        ctx.AuditLogs.Add(MakeRow(typeof(Receipt), baseType: null, "rcpt-1"));
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditFor<Receipt>().ToListAsync();

        Assert.Single(rows);
        Assert.Equal("rcpt-1", rows[0].EntityId);
    }

    [Fact]
    public async Task AuditFor_TBase_does_not_match_unrelated_runtime_types()
    {
        await using var ctx = NewContext();
        ctx.AuditLogs.Add(MakeRow(typeof(Receipt), baseType: null, "rcpt-1"));
        await ctx.SaveChangesAsync();

        var rows = await ctx.AuditFor<Document>().ToListAsync();

        Assert.Empty(rows);
    }
}
