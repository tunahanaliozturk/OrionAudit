using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// EF Core fluent configuration for <see cref="AuditChainAnchor"/>. Applied automatically by
/// <c>modelBuilder.ApplyOrionAuditConfigurations()</c>. Harmless when hash-chaining is not enabled -
/// the table simply stays empty, exactly like the snapshot-cursor and capture-queue companion tables.
/// </summary>
public sealed class AuditChainAnchorEntityTypeConfiguration : IEntityTypeConfiguration<AuditChainAnchor>
{
    /// <summary>Default table name when no override is supplied.</summary>
    public const string DefaultTableName = "OrionAudit_Chain_Anchor";

    private readonly string tableName;

    /// <summary>Initializes a new configuration using <see cref="DefaultTableName"/>.</summary>
    public AuditChainAnchorEntityTypeConfiguration() : this(DefaultTableName) { }

    /// <summary>Initializes a new configuration with a custom table name.</summary>
    public AuditChainAnchorEntityTypeConfiguration(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.tableName = tableName;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditChainAnchor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(tableName);
        // Composite primary key mirrors the chain key. TenantId stays string.Empty for single-tenant
        // / null-tenant streams so no PK member is ever nullable (most providers require non-null PKs),
        // matching how SnapshotCursor keys its rows.
        builder.HasKey(x => new { x.EntityType, x.EntityId, x.TenantId });

        builder.Property(x => x.EntityType).IsRequired().HasMaxLength(512);
        builder.Property(x => x.EntityId).IsRequired().HasMaxLength(128);
        builder.Property(x => x.TenantId).IsRequired().HasMaxLength(128);
        // HMAC-SHA256 rendered as lowercase hex is exactly 64 chars; fixed-length so a CHAR-capable
        // provider stores it compactly, matching the AuditLog hash columns.
        builder.Property(x => x.LatestEntryHash).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.RowCount).IsRequired();
        builder.Property(x => x.KeyId).IsRequired();
    }
}
