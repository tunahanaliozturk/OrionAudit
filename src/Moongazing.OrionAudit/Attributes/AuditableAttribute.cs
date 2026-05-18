namespace Moongazing.OrionAudit;

/// <summary>
/// Marks an entity class for audit capture. Properties without further attributes are captured
/// normally; use <see cref="NotAuditableAttribute"/>, <see cref="HashedAuditAttribute"/>, or
/// <see cref="RedactedAuditAttribute"/> to control individual fields.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AuditableAttribute : Attribute
{
}
