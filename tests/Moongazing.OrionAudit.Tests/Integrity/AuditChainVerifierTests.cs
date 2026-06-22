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

    // Builds a clean, correctly-stamped 3-row stream (insert + two updates).
    private static List<AuditLog> CleanStream()
    {
        var rows = new List<AuditLog>
        {
            Row(1, AuditAction.Inserted, "[]"),
            Row(2, AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":2}]"),
            Row(3, AuditAction.Updated, "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":3}]"),
        };
        AuditHashChainStamper.Stamp(
            rows,
            new Dictionary<AuditHashChainStamper.ChainKey, string?>(),
            AuditHashChainScope.PerEntityStream);
        return rows;
    }

    [Fact]
    public void Verify_CleanChain_IsValid()
    {
        var rows = CleanStream();
        var result = AuditChainVerifier.VerifyStream(rows);
        Assert.True(result.IsValid);
        Assert.Equal(3, result.VerifiedRowCount);
        Assert.Equal(AuditChainBreakReason.None, result.Reason);
    }

    [Fact]
    public void Verify_IsIdempotent_RepeatedRunsAgree()
    {
        var rows = CleanStream();
        var first = AuditChainVerifier.VerifyStream(rows);
        var second = AuditChainVerifier.VerifyStream(rows);
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
    public void Verify_MutatedContent_FailsAtThatRow_WithContentMismatch()
    {
        var rows = CleanStream();
        // Tamper with the middle row's diff AFTER it was hashed. Its stored EntryHash no longer
        // matches its content; the link pointers are untouched.
        rows[1].Diff = "[{\"op\":\"replace\",\"path\":\"/v\",\"value\":999}]";

        var result = AuditChainVerifier.VerifyStream(rows);

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.ContentMismatch, result.Reason);
        Assert.Equal(rows[1].Id, result.BrokenAtId);
        Assert.Equal(1, result.VerifiedRowCount); // row 0 passed before the break
    }

    [Fact]
    public void Verify_DeletedRow_FailsAtSuccessor_WithBrokenLink()
    {
        var rows = CleanStream();
        // Remove the middle row. Row 2's PreviousHash still points at the deleted row's hash, but its
        // real predecessor is now row 0, whose hash differs.
        rows.RemoveAt(1);

        var result = AuditChainVerifier.VerifyStream(rows);

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.BrokenLink, result.Reason);
        Assert.Equal(rows[1].Id, result.BrokenAtId); // the surviving successor
    }

    [Fact]
    public void Verify_ReorderedRows_FailsWithBrokenLink()
    {
        var rows = CleanStream();
        // Swap the order of rows 1 and 2 as presented to the verifier (a reordering attack). The
        // verifier reads them in the given sequence; the link no longer matches.
        (rows[1], rows[2]) = (rows[2], rows[1]);

        var result = AuditChainVerifier.VerifyStream(rows);

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
        AuditHashChainStamper.Stamp(
            append,
            new Dictionary<AuditHashChainStamper.ChainKey, string?>
            {
                [new AuditHashChainStamper.ChainKey(EntityType, EntityId)] = rows[^1].EntryHash,
            },
            AuditHashChainScope.PerEntityStream);
        rows.AddRange(append);

        var result = AuditChainVerifier.VerifyStream(rows);
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
        AuditHashChainStamper.Stamp(
            hashed,
            new Dictionary<AuditHashChainStamper.ChainKey, string?>(),
            AuditHashChainScope.PerEntityStream);

        var all = new List<AuditLog> { legacy1, legacy2 };
        all.AddRange(hashed);

        var result = AuditChainVerifier.VerifyStream(all);
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

        var result = AuditChainVerifier.VerifyStream(rows);

        Assert.False(result.IsValid);
        Assert.Equal(AuditChainBreakReason.MissingHashAfterChainStart, result.Reason);
        Assert.Equal(rows[1].Id, result.BrokenAtId);
    }

    [Fact]
    public void Verify_EmptyStream_IsValidWithZeroVerified()
    {
        var result = AuditChainVerifier.VerifyStream(Array.Empty<AuditLog>());
        Assert.True(result.IsValid);
        Assert.Equal(0, result.VerifiedRowCount);
    }
}
