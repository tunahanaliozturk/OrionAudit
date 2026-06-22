using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Deterministic canonicalization + SHA-256 hashing of an <see cref="AuditLog"/> row for the
/// tamper-evident hash chain. Pure and stateless: the same row content plus the same predecessor
/// hash always yields the same <see cref="AuditLog.EntryHash"/>, on any platform, culture, or
/// runtime. Reflection-free and Native-AOT clean.
/// </summary>
/// <remarks>
/// <para>
/// The canonical form is a length-prefixed concatenation of the row's content fields in a fixed
/// order, UTF-8 encoded, hashed with SHA-256, and rendered as lowercase hex. Length-prefixing each
/// field (rather than joining with a delimiter) makes the encoding injective: no two distinct field
/// tuples can produce the same byte stream, so an attacker cannot shuffle content across field
/// boundaries (for example moving text from <c>UserDisplay</c> into <c>Diff</c>) without changing
/// the hash. A null field is encoded distinctly from an empty string.
/// </para>
/// <para>
/// The chain link is folded in by appending the predecessor's hash as the final field, so each row
/// commits to the entire history before it: <c>EntryHash = SHA256(canonical(row) || PreviousHash)</c>.
/// Mutating any field of any row, deleting a row, or reordering two rows breaks every hash from that
/// point forward, which is what <c>IAuditIntegrityVerifier</c> detects.
/// </para>
/// </remarks>
public static class AuditEntryHasher
{
    // A length-prefixed null marker. Distinct from "0:" (an empty, non-null string) so that a null
    // field and an empty-string field never canonicalize to the same bytes.
    private const string NullMarker = "-1:";

    /// <summary>
    /// Computes the canonical lowercase-hex SHA-256 <see cref="AuditLog.EntryHash"/> for
    /// <paramref name="entry"/> chained onto <paramref name="previousHash"/>.
    /// </summary>
    /// <param name="entry">The row whose content is hashed. Its own <see cref="AuditLog.EntryHash"/>
    /// and <see cref="AuditLog.PreviousHash"/> are NOT part of the canonical content (only the
    /// supplied <paramref name="previousHash"/> is folded in), so the hash is stable regardless of
    /// what those columns currently hold.</param>
    /// <param name="previousHash">The predecessor row's <see cref="AuditLog.EntryHash"/> in the same
    /// chain scope, or <see langword="null"/> for the genesis row of a stream.</param>
    /// <returns>The 64-character lowercase hex SHA-256 digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    public static string ComputeEntryHash(AuditLog entry, string? previousHash)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var canonical = BuildCanonicalString(entry, previousHash);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var digest = SHA256.HashData(bytes);
        return ToLowerHex(digest);
    }

    // Lowercase hex of a 32-byte SHA-256 digest. Hand-rolled rather than Convert.ToHexStringLower
    // (which is .NET 9+) so the same code path compiles on the net8.0 target. Stack-allocated, no
    // per-byte allocation.
    private static string ToLowerHex(ReadOnlySpan<byte> digest)
    {
        const string HexDigits = "0123456789abcdef";
        Span<char> chars = stackalloc char[digest.Length * 2];
        for (var i = 0; i < digest.Length; i++)
        {
            var b = digest[i];
            chars[i * 2] = HexDigits[b >> 4];
            chars[(i * 2) + 1] = HexDigits[b & 0xF];
        }
        return new string(chars);
    }

    /// <summary>
    /// Builds the deterministic canonical string for a row chained onto <paramref name="previousHash"/>.
    /// Exposed internally so tests can assert canonicalization stability independently of the digest.
    /// </summary>
    internal static string BuildCanonicalString(AuditLog entry, string? previousHash)
    {
        // Fixed field order. Every content field that defines "what this audit row asserts" is
        // included so a change to any of them is detected. Id is included so swapping two rows'
        // identities (without touching their other content) still breaks the chain. The mutable
        // chain columns (EntryHash, PreviousHash) are deliberately excluded from the row's own
        // content; the link is carried solely by the previousHash argument appended last.
        var builder = new StringBuilder(256);
        AppendField(builder, entry.Id.ToString("D", CultureInfo.InvariantCulture));
        AppendField(builder, entry.EntityType);
        AppendField(builder, entry.EntityBaseType);
        AppendField(builder, entry.EntityId);
        AppendField(builder, ((byte)entry.Action).ToString(CultureInfo.InvariantCulture));
        AppendField(builder, CanonicalTimestamp(entry.OccurredOnUtc));
        AppendField(builder, entry.UserId);
        AppendField(builder, entry.UserDisplay);
        AppendField(builder, entry.UserType);
        AppendField(builder, entry.TenantId);
        AppendField(builder, entry.CorrelationId);
        AppendField(builder, entry.Diff);
        AppendField(builder, entry.Snapshot);
        AppendField(builder, entry.Error);
        AppendField(builder, previousHash);
        return builder.ToString();
    }

    // Canonical timestamp: integer milliseconds since the Unix epoch (UTC). Two properties matter
    // for the chain to survive persistence:
    //   1. Kind-independent. SQLite (and other providers) store DateTime as TEXT and read it back as
    //      DateTimeKind.Unspecified, dropping the UTC marker. A format that renders the Kind (such as
    //      the round-trip "O" specifier, which appends 'Z' for UTC) would hash differently before vs.
    //      after a round-trip. Reducing to an epoch offset removes Kind from the canonical form.
    //   2. Precision-stable across providers. Capture stamps a full-tick (100ns) DateTime, but
    //      relational providers truncate to their column precision (SQL Server/SQLite 100ns, but
    //      MySQL DATETIME(6) / PostgreSQL timestamp only microseconds). Normalising to whole
    //      milliseconds is the common precision every supported backend preserves, so the in-memory
    //      stamp and the read-back row canonicalize identically everywhere. Millisecond resolution is
    //      ample for tamper-evidence: ordering and the rest of the row content carry the rest.
    // The input is treated as UTC regardless of its Kind (capture always supplies a UTC DateTime).
    private static string CanonicalTimestamp(DateTime occurredOnUtc)
    {
        var utc = occurredOnUtc.Kind == DateTimeKind.Utc
            ? occurredOnUtc
            : DateTime.SpecifyKind(occurredOnUtc, DateTimeKind.Utc);
        var epochMilliseconds = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
        return epochMilliseconds.ToString(CultureInfo.InvariantCulture);
    }

    // Emits "<utf8-byte-length>:<value>" for a non-null field, or the null marker. The byte length
    // (not char length) is used so multi-byte content cannot be padded to collide with a different
    // field split. A literal separator inside the value is harmless because the length tells the
    // reader exactly how many bytes the value occupies.
    private static void AppendField(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append(NullMarker);
            return;
        }
        var byteLength = Encoding.UTF8.GetByteCount(value);
        builder.Append(byteLength.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }
}
