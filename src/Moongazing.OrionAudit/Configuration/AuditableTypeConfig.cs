using System.Collections.Frozen;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>How a property is treated when building audit snapshots.</summary>
public enum AuditFieldRule : byte
{
    /// <summary>Capture the value as-is.</summary>
    Capture = 0,
    /// <summary>Omit the property from snapshots entirely.</summary>
    Exclude = 1,
    /// <summary>Replace the value with a SHA-256 hex hash.</summary>
    Hash = 2,
    /// <summary>Replace the value with the literal <c>"&lt;redacted&gt;"</c>.</summary>
    Redact = 3,
}

/// <summary>Frozen per-type configuration: which type is audited and how its fields are treated.</summary>
public sealed class AuditableTypeConfig
{
    private readonly FrozenDictionary<string, AuditFieldRule> rules;

    /// <summary>Initializes a new configuration with the supplied field rules.</summary>
    public AuditableTypeConfig(
        Type entityType,
        IDictionary<string, AuditFieldRule> rules,
        string? softDeleteProperty = null)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(rules);
        EntityType = entityType;
        this.rules = rules.ToFrozenDictionary(StringComparer.Ordinal);
        SoftDeleteProperty = softDeleteProperty;
    }

    /// <summary>The audited entity CLR type.</summary>
    public Type EntityType { get; }

    /// <summary>
    /// Name of the boolean property whose flip from <c>false</c> to <c>true</c> is captured as
    /// <see cref="AuditAction.SoftDeleted"/>. Null when no soft-delete rule is configured.
    /// </summary>
    public string? SoftDeleteProperty { get; }

    /// <summary>
    /// Returns the rule for a given property name. Properties without an explicit rule are
    /// captured normally (<see cref="AuditFieldRule.Capture"/>).
    /// </summary>
    public AuditFieldRule FieldRule(string propertyName)
        => rules.TryGetValue(propertyName, out var rule) ? rule : AuditFieldRule.Capture;
}
