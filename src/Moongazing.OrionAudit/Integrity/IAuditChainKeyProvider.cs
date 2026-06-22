namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Supplies the secret key material that keys the tamper-evident audit chain's HMAC. The key lives
/// <strong>outside</strong> the audited data so the chain protects against an attacker who can write
/// audit rows but cannot obtain this key: without the key a forged row cannot be given a valid MAC,
/// and an existing row cannot be silently recomputed. Mirrors the key-provider shape OrionVault uses
/// (a short key id plus a lookup), so the key can be rotated without rewriting history.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST be thread-safe and are registered as singletons. The chain stores the
/// <see cref="ActiveKeyId"/> on each row (and the stream anchor) so a later verification looks up the
/// exact key version a row was MAC'd with via <see cref="TryGetKey"/> - a rotated-in newer key does
/// not invalidate rows written under an older one, as long as the old key remains registered.
/// </para>
/// <para>
/// <b>Security boundary.</b> Integrity holds against an adversary who can modify the audit database
/// but cannot read this key. Store the key in a secret manager / KMS / environment secret that is not
/// co-located with the audit table; a key checked into the audit database itself provides no
/// protection. v0.9.0 ships <see cref="StaticAuditChainKeyProvider"/> (in-config base64 keys);
/// custom providers (KMS, Key Vault) implement this interface.
/// </para>
/// </remarks>
public interface IAuditChainKeyProvider
{
    /// <summary>
    /// The key id stamped on every newly chained row. Must be registered (i.e.
    /// <see cref="TryGetKey"/>(<see cref="ActiveKeyId"/>) returns non-null).
    /// </summary>
    int ActiveKeyId { get; }

    /// <summary>
    /// Looks up the key material for <paramref name="keyId"/>, or <see langword="null"/> when that id
    /// is not registered. Returned memory is the raw HMAC key (recommended 32 bytes for HMAC-SHA256).
    /// </summary>
    ReadOnlyMemory<byte>? TryGetKey(int keyId);
}
