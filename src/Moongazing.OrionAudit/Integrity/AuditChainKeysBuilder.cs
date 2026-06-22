using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Fluent builder for the tamper-evident chain's HMAC keys, exposed through
/// <see cref="AuditHashChainOptions.UseKey(int, string)"/> /
/// <see cref="AuditHashChainOptions.UseKey(int, byte[])"/>. Collects one or more versioned keys; the
/// active key id (set on <see cref="AuditHashChainOptions"/>) selects which one stamps new rows, while
/// retained older keys keep previously-written rows verifiable across a rotation.
/// </summary>
public sealed class AuditChainKeysBuilder
{
    private readonly Dictionary<int, byte[]> keys = new();

    /// <summary>Registers a base64-encoded key under <paramref name="keyId"/>.</summary>
    /// <exception cref="OrionAuditConfigurationException">
    /// <paramref name="keyId"/> is already registered, the value is not valid base64, or it decodes to
    /// fewer than <see cref="StaticAuditChainKeyProvider.MinKeyBytes"/> bytes.
    /// </exception>
    public AuditChainKeysBuilder Add(int keyId, string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new OrionAuditConfigurationException($"Audit hash chain key {keyId} is not valid base64.", ex);
        }
        return Add(keyId, bytes);
    }

    /// <summary>Registers raw key material under <paramref name="keyId"/>.</summary>
    /// <exception cref="OrionAuditConfigurationException">
    /// <paramref name="keyId"/> is already registered, or the key is shorter than
    /// <see cref="StaticAuditChainKeyProvider.MinKeyBytes"/> bytes.
    /// </exception>
    public AuditChainKeysBuilder Add(int keyId, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (keys.ContainsKey(keyId))
        {
            throw new OrionAuditConfigurationException($"Duplicate audit hash chain key id {keyId}.");
        }
        if (key.Length < StaticAuditChainKeyProvider.MinKeyBytes)
        {
            throw new OrionAuditConfigurationException(
                $"Audit hash chain key {keyId} is {key.Length} bytes; expected at least {StaticAuditChainKeyProvider.MinKeyBytes}.");
        }
        keys[keyId] = (byte[])key.Clone();
        return this;
    }

    /// <summary>True once at least one key has been registered.</summary>
    internal bool HasAny => keys.Count > 0;

    internal IReadOnlyDictionary<int, byte[]> Build() => keys;
}
