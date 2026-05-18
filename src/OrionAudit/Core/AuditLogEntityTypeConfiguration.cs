using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OrionAudit;

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

    /// <summary>Initializes a new configuration using <see cref="DefaultTableName"/>.</summary>
    public AuditLogEntityTypeConfiguration() : this(DefaultTableName) { }

    /// <summary>Initializes a new configuration with a custom table name.</summary>
    public AuditLogEntityTypeConfiguration(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.tableName = tableName;
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
        builder.Property(x => x.Diff).IsRequired();

        builder.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_EntityLookup");
        builder.HasIndex(x => new { x.TenantId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_TenantTimeline");
        builder.HasIndex(x => new { x.UserId, x.OccurredOnUtc })
            .HasDatabaseName("IX_OrionAudit_UserActivity");
    }
}
