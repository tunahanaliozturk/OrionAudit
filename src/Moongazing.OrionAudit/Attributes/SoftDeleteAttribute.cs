namespace Moongazing.OrionAudit;

/// <summary>
/// Declares a boolean property whose flip from <c>false</c> to <c>true</c> is captured as
/// <see cref="AuditAction.SoftDeleted"/> rather than <see cref="AuditAction.Updated"/>.
/// </summary>
/// <remarks>
/// The property must be a non-nullable <see cref="bool"/>. The attribute is class-level for
/// discoverability; an equivalent fluent override is available via <c>AuditTypeBuilder.SoftDelete</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SoftDeleteAttribute : Attribute
{
    /// <summary>The name of the boolean property whose flip signals a soft delete.</summary>
    public string PropertyName { get; }

    /// <summary>Initializes a new instance pointing at the supplied property name.</summary>
    public SoftDeleteAttribute(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        PropertyName = propertyName;
    }
}
