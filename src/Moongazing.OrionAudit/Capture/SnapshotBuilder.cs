using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

    private const string ReflectiveSerializationMessage =
        "Serializes non-primitive property values with the reflection-based JsonSerializer. " +
        "Supply a JsonSerializerContext (OrionAuditOptions.UseJsonContext) to use the trim-safe, " +
        "Native-AOT-clean snapshot path instead.";

    // Snapshots run on every save: keep hot paths allocation-free where possible.
    private const int StackBufferSize = 256;
    private const int Sha256ByteCount = 32;

    /// <summary>
    /// Produces a <see cref="JsonObject"/> snapshot of the supplied property values, applying any
    /// configured field rules for <paramref name="entityType"/>. Non-primitive property values are
    /// serialised through <paramref name="jsonContext"/> — trim-safe and Native-AOT clean. A value
    /// whose type is not registered in the context throws <see cref="OrionAuditException"/>.
    /// </summary>
    public static JsonObject Build(
        Type entityType,
        IReadOnlyDictionary<string, object?> propertyValues,
        IAuditConfiguration configuration,
        JsonSerializerContext jsonContext)
    {
        ArgumentNullException.ThrowIfNull(jsonContext);
        return BuildCore(entityType, propertyValues, configuration,
            value => ConvertViaContext(value, jsonContext));
    }

    /// <summary>
    /// Produces a <see cref="JsonObject"/> snapshot of the supplied property values, applying any
    /// configured field rules for <paramref name="entityType"/>. Non-primitive property values are
    /// serialised with the reflection-based <see cref="JsonSerializer"/>. Use the overload that
    /// takes a <see cref="JsonSerializerContext"/> for a trim-safe, Native-AOT-clean snapshot path.
    /// </summary>
    [RequiresUnreferencedCode(ReflectiveSerializationMessage)]
    [RequiresDynamicCode(ReflectiveSerializationMessage)]
    public static JsonObject Build(
        Type entityType,
        IReadOnlyDictionary<string, object?> propertyValues,
        IAuditConfiguration configuration)
        => BuildCore(entityType, propertyValues, configuration, ConvertViaReflection);

    private static JsonObject BuildCore(
        Type entityType,
        IReadOnlyDictionary<string, object?> propertyValues,
        IAuditConfiguration configuration,
        Func<object, JsonNode?> convertNonPrimitive)
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
                    snapshot[propName] = HashValue(rawValue, convertNonPrimitive);
                    break;
                case AuditFieldRule.Capture:
                default:
                    snapshot[propName] = ConvertValue(rawValue, convertNonPrimitive);
                    break;
            }
        }

        return snapshot;
    }

    private static string? HashValue(object? value, Func<object, JsonNode?> convertNonPrimitive)
    {
        if (value is null)
        {
            return null;
        }
        // Strings hash by their raw text (stable across versions); other values hash by their
        // canonical JSON representation, sharing the same conversion path as captured values.
        var text = value as string
            ?? ConvertValue(value, convertNonPrimitive)?.ToJsonString()
            ?? "null";
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

    private static JsonNode? ConvertValue(object? value, Func<object, JsonNode?> convertNonPrimitive)
    {
        // Primitive fast path skips JsonSerializer entirely on every property of every audited
        // entity. Non-primitive values go through the supplied converter — either the
        // source-generated context (trim-safe, AOT-clean) or the reflective serializer.
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
            _ => convertNonPrimitive(value),
        };
    }

    private static JsonNode? ConvertViaContext(object value, JsonSerializerContext jsonContext)
    {
        var typeInfo = jsonContext.GetTypeInfo(value.GetType());
        if (typeInfo is null)
        {
            throw new OrionAuditException(
                $"OrionAudit could not snapshot a value of type '{value.GetType()}': it is not " +
                $"registered in the supplied JsonSerializerContext. Add " +
                $"[JsonSerializable(typeof({value.GetType().Name}))] to the context passed to UseJsonContext.");
        }
        return JsonSerializer.SerializeToNode(value, typeInfo);
    }

    [RequiresUnreferencedCode(ReflectiveSerializationMessage)]
    [RequiresDynamicCode(ReflectiveSerializationMessage)]
    private static JsonNode? ConvertViaReflection(object value)
        => JsonSerializer.SerializeToNode(value, value.GetType());
}
