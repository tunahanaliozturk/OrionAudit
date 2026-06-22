using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.Tests.Integrity;

public class AuditEntryHasherTests
{
    private static readonly Guid FixedId = new("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AuditLog Row(
        string entityId = "e1",
        string entityType = "T",
        AuditAction action = AuditAction.Updated,
        string? userDisplay = null,
        string? diff = null)
        => new()
        {
            Id = FixedId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OccurredOnUtc = FixedTime,
            UserDisplay = userDisplay,
            Diff = diff ?? "[]",
        };

    [Fact]
    public void ComputeEntryHash_IsDeterministic_ForSameInput()
    {
        var a = AuditEntryHasher.ComputeEntryHash(Row(), previousHash: null);
        var b = AuditEntryHasher.ComputeEntryHash(Row(), previousHash: null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeEntryHash_Produces64CharLowercaseHex()
    {
        var hash = AuditEntryHasher.ComputeEntryHash(Row(), previousHash: null);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void ComputeEntryHash_ChangesWhenAnyContentFieldChanges()
    {
        var baseline = AuditEntryHasher.ComputeEntryHash(Row(diff: "[]"), previousHash: null);
        var changedDiff = AuditEntryHasher.ComputeEntryHash(
            Row(diff: "[{\"op\":\"replace\",\"path\":\"/n\",\"value\":1}]"), previousHash: null);
        Assert.NotEqual(baseline, changedDiff);
    }

    [Fact]
    public void ComputeEntryHash_ChangesWhenPreviousHashChanges()
    {
        var genesis = AuditEntryHasher.ComputeEntryHash(Row(), previousHash: null);
        var chained = AuditEntryHasher.ComputeEntryHash(Row(), previousHash: new string('a', 64));
        Assert.NotEqual(genesis, chained);
    }

    [Fact]
    public void Canonicalization_DistinguishesNullFromEmptyString()
    {
        // A null UserDisplay must not canonicalize to the same bytes as an empty-string UserDisplay,
        // otherwise an attacker could blank a field without changing the hash.
        var withNull = AuditEntryHasher.ComputeEntryHash(Row(userDisplay: null), previousHash: null);
        var withEmpty = AuditEntryHasher.ComputeEntryHash(Row(userDisplay: ""), previousHash: null);
        Assert.NotEqual(withNull, withEmpty);
    }

    [Fact]
    public void Canonicalization_IsInjectiveAcrossFieldBoundaries()
    {
        // Length-prefixing must stop content from migrating across field boundaries without
        // detection. ("ab" in EntityId, "" in UserDisplay) and ("a" in EntityId, "b" in UserDisplay)
        // would collide under naive delimiter joining; they must hash differently here.
        var left = AuditEntryHasher.ComputeEntryHash(
            new AuditLog { Id = FixedId, EntityType = "T", EntityId = "ab", Action = AuditAction.Updated, OccurredOnUtc = FixedTime, UserDisplay = "", Diff = "[]" },
            previousHash: null);
        var right = AuditEntryHasher.ComputeEntryHash(
            new AuditLog { Id = FixedId, EntityType = "T", EntityId = "a", Action = AuditAction.Updated, OccurredOnUtc = FixedTime, UserDisplay = "b", Diff = "[]" },
            previousHash: null);
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Canonicalization_IgnoresExistingHashColumnsOnTheRow()
    {
        // The row's own EntryHash/PreviousHash columns are NOT part of its canonical content, so a
        // recompute is stable no matter what those columns currently hold (this is what lets the
        // verifier recompute and compare).
        var clean = Row();
        var withStaleHashes = Row();
        withStaleHashes.EntryHash = "deadbeef";
        withStaleHashes.PreviousHash = "feedface";

        Assert.Equal(
            AuditEntryHasher.ComputeEntryHash(clean, previousHash: "abc"),
            AuditEntryHasher.ComputeEntryHash(withStaleHashes, previousHash: "abc"));
    }

    [Fact]
    public void BuildCanonicalString_IsStable_AcrossRepeatedCalls()
    {
        var first = AuditEntryHasher.BuildCanonicalString(Row(userDisplay: "Alice"), "prev");
        var second = AuditEntryHasher.BuildCanonicalString(Row(userDisplay: "Alice"), "prev");
        Assert.Equal(first, second);
        // The previous hash is folded in as the final field, so it appears in the canonical string.
        Assert.Contains("prev", first, StringComparison.Ordinal);
    }
}
