using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Builds JSON snapshots of audited entity state, applying field-level rules
/// (<see cref="AuditFieldRule.Exclude"/>, <see cref="AuditFieldRule.Hash"/>,
/// <see cref="AuditFieldRule.Redact"/>) from the supplied configuration.
/// </summary>
public static class SnapshotBuilder
{
    /// <summary>Marker value substituted for properties marked with <see cref="RedactedAuditAttribute"/>.</summary>
    public const string RedactedMarker = "<redacted>";

    // Snapshots run on every save: keep hot paths allocation-free where possible.
    private const int StackBufferSize = 256;
    private const int Sha256ByteCount = 32;

    /// <summary>
    /// Produces a <see cref="JsonObject"/> snapshot of the supplied property values, applying any
    /// configured field rules for <paramref name="entityType"/>.
    /// </summary>
    public static JsonObject Build(
        Type entityType,
        IReadOnlyDictionary<string, object?> propertyValues,
        IAuditConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(propertyValues);
        ArgumentNullException.ThrowIfNull(configuration);

        var typeConfig = configuration.GetConfig(entityType);
        var snapshot = new JsonObject();

        foreach (var (propName, rawValue) in propertyValues)
        {
            var rule = typeConfig?.FieldRule(propName) ?? AuditFieldRule.Capture;
            switch (rule)
            {
                case AuditFieldRule.Exclude:
                    continue;
                case AuditFieldRule.Redact:
                    snapshot[propName] = RedactedMarker;
                    break;
                case AuditFieldRule.Hash:
                    snapshot[propName] = HashValue(rawValue);
                    break;
                case AuditFieldRule.Capture:
                default:
                    snapshot[propName] = ConvertToNode(rawValue);
                    break;
            }
        }

        return snapshot;
    }

    private static string? HashValue(object? value)
    {
        if (value is null)
        {
            return null;
        }
        var text = value as string ?? JsonSerializer.Serialize(value);
        var byteCount = Encoding.UTF8.GetByteCount(text);

        byte[]? rented = null;
        Span<byte> buffer = byteCount <= StackBufferSize
            ? stackalloc byte[StackBufferSize]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            buffer = buffer[..byteCount];
            Encoding.UTF8.GetBytes(text, buffer);

            Span<byte> hash = stackalloc byte[Sha256ByteCount];
            SHA256.HashData(buffer, hash);
#if NET9_0_OR_GREATER
            return Convert.ToHexStringLower(hash);
#else
            return Convert.ToHexString(hash).ToLowerInvariant();
#endif
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static JsonNode? ConvertToNode(object? value)
    {
        // Primitive fast path skips JsonSerializer's reflection machinery on every property of
        // every audited entity. Anything not listed falls back to the reflective serializer,
        // which is also the only path that handles user-defined value types and collections.
        return value switch
        {
            null => null,
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            byte u8 => JsonValue.Create(u8),
            sbyte i8 => JsonValue.Create(i8),
            short i16 => JsonValue.Create(i16),
            ushort u16 => JsonValue.Create(u16),
            int i32 => JsonValue.Create(i32),
            uint u32 => JsonValue.Create(u32),
            long i64 => JsonValue.Create(i64),
            ulong u64 => JsonValue.Create(u64),
            float f32 => JsonValue.Create(f32),
            double f64 => JsonValue.Create(f64),
            decimal d => JsonValue.Create(d),
            char c => JsonValue.Create(c),
            DateTime dt => JsonValue.Create(dt),
            DateTimeOffset dto => JsonValue.Create(dto),
            DateOnly date => JsonValue.Create(date.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            TimeOnly time => JsonValue.Create(time.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            TimeSpan ts => JsonValue.Create(ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture)),
            Guid g => JsonValue.Create(g),
            byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
            _ => JsonSerializer.SerializeToNode(value, value.GetType())
        };
    }
}
