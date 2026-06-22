using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.Tests.Integrity;

/// <summary>
/// Fixed, deterministic chain keys for the integrity tests so MACs are reproducible across runs and
/// frameworks. The byte values are arbitrary but pinned; a second, different key backs the wrong-key
/// tests.
/// </summary>
internal static class TestChainKeys
{
    public const int ActiveKeyId = 7;
    public const int OtherKeyId = 8;

    // 32-byte keys (HMAC-SHA256 recommended length). Distinct, fixed patterns.
    private static readonly byte[] Primary = CreateKey(0x11);
    private static readonly byte[] Other = CreateKey(0x22);

    public static ReadOnlyMemory<byte> Key => Primary;

    public static IAuditChainKeyProvider Provider { get; } =
        new StaticAuditChainKeyProvider(
            new Dictionary<int, byte[]> { [ActiveKeyId] = Primary }, ActiveKeyId);

    /// <summary>A provider whose only key id matches the rows' stamped id but with different material.</summary>
    public static IAuditChainKeyProvider WrongKeyProvider { get; } =
        new StaticAuditChainKeyProvider(
            new Dictionary<int, byte[]> { [ActiveKeyId] = Other }, ActiveKeyId);

    private static byte[] CreateKey(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(seed + i);
        }
        return key;
    }
}
