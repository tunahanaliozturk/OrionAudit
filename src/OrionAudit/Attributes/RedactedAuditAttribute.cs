namespace OrionAudit;

/// <summary>
/// Replaces the property's value with a literal <c>"&lt;redacted&gt;"</c> in audit snapshots.
/// Equality detection is broken (the value is always equal), so changes to redacted fields are
/// not visible in diffs — use this when even the existence of a change is sensitive.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RedactedAuditAttribute : Attribute
{
}
