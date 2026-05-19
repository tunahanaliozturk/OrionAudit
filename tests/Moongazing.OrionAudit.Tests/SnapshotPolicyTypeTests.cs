using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class SnapshotPolicyTypeTests
{
    [Fact]
    public void Never_IsSingleton()
    {
        Assert.Same(SnapshotPolicy.Never, SnapshotPolicy.Never);
    }

    [Fact]
    public void EveryN_RejectsZeroOrNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SnapshotPolicy.Every(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SnapshotPolicy.Every(-1));
    }

    [Fact]
    public void EveryDuration_RejectsZeroOrNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SnapshotPolicy.EveryDuration(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => SnapshotPolicy.EveryDuration(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void EveryN_ExposesUpdateCount()
    {
        var policy = SnapshotPolicy.Every(7);
        Assert.IsType<SnapshotPolicy>(policy, exactMatch: false);
        Assert.Contains("Updates = 7", policy.ToString());
    }

    private sealed class CursorContext : DbContext
    {
        public DbSet<SnapshotCursor> Cursors => Set<SnapshotCursor>();
        public CursorContext(DbContextOptions<CursorContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new SnapshotCursorEntityTypeConfiguration());
        }
    }

    [Fact]
    public void SnapshotCursor_TableMapsWithCompositeKey()
    {
        var opts = new DbContextOptionsBuilder<CursorContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var ctx = new CursorContext(opts);
        var entity = ctx.Model.FindEntityType(typeof(SnapshotCursor))!;
        Assert.Equal("OrionAudit_Snapshot_Cursors", entity.GetTableName());
        var pk = entity.FindPrimaryKey()!;
        Assert.Equal(3, pk.Properties.Count);
        Assert.Equal(
            new[] { nameof(SnapshotCursor.EntityType), nameof(SnapshotCursor.EntityId), nameof(SnapshotCursor.TenantId) },
            pk.Properties.Select(p => p.Name).ToArray());
    }
}
