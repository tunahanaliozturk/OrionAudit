using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Top-level fluent builder for OrionAudit configuration. Accumulates per-type rules from both
/// <see cref="AuditableAttribute"/> discovery and explicit <c>Audit&lt;T&gt;()</c> calls, then
/// produces a frozen <see cref="IAuditConfiguration"/> via <see cref="Build"/>.
/// </summary>
public sealed class AuditConfigurationBuilder
{
    private readonly Dictionary<Type, Dictionary<string, AuditFieldRule>> rulesByType = new();
    private readonly Dictionary<Type, string?> softDeleteByType = new();
    private readonly Dictionary<Type, Type> baseTypeByType = new();
    private IReadOnlyList<CustomColumn> customColumns = Array.Empty<CustomColumn>();

    /// <summary>Set by <c>AddOrionAudit</c> from <c>OrionAuditOptions.CustomColumns</c>.</summary>
    internal void RegisterCustomColumns(IReadOnlyList<CustomColumn> columns)
        => customColumns = columns ?? Array.Empty<CustomColumn>();

    /// <summary>Registers a type for audit with optional field-level overrides.</summary>
    public AuditConfigurationBuilder Audit<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(Action<AuditTypeBuilder<T>>? configure = null) where T : class
    {
        var entityType = typeof(T);
        var rules = GetOrCreateRules(entityType);
        ApplyAttributeRules(entityType, rules);
        ApplySoftDeleteAttribute(entityType);
        ApplyBaseTypeAttribute(entityType);

        if (configure is not null)
        {
            var typeBuilder = new AuditTypeBuilder<T>();
            configure(typeBuilder);
            foreach (var (propName, rule) in typeBuilder.Rules)
            {
                rules[propName] = rule;
            }
            if (typeBuilder.SoftDeleteProperty is not null)
            {
                softDeleteByType[entityType] = typeBuilder.SoftDeleteProperty;
            }
            if (typeBuilder.BaseType is not null)
            {
                baseTypeByType[entityType] = typeBuilder.BaseType;
            }
        }

        return this;
    }

    /// <summary>Registers a type for audit using only attribute-based rules.</summary>
    public AuditConfigurationBuilder Audit([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var rules = GetOrCreateRules(entityType);
        ApplyAttributeRules(entityType, rules);
        ApplySoftDeleteAttribute(entityType);
        ApplyBaseTypeAttribute(entityType);
        return this;
    }

    private void ApplyBaseTypeAttribute(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<AuditableAttribute>(inherit: false);
        if (attr?.BaseType is not null)
        {
            baseTypeByType[entityType] = attr.BaseType;
        }
    }

    /// <summary>Freezes accumulated rules into a runtime <see cref="IAuditConfiguration"/>.</summary>
    public IAuditConfiguration Build()
    {
        var configsByType = rulesByType.ToDictionary(
            kvp => kvp.Key,
            kvp => new AuditableTypeConfig(
                kvp.Key,
                kvp.Value,
                softDeleteByType.TryGetValue(kvp.Key, out var sd) ? sd : null,
                baseTypeByType.TryGetValue(kvp.Key, out var bt) ? bt : null));
        return new AuditConfiguration(configsByType, customColumns);
    }

    private Dictionary<string, AuditFieldRule> GetOrCreateRules(Type entityType)
    {
        if (!rulesByType.TryGetValue(entityType, out var rules))
        {
            rules = new Dictionary<string, AuditFieldRule>(StringComparer.Ordinal);
            rulesByType[entityType] = rules;
        }
        return rules;
    }

    private void ApplySoftDeleteAttribute([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType)
    {
        var attr = entityType.GetCustomAttribute<SoftDeleteAttribute>();
        if (attr is null)
        {
            return;
        }
        var prop = entityType.GetProperty(attr.PropertyName,
            BindingFlags.Instance | BindingFlags.Public);
        if (prop is null || prop.PropertyType != typeof(bool))
        {
            throw new OrionAuditConfigurationException(
                $"[SoftDelete] on '{entityType.Name}' points at '{attr.PropertyName}', which is not a public boolean instance property.");
        }
        softDeleteByType.TryAdd(entityType, attr.PropertyName);
    }

    private static void ApplyAttributeRules([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType, Dictionary<string, AuditFieldRule> rules)
    {
        foreach (var prop in entityType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (prop.GetCustomAttribute<NotAuditableAttribute>() is not null)
            {
                rules.TryAdd(prop.Name, AuditFieldRule.Exclude);
            }
            else if (prop.GetCustomAttribute<HashedAuditAttribute>() is not null)
            {
                rules.TryAdd(prop.Name, AuditFieldRule.Hash);
            }
            else if (prop.GetCustomAttribute<RedactedAuditAttribute>() is not null)
            {
                rules.TryAdd(prop.Name, AuditFieldRule.Redact);
            }
        }
    }
}
