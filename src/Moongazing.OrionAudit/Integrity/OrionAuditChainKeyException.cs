namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Thrown when the tamper-evident chain cannot resolve the key material it needs: either the active
/// key id is not registered with the <see cref="IAuditChainKeyProvider"/> at stamp time, or a row to
/// be verified references a key id that is no longer registered. Distinct from
/// <see cref="OrionAuditConfigurationException"/> (a startup misconfiguration) because it surfaces a
/// key-availability failure during capture or verification.
/// </summary>
public sealed class OrionAuditChainKeyException : OrionAuditException
{
    /// <summary>Initializes a new instance with the supplied message.</summary>
    public OrionAuditChainKeyException(string message) : base(message) { }
}
