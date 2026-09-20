using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.Tests.Integrity;

/// <summary>
/// <see cref="AuditChainOrder.ForWalk"/> has to satisfy two requirements that pull against each
/// other: among rows that carry a <see cref="AuditLog.ChainSequence"/> the sequence is the chain's
/// order and nothing else may disturb it, while rows written before that column existed can only be
/// placed by timestamp - including ones an old build appends mid-rollout, after sequenced rows.
/// </summary>
public class AuditChainOrderForWalkTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AuditLog Row(string name, int minutes, long? sequence)
        => new()
        {
            Id = Guid.NewGuid(),
            EntityType = "Acme.Order, Acme",
            EntityId = "o1",
            Action = AuditAction.Updated,
            OccurredOnUtc = T0.AddMinutes(minutes),
            ChainSequence = sequence,
            Diff = name,                        // the label, so the assertions read as an order
        };

    private static string[] Order(params AuditLog[] rows)
        => AuditChainOrder.ForWalk(rows).Select(r => r.Diff).ToArray();

    [Fact]
    public void SequencedRows_AreOrderedBySequenceEvenWhenTheirTimestampsInvert()
    {
        // The defect this exists for. OccurredOnUtc is stamped near the start of capture and the
        // chain's order is settled later, by whoever wins the anchor lock, so two concurrent writers
        // can invert: "b" read the earlier clock value but landed second in the chain. Any ordering
        // that consults the timestamp first reads this stream backwards.
        var a = Row("a", minutes: 5, sequence: 0);
        var b = Row("b", minutes: 1, sequence: 1);

        Assert.Equal(new[] { "a", "b" }, Order(b, a));
    }

    [Fact]
    public void UnsequencedRows_KeepTheOrderTheyWereHandedIn()
    {
        // A chain written before ChainSequence existed. The incoming order is the database's - which
        // breaks Id ties by its own collation, not .NET's - so it must survive untouched.
        var first = Row("first", minutes: 1, sequence: null);
        var second = Row("second", minutes: 2, sequence: null);
        var third = Row("third", minutes: 3, sequence: null);

        Assert.Equal(new[] { "first", "second", "third" }, Order(first, second, third));
    }

    [Fact]
    public void LegacyPrefix_ComesBeforeTheSequencedRowsThatContinueIt()
    {
        var legacy1 = Row("legacy1", minutes: 1, sequence: null);
        var legacy2 = Row("legacy2", minutes: 2, sequence: null);
        var seq2 = Row("seq2", minutes: 3, sequence: 2);
        var seq3 = Row("seq3", minutes: 4, sequence: 3);

        Assert.Equal(new[] { "legacy1", "legacy2", "seq2", "seq3" }, Order(legacy1, legacy2, seq2, seq3));
    }

    [Fact]
    public void RollingUpgrade_UnsequencedRowAppendedLater_StaysWhereItWasWritten()
    {
        // An instance still on the old build appends after a new instance already appended. Ordering
        // every unsequenced row first would drag it to the front of the stream.
        var seq0 = Row("seq0", minutes: 1, sequence: 0);
        var seq1 = Row("seq1", minutes: 2, sequence: 1);
        var legacy = Row("legacy", minutes: 3, sequence: null);

        Assert.Equal(new[] { "seq0", "seq1", "legacy" }, Order(seq0, seq1, legacy));
    }

    [Fact]
    public void RollingUpgrade_BuildsInterleaving_KeepEachBuildsOwnOrderExactly()
    {
        // Both builds writing to one stream throughout a rollout. The timestamp decides only how the
        // two sides interleave; within each side the order is the side's own and is never disturbed -
        // note "seqB" and "seqC" invert on timestamp and still come out in sequence order.
        var seqA = Row("seqA", minutes: 1, sequence: 0);
        var legacyA = Row("legacyA", minutes: 2, sequence: null);
        var seqB = Row("seqB", minutes: 5, sequence: 2);
        var seqC = Row("seqC", minutes: 4, sequence: 3);
        var legacyB = Row("legacyB", minutes: 9, sequence: null);

        Assert.Equal(
            new[] { "seqA", "legacyA", "seqB", "seqC", "legacyB" },
            Order(seqA, legacyA, seqB, seqC, legacyB));
    }

    [Fact]
    public void OnATie_TheUnsequencedRowGoesLast()
    {
        // The one comparison that stays a judgement call: during an overlap the old build's write is
        // the one arriving late, so it sorts after. Reaching this needs two builds appending to one
        // stream within a single tick of the timestamp column.
        var sequenced = Row("sequenced", minutes: 1, sequence: 7);
        var legacy = Row("legacy", minutes: 1, sequence: null);

        Assert.Equal(new[] { "sequenced", "legacy" }, Order(legacy, sequenced));
    }

    [Fact]
    public void EmptyStream_IsEmpty()
        => Assert.Empty(AuditChainOrder.ForWalk(Array.Empty<AuditLog>()));
}
