using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit;

/// <summary>
/// EF Core fluent configuration for <see cref="AuditLog"/>. Apply via
/// <c>modelBuilder.ApplyOrionAuditConfigurations()</c> (extension method) or by calling
/// <c>ApplyConfiguration(new AuditLogEntityTypeConfiguration("table-name"))</c> directly.
/// </summary>
public sealed class AuditLogEntityTypeConfiguration : IEntityTypeConfiguration<AuditLog>
{
    /// <summary>Default table name when no override is supplied.</summary>
    public const string DefaultTableName = "OrionAudit_Log";

    private readonly string tableName;
    private readonly OrionAuditColumnHints columnHints;
    private readonly IReadOnlyList<CustomColumn> customColumns;

    /// <summary>Initializes a new configuration using <see cref="DefaultTableName"/> and <see cref="OrionAuditColumnHints.Auto"/>.</summary>
    public AuditLogEntityTypeConfiguration()
        : this(DefaultTableName, OrionAuditColumnHints.Auto, Array.Empty<CustomColumn>()) { }

    /// <summary>Initializes a new configuration with a custom table name.</summary>
    public AuditLogEntityTypeConfiguration(string tableName)
        : this(tableName, OrionAuditColumnHints.Auto, Array.Empty<CustomColumn>()) { }

    /// <summary>Initializes a new configuration with a custom table name and provider column hint.</summary>
    public AuditLogEntityTypeConfiguration(string tableName, OrionAuditColumnHints columnHints)
        : this(tableName, columnHints, Array.Empty<CustomColumn>()) { }

    /// <summary>
    /// Initializes a new configuration with a custom table name, provider column hint, and
    /// the list of <see cref="CustomColumn"/>s to materialise as shadow properties.
    /// </summary>
    public AuditLogEntityTypeConfiguration(
        string tableName,
        OrionAuditColumnHints columnHints,
        IReadOnlyList<CustomColumn> customColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(customColumns);
        this.tableName = tableName;
        this.columnHints = columnHints;
        this.customColumns = customColumns;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(tableName);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EntityType).IsRequired().HasMaxLength(512);
        builder.Property(x => x.EntityId).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Action).IsRequired();
        builder.Property(x => x.OccurredOnUtc).IsRequired();
        builder.Property(x => x.UserId).HasMaxLength(128);
        builder.Property(x => x.UserDisplay).HasMaxLength(256);
        builder.Property(x => x.UserType).HasMaxLength(32);
        builder.Property(x => x.TenantId).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);

        // Tamper-evidence hash chain (v0.9.0). Both nullable: null is the off-state for consumers
        // who never enable hash-chaining, and the genesis/unverified-prefix marker for those who
        // do. SHA-256 rendered as lowercase hex is exactly 64 chars. Fixed-length so a provider
        // that supports CHAR can store them compactly; EF treats them as ordinary optional columns.
        builder.Property(x => x.EntryHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.PreviousHash).HasMaxLength(64).IsFixedLength();

        var diffProperty = builder.Property(x => x.Diff).IsRequired();
        var snapshotProperty = builder.Property(x => x.Snapshot);
        var hintedType = columnHints switch
        {
            OrionAuditColumnHints.SqlServerNvarcharMax => "nvarchar(max)",
            OrionAuditColumnHints.PostgresJsonb => "jsonb",
            OrionAuditColumnHints.SqliteText => "TEXT",
            OrionAuditColumnHints.MySqlJson => "json",
            OrionAuditColumnHints.MySqlLongText => "longtext",
            _ => null,
        };
        if (hintedType is not null)
        {
            diffProperty.HasColumnType(hintedType);
            snapshotProperty.HasColumnType(hintedType);
        }

        builder.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_EntityLookup");
        builder.HasIndex(x => new { x.TenantId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_TenantTimeline");
        builder.HasIndex(x => new { x.UserId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_UserActivity");

        // Consumer-registered custom columns become tipped, nullable shadow properties.
        // String columns default to HasMaxLength(512); other types use EF's defaults.
        // Non-nullable value types are lifted to Nullable<T> so EF can mark the property
        // optional. Indexes are intentionally left to the consumer's migration — they know
        // which columns they'll filter on.
        foreach (var column in customColumns)
        {
            var effectiveClrType = column.ClrType.IsValueType
                && Nullable.GetUnderlyingType(column.ClrType) is null
                ? typeof(Nullable<>).MakeGenericType(column.ClrType)
                : column.ClrType;
            var prop = builder.Property(effectiveClrType, column.Name);
            prop.IsRequired(false);
            if (column.ClrType == typeof(string))
            {
                prop.HasMaxLength(512);
            }
        }
    }
}
