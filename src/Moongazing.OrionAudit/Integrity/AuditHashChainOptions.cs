namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Marker / tunables for the tamper-evident hash chain. Registered as a singleton only when the
/// consumer opts in via <c>o.UseHashChain(...)</c>; its presence in the container is how the capture
/// interceptor and async dispatcher switch chaining on, mirroring the way
/// <c>AsyncCaptureOptions</c> gates async capture. Absent ⇒ chaining is off and the
/// <see cref="AuditLog.EntryHash"/> / <see cref="AuditLog.PreviousHash"/> columns stay null.
/// </summary>
/// <remarks>
/// The chain is a <strong>keyed</strong> MAC (HMAC-SHA256), not a bare hash, so a key MUST be
/// configured via <see cref="UseKey(int, string)"/> / <see cref="UseKey(int, byte[])"/> or by
/// supplying a custom <see cref="IAuditChainKeyProvider"/>. Enabling hash-chaining without a key
/// throws at startup (see the DI wiring) - an unkeyed chain would be forgeable by anyone who can
/// write rows, which defeats the feature.
/// </remarks>
public sealed class AuditHashChainOptions
{
    private AuditChainKeysBuilder? keysBuilder;

    /// <summary>
    /// The chain scope: what set of rows forms one chain. Defaults to
    /// <see cref="AuditHashChainScope.PerEntityStream"/>.
    /// </summary>
    public AuditHashChainScope Scope { get; set; } = AuditHashChainScope.PerEntityStream;

    /// <summary>
    /// The key id used to MAC newly chained rows. Must match a key registered via
    /// <see cref="UseKey(int, string)"/> / <see cref="UseKey(int, byte[])"/>. Defaults to 1. The id is
    /// stamped on each row and the stream anchor so verification can look up the exact key a row was
    /// MAC'd with, allowing keys to be rotated without invalidating older rows.
    /// </summary>
    public int ActiveKeyId { get; set; } = 1;

    /// <summary>
    /// Registers a base64-encoded HMAC key under <paramref name="keyId"/>. Call once per key version;
    /// set <see cref="ActiveKeyId"/> to choose which one stamps new rows. The secret must be stored
    /// outside the audit database (see <see cref="IAuditChainKeyProvider"/>).
    /// </summary>
    public AuditHashChainOptions UseKey(int keyId, string base64Key)
    {
        (keysBuilder ??= new AuditChainKeysBuilder()).Add(keyId, base64Key);
        ActiveKeyId = keyId;
        return this;
    }

    /// <summary>
    /// Registers raw HMAC key material under <paramref name="keyId"/> and makes it the active key.
    /// </summary>
    public AuditHashChainOptions UseKey(int keyId, byte[] key)
    {
        (keysBuilder ??= new AuditChainKeysBuilder()).Add(keyId, key);
        ActiveKeyId = keyId;
        return this;
    }

    /// <summary>The configured keys builder, or null when no key was registered via <c>UseKey</c>.</summary>
    internal AuditChainKeysBuilder? KeysBuilder => keysBuilder;
}
