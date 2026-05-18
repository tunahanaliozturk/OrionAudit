namespace OrionAudit;

/// <summary>
/// Marks a property as excluded from audit capture. The property's value is removed from both
/// before and after snapshots; diffs will never reference it.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NotAuditableAttribute : Attribute
{
}
