namespace OrionAudit;

/// <summary>
/// Replaces the property's value with a SHA-256 hex hash in audit snapshots. Hash is deterministic,
/// so equality detection still works (same input ⇒ same hash) without leaking the cleartext value.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class HashedAuditAttribute : Attribute
{
}
