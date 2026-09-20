using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Evaluates the periodic <see cref="SnapshotPolicy"/> against the <see cref="SnapshotCursor"/>
/// companion table. Shared by the synchronous interceptor and the async dispatcher so both
/// reach the same snapshot decision.
/// </summary>
internal static class SnapshotPolicyEvaluator
{
    /// <summary>
    /// Returns true when the supplied audit row should also carry a full snapshot, advancing
    /// (and lazily creating) the entity's <see cref="SnapshotCursor"/>. Must be called inside
    /// the same transaction that writes the resulting rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Concurrency.</b> <see cref="SnapshotCursor.UpdatesSinceLast"/> is read-modify-written
    /// with no lock and no concurrency token, deliberately. Two saves that touch the same entity
    /// concurrently both read the same count before either commits, and both write
    /// <c>count + 1</c>: a group of <c>k</c> overlapping saves for one entity advances the counter
    /// by 1 rather than by <c>k</c>.
    /// </para>
    /// <para>
    /// <b>The bound that puts on <c>SnapshotEvery(n)</c>.</b> Consecutive snapshots for an entity
    /// are at least <c>n</c> and at most <c>n * k</c> updates apart, where <c>k</c> is the largest
    /// number of saves for that one entity stream (<see cref="SnapshotCursor.EntityType"/> +
    /// <see cref="SnapshotCursor.EntityId"/> + <see cref="SnapshotCursor.TenantId"/>) that overlap,
    /// meaning all of them read the cursor before any of them commits. <c>k</c> is 1 for every
    /// entity that is not written concurrently with itself, so the common case drifts not at all;
    /// separate entities hold separate cursor rows and never interact. The counter only moves
    /// forward — every group contributes at least one increment — so the cadence stretches but
    /// never stalls, and the n-th snapshot always arrives. Members of one group compute the same
    /// value and so reach the same decision, which means a collision can produce a duplicate
    /// snapshot but can never skip one.
    /// </para>
    /// <para>
    /// <b>What is not affected.</b> Nothing about the audit trail itself: no row is lost, delayed,
    /// or mis-stamped, and the diff chain stays complete, so <c>AuditReconstructor</c> can always
    /// rebuild any state. <see cref="AuditLog.Snapshot"/> is a replay-cost optimisation, and the
    /// cost of a lost increment is one snapshot taken later than intended.
    /// </para>
    /// <para>
    /// <b>Why it is not guarded.</b> A concurrency token on the cursor would make the losing writer
    /// raise <see cref="DbUpdateConcurrencyException"/> — but the cursor is written inside the
    /// consumer's <c>SaveChanges</c>, so that exception aborts the consumer's business transaction
    /// and names a table they never asked for. Failing a customer's order save to keep a snapshot
    /// cadence exact is the wrong trade, and it would contradict how the rest of capture behaves: a
    /// diff failure annotates the row, a custom-column provider failure annotates the row, and an
    /// observer fault is swallowed. Consumers who need a cadence that holds under same-entity
    /// concurrency should use <c>SnapshotEvery(TimeSpan)</c>, which keys off
    /// <see cref="SnapshotCursor.LastSnapshotUtc"/> rather than a counter: a concurrent group there
    /// takes an extra snapshot instead of skipping one, so the interval is never exceeded.
    /// </para>
    /// <para>
    /// <b>Known edge.</b> Two saves that are both the <em>first</em> update to one entity insert
    /// that entity's cursor row concurrently, and the composite primary key rejects the second with
    /// a <see cref="DbUpdateException"/>. That predates this analysis and is not introduced by it.
    /// </para>
    /// <para>
    /// The cursor is read with <c>FindAsync</c>: this runs on the caller's <c>SavingChangesAsync</c>
    /// hot path, where a synchronous round-trip blocks a thread-pool thread for every save. Once
    /// the cursor is tracked, later saves on the same context resolve it from the change tracker
    /// and never touch the database again.
    /// </para>
    /// </remarks>
    public static async Task<bool> ShouldSnapshotAsync(
        DbContext ctx,
        SnapshotPolicy policy,
        AuditLog row,
        DateTime occurredOn,
        CancellationToken cancellationToken)
    {
        var cursor = await ctx.Set<SnapshotCursor>()
            .FindAsync([row.EntityType, row.EntityId, row.TenantId ?? string.Empty], cancellationToken)
            .ConfigureAwait(false);
        if (cursor is null)
        {
            cursor = new SnapshotCursor
            {
                EntityType = row.EntityType,
                EntityId = row.EntityId,
                TenantId = row.TenantId ?? string.Empty,
                UpdatesSinceLast = 0,
                LastSnapshotUtc = null,
            };
            ctx.Add(cursor);
        }

        cursor.UpdatesSinceLast++;
        var shouldSnapshot = policy switch
        {
            SnapshotPolicy.EveryNthPolicy n => cursor.UpdatesSinceLast >= n.Updates,
            SnapshotPolicy.EveryDurationPolicy d =>
                cursor.LastSnapshotUtc is null
                || (occurredOn - cursor.LastSnapshotUtc.Value) >= d.Elapsed,
            _ => false,
        };

        if (shouldSnapshot)
        {
            cursor.UpdatesSinceLast = 0;
            cursor.LastSnapshotUtc = occurredOn;
        }
        return shouldSnapshot;
    }
}
