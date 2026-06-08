using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Moongazing.OrionAudit;

/// <summary>
/// EF Core fluent configuration for <see cref="AuditCaptureQueueEntry"/>. Applied automatically
/// by <c>modelBuilder.ApplyOrionAuditConfigurations()</c> — harmless when async capture is not
/// enabled, the table simply stays empty.
/// </summary>
public sealed class AuditCaptureQueueEntityTypeConfiguration : IEntityTypeConfiguration<AuditCaptureQueueEntry>
{
    /// <summary>Default table name when no override is supplied.</summary>
    public const string DefaultTableName = "OrionAudit_Capture_Queue";

    private readonly string tableName;

    /// <summary>Initializes a new configuration using <see cref="DefaultTableName"/>.</summary>
    public AuditCaptureQueueEntityTypeConfiguration() : this(DefaultTableName) { }

    /// <summary>Initializes a new configuration with a custom table name.</summary>
    public AuditCaptureQueueEntityTypeConfiguration(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        this.tableName = tableName;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditCaptureQueueEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(tableName);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.EntityType).IsRequired().HasMaxLength(512);
        builder.Property(x => x.EntityBaseType).HasMaxLength(512);
        builder.Property(x => x.EntityId).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Action).IsRequired();
        builder.Property(x => x.BeforeJson).IsRequired();
        builder.Property(x => x.AfterJson).IsRequired();
        // User / tenant / correlation column lengths match AuditLogEntityTypeConfiguration
        // exactly — the dispatcher copies these fields verbatim onto the AuditLog row, so a
        // wider queue column could admit a value that later fails the AuditLog insert.
        builder.Property(x => x.UserId).HasMaxLength(128);
        builder.Property(x => x.UserDisplay).HasMaxLength(256);
        builder.Property(x => x.UserType).HasMaxLength(32);
        builder.Property(x => x.TenantId).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.OccurredOnUtc).IsRequired();
        builder.Property(x => x.Attempts).IsRequired();
        builder.Property(x => x.ClaimToken).HasMaxLength(64);
        builder.Property(x => x.CustomColumnsJson);

        // The dispatcher's claim query filters unclaimed, non-dead-lettered rows and orders by Id.
        builder.HasIndex(x => new { x.Error, x.ClaimToken });
    }
}
