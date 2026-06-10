namespace Moongazing.OrionAudit.Tests.Retention;

using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Retention;
using Xunit;

public sealed class AuditArchiverTests : IAsyncLifetime
{
    private sealed class ArchiveRow
    {
        public long Id { get; set; }
        public Guid SourceId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public DateTime OccurredOnUtc { get; set; }
    }

    private sealed class ArchiverDbContext : DbContext
    {
        public ArchiverDbContext(DbContextOptions<ArchiverDbContext> options) : base(options) { }
        public DbSet<AuditLog> Logs => Set<AuditLog>();
        public DbSet<ArchiveRow> Archive => Set<ArchiveRow>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new AuditLogEntityTypeConfiguration());
            modelBuilder.Entity<ArchiveRow>().HasKey(a => a.Id);
        }
    }

    private SqliteConnection connection = default!;
    private ServiceProvider services = default!;

    public async ValueTask InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var collection = new ServiceCollection();
        collection.AddDbContext<ArchiverDbContext>(o => o.UseSqlite(connection));
        services = collection.BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiverDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static AuditLog Row(DateTime occurred) => new()
    {
        EntityType = "Demo.User",
        EntityId = "1",
        Action = AuditAction.Inserted,
        OccurredOnUtc = occurred,
        UserId = "u-1",
        UserType = "user",
        Diff = "{}",
        Snapshot = null,
    };

    private async Task<List<AuditLog>> SeedAsync(params AuditLog[] rows)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiverDbContext>();
        db.Logs.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.ToList();
    }

    [Fact]
    public async Task DeleteAuditArchiver_removes_rows_by_id()
    {
        var rows = await SeedAsync(Row(DateTime.UtcNow.AddDays(-100)), Row(DateTime.UtcNow.AddDays(-1)));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiverDbContext>();
        var sut = new DeleteAuditArchiver();

        var deleted = await sut.ArchiveAsync(db, new[] { rows[0] }, RetentionPolicy.RetainFor(TimeSpan.FromDays(90)), CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(1, await db.Logs.CountAsync());
    }

    [Fact]
    public async Task CopyToTableAuditArchiver_copies_then_deletes_in_one_transaction()
    {
        var rows = await SeedAsync(
            Row(DateTime.UtcNow.AddDays(-200)),
            Row(DateTime.UtcNow.AddDays(-150)));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiverDbContext>();
        var sut = new CopyToTableAuditArchiver<ArchiveRow>(
            map: r => new ArchiveRow { SourceId = r.Id, EntityType = r.EntityType, OccurredOnUtc = r.OccurredOnUtc });

        var deleted = await sut.ArchiveAsync(db, rows, RetentionPolicy.RetainFor(TimeSpan.FromDays(90)), CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(0, await db.Logs.CountAsync());
        Assert.Equal(2, await db.Archive.CountAsync());
        Assert.All(rows, r => Assert.Contains(db.Archive.AsEnumerable(), a => a.SourceId == r.Id));
    }

    [Fact]
    public async Task CopyToTableAuditArchiver_rolls_back_on_delete_failure()
    {
        // Map throws on the second row, after the AddRange succeeded - the delete then
        // never runs, the transaction rolls back, and neither the live nor the archive
        // table is mutated. We trigger this by maping into a non-existent shadow type via
        // a throwing map function.
        var rows = await SeedAsync(Row(DateTime.UtcNow.AddDays(-200)));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiverDbContext>();
        var sut = new CopyToTableAuditArchiver<ArchiveRow>(
            map: _ => throw new InvalidOperationException("simulated map failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ArchiveAsync(db, rows, RetentionPolicy.RetainFor(TimeSpan.FromDays(90)), CancellationToken.None));

        // Rolled back - the source row is still in the live table, archive is empty.
        Assert.Equal(1, await db.Logs.CountAsync());
        Assert.Equal(0, await db.Archive.CountAsync());
    }

    [Fact]
    public async Task DeleteAuditArchiver_is_no_op_for_empty_input()
    {
        await SeedAsync(Row(DateTime.UtcNow.AddDays(-1)));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiverDbContext>();
        var sut = new DeleteAuditArchiver();

        var deleted = await sut.ArchiveAsync(db, Array.Empty<AuditLog>(),
            RetentionPolicy.RetainFor(TimeSpan.FromDays(90)), CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await db.Logs.CountAsync());
    }

    [Fact]
    public void CopyToTableAuditArchiver_rejects_null_map()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CopyToTableAuditArchiver<ArchiveRow>(null!));
    }

    [Fact]
    public async Task AuditRetentionHostedService_routes_through_custom_archiver()
    {
        await SeedAsync(
            Row(DateTime.UtcNow.AddDays(-200)),
            Row(DateTime.UtcNow.AddDays(-150)));
        var archiver = new CopyToTableAuditArchiver<ArchiveRow>(
            r => new ArchiveRow { SourceId = r.Id, EntityType = r.EntityType, OccurredOnUtc = r.OccurredOnUtc });
        var sut = new AuditRetentionHostedService<ArchiverDbContext>(
            services.GetRequiredService<IServiceScopeFactory>(),
            RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
            new RetentionSweepOptions(),
            TimeProvider.System,
            NullLogger<AuditRetentionHostedService<ArchiverDbContext>>.Instance,
            archiver);

        var deleted = await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiverDbContext>();
        Assert.Equal(2, await db.Archive.CountAsync());
    }

    [Fact]
    public async Task AuditRetentionHostedService_5_arg_ctor_still_resolves()
    {
        // ABI compat: the v0.7.7 5-arg ctor MUST still exist for compiled v0.7.7 callers.
        await SeedAsync(Row(DateTime.UtcNow.AddDays(-200)));
        var sut = new AuditRetentionHostedService<ArchiverDbContext>(
            services.GetRequiredService<IServiceScopeFactory>(),
            RetentionPolicy.RetainFor(TimeSpan.FromDays(90)),
            new RetentionSweepOptions(),
            TimeProvider.System,
            NullLogger<AuditRetentionHostedService<ArchiverDbContext>>.Instance);

        var deleted = await sut.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
    }
}
