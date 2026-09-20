using Moongazing.OrionAudit.Integrity;

namespace Moongazing.OrionAudit.Tests.Integrity;

/// <summary>
/// The order <see cref="AuditHashChainStamper.DistinctKeys"/> returns streams in is the order their
/// anchor locks are taken in, so it has to be one every caller agrees on.
/// </summary>
/// <remarks>
/// A batch spanning several streams holds each anchor lock while it goes after the next. Two
/// concurrent batches touching streams A and B in opposite orders hold A and B respectively and then
/// each wait on the other, and the database can only resolve that by killing one. The keys used to
/// come out of a <see cref="HashSet{T}"/>, whose enumeration follows insertion order - so the lock
/// order was whatever order the rows happened to be captured in, which two callers have no reason to
/// share.
/// </remarks>
public class AuditHashChainStamperKeyOrderTests
{
    private static AuditLog Row(string entityType, string entityId, string? tenantId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            EntityType = entityType,
            EntityId = entityId,
            TenantId = tenantId,
            Action = AuditAction.Updated,
            OccurredOnUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Diff = "[]",
        };

    private static List<string> KeysOf(IEnumerable<AuditLog> rows)
        => AuditHashChainStamper
            .DistinctKeys(rows.ToList(), AuditHashChainScope.PerEntityStream)
            .Select(k => $"{k.EntityType}|{k.EntityId}|{k.TenantId}")
            .ToList();

    [Fact]
    public void DistinctKeys_IsTheSameOrderWhateverOrderTheRowsArrivedIn()
    {
        // The two batches hold the same streams and differ only in capture order - exactly the two
        // concurrent dispatch cycles that used to take their locks in opposite orders.
        var rows = new List<AuditLog>
        {
            Row("Acme.Order, Acme", "b"),
            Row("Acme.Customer, Acme", "a"),
            Row("Acme.Order, Acme", "a"),
            Row("Acme.Customer, Acme", "b"),
        };

        var forward = KeysOf(rows);
        var reversed = KeysOf(Enumerable.Reverse(rows));
        var shuffled = KeysOf(new[] { rows[2], rows[0], rows[3], rows[1] });

        Assert.Equal(forward, reversed);
        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public void DistinctKeys_OrdersOrdinallyOnEveryComponentOfTheKey()
    {
        // Ordinal, and over the whole key: two streams that agree on the type and id but differ by
        // tenant must still have a defined order, or a batch touching both has no global order to
        // follow. Ordinal rather than culture-aware because two processes under different locales
        // have to agree on it.
        var rows = new List<AuditLog>
        {
            Row("Zeta", "b", "t2"),
            Row("Zeta", "b", "t1"),
            Row("Zeta", "a", "t9"),
            Row("Alpha", "z", "t0"),
        };

        Assert.Equal(
            new[] { "Alpha|z|t0", "Zeta|a|t9", "Zeta|b|t1", "Zeta|b|t2" },
            KeysOf(rows));
    }

    [Fact]
    public void DistinctKeys_StillDeduplicatesStreams()
    {
        // Sorting must not have turned the set into a list with repeats: one lock per stream.
        var rows = new List<AuditLog>
        {
            Row("Acme.Order, Acme", "a"),
            Row("Acme.Order, Acme", "a"),
            Row("Acme.Order, Acme", "a"),
        };

        Assert.Single(KeysOf(rows));
    }

    [Fact]
    public void DistinctKeys_TreatsANullTenantAsTheCanonicalEmptyStream()
    {
        // A null tenant and an empty-string tenant are one stream, so they must yield one key - and
        // therefore one lock - rather than two that could be taken in either order.
        var rows = new List<AuditLog>
        {
            Row("Acme.Order, Acme", "a", tenantId: null),
            Row("Acme.Order, Acme", "a", tenantId: ""),
        };

        Assert.Equal(new[] { "Acme.Order, Acme|a|" }, KeysOf(rows));
    }
}
