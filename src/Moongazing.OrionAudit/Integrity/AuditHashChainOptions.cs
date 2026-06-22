namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Marker / tunables for the tamper-evident hash chain. Registered as a singleton only when the
/// consumer opts in via <c>o.UseHashChain(...)</c>; its presence in the container is how the capture
/// interceptor and async dispatcher switch chaining on, mirroring the way
/// <c>AsyncCaptureOptions</c> gates async capture. Absent ⇒ chaining is off and the
/// <see cref="AuditLog.EntryHash"/> / <see cref="AuditLog.PreviousHash"/> columns stay null.
/// </summary>
public sealed class AuditHashChainOptions
{
    /// <summary>
    /// The chain scope: what set of rows forms one chain. Defaults to
    /// <see cref="AuditHashChainScope.PerEntityStream"/>.
    /// </summary>
    public AuditHashChainScope Scope { get; set; } = AuditHashChainScope.PerEntityStream;
}
