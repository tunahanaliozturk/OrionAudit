using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Deterministic canonicalization + keyed HMAC-SHA256 MAC of an <see cref="AuditLog"/> row for the
/// tamper-evident hash chain. Pure and stateless: the same row content, predecessor hash, custom
/// columns, and key always yield the same <see cref="AuditLog.EntryHash"/>, on any platform, culture,
/// or runtime. Reflection-free and Native-AOT clean.
/// </summary>
/// <remarks>
/// <para>
/// The canonical form is a length-prefixed concatenation of the row's content fields in a fixed
/// order, plus the registered custom-column values in a deterministic (name-sorted) order, with the
/// predecessor's MAC folded in as the final field. The whole canonical byte stream is run through
/// <c>HMAC-SHA256</c> with a key supplied by an <see cref="IAuditChainKeyProvider"/> that lives
/// outside the audited data, and rendered as lowercase hex. Length-prefixing each field (rather than
/// joining with a delimiter) makes the encoding injective: no two distinct field tuples can produce
/// the same byte stream, so an attacker cannot shuffle content across field boundaries (for example
/// moving text from <c>UserDisplay</c> into <c>Diff</c>) without changing the MAC. A null field is
/// encoded distinctly from an empty string.
/// </para>
/// <para>
/// <b>Why keyed.</b> A bare hash (plain SHA-256) is forgeable: anyone able to write audit rows could
/// recompute <c>PreviousHash</c>/<c>EntryHash</c> for a fabricated row and produce a chain that
/// verifies, so it does not protect against the very actor it implies. Keying the chain with a secret
/// that the attacker cannot read means a forged or altered row cannot be given a valid MAC. Integrity
/// therefore holds against an adversary who can modify the audit database but not obtain the MAC key;
/// the key must be stored outside the audit database.
/// </para>
/// <para>
/// The chain link is folded in by appending the predecessor's MAC as the final field, so each row
/// commits to the entire history before it: <c>EntryHash = HMAC(key, canonical(row) || PreviousHash)</c>.
/// Mutating any field of any row, deleting a row, or reordering two rows breaks every MAC from that
/// point forward, which is what <c>IAuditIntegrityVerifier</c> detects.
/// </para>
/// </remarks>
public static class AuditEntryHasher
{
    // A length-prefixed null marker. Distinct from "0:" (an empty, non-null string) so that a null
    // field and an empty-string field never canonicalize to the same bytes.
    private const string NullMarker = "-1:";

    /// <summary>
    /// Computes the canonical lowercase-hex HMAC-SHA256 <see cref="AuditLog.EntryHash"/> for
    /// <paramref name="entry"/> chained onto <paramref name="previousHash"/> under <paramref name="key"/>.
    /// </summary>
    /// <param name="entry">The row whose content is MAC'd. Its own <see cref="AuditLog.EntryHash"/>
    /// and <see cref="AuditLog.PreviousHash"/> are NOT part of the canonical content (only the
    /// supplied <paramref name="previousHash"/> is folded in), so the MAC is stable regardless of
    /// what those columns currently hold.</param>
    /// <param name="previousHash">The predecessor row's <see cref="AuditLog.EntryHash"/> in the same
    /// chain scope, or <see langword="null"/> for the genesis row of a stream.</param>
    /// <param name="key">The HMAC key (from <see cref="IAuditChainKeyProvider"/>).</param>
    /// <param name="customColumns">The registered custom-column (name, value) pairs to bind into the
    /// MAC, in any order (this method sorts them deterministically). Pass an empty list when no custom
    /// columns are registered. Values are the column's invariant-culture string form, or null.</param>
    /// <returns>The 64-character lowercase hex HMAC-SHA256 digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> or <paramref name="customColumns"/> is null.</exception>
    public static string ComputeEntryHash(
        AuditLog entry,
        string? previousHash,
        ReadOnlySpan<byte> key,
        IReadOnlyList<KeyValuePair<string, string?>> customColumns)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(customColumns);

        var canonical = BuildCanonicalString(entry, previousHash, customColumns);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        Span<byte> digest = stackalloc byte[32]; // HMAC-SHA256 output size.
        HMACSHA256.HashData(key, bytes, digest);
        return ToLowerHex(digest);
    }

    /// <summary>
    /// Convenience overload for callers with no custom columns (the common case and most tests).
    /// </summary>
    public static string ComputeEntryHash(AuditLog entry, string? previousHash, ReadOnlySpan<byte> key)
        => ComputeEntryHash(entry, previousHash, key, Array.Empty<KeyValuePair<string, string?>>());

    // Lowercase hex of a 32-byte HMAC-SHA256 digest. Hand-rolled rather than Convert.ToHexStringLower
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
    /// Builds the deterministic canonical string for a row chained onto <paramref name="previousHash"/>
    /// with <paramref name="customColumns"/> folded in. Exposed internally so tests can assert
    /// canonicalization stability independently of the MAC.
    /// </summary>
    internal static string BuildCanonicalString(
        AuditLog entry,
        string? previousHash,
        IReadOnlyList<KeyValuePair<string, string?>> customColumns)
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

        // Registered custom columns, in a deterministic order so the MAC is independent of the order
        // the consumer registered them or the order EF returns shadow values. Sort by column name
        // (ordinal, invariant) and length-prefix BOTH the name and the value: binding the name (not
        // just the value) stops a rename or a value swapped between two columns from going unnoticed,
        // and a count prefix stops a column being silently dropped. Editing any custom column value
        // after capture changes this segment and breaks verification (finding: custom columns must be
        // covered by the MAC). The count is emitted first so adding/removing a column is detectable.
        AppendField(builder, customColumns.Count.ToString(CultureInfo.InvariantCulture));
        if (customColumns.Count > 0)
        {
            var ordered = SortByNameOrdinal(customColumns);
            foreach (var column in ordered)
            {
                AppendField(builder, column.Key);
                AppendField(builder, column.Value);
            }
        }

        // Predecessor MAC folded in last so each row commits to the whole prior history.
        AppendField(builder, previousHash);
        return builder.ToString();
    }

    // Deterministic, allocation-light sort by column name (ordinal, invariant). A small array copy +
    // Array.Sort keeps it reflection-free and culture-independent. Duplicate names cannot occur:
    // AddColumn rejects them at registration, and the verifier reads each registered name once.
    private static KeyValuePair<string, string?>[] SortByNameOrdinal(
        IReadOnlyList<KeyValuePair<string, string?>> customColumns)
    {
        var array = new KeyValuePair<string, string?>[customColumns.Count];
        for (var i = 0; i < customColumns.Count; i++)
        {
            array[i] = customColumns[i];
        }
        Array.Sort(array, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return array;
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
