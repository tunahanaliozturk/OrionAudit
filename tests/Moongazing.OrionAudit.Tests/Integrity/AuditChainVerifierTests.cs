using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.Tests.Integrity;

public class AuditChainVerifierTests
{
    private const string EntityType = "Acme.Order, Acme";
    private const string EntityId = "o1";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Deterministic, strictly-increasing Guid id from a sequence byte (same technique the store
    // tests use). Pins tie-break ordering so the chain order is reproducible.
    private static Guid SeqId(byte sequence)
    {
        var bytes = new byte[16];
        bytes[15] = sequence;
        return new Guid(bytes);
    }

    private static AuditLog Row(byte seq, AuditAction action, string diff)
        => new()
        {
            Id = SeqId(seq),
            EntityType = EntityType,
            EntityId = EntityId,
            Action = action,
            OccurredOnUtc = T0.AddMinutes(seq),
            Diff = diff,
        };

    // No-custom-column resolver + the fixed test key; no anchor unless a test supplies one.
    private static AuditChainVerifier.StreamVerificationContext Ctx(AuditChainAnchor? anchor = null)
        => new(
            keyId => keyId == TestChainKeys.ActiveKeyId ? TestChainKeys.Key : null,
            _ => Array.Empty<KeyValuePair<string, string?>>(),
            anchor);

    private static void Stamp(List<AuditLog> rows, IReadOnlyDictionary<AuditHashChainStamper.ChainKey, string?> heads)
        => AuditHashChainStamper.Stamp(
            rows, heads, AuditHashChainScope.PerEntityStream,
            TestChainKeys.ActiveKeyId, TestChainKeys.Key,
            _ => Array.Empty<KeyValuePair<string, string?>>());

    // Builds a clean, correctly-stamped 3-row stream (insert + two updates).
    private static List<AuditLog> CleanStream()
    {
        var rows = new List<AuditLog>
        {
            Row(1, AuditAction.Inserted, "[]"),
            Row(2, AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":2}]"),
            Row(3, AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":3}]"),
        };
        Stamp(rows, new Dictionary<AuditHashChainStamper.ChainKey, string?>());
        return rows;
    }

    private static AuditChainAnchor AnchorFor(List<AuditLog> rows)
        => new()
        {
            EntityType = EntityType,
            EntityId = EntityId,
            TenantId = string.Empty,
            LatestEntryHash = rows[^1].EntryHash!,
            RowCount = rows.Count,
            KeyId = TestChainKeys.ActiveKeyId,
        };

    [Fact]
    public void Verify_CleanChain_IsValid()
    {
        var rows = CleanStream();
        var result = AuditChainVerifier.VerifyStream(rows, Ctx());
        Assert.True(result.IsValid);
        Assert.Equal(3, result.VerifiedRowCount);
        Assert.Equal(AuditChainBreakReason.None, result.Reason);
    }

    [Fact]
    public void Verify_CleanChain_WithMatchingAnchor_IsValid()
    {
        var rows = CleanStream();
        var result = AuditChainVerifier.VerifyStream(rows, Ctx(AnchorFor(rows)));
        Assert.True(result.IsValid);
        Assert.Equal(3, result.VerifiedRowCount);
    }

    [Fact]
    public void Verify_IsIdempotent_RepeatedRunsAgree()
    {
        var rows = CleanStream();
        var first = AuditChainVerifier.VerifyStream(rows, Ctx());
        var second = AuditChainVerifier.VerifyStream(rows, Ctx());
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(first.VerifiedRowCount, second.VerifiedRowCount);
    }

    [Fact]
    public void Verify_GenesisRow_HasNullPreviousHash()
    {
        var rows = CleanStream();
        Assert.Null(rows[0].PreviousHash);
        Assert.NotNull(rows[0].EntryHash);
        Assert.Equal(rows[0].EntryHash, rows[1].PreviousHash);
        Assert.Equal(rows[1].EntryHash, rows[2].PreviousHash);
    }

    [Fact]
    public void Verify_StampedRows_CarryTheActiveKeyId()
    {
        var rows = CleanStream();
        Assert.All(rows, r => Assert.Equal(TestChainKeys.ActiveKeyId, r.HashKeyId));
    }

    [Fact]
    public void Verify_MutatedContent_FailsAtThatRow_WithContentMismatch()
    {
        var rows = CleanStream();
        // Tamper with the middle row's diff AFTER it was hashed. Its stored EntryHash no longer
        // matches its content; the link pointers are untouched.
        rows[1].Diff = "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":999}]";

        var result = AuditChainVerifier.VerifyStream(rows, Ctx());

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
        Assert.Equal(rows[1].Id, result.BrokenAtId);
        Assert.Equal(1, result.VerifiedRowCount); // row 0 passed before the break
    }

    [Fact]
    public void Verify_WrongKey_FailsWithContentMismatch()
    {
        // Rows stamped under the real key, verified with a provider whose key id matches but whose
        // material differs: every recomputed MAC differs, so the genesis already fails. This is the
        // property that makes the chain unforgeable without the key.
        var rows = CleanStream();
        var wrongKeyCtx = new AuditChainVerifier.StreamVerificationContext(
            keyId => keyId == TestChainKeys.ActiveKeyId ? new byte[32] : (ReadOnlyMemory<byte>?)null,
            _ => Array.Empty<KeyValuePair<string, string?>>(),
            null);

        var result = AuditChainVerifier.VerifyStream(rows, wrongKeyCtx);

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
        Assert.Equal(rows[0].Id, result.BrokenAtId);
    }

    [Fact]
    public void Verify_UnknownKeyId_FailsWithUnknownKey()
    {
        var rows = CleanStream();
        var noKeyCtx = new AuditChainVerifier.StreamVerificationContext(
            _ => null, // no key id resolves
            _ => Array.Empty<KeyValuePair<string, string?>>(),
            null);

        var result = AuditChainVerifier.VerifyStream(rows, noKeyCtx);

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.UnknownKey, result.Reason);
        Assert.Equal(rows[0].Id, result.BrokenAtId);
    }

    [Fact]
    public void Verify_DeletedRow_FailsAtSuccessor_WithBrokenLink()
    {
        var rows = CleanStream();
        // Remove the middle row. Row 2's PreviousHash still points at the deleted row's hash, but its
        // real predecessor is now row 0, whose hash differs.
        rows.RemoveAt(1);

        var result = AuditChainVerifier.VerifyStream(rows, Ctx());

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.BrokenLink, result.Reason);
        Assert.Equal(rows[1].Id, result.BrokenAtId); // the surviving successor
    }

    [Fact]
    public void Verify_TailRowDeleted_WithAnchor_FailsWithTruncated()
    {
        // The classic truncation attack: drop the last row(s). The surviving prefix links intact, so
        // without the anchor it verifies. With the anchor (which remembers the true tail + count) the
        // shortfall is detected.
        var rows = CleanStream();
        var anchor = AnchorFor(rows);    // anchor remembers all 3
        rows.RemoveAt(2);                // delete the tail row

        var result = AuditChainVerifier.VerifyStream(rows, Ctx(anchor));

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.Truncated, result.Reason);
    }

    [Fact]
    public void Verify_WholeStreamDeleted_WithAnchor_FailsWithTruncated()
    {
        var rows = CleanStream();
        var anchor = AnchorFor(rows);

        var result = AuditChainVerifier.VerifyStream(new List<AuditLog>(), Ctx(anchor));

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.Truncated, result.Reason);
        Assert.Null(result.BrokenAtId); // no surviving row to point at
        Assert.Equal(EntityId, result.BrokenEntityId);
    }

    [Fact]
    public void Verify_ReorderedRows_FailsWithBrokenLink()
    {
        var rows = CleanStream();
        // Swap the order of rows 1 and 2 as presented to the verifier (a reordering attack). The
        // verifier reads them in the given sequence; the link no longer matches.
        (rows[1], rows[2]) = (rows[2], rows[1]);

        var result = AuditChainVerifier.VerifyStream(rows, Ctx());

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.BrokenLink, result.Reason);
    }

    [Fact]
    public void Verify_AppendedRow_ContinuesChain_AndStaysValid()
    {
        var rows = CleanStream();
        // Append a 4th row chained onto the existing head, as a later save would.
        var append = new List<AuditLog>
        {
            Row(4, AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":4}]"),
        };
        Stamp(append, new Dictionary<AuditHashChainStamper.ChainKey, string?>
        {
            [new AuditHashChainStamper.ChainKey(EntityType, EntityId, string.Empty)] = rows[^1].EntryHash,
        });
        rows.AddRange(append);

        var result = AuditChainVerifier.VerifyStream(rows, Ctx());
        Assert.True(result.IsValid);
        Assert.Equal(4, result.VerifiedRowCount);
        Assert.Equal(rows[2].EntryHash, rows[3].PreviousHash);
    }

    [Fact]
    public void Verify_UnhashedPrefixThenHashedSuffix_VerifiesOnlyTheHashedTail()
    {
        // Pre-existing rows (written before hash-chaining was enabled) carry no hash and form an
        // unverified prefix. The verifier skips them and verifies the hashed genesis onward.
        var legacy1 = Row(1, AuditAction.Inserted, "[]");
        var legacy2 = Row(2, AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":2}]");
        // legacy rows have null EntryHash by default.

        var hashed = new List<AuditLog>
        {
            Row(3, AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":3}]"),
            Row(4, AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":4}]"),
        };
        Stamp(hashed, new Dictionary<AuditHashChainStamper.ChainKey, string?>());

        var all = new List<AuditLog> { legacy1, legacy2 };
        all.AddRange(hashed);

        var result = AuditChainVerifier.VerifyStream(all, Ctx());
        Assert.True(result.IsValid);
        Assert.Equal(2, result.VerifiedRowCount); // only the two hashed rows
        Assert.Null(hashed[0].PreviousHash);       // first hashed row is genesis
    }

    [Fact]
    public void Verify_UnhashedRowInsideHashedRegion_FailsWithMissingHash()
    {
        var rows = CleanStream();
        // Forge: replace the middle row's hash with null (an unhashed row inside the hashed region).
        rows[1].EntryHash = null;
        rows[1].PreviousHash = null;

        var result = AuditChainVerifier.VerifyStream(rows, Ctx());

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.MissingHashAfterChainStart, result.Reason);
        Assert.Equal(rows[1].Id, result.BrokenAtId);
    }

    [Fact]
    public void Verify_EmptyStream_NoAnchor_IsValidWithZeroVerified()
    {
        var result = AuditChainVerifier.VerifyStream(Array.Empty<AuditLog>(), Ctx());
        Assert.True(result.IsValid);
        Assert.Equal(0, result.VerifiedRowCount);
    }

    [Fact]
    public void KeyFor_UnknownScope_Throws()
    {
        var row = Row(1, AuditAction.Inserted, "[]");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AuditHashChainStamper.KeyFor(row, (AuditHashChainScope)999));
    }

    [Fact]
    public void KeyFor_IncludesTenant_SoTenantsAreDistinctStreams()
    {
        var t1 = Row(1, AuditAction.Inserted, "[]");
        t1.TenantId = "t1";
        var t2 = Row(1, AuditAction.Inserted, "[]");
        t2.TenantId = "t2";

        var k1 = AuditHashChainStamper.KeyFor(t1, AuditHashChainScope.PerEntityStream);
        var k2 = AuditHashChainStamper.KeyFor(t2, AuditHashChainScope.PerEntityStream);
        Assert.NotEqual(k1, k2);
        Assert.Equal("t1", k1.TenantId);
        Assert.Equal("t2", k2.TenantId);
    }
}
