using System.Linq.Expressions;
using System.Reflection;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Fluent builder for per-type audit rules. Returned to consumers from
/// <see cref="AuditConfigurationBuilder.Audit{T}(Action{AuditTypeBuilder{T}})"/>.
/// </summary>
public sealed class AuditTypeBuilder<T> where T : class
{
    internal Dictionary<string, AuditFieldRule> Rules { get; } = new(StringComparer.Ordinal);

    /// <summary>Name of a boolean property whose flip captures <see cref="AuditAction.SoftDeleted"/>.</summary>
    internal string? SoftDeleteProperty { get; private set; }

    /// <summary>
    /// Declares the boolean property whose flip from <c>false</c> to <c>true</c> is captured
    /// as <see cref="AuditAction.SoftDeleted"/> rather than <see cref="AuditAction.Updated"/>.
    /// Equivalent to the class-level <c>[SoftDelete(nameof(...))]</c> attribute.
    /// </summary>
    public AuditTypeBuilder<T> SoftDelete(Expression<Func<T, bool>> selector)
    {
        SoftDeleteProperty = PropertyName(selector);
        return this;
    }

    /// <summary>Marks the property as excluded from audit snapshots.</summary>
    public AuditTypeBuilder<T> Exclude<TProp>(Expression<Func<T, TProp>> selector)
    {
        Rules[PropertyName(selector)] = AuditFieldRule.Exclude;
        return this;
    }

    /// <summary>Marks the property to be replaced with a SHA-256 hash in snapshots.</summary>
    public AuditTypeBuilder<T> Hash<TProp>(Expression<Func<T, TProp>> selector)
    {
        Rules[PropertyName(selector)] = AuditFieldRule.Hash;
        return this;
    }

    /// <summary>Marks the property to be replaced with the literal <c>"&lt;redacted&gt;"</c> in snapshots.</summary>
    public AuditTypeBuilder<T> Redact<TProp>(Expression<Func<T, TProp>> selector)
    {
        Rules[PropertyName(selector)] = AuditFieldRule.Redact;
        return this;
    }

    private static string PropertyName<TProp>(Expression<Func<T, TProp>> selector)
    {
        if (selector.Body is MemberExpression member && member.Member is PropertyInfo prop)
        {
            return prop.Name;
        }
        if (selector.Body is UnaryExpression { Operand: MemberExpression inner } && inner.Member is PropertyInfo innerProp)
        {
            return innerProp.Name;
        }
        throw new OrionAuditConfigurationException(
            $"Expression '{selector}' is not a simple property accessor.");
    }
}
