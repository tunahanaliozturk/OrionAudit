namespace Moongazing.OrionAudit;

/// <summary>
/// Marks an entity class for audit capture. Properties without further attributes are captured
/// normally; use <see cref="NotAuditableAttribute"/>, <see cref="HashedAuditAttribute"/>, or
/// <see cref="RedactedAuditAttribute"/> to control individual fields.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AuditableAttribute : Attribute
{
    /// <summary>
    /// Default constructor. Capture stays at the runtime CLR type with no base-type stamping.
    /// </summary>
    public AuditableAttribute() { }

    /// <summary>
    /// Construct with an explicit base type for TPH / polymorphic capture. The runtime CLR type
    /// is still stamped on <see cref="AuditLog.EntityType"/>; the supplied <paramref name="baseType"/>
    /// is stamped on <see cref="AuditLog.EntityBaseType"/> so a future <c>AuditFor&lt;TBase&gt;()</c>
    /// query can return the full hierarchy. Inheritance-aware querying lands in v0.7.2.
    /// </summary>
    /// <param name="baseType">A non-null base class of the decorated entity.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseType"/> is null.</exception>
    public AuditableAttribute(Type baseType)
    {
        ArgumentNullException.ThrowIfNull(baseType);
        BaseType = baseType;
    }

    /// <summary>The declared base type for TPH / polymorphic capture, or <see langword="null"/>.</summary>
    public Type? BaseType { get; }
}
