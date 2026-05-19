using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Moongazing.OrionAudit;

/// <summary>
/// EF Core fluent configuration for <see cref="SnapshotCursor"/>. Applied automatically by
/// <c>modelBuilder.ApplyOrionAuditConfigurations()</c> when periodic snapshotting is enabled.
/// </summary>
public sealed class SnapshotCursorEntityTypeConfiguration : IEntityTypeConfiguration<SnapshotCursor>
{
    /// <summary>Default table name when no override is supplied.</summary>
    public const string DefaultTableName = "OrionAudit_Snapshot_Cursors";

    private readonly string tableName;

    /// <summary>Initializes a new configuration using <see cref="DefaultTableName"/>.</summary>
    public SnapshotCursorEntityTypeConfiguration() : this(DefaultTableName) { }

    /// <summary>Initializes a new configuration with a custom table name.</summary>
    public SnapshotCursorEntityTypeConfiguration(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.tableName = tableName;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SnapshotCursor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(tableName);
        // Composite primary key — TenantId stays string.Empty for single-tenant deployments so
        // we never have a nullable PK column (most DBs require non-null PK members).
        builder.HasKey(x => new { x.EntityType, x.EntityId, x.TenantId });

        builder.Property(x => x.EntityType).IsRequired().HasMaxLength(512);
        builder.Property(x => x.EntityId).IsRequired().HasMaxLength(128);
        builder.Property(x => x.TenantId).IsRequired().HasMaxLength(128);
        builder.Property(x => x.UpdatesSinceLast).IsRequired();
        builder.Property(x => x.LastSnapshotUtc);
    }
}
