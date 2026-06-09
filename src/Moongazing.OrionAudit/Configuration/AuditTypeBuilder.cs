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

    /// <summary>
    /// Per-property display labels surfaced by the viewer (v0.7.3). Keyed by property name
    /// (the same key the rules dictionary uses), value is the human-readable label.
    /// </summary>
    internal Dictionary<string, string> FieldLabels { get; } = new(StringComparer.Ordinal);

    /// <summary>Optional human-readable label for the entity type itself (e.g. "Sales Order").</summary>
    internal string? EntityLabel { get; private set; }

    /// <summary>Name of a boolean property whose flip captures <see cref="AuditAction.SoftDeleted"/>.</summary>
    internal string? SoftDeleteProperty { get; private set; }

    /// <summary>
    /// Declared base type for TPH / polymorphic capture, or <see langword="null"/> when the
    /// captured type stands alone. Stamped on <see cref="AuditLog.EntityBaseType"/> by the
    /// capture interceptor.
    /// </summary>
    internal Type? BaseType { get; private set; }

    /// <summary>
    /// Declares <typeparamref name="TBase"/> as the base type for TPH / polymorphic capture.
    /// The runtime CLR type still gets stamped on <see cref="AuditLog.EntityType"/>; the base
    /// type's full name is stamped on <see cref="AuditLog.EntityBaseType"/> so a future
    /// <c>AuditFor&lt;TBase&gt;()</c> query (v0.7.2) can return the full hierarchy.
    /// Equivalent to the class-level <c>[Auditable(typeof(TBase))]</c> constructor overload.
    /// </summary>
    public AuditTypeBuilder<T> UseBaseType<TBase>() where TBase : class
    {
        BaseType = typeof(TBase);
        return this;
    }

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

    /// <summary>
    /// Assigns a human-readable display label to a property. The label flows through the
    /// viewer's <c>FieldChange.DisplayLabel</c> so a column captured as <c>SubTotal</c> can
    /// surface as <c>"Net"</c> in the UI without renaming the entity. Has no effect on
    /// capture or snapshot behaviour.
    /// </summary>
    /// <example>
    /// <code>
    /// o.Audit&lt;Order&gt;(b => b.Label(o =&gt; o.SubTotal, "Net"));
    /// </code>
    /// </example>
    public AuditTypeBuilder<T> Label<TProp>(Expression<Func<T, TProp>> selector, string displayLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayLabel);
        FieldLabels[PropertyName(selector)] = displayLabel;
        return this;
    }

    /// <summary>
    /// Assigns a human-readable display label to the entity type itself (e.g. <c>"Sales Order"</c>
    /// for an <c>Order</c> CLR type). Surfaces as <c>AuditEntryView.EntityDisplayLabel</c>.
    /// </summary>
    public AuditTypeBuilder<T> Label(string displayLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayLabel);
        EntityLabel = displayLabel;
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
