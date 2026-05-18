using System.Collections.Frozen;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>Default <see cref="IAuditConfiguration"/> implementation backed by a <see cref="FrozenDictionary{TKey, TValue}"/>.</summary>
public sealed class AuditConfiguration : IAuditConfiguration
{
    private readonly FrozenDictionary<Type, AuditableTypeConfig> byType;

    /// <summary>Initializes a new configuration. Intended to be called only by <see cref="AuditConfigurationBuilder"/>.</summary>
    public AuditConfiguration(IDictionary<Type, AuditableTypeConfig> byType)
    {
        ArgumentNullException.ThrowIfNull(byType);
        this.byType = byType.ToFrozenDictionary();
    }

    /// <inheritdoc />
    public bool IsAudited(Type entityType)
        => byType.ContainsKey(entityType);

    /// <inheritdoc />
    public AuditableTypeConfig? GetConfig(Type entityType)
        => byType.TryGetValue(entityType, out var config) ? config : null;
}
