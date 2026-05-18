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

    /// <summary>Registers a type for audit with optional field-level overrides.</summary>
    public AuditConfigurationBuilder Audit<T>(Action<AuditTypeBuilder<T>>? configure = null) where T : class
    {
        var entityType = typeof(T);
        var rules = GetOrCreateRules(entityType);
        ApplyAttributeRules(entityType, rules);

        if (configure is not null)
        {
            var typeBuilder = new AuditTypeBuilder<T>();
            configure(typeBuilder);
            foreach (var (propName, rule) in typeBuilder.Rules)
            {
                rules[propName] = rule;
            }
        }

        return this;
    }

    /// <summary>Registers a type for audit using only attribute-based rules.</summary>
    public AuditConfigurationBuilder Audit(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var rules = GetOrCreateRules(entityType);
        ApplyAttributeRules(entityType, rules);
        return this;
    }

    /// <summary>Freezes accumulated rules into a runtime <see cref="IAuditConfiguration"/>.</summary>
    public IAuditConfiguration Build()
    {
        var configsByType = rulesByType.ToDictionary(
            kvp => kvp.Key,
            kvp => new AuditableTypeConfig(kvp.Key, kvp.Value));
        return new AuditConfiguration(configsByType);
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

    private static void ApplyAttributeRules(Type entityType, Dictionary<string, AuditFieldRule> rules)
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
