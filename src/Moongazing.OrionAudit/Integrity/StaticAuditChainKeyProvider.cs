using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// In-configuration <see cref="IAuditChainKeyProvider"/>: a fixed set of versioned keys supplied at
/// startup (typically loaded from a secret manager / environment and registered via
/// <c>o.UseHashChain(h =&gt; h.UseKey(...))</c>). Thread-safe and immutable after construction;
/// register as a singleton.
/// </summary>
/// <remarks>
/// Defensive-copies the supplied key material so a caller mutating the source arrays after
/// construction cannot change the MAC and silently break verification. The secret must still be
/// stored outside the audit database for the integrity guarantee to hold (see
/// <see cref="IAuditChainKeyProvider"/>).
/// </remarks>
public sealed class StaticAuditChainKeyProvider : IAuditChainKeyProvider
{
    /// <summary>
    /// Minimum accepted key length in bytes. HMAC-SHA256 has a 64-byte block, so keys up to 64 bytes
    /// add entropy; below 16 bytes the keyed-MAC guarantee weakens, so shorter keys are rejected.
    /// </summary>
    public const int MinKeyBytes = 16;

    private readonly Dictionary<int, byte[]> keys;

    /// <summary>
    /// Creates a provider over <paramref name="keys"/> (key id → key material), using
    /// <paramref name="activeKeyId"/> for newly chained rows.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is null.</exception>
    /// <exception cref="OrionAuditConfigurationException">
    /// <paramref name="keys"/> is empty, <paramref name="activeKeyId"/> is not registered, or any key
    /// is null or shorter than <see cref="MinKeyBytes"/> bytes.
    /// </exception>
    public StaticAuditChainKeyProvider(IReadOnlyDictionary<int, byte[]> keys, int activeKeyId)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new OrionAuditConfigurationException(
                "The audit hash chain requires at least one key. Call UseKey(...) when enabling UseHashChain.");
        }
        if (!keys.ContainsKey(activeKeyId))
        {
            throw new OrionAuditConfigurationException(
                $"Audit hash chain ActiveKeyId {activeKeyId} is not registered. Registered ids: [{string.Join(", ", keys.Keys)}].");
        }

        var copy = new Dictionary<int, byte[]>(keys.Count);
        foreach (var (id, key) in keys)
        {
            if (key is null)
            {
                throw new OrionAuditConfigurationException($"Audit hash chain key id {id} is null.");
            }
            if (key.Length < MinKeyBytes)
            {
                throw new OrionAuditConfigurationException(
                    $"Audit hash chain key id {id} is {key.Length} bytes; expected at least {MinKeyBytes}.");
            }
            copy[id] = (byte[])key.Clone();
        }

        this.keys = copy;
        ActiveKeyId = activeKeyId;
    }

    /// <inheritdoc />
    public int ActiveKeyId { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte>? TryGetKey(int keyId)
        => keys.TryGetValue(keyId, out var key) ? key : null;
}
