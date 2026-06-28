using Moongazing.OrionAudit.Store;

namespace Moongazing.OrionAudit.Tests;

public class AuditHistoryStoreBaseTests
{
    // A store that overrides nothing — both operations should fall through to the throwing default,
    // modelling a backend that cannot query or compact (the capability-default pattern).
    private sealed class UnsupportedStore : AuditHistoryStoreBase { }

    // A store that supports querying but not compaction.
    private sealed class QueryOnlyStore : AuditHistoryStoreBase
    {
        public override Task<AuditHistoryPage> QueryAsync(AuditHistoryQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(AuditHistoryPage.Empty(query.Skip, query.EffectiveTake));
    }

    [Fact]
    public async Task UnsupportedStore_Query_ThrowsNotSupported()
    {
        var store = new UnsupportedStore();
        await Assert.ThrowsAsync<NotSupportedException>(() => store.QueryAsync(new AuditHistoryQuery()));
    }

    [Fact]
    public async Task UnsupportedStore_Compact_ThrowsNotSupported()
    {
        var store = new UnsupportedStore();
        await Assert.ThrowsAsync<NotSupportedException>(() => store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = "T",
            EntityId = "1",
            RetainTail = 0,
        }));
    }

    [Fact]
    public async Task UnsupportedStore_Aggregate_ThrowsNotSupported()
    {
        var store = new UnsupportedStore();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => store.AggregateAsync(new AuditAggregationQuery()));
    }

    [Fact]
    public async Task QueryOnlyStore_QuerySupported_CompactStillThrows()
    {
        var store = new QueryOnlyStore();

        var page = await store.QueryAsync(new AuditHistoryQuery());
        Assert.Equal(0, page.TotalCount);

        await Assert.ThrowsAsync<NotSupportedException>(() => store.CompactAsync(new AuditCompactionRequest
        {
            EntityType = "T",
            EntityId = "1",
            RetainTail = 0,
        }));
    }
}
